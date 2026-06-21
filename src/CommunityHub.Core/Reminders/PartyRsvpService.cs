using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Reminders;

/// <summary>
/// Backs the anonymous Party RSVP (REQUIREMENTS §6): resolves the active edition +
/// the Party window (16:00–18:00 on the edition's pre-day = <see cref="Event.StartDate"/>),
/// upserts an RSVP by email, and lists/counts them for organizers. No login, no
/// Participant created. Basic validation only (name + a plausible email).
/// </summary>
public sealed class PartyRsvpService
{
    private readonly CommunityHubDbContext _db;
    public PartyRsvpService(CommunityHubDbContext db) => _db = db;

    /// <summary>The Party start/end hour (local), per the operator: 16:00–18:00.</summary>
    public const int PartyStartHour = 16;
    public const int PartyEndHour = 18;

    public sealed record PartyInfo(int EventId, string EventName, DateOnly Date, int StartHour, int EndHour);

    /// <summary>The active edition's Party, or null when there is no active edition.</summary>
    public async Task<PartyInfo?> GetActivePartyAsync(CancellationToken ct = default)
    {
        var ev = await _db.Events.AsNoTracking()
            .Where(e => e.IsActive)
            .Select(e => new { e.Id, e.DisplayName, e.StartDate })
            .FirstOrDefaultAsync(ct);
        return ev is null ? null
            : new PartyInfo(ev.Id, ev.DisplayName, ev.StartDate, PartyStartHour, PartyEndHour);
    }

    public sealed record SubmitResult(bool Ok, string? Error);

    /// <summary>
    /// Record an RSVP for the active edition (upsert by email). Validates name +
    /// a basic email shape. Returns an error message the form shows; never throws.
    /// </summary>
    public async Task<SubmitResult> SubmitAsync(
        string? name, string? email, bool attending, string? ipHash, CancellationToken ct = default)
    {
        var party = await GetActivePartyAsync(ct);
        if (party is null) return new SubmitResult(false, "There is no active event right now.");

        name = name?.Trim() ?? string.Empty;
        email = email?.Trim() ?? string.Empty;
        if (name.Length < 2) return new SubmitResult(false, "Please enter your name.");
        if (!LooksLikeEmail(email)) return new SubmitResult(false, "Please enter a valid email address.");

        var existing = await _db.PartyRsvps
            .FirstOrDefaultAsync(r => r.EventId == party.EventId && r.Email == email, ct);
        var now = DateTimeOffset.UtcNow;
        if (existing is null)
        {
            _db.PartyRsvps.Add(new PartyRsvp
            {
                EventId = party.EventId, Name = name, Email = email,
                Attending = attending, IpHash = ipHash, CreatedAt = now, UpdatedAt = now,
            });
        }
        else
        {
            existing.Name = name;
            existing.Attending = attending;
            existing.IpHash = ipHash;
            existing.UpdatedAt = now;
        }
        await _db.SaveChangesAsync(ct);
        return new SubmitResult(true, null);
    }

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
