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
        => MapLength(startsAt, endsAt, null);

    /// <summary>
    /// Derive the default <see cref="SessionLength"/> for an imported session. Prefers the
    /// scheduled start/end (rounded to the nearest 20/50/60 bucket; ≥ 4 h ⇒ Full day). When the
    /// times are absent — which is the norm until the organizer PUBLISHES the Sessionize schedule
    /// grid (the API returns empty <c>startsAt</c>/<c>endsAt</c> for unscheduled sessions) — fall
    /// back to the source FORMAT label: a duration written into the label (e.g. "Technical Session
    /// (60 min)", "(50 min)") wins, and a "Master Class"/"workshop" format maps to
    /// <see cref="SessionLength.FullDay"/>. Only when neither a time nor a label hint exists does it
    /// default to <see cref="SessionLength.SixtyMin"/>.
    /// </summary>
    public static SessionLength MapLength(
        DateTimeOffset? startsAt, DateTimeOffset? endsAt, string? category)
    {
        if (startsAt is null || endsAt is null) return LengthFromCategory(category);

        var minutes = (endsAt.Value - startsAt.Value).TotalMinutes;
        if (minutes <= 0) return LengthFromCategory(category);

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
    /// Length hint from the source FORMAT/category label when no usable start/end exists.
    /// A "(NN min)" duration written in the label maps to the nearest 20/50/60 bucket; a
    /// "master class"/"masterclass"/"workshop" format is a full day. No hint ⇒ the safe
    /// <see cref="SessionLength.SixtyMin"/> default (the historic untimed-talk fallback).
    /// </summary>
    private static SessionLength LengthFromCategory(string? category)
    {
        if (string.IsNullOrWhiteSpace(category)) return SessionLength.SixtyMin;

        var c = category.ToLowerInvariant();
        if (c.Contains("master class") || c.Contains("masterclass") || c.Contains("workshop"))
            return SessionLength.FullDay;

        // A duration written into the format label, e.g. "Technical Session (60 min)" or
        // "Technical Session (morning 07:20-08:10 - 50 min)". Take the LAST "<n> min" match so a
        // label that also contains a clock range ("07:20-08:10") doesn't capture the wrong number.
        var matches = System.Text.RegularExpressions.Regex.Matches(c, @"(\d{1,3})\s*min");
        if (matches.Count > 0
            && int.TryParse(matches[^1].Groups[1].Value, out var minutes)
            && minutes > 0)
        {
            if (minutes >= FullDayThresholdMinutes) return SessionLength.FullDay;
            var candidates = new[] { SessionLength.TwentyMin, SessionLength.FiftyMin, SessionLength.SixtyMin };
            var best = candidates[0];
            var bestDelta = double.MaxValue;
            foreach (var cand in candidates)
            {
                var delta = Math.Abs(minutes - (int)cand);
                if (delta < bestDelta) { bestDelta = delta; best = cand; }
            }
            return best;
        }

        return SessionLength.SixtyMin;
    }

    /// <summary>
    /// §154: the session's NUMERIC length in minutes, for display ("60 min") and the
    /// new <c>Session.LengthMinutes</c> column. Prefers the scheduled start/end when
    /// the grid is published; otherwise parses a "(NN min)" duration out of the source
    /// Format label (e.g. "Technical Session (60 min)" → 60). Returns <c>null</c> when
    /// neither a usable time nor a "(NN min)" hint exists — e.g. a "Master Class" with
    /// no minutes, which is a full-day handled via the <see cref="SessionLength"/>
    /// bucket rather than a numeric figure.
    /// </summary>
    public static int? MapLengthMinutes(
        DateTimeOffset? startsAt, DateTimeOffset? endsAt, string? category)
    {
        if (startsAt is { } start && endsAt is { } end)
        {
            var minutes = (end - start).TotalMinutes;
            if (minutes > 0) return (int)Math.Round(minutes);
        }
        return ParseLengthMinutes(category);
    }

    /// <summary>
    /// §154: parse a "(NN min)" duration out of the source Format label, returning the
    /// numeric minutes or <c>null</c> when the label has no such hint. Takes the LAST
    /// "<n> min" match so a label that also contains a clock range ("07:20-08:10")
    /// doesn't capture the wrong number (same rule as <see cref="LengthFromCategory"/>).
    /// A "Master Class"/"workshop" with no "(NN min)" → null (full-day, not a figure).
    /// </summary>
    public static int? ParseLengthMinutes(string? category)
    {
        if (string.IsNullOrWhiteSpace(category)) return null;

        var matches = System.Text.RegularExpressions.Regex.Matches(
            category.ToLowerInvariant(), @"(\d{1,3})\s*min");
        if (matches.Count > 0
            && int.TryParse(matches[^1].Groups[1].Value, out var minutes)
            && minutes > 0)
        {
            return minutes;
        }
        return null;
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
