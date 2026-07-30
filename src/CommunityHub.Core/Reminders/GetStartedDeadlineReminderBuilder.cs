using System.Globalization;
using System.Net;
using System.Resources;
using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using CommunityHub.Forms;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Reminders;

/// <summary>
/// §326b (operator 2026-07-25) — the ONE-SHOT "complete your Get Started before the
/// deadline" reminder for SPEAKERS.
///
/// <para><b>Why it exists.</b> The Get-Started completion deadline for speakers is
/// 1 Oct 2026; the operator wants one reminder mail on 30 Sep 2026 sent ONLY to
/// speakers whose Get-Started wizard is not yet complete. Speakers who finished get
/// nothing. This is distinct from the §250 biweekly digest (which keeps its own
/// cadence and double-nag guard) — this is a deadline-anchored, once-ever nudge.</para>
///
/// <para><b>Config.</b> The dates live in the speaker-deadlines config file
/// (<see cref="SpeakerDeadlineConfig.GetStartedDeadline"/> — reminderDate +
/// deadline). No block ⇒ the builder is inert. Dates are EVENT-LOCAL (Danish,
/// <see cref="EventTimezone.Tz"/>) calendar dates.</para>
///
/// <para><b>Window + dedup.</b> Fires when event-local "today" is inside
/// [reminderDate, deadline]: the window (rather than an exact-date match) lets a
/// ring-dropped or missed-run send self-heal on the following run(s) before the
/// deadline passes. The OccasionKey <c>getstarted-deadline:{pid}</c> carries NO
/// window index, so the <see cref="ReminderEngine"/> ledger sends it ONCE EVER per
/// speaker — and only stamps on real delivery (<see cref="IEmailDeliveryOutcome"/>),
/// exactly like every other reminder.</para>
///
/// <para><b>Completion signal.</b> The open steps come from
/// <see cref="SpeakerWizardService"/> itself (never the task table), the same source
/// the wizard page renders — any open step counts, party included (the operator's
/// rule is "sent if Get Started is NOT completed", not the §250 digest's
/// self-nagging skip).</para>
/// </summary>
public sealed class GetStartedDeadlineReminderBuilder
{
    private const string TemplateName = "getstarted-deadline-reminder";

    /// <summary>ReminderType + OccasionKey prefix in the SentReminder ledger.</summary>
    public const string ReminderType = "getstarted-deadline";

    private static readonly JsonHelper Config = new();

    /// <summary>Step titles from the SAME shared resource the wizard renders.</summary>
    private static readonly Lazy<ResourceManager> StepTitles = new(() =>
        new ResourceManager(
            "CommunityHub.Core.Resources.SharedResource",
            typeof(Resources.SharedResource).Assembly));

    private readonly CommunityHubDbContext _db;
    private readonly EmailTemplateProvider _templates;
    private readonly TimeProvider _clock;
    private readonly SpeakerWizardService _speakerWizard;
    private readonly SpeakerDeadlineOptions _options;

    public GetStartedDeadlineReminderBuilder(
        CommunityHubDbContext db,
        EmailTemplateProvider templates,
        TimeProvider clock,
        SpeakerWizardService speakerWizard,
        SpeakerDeadlineOptions options)
    {
        _db = db;
        _templates = templates;
        _clock = clock;
        _speakerWizard = speakerWizard;
        _options = options;
    }

    public async Task<IReadOnlyList<ReminderMessage>> BuildDueAsync(
        int eventId, CancellationToken ct = default)
    {
        var deadline = Config.Load(_options.ConfigPath)?.GetStartedDeadline;
        if (deadline is null) return Array.Empty<ReminderMessage>();

        // Event-local (Danish) date — the operator thinks in Danish dates, and the
        // 08:00-UTC job must not fire a day early/late around midnight boundaries.
        var todayLocal = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), EventTimezone.Tz).DateTime);
        if (todayLocal < deadline.ReminderDate || todayLocal > deadline.Deadline)
            return Array.Empty<ReminderMessage>();

        var ev = await _db.Events
            .Where(e => e.Id == eventId)
            .Select(e => new { e.CommunityName, e.DisplayName })
            .FirstOrDefaultAsync(ct);
        if (ev is null) return Array.Empty<ReminderMessage>();

        // §499 — Remindable() rather than a bare IsActive. The pair (IsActive AND lifecycle Active)
        // is what PinIdentityProvider demands to sign in, so nobody is chased about a deadline they
        // could not act on even if they wanted to.
        var speakers = await _db.Participants
            .AsNoTracking()
            .Remindable()
            .Where(p => p.EventId == eventId
                        && p.Role == ParticipantRole.Speaker)
            .Select(p => new { p.Id, p.Email, p.FullName })
            .ToListAsync(ct);

        var deadlineText = deadline.Deadline.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);
        var messages = new List<ReminderMessage>();
        foreach (var p in speakers)
        {
            if (string.IsNullOrWhiteSpace(p.Email)) continue;

            var wizard = await _speakerWizard.BuildAsync(eventId, p.Id, ct);
            var openKeys = wizard.Steps.Where(s => !s.Done).Select(s => s.Key).ToList();
            if (openKeys.Count == 0) continue;   // completed — no mail, ever

            var firstName = string.IsNullOrWhiteSpace(p.FullName)
                ? "there"
                : p.FullName.Split(' ')[0];

            var tokens = _templates.NewTokenSet(p.Id);
            tokens["firstName"] = firstName;
            tokens["communityName"] = ev.CommunityName ?? string.Empty;
            tokens["eventDisplayName"] = ev.DisplayName ?? string.Empty;
            tokens["deadlineDate"] = deadlineText;
            tokens["openStepCount"] = openKeys.Count.ToString(CultureInfo.InvariantCulture);
            tokens["openStepsHtml"] = string.Concat(openKeys.Select(k =>
                $"<li style=\"margin:0 0 6px;\">{WebUtility.HtmlEncode(TitleFor(k))}</li>"));
            var rendered = _templates.Render(TemplateName, tokens);

            messages.Add(new ReminderMessage(
                RecipientEmail: p.Email,
                ReminderType: ReminderType,
                // NO window index — once ever per speaker (self-heals inside the window).
                OccasionKey: $"{ReminderType}:{p.Id}",
                Subject: rendered.Subject,
                HtmlBody: rendered.HtmlBody,
                Persona: OnboardingEmailSets.PersonaFor(ParticipantRole.Speaker).ToString(),
                ParticipantId: p.Id,
                RecipientName: p.FullName,
                // Same transport ring the §250 digest rides (welcome-email).
                FeatureKey: "welcome-email", MailKey: TemplateName));
        }

        return messages;
    }

    private static string TitleFor(string key)
    {
        try { return StepTitles.Value.GetString("SpeakerWiz.Step." + key) ?? key; }
        catch { return key; }
    }

    /// <summary>Config-file reader (same file + shape the <see cref="SpeakerDeadlineSeeder"/>
    /// loads); tolerant of a missing file — the builder is then inert.</summary>
    private sealed class JsonHelper
    {
        private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        public SpeakerDeadlineConfig? Load(string path)
        {
            try
            {
                // §326bb: resolve against AppContext.BaseDirectory as well as the working
                // directory. This builder runs in the FUNCTIONS host, which starts from a
                // mounted package whose working dir is NOT the content root — so the bare
                // relative "config/speaker-deadlines.eldk27.json" missed, Load returned null,
                // and BuildDueAsync returned Array.Empty with no log line. The §326b
                // once-ever deadline mail would simply never have gone out on 2026-09-30,
                // and its 2-day window would have closed unnoticed. Same fix, and the same
                // prior incident, as EventEditionConfig / SponsorConfig / EmailTemplateProvider.
                var resolved = CommunityHub.Core.Config.ConfigPaths.Resolve(path);
                if (!File.Exists(resolved)) return null;
                return System.Text.Json.JsonSerializer.Deserialize<SpeakerDeadlineConfig>(
                    File.ReadAllText(resolved), JsonOptions);
            }
            catch
            {
                return null;   // unreadable/invalid config ⇒ inert, never a crashed run
            }
        }
    }
}
