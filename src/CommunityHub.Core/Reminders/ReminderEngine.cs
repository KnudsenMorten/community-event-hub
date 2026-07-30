using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Reminders;

/// <summary>
/// One reminder the engine wants to send.
///
/// <see cref="RecipientEmail"/> is the IDENTITY address — it is the idempotency
/// key (together with type + occasion) and what the ledger records, so it must
/// stay stable. <see cref="DeliverToEmail"/> is where the mail is actually sent:
/// a speaker's <c>EffectiveEmail</c> (override ?? Sessionize). When a builder
/// does not set it, delivery falls back to the identity address.
/// </summary>
public sealed record ReminderMessage(
    string RecipientEmail,
    string ReminderType,
    string OccasionKey,
    string Subject,
    string HtmlBody,
    string? DeliverToEmail = null,
    string? Persona = null,
    int? ParticipantId = null,
    string? RecipientName = null,
    IReadOnlyCollection<string>? Cc = null,
    string? FeatureKey = null,
    // §707 — THE MAIL IDENTITY: the EmailTemplateCatalog key of the mail this message IS.
    //
    // 🔒 Without it every reminder resolved by its FEATURE ring. The gate uses the per-mail ring only
    // when the send sets EmailContext.TemplateName, and the builders rendered a template then dropped
    // its identity before sending — so the per-mail rings shown on /Organizer/Settings for
    // task-deadline-reminder, getstarted-digest, hotel-cutoff-reminder and app-game-gift-reminder were
    // NEVER CONSULTED. A control displaying an audience it did not govern (§326bx), on the busiest mail
    // path in the system (attendee-party 71 sends, getstarted-digest 29).
    //
    // It also blocked the per-ROLE control: "reminder to speaker is NOT the same as reminder to
    // organizer" (§705.3b) is exactly a reminder, and reminders were the one path that could not carry
    // a per-mail ring at all.
    //
    // Set it to the SAME key the builder passes to _templates.Render(...). NULL keeps the old
    // feature-ring behaviour, so an un-migrated builder degrades to what it did before rather than
    // failing closed and going silent.
    string? MailKey = null)
{
    /// <summary>The address the mail is actually sent to: override ?? identity.</summary>
    public string EffectiveRecipient =>
        string.IsNullOrWhiteSpace(DeliverToEmail) ? RecipientEmail : DeliverToEmail;

    /// <summary>
    /// The FeatureCatalog key the transport ring-gates this message on. Defaults to
    /// <c>reminder-jobs</c> (the historic behaviour for every cadence); a builder may
    /// override it (§250: the Get-Started digest rides the <c>welcome-email</c> ring).
    /// </summary>
    public string EffectiveFeatureKey =>
        string.IsNullOrWhiteSpace(FeatureKey) ? "reminder-jobs" : FeatureKey;
}

/// <summary>
/// The reminder-sending engine (CONTEXT.md section 11c-e). It is deliberately
/// stateless and idempotent: callers compute the set of reminders that are
/// currently due, hand them here, and the engine sends only the ones not
/// already recorded in the <see cref="SentReminder"/> ledger.
///
/// Because "already sent" lives in the database (a UNIQUE index on
/// EventId+recipient+type+occasion), a missed scheduled run self-heals on the
/// next run, and two overlapping runs cannot double-send. This is the fix for
/// the source PowerShell scripts, which had no dedup and re-sent daily.
/// </summary>
public sealed class ReminderEngine
{
    private readonly CommunityHubDbContext _db;
    private readonly IEmailSender _emailSender;
    private readonly TimeProvider _clock;
    private readonly IEmailContextAccessor? _emailContext;
    private readonly IBulkSendPacer? _pacer;
    // §234: delivered-vs-dropped seam — a ring-dropped reminder is NOT recorded in
    // the SentReminder ledger, so it is retried on a later run once rings widen.
    // Null (legacy/test wiring) ⇒ old always-record behaviour.
    private readonly IEmailDeliveryOutcome? _outcome;

    public ReminderEngine(
        CommunityHubDbContext db,
        IEmailSender emailSender,
        TimeProvider clock,
        IEmailContextAccessor? emailContext = null,
        IBulkSendPacer? pacer = null,
        IEmailDeliveryOutcome? outcome = null)
    {
        _db = db;
        _emailSender = emailSender;
        _clock = clock;
        _emailContext = emailContext;
        // §219 (Risk-4): paces this bulk loop under Brevo's rate limit. Optional so
        // existing tests/callers that construct the engine without a pacer are unchanged
        // (null ⇒ no pacing delay).
        _pacer = pacer;
        _outcome = outcome;
    }

    /// <summary>
    /// Send each reminder that has not already been recorded for this edition.
    /// Returns the count actually sent. Per-message failures are swallowed so
    /// one bad address does not abort the batch; they are simply not recorded
    /// as sent and will be retried next run.
    /// </summary>
    public async Task<int> SendDueAsync(
        int eventId,
        IReadOnlyCollection<ReminderMessage> due,
        CancellationToken ct = default)
    {
        if (due.Count == 0) return 0;

        // Load the already-sent keys for this edition in one query.
        var alreadySent = await _db.SentReminders
            .Where(s => s.EventId == eventId)
            .Select(s => new
            {
                s.RecipientEmail, s.ReminderType, s.OccasionKey
            })
            .ToListAsync(ct);

        var sentKeys = alreadySent
            .Select(s => Key(s.RecipientEmail, s.ReminderType, s.OccasionKey))
            .ToHashSet();

        var sentCount = 0;
        // §219 (Risk-4): count of messages actually DISPATCHED (attempted) so far, used
        // to pace BETWEEN sends. An idempotent-skip does not increment it, so the pace is
        // inserted only between real outbound sends.
        var dispatched = 0;
        foreach (var msg in due)
        {
            var key = Key(msg.RecipientEmail, msg.ReminderType, msg.OccasionKey);
            if (sentKeys.Contains(key))
            {
                continue; // already sent - idempotent skip
            }

            // §219 PACING: delay before every send AFTER the first, so a ~1500-recipient
            // batch is spaced under Brevo's per-second rate limit. The first send fires
            // immediately (no leading latency); skipped/duplicate messages never pace.
            if (dispatched > 0 && _pacer is not null)
            {
                await _pacer.PaceAsync(ct);
            }
            dispatched++;

            try
            {
                // Dedup keys on the identity address (msg.RecipientEmail); the
                // mail itself goes to the effective address (override ?? id).
                // The persona-aware category + participant flow into the EmailLog
                // (10a-4); the secondary email rides along as CC.
                var category = string.IsNullOrWhiteSpace(msg.Persona)
                    ? msg.ReminderType
                    : $"{msg.ReminderType}:{msg.Persona}";
                // §707 — carry the MAIL IDENTITY so the send resolves its own (mail × role) ring
                // instead of falling back to the feature ring. `category` stays the reminder TYPE:
                // it is the ledger/occasion key, not a mail identity, and conflating the two is what
                // hid this for so long.
                using (_emailContext?.Set(new EmailContext(
                    category, eventId, msg.ParticipantId, msg.RecipientName,
                    TemplateName: msg.MailKey,
                    FeatureKey: msg.EffectiveFeatureKey)))
                {
                    await _emailSender.SendAsync(
                        msg.EffectiveRecipient, msg.Subject, msg.HtmlBody, msg.Cc, ct);
                }

                // §234: a gated (ring-dropped / kill-switched) reminder is NOT
                // recorded in the ledger — it is retried on a later run once the
                // operator widens the ring, exactly like a failed send.
                if (_outcome is not null && !_outcome.LastSendDelivered)
                {
                    continue;
                }
            }
            catch
            {
                // Not recorded as sent => retried on the next run.
                continue;
            }

            // Record the send. The UNIQUE index is the final guard against a
            // duplicate if two runs raced.
            _db.SentReminders.Add(new SentReminder
            {
                EventId = eventId,
                RecipientEmail = msg.RecipientEmail,
                ReminderType = msg.ReminderType,
                OccasionKey = msg.OccasionKey,
                SentAt = _clock.GetUtcNow(),
            });

            try
            {
                await _db.SaveChangesAsync(ct);
                sentKeys.Add(key);
                sentCount++;
            }
            catch (DbUpdateException)
            {
                // Almost always: a concurrent run already inserted this exact
                // SentReminder (the UNIQUE index rejected the duplicate) - the
                // email is recorded, nothing to do. NOTE: a non-constraint DB
                // error would also land here and the just-sent email would not
                // be recorded, so it could re-send next run. Acceptable given
                // the job is single-instance and daily; if that changes,
                // inspect the inner exception to distinguish the two cases.
                _db.ChangeTracker.Clear();
            }
        }

        return sentCount;
    }

    private static string Key(string email, string type, string occasion) =>
        $"{email}\u0001{type}\u0001{occasion}";
}
