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

    public TaskReminderBuilder(
        CommunityHubDbContext db,
        EmailTemplateProvider templates,
        TimeProvider clock,
        SponsorRecipientResolver sponsorRecipients,
        EventEditionConfigLoader? eventConfigLoader = null,
        EventConfigOptions? eventConfigOptions = null)
    {
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

        // Open, assigned, dated tasks for this edition.
        var tasks = await _db.Tasks
            .Where(t => t.EventId == eventId
                        && t.State != TaskState.Done
                        && t.DueDate != null
                        && t.AssignedParticipantId != null)
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

            var firstName = string.IsNullOrWhiteSpace(t.Participant.FullName)
                ? "there"
                : t.Participant.FullName.Split(' ')[0];
            // Single wording now the reminder only goes out on the due day (§81).
            var state = "due today";

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
                tokens["dueDate"] = t.DueDate.ToString("dd/MM/yyyy");
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

            // One reminder per task, ever (no milestone suffix any more — §81).
            var occasionKey = $"task:{t.Id}:due";

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
                    var cCc = string.IsNullOrWhiteSpace(c.SecondaryEmail)
                        ? null
                        : new[] { c.SecondaryEmail!.Trim() };
                    // §169: render PER coordinator so each carries THEIR own magic-link.
                    var cRendered = RenderForRecipient(c.ParticipantId);
                    messages.Add(new ReminderMessage(
                        RecipientEmail: c.Email,
                        ReminderType: "task-deadline",
                        OccasionKey: $"{occasionKey}:{c.Email}",
                        Subject: cRendered.Subject,
                        HtmlBody: cRendered.HtmlBody,
                        DeliverToEmail: c.Email,
                        Persona: persona,
                        ParticipantId: c.ParticipantId,
                        RecipientName: c.FullName,
                        Cc: cCc));
                }
                continue;
            }

            var cc = string.IsNullOrWhiteSpace(t.Participant.SecondaryEmail)
                ? null
                : new[] { t.Participant.SecondaryEmail!.Trim() };

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
                Cc: cc));
        }

        return messages;
    }
}
