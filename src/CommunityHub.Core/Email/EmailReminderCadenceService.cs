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
    /// The interval in force for this mail and (optionally) this RECIPIENT'S ROLE:
    /// <c>per-role ?? all-roles ?? shipped default</c>. <c>null</c> ⇒ once, ever.
    /// </summary>
    /// <param name="role">
    /// §881 — the role of the person about to be chased. Pass it whenever it is known: a shared
    /// template can carry a different cadence per role, and omitting it silently resolves the
    /// all-roles value, which is the §874.2 shape (a per-role value stored and never read).
    /// </param>
    public async Task<int?> GetIntervalDaysAsync(
        int eventId, string templateKey, ParticipantRole? role = null, CancellationToken ct = default)
    {
        var rows = await _db.EmailReminderCadences.AsNoTracking()
            .Where(c => c.EventId == eventId && c.TemplateKey == templateKey
                        && (c.Role == null || c.Role == role))
            .Select(c => new { c.Role, c.IntervalDays })
            .ToListAsync(ct);

        // Per-role wins; the all-roles row is the fallback; the catalog default is the floor.
        var own = role is null ? null : rows.FirstOrDefault(r => r.Role == role);
        if (own is not null) return own.IntervalDays;

        var all = rows.FirstOrDefault(r => r.Role is null);
        return all is not null
            ? all.IntervalDays
            : EmailTemplateCatalog.DefaultIntervalDaysFor(templateKey);
    }

    /// <summary>
    /// §881 — every cadence row for one mail: the all-roles value plus each role's own. ONE query
    /// for a builder that then chases people of several roles.
    /// </summary>
    /// <remarks>
    /// <para>🔒 A builder must not issue a lookup per recipient, and it must not read a single
    /// interval before the loop either — the latter is precisely the bug this feature fixes, one
    /// cadence applied to everyone. It reads this map once and resolves per person.</para>
    ///
    /// <para>⚠️ <b>Not a <c>Dictionary&lt;ParticipantRole?, …&gt;</c>.</b> That was the first shape
    /// and it throws <c>ArgumentNullException</c> the moment an all-roles row exists, because a
    /// dictionary key may not be null — i.e. it would have failed on every edition that had ever
    /// used this control. A record states the two cases instead of smuggling one in as a null key.</para>
    /// </remarks>
    public sealed record CadenceMap(
        bool HasAllRoles,
        int? AllRoles,
        IReadOnlyDictionary<ParticipantRole, int?> PerRole);

    /// <inheritdoc cref="CadenceMap"/>
    public async Task<CadenceMap> GetIntervalMapAsync(
        int eventId, string templateKey, CancellationToken ct = default)
    {
        var rows = await _db.EmailReminderCadences.AsNoTracking()
            .Where(c => c.EventId == eventId && c.TemplateKey == templateKey)
            .Select(c => new { c.Role, c.IntervalDays })
            .ToListAsync(ct);

        var all = rows.FirstOrDefault(r => r.Role is null);
        return new CadenceMap(
            all is not null,
            all?.IntervalDays,
            rows.Where(r => r.Role is not null)
                .ToDictionary(r => r.Role!.Value, r => r.IntervalDays));
    }

    /// <summary>
    /// §881 — resolve one person's interval from a map read by <see cref="GetIntervalMapAsync"/>:
    /// <c>per-role ?? all-roles ?? shipped default</c>. Pure, so the rule is testable on its own and
    /// stated exactly once.
    /// </summary>
    /// <remarks>
    /// 🔑 A stored value of <c>null</c> is a real setting — "this role hears it once, ever" — not an
    /// absent one. Presence, never the value, decides whether to fall through to the next level; the
    /// alternative would make "once only" impossible to express for a single role.
    /// </remarks>
    public static int? Resolve(CadenceMap map, string templateKey, ParticipantRole? role)
    {
        if (role is not null && map.PerRole.TryGetValue(role.Value, out var own)) return own;
        if (map.HasAllRoles) return map.AllRoles;
        return EmailTemplateCatalog.DefaultIntervalDaysFor(templateKey);
    }

    /// <summary>
    /// §881 — every PER-ROLE cadence row for the edition, keyed by (template, role), for the
    /// Settings page. A missing entry means that role follows the all-roles value.
    /// </summary>
    public async Task<IReadOnlyDictionary<(string TemplateKey, ParticipantRole Role), int?>>
        GetPerRoleAsync(int eventId, CancellationToken ct = default)
    {
        var rows = await _db.EmailReminderCadences.AsNoTracking()
            .Where(c => c.EventId == eventId && c.Role != null)
            .Select(c => new { c.TemplateKey, c.Role, c.IntervalDays })
            .ToListAsync(ct);

        return rows.ToDictionary(r => (r.TemplateKey, r.Role!.Value), r => r.IntervalDays);
    }

    /// <summary>
    /// §881 — drop a ROLE's own cadence so it follows the all-roles value again. The cadence
    /// equivalent of the ring picker's "— follow the all-roles ring —", and it must exist: without
    /// it a per-role number could be set but never un-set, and 0 already means "send once, ever".
    /// </summary>
    public async Task<bool> ClearIntervalAsync(
        int eventId, string templateKey, ParticipantRole role, CancellationToken ct = default)
    {
        var row = await _db.EmailReminderCadences
            .FirstOrDefaultAsync(
                c => c.EventId == eventId && c.TemplateKey == templateKey && c.Role == role, ct);
        if (row is null) return false;

        _db.EmailReminderCadences.Remove(row);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Every recurring mail with its ALL-ROLES cadence, for the Settings page. Per-role values are
    /// read separately (<see cref="GetIntervalMapAsync"/>) so this row keeps meaning exactly what it
    /// says: the value a role follows when it has none of its own.
    /// </summary>
    public async Task<IReadOnlyList<EmailReminderCadenceState>> GetAllAsync(
        int eventId, CancellationToken ct = default)
    {
        var rows = await _db.EmailReminderCadences.AsNoTracking()
            .Where(c => c.EventId == eventId && c.Role == null)
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
    /// <param name="role">
    /// §881 — null sets the ALL-ROLES value (the one every role without its own row follows); a role
    /// sets that role alone and leaves the others exactly where they are.
    /// </param>
    public async Task<bool> SetIntervalAsync(
        int eventId, string templateKey, int? intervalDays, string? byEmail,
        ParticipantRole? role = null, CancellationToken ct = default)
    {
        if (!EmailTemplateCatalog.IsRecurring(templateKey)) return false;
        if (intervalDays is <= 0) return false;
        // 🔒 A per-role cadence for a role this mail never reaches would be a control governing
        // nothing (§326bx) — and, worse, a stored number the operator would believe in.
        if (role is not null
            && !EmailTemplateCatalog.RecipientRolesFor(templateKey).Contains(role.Value))
        {
            return false;
        }

        var row = await _db.EmailReminderCadences
            .FirstOrDefaultAsync(
                c => c.EventId == eventId && c.TemplateKey == templateKey && c.Role == role, ct);

        if (row is null)
        {
            row = new EmailReminderCadence
            {
                EventId = eventId, TemplateKey = templateKey, Role = role,
            };
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
