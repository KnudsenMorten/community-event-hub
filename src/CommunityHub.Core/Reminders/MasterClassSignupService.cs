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
        int SessionId, string Title, int? Capacity, int Confirmed, int Offered, int Waitlisted)
    {
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

    public sealed record MySignup(
        int SessionId, string Title, MasterClassSignupStatus Status,
        int? WaitlistPosition, DateTimeOffset? OfferExpiresAt, bool WantsMonthReminder = false);

    public sealed record SignupResult(bool Ok, string? Error, MySignup? Signup);

    /// <summary>A promotion produced by a freed seat — who to notify + how.</summary>
    public sealed record PromotionResult(
        int SessionId, int? PromotedSignupId, int? PromotedAttendeeId, PromotionKind? Kind);

    // --- selection-invite tracking ------------------------------------------

    /// <summary>(eligible 2-day attendees, how many invited, how many not yet invited).</summary>
    public async Task<(int Eligible, int Invited, int NotInvited)> InviteStatsAsync(
        int eventId, CancellationToken ct = default)
    {
        var rows = await _db.Attendees.AsNoTracking()
            .Where(a => a.EventId == eventId && a.TicketStatus == TicketStatus.TwoDay)
            .Select(a => a.MasterClassInviteSentAt).ToListAsync(ct);
        var invited = rows.Count(x => x != null);
        return (rows.Count, invited, rows.Count - invited);
    }

    /// <summary>2-day-ticket attendees not yet sent the selection invite.</summary>
    public Task<List<int>> EligibleNotInvitedIdsAsync(int eventId, CancellationToken ct = default) =>
        _db.Attendees.AsNoTracking()
            .Where(a => a.EventId == eventId && a.TicketStatus == TicketStatus.TwoDay
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
        return _db.Attendees.FirstOrDefaultAsync(a => a.EventId == eventId && a.Email == norm, ct);
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
            .Where(s => s.EventId == eventId && s.Type == SessionType.CommunityMasterClass)
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
                Type = SessionType.CommunityMasterClass, IsHubAdded = true, CreatedAt = now,
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
            a => a.Id == attendeeId && a.EventId == eventId && a.TicketStatus == TicketStatus.TwoDay, ct);

    public async Task SetCapacityAsync(int eventId, int sessionId, int? capacity, CancellationToken ct = default)
    {
        var mc = await _db.Sessions.FirstOrDefaultAsync(
            s => s.Id == sessionId && s.EventId == eventId && s.Type == SessionType.CommunityMasterClass, ct);
        if (mc is null) return;
        mc.MasterClassCapacity = capacity is > 0 ? capacity : null;
        mc.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    // --- reads ---------------------------------------------------------------

    public async Task<IReadOnlyList<McOption>> ListMasterClassesAsync(int eventId, CancellationToken ct = default)
    {
        await ExpireOffersAsync(DateTimeOffset.UtcNow, eventId, ct);
        var mcs = await _db.Sessions.AsNoTracking()
            .Where(s => s.EventId == eventId && s.Type == SessionType.CommunityMasterClass && !s.IsServiceSession)
            .Select(s => new { s.Id, s.Title, s.MasterClassCapacity }).ToListAsync(ct);

        var counts = await _db.MasterClassSignups.AsNoTracking()
            .Where(x => x.EventId == eventId)
            .GroupBy(x => new { x.SessionId, x.Status })
            .Select(g => new { g.Key.SessionId, g.Key.Status, Count = g.Count() }).ToListAsync(ct);

        int C(int sid, MasterClassSignupStatus st) =>
            counts.Where(c => c.SessionId == sid && c.Status == st).Sum(c => c.Count);

        return mcs.Select(m => new McOption(m.Id, m.Title, m.MasterClassCapacity,
            C(m.Id, MasterClassSignupStatus.Confirmed),
            C(m.Id, MasterClassSignupStatus.Offered),
            C(m.Id, MasterClassSignupStatus.Waitlisted)))
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
            .Select(x => new { x.SessionId, x.Status, x.CreatedAt, x.OfferExpiresAt, x.WantsMonthBeforeReminder, Title = x.Session.Title })
            .ToListAsync(ct);

        var outList = new List<MySignup>();
        foreach (var r in rows)
        {
            int? pos = null;
            if (r.Status == MasterClassSignupStatus.Waitlisted)
                pos = await _db.MasterClassSignups.AsNoTracking().CountAsync(
                    x => x.EventId == eventId && x.SessionId == r.SessionId
                         && x.Status == MasterClassSignupStatus.Waitlisted
                         && x.CreatedAt <= r.CreatedAt, ct);
            outList.Add(new MySignup(r.SessionId, r.Title, r.Status, pos, r.OfferExpiresAt, r.WantsMonthBeforeReminder));
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
        var rows = await _db.MasterClassSignups.AsNoTracking()
            .Where(x => x.EventId == eventId && x.SessionId == sessionId)
            .OrderBy(x => x.CreatedAt)
            .Select(x => new RosterEntry(x.Status, x.AttendeeId,
                ((x.Attendee.FirstName ?? "") + " " + (x.Attendee.LastName ?? "")).Trim(),
                x.Attendee.Email, x.CreatedAt))
            .ToListAsync(ct);

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
                 && s.Type == SessionType.CommunityMasterClass && !s.IsServiceSession, ct);
        if (mc is null) return new SignupResult(false, "That Master Class was not found.", null);

        await ExpireOffersAsync(DateTimeOffset.UtcNow, eventId, ct);

        var mine = await _db.MasterClassSignups
            .Where(x => x.EventId == eventId && x.AttendeeId == attendeeId).ToListAsync(ct);
        if (mine.Any(x => x.SessionId == sessionId))
            return new SignupResult(true, null, (await GetForAttendeeAsync(eventId, attendeeId, ct))
                .FirstOrDefault(s => s.SessionId == sessionId));

        var hasConfirmed = mine.Any(x => x.Status == MasterClassSignupStatus.Confirmed);
        var hasWaitOrOffer = mine.Any(x => x.Status is MasterClassSignupStatus.Waitlisted or MasterClassSignupStatus.Offered);

        var taken = await SeatsTakenAsync(eventId, sessionId, ct);
        var hasRoom = mc.MasterClassCapacity is not int cap || taken < cap;
        var now = DateTimeOffset.UtcNow;

        MasterClassSignupStatus status;
        if (hasRoom)
        {
            if (hasConfirmed)
                return new SignupResult(false,
                    "You already have a confirmed Master Class. Give it up first to take another.", null);
            status = MasterClassSignupStatus.Confirmed;
        }
        else
        {
            if (hasWaitOrOffer)
                return new SignupResult(false,
                    "You can wait-list for one Master Class at a time. Leave your current waitlist first.", null);
            status = MasterClassSignupStatus.Waitlisted;
        }

        // Consent gate: waitlisting while you already hold a seat means that, on
        // promotion, your current Master Class is auto-cancelled and you're moved
        // here (AutoSwitch default; OfferAndDecide also times out to auto-switch).
        // The attendee must accept those terms. (Skip mode never cancels, so no consent.)
        var settings = await GetSettingsAsync(eventId, ct);
        var willAutoCancel = status == MasterClassSignupStatus.Waitlisted && hasConfirmed
            && settings.PromotionMode != MasterClassPromotionMode.Skip;
        if (willAutoCancel && !autoSwitchConsent)
            return new SignupResult(false,
                "Please accept that joining this waitlist will cancel your current Master Class and move you here automatically.", null);

        _db.MasterClassSignups.Add(new MasterClassSignup
        {
            EventId = eventId, SessionId = sessionId, AttendeeId = attendeeId,
            Status = status, CreatedAt = now, UpdatedAt = now,
            ConfirmedAt = status == MasterClassSignupStatus.Confirmed ? now : null,
            AutoSwitchConsentAt = willAutoCancel ? now : null,
        });
        await _db.SaveChangesAsync(ct);
        return new SignupResult(true, null,
            (await GetForAttendeeAsync(eventId, attendeeId, ct)).FirstOrDefault(s => s.SessionId == sessionId));
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
        var mine = await _db.MasterClassSignups.FirstOrDefaultAsync(
            x => x.EventId == eventId && x.AttendeeId == attendeeId && x.SessionId == sessionId, ct);
        if (mine is null) return null;

        var freedSeat = mine.Status is MasterClassSignupStatus.Confirmed or MasterClassSignupStatus.Offered;
        _db.MasterClassSignups.Remove(mine);
        await _db.SaveChangesAsync(ct);

        if (!freedSeat) return new PromotionResult(sessionId, null, null, null);
        return await PromoteNextAsync(eventId, sessionId, ct);
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
            freed = await PromoteNextAsync(eventId, freedSession, ct);
        }

        offered.Status = MasterClassSignupStatus.Confirmed;
        offered.ConfirmedAt = DateTimeOffset.UtcNow;
        offered.OfferExpiresAt = null;
        offered.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return (true, null, freed);
    }

    /// <summary>Decline a held offer: drop it (frees the held seat → promote next).</summary>
    public async Task<PromotionResult?> DeclineOfferAsync(
        int eventId, int attendeeId, CancellationToken ct = default)
    {
        var offered = await _db.MasterClassSignups.FirstOrDefaultAsync(
            x => x.EventId == eventId && x.AttendeeId == attendeeId
                 && x.Status == MasterClassSignupStatus.Offered, ct);
        if (offered is null) return null;
        var sessionId = offered.SessionId;
        _db.MasterClassSignups.Remove(offered);
        await _db.SaveChangesAsync(ct);
        return await PromoteNextAsync(eventId, sessionId, ct);
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
        var expired = await _db.MasterClassSignups
            .Where(x => x.Status == MasterClassSignupStatus.Offered
                        && x.OfferExpiresAt != null && x.OfferExpiresAt <= now
                        && (eventId == null || x.EventId == eventId))
            .ToListAsync(ct);
        if (expired.Count == 0) return Array.Empty<PromotionResult>();

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
                var promo = await PromoteNextAsync(o.EventId, freedSession, ct);
                if (promo is not null) results.Add(promo);
            }
        }
        return results;
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

    /// <summary>
    /// Promote into a just-freed seat per the edition's <see cref="MasterClassPromotionMode"/>.
    /// A waitlisted attendee with no other seat is always CONFIRMED. When the next
    /// waitlisted attendee already holds a confirmed seat: <b>OfferAndDecide</b> holds
    /// the seat as an OFFER (configurable hours) for them to choose; <b>AutoSwitch</b>
    /// moves them and releases their old seat (promoting that MC too); <b>Skip</b>
    /// passes them over to the first seatless waitlister. Only promotes when there's room.
    /// </summary>
    private async Task<PromotionResult?> PromoteNextAsync(int eventId, int sessionId, CancellationToken ct)
    {
        var mc = await _db.Sessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.EventId == eventId, ct);
        if (mc is null) return null;
        var taken = await SeatsTakenAsync(eventId, sessionId, ct);
        if (mc.MasterClassCapacity is int cap && taken >= cap) return null; // no free seat

        var settings = await GetSettingsAsync(eventId, ct);
        var now = DateTimeOffset.UtcNow;

        // Waitlist in FIFO order; behaviour depends on whether the candidate holds a seat.
        var waitlist = await _db.MasterClassSignups
            .Where(x => x.EventId == eventId && x.SessionId == sessionId
                        && x.Status == MasterClassSignupStatus.Waitlisted)
            .OrderBy(x => x.CreatedAt).ToListAsync(ct);
        if (waitlist.Count == 0) return new PromotionResult(sessionId, null, null, null);

        async Task<bool> HoldsSeat(int attendeeId) => await _db.MasterClassSignups.AnyAsync(
            x => x.EventId == eventId && x.AttendeeId == attendeeId
                 && x.Status == MasterClassSignupStatus.Confirmed, ct);

        PromotionResult Confirm(MasterClassSignup s)
        {
            s.Status = MasterClassSignupStatus.Confirmed;
            s.ConfirmedAt = now; s.OfferExpiresAt = null; s.PromotionNotifiedAt = null; s.UpdatedAt = now;
            return new PromotionResult(sessionId, s.Id, s.AttendeeId, PromotionKind.Confirmed);
        }

        var first = waitlist[0];
        var firstHolds = await HoldsSeat(first.AttendeeId);

        if (!firstHolds)
        {
            var r = Confirm(first);
            await _db.SaveChangesAsync(ct);
            return r;
        }

        switch (settings.PromotionMode)
        {
            case MasterClassPromotionMode.OfferAndDecide:
                first.Status = MasterClassSignupStatus.Offered;
                first.OfferExpiresAt = now + TimeSpan.FromHours(settings.OfferHoldHours);
                first.PromotionNotifiedAt = null; first.UpdatedAt = now;
                await _db.SaveChangesAsync(ct);
                return new PromotionResult(sessionId, first.Id, first.AttendeeId, PromotionKind.Offered);

            case MasterClassPromotionMode.AutoSwitch:
            {
                var old = await _db.MasterClassSignups.FirstOrDefaultAsync(
                    x => x.EventId == eventId && x.AttendeeId == first.AttendeeId
                         && x.Status == MasterClassSignupStatus.Confirmed, ct);
                var r = Confirm(first);
                await _db.SaveChangesAsync(ct);
                if (old is not null)
                {
                    var freedSession = old.SessionId;
                    _db.MasterClassSignups.Remove(old);
                    await _db.SaveChangesAsync(ct);
                    await PromoteNextAsync(eventId, freedSession, ct); // cascade into the released seat
                }
                return r;
            }

            case MasterClassPromotionMode.Skip:
            default:
            {
                foreach (var w in waitlist)
                {
                    if (await HoldsSeat(w.AttendeeId)) continue; // pass over seat-holders
                    var r = Confirm(w);
                    await _db.SaveChangesAsync(ct);
                    return r;
                }
                return new PromotionResult(sessionId, null, null, null); // nobody eligible
            }
        }
    }
}
