using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// Maps an imported Sessionize / Backstage session to sensible <see cref="SessionType"/>
/// and <see cref="SessionLength"/> defaults (REQUIREMENTS § session type + length).
/// Imported sessions carry no explicit hub type/length, so we derive them from the
/// source CATEGORY / FORMAT label (when present) and the duration (start/end).
/// Hub-added sessions set these explicitly and never go through here.
///
/// Pure + static so it is unit-testable without a network call or DB. The mapping is
/// a documented heuristic, not a guess hidden in the importer:
///  - <b>Length</b>: round the start→end minutes to the nearest supported bucket
///    (20 / 50 / 60). A session at or beyond a half-day (≥ 4 h) — or with no times —
///    maps to <see cref="SessionLength.FullDay"/> only via the master-class path;
///    otherwise an untimed session defaults to <see cref="SessionLength.SixtyMin"/>.
///  - <b>Type</b>: derived from the source category / format label first
///    (contains "master class"/"masterclass"/"workshop" → MasterClass; "keynote" →
///    Keynote; "ask the experts" → AskTheExperts; "panel" → PanelDiscussion;
///    "welcome" → Welcome); when no label matches, a full-day-length session is a
///    <see cref="SessionType.MasterClass"/> and everything else defaults to
///    <see cref="SessionType.TechnicalSession"/>. The importer never infers
///    <see cref="SessionType.Other"/> — that is a neutral fallback only.
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
    /// Derive the default <see cref="SessionType"/> from an imported session's source
    /// category / format label, falling back to the duration when no label matches.
    /// A full-day length (with no recognised label) implies a master class; everything
    /// else is a regular technical session. The importer never assigns
    /// <see cref="SessionType.Other"/>.
    /// </summary>
    /// <param name="category">
    /// The Sessionize category / Backstage format label, when present (e.g.
    /// "Master Class", "Keynote", "Ask the Experts", "Panel"). Null/blank → derive
    /// from length only (the pre-existing duration fallback).
    /// </param>
    /// <param name="length">The mapped <see cref="SessionLength"/> (duration fallback).</param>
    public static SessionType MapType(string? category, SessionLength length)
    {
        if (!string.IsNullOrWhiteSpace(category))
        {
            var c = category.Trim().ToLowerInvariant();
            if (c.Contains("master class") || c.Contains("masterclass") || c.Contains("workshop"))
                return SessionType.MasterClass;
            if (c.Contains("keynote"))
                return SessionType.Keynote;
            if (c.Contains("ask the expert"))
                return SessionType.AskTheExperts;
            if (c.Contains("panel"))
                return SessionType.PanelDiscussion;
            if (c.Contains("welcome"))
                return SessionType.Welcome;
            // A recognised category that isn't one of the special kinds is a
            // regular technical session (NOT Other).
            return SessionType.TechnicalSession;
        }

        // No label → keep the historic duration fallback.
        return length == SessionLength.FullDay
            ? SessionType.MasterClass
            : SessionType.TechnicalSession;
    }

    /// <summary>
    /// Back-compat duration-only overload: derive the default type from length alone.
    /// A full-day length implies a master class; everything else is a technical session.
    /// </summary>
    public static SessionType MapType(SessionLength length) => MapType(null, length);
}
