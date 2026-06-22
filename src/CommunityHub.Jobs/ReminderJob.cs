using CommunityHub.Core.Audit;
using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Settings;
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
    private readonly FeatureGateService _gate;
    private readonly IAuditTrail _audit;
    private readonly ILogger<ReminderJob> _log;

    public ReminderJob(
        CommunityHubDbContext db,
        SpeakerDeadlineSeeder speakerDeadlines,
        TaskReminderBuilder taskReminders,
        ReminderEngine engine,
        CommunityHub.Core.Email.OnboardingStepResetEmailService stepResetEmails,
        CommunityHub.Core.Email.SpeakerQuestionDigestService speakerQuestionDigest,
        FeatureGateService gate,
        IAuditTrail audit,
        ILogger<ReminderJob> log)
    {
        _db = db;
        _speakerDeadlines = speakerDeadlines;
        _taskReminders = taskReminders;
        _engine = engine;
        _stepResetEmails = stepResetEmails;
        _speakerQuestionDigest = speakerQuestionDigest;
        _gate = gate;
        _audit = audit;
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
            // GATE (REQUIREMENTS §23): the reminder/digest automation is an advanced
            // feature, off by default. When disabled for this edition the job
            // no-ops — no tasks seeded, no reminders or digests computed/sent.
            if (!await _gate.IsFeatureEnabledAsync("reminder-jobs", eventId, ct))
            {
                _log.LogInformation(
                    "ReminderJob: event {EventId} — feature 'reminder-jobs' disabled, skipped.",
                    eventId);
                continue;
            }

            // Seed speaker-deadline tasks first - idempotent, so this only
            // creates tasks for speakers who do not yet have them.
            var seeded = await _speakerDeadlines.SeedAsync(eventId, ct);

            var due = await _taskReminders.BuildDueAsync(eventId, ct);
            var sent = await _engine.SendDueAsync(eventId, due, ct);

            // The digest/notification sends are a SECOND, finer gate ('digest-emails',
            // which itself depends on the global outbound-email switch): an organizer
            // can keep deadline seeding/reminders on but silence the speaker digests.
            int stepResets = 0, questionDigests = 0;
            var digestsOn = await _gate.AreAllEnabledAsync(
                eventId, ct, "digest-emails", FeatureCatalog.OutboundEmailKey);
            if (digestsOn)
            {
                // Consume the onboarding flip-to-0 hand-off (10a-6): email each person
                // whose wizard step an organizer re-opened, then resolve the action.
                stepResets = await _stepResetEmails.SendPendingAsync(eventId, ct);

                // Email each speaker a digest of the OPEN audience questions on their
                // sessions (§21). Idempotent: a digest only re-sends when a brand-new
                // question raises the speaker's open-question fingerprint.
                questionDigests = await _speakerQuestionDigest.SendPendingAsync(eventId, ct);
            }
            else
            {
                _log.LogInformation(
                    "ReminderJob: event {EventId} — feature 'digest-emails' disabled — "
                    + "no digest/step-reset emails sent.", eventId);
            }

            _log.LogInformation(
                "ReminderJob: event {EventId} - {Seeded} deadline tasks seeded, "
                + "{Due} reminders due, {Sent} sent, {StepResets} step-reset reminders sent, "
                + "{QuestionDigests} speaker question digests sent.",
                eventId, seeded, due.Count, sent, stepResets, questionDigests);

            // Named Engine event (REQUIREMENTS §24) — the reminder RUN summary. (Each
            // email is separately captured as an Email event.) Only when the run did
            // something (seeded/sent/digested).
            if (seeded + sent + stepResets + questionDigests > 0)
                await _audit.RecordAsync(new AuditEntry
                {
                    EventId = eventId,
                    Category = AuditCategory.Engine,
                    Action = "reminder-jobs",
                    ActorEmail = "system",
                    Source = AuditSource.Job,
                    Outcome = AuditOutcome.Success,
                    Summary = $"Reminder run: {seeded} deadlines seeded, {sent} reminders, "
                        + $"{stepResets} step-resets, {questionDigests} speaker digests",
                }, ct);
        }
    }
}
