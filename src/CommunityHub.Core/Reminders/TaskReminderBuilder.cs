using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Reminders;

/// <summary>
/// Computes the task-deadline reminders that are currently due for an edition
/// (CONTEXT.md section 6/11). A reminder is due when an assigned task is still
/// open and its due date is within the reminder window.
///
/// The OccasionKey embeds the task id and the milestone, so the same task can
/// legitimately trigger a reminder at, say, 14 days out and again at 3 days
/// out, but never the same milestone twice (the ReminderEngine dedups on it).
///
/// Email bodies are rendered from the branded template system
/// (task-deadline-reminder.html into _layout.html) - no HTML is built here.
/// </summary>
public sealed class TaskReminderBuilder
{
    /// <summary>
    /// Days-before-due at which a reminder fires. Documented default cadence
    /// (weekly-ish, then a final nudge) per CONTEXT.md - not daily.
    /// </summary>
    private static readonly int[] MilestonesDaysBefore = { 14, 7, 3, 1 };

    private const string TemplateName = "task-deadline-reminder";

    private readonly CommunityHubDbContext _db;
    private readonly EmailTemplateProvider _templates;
    private readonly TimeProvider _clock;
    private readonly SponsorRecipientResolver _sponsorRecipients;

    public TaskReminderBuilder(
        CommunityHubDbContext db,
        EmailTemplateProvider templates,
        TimeProvider clock,
        SponsorRecipientResolver sponsorRecipients)
    {
        _db = db;
        _templates = templates;
        _clock = clock;
        _sponsorRecipients = sponsorRecipients;
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

            // Find the milestone this matches (if any). If a run was missed,
            // the most-passed milestone still fires once, then dedups.
            var milestone = MilestonesDaysBefore
                .Where(m => daysLeft <= m)
                .DefaultIfEmpty(-1)
                .Max();
            if (milestone < 0)
            {
                continue; // not yet within any reminder window
            }

            var firstName = string.IsNullOrWhiteSpace(t.Participant.FullName)
                ? "there"
                : t.Participant.FullName.Split(' ')[0];
            var state = daysLeft <= 0
                ? "due today"
                : daysLeft == 1 ? "due tomorrow"
                : $"due in {daysLeft} days";

            // Build the token set: branding tokens + this task's tokens.
            var tokens = _templates.NewTokenSet();
            tokens["firstName"] = firstName;
            tokens["communityName"] = communityName;
            tokens["eventDisplayName"] = eventDisplayName;
            tokens["taskTitle"] = t.Title;
            tokens["dueDate"] = t.DueDate.ToString("dd/MM/yyyy");
            tokens["state"] = state;
            tokens["taskLink"] = "Open the hub to see and update this task.";

            var rendered = _templates.Render(TemplateName, tokens);

            // Persona-aware reminder (10a-4): the persona group is derived from the
            // assigned participant's role so the send is categorised per persona
            // (volunteer / speaker / media / sponsor / organizer) in the log. The
            // participant's secondary email rides along as CC (10a-5).
            var persona = Email.OnboardingEmailSets.PersonaFor(t.Participant.Role)
                .ToString();

            var occasionKey = $"task:{t.Id}:m{milestone}";

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
                    messages.Add(new ReminderMessage(
                        RecipientEmail: c.Email,
                        ReminderType: "task-deadline",
                        OccasionKey: $"{occasionKey}:{c.Email}",
                        Subject: rendered.Subject,
                        HtmlBody: rendered.HtmlBody,
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
