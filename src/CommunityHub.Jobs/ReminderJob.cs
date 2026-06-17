using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Reminders;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// The reminder job (CONTEXT.md section 11). Runs daily. It first seeds any
/// missing speaker-deadline tasks (so a speaker imported yesterday gets their
/// deadlines today), then computes the task-deadline reminders that are due
/// for the active edition(s) and sends them through <see cref="ReminderEngine"/>
/// - which dedups against the SentReminder ledger so nothing is sent twice and
/// a missed run self-heals.
/// </summary>
public sealed class ReminderJob
{
    private readonly CommunityHubDbContext _db;
    private readonly SpeakerDeadlineSeeder _speakerDeadlines;
    private readonly TaskReminderBuilder _taskReminders;
    private readonly ReminderEngine _engine;
    private readonly CommunityHub.Core.Email.OnboardingStepResetEmailService _stepResetEmails;
    private readonly CommunityHub.Core.Email.SpeakerQuestionDigestService _speakerQuestionDigest;
    private readonly ILogger<ReminderJob> _log;

    public ReminderJob(
        CommunityHubDbContext db,
        SpeakerDeadlineSeeder speakerDeadlines,
        TaskReminderBuilder taskReminders,
        ReminderEngine engine,
        CommunityHub.Core.Email.OnboardingStepResetEmailService stepResetEmails,
        CommunityHub.Core.Email.SpeakerQuestionDigestService speakerQuestionDigest,
        ILogger<ReminderJob> log)
    {
        _db = db;
        _speakerDeadlines = speakerDeadlines;
        _taskReminders = taskReminders;
        _engine = engine;
        _stepResetEmails = stepResetEmails;
        _speakerQuestionDigest = speakerQuestionDigest;
        _log = log;
    }

    /// <summary>Daily at 08:00 UTC. NCRONTAB: sec min hour day month weekday.</summary>
    [Function("ReminderJob")]
    public async Task Run(
        [TimerTrigger("0 0 8 * * *")] TimerInfo timer,
        CancellationToken ct)
    {
        var activeEventIds = await _db.Events
            .Where(e => e.IsActive)
            .Select(e => e.Id)
            .ToListAsync(ct);

        foreach (var eventId in activeEventIds)
        {
            // Seed speaker-deadline tasks first - idempotent, so this only
            // creates tasks for speakers who do not yet have them.
            var seeded = await _speakerDeadlines.SeedAsync(eventId, ct);

            var due = await _taskReminders.BuildDueAsync(eventId, ct);
            var sent = await _engine.SendDueAsync(eventId, due, ct);

            // Consume the onboarding flip-to-0 hand-off (10a-6): email each person
            // whose wizard step an organizer re-opened, then resolve the action.
            var stepResets = await _stepResetEmails.SendPendingAsync(eventId, ct);

            // Email each speaker a digest of the OPEN audience questions on their
            // sessions (§21). Idempotent: a digest only re-sends when a brand-new
            // question raises the speaker's open-question fingerprint.
            var questionDigests = await _speakerQuestionDigest.SendPendingAsync(eventId, ct);

            _log.LogInformation(
                "ReminderJob: event {EventId} - {Seeded} deadline tasks seeded, "
                + "{Due} reminders due, {Sent} sent, {StepResets} step-reset reminders sent, "
                + "{QuestionDigests} speaker question digests sent.",
                eventId, seeded, due.Count, sent, stepResets, questionDigests);
        }
    }
}
