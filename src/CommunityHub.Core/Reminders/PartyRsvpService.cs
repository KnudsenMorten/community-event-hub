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

    public sealed record PartyInfo(
        int EventId, string EventName, DateOnly Date,
        int StartHour, int EndHour, int StartMinute = 0, int EndMinute = 0);

    /// <summary>The active edition's Party, or null when there is no active edition.</summary>
    public async Task<PartyInfo?> GetActivePartyAsync(CancellationToken ct = default)
    {
        var ev = await _db.Events.AsNoTracking()
            .Where(e => e.IsActive)
            .Select(e => new { e.Id, e.DisplayName, e.StartDate })
            .FirstOrDefaultAsync(ct);
        return ev is null ? null
            : new PartyInfo(ev.Id, ev.DisplayName, ev.StartDate,
                PartyStartHour, PartyEndHour, PartyStartMinute, PartyEndMinute);
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

    public Task<List<PartyRsvp>> GetAllAsync(int eventId, CancellationToken ct = default) =>
        _db.PartyRsvps.AsNoTracking().Where(r => r.EventId == eventId)
            .OrderByDescending(r => r.UpdatedAt).ToListAsync(ct);

    /// <summary>(total submissions, attending count) for an edition.</summary>
    public async Task<(int Total, int Attending)> CountsAsync(int eventId, CancellationToken ct = default)
    {
        var rows = await _db.PartyRsvps.AsNoTracking()
            .Where(r => r.EventId == eventId)
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
