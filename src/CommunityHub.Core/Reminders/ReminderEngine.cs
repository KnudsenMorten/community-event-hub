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
    IReadOnlyCollection<string>? Cc = null)
{
    /// <summary>The address the mail is actually sent to: override ?? identity.</summary>
    public string EffectiveRecipient =>
        string.IsNullOrWhiteSpace(DeliverToEmail) ? RecipientEmail : DeliverToEmail;
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

    public ReminderEngine(
        CommunityHubDbContext db,
        IEmailSender emailSender,
        TimeProvider clock,
        IEmailContextAccessor? emailContext = null)
    {
        _db = db;
        _emailSender = emailSender;
        _clock = clock;
        _emailContext = emailContext;
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
        foreach (var msg in due)
        {
            var key = Key(msg.RecipientEmail, msg.ReminderType, msg.OccasionKey);
            if (sentKeys.Contains(key))
            {
                continue; // already sent - idempotent skip
            }

            try
            {
                // Dedup keys on the identity address (msg.RecipientEmail); the
                // mail itself goes to the effective address (override ?? id).
                // The persona-aware category + participant flow into the EmailLog
                // (10a-4); the secondary email rides along as CC.
                var category = string.IsNullOrWhiteSpace(msg.Persona)
                    ? msg.ReminderType
                    : $"{msg.ReminderType}:{msg.Persona}";
                using (_emailContext?.Set(new EmailContext(
                    category, eventId, msg.ParticipantId, msg.RecipientName)))
                {
                    await _emailSender.SendAsync(
                        msg.EffectiveRecipient, msg.Subject, msg.HtmlBody, msg.Cc, ct);
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
