using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Entitlements;

/// <summary>
/// Which days a speaker PRESENTS, derived from their linked sessions (§299 C5 —
/// the stored <see cref="SpeakerProfile.SpeakingPreDay"/> /
/// <see cref="SpeakerProfile.SpeakingMainDay"/> flags are retired from logic;
/// the schedule is the single source of truth). Loaded via
/// <see cref="SpeakerDayScope.DaysBySpeakerAsync"/>.
/// </summary>
public sealed record SpeakerDays(bool PresentsPreDay, bool PresentsMainDay)
{
    /// <summary>A speaker with no linked sessions (presents on no day).</summary>
    public static readonly SpeakerDays None = new(false, false);

    /// <summary>
    /// §299 6.3 — the number of DISTINCT days this speaker presents on (0, 1 or
    /// 2). Drives the ELDK-funded polo count and the Community funded hotel
    /// nights; multiple same-day sessions still count 1 for that day.
    /// </summary>
    public int DistinctFundedDays => (PresentsPreDay ? 1 : 0) + (PresentsMainDay ? 1 : 0);
}

/// <summary>
/// §296/§299 6.3 — ELDK-funded speaker quantities are linked to WHICH DAYS a speaker PRESENTS,
/// derived from their linked sessions (never the retired <see cref="SpeakerProfile.SpeakingPreDay"/>
/// flag). A session counts for the PRE-day when it is a <see cref="SessionType.MasterClass"/> or is
/// scheduled on <see cref="Event.PreDayDate"/>; it counts for the MAIN day when it is any
/// non-MasterClass session (unscheduled, or scheduled within
/// <see cref="Event.StartDate"/>..<see cref="Event.EndDate"/>). Test sessions
/// (<see cref="Session.UsedForTesting"/>) still count — they are real rehearsal content — but
/// service sessions (<see cref="Session.IsServiceSession"/>, breaks/lunch) never do.
///
/// <para>Funding by <see cref="SpeakerCategory"/> (§299 6.3): Community + Guest are ELDK-funded
/// (polos = distinct presenting days); a Guest's hotel nights are ORGANIZER-ENTERED
/// (<see cref="SpeakerProfile.GuestFundedNights"/>, individual agreement); Sponsor = nothing; an
/// UNCATEGORIZED (null) speaker is excluded from every tally.</para>
/// </summary>
public static class SpeakerDayScope
{
    /// <summary>
    /// Participant ids of speakers who present at least one MasterClass session in the edition.
    /// SUPERSEDED by <see cref="DaysBySpeakerAsync"/> (which also derives the pre-day from a
    /// scheduled <see cref="Event.PreDayDate"/> session) — kept for callers that only need the
    /// strict "presents a master class" set.
    /// </summary>
    public static async Task<HashSet<int>> MasterClassSpeakerIdsAsync(
        CommunityHubDbContext db, int eventId, CancellationToken ct = default) =>
        (await db.Sessions
            .Where(s => s.EventId == eventId && s.Type == SessionType.MasterClass)
            .SelectMany(s => s.SessionSpeakers.Select(ss => ss.ParticipantId))
            .Distinct()
            .ToListAsync(ct))
            .ToHashSet();

    /// <summary>True when this participant presents at least one MasterClass session.</summary>
    public static Task<bool> IsMasterClassSpeakerAsync(
        CommunityHubDbContext db, int eventId, int participantId, CancellationToken ct = default) =>
        db.Sessions.AnyAsync(s => s.EventId == eventId
            && s.Type == SessionType.MasterClass
            && s.SessionSpeakers.Any(ss => ss.ParticipantId == participantId), ct);

    /// <summary>
    /// §299 C5 — the derived presenting days for EVERY speaker with at least one linked session in
    /// the edition (participant id → <see cref="SpeakerDays"/>). A speaker with no linked sessions
    /// has no entry — treat a miss as <see cref="SpeakerDays.None"/>.
    /// </summary>
    public static async Task<IReadOnlyDictionary<int, SpeakerDays>> DaysBySpeakerAsync(
        CommunityHubDbContext db, int eventId, CancellationToken ct = default)
    {
        var evt = await db.Events.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == eventId, ct);
        if (evt is null) return new Dictionary<int, SpeakerDays>();

        // Service sessions (breaks/lunch) never count; UsedForTesting sessions DO
        // (they are real rehearsal content). Computed in memory — the date rules
        // are trivial CLR logic.
        var links = await db.Sessions
            .Where(s => s.EventId == eventId && !s.IsServiceSession)
            .SelectMany(s => s.SessionSpeakers.Select(ss =>
                new { ss.ParticipantId, s.Type, s.StartsAt }))
            .ToListAsync(ct);

        return links
            .GroupBy(l => l.ParticipantId)
            .ToDictionary(
                g => g.Key,
                g => new SpeakerDays(
                    g.Any(l => CountsAsPreDay(l.Type, l.StartsAt, evt.PreDayDate)),
                    g.Any(l => CountsAsMainDay(l.Type, l.StartsAt, evt.StartDate, evt.EndDate))));
    }

    /// <summary>
    /// The derived presenting days for ONE speaker (<see cref="SpeakerDays.None"/> when they have
    /// no linked sessions). Same rules as <see cref="DaysBySpeakerAsync"/>, loaded per participant
    /// for the single-person gates (forms, deadlines).
    /// </summary>
    public static async Task<SpeakerDays> DaysForSpeakerAsync(
        CommunityHubDbContext db, int eventId, int participantId, CancellationToken ct = default)
    {
        var evt = await db.Events.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == eventId, ct);
        if (evt is null) return SpeakerDays.None;

        var links = await db.Sessions
            .Where(s => s.EventId == eventId
                        && !s.IsServiceSession
                        && s.SessionSpeakers.Any(ss => ss.ParticipantId == participantId))
            .Select(s => new { s.Type, s.StartsAt })
            .ToListAsync(ct);

        return new SpeakerDays(
            links.Any(l => CountsAsPreDay(l.Type, l.StartsAt, evt.PreDayDate)),
            links.Any(l => CountsAsMainDay(l.Type, l.StartsAt, evt.StartDate, evt.EndDate)));
    }

    /// <summary>
    /// §299 6.3 — ELDK-funded polo count for a speaker: Community + Guest get one polo per distinct
    /// presenting day (0 when no linked sessions); Sponsor and uncategorized (null) get none.
    /// </summary>
    public static int FundedPolos(SpeakerCategory? category, SpeakerDays days) =>
        SpeakerProfile.EldkFunded(category) ? days.DistinctFundedDays : 0;

    /// <summary>
    /// §299 6.3 — ELDK-funded hotel nights for a speaker: Community = matches the polo rule (one
    /// night per distinct presenting day); Guest = the ORGANIZER-ENTERED
    /// <paramref name="guestFundedNights"/> (individual agreement, 0 until entered); Sponsor and
    /// uncategorized (null) = 0.
    /// </summary>
    public static int FundedHotelNights(SpeakerCategory? category, SpeakerDays days, int? guestFundedNights) =>
        category switch
        {
            SpeakerCategory.Community => days.DistinctFundedDays,
            SpeakerCategory.Guest => guestFundedNights ?? 0,
            _ => 0,
        };

    // A session counts as PRE-day when it is a MasterClass (pre-day = the master-class day) or is
    // scheduled on the event's PreDayDate (a non-MC session placed on the pre-day still counts).
    private static bool CountsAsPreDay(SessionType type, DateTimeOffset? startsAt, DateOnly? preDayDate) =>
        type == SessionType.MasterClass
        || (preDayDate is not null
            && startsAt is not null
            && DateOnly.FromDateTime(startsAt.Value.Date) == preDayDate.Value);

    // A non-MasterClass session counts as MAIN day when it is unscheduled (typed only — assume the
    // main conference) or scheduled within StartDate..EndDate. A non-MC session scheduled on the
    // pre-day counts as pre-day (above), not main day.
    private static bool CountsAsMainDay(
        SessionType type, DateTimeOffset? startsAt, DateOnly start, DateOnly end)
    {
        if (type == SessionType.MasterClass) return false;
        if (startsAt is null) return true;
        var d = DateOnly.FromDateTime(startsAt.Value.Date);
        return d >= start && d <= end;
    }
}
