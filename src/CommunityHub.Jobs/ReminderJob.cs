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
    private readonly PartyTaskSeeder _partyTasks;
    private readonly AttendeeMasterClassTaskSeeder _mcTasks;
    private readonly TaskReminderBuilder _taskReminders;
    private readonly AttendeePartyReminderBuilder _attendeePartyReminders;
    private readonly AttendeeMasterClassReminderBuilder _attendeeMcReminders;
    private readonly GetStartedDigestBuilder _getStartedDigests;
    private readonly OrganizerWelcomeAnchorSeeder _organizerAnchors;
    private readonly GetStartedDeadlineReminderBuilder _getStartedDeadline;
    private readonly HotelCutoffReminderBuilder _hotelCutoffs;
    // §1127 — the sponsor webshop-links chaser.
    private readonly SponsorWebshopLinksReminderBuilder _sponsorWebshopLinks;
    private readonly ReminderEngine _engine;
    private readonly CommunityHub.Core.Email.OnboardingStepResetEmailService _stepResetEmails;
    private readonly CommunityHub.Core.Email.SpeakerQuestionDigestService _speakerQuestionDigest;
    private readonly FeatureGateService _gate;
    private readonly IAuditTrail _audit;
    private readonly ILogger<ReminderJob> _log;

    public ReminderJob(
        CommunityHubDbContext db,
        SpeakerDeadlineSeeder speakerDeadlines,
        PartyTaskSeeder partyTasks,
        AttendeeMasterClassTaskSeeder mcTasks,
        TaskReminderBuilder taskReminders,
        AttendeePartyReminderBuilder attendeePartyReminders,
        AttendeeMasterClassReminderBuilder attendeeMcReminders,
        GetStartedDigestBuilder getStartedDigests,
        OrganizerWelcomeAnchorSeeder organizerAnchors,
        GetStartedDeadlineReminderBuilder getStartedDeadline,
        HotelCutoffReminderBuilder hotelCutoffs,
        SponsorWebshopLinksReminderBuilder sponsorWebshopLinks,
        ReminderEngine engine,
        CommunityHub.Core.Email.OnboardingStepResetEmailService stepResetEmails,
        CommunityHub.Core.Email.SpeakerQuestionDigestService speakerQuestionDigest,
        FeatureGateService gate,
        IAuditTrail audit,
        ILogger<ReminderJob> log)
    {
        _db = db;
        _speakerDeadlines = speakerDeadlines;
        _partyTasks = partyTasks;
        _mcTasks = mcTasks;
        _taskReminders = taskReminders;
        _attendeePartyReminders = attendeePartyReminders;
        _attendeeMcReminders = attendeeMcReminders;
        _getStartedDigests = getStartedDigests;
        _organizerAnchors = organizerAnchors;
        _getStartedDeadline = getStartedDeadline;
        _hotelCutoffs = hotelCutoffs;
        _sponsorWebshopLinks = sponsorWebshopLinks;
        _engine = engine;
        _stepResetEmails = stepResetEmails;
        _speakerQuestionDigest = speakerQuestionDigest;
        _gate = gate;
        _audit = audit;
        _log = log;
    }

    /// <summary>
    /// §878 — BASE TICK ONLY; the cadence is the operator's interval on /Organizer/Jobs.
    /// 🔒 Safe to run often: every builder's <c>OccasionKey</c> embeds the DAY, and the
    /// ReminderEngine ledger sends one message per occasion per day — so extra passes find
    /// nothing to send rather than mailing anyone twice. NCRONTAB: sec min hour day month weekday.
    /// </summary>
    [Function("ReminderJob")]
    public async Task Run(
        [TimerTrigger("0 */5 * * * *")] TimerInfo timer,
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

            // §164/§206: seed the crew + attendee party sign-up tasks (idempotent) so the
            // reminder computation below picks them up and nags anyone still unanswered.
            seeded += await _partyTasks.SeedAsync(eventId, ct);

            // §207: seed the 2-day attendee "Select your Master Class" tasks (idempotent).
            seeded += await _mcTasks.SeedAsync(eventId, ct);

            var due = await _taskReminders.BuildDueAsync(eventId, ct);
            var sent = await _engine.SendDueAsync(eventId, due, ct);

            // §177/§206: the CREW + ATTENDEE party-RSVP cadence — every 2 weeks from welcome
            // (no date gate) until they RSVP. Gated by the SAME 'reminder-jobs' switch as above
            // (this whole block only runs when it is enabled) + deduped per 2-week window by the engine.
            var attendeeParty = await _attendeePartyReminders.BuildDueAsync(eventId, ct);
            sent += await _engine.SendDueAsync(eventId, attendeeParty, ct);

            // §207: the 2-day attendee Master Class selection cadence — every 2 weeks from
            // welcome until they confirm a Master Class. Same gate + per-window dedup.
            var attendeeMc = await _attendeeMcReminders.BuildDueAsync(eventId, ct);
            sent += await _engine.SendDueAsync(eventId, attendeeMc, ct);

            // §250: the biweekly Get-Started-incomplete digest (WIZARD steps only, read
            // from the wizard services — never the task table). Same 'reminder-jobs' run
            // gate + per-window dedup; ring-gated at the transport under 'welcome-email'
            // (ReminderMessage.FeatureKey), and the engine's delivery-outcome seam keeps a
            // ring-dropped digest out of the ledger so it retries once rings widen.
            // §994 — organizers get NO welcome mail (WelcomeVariants returns null for Organizer), so
            // §738's "never welcomed ⇒ never chased" gate made them permanently unchaseable. Seed
            // the anchor from CreatedAt FIRST, so a new organizer is eligible on this very run
            // instead of needing the §985a hand-backfill repeated for every one added.
            // 🔒 Only fills a NULL, and eligibility is not a mail — the digest still skips a
            // 100 %-complete wizard.
            await _organizerAnchors.RunAsync(eventId, ct);

            var getStarted = await _getStartedDigests.BuildDueAsync(eventId, ct);
            sent += await _engine.SendDueAsync(eventId, getStarted, ct);

            // §326b: the ONE-SHOT speaker Get-Started DEADLINE reminder — fires inside
            // the configured reminderDate..deadline window (speaker-deadlines config,
            // event-local dates), once ever per speaker (no window index in the
            // occasion key), only to speakers whose wizard is incomplete. Same
            // 'reminder-jobs' run gate + welcome-email transport ring as the digest.
            var getStartedDeadline = await _getStartedDeadline.BuildDueAsync(eventId, ct);
            sent += await _engine.SendDueAsync(eventId, getStartedDeadline, ct);

            // §326bs: warn the ORGANIZERS 3 days before a hotel release deadline, carrying
            // that hotel's live over/under per night. Window-based (a missed run self-heals),
            // once ever per organizer per cut-off. Internal ops mail — the audience is the
            // Organizer role, never participants.
            var hotelCutoffs = await _hotelCutoffs.BuildDueAsync(eventId, ct);
            sent += await _engine.SendDueAsync(eventId, hotelCutoffs, ct);

            // §1127: chase a sponsor whose WEBSHOP website or LinkedIn is blank, and send the
            // operator one weekly list of the companies still missing something. Since §1125/§1126
            // the webshop owns those fields and the CEH inputs are read-only, so a blank can only
            // be fixed at the source — the chaser is what stops that being a dead end.
            // ⚠️ X/Twitter is optional and is never chased.
            var sponsorLinks = await _sponsorWebshopLinks.BuildAsync(eventId, ct);
            sent += await _engine.SendDueAsync(eventId, sponsorLinks, ct);

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

                // 🗑 §765 — THE §203 PENDING-APPROVALS MAIL MOVED OUT OF THIS JOB.
                //
                // §879 then SPLIT it in two, and both halves are their own interval-driven job:
                // SpeakersHeldJob (every 10 minutes, once per change) and
                // VolunteersAwaitingReviewJob (weekly). The operator owns both cadences on the Jobs
                // page instead of either inheriting whatever spacing the shared engine-alert
                // throttle window happened to impose (operator 2026-08-01: "we must receive reminder
                // daily … And it must be configurable on settings").
                //
                // 🔒 REMOVED here rather than left alongside: two callers would double-send it, and
                // the one whose cadence he can see would not be the one deciding.
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
