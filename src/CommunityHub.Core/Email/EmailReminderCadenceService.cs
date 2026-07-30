using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Email;

/// <summary>One recurring mail's cadence, and where the value came from.</summary>
/// <param name="TemplateKey">The mail.</param>
/// <param name="IntervalDays">Days between repeats; <c>null</c> ⇒ send once, ever.</param>
/// <param name="IsOverridden">True when an organizer set it (rather than the shipped default).</param>
/// <param name="StopsWhen">What makes the chasing stop, for the Settings row.</param>
public sealed record EmailReminderCadenceState(
    string TemplateKey,
    int? IntervalDays,
    bool IsOverridden,
    string StopsWhen);

/// <summary>
/// §707.11 — reads and writes the per-mail repeat interval for RECURRING mail, and answers the only
/// question the builders actually ask: <b>is this person due again yet?</b>
/// </summary>
/// <remarks>
/// 🔑 <b><c>lastSentAt + IntervalDays</c>, never a calendar window.</b> Operator 2026-07-30:
/// *"last known sendt + x date - once it hits the race time, when an email goes out"*. The builders
/// previously derived <c>windowIndex = daysSince(welcome) / 14</c>, which is a different rule: a send
/// delayed by a ring drop delivered late in its window while the next window opened on schedule, so
/// one person could receive two mails a day apart (§707.10). Measuring from the last SEND makes the
/// gap real and self-correcting.
///
/// <para><b>The first send still waits a full interval from the anchor</b> (the welcome), which is the
/// §232 "quiet for two weeks after the welcome" rule — a person is not chased about a task in the same
/// breath as being welcomed.</para>
/// </remarks>
public sealed class EmailReminderCadenceService
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public EmailReminderCadenceService(CommunityHubDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>
    /// The interval in force for this mail: the operator's override, else the shipped default.
    /// <c>null</c> ⇒ once, ever.
    /// </summary>
    public async Task<int?> GetIntervalDaysAsync(
        int eventId, string templateKey, CancellationToken ct = default)
    {
        var row = await _db.EmailReminderCadences.AsNoTracking()
            .FirstOrDefaultAsync(c => c.EventId == eventId && c.TemplateKey == templateKey, ct);

        return row is not null
            ? row.IntervalDays
            : EmailTemplateCatalog.DefaultIntervalDaysFor(templateKey);
    }

    /// <summary>Every recurring mail with its effective cadence, for the Settings page.</summary>
    public async Task<IReadOnlyList<EmailReminderCadenceState>> GetAllAsync(
        int eventId, CancellationToken ct = default)
    {
        var rows = await _db.EmailReminderCadences.AsNoTracking()
            .Where(c => c.EventId == eventId)
            .Select(c => new { c.TemplateKey, c.IntervalDays })
            .ToListAsync(ct);

        var overrides = rows.ToDictionary(
            r => r.TemplateKey, r => r.IntervalDays, StringComparer.OrdinalIgnoreCase);

        return EmailTemplateCatalog.RecurringMails
            .Select(kv =>
            {
                var has = overrides.TryGetValue(kv.Key, out var ov);
                return new EmailReminderCadenceState(
                    kv.Key,
                    has ? ov : kv.Value.DefaultIntervalDays,
                    has,
                    kv.Value.StopsWhen);
            })
            .OrderBy(s => s.TemplateKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Set this mail's repeat interval. <paramref name="intervalDays"/> null ⇒ once, ever.
    /// Refuses a mail that is not recurring, so a control can never be created for a mail whose
    /// cadence would govern nothing.
    /// </summary>
    public async Task<bool> SetIntervalAsync(
        int eventId, string templateKey, int? intervalDays, string? byEmail,
        CancellationToken ct = default)
    {
        if (!EmailTemplateCatalog.IsRecurring(templateKey)) return false;
        if (intervalDays is <= 0) return false;

        var row = await _db.EmailReminderCadences
            .FirstOrDefaultAsync(c => c.EventId == eventId && c.TemplateKey == templateKey, ct);

        if (row is null)
        {
            row = new EmailReminderCadence { EventId = eventId, TemplateKey = templateKey };
            _db.EmailReminderCadences.Add(row);
        }

        row.IntervalDays = intervalDays;
        row.UpdatedAt = _clock.GetUtcNow();
        row.UpdatedByEmail = byEmail;

        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// The most recent send per OCCASION ROOT for this reminder type — one query for the whole batch,
    /// because a builder must never issue a lookup per candidate.
    /// </summary>
    /// <remarks>
    /// 🔑 <b>Keyed on the occasion root, NOT on the recipient.</b> The root identifies the THING being
    /// chased (<c>task:42</c>, <c>party-attendee:7</c>, <c>getstarted:19</c>); §707.11 appends the send
    /// date as the last segment, so the root is everything before the final <c>:</c>.
    ///
    /// <para>Keying on the recipient instead would silently collapse a person's separate chases into
    /// one: someone with three overdue tasks would hear about ONE of them per interval and the other
    /// two would go quiet — a reminder system that stops reminding.</para>
    /// </remarks>
    public Task<IReadOnlyDictionary<string, DateOnly>> LastSentByOccasionAsync(
        int eventId, string reminderType, CancellationToken ct = default) =>
        LastSentByOccasionAsync(_db, eventId, reminderType, ct);

    /// <summary>
    /// The same lookup, as a static over any <see cref="CommunityHubDbContext"/>.
    /// </summary>
    /// <remarks>
    /// 🔒 A builder must be able to read the ledger even when no cadence SERVICE was injected — the
    /// service is optional (older test constructions pass none), but the "have I already sent this?"
    /// question is not. Making the lookup depend on DI meant a builder without the service had an
    /// EMPTY history and therefore considered every recipient due EVERY DAY. Caught by five existing
    /// cadence tests, which is exactly what they are for.
    /// </remarks>
    public static async Task<IReadOnlyDictionary<string, DateOnly>> LastSentByOccasionAsync(
        CommunityHubDbContext db, int eventId, string reminderType, CancellationToken ct = default)
    {
        var rows = await db.SentReminders.AsNoTracking()
            .Where(s => s.EventId == eventId && s.ReminderType == reminderType)
            .Select(s => new { s.OccasionKey, s.SentAt })
            .ToListAsync(ct);

        var byRoot = new Dictionary<string, DateOnly>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            var key = r.OccasionKey ?? string.Empty;
            var cut = key.LastIndexOf(':');
            // Pre-§707.11 rows end in `:wk3` / `:due` rather than a date. Trimming the last segment
            // still yields the right root for those, so historic sends keep counting — which is what
            // stops the very first run after the deploy re-chasing everyone at once.
            var root = cut > 0 ? key[..cut] : key;
            var when = DateOnly.FromDateTime(r.SentAt.UtcDateTime);
            if (!byRoot.TryGetValue(root, out var existing) || when > existing)
            {
                byRoot[root] = when;
            }
        }
        return byRoot;
    }

    /// <summary>
    /// 🔑 THE RULE, in one place: is this person due a repeat today?
    /// </summary>
    /// <param name="today">Today (UTC date).</param>
    /// <param name="anchor">
    /// The date the goal became chaseable — the welcome for a wizard/task chaser, the invite for the
    /// Master Class selection. The FIRST send waits a full interval from here (§232).
    /// </param>
    /// <param name="lastSent">The last time this occasion was chased, or null if never.</param>
    /// <param name="intervalDays">Days between repeats; null ⇒ once, ever.</param>
    /// <param name="firstSendAtAnchor">
    /// 🔒 The two anchors mean opposite things, and conflating them breaks one of the two rules.
    /// <c>false</c> (default) — the anchor is a WELCOME, and the first chase waits a full interval
    /// after it (§232: never chase someone about a task in the same breath as welcoming them).
    /// <c>true</c> — the anchor is the DUE DATE, so the first mail fires ON it (§81) and only the
    /// REPEATS are spaced.
    /// </param>
    public static bool IsDue(
        DateOnly today, DateOnly anchor, DateOnly? lastSent, int? intervalDays,
        bool firstSendAtAnchor = false)
    {
        // Never repeats: due only if it has never been sent AND the anchor has matured.
        if (intervalDays is not int every || every <= 0)
        {
            return lastSent is null && today.DayNumber >= anchor.DayNumber;
        }

        if (lastSent is DateOnly sent)
        {
            return today.DayNumber - sent.DayNumber >= every;
        }

        return firstSendAtAnchor
            ? today.DayNumber >= anchor.DayNumber
            : today.DayNumber - anchor.DayNumber >= every;
    }
}
