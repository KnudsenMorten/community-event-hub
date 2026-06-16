using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// Maps an imported Sessionize session to sensible <see cref="SessionType"/> and
/// <see cref="SessionLength"/> defaults (REQUIREMENTS § session type + length).
/// Imported sessions carry no explicit type/length, so we derive them from the
/// Sessionize duration (start/end) and service-session flag. Hub-added sessions set
/// these explicitly and never go through here.
///
/// Pure + static so it is unit-testable without a network call or DB. The mapping is
/// a documented heuristic, not a guess hidden in the importer:
///  - <b>Length</b>: round the start→end minutes to the nearest supported bucket
///    (20 / 50 / 60). A session at or beyond a half-day (≥ 4 h) — or with no times —
///    that is a master class maps to <see cref="SessionLength.FullDay"/>; otherwise an
///    untimed session defaults to <see cref="SessionLength.SixtyMin"/>.
///  - <b>Type</b>: a full-day-length session is a <see cref="SessionType.CommunityMasterClass"/>;
///    everything else defaults to <see cref="SessionType.CommunityTechSession"/>. The
///    importer never infers <see cref="SessionType.SponsorSession"/> — that is a
///    hub-added designation only.
/// </summary>
public static class SessionDefaultsMapper
{
    /// <summary>A session at/above this many minutes is treated as a full day.</summary>
    private const double FullDayThresholdMinutes = 4 * 60; // 4 hours

    /// <summary>
    /// Derive the default <see cref="SessionLength"/> for an imported session from its
    /// scheduled start/end. Null/zero/negative duration → <see cref="SessionLength.SixtyMin"/>
    /// (the safe default for an untimed talk).
    /// </summary>
    public static SessionLength MapLength(DateTimeOffset? startsAt, DateTimeOffset? endsAt)
    {
        if (startsAt is null || endsAt is null) return SessionLength.SixtyMin;

        var minutes = (endsAt.Value - startsAt.Value).TotalMinutes;
        if (minutes <= 0) return SessionLength.SixtyMin;

        if (minutes >= FullDayThresholdMinutes) return SessionLength.FullDay;

        // Nearest supported bucket among 20 / 50 / 60.
        var candidates = new[] { SessionLength.TwentyMin, SessionLength.FiftyMin, SessionLength.SixtyMin };
        var best = candidates[0];
        var bestDelta = double.MaxValue;
        foreach (var c in candidates)
        {
            var delta = Math.Abs(minutes - (int)c);
            if (delta < bestDelta)
            {
                bestDelta = delta;
                best = c;
            }
        }
        return best;
    }

    /// <summary>
    /// Derive the default <see cref="SessionType"/> for an imported session. A full-day
    /// length implies a master class; everything else is a regular community tech
    /// session. The importer never assigns <see cref="SessionType.SponsorSession"/>.
    /// </summary>
    public static SessionType MapType(SessionLength length) =>
        length == SessionLength.FullDay
            ? SessionType.CommunityMasterClass
            : SessionType.CommunityTechSession;
}
