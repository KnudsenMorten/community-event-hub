using System.Net;
using System.Resources;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Forms;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Reminders;

/// <summary>
/// §250 — the biweekly "finish your Get Started" digest, WIZARD STEPS ONLY.
///
/// <para><b>Audience.</b> Every ACTIVE participant whose role has a Get-Started wizard
/// and whose wizard is &lt;100% complete. The open steps are enumerated via the WIZARD
/// SERVICES THEMSELVES (<see cref="SpeakerWizardService"/> / <see cref="RoleWizardService"/> /
/// <see cref="AttendeeWizardService"/> / <see cref="SponsorWizardService"/> — the exact same
/// source the wizard pages render), NEVER the task table — so non-wizard deadline tasks
/// (speaker preview/final deck uploads, travel reimbursement) can NEVER leak in (§250 scope
/// guard: those keep their own due-date reminder track). Sponsor contacts follow the
/// universal sponsor-email audience rule (§7c): only EVENT-COORDINATOR contacts are mailed
/// (the sponsor wizard is company-scoped, so the coordinator answers for the company).</para>
///
/// <para><b>Cadence (§232).</b> Anchored on the participant's welcome
/// (<see cref="Participant.WelcomeWithLoginSentAt"/> ?? <see cref="Participant.CreatedAt"/>);
/// the FIRST digest fires no earlier than 14 days after it (the welcome is the day-0 nudge),
/// then one per 14-day window — the OccasionKey <c>getstarted:{pid}:wk{n}</c> embeds the
/// window index so the <see cref="ReminderEngine"/> ledger sends exactly one per window and
/// a missed run self-heals. The ledger is written only on REAL delivery (the engine's
/// <see cref="IEmailDeliveryOutcome"/> seam), so a ring-dropped digest retries once rings widen.</para>
///
/// <para><b>Double-nag guard.</b> Party sign-up and Master Class selection already have
/// their OWN §232 biweekly cadences (<see cref="AttendeePartyReminderBuilder"/> /
/// <see cref="AttendeeMasterClassReminderBuilder"/>). When those are the ONLY open steps,
/// the digest is SKIPPED for that person — the dedicated cadence covers them (this also
/// naturally excludes attendees, whose whole wizard is masterclass+party). Suspended 1-day
/// attendees (§242, deactivated) are excluded by the Active filter.</para>
///
/// <para><b>Delivery.</b> ONE digest email (<c>getstarted-digest</c> template) listing the
/// open step TITLES (the same resx strings the wizard pages show) + a single magic-link
/// button into THEIR wizard route. Ring-gated at the transport under the
/// <c>welcome-email</c> feature (<see cref="ReminderMessage.FeatureKey"/>), same key the
/// template catalog files it under.</para>
/// </summary>
public sealed class GetStartedDigestBuilder
{
    private const string TemplateName = "getstarted-digest";

    /// <summary>Days between repeats — every 2 weeks (§232).</summary>
    public const int IntervalDays = 14;

    /// <summary>Wizard steps that already have their own §232 cadence (party + Master
    /// Class) — when these are the ONLY open steps the digest is skipped (no double-nag).</summary>
    private static readonly HashSet<string> SelfNaggingStepKeys =
        new(StringComparer.OrdinalIgnoreCase) { "party", "masterclass" };

    /// <summary>The wizard-step titles come from the SAME shared resource the wizard
    /// pages render (SharedResource.resx), so email and GUI never drift.</summary>
    private static readonly Lazy<ResourceManager> StepTitles = new(() =>
        new ResourceManager(
            "CommunityHub.Core.Resources.SharedResource",
            typeof(Resources.SharedResource).Assembly));

    private readonly CommunityHubDbContext _db;
    private readonly EmailTemplateProvider _templates;
    private readonly TimeProvider _clock;
    private readonly SpeakerWizardService _speakerWizard;
    private readonly RoleWizardService _roleWizard;
    private readonly AttendeeWizardService _attendeeWizard;
    private readonly SponsorWizardService _sponsorWizard;
    private readonly EmailReminderCadenceService? _cadence;

    /// <summary>The reminder TYPE — the ledger key this mail's history is stored under.</summary>
    public const string ReminderTypeName = "getstarted-digest";

    public GetStartedDigestBuilder(
        CommunityHubDbContext db,
        EmailTemplateProvider templates,
        TimeProvider clock,
        SpeakerWizardService speakerWizard,
        RoleWizardService roleWizard,
        AttendeeWizardService attendeeWizard,
        SponsorWizardService sponsorWizard,
        // §707.11 — optional so existing constructions keep compiling; null ⇒ the shipped default.
        EmailReminderCadenceService? cadence = null)
    {
        _db = db;
        _templates = templates;
        _clock = clock;
        _speakerWizard = speakerWizard;
        _roleWizard = roleWizard;
        _attendeeWizard = attendeeWizard;
        _sponsorWizard = sponsorWizard;
        _cadence = cadence;
    }

    public async Task<IReadOnlyList<ReminderMessage>> BuildDueAsync(
        int eventId, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);

        var ev = await _db.Events
            .Where(e => e.Id == eventId)
            .Select(e => new { e.CommunityName, e.DisplayName })
            .FirstOrDefaultAsync(ct);
        if (ev is null) return System.Array.Empty<ReminderMessage>();

        // §707.11/§881 — the operator's cadence MAP for this mail + every recipient's last send,
        // read ONCE. A map rather than a single number because this mail reaches three roles and he
        // can now set them apart (operator 2026-08-05: *"i need to define the cadence for reminders
        // for get started pending for sponsor"*). Resolving per person is the whole feature —
        // reading one interval before the loop is the bug it fixes.
        var intervalMap = _cadence is null
            ? null
            : await _cadence.GetIntervalMapAsync(eventId, TemplateName, ct);
        IReadOnlyDictionary<string, DateOnly> lastSentByOccasion = await EmailReminderCadenceService.LastSentByOccasionAsync(_db, eventId, ReminderTypeName, ct);

        // Every ACTIVE participant (deactivated logins — incl. §242-suspended 1-day
        // attendees — are never nagged). The window gate below runs BEFORE the (more
        // expensive) wizard build, so quiet-period participants cost one row read.
        var people = await _db.Participants
            .AsNoTracking()
            // §499 — the shared remindable rule: a digest nudging someone toward Get Started is
            // pointless for anyone who cannot sign in to open it.
            .Remindable()
            .Where(p => p.EventId == eventId)
            .Select(p => new
            {
                p.Id, p.Role, p.Email, p.FullName,
                p.IsEventCoordinator, p.SponsorCompanyId,
                p.WelcomeWithLoginSentAt, p.CreatedAt,
            })
            .ToListAsync(ct);

        var messages = new List<ReminderMessage>();
        foreach (var p in people)
        {
            if (string.IsNullOrWhiteSpace(p.Email)) continue;

            // §7c sponsor audience rule: sponsor mail goes to event-coordinator
            // contacts only (the company-scoped wizard is the coordinator's to finish;
            // signer-only / booth-member contacts are never nagged about it).
            if (p.Role == ParticipantRole.Sponsor && !p.IsEventCoordinator) continue;

            // 🔒 §738 — NEVER WELCOMED ⇒ NEVER CHASED. This used to fall back to CreatedAt, so a
            // MISSING welcome stamp read as "welcomed in June" and the person was maximally OVERDUE
            // — due the instant their ring opened. On 2026-07-31 that delivered "a few Get Started
            // steps are still waiting for you" at 08:00 to sponsors whose welcome went out at 08:45.
            // §232 is the opposite rule: the welcome IS the day-0 nudge and the first digest waits a
            // full interval after it. A null date must never read as "long ago".
            //
            // ⚠️ 61 active participants had no welcome stamp when this was found, so this is the
            // difference between one bad morning and every ring-widening from here to ticket launch.
            // They go quiet until they are welcomed — which is the actual thing to fix (§721).
            if (p.WelcomeWithLoginSentAt is null) continue;

            // §232 cadence, anchored on the welcome that was actually sent.
            var anchor = DateOnly.FromDateTime(p.WelcomeWithLoginSentAt.Value.UtcDateTime);
            if (today < anchor) continue;                 // clock skew — not due yet

            // 🔒 §707.11 — DUE = lastSent + interval (§707.10), not `daysSince / IntervalDays`. The
            // first digest still waits a full interval after the welcome (§232).
            var occasionRoot = $"getstarted:{p.Id}";
            var lastSent = lastSentByOccasion.TryGetValue(occasionRoot, out var ls)
                ? ls : (DateOnly?)null;
            // §881 — THIS PERSON'S interval: their role's own value, else the all-roles one, else
            // the shipped default.
            var intervalDays = intervalMap is null
                ? IntervalDays
                : EmailReminderCadenceService.Resolve(intervalMap, TemplateName, p.Role);
            if (!EmailReminderCadenceService.IsDue(today, anchor, lastSent, intervalDays)) continue;

            // Enumerate the role's wizard via the WIZARD SERVICE itself (never tasks).
            var wizard = await OpenStepsAsync(eventId, p.Id, p.Role, ct);
            if (wizard is null) continue;                       // role has no wizard / no company
            var (openKeys, titlePrefix, route) = wizard.Value;
            if (openKeys.Count == 0) continue;                  // 100% complete — digest stops

            // Double-nag guard: party/Master-Class-only leftovers ride their own cadence.
            if (openKeys.All(k => SelfNaggingStepKeys.Contains(k))) continue;

            var firstName = string.IsNullOrWhiteSpace(p.FullName)
                ? "there"
                : p.FullName.Split(' ')[0];

            var tokens = _templates.NewTokenSet(p.Id);
            tokens["firstName"] = firstName;
            tokens["communityName"] = ev.CommunityName ?? string.Empty;
            tokens["eventDisplayName"] = ev.DisplayName ?? string.Empty;
            tokens["openStepCount"] = openKeys.Count.ToString();
            tokens["getStartedPath"] = route;
            tokens["openStepsHtml"] = string.Concat(openKeys.Select(k =>
                $"<li style=\"margin:0 0 6px;\">{WebUtility.HtmlEncode(TitleFor(titlePrefix, k))}</li>"));
            var rendered = _templates.Render(TemplateName, tokens);

            messages.Add(new ReminderMessage(
                RecipientEmail: p.Email,
                ReminderType: "getstarted-digest",
                // One digest per person per 2-week window (self-healing dedup).
                // §707.11 — the occasion is the DAY; "due" is decided above by lastSent + interval.
                OccasionKey: $"{occasionRoot}:{today:yyyyMMdd}",
                Subject: rendered.Subject,
                HtmlBody: rendered.HtmlBody,
                Persona: OnboardingEmailSets.PersonaFor(p.Role).ToString(),
                ParticipantId: p.Id,
                RecipientName: p.FullName,
                // §250: the digest rides the welcome-email ring at the transport.
                FeatureKey: "welcome-email", MailKey: TemplateName));
        }

        return messages;
    }

    /// <summary>
    /// The OPEN step keys for this participant's wizard, with the resx title prefix and
    /// the wizard route the digest's magic-link button deep-links to — or null when the
    /// role has no wizard for this person (e.g. a sponsor contact without a company).
    /// Sponsor steps with an UNDETERMINABLE state (Done == null, e.g. e-conomic briefly
    /// unavailable) are not counted as open — mirroring the wizard page, which never
    /// makes them the "Continue" target.
    /// </summary>
    private async Task<(IReadOnlyList<string> OpenKeys, string TitlePrefix, string Route)?>
        OpenStepsAsync(int eventId, int participantId, ParticipantRole role, CancellationToken ct)
    {
        switch (role)
        {
            case ParticipantRole.Speaker:
            {
                var v = await _speakerWizard.BuildAsync(eventId, participantId, ct);
                return (v.Steps.Where(s => !s.Done).Select(s => s.Key).ToList(),
                    "SpeakerWiz.Step.", "/Forms/Wizard");
            }
            case ParticipantRole.Sponsor:
            {
                var v = await _sponsorWizard.BuildAsync(eventId, participantId, ct);
                if (v is null) return null; // no company link ⇒ no wizard
                return (v.Steps.Where(s => s.Done == false).Select(s => s.Key).ToList(),
                    "SponsorWiz.Step.", "/Forms/Wizard");   // §557: all roles go to the new Get Started
            }
            case ParticipantRole.Attendee:
            {
                var v = await _attendeeWizard.BuildAsync(eventId, participantId, ct);
                return (v.Steps.Where(s => !s.Done).Select(s => s.Key).ToList(),
                    "RoleWiz.Step.", "/Forms/Wizard");
            }
            default:
            {
                if (!RoleWizardService.Handles(role)) return null;
                var v = await _roleWizard.BuildAsync(eventId, participantId, ct);
                return (v.Steps.Where(s => !s.Done).Select(s => s.Key).ToList(),
                    "RoleWiz.Step.", "/Forms/Wizard");
            }
        }
    }

    /// <summary>The step's GUI title from SharedResource.resx (fallback: the key itself).</summary>
    private static string TitleFor(string prefix, string key)
    {
        try { return StepTitles.Value.GetString(prefix + key) ?? key; }
        catch { return key; }
    }
}
