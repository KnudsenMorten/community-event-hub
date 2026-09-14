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
/// <para>🔴 <b>THE DOUBLE-NAG GUARD IS GONE (§968, 2026-08-08) — and it had become a HOLE.</b>
/// It used to skip anyone whose ONLY open steps were <c>party</c> / <c>masterclass</c>, on the
/// grounds that those "ride their own §232 cadence" in
/// <see cref="AttendeePartyReminderBuilder"/> / <see cref="AttendeeMasterClassReminderBuilder"/>.
/// <b>Both of those were retired on 2026-07-31 (§733.1) and now return nothing at all</b> — so the
/// digest was deferring to cadences that no longer existed, and somebody owing only a party answer
/// or a Master Class choice was chased by <b>nothing</b>, for ever, while their wizard sat at 90%.
/// ⚠️ It also *"naturally excluded attendees, whose whole wizard is masterclass+party"* — i.e. the
/// entire attendee population was silently outside the digest.</para>
///
/// <para>🔑 Two halves of one decision moved on different days and the gap between them was silent —
/// the §939 / §945 shape again. §733.1 retired the dedicated chasers *because* the wizard digest was
/// to be the single chase; this line is the other half of that sentence, finally applied. Suspended
/// 1-day attendees (§242, deactivated) are still excluded by the Active filter.</para>
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

    /// <summary>
    /// §1081 stage 3 — the SHARED sponsor audience rule. Optional so existing constructions keep
    /// compiling; null ⇒ the flag-only fallback this builder used to apply inline.
    /// </summary>
    private readonly SponsorRecipientResolver? _sponsorRecipients;

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
        EmailReminderCadenceService? cadence = null,
        // §1081 stage 3 — optional + last, same pattern; null ⇒ the flag-only fallback.
        SponsorRecipientResolver? sponsorRecipients = null)
    {
        _sponsorRecipients = sponsorRecipients;
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

        // §1081 stage A — sponsor facts are COMPANY state, and a company normally has several
        // coordinators due the same digest on the same day. Read once, reused for all of them.
        var companyFacts = new Dictionary<string, SponsorCompanyDigestFacts>(StringComparer.Ordinal);

        // §1081 stage 3 — resolved coordinator ids per company, same reasoning: one answer, reused
        // by every coordinator of that company in this pass.
        var coordinatorIds = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);

        foreach (var p in people)
        {
            if (string.IsNullOrWhiteSpace(p.Email)) continue;

            // §7c sponsor audience rule: sponsor mail goes to event-coordinator contacts only (the
            // company-scoped wizard is the coordinator's to finish; signer-only / booth-member
            // contacts are never nagged about it).
            //
            // 🔴 §1081 stage 3 — ASKED THROUGH THE SHARED RESOLVER, not re-implemented here. This
            // line used to be `!p.IsEventCoordinator`, which is only the FALLBACK half of the rule:
            // SponsorRecipientResolver treats the e-conomic Role-2 set as PRIMARY and the hub flag
            // as an additive override. ⇒ A coordinator who holds Role 2 in ERP but whose hub flag
            // was never set received sponsor TASK reminders (which route through the resolver) and
            // silently did NOT receive the Get Started digest. One question, two answers — §366.
            //
            // 🔑 Resolved ONCE PER COMPANY and cached: a company normally has several coordinators
            // in this same loop, and the resolver may call e-conomic.
            if (p.Role == ParticipantRole.Sponsor)
            {
                if (_sponsorRecipients is null)
                {
                    if (!p.IsEventCoordinator) continue;      // fallback: exactly the old behaviour
                }
                else
                {
                    var companyId = p.SponsorCompanyId ?? string.Empty;
                    if (!coordinatorIds.TryGetValue(companyId, out var ids))
                    {
                        ids = (await _sponsorRecipients.ResolveAsync(eventId, companyId, ct))
                            .Select(r => r.ParticipantId).ToHashSet();
                        coordinatorIds[companyId] = ids;
                    }
                    if (!ids.Contains(p.Id)) continue;
                }
            }

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
            var wizard = await StepsForDigestAsync(eventId, p.Id, p.Role, companyFacts, ct);
            if (wizard is null) continue;                       // role has no wizard / no company
            var (openKeys, doneKeys, titlePrefix, route, missingFieldKeys, credits) = wizard;
            if (openKeys.Count == 0) continue;                  // 100% complete — digest stops

            // 🔴 §968 — NO STEP IS EXEMPT. There used to be a skip here for people whose only open
            // steps were party / Master Class, deferring to dedicated chasers that were retired the
            // same week (§733.1) — so those people, and every attendee, were chased by nothing.
            // Operator 2026-08-08: *"they must be reminded like everyone else"*.

            var firstName = string.IsNullOrWhiteSpace(p.FullName)
                ? "there"
                : p.FullName.Split(' ')[0];

            var tokens = _templates.NewTokenSet(p.Id);
            tokens["firstName"] = firstName;
            tokens["communityName"] = ev.CommunityName ?? string.Empty;
            tokens["eventDisplayName"] = ev.DisplayName ?? string.Empty;
            tokens["openStepCount"] = openKeys.Count.ToString();
            tokens["getStartedPath"] = route;

            // 🔴 §1081 stage A — NAME THE FIELDS, NOT JUST THE STEP. A sponsor chased for "Company
            // details" cannot tell which of three fields is blank, and §854 already settled that
            // shape: what somebody has to act on must be named, because "some sponsors" is not
            // chaseable. The missing-field list is attached to the step it belongs to.
            tokens["openStepsHtml"] = string.Concat(openKeys.Select(k =>
            {
                var title = WebUtility.HtmlEncode(TitleFor(titlePrefix, k));
                var detail = k == "company" && missingFieldKeys.Count > 0
                    ? " — <span style=\"color:#6b7280;\">"
                      + WebUtility.HtmlEncode(string.Join(", ", missingFieldKeys.Select(FieldTitleFor)))
                      + "</span>"
                    : string.Empty;
                return $"<li style=\"margin:0 0 6px;\">{title}{detail}</li>";
            }));

            // 🔑 §1081 stage A — THE REASON THIS WHOLE CHANGE EXISTS. A coordinator was chased while
            // a colleague had already done several steps, and the mail never said so — so it read as
            // the hub not knowing, and the operator as not being listened to. Both were right: the
            // steps named below WERE done, and the ones above genuinely were not. Saying both is what
            // turns an accusation into a shared checklist.
            tokens["doneStepCount"] = doneKeys.Count.ToString();

            // 🔒 The whole block is ONE token, so nothing is emitted when nothing is done yet — a
            // heading reading "Already done (0):" above an empty box would be worse than silence, and
            // that is the state a brand-new sponsor is in.
            var doneItems = string.Concat(doneKeys.Select(k =>
            {
                var title = WebUtility.HtmlEncode(TitleFor(titlePrefix, k));
                if (!credits.TryGetValue(k, out var credit)) return $"<li style=\"margin:0 0 6px;\">{title}</li>";
                // Attribution ONLY where the hub actually recorded it — never inferred.
                var by = WebUtility.HtmlEncode(credit.Email);
                // 🔒 InvariantCulture, deliberately: this date sits inside an English template body,
                // and a server whose thread culture happens to be da-DK would otherwise render a
                // Danish month into an English sentence. The rest of the mail's copy is resx-driven;
                // this fragment is composed here, so it has to pick its own culture rather than
                // inherit whatever the job host was started with.
                var when = credit.When is { } w
                    ? " on " + w.UtcDateTime.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture)
                    : string.Empty;
                return $"<li style=\"margin:0 0 6px;\">{title} — <span style=\"color:#6b7280;\">"
                     + $"{by}{when}</span></li>";
            }));
            tokens["doneBlockHtml"] = doneKeys.Count == 0
                ? string.Empty
                : $"<p style=\"margin:0 0 8px;font-weight:600;\">Already done ({doneKeys.Count}):</p>"
                  + "<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"margin:0 0 16px;\">"
                  + "<tr><td style=\"background-color:#f4faf5;border-left:3px solid #4caf50;padding:14px 18px;border-radius:4px;\">"
                  + $"<ul style=\"margin:0;padding:0 0 0 18px;line-height:1.6;\">{doneItems}</ul>"
                  + "</td></tr></table>";

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
    /// §1081 stage A — what the digest needs to describe the checklist HONESTLY: what is still open,
    /// what is already DONE (and, for a sponsor, who did it and when), and — for the sponsor company
    /// step — WHICH FIELDS are missing.
    /// </summary>
    /// <param name="Credits">Step key → "completed by X on date", where we actually know. Empty for
    /// the roles whose steps are personal: attribution only means something for a COMPANY checklist,
    /// where the reader may not be the person who did the work.</param>
    private sealed record DigestSteps(
        IReadOnlyList<string> OpenKeys,
        IReadOnlyList<string> DoneKeys,
        string TitlePrefix,
        string Route,
        IReadOnlyList<string> MissingFieldKeys,
        IReadOnlyDictionary<string, (string Email, DateTimeOffset? When)> Credits);

    /// <summary>
    /// The step keys for this participant's wizard, with the resx title prefix and the wizard route
    /// the digest's magic-link button deep-links to — or null when the role has no wizard for this
    /// person (e.g. a sponsor contact without a company).
    /// <para>Sponsor steps with an UNDETERMINABLE state (<c>Done == null</c>) are counted as NEITHER
    /// open nor done — mirroring the wizard page, which never makes them the "Continue" target, and
    /// §1081's rule that a step we cannot evaluate must not be presented as an obligation.</para>
    /// </summary>
    private async Task<DigestSteps?> StepsForDigestAsync(
        int eventId, int participantId, ParticipantRole role,
        Dictionary<string, SponsorCompanyDigestFacts> companyFacts, CancellationToken ct)
    {
        // The three wizards return three unrelated step records (SpeakerWizardStep / RoleWizardStep /
        // SponsorWizardStep) with no shared interface, so each call site projects to (key, done)
        // before this runs. 🔑 No attribution: these steps are PERSONAL, and "completed by you" is
        // noise. Attribution earns its place only on a COMPANY checklist, where the reader may not be
        // the person who did the work.
        static DigestSteps Personal(IEnumerable<(string Key, bool Done)> steps, string prefix) =>
            new(steps.Where(s => !s.Done).Select(s => s.Key).ToList(),
                steps.Where(s => s.Done).Select(s => s.Key).ToList(),
                prefix, "/Forms/Wizard",
                Array.Empty<string>(),
                new Dictionary<string, (string, DateTimeOffset?)>());

        switch (role)
        {
            case ParticipantRole.Speaker:
                return Personal(
                    (await _speakerWizard.BuildAsync(eventId, participantId, ct))
                        .Steps.Select(s => (s.Key, s.Done)),
                    "SpeakerWiz.Step.");

            case ParticipantRole.Sponsor:
            {
                var v = await _sponsorWizard.BuildAsync(eventId, participantId, ct);
                if (v is null) return null; // no company link ⇒ no wizard

                // 🔑 The sponsor checklist is COMPANY state, so the facts behind it are read ONCE per
                // company and shared by every coordinator due a digest — the same row, the same
                // answer, and one query set instead of one per person.
                var companyId = await _db.Participants.AsNoTracking()
                    .Where(p => p.Id == participantId)
                    .Select(p => p.SponsorCompanyId)
                    .FirstOrDefaultAsync(ct) ?? string.Empty;

                if (!companyFacts.TryGetValue(companyId, out var facts))
                {
                    facts = await LoadSponsorFactsAsync(eventId, companyId, ct);
                    companyFacts[companyId] = facts;
                }

                return new DigestSteps(
                    v.Steps.Where(s => s.Done == false).Select(s => s.Key).ToList(),
                    v.Steps.Where(s => s.Done == true).Select(s => s.Key).ToList(),
                    "SponsorWiz.Step.", "/Forms/Wizard",   // §557: all roles go to the new Get Started
                    facts.MissingFieldKeys,
                    facts.Credits);
            }

            case ParticipantRole.Attendee:
                return Personal(
                    (await _attendeeWizard.BuildAsync(eventId, participantId, ct))
                        .Steps.Select(s => (s.Key, s.Done)),
                    "RoleWiz.Step.");

            default:
            {
                if (!RoleWizardService.Handles(role)) return null;
                return Personal(
                    (await _roleWizard.BuildAsync(eventId, participantId, ct))
                        .Steps.Select(s => (s.Key, s.Done)),
                    "RoleWiz.Step.");
            }
        }
    }

    /// <summary>
    /// §1081 stage A — the per-COMPANY facts the sponsor digest needs beyond the step list:
    /// which content fields are still blank, and who completed the steps that ARE done.
    /// </summary>
    private sealed record SponsorCompanyDigestFacts(
        IReadOnlyList<string> MissingFieldKeys,
        IReadOnlyDictionary<string, (string Email, DateTimeOffset? When)> Credits);

    /// <summary>
    /// Reads the attribution the hub genuinely holds — never invents it. Where a step was completed
    /// by a route that recorded no actor, it simply appears as done with no name, which is honest and
    /// still answers the question the reported incident turned on (*"a colleague already did this"*).
    /// </summary>
    private async Task<SponsorCompanyDigestFacts> LoadSponsorFactsAsync(
        int eventId, string companyId, CancellationToken ct)
    {
        var credits = new Dictionary<string, (string Email, DateTimeOffset? When)>(StringComparer.Ordinal);

        var info = await _db.SponsorInfos.AsNoTracking()
            .FirstOrDefaultAsync(s => s.EventId == eventId && s.SponsorCompanyId == companyId, ct);

        var content = Core.Sponsors.SponsorCompanyContent.StatusOf(info);

        if (info is not null)
        {
            if (content.AllDelivered && !string.IsNullOrWhiteSpace(info.LastUpdatedByEmail))
                credits["company"] = (info.LastUpdatedByEmail!, info.UpdatedAt);

            if (!string.IsNullOrWhiteSpace(info.BoothCheckInSlot)
                && !string.IsNullOrWhiteSpace(info.BoothCheckInSetByEmail))
                credits["booth-checkin"] = (info.BoothCheckInSetByEmail!, info.BoothCheckInSetAt);
        }

        // The logo step — the one that produced the reported complaint. The audit records who
        // uploaded each kind and when; the LATEST of the two is the honest "completed on".
        var lastLogo = await _db.SponsorUploadAudits.AsNoTracking()
            .Where(a => a.EventId == eventId && a.SponsorCompanyId == companyId
                        && (a.Kind == "some" || a.Kind == "print"))
            .OrderByDescending(a => a.UploadedAt)
            .Select(a => new { a.UploadedByEmail, a.UploadedAt })
            .FirstOrDefaultAsync(ct);
        if (lastLogo is not null && !string.IsNullOrWhiteSpace(lastLogo.UploadedByEmail))
            credits["logos"] = (lastLogo.UploadedByEmail, lastLogo.UploadedAt);

        return new SponsorCompanyDigestFacts(content.MissingFieldKeys, credits);
    }

    /// <summary>The step's GUI title from SharedResource.resx (fallback: the key itself).</summary>
    private static string TitleFor(string prefix, string key)
    {
        try { return StepTitles.Value.GetString(prefix + key) ?? key; }
        catch { return key; }
    }

    /// <summary>
    /// §1081 — a missing CONTENT FIELD's label, from the same resx the form labels come from, so the
    /// mail names the field the sponsor will actually see on the page.
    /// </summary>
    private static string FieldTitleFor(string fieldKey)
    {
        try
        {
            return StepTitles.Value.GetString(
                Core.Sponsors.SponsorCompanyContent.ResourcePrefix + fieldKey) ?? fieldKey;
        }
        catch { return fieldKey; }
    }
}
