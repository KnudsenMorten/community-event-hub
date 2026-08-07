using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Reminders;

/// <summary>
/// Backs the Party RSVP (REQUIREMENTS §6, §164): resolves the active edition +
/// the Party window (16:00–18:30 on the edition's pre-day = <see cref="Event.StartDate"/>),
/// upserts an RSVP by email, and lists/counts them for organizers. Serves BOTH the
/// anonymous public form (no login, no Participant) AND a signed-in participant who
/// RSVPs as themselves (carries their <c>ParticipantId</c> + a sponsor head count).
/// Basic validation only (name + a plausible email).
/// </summary>
public sealed class PartyRsvpService
{
    private readonly CommunityHubDbContext _db;
    public PartyRsvpService(CommunityHubDbContext db) => _db = db;

    /// <summary>The Party window (local), per the operator (§164): 16:00–18:30. The end
    /// minute is a first-class part of the window so the display + the .ics DTEND stay
    /// data-driven from this ONE source rather than three hardcoded ":00"/":30" spots.</summary>
    public const int PartyStartHour = 16;
    public const int PartyStartMinute = 0;
    public const int PartyEndHour = 18;
    public const int PartyEndMinute = 30;

    /// <summary>The Party venue/area (§206): the expo/food area at Bella Center. Shown on the
    /// form and used as the calendar-invite LOCATION so the one source feeds both.</summary>
    public const string PartyLocation = "Expo / food area, Bella Center";

    /// <summary>The IANA timezone the Party window is expressed in (Copenhagen — Bella Center).
    /// Used to turn the local 16:00–18:30 window into a UTC calendar invite (§206/§193).</summary>
    public const string PartyTimezone = "Europe/Copenhagen";

    public sealed record PartyInfo(
        int EventId, string EventName, DateOnly Date,
        int StartHour, int EndHour, int StartMinute = 0, int EndMinute = 0,
        string Location = PartyLocation);

    /// <summary>The active edition's Party, or null when there is no active edition.</summary>
    public async Task<PartyInfo?> GetActivePartyAsync(CancellationToken ct = default)
    {
        var ev = await _db.Events.AsNoTracking()
            .Where(e => e.IsActive)
            .Select(e => new { e.Id, e.DisplayName, e.StartDate })
            .FirstOrDefaultAsync(ct);
        return ev is null ? null
            : new PartyInfo(ev.Id, ev.DisplayName, ev.StartDate,
                PartyStartHour, PartyEndHour, PartyStartMinute, PartyEndMinute, PartyLocation);
    }

    /// <summary>
    /// The Party window as absolute UTC instants for the active edition (§206/§193): the
    /// edition's pre-day at <see cref="PartyStartHour"/>:<see cref="PartyStartMinute"/>–
    /// <see cref="PartyEndHour"/>:<see cref="PartyEndMinute"/> local (<see cref="PartyTimezone"/>),
    /// converted to UTC so a calendar invite lands at the right wall-clock time. Null when no
    /// active edition.
    /// </summary>
    public static (DateTimeOffset StartUtc, DateTimeOffset EndUtc) WindowUtc(PartyInfo party)
    {
        var tz = CommunityHub.Core.Config.EventLocalTime.Resolve(PartyTimezone);
        var startLocal = party.Date.ToDateTime(new TimeOnly(party.StartHour, party.StartMinute));
        var endLocal = party.Date.ToDateTime(new TimeOnly(party.EndHour, party.EndMinute));
        var start = new DateTimeOffset(startLocal, tz.GetUtcOffset(startLocal));
        var end = new DateTimeOffset(endLocal, tz.GetUtcOffset(endLocal));
        return (start.ToUniversalTime(), end.ToUniversalTime());
    }

    public sealed record SubmitResult(bool Ok, string? Error);

    /// <summary>
    /// Record an RSVP for the active edition (upsert by email). Validates name +
    /// a basic email shape. Returns an error message the form shows; never throws.
    /// <paramref name="headCount"/> (sponsor "how many from your company") and
    /// <paramref name="participantId"/> (set when a signed-in participant RSVPs) are
    /// stamped on the row; both are null for an anonymous single-person RSVP (§164).
    /// </summary>
    public async Task<SubmitResult> SubmitAsync(
        string? name, string? email, bool attending, string? ipHash,
        int? headCount = null, int? participantId = null, CancellationToken ct = default)
    {
        var party = await GetActivePartyAsync(ct);
        if (party is null) return new SubmitResult(false, "There is no active event right now.");

        name = name?.Trim() ?? string.Empty;
        email = email?.Trim() ?? string.Empty;
        if (name.Length < 2) return new SubmitResult(false, "Please enter your name.");
        if (!LooksLikeEmail(email)) return new SubmitResult(false, "Please enter a valid email address.");

        // A head count is only meaningful when the person is actually attending; a
        // declined RSVP carries none. Clamp to a sane minimum of 1 when supplied.
        var resolvedHeadCount = (attending && headCount is { } hc) ? Math.Max(1, hc) : (int?)null;

        var existing = await _db.PartyRsvps
            .FirstOrDefaultAsync(r => r.EventId == party.EventId && r.Email == email, ct);
        var now = DateTimeOffset.UtcNow;
        if (existing is null)
        {
            _db.PartyRsvps.Add(new PartyRsvp
            {
                EventId = party.EventId, Name = name, Email = email,
                Attending = attending, HeadCount = resolvedHeadCount, ParticipantId = participantId,
                IpHash = ipHash, CreatedAt = now, UpdatedAt = now,
            });
        }
        else
        {
            existing.Name = name;
            existing.Attending = attending;
            existing.HeadCount = resolvedHeadCount;
            // Stamp the participant link when a signed-in person re-submits a row that
            // may have started life anonymous (same email); never null it back out.
            if (participantId is not null) existing.ParticipantId = participantId;
            existing.IpHash = ipHash;
            existing.UpdatedAt = now;
        }
        await _db.SaveChangesAsync(ct);
        return new SubmitResult(true, null);
    }

    /// <summary>§164: the signed-in participant's own RSVP for the edition (so the form can
    /// prefill their prior answer + head count), or null when they haven't answered yet.</summary>
    public Task<PartyRsvp?> GetForParticipantAsync(int eventId, int participantId, CancellationToken ct = default) =>
        _db.PartyRsvps.AsNoTracking()
            .FirstOrDefaultAsync(r => r.EventId == eventId && r.ParticipantId == participantId, ct);

    /// <summary>§228: a sponsor company's single GROUP party reservation, surfaced to every
    /// contact linked to the company (who registered it, the head count, and when).</summary>
    public sealed record CompanyReservation(
        int RsvpId, string Name, string Email, bool Attending, int? HeadCount,
        DateTimeOffset UpdatedAt, int? ParticipantId);

    /// <summary>
    /// §228: the ONE group reservation for the sponsor company that <paramref name="participantId"/>
    /// is linked to — the most recently updated RSVP submitted by ANY participant of that company.
    /// Null when the participant has no company link or nobody from the company has answered yet.
    /// Every linked contact sees the same reservation, whoever registered it.
    /// </summary>
    public async Task<CompanyReservation?> GetCompanyReservationAsync(
        int eventId, int participantId, CancellationToken ct = default)
    {
        var companyId = await _db.Participants.AsNoTracking()
            .Where(p => p.Id == participantId && p.EventId == eventId)
            .Select(p => p.SponsorCompanyId)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(companyId)) return null;
        return await FindCompanyReservationAsync(eventId, companyId, ct);
    }

    private async Task<CompanyReservation?> FindCompanyReservationAsync(
        int eventId, string companyId, CancellationToken ct)
    {
        return await _db.PartyRsvps.AsNoTracking()
            .Where(r => r.EventId == eventId && r.ParticipantId != null)
            .Where(r => _db.Participants.Any(p =>
                p.Id == r.ParticipantId && p.EventId == eventId && p.SponsorCompanyId == companyId))
            .OrderByDescending(r => r.UpdatedAt).ThenBy(r => r.Id)
            .Select(r => new CompanyReservation(
                r.Id, r.Name, r.Email, r.Attending, r.HeadCount, r.UpdatedAt, r.ParticipantId))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// §228: record the SPONSOR COMPANY's single group reservation. Whichever linked contact
    /// saves, the SAME reservation row is updated (re-stamped to the submitter) — never a
    /// second row per contact — and any other lingering RSVP rows from the company's contacts
    /// are neutralised (flipped to not-attending, head count cleared) so the group counts
    /// exactly once in the headcount. Falls back to the personal upsert when the participant
    /// has no company link.
    /// </summary>
    public async Task<SubmitResult> SubmitGroupAsync(
        string? name, string? email, bool attending, int? headCount,
        int participantId, CancellationToken ct = default)
    {
        var party = await GetActivePartyAsync(ct);
        if (party is null) return new SubmitResult(false, "There is no active event right now.");

        var companyId = await _db.Participants.AsNoTracking()
            .Where(p => p.Id == participantId && p.EventId == party.EventId)
            .Select(p => p.SponsorCompanyId)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(companyId))
            return await SubmitAsync(name, email, attending, ipHash: null, headCount, participantId, ct);

        name = name?.Trim() ?? string.Empty;
        email = email?.Trim() ?? string.Empty;
        if (name.Length < 2) return new SubmitResult(false, "Please enter your name.");
        if (!LooksLikeEmail(email)) return new SubmitResult(false, "Please enter a valid email address.");
        var resolvedHeadCount = (attending && headCount is { } hc) ? Math.Max(1, hc) : (int?)null;

        var now = DateTimeOffset.UtcNow;

        // Every RSVP row held by ANY of the company's linked contacts (oldest first so the
        // FIRST becomes/stays the canonical group row and later strays are neutralised).
        var companyRows = await _db.PartyRsvps
            .Where(r => r.EventId == party.EventId && r.ParticipantId != null)
            .Where(r => _db.Participants.Any(p =>
                p.Id == r.ParticipantId && p.EventId == party.EventId && p.SponsorCompanyId == companyId))
            .OrderBy(r => r.Id)
            .ToListAsync(ct);

        var group = companyRows.FirstOrDefault();
        if (group is null)
        {
            _db.PartyRsvps.Add(new PartyRsvp
            {
                EventId = party.EventId, Name = name, Email = email,
                Attending = attending, HeadCount = resolvedHeadCount, ParticipantId = participantId,
                CreatedAt = now, UpdatedAt = now,
            });
        }
        else
        {
            group.Name = name;
            group.Email = email;
            group.Attending = attending;
            group.HeadCount = resolvedHeadCount;
            group.ParticipantId = participantId;
            group.UpdatedAt = now;
            // Neutralise any OTHER company rows so the group is counted exactly once.
            foreach (var stray in companyRows.Skip(1).Where(r => r.Attending || r.HeadCount != null))
            {
                stray.Attending = false;
                stray.HeadCount = null;
                stray.UpdatedAt = now;
            }
        }
        await _db.SaveChangesAsync(ct);
        return new SubmitResult(true, null);
    }

    /// <summary>
    /// §253 G3 — the organizer list/export/headcount rows: every anonymous RSVP
    /// (no participant link — the public form's nullable-FK semantics are kept)
    /// plus the RSVPs of still-ACTIVE participants. A deactivated participant's
    /// row is excluded belt-and-braces (the G1 cascade also cancels it) so the
    /// venue food order never counts someone who left.
    ///
    /// <para>🔒 §946 — <b>and never a TEST USER</b> (operator 2026-08-07:
    /// <i>"IsTestUsers should not count towards these logistics"</i>). A party RSVP is a seat and a
    /// meal the venue is told to prepare. The predicate is the FLAG, not the ring — the seeded
    /// <c>test-*@</c> accounts are <c>IsTestUser</c> without being Ring 1.</para>
    ///
    /// <para>⚠️ An ANONYMOUS RSVP (no participant link) still counts: there is no flag to read and no
    /// way to know it is synthetic, and dropping unlinked rows would silently lose real public
    /// sign-ups — the expensive direction.</para>
    /// </summary>
    public Task<List<PartyRsvp>> GetAllAsync(int eventId, CancellationToken ct = default) =>
        _db.PartyRsvps.AsNoTracking().Where(r => r.EventId == eventId)
            .Where(r => r.ParticipantId == null
                        || _db.Participants.Any(p => p.Id == r.ParticipantId && p.IsActive && !p.IsTestUser))
            .OrderByDescending(r => r.UpdatedAt).ToListAsync(ct);

    /// <summary>(total submissions, attending count) for an edition — active-only and non-test,
    /// the same §253 G3 + §946 filter as <see cref="GetAllAsync"/> so the headline number and
    /// the list always agree. 🔒 They MUST stay identical: a total that drops by three while the
    /// roster beside it still shows the three rows makes an organizer reconcile by hand and trust
    /// neither.</summary>
    public async Task<(int Total, int Attending)> CountsAsync(int eventId, CancellationToken ct = default)
    {
        var rows = await _db.PartyRsvps.AsNoTracking()
            .Where(r => r.EventId == eventId)
            .Where(r => r.ParticipantId == null
                        || _db.Participants.Any(p => p.Id == r.ParticipantId && p.IsActive && !p.IsTestUser))
            .Select(r => r.Attending).ToListAsync(ct);
        return (rows.Count, rows.Count(a => a));
    }

    private static bool LooksLikeEmail(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var at = s.IndexOf('@');
        return at > 0 && at < s.Length - 1 && s.IndexOf('.', at) > at;
    }
}
