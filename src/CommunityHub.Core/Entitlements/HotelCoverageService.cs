using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Entitlements;

/// <summary>One person's resolved coverage, with everything a surface needs to explain it.</summary>
/// <param name="Class">
/// Null when hotel coverage does not apply to them (attendee / sponsor / uncategorized speaker) —
/// then <paramref name="Result"/> covers nothing and no promise is made.
/// </param>
public sealed record ParticipantHotelCoverage(
    int ParticipantId,
    HotelCoverageClass? Class,
    HotelCoverageRule Rule,
    HotelCoverageResult Result)
{
    /// <summary>True when this person is running on the class standard with nothing added.</summary>
    public bool OnStandard => Result.Extra == 0;
}

/// <summary>
/// §1296 — the ONE place that answers "how many hotel nights does ELDK pay for this person, and how
/// many do they pay themselves?". EF-backed, edition-scoped; the arithmetic itself is the pure
/// <see cref="HotelCoverage"/>.
/// </summary>
/// <remarks>
/// <para>🔑 <b>Every surface calls this and none of them re-derives it</b> — the hotel form, the
/// person's organizer page, the hotel-assignments list, the per-hotel Excel and the confirmation
/// mail. That is the whole design: a number about somebody's money that is computed in five places
/// is five numbers.</para>
///
/// <para>It replaces <c>SpeakerDayScope.FundedHotelNights</c>, which was speaker-only, hard-coded,
/// and displayed on exactly one page.</para>
/// </remarks>
public sealed class HotelCoverageService
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public HotelCoverageService(CommunityHubDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    // ---------------------------------------------------------------------
    //  The standards (the "included nights per rule")
    // ---------------------------------------------------------------------

    /// <summary>
    /// Every class's effective rule for an edition: the stored row where one exists, the catalog
    /// default everywhere else. Always returns all classes, so a caller never has to know which
    /// ones have been saved.
    /// </summary>
    public async Task<IReadOnlyDictionary<HotelCoverageClass, HotelCoverageRule>> GetStandardsAsync(
        int eventId, CancellationToken ct = default)
    {
        var stored = await _db.HotelCoverageStandards
            .AsNoTracking()
            .Where(s => s.EventId == eventId)
            .ToListAsync(ct);

        var byClass = stored.ToDictionary(s => s.Class, s => s.ToRule());

        return HotelCoverage.All.ToDictionary(
            c => c,
            c => byClass.TryGetValue(c, out var r) ? r : HotelCoverage.DefaultFor(c));
    }

    /// <summary>Which classes an organizer has actually saved (the rest are showing defaults).</summary>
    public async Task<IReadOnlySet<HotelCoverageClass>> GetCustomizedClassesAsync(
        int eventId, CancellationToken ct = default) =>
        (await _db.HotelCoverageStandards
            .AsNoTracking()
            .Where(s => s.EventId == eventId)
            .Select(s => s.Class)
            .ToListAsync(ct))
        .ToHashSet();

    /// <summary>Set one class's standard for an edition (upsert).</summary>
    public async Task SetStandardAsync(
        int eventId, HotelCoverageClass cls, HotelCoverageMode mode, int nights,
        string? byEmail, CancellationToken ct = default)
    {
        var row = await _db.HotelCoverageStandards
            .FirstOrDefaultAsync(s => s.EventId == eventId && s.Class == cls, ct);

        if (row is null)
        {
            row = new HotelCoverageStandard { EventId = eventId, Class = cls };
            _db.HotelCoverageStandards.Add(row);
        }

        row.Mode = mode;
        // Clamped, never rejected: a negative night count is a typo, and refusing the whole save
        // would lose the mode change the operator actually came for.
        row.Nights = mode == HotelCoverageMode.Nights ? Math.Clamp(nights, 0, 60) : 0;
        row.UpdatedAt = _clock.GetUtcNow();
        row.UpdatedByEmail = byEmail;

        await _db.SaveChangesAsync(ct);
    }

    // ---------------------------------------------------------------------
    //  The per-person extra
    // ---------------------------------------------------------------------

    /// <summary>This person's extra nights (0 when no row exists).</summary>
    public async Task<int> GetExtraNightsAsync(
        int eventId, int participantId, CancellationToken ct = default) =>
        await _db.HotelCoverageExtras
            .AsNoTracking()
            .Where(x => x.EventId == eventId && x.ParticipantId == participantId)
            .Select(x => (int?)x.ExtraNights)
            .FirstOrDefaultAsync(ct) ?? 0;

    /// <summary>
    /// Set one person's extra nights (upsert). Zero DELETES the row rather than storing a 0, so
    /// "has a row" keeps meaning "was granted something".
    /// </summary>
    public async Task SetExtraNightsAsync(
        int eventId, int participantId, int extraNights, string? note, string? byEmail,
        CancellationToken ct = default)
    {
        var extra = Math.Clamp(extraNights, 0, 60);

        var row = await _db.HotelCoverageExtras
            .FirstOrDefaultAsync(x => x.EventId == eventId && x.ParticipantId == participantId, ct);

        if (extra == 0 && string.IsNullOrWhiteSpace(note))
        {
            if (row is not null) _db.HotelCoverageExtras.Remove(row);
            await _db.SaveChangesAsync(ct);
            return;
        }

        if (row is null)
        {
            row = new HotelCoverageExtra { EventId = eventId, ParticipantId = participantId };
            _db.HotelCoverageExtras.Add(row);
        }

        row.ExtraNights = extra;
        row.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        row.UpdatedAt = _clock.GetUtcNow();
        row.UpdatedByEmail = byEmail;

        await _db.SaveChangesAsync(ct);
    }

    // ---------------------------------------------------------------------
    //  Resolving people
    // ---------------------------------------------------------------------

    /// <summary>
    /// Resolve ONE participant. Reads their role, speaker category, presenting days, booked nights
    /// and extra — so a caller that has none of those in hand can still ask the question.
    /// </summary>
    public async Task<ParticipantHotelCoverage> ResolveAsync(
        int eventId, int participantId, CancellationToken ct = default)
    {
        var p = await _db.Participants
            .AsNoTracking()
            .Where(x => x.Id == participantId && x.EventId == eventId)
            .Select(x => new { x.Id, x.Role })
            .FirstOrDefaultAsync(ct);

        if (p is null)
        {
            return new ParticipantHotelCoverage(
                participantId, null, HotelCoverageRule.Nothing,
                HotelCoverage.Resolve(HotelCoverageRule.Nothing, 0, 0));
        }

        var category = await _db.SpeakerProfiles
            .AsNoTracking()
            .Where(s => s.EventId == eventId && s.ParticipantId == participantId)
            .Select(s => s.Category)
            .FirstOrDefaultAsync(ct);

        var days = p.Role == ParticipantRole.Speaker
            ? await SpeakerDayScope.DaysForSpeakerAsync(_db, eventId, participantId, ct)
            : SpeakerDays.None;

        var booked = await BookedNightsAsync(eventId, participantId, ct);
        var extra = await GetExtraNightsAsync(eventId, participantId, ct);
        var standards = await GetStandardsAsync(eventId, ct);

        return Build(p.Id, p.Role, category, days, standards, extra, booked);
    }

    /// <summary>
    /// Resolve MANY participants in one pass — the list surfaces (hotel assignments, the per-hotel
    /// Excel). Four queries regardless of how many people, rather than four per person.
    /// </summary>
    public async Task<IReadOnlyDictionary<int, ParticipantHotelCoverage>> ResolveManyAsync(
        int eventId, IReadOnlyCollection<int> participantIds, CancellationToken ct = default)
    {
        if (participantIds.Count == 0) return new Dictionary<int, ParticipantHotelCoverage>();

        var ids = participantIds.Distinct().ToList();

        var people = await _db.Participants
            .AsNoTracking()
            .Where(p => p.EventId == eventId && ids.Contains(p.Id))
            .Select(p => new { p.Id, p.Role })
            .ToListAsync(ct);

        var categories = await _db.SpeakerProfiles
            .AsNoTracking()
            .Where(s => s.EventId == eventId && ids.Contains(s.ParticipantId))
            .Select(s => new { s.ParticipantId, s.Category })
            .ToDictionaryAsync(s => s.ParticipantId, s => s.Category, ct);

        var daysBySpeaker = await SpeakerDayScope.DaysBySpeakerAsync(_db, eventId, ct);

        var extras = await _db.HotelCoverageExtras
            .AsNoTracking()
            .Where(x => x.EventId == eventId && ids.Contains(x.ParticipantId))
            .ToDictionaryAsync(x => x.ParticipantId, x => x.ExtraNights, ct);

        var bookings = await _db.HotelBookings
            .AsNoTracking()
            .Where(b => b.EventId == eventId && ids.Contains(b.ParticipantId) && b.NeedsRoom)
            .Select(b => new { b.ParticipantId, b.CheckInDate, b.CheckOutDate })
            .ToListAsync(ct);
        var nights = bookings.ToDictionary(
            b => b.ParticipantId, b => HotelCoverage.NightsBetween(b.CheckInDate, b.CheckOutDate));

        var standards = await GetStandardsAsync(eventId, ct);

        return people.ToDictionary(
            p => p.Id,
            p => Build(
                p.Id, p.Role,
                categories.TryGetValue(p.Id, out var c) ? c : null,
                daysBySpeaker.TryGetValue(p.Id, out var d) ? d : SpeakerDays.None,
                standards,
                extras.TryGetValue(p.Id, out var e) ? e : 0,
                nights.TryGetValue(p.Id, out var n) ? n : 0));
    }

    /// <summary>Booked nights for one person — 0 when they need no room or have no dates.</summary>
    public async Task<int> BookedNightsAsync(
        int eventId, int participantId, CancellationToken ct = default)
    {
        var b = await _db.HotelBookings
            .AsNoTracking()
            .Where(x => x.EventId == eventId && x.ParticipantId == participantId && x.NeedsRoom)
            .Select(x => new { x.CheckInDate, x.CheckOutDate })
            .FirstOrDefaultAsync(ct);

        return b is null ? 0 : HotelCoverage.NightsBetween(b.CheckInDate, b.CheckOutDate);
    }

    /// <summary>
    /// The pure assembly step, exposed so a caller that ALREADY has the parts (the hotel form knows
    /// the dates the person is typing right now) can resolve without touching the database.
    /// </summary>
    public static ParticipantHotelCoverage Build(
        int participantId,
        ParticipantRole role,
        SpeakerCategory? category,
        SpeakerDays days,
        IReadOnlyDictionary<HotelCoverageClass, HotelCoverageRule> standards,
        int extraNights,
        int bookedNights)
    {
        var cls = HotelCoverage.ClassFor(role, category, days);

        var rule = cls is HotelCoverageClass c && standards.TryGetValue(c, out var r)
            ? r
            : HotelCoverageRule.Nothing;

        return new ParticipantHotelCoverage(
            participantId, cls, rule, HotelCoverage.Resolve(rule, extraNights, bookedNights));
    }
}
