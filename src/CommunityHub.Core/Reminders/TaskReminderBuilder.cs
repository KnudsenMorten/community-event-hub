using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Reminders;

/// <summary>
/// Computes the task-deadline reminders that are currently due for an edition
/// (CONTEXT.md section 6/11). A single reminder is sent at 08:00 ON the day the
/// task is DUE — there is no multi-day-before cadence (REQUIREMENTS §81).
///
/// The OccasionKey embeds the task id only, so a task triggers exactly one
/// deadline reminder ever (the ReminderEngine dedups on it). A missed daily run
/// self-heals: an overdue task still fires once on the next run.
///
/// Email bodies are rendered from the branded template system
/// (task-deadline-reminder.html into _layout.html) - no HTML is built here.
/// </summary>
public sealed class TaskReminderBuilder
{
    private const string TemplateName = "task-deadline-reminder";

    private const string DefaultSupportEmail = "info@expertslive.dk";

    private readonly CommunityHubDbContext _db;
    private readonly EmailTemplateProvider _templates;
    private readonly TimeProvider _clock;
    private readonly SponsorRecipientResolver _sponsorRecipients;

    // Optional edition-config source for the per-role contact footer (config →
    // token bridge). Null in older test constructions — then the contact tokens
    // render blank / fall back to the support email, leaving behaviour unchanged.
    private readonly EventEditionConfigLoader? _eventConfigLoader;
    private readonly EventConfigOptions? _eventConfigOptions;

    // Lazily-loaded once and cached so we don't re-read the JSON per message.
    private IReadOnlyDictionary<string, string>? _placeholders;
    private string? _supportEmail;

    /// <summary>The reminder TYPE — the ledger key this mail's history is stored under.</summary>
    public const string ReminderTypeName = "task-deadline";

    private readonly EmailReminderCadenceService? _cadence;

    public TaskReminderBuilder(
        CommunityHubDbContext db,
        EmailTemplateProvider templates,
        TimeProvider clock,
        SponsorRecipientResolver sponsorRecipients,
        EventEditionConfigLoader? eventConfigLoader = null,
        EventConfigOptions? eventConfigOptions = null,
        // §707.11 — optional so existing constructions keep compiling; null ⇒ the shipped default.
        EmailReminderCadenceService? cadence = null)
    {
        _cadence = cadence;
        _db = db;
        _templates = templates;
        _clock = clock;
        _sponsorRecipients = sponsorRecipients;
        _eventConfigLoader = eventConfigLoader;
        _eventConfigOptions = eventConfigOptions;
    }

    /// <summary>
    /// Resolve (once, cached) the edition placeholders + support email used for the
    /// per-role contact footer. With no config loader wired, returns an empty map
    /// and the default support email so the footer is render-blank-safe.
    /// </summary>
    private (IReadOnlyDictionary<string, string> Placeholders, string SupportEmail) ContactConfig()
    {
        if (_placeholders is not null && _supportEmail is not null)
        {
            return (_placeholders, _supportEmail);
        }

        var placeholders = (IReadOnlyDictionary<string, string>)
            new Dictionary<string, string>();
        var supportEmail = DefaultSupportEmail;

        if (_eventConfigLoader is not null)
        {
            try
            {
                var path = _eventConfigOptions?.EventConfigPath
                           ?? new EventConfigOptions().EventConfigPath;
                var cfg = _eventConfigLoader.Load(path);
                placeholders = cfg.Placeholders ?? placeholders;
                if (cfg.Placeholders is not null
                    && cfg.Placeholders.TryGetValue("supportEmail", out var se)
                    && !string.IsNullOrWhiteSpace(se))
                {
                    supportEmail = se;
                }
            }
            catch
            {
                // Fail-safe: a missing/broken config never breaks reminders — the
                // footer just falls back to the default support email.
            }
        }

        _placeholders = placeholders;
        _supportEmail = supportEmail;
        return (placeholders, supportEmail);
    }

    public async Task<IReadOnlyList<ReminderMessage>> BuildDueAsync(
        int eventId, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);

        var ev = await _db.Events
            .Where(e => e.Id == eventId)
            .Select(e => new { e.CommunityName, e.DisplayName })
            .FirstOrDefaultAsync(ct);
        var communityName = ev?.CommunityName ?? string.Empty;
        var eventDisplayName = ev?.DisplayName ?? string.Empty;

        // 🔒 §707.11 — the task reminder now REPEATS (operator 2026-07-30: *"the 2 must follow same
        // pattern"*). It used to be once-ever (OccasionKey `task:{id}:due`, §81), so a person who
        // ignored it was never chased again. The interval is the operator's, per mail; `null` would
        // restore the old once-only behaviour.
        var intervalDays = _cadence is null
            ? EmailTemplateCatalog.DefaultIntervalDaysFor(TemplateName)
            : await _cadence.GetIntervalDaysAsync(eventId, TemplateName, ct);
        IReadOnlyDictionary<string, DateOnly> lastSentByOccasion = await EmailReminderCadenceService.LastSentByOccasionAsync(_db, eventId, ReminderTypeName, ct);

        // Open, assigned, dated tasks for this edition. §253 G9: only ACTIVE
        // assignees — a deactivated participant must never receive a due-day
        // reminder (the same gate the party builder has, AttendeePartyReminderBuilder).
        var tasks = await _db.Tasks
            .Where(t => t.EventId == eventId
                        && t.State != TaskState.Done
                        && t.DueDate != null
                        && t.AssignedParticipantId != null
                        && t.AssignedParticipant!.IsActive)
            .Select(t => new
            {
                t.Id,
                t.Title,
                DueDate = t.DueDate!.Value,
                // The full entity carries Role + SponsorCompanyId, which drive the
                // sponsor coordinator-only audience rule (REQUIREMENTS §7c) below.
                Participant = t.AssignedParticipant!,
                // Speaker contact-email override (null for non-speakers / unset).
                ContactEmailOverride = _db.SpeakerProfiles
                    .Where(sp => sp.ParticipantId == t.AssignedParticipantId)
                    .Select(sp => sp.ContactEmailOverride)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        var messages = new List<ReminderMessage>();
        foreach (var t in tasks)
        {
            var daysLeft = t.DueDate.DayNumber - today.DayNumber;

            // REQUIREMENTS §81: a deadline reminder fires ONLY on the due day
            // (08:00, via the daily job). Not before. A missed run self-heals —
            // an overdue task (daysLeft < 0) still fires once on the next run.
            if (daysLeft > 0)
            {
                continue; // due date not reached yet
            }

            // 🔒 §707.11 — REPEAT until the task is done. The DUE DATE is the anchor and
            // `firstSendAtAnchor: true` keeps §81 intact: the first mail still lands ON the due day,
            // and only the REPEATS are spaced by the interval, measured per TASK from its last send
            // (§707.10). `State != Done` above is the stop signal, so finishing the task ends it.
            var taskOccasion = $"task:{t.Id}";
            var lastSentForTask = lastSentByOccasion.TryGetValue(taskOccasion, out var tls)
                ? tls : (DateOnly?)null;
            if (!EmailReminderCadenceService.IsDue(
                    today, t.DueDate, lastSentForTask, intervalDays, firstSendAtAnchor: true))
            {
                continue;
            }

            var firstName = string.IsNullOrWhiteSpace(t.Participant.FullName)
                ? "there"
                : t.Participant.FullName.Split(' ')[0];
            // §340-A-1: the wording must match what actually happened. §81 made this a
            // due-DAY reminder and the copy was collapsed to a single "due today" — but
            // this builder deliberately fires for OVERDUE tasks too (the daysLeft > 0
            // skip above, and the class doc: "an overdue task still fires once on the
            // next run"). So a task due weeks ago was announcing itself as due today.
            // That is not cosmetic: the reminder is a deadline instruction, and a wrong
            // deadline is worse than none. It bites hardest exactly when a release ring
            // is widened, because every newly-in-ring person receives their whole
            // backlog at once — the moment the mail must be trustworthy.
            // `dueDate` is already in the token set, so the real date is shown either way.
            var state = daysLeft == 0 ? "due today" : "overdue";

            // Per-role organizer-lead contact footer (config → token bridge). The
            // names/emails live ONLY in the edition config; here we just resolve
            // the recipient's role to contactName/contactEmail/supportEmail tokens.
            var (placeholders, supportEmail) = ContactConfig();

            // §169: render the reminder for a SPECIFIC recipient participant so the
            // hub CTA ({{hubUrl}}) becomes THAT recipient's personal auto-login
            // magic-link. The sponsor branch below sends one body PER coordinator
            // (each a known participant), so it renders once PER coordinator — every
            // coordinator gets their OWN link instead of one shared plain URL. The
            // seam is fail-safe: with no participant / no magic-link service wired,
            // the plain hub URL is kept (see EmailTemplateProvider.NewTokenSet).
            RenderedEmail RenderForRecipient(int? recipientParticipantId)
            {
                var tokens = _templates.NewTokenSet(recipientParticipantId);
                tokens["firstName"] = firstName;
                tokens["communityName"] = communityName;
                tokens["eventDisplayName"] = eventDisplayName;
                tokens["taskTitle"] = t.Title;
                tokens["dueDate"] = t.DueDate.ToString("d MMM yyyy");
                tokens["state"] = state;
                tokens["taskLink"] = "Open the hub to see and update this task.";
                RoleContact.AddTo(tokens, t.Participant.Role, placeholders, supportEmail);
                return _templates.Render(TemplateName, tokens);
            }

            // Persona-aware reminder (10a-4): the persona group is derived from the
            // assigned participant's role so the send is categorised per persona
            // (volunteer / speaker / media / sponsor / organizer) in the log. The
            // participant's secondary email rides along as CC (10a-5).
            var persona = Email.OnboardingEmailSets.PersonaFor(t.Participant.Role)
                .ToString();

            // §707.11 — the occasion is the DAY now; "due" is decided above by the due date plus
            // lastSent + interval. Was `task:{id}:due`, which by having no varying segment is what
            // made this mail once-ever.
            var occasionKey = $"{taskOccasion}:{today:yyyyMMdd}";

            // SPONSOR audience rule (REQUIREMENTS §7c): a sponsor task reminder
            // does NOT go to whoever the task happens to be assigned to (which may
            // be a signer-only contact) — it goes to the company's EVENT-COORDINATOR
            // contacts (signer-only excluded, both-roles included, all coordinators).
            // Routed through the shared SponsorRecipientResolver so the audience rule
            // lives in one place. The OccasionKey embeds each coordinator's address
            // so every coordinator is deduped independently in the ledger.
            if (t.Participant.Role == ParticipantRole.Sponsor
                && !string.IsNullOrWhiteSpace(t.Participant.SponsorCompanyId))
            {
                var coordinators = await _sponsorRecipients.ResolveAsync(
                    eventId, t.Participant.SponsorCompanyId!, ct);
                foreach (var c in coordinators)
                {
                    // 🔒 §707.11 — each coordinator is chased on their OWN clock, so the root embeds
                    // the address and the DATE stays the final segment (the root is everything before
                    // the last ':'). Putting the date before the address would make every day a new
                    // root, and the cadence would never hold anyone back.
                    var cOccasion = $"{taskOccasion}:{c.Email}";
                    var cLastSent = lastSentByOccasion.TryGetValue(cOccasion, out var cls)
                        ? cls : (DateOnly?)null;
                    if (!EmailReminderCadenceService.IsDue(
                            today, t.DueDate, cLastSent, intervalDays, firstSendAtAnchor: true))
                    {
                        continue;
                    }

                    // §422: CcEmail is already the resolved alternate (see SponsorRecipient).
                    var cCc = c.CcEmail is null ? null : new[] { c.CcEmail };
                    // §169: render PER coordinator so each carries THEIR own magic-link.
                    var cRendered = RenderForRecipient(c.ParticipantId);
                    messages.Add(new ReminderMessage(
                        RecipientEmail: c.Email,
                        ReminderType: ReminderTypeName,
                        OccasionKey: $"{cOccasion}:{today:yyyyMMdd}",
                        Subject: cRendered.Subject,
                        HtmlBody: cRendered.HtmlBody,
                        DeliverToEmail: c.Email,
                        Persona: persona,
                        ParticipantId: c.ParticipantId,
                        RecipientName: c.FullName,
                        Cc: cCc, MailKey: TemplateName));
                }
                continue;
            }

            // §422: the alternate inbox the tasks banner advertises. It resolves the
            // organizer-set SecondaryEmail first, then the participant's OWN AlternateEmail —
            // which until now nothing in the mail path read, so "we'll copy every reminder
            // there as well" was not true for anyone who set it themselves.
            var ccAddress = Participants.AlternateEmailPolicy.CcFor(
                t.Participant.SecondaryEmail, t.Participant.AlternateEmail);
            var cc = ccAddress is null ? null : new[] { ccAddress };

            // §169: the assignee's own personal magic-link body.
            var rendered = RenderForRecipient(t.Participant.Id);
            messages.Add(new ReminderMessage(
                RecipientEmail: t.Participant.Email,
                ReminderType: "task-deadline",
                OccasionKey: occasionKey,
                Subject: rendered.Subject,
                HtmlBody: rendered.HtmlBody,
                // Deliver to the effective address (override ?? Sessionize);
                // the dedup key above stays the identity address.
                DeliverToEmail: SpeakerProfile.EffectiveEmailFor(
                    t.Participant.Email, t.ContactEmailOverride),
                Persona: persona,
                ParticipantId: t.Participant.Id,
                RecipientName: t.Participant.FullName,
                Cc: cc, MailKey: TemplateName));
        }

        return messages;
    }
}
