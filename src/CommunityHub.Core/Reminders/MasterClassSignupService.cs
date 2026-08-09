using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Reminders;

/// <summary>
/// CEH-owned Master Class signup + waitlist (REQUIREMENTS §6) — replaces the Zoho
/// Bookings flow. Rules: eligibility = a 2-day Backstage ticket; a person holds
/// <b>at most one confirmed seat</b> AND may wait-list for <b>at most one other</b>
/// MC; each MC has a <see cref="Session.MasterClassCapacity"/>. When a seat frees
/// (a give-up / removal) the first waitlisted attendee is promoted <b>instantly</b>
/// (event-driven, not a timer): if they have no other seat they are confirmed; if
/// they already hold a seat elsewhere the freed seat is <b>offered</b> (held 3h) and
/// they must decide — keep their current seat or give it up to switch. Undecided
/// offers expire (lazily on the next interaction + a backstop job) and pass on.
/// </summary>
public sealed class MasterClassSignupService
{
    private readonly CommunityHubDbContext _db;
    public MasterClassSignupService(CommunityHubDbContext db) => _db = db;

    /// <summary>Default offer-hold when no per-edition setting is stored.</summary>
    public const int DefaultOfferHoldHours = 12;

    public enum PromotionKind { Confirmed, Offered }

    /// <summary>Per-edition settings (offer-hold hours + promotion mode), with defaults.</summary>
    public async Task<MasterClassSettings> GetSettingsAsync(int eventId, CancellationToken ct = default)
    {
        var row = await _db.MasterClassSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.EventId == eventId, ct);
        return row ?? new MasterClassSettings { EventId = eventId };
    }

    /// <summary>Organizer: save the offer-hold hours + promotion mode for an edition (upsert).</summary>
    public async Task SaveSettingsAsync(
        int eventId, int offerHoldHours, MasterClassPromotionMode mode, string? byEmail,
        CancellationToken ct = default)
    {
        var row = await _db.MasterClassSettings.FirstOrDefaultAsync(s => s.EventId == eventId, ct);
        if (row is null) { row = new MasterClassSettings { EventId = eventId }; _db.MasterClassSettings.Add(row); }
        row.OfferHoldHours = offerHoldHours is > 0 and <= 720 ? offerHoldHours : DefaultOfferHoldHours;
        row.PromotionMode = mode;
        row.UpdatedByEmail = byEmail;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Public availability traffic-light for a master class.</summary>
    public enum AvailabilityLevel { Available, FillingUp, Full }

    public sealed record McOption(
        int SessionId, string Title, int? Capacity, int Confirmed, int Offered, int Waitlisted,
        string? Abstract = null, IReadOnlyList<string>? Speakers = null)
    {
        /// <summary>Speaker display names (never null), for the "presented by" line.</summary>
        public IReadOnlyList<string> SpeakerNames => Speakers ?? Array.Empty<string>();
        /// <summary>Confirmed + Offered both occupy a seat.</summary>
        public int Taken => Confirmed + Offered;
        public bool IsFull => Capacity is int c && Taken >= c;
        /// <summary>Free seats (null = no capacity configured / unlimited).</summary>
        public int? Free => Capacity is int c ? Math.Max(0, c - Taken) : (int?)null;

        /// <summary>
        /// Traffic light: <b>Full</b> (red) when no seat left; <b>FillingUp</b> (yellow)
        /// when fewer than 20% of seats remain; otherwise <b>Available</b> (green). An
        /// uncapped class is always Available.
        /// </summary>
        public AvailabilityLevel Availability =>
            Capacity is not int cap || cap <= 0 ? AvailabilityLevel.Available
            : Taken >= cap ? AvailabilityLevel.Full
            : (double)(cap - Taken) / cap < 0.20 ? AvailabilityLevel.FillingUp
            : AvailabilityLevel.Available;
    }

    /// <param name="AutoSwitchConsentAt">
    /// §384c — when the attendee consented to releasing a held seat if this waitlist place turns
    /// into a real one. Surfaced so the wizard step can render the consent checkbox in the state
    /// the attendee last left it: without it, walking the wizard a second time showed the box
    /// unticked even though consent was on record, which reads as "my answer was lost".
    /// Optional trailing parameter so existing constructions are unaffected.
    /// </param>
    public sealed record MySignup(
        int SessionId, string Title, MasterClassSignupStatus Status,
        int? WaitlistPosition, DateTimeOffset? OfferExpiresAt, bool WantsMonthReminder = false,
        DateTimeOffset? AutoSwitchConsentAt = null);

    public sealed record SignupResult(bool Ok, string? Error, MySignup? Signup);

    /// <summary>A promotion produced by a freed seat — who to notify + how.</summary>
    /// <param name="ReleasedTitle">
    /// §386 — the class whose seat was AUTO-RELEASED to take this promotion, when the attendee held
    /// one elsewhere. Null when nothing was given up. Carried so the promotion mail can say what was
    /// traded: the release happened silently, so the attendee learned they had lost a seat only by
    /// noticing it gone (operator 2026-07-26: <i>"if i move up and get a requested/waitlist i should
    /// be informed that i received it and the old was cancelled"</i>).
    /// </param>
    public sealed record PromotionResult(
        int SessionId, int? PromotedSignupId, int? PromotedAttendeeId, PromotionKind? Kind,
        string? ReleasedTitle = null);

    // --- selection-invite tracking ------------------------------------------

    /// <summary>(eligible 2-day attendees, how many invited, how many not yet invited).</summary>
    public async Task<(int Eligible, int Invited, int NotInvited)> InviteStatsAsync(
        int eventId, CancellationToken ct = default)
    {
        var rows = await _db.Attendees.AsNoTracking()
            .Where(a => a.EventId == eventId && a.TicketStatus == TicketStatus.TwoDay
                        && a.MirrorState == MirrorState.Active)
            .Select(a => a.MasterClassInviteSentAt).ToListAsync(ct);
        var invited = rows.Count(x => x != null);
        return (rows.Count, invited, rows.Count - invited);
    }

    /// <summary>2-day-ticket attendees not yet sent the selection invite.</summary>
    public Task<List<int>> EligibleNotInvitedIdsAsync(int eventId, CancellationToken ct = default) =>
        _db.Attendees.AsNoTracking()
            .Where(a => a.EventId == eventId && a.TicketStatus == TicketStatus.TwoDay
                        && a.MirrorState == MirrorState.Active
                        && a.MasterClassInviteSentAt == null)
            .Select(a => a.Id).ToListAsync(ct);

    // --- tokens / resolution -------------------------------------------------

    public Task<Attendee?> ResolveByTokenAsync(string? token, CancellationToken ct = default) =>
        string.IsNullOrWhiteSpace(token)
            ? Task.FromResult<Attendee?>(null)
            : _db.Attendees.FirstOrDefaultAsync(a => a.SelfServiceToken == token, ct);

    public Task<Attendee?> ResolveByEmailAsync(int eventId, string? email, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email)) return Task.FromResult<Attendee?>(null);
        var norm = email.Trim().ToLowerInvariant();
        // §234 5: one email may legitimately hold SEVERAL tickets (the (EventId, Email)
        // index is non-unique — Zoho allows it). Resolve deterministically to the row
        // that actually carries the person's entitlement: an ACTIVE mirror row first,
        // then a 2-day (Master-Class) ticket over other classes, then the oldest row.
        return _db.Attendees
            .Where(a => a.EventId == eventId && a.Email == norm)
            .OrderByDescending(a => a.MirrorState == MirrorState.Active)
            .ThenByDescending(a => a.TicketStatus == TicketStatus.TwoDay)
            .ThenBy(a => a.Id)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<string?> EnsureSelfServiceTokenAsync(int attendeeId, CancellationToken ct = default)
    {
        var a = await _db.Attendees.FindAsync(new object?[] { attendeeId }, ct);
        if (a is null) return null;
        if (string.IsNullOrWhiteSpace(a.SelfServiceToken))
        {
            a.SelfServiceToken = Convert.ToBase64String(
                System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
                .Replace('+', '-').Replace('/', '_').TrimEnd('=');
            await _db.SaveChangesAsync(ct);
        }
        return a.SelfServiceToken;
    }

    public sealed record IcsInfo(string Title, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, DateOnly EditionStart);

    /// <summary>Session + edition date info for building a master-class .ics, or null.</summary>
    public async Task<IcsInfo?> GetSessionForIcsAsync(int eventId, int sessionId, CancellationToken ct = default)
    {
        var row = await _db.Sessions.AsNoTracking()
            .Where(s => s.Id == sessionId && s.EventId == eventId)
            .Select(s => new { s.Title, s.StartsAt, s.EndsAt, s.Event.StartDate })
            .FirstOrDefaultAsync(ct);
        return row is null ? null : new IcsInfo(row.Title, row.StartsAt, row.EndsAt, row.StartDate);
    }

    /// <summary>
    /// Confirmed signups that opted into the ~1-month-before reminder, not yet sent,
    /// whose master class falls within the next <paramref name="windowDays"/> (and
    /// not in the past). The MC date is the session start, or the edition pre-day
    /// when the session has no time yet. Returns signup ids (for the reminder job).
    /// </summary>
    public async Task<IReadOnlyList<int>> DueMonthReminderSignupIdsAsync(
        DateTimeOffset now, int windowDays = 31, CancellationToken ct = default)
    {
        var rows = await _db.MasterClassSignups.AsNoTracking()
            .Where(x => x.Status == MasterClassSignupStatus.Confirmed
                        && x.WantsMonthBeforeReminder && x.MonthReminderSentAt == null)
            .Select(x => new { x.Id, x.Session.StartsAt, x.Session.Event.StartDate })
            .ToListAsync(ct);

        var due = new List<int>();
        foreach (var r in rows)
        {
            var date = r.StartsAt ?? new DateTimeOffset(r.StartDate.ToDateTime(new TimeOnly(0, 0)), TimeSpan.Zero);
            if (date >= now && date <= now.AddDays(windowDays)) due.Add(r.Id);
        }
        return due;
    }

    /// <summary>Stamp that the ~1-month-before reminder was sent.</summary>
    public async Task MarkMonthReminderSentAsync(int signupId, CancellationToken ct = default)
    {
        var s = await _db.MasterClassSignups.FindAsync(new object?[] { signupId }, ct);
        if (s is null) return;
        s.MonthReminderSentAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Set the ~1-month-before calendar reminder opt-in on the attendee's confirmed seat.</summary>
    public async Task SetMonthReminderOptInAsync(int eventId, int attendeeId, bool wants, CancellationToken ct = default)
    {
        var s = await _db.MasterClassSignups.FirstOrDefaultAsync(
            x => x.EventId == eventId && x.AttendeeId == attendeeId
                 && x.Status == MasterClassSignupStatus.Confirmed, ct);
        if (s is null) return;
        s.WantsMonthBeforeReminder = wants;
        s.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>The signup id for an (attendee, session), or null.</summary>
    public async Task<int?> SignupIdAsync(int eventId, int attendeeId, int sessionId, CancellationToken ct = default) =>
        (await _db.MasterClassSignups.AsNoTracking()
            .Where(x => x.EventId == eventId && x.AttendeeId == attendeeId && x.SessionId == sessionId)
            .Select(x => (int?)x.Id).FirstOrDefaultAsync(ct));

    /// <summary>The 8 ELDK27 master-class topics offered (operator list).</summary>
    public static readonly string[] DefaultMasterClassTitles =
    {
        "Intune", "Security", "Data Compliance & Security",
        "AI for Makers (Copilot & Agents)", "AI for Engineers/Developers (Build Your Own AI)",
        "Microsoft 365", "Identity", "Azure",
    };

    /// <summary>
    /// Create the standard master-class sessions for an edition (idempotent — skips
    /// any title that already exists). Hub-added; capacity left unset for the
    /// organizer to fill. Returns how many were created.
    /// </summary>
    public async Task<int> SeedDefaultMasterClassesAsync(int eventId, CancellationToken ct = default)
    {
        var existing = await _db.Sessions.AsNoTracking()
            .Where(s => s.EventId == eventId && s.Type == SessionType.MasterClass)
            .Select(s => s.Title).ToListAsync(ct);
        var have = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);

        var now = DateTimeOffset.UtcNow;
        var added = 0;
        foreach (var title in DefaultMasterClassTitles)
        {
            if (have.Contains(title)) continue;
            _db.Sessions.Add(new Session
            {
                EventId = eventId, Title = title,
                // Synthetic hub id (hub-<guid>) so each seeded MC is UNIQUE on the
                // (EventId, SessionizeId) unique index (an empty default collides on
                // the 2nd seed) and the Sessionize import never matches/deletes it.
                SessionizeId = $"hub-{Guid.NewGuid():D}",
                Type = SessionType.MasterClass, IsHubAdded = true, CreatedAt = now,
            });
            added++;
        }
        if (added > 0) await _db.SaveChangesAsync(ct);
        return added;
    }

    /// <summary>The edition's display name (for headings), or empty.</summary>
    public async Task<string> EventNameAsync(int eventId, CancellationToken ct = default) =>
        (await _db.Events.AsNoTracking().Where(e => e.Id == eventId)
            .Select(e => e.DisplayName).FirstOrDefaultAsync(ct)) ?? string.Empty;

    public Task<bool> IsEligibleAsync(int eventId, int attendeeId, CancellationToken ct = default) =>
        _db.Attendees.AsNoTracking().AnyAsync(
            a => a.Id == attendeeId && a.EventId == eventId
                 && a.TicketStatus == TicketStatus.TwoDay
                 // Soft-cancelled holders are NOT eligible (§128): cancellation rides
                 // MirrorState, so the 2-day filter is applied over the ACTIVE set only.
                 && a.MirrorState == MirrorState.Active, ct);

    /// <summary>
    /// Set (or clear) a master class's seat capacity. Per §93/§94, raising the cap (or
    /// making it unlimited) OPENS seats that belong to the waitlist FIRST — so every
    /// newly-opened seat is consumed by the waitlist (highest first, chaining the
    /// auto-switch cascade) before the public can ever see it. Runs in a transaction and
    /// each promotion claims its seat atomically (§218 <see cref="TryClaimSeatAsync"/>) so
    /// a concurrent public booker can't grab an opened seat. Returns the promotions
    /// produced (so the caller can notify each moved attendee).
    /// </summary>
    public async Task<IReadOnlyList<PromotionResult>> SetCapacityAsync(
        int eventId, int sessionId, int? capacity, CancellationToken ct = default)
    {
        return await InTxAsync<IReadOnlyList<PromotionResult>>(async () =>
        {
            var mc = await _db.Sessions.FirstOrDefaultAsync(
                s => s.Id == sessionId && s.EventId == eventId && s.Type == SessionType.MasterClass, ct);
            if (mc is null) return Array.Empty<PromotionResult>();

            // §326ba: 0 (or negative) is a MISTAKE, never a request for "unlimited".
            // The old expression `capacity is > 0 ? capacity : null` collapsed 0 into NULL,
            // and NULL is the one branch of the §218 seat claim that ALWAYS succeeds
            // (`MasterClassCapacity IS NULL` ⇒ every claim wins). So a mistyped 0 silently
            // UNCAPPED the room, and the promotion loop below then drained the entire
            // waitlist into it and mailed every one of them — from one form post, with no
            // confirmation. Unlimited is still expressible, but only by CLEARING the field
            // (null), which the page now makes you confirm.
            if (capacity is <= 0) return Array.Empty<PromotionResult>();

            mc.MasterClassCapacity = capacity;
            mc.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);

            // Fill every seat opened by the new cap from the waitlist (one promotion per
            // free seat; each promotion may cascade into another class — see §93).
            var promotions = new List<PromotionResult>();
            while (true)
            {
                var r = await PromoteNextAsync(eventId, sessionId, promotions, ct);
                if (r?.PromotedSignupId is null) break;   // no free seat OR empty waitlist
            }
            return (IReadOnlyList<PromotionResult>)promotions;
        }, ct);
    }

    // --- reads ---------------------------------------------------------------

    /// <param name="excludeTestSessions">
    /// §972 — drop Master Classes flagged <see cref="Session.UsedForTesting"/>. The attendee
    /// SELECTION and WAITLIST surfaces pass <c>true</c>.
    /// </param>
    /// <remarks>
    /// <para>🔴 <b>§972 (operator 2026-08-09): <i>"i would like to not show my test master class
    /// anymore in prod"</i>.</b> <see cref="Session.UsedForTesting"/> already promised this — §299
    /// 4.5/b8 states a flagged session <i>"never appears on any PUBLIC page"</i> and is hub-visible
    /// only to ring 0/1 — and the Backstage push honours it in three places. <b>This query never
    /// did.</b> It filtered <c>!IsServiceSession</c> and nothing else, so "Test Master Class" was
    /// offered on the attendee selection step and the waitlist. The flag existed; one of its two
    /// stated guarantees was simply unimplemented here.</para>
    ///
    /// <para>🔒 <b>Defaults to FALSE so no existing caller changes behaviour</b> (operator: <i>"no
    /// changing of default behavior"</i>). The organizer Master Class page keeps seeing it — hiding a
    /// row from the screen where its own flag is set and cleared is how a record becomes
    /// unreachable — and every other caller is untouched until someone decides otherwise.</para>
    ///
    /// <para>⚠️ Keyed on <see cref="Session.UsedForTesting"/> ALONE, never
    /// <see cref="Session.IsTestData"/>. IsTestData (§909) means "never announced on social media" —
    /// a campaign property. Borrowing it for visibility would hide every session kept out of the SoMe
    /// queue, a different and much larger set.</para>
    /// </remarks>
    public async Task<IReadOnlyList<McOption>> ListMasterClassesAsync(
        int eventId, CancellationToken ct = default, bool excludeTestSessions = false)
    {
        await ExpireOffersAsync(DateTimeOffset.UtcNow, eventId, ct);
        var mcs = await _db.Sessions.AsNoTracking()
            .Where(s => s.EventId == eventId && s.Type == SessionType.MasterClass && !s.IsServiceSession
                        && (!excludeTestSessions || !s.UsedForTesting))
            .Select(s => new
            {
                s.Id, s.Title, s.MasterClassCapacity, s.Abstract,
                Speakers = s.SessionSpeakers
                    .Where(ss => ss.Participant != null && ss.Participant.FullName != null)
                    .Select(ss => ss.Participant!.FullName!)
                    .ToList()
            }).ToListAsync(ct);

        var counts = await _db.MasterClassSignups.AsNoTracking()
            .Where(x => x.EventId == eventId)
            .GroupBy(x => new { x.SessionId, x.Status })
            .Select(g => new { g.Key.SessionId, g.Key.Status, Count = g.Count() }).ToListAsync(ct);

        int C(int sid, MasterClassSignupStatus st) =>
            counts.Where(c => c.SessionId == sid && c.Status == st).Sum(c => c.Count);

        return mcs.Select(m => new McOption(m.Id, m.Title, m.MasterClassCapacity,
            C(m.Id, MasterClassSignupStatus.Confirmed),
            C(m.Id, MasterClassSignupStatus.Offered),
            C(m.Id, MasterClassSignupStatus.Waitlisted),
            m.Abstract,
            m.Speakers.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()))
            .OrderBy(m => m.Title).ToList();
    }

    /// <summary>
    /// The active edition's master classes with live availability — for the public
    /// landing page. Returns the edition display name (null when no active edition).
    /// </summary>
    public async Task<(string? EventName, IReadOnlyList<McOption> MasterClasses)> ListActiveAsync(
        CancellationToken ct = default)
    {
        var ev = await _db.Events.AsNoTracking()
            .Where(e => e.IsActive).Select(e => new { e.Id, e.DisplayName })
            .FirstOrDefaultAsync(ct);
        if (ev is null) return (null, Array.Empty<McOption>());
        return (ev.DisplayName, await ListMasterClassesAsync(ev.Id, ct));
    }

    /// <summary>The attendee's signups (0–2: a confirmed seat and/or a waitlist/offer).</summary>
    public async Task<IReadOnlyList<MySignup>> GetForAttendeeAsync(
        int eventId, int attendeeId, CancellationToken ct = default)
    {
        await ExpireOffersAsync(DateTimeOffset.UtcNow, eventId, ct);
        var rows = await _db.MasterClassSignups.AsNoTracking()
            .Where(x => x.EventId == eventId && x.AttendeeId == attendeeId)
            .Select(x => new { x.SessionId, x.Status, x.CreatedAt, x.OfferExpiresAt, x.WantsMonthBeforeReminder, x.AutoSwitchConsentAt, Title = x.Session.Title })
            .ToListAsync(ct);

        var outList = new List<MySignup>();
        foreach (var r in rows)
        {
            int? pos = null;
            if (r.Status == MasterClassSignupStatus.Waitlisted)
            {
                // 1-based FIFO position. The `CreatedAt <=` comparison is client-side (the
                // EF SQLite provider can't compare a DateTimeOffset; SQL Server can) over
                // the bounded per-class waitlist.
                var times = await _db.MasterClassSignups.AsNoTracking()
                    .Where(x => x.EventId == eventId && x.SessionId == r.SessionId
                                && x.Status == MasterClassSignupStatus.Waitlisted)
                    .Select(x => x.CreatedAt).ToListAsync(ct);
                pos = times.Count(t => t <= r.CreatedAt);
            }
            outList.Add(new MySignup(
                r.SessionId, r.Title, r.Status, pos, r.OfferExpiresAt, r.WantsMonthBeforeReminder,
                r.AutoSwitchConsentAt));
        }
        return outList;
    }

    public sealed record RosterRow(int AttendeeId, string Name, string Email, DateTimeOffset SignedUpAt);
    private sealed record RosterEntry(
        MasterClassSignupStatus Status, int AttendeeId, string Name, string Email, DateTimeOffset CreatedAt);

    /// <summary>Organizer roster: confirmed seats, held offers, and the ordered waitlist.</summary>
    public async Task<(IReadOnlyList<RosterRow> Seated, IReadOnlyList<RosterRow> Offered, IReadOnlyList<RosterRow> Waitlist)>
        GetRosterAsync(int eventId, int sessionId, CancellationToken ct = default)
    {
        await ExpireOffersAsync(DateTimeOffset.UtcNow, eventId, ct);
        // CreatedAt ordering is client-side (the EF SQLite provider can't ORDER BY a
        // DateTimeOffset; SQL Server can) over one class's signups.
        var rows = (await _db.MasterClassSignups.AsNoTracking()
            .Where(x => x.EventId == eventId && x.SessionId == sessionId)
            .Select(x => new RosterEntry(x.Status, x.AttendeeId,
                ((x.Attendee.FirstName ?? "") + " " + (x.Attendee.LastName ?? "")).Trim(),
                x.Attendee.Email, x.CreatedAt))
            .ToListAsync(ct))
            .OrderBy(r => r.CreatedAt)
            .ToList();

        IReadOnlyList<RosterRow> Pick(MasterClassSignupStatus st) =>
            rows.Where(r => r.Status == st)
                .Select(r => new RosterRow(r.AttendeeId, r.Name, r.Email, r.CreatedAt)).ToList();
        return (Pick(MasterClassSignupStatus.Confirmed),
                Pick(MasterClassSignupStatus.Offered),
                Pick(MasterClassSignupStatus.Waitlisted));
    }

    // --- writes --------------------------------------------------------------

    public async Task<SignupResult> SignUpAsync(
        int eventId, int attendeeId, int sessionId, bool autoSwitchConsent = false,
        CancellationToken ct = default)
    {
        if (!await IsEligibleAsync(eventId, attendeeId, ct))
            return new SignupResult(false, "A 2-day ticket is required to book a Master Class.", null);

        var mc = await _db.Sessions.FirstOrDefaultAsync(
            s => s.Id == sessionId && s.EventId == eventId
                 && s.Type == SessionType.MasterClass && !s.IsServiceSession, ct);
        if (mc is null) return new SignupResult(false, "That Master Class was not found.", null);

        await ExpireOffersAsync(DateTimeOffset.UtcNow, eventId, ct);

        // §218 OPTIMISTIC seat decision: the seat grant is a single atomic conditional
        // write (TryClaimSeatAsync) that detects "full" at commit under a brief one-row
        // lock — no serializable range locks, no SELECT-then-INSERT window. The short
        // transaction keeps the claim + the matching signup insert atomic.
        var (ok, error) = await InTxAsync<(bool Ok, string? Error)>(async () =>
        {
            var mine = await _db.MasterClassSignups
                .Where(x => x.EventId == eventId && x.AttendeeId == attendeeId).ToListAsync(ct);
            if (mine.Any(x => x.SessionId == sessionId))
                return (true, null);   // already signed up for this exact class → idempotent OK

            var hasConfirmed = mine.Any(x => x.Status == MasterClassSignupStatus.Confirmed);
            var hasWaitOrOffer = mine.Any(x => x.Status is MasterClassSignupStatus.Waitlisted or MasterClassSignupStatus.Offered);
            var now = DateTimeOffset.UtcNow;

            if (hasConfirmed)
            {
                // Already hold a confirmed seat → may NEVER take a second confirmed seat.
                // If the target currently has room (free seat + no waitlist, §94) the rule
                // is "give up your current seat first"; otherwise they may join the
                // waitlist (auto-switch). This room check is read-only (message-only) — no
                // seat is ever granted to a confirmed holder, so it needs no atomicity.
                if (await HasPublicRoomAsync(eventId, sessionId, ct))
                    return (false,
                        "You already have a confirmed Master Class. Give it up first to take another.");

                if (hasWaitOrOffer)
                    return (false,
                        "You can wait-list for one Master Class at a time. Leave your current waitlist first.");
                // Consent gate (§63/§93): on promotion their current MC is AUTO-CANCELLED
                // and they're moved here automatically — they must accept that.
                if (!autoSwitchConsent)
                    return (false,
                        "Please accept that joining this waitlist will cancel your current Master Class and move you here automatically.");

                _db.MasterClassSignups.Add(new MasterClassSignup
                {
                    EventId = eventId, SessionId = sessionId, AttendeeId = attendeeId,
                    Status = MasterClassSignupStatus.Waitlisted, CreatedAt = now, UpdatedAt = now,
                    AutoSwitchConsentAt = now,
                });
                await _db.SaveChangesAsync(ct);
                return (true, null);
            }

            // No confirmed seat yet → try to atomically grab one (§94: only when a seat is
            // free AND nobody is waitlisted). 1 row ⇒ seat granted; 0 rows ⇒ full/waitlist.
            if (await TryClaimSeatAsync(eventId, sessionId, blockWhenWaitlisted: true, ct))
            {
                // §234 RE-VALIDATE UNDER THE LOCK: `mine` was read BEFORE the claim, so a
                // concurrent §93 promotion may have JUST confirmed this attendee elsewhere
                // (their waitlisted row in another class flips to Confirmed in an
                // independent transaction). Re-read the attendee's rows with a LOCKING read
                // (UPDLOCK on SQL Server — sees latest COMMITTED rows, immune to the RCSI
                // snapshot, and serializes against the promotion's row update) so signup +
                // promotion can never leave one person with TWO confirmed seats. Bailing
                // here is safe: the claim only touched Session.UpdatedAt — no signup row
                // was written, so no seat is consumed (the live COUNT is the truth).
                var fresh = await ReadAttendeeSignupsLockedAsync(eventId, attendeeId, ct);
                if (fresh.Any(x => x.SessionId == sessionId))
                    return (true, null);   // row appeared concurrently → idempotent OK
                if (fresh.Any(x => x.Status == MasterClassSignupStatus.Confirmed))
                    return (false,
                        "You already have a confirmed Master Class. Give it up first to take another.");

                _db.MasterClassSignups.Add(new MasterClassSignup
                {
                    EventId = eventId, SessionId = sessionId, AttendeeId = attendeeId,
                    Status = MasterClassSignupStatus.Confirmed,
                    CreatedAt = now, UpdatedAt = now, ConfirmedAt = now,
                });
                await _db.SaveChangesAsync(ct);
                return (true, null);
            }

            // Full (or a waitlist already exists) → join the waitlist.
            if (hasWaitOrOffer)
                return (false,
                    "You can wait-list for one Master Class at a time. Leave your current waitlist first.");
            _db.MasterClassSignups.Add(new MasterClassSignup
            {
                EventId = eventId, SessionId = sessionId, AttendeeId = attendeeId,
                Status = MasterClassSignupStatus.Waitlisted, CreatedAt = now, UpdatedAt = now,
            });
            await _db.SaveChangesAsync(ct);
            return (true, null);
        }, ct);

        if (!ok) return new SignupResult(false, error, null);
        return new SignupResult(true, null,
            (await GetForAttendeeAsync(eventId, attendeeId, ct)).FirstOrDefault(s => s.SessionId == sessionId));
    }

    /// <summary>
    /// The verbatim error shown when a switch/sign-up loses the race for the last seat —
    /// the target master class FILLED after the page rendered it as Available. The
    /// attendee keeps their existing seat (the switch is all-or-nothing).
    /// </summary>
    public const string NowFullError =
        "Sorry — this could not be completed: the Master Class is now full.";

    /// <summary>
    /// ATOMIC "switch to this Master Class" (§139). The attendee gives up their current
    /// confirmed seat and takes a seat in <paramref name="targetSessionId"/> as ONE unit:
    /// either both happen or neither does, so a failure can never leave them with no seat.
    /// <para>
    /// The whole read-decide-write runs inside the SERIALIZABLE transaction so the
    /// confirmed-count &lt; capacity guard is re-checked AT COMMIT TIME against range-locked
    /// rows. If the class filled during the session (e.g. a §93 promotion took the last
    /// seat after the page rendered it Available), the operation FAILS with
    /// <see cref="NowFullError"/> and the attendee's existing seat is left intact — the old
    /// seat is only released once the new seat is secured. Per §94 a target that has grown
    /// a waitlist is also refused (its seats belong to the waitlist first).
    /// </para>
    /// Releasing the old seat promotes that class's waitlist (§93), returned so the caller
    /// can notify whoever was moved in.
    /// </summary>
    public async Task<(bool Ok, string? Error, PromotionResult? FreedPromotion)> SwitchAsync(
        int eventId, int attendeeId, int targetSessionId, CancellationToken ct = default)
    {
        if (!await IsEligibleAsync(eventId, attendeeId, ct))
            return (false, "A 2-day ticket is required to book a Master Class.", null);

        var mc = await _db.Sessions.FirstOrDefaultAsync(
            s => s.Id == targetSessionId && s.EventId == eventId
                 && s.Type == SessionType.MasterClass && !s.IsServiceSession, ct);
        if (mc is null) return (false, "That Master Class was not found.", null);

        await ExpireOffersAsync(DateTimeOffset.UtcNow, eventId, ct);

        return await InTxAsync<(bool, string?, PromotionResult?)>(async () =>
        {
            var mine = await _db.MasterClassSignups
                .Where(x => x.EventId == eventId && x.AttendeeId == attendeeId).ToListAsync(ct);

            // Already confirmed here → nothing to do (idempotent).
            if (mine.Any(x => x.SessionId == targetSessionId
                              && x.Status == MasterClassSignupStatus.Confirmed))
                return (true, null, (PromotionResult?)null);

            // §218 OPTIMISTIC GUARD: atomically claim a seat in the target FIRST (a free
            // seat AND no waitlist, §94 priority) — a single conditional write that detects
            // "full" at commit. Done BEFORE we touch the old seat, so any failure leaves
            // the attendee's existing seat untouched (the switch is all-or-nothing).
            if (!await TryClaimSeatAsync(eventId, targetSessionId, blockWhenWaitlisted: true, ct))
            {
                // Distinguish the message: a grown waitlist (§94) vs a genuinely full room.
                // Read-only and best-effort — the claim above already failed safely.
                if (await WaitlistCountAsync(eventId, targetSessionId, ct) > 0)
                    return (false,
                        "Sorry — this could not be completed: this Master Class now has a waitlist, so its seats go to waitlisted attendees first. Your current seat was kept — join the waitlist instead.",
                        (PromotionResult?)null);
                return (false, NowFullError, (PromotionResult?)null);
            }

            var now = DateTimeOffset.UtcNow;
            // The attendee's existing confirmed seat (in another class), if any.
            var oldConfirmed = mine.FirstOrDefault(x => x.Status == MasterClassSignupStatus.Confirmed);
            // Any waitlist/offer the attendee holds for THIS target is superseded by taking the seat.
            var targetPending = mine.FirstOrDefault(
                x => x.SessionId == targetSessionId
                     && x.Status is MasterClassSignupStatus.Waitlisted or MasterClassSignupStatus.Offered);

            if (targetPending is not null) _db.MasterClassSignups.Remove(targetPending);

            // Secure the new confirmed seat FIRST.
            _db.MasterClassSignups.Add(new MasterClassSignup
            {
                EventId = eventId, SessionId = targetSessionId, AttendeeId = attendeeId,
                Status = MasterClassSignupStatus.Confirmed,
                CreatedAt = now, UpdatedAt = now, ConfirmedAt = now,
            });
            await _db.SaveChangesAsync(ct);

            // Only AFTER the new seat is secured do we release the old one (+ promote its waitlist).
            PromotionResult? freed = null;
            if (oldConfirmed is not null)
            {
                var freedSession = oldConfirmed.SessionId;
                _db.MasterClassSignups.Remove(oldConfirmed);
                await _db.SaveChangesAsync(ct);
                var sink = new List<PromotionResult>();
                await PromoteNextAsync(eventId, freedSession, sink, ct);
                freed = sink.Count > 0 ? sink[0] : null;
            }
            return (true, null, freed);
        }, ct);
    }

    /// <summary>
    /// Remove the attendee's entry for ONE master class (give up a seat, leave a
    /// waitlist, or drop a held offer). Freeing a seat (Confirmed/Offered) instantly
    /// promotes the next waitlisted attendee — the returned <see cref="PromotionResult"/>
    /// says who to notify and whether they were confirmed or offered.
    /// </summary>
    public async Task<PromotionResult?> RemoveAsync(
        int eventId, int attendeeId, int sessionId, CancellationToken ct = default)
    {
        // Remove + (if a seat was freed) promote the waitlist atomically, so a public
        // booker can't slip into the seat between the give-up and the promotion (§93/§94).
        return await InTxAsync<PromotionResult?>(async () =>
        {
            var mine = await _db.MasterClassSignups.FirstOrDefaultAsync(
                x => x.EventId == eventId && x.AttendeeId == attendeeId && x.SessionId == sessionId, ct);
            if (mine is null) return null;

            var freedSeat = mine.Status is MasterClassSignupStatus.Confirmed or MasterClassSignupStatus.Offered;
            _db.MasterClassSignups.Remove(mine);
            await _db.SaveChangesAsync(ct);

            if (!freedSeat) return new PromotionResult(sessionId, null, null, null);

            var sink = new List<PromotionResult>();
            await PromoteNextAsync(eventId, sessionId, sink, ct);
            // Return the DIRECT promotion into the just-freed class (sink[0]); any cascade
            // promotions into other classes are in sink[1..] (see note in PromoteNextAsync).
            return sink.Count > 0 ? sink[0] : new PromotionResult(sessionId, null, null, null);
        }, ct);
    }

    /// <summary>
    /// Accept a held offer: the attendee gives up their current confirmed seat (per
    /// the rule — never two confirmed) and the offered seat becomes confirmed. Giving
    /// up the old seat frees it, so its waitlist is promoted too (returned so the
    /// caller can notify that person).
    /// </summary>
    public async Task<(bool Ok, string? Error, PromotionResult? FreedPromotion)> AcceptOfferAsync(
        int eventId, int attendeeId, CancellationToken ct = default)
    {
        await ExpireOffersAsync(DateTimeOffset.UtcNow, eventId, ct);
        // The accept (Offered→Confirmed) keeps the seat the offer already held — Offered
        // and Confirmed both count against capacity — so NO new claim is needed; only the
        // released OLD seat re-enters its waitlist, and that promotion claims atomically.
        // The whole free+promote+flip runs in one transaction so it can't race a booker.
        return await InTxAsync<(bool Ok, string? Error, PromotionResult? FreedPromotion)>(async () =>
        {
            var offered = await _db.MasterClassSignups.FirstOrDefaultAsync(
                x => x.EventId == eventId && x.AttendeeId == attendeeId
                     && x.Status == MasterClassSignupStatus.Offered, ct);
            if (offered is null) return (false, "You don't have a Master Class offer to accept.", null);

            var confirmed = await _db.MasterClassSignups.FirstOrDefaultAsync(
                x => x.EventId == eventId && x.AttendeeId == attendeeId
                     && x.Status == MasterClassSignupStatus.Confirmed, ct);

            PromotionResult? freed = null;
            if (confirmed is not null)
            {
                var freedSession = confirmed.SessionId;
                _db.MasterClassSignups.Remove(confirmed);
                await _db.SaveChangesAsync(ct);
                var sink = new List<PromotionResult>();
                await PromoteNextAsync(eventId, freedSession, sink, ct);
                freed = sink.Count > 0 ? sink[0] : null;
            }

            offered.Status = MasterClassSignupStatus.Confirmed;
            offered.ConfirmedAt = DateTimeOffset.UtcNow;
            offered.OfferExpiresAt = null;
            offered.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            return (true, null, freed);
        }, ct);
    }

    /// <summary>Decline a held offer: drop it (frees the held seat → promote next).</summary>
    public async Task<PromotionResult?> DeclineOfferAsync(
        int eventId, int attendeeId, CancellationToken ct = default)
    {
        // Drop + promote atomically so a public booker can't slip into the freed seat.
        return await InTxAsync<PromotionResult?>(async () =>
        {
            var offered = await _db.MasterClassSignups.FirstOrDefaultAsync(
                x => x.EventId == eventId && x.AttendeeId == attendeeId
                     && x.Status == MasterClassSignupStatus.Offered, ct);
            if (offered is null) return null;
            var sessionId = offered.SessionId;
            _db.MasterClassSignups.Remove(offered);
            await _db.SaveChangesAsync(ct);
            var sink = new List<PromotionResult>();
            await PromoteNextAsync(eventId, sessionId, sink, ct);
            return sink.Count > 0 ? sink[0] : new PromotionResult(sessionId, null, null, null);
        }, ct);
    }

    /// <summary>
    /// Expire held offers past their window (lazy housekeeping + the backstop job).
    /// Per the operator default, an undecided offer falls back to <b>option a —
    /// auto-switch</b>: the offered seat becomes confirmed and the attendee's previous
    /// seat is released (which promotes that MC's waitlist). Returns the promotions
    /// produced on the RELEASED seats (to notify those people).
    /// </summary>
    public async Task<IReadOnlyList<PromotionResult>> ExpireOffersAsync(
        DateTimeOffset now, int? eventId = null, CancellationToken ct = default)
    {
        // Apply the optional edition filter as a separate Where (an inline
        // `eventId == null || x.EventId == eventId` with a captured nullable does not
        // translate on relational providers — SQLite/SQL Server — only EF in-memory).
        var q = _db.MasterClassSignups
            .Where(x => x.Status == MasterClassSignupStatus.Offered && x.OfferExpiresAt != null);
        if (eventId is int evid) q = q.Where(x => x.EventId == evid);
        // The `<= now` DateTimeOffset comparison is evaluated client-side: SQL Server
        // translates it but the EF SQLite provider does not, and the candidate set (held
        // offers with an expiry) is tiny, so we materialize then filter. Result is
        // identical on every provider.
        var expired = (await q.ToListAsync(ct)).Where(x => x.OfferExpiresAt <= now).ToList();
        if (expired.Count == 0) return Array.Empty<PromotionResult>();

        // Flip each offer + release its old seat + promote the released class's waitlist
        // atomically (§218 PromoteNextAsync claims each freed seat) so a concurrent booker
        // can't take a seat freed here. Offered→Confirmed keeps the held seat (both count),
        // so the flip itself needs no claim.
        return await InTxAsync<IReadOnlyList<PromotionResult>>(async () =>
        {
            var results = new List<PromotionResult>();
            foreach (var o in expired)
            {
                // Auto-switch fallback: take the offered seat, release the old one.
                var old = await _db.MasterClassSignups.FirstOrDefaultAsync(
                    x => x.EventId == o.EventId && x.AttendeeId == o.AttendeeId
                         && x.Status == MasterClassSignupStatus.Confirmed, ct);
                o.Status = MasterClassSignupStatus.Confirmed;
                o.ConfirmedAt = now; o.OfferExpiresAt = null; o.UpdatedAt = now;
                await _db.SaveChangesAsync(ct);
                if (old is not null)
                {
                    var freedSession = old.SessionId;
                    _db.MasterClassSignups.Remove(old);
                    await _db.SaveChangesAsync(ct);
                    await PromoteNextAsync(o.EventId, freedSession, results, ct);
                }
            }
            return results;
        }, ct);
    }

    public async Task MarkPromotionNotifiedAsync(int signupId, CancellationToken ct = default)
    {
        var s = await _db.MasterClassSignups.FindAsync(new object?[] { signupId }, ct);
        if (s is null) return;
        s.PromotionNotifiedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    // --- internals -----------------------------------------------------------

    private Task<int> SeatsTakenAsync(int eventId, int sessionId, CancellationToken ct) =>
        _db.MasterClassSignups.CountAsync(
            x => x.EventId == eventId && x.SessionId == sessionId
                 && (x.Status == MasterClassSignupStatus.Confirmed
                     || x.Status == MasterClassSignupStatus.Offered), ct);

    private Task<int> WaitlistCountAsync(int eventId, int sessionId, CancellationToken ct) =>
        _db.MasterClassSignups.CountAsync(
            x => x.EventId == eventId && x.SessionId == sessionId
                 && x.Status == MasterClassSignupStatus.Waitlisted, ct);

    /// <summary>
    /// Run <paramref name="action"/> inside a database transaction (default READ
    /// COMMITTED isolation — <b>no longer SERIALIZABLE</b>, REQUIREMENTS §218). The
    /// optimistic <see cref="TryClaimSeatAsync"/> does the "is there room?" decision as a
    /// single atomic guarded UPDATE on the capacity-bearing Session row, so we no longer
    /// hold serializable range locks over the whole signup set. The transaction exists
    /// only to keep a multi-statement unit (claim + insert, or free + promote) atomic and
    /// to hold the brief single-row lock the claim takes until the matching signup write
    /// commits. Honours the configured retrying execution strategy (Azure SQL). On a
    /// NON-relational store (the EF in-memory test provider, which has no transactions) it
    /// simply runs the action — the ordering/logic invariants are still exercised.
    /// </summary>
    private async Task<T> InTxAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        if (!_db.Database.IsRelational())
            return await action();

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var result = await action();
            await tx.CommitAsync(ct);
            return result;
        });
    }

    /// <summary>
    /// OPTIMISTIC atomic seat claim (REQUIREMENTS §218) — detects "class full" AT COMMIT
    /// with NO global serializable lock. On a relational provider this is a single guarded
    /// statement: <c>UPDATE Sessions SET UpdatedAt = now WHERE Id = … AND (Capacity IS
    /// NULL OR (SELECT COUNT(*) of confirmed+offered signups) &lt; Capacity)</c> — the seat
    /// count is read LIVE from the signup rows (zero counter-drift) and we "touch" the
    /// capacity-bearing Session row so the DB evaluates the capacity test as one atomic
    /// statement.
    /// <para>
    /// <b>§219 RCSI FIX.</b> On <b>SQL Server / Azure SQL (prod has
    /// <c>READ_COMMITTED_SNAPSHOT ON</c>)</b> the capacity <c>COUNT(*)</c> is read with the
    /// table hint <c>WITH (UPDLOCK, HOLDLOCK)</c> via raw SQL (EF cannot emit table hints).
    /// Under RCSI a plain <c>READ COMMITTED</c> sub-select reads a <i>pre-statement
    /// row-version snapshot</i> and misses seats other transactions just committed → the old
    /// code OVERSOLD. <c>UPDLOCK</c> forces a <i>locking</i> read of the latest <b>committed</b>
    /// rows (immune to the RCSI snapshot); <c>HOLDLOCK</c> makes it a key-range (serializable)
    /// lock on the <c>(EventId, SessionId, Status)</c> index held to commit, so two concurrent
    /// claims FOR THE SAME CLASS serialize and the blocked one re-reads the now-committed count
    /// and correctly sees "full". No new column, no counter to drift, and only claims on the
    /// <i>same</i> class contend (claims on different classes lock disjoint key ranges).
    /// </para>
    /// <list type="bullet">
    /// <item><b>1 row affected ⇒ a seat is reserved.</b> The range/row locks are held until
    /// the caller's transaction commits the matching signup write, so a concurrent claim on
    /// the same class blocks, then re-evaluates the live committed count and correctly sees
    /// "full" — never an oversell of the last seat.</item>
    /// <item><b>0 rows affected ⇒ full ⇒ the caller waitlists.</b></item>
    /// </list>
    /// When <paramref name="blockWhenWaitlisted"/> is true the claim ALSO fails if anyone
    /// is waitlisted (§94 waitlist-priority: a public booker / switch may take a seat only
    /// when free AND nobody waits). Promotions pass it false — serving the waitlist is the
    /// whole point. Other relational providers (the EF SQLite test provider) keep the plain
    /// guarded <c>ExecuteUpdate</c>: SQLite has no RCSI and serializes its single write
    /// connection, so the same logic is already correct there. On the non-relational
    /// in-memory provider (no <c>ExecuteUpdate</c>; single-threaded tests) it falls back to
    /// an in-process count check; the real concurrency path is the SQL Server one, validated
    /// by the §221 prod Azure SQL load-sim.
    /// </summary>
    private async Task<bool> TryClaimSeatAsync(
        int eventId, int sessionId, bool blockWhenWaitlisted, CancellationToken ct)
    {
        if (_db.Database.IsRelational())
        {
            // SQL SERVER / AZURE SQL: read the capacity COUNT under a key-range lock so it
            // sees COMMITTED seats (RCSI-immune, §219). Status ints: Confirmed=0, Offered=2
            // occupy a seat; Waitlisted=1. The hinted sub-select needs raw SQL — EF Core
            // cannot attach WITH (UPDLOCK, HOLDLOCK). Runs inside the caller's transaction
            // (InTxAsync), so the lock is held until the matching signup write commits.
            if (_db.Database.IsSqlServer())
            {
                var now = DateTimeOffset.UtcNow;
                // Call ExecuteSqlInterpolatedAsync directly per branch so each interpolated
                // string target-types to FormattableString (a ternary would collapse them to
                // a plain string and lose parameterization).
                int affectedSql = blockWhenWaitlisted
                    ? await _db.Database.ExecuteSqlInterpolatedAsync($@"
UPDATE [Sessions] SET [UpdatedAt] = {now}
WHERE [Id] = {sessionId} AND [EventId] = {eventId} AND [Type] = 2
  AND ([MasterClassCapacity] IS NULL
       OR (SELECT COUNT(*) FROM [MasterClassSignups] WITH (UPDLOCK, HOLDLOCK)
           WHERE [SessionId] = {sessionId} AND [Status] IN (0, 2)) < [MasterClassCapacity])
  AND NOT EXISTS (SELECT 1 FROM [MasterClassSignups] WITH (UPDLOCK, HOLDLOCK)
                  WHERE [SessionId] = {sessionId} AND [Status] = 1)", ct)
                    : await _db.Database.ExecuteSqlInterpolatedAsync($@"
UPDATE [Sessions] SET [UpdatedAt] = {now}
WHERE [Id] = {sessionId} AND [EventId] = {eventId} AND [Type] = 2
  AND ([MasterClassCapacity] IS NULL
       OR (SELECT COUNT(*) FROM [MasterClassSignups] WITH (UPDLOCK, HOLDLOCK)
           WHERE [SessionId] = {sessionId} AND [Status] IN (0, 2)) < [MasterClassCapacity])", ct);
                return affectedSql > 0;
            }

            // Other relational providers (EF SQLite tests): no table hints, no RCSI, single
            // serialized write connection — the plain guarded conditional UPDATE is correct.
            var q = _db.Sessions.Where(s =>
                s.Id == sessionId && s.EventId == eventId
                && s.Type == SessionType.MasterClass
                && (s.MasterClassCapacity == null
                    || _db.MasterClassSignups.Count(x =>
                            x.SessionId == sessionId
                            && (x.Status == MasterClassSignupStatus.Confirmed
                                || x.Status == MasterClassSignupStatus.Offered))
                        < s.MasterClassCapacity));
            if (blockWhenWaitlisted)
                q = q.Where(s => !_db.MasterClassSignups.Any(x =>
                    x.SessionId == sessionId && x.Status == MasterClassSignupStatus.Waitlisted));

            var affected = await q.ExecuteUpdateAsync(
                set => set.SetProperty(s => s.UpdatedAt, DateTimeOffset.UtcNow), ct);
            return affected > 0;
        }

        // In-memory fallback (no ExecuteUpdate; tests run single-threaded so a
        // read-then-decide is sufficient — the relational path above is what real
        // concurrency exercises, proven by the §220 relational tests).
        var mc = await _db.Sessions.AsNoTracking().FirstOrDefaultAsync(
            s => s.Id == sessionId && s.EventId == eventId
                 && s.Type == SessionType.MasterClass, ct);
        if (mc is null) return false;
        if (mc.MasterClassCapacity is int cap && await SeatsTakenAsync(eventId, sessionId, ct) >= cap)
            return false;
        if (blockWhenWaitlisted && await WaitlistCountAsync(eventId, sessionId, ct) > 0)
            return false;
        return true;
    }

    /// <summary>
    /// §234 — LOCKING re-read of one attendee's signup rows, used to RE-VALIDATE the
    /// one-confirmed-seat invariant INSIDE the claim's locked region (after
    /// <see cref="TryClaimSeatAsync"/> succeeded, before the signup row is written).
    /// On <b>SQL Server / Azure SQL</b> the rows are read <c>WITH (UPDLOCK, HOLDLOCK)</c>:
    /// UPDLOCK forces a locking read of the latest COMMITTED rows (immune to the RCSI
    /// pre-statement snapshot a plain READ COMMITTED select would use) and serializes
    /// against a concurrent §93 promotion updating the same attendee's rows; HOLDLOCK
    /// key-range-locks the attendee's slice of the (EventId, AttendeeId) index until the
    /// caller's transaction commits, so a promotion can't confirm this attendee elsewhere
    /// between this check and our insert. Other providers (the EF SQLite / in-memory test
    /// providers) have no RCSI — a plain fresh re-read is already the latest state there.
    /// </summary>
    private async Task<IReadOnlyList<MasterClassSignup>> ReadAttendeeSignupsLockedAsync(
        int eventId, int attendeeId, CancellationToken ct)
    {
        if (_db.Database.IsRelational() && _db.Database.IsSqlServer())
        {
            return await _db.MasterClassSignups
                .FromSqlInterpolated($@"
SELECT * FROM [MasterClassSignups] WITH (UPDLOCK, HOLDLOCK)
WHERE [EventId] = {eventId} AND [AttendeeId] = {attendeeId}")
                .AsNoTracking()
                .ToListAsync(ct);
        }

        // SQLite (serialized single write connection) / in-memory: a fresh re-read IS the
        // latest committed state — same logic, no table hints available or needed.
        return await _db.MasterClassSignups.AsNoTracking()
            .Where(x => x.EventId == eventId && x.AttendeeId == attendeeId)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Read-only "does a public booker have room here right now?" (free seat AND no
    /// waitlist, §94). Used only to choose the right MESSAGE for an attendee who already
    /// holds a confirmed seat (give-up-first vs join-the-waitlist) — never to grant a
    /// seat, so it needs no atomicity (the actual grant always goes through
    /// <see cref="TryClaimSeatAsync"/>).
    /// </summary>
    private async Task<bool> HasPublicRoomAsync(int eventId, int sessionId, CancellationToken ct)
    {
        var mc = await _db.Sessions.AsNoTracking().FirstOrDefaultAsync(
            s => s.Id == sessionId && s.EventId == eventId
                 && s.Type == SessionType.MasterClass, ct);
        if (mc is null) return false;
        if (mc.MasterClassCapacity is int cap && await SeatsTakenAsync(eventId, sessionId, ct) >= cap)
            return false;
        return await WaitlistCountAsync(eventId, sessionId, ct) == 0;
    }

    /// <summary>
    /// §93 ATOMIC PROMOTION into a just-freed/added seat. Takes the attendee HIGHEST up
    /// the FIFO waitlist, confirms them in <paramref name="sessionId"/>, removes them
    /// from the waitlist, and CANCELS any confirmed seat they held in another class — so
    /// they end with exactly ONE active Master Class and NO waitlist place. There is no
    /// "offer/choose" step: the move just happens. Cancelling the old seat frees one
    /// THERE, so we cascade into that class's waitlist (chain promotions until no more
    /// waitlisted person can be promoted). Only promotes when there is a free seat. Every
    /// actual promotion in the chain is appended to <paramref name="sink"/> (so the caller
    /// can notify each moved attendee). MUST run inside the caller's transaction (the entry
    /// points wrap it via <see cref="InTxAsync"/>; this method never opens its own).
    /// <para>
    /// §218 race-safety: the free seat is reserved with the SAME optimistic atomic claim
    /// (<see cref="TryClaimSeatAsync"/>, waitlist-priority OFF — serving the waitlist is the
    /// point) the public path uses, so a concurrent give-up/promote can never double-fill a
    /// single seat. We claim FIRST, then pick the current head of the waitlist — so two
    /// concurrent promotions each grab a DISTINCT seat and promote DISTINCT attendees (no
    /// double-promote), and the claim's row lock blocks a racing public booker.
    /// </para>
    /// </summary>
    private async Task<PromotionResult?> PromoteNextAsync(
        int eventId, int sessionId, List<PromotionResult> sink, CancellationToken ct)
    {
        // Reserve a seat atomically BEFORE choosing who fills it (so concurrent promotions
        // can't both pick the same head and consume two seats for one person). 0 rows ⇒ no
        // free seat (full / no such class) ⇒ nothing to promote.
        if (!await TryClaimSeatAsync(eventId, sessionId, blockWhenWaitlisted: false, ct))
            return null;

        // Highest up the waitlist (FIFO by CreatedAt), read AFTER the claim so a racing
        // promotion that already confirmed the previous head sees the next one. They are
        // taken regardless of any seat held elsewhere — that old seat is auto-cancelled
        // below (§93). The CreatedAt ordering is done client-side (the EF SQLite provider
        // can't ORDER BY a DateTimeOffset; SQL Server can) over the bounded waitlist set.
        var waiting = await _db.MasterClassSignups
            .Where(x => x.EventId == eventId && x.SessionId == sessionId
                        && x.Status == MasterClassSignupStatus.Waitlisted)
            .ToListAsync(ct);
        var first = waiting.OrderBy(x => x.CreatedAt).FirstOrDefault();
        if (first is null) return new PromotionResult(sessionId, null, null, null); // nobody waiting

        var now = DateTimeOffset.UtcNow;

        // Their existing confirmed seat in another class (to be released on switch).
        var old = await _db.MasterClassSignups.FirstOrDefaultAsync(
            x => x.EventId == eventId && x.AttendeeId == first.AttendeeId
                 && x.Status == MasterClassSignupStatus.Confirmed && x.SessionId != sessionId, ct);

        // §386: read the released class's TITLE before the row goes, so the promotion mail can name
        // what was traded. Afterwards there is nothing left to look it up from.
        string? releasedTitle = null;
        if (old is not null)
        {
            releasedTitle = await _db.Sessions.AsNoTracking()
                .Where(s => s.Id == old.SessionId)
                .Select(s => s.Title)
                .FirstOrDefaultAsync(ct);
        }

        first.Status = MasterClassSignupStatus.Confirmed;
        first.ConfirmedAt = now;
        first.OfferExpiresAt = null;
        first.PromotionNotifiedAt = null;
        first.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        var result = new PromotionResult(
            sessionId, first.Id, first.AttendeeId, PromotionKind.Confirmed, releasedTitle);
        sink.Add(result);

        if (old is not null)
        {
            var freedSession = old.SessionId;
            _db.MasterClassSignups.Remove(old);
            await _db.SaveChangesAsync(ct);
            await PromoteNextAsync(eventId, freedSession, sink, ct); // cascade into the released seat
        }
        return result;
    }
}
