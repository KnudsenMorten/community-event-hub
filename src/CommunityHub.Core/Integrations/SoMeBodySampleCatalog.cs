namespace CommunityHub.Core.Integrations;

/// <summary>
/// §912 — the subject key for a SPONSOR SPEAKER SESSION, in one place.
/// </summary>
/// <remarks>
/// 🔴 <b>It cannot be <c>session:{id}</c>.</b> A sponsor speaker session is a
/// <see cref="Domain.SponsorSession"/> row, a different table with its OWN id space — so
/// <c>session:1</c> and a sponsor session with id 1 are different things entirely, and a numeric
/// parse alone would happily announce the wrong one. (Measured on PROD: <c>Sessions.Id = 1</c> is
/// "ELDK27 Welcome", while <c>SponsorSessions.Id = 1</c> is a sponsor's own talk.)
/// <para>🔒 Both the planner and the publisher route on this prefix, so they cannot drift into
/// disagreeing about what a key means.</para>
/// </remarks>
public static class SoMeSponsorSessionKey
{
    public const string Prefix = "sponsorsession:";

    public static string For(int sponsorSessionId) => $"{Prefix}{sponsorSessionId}";

    /// <summary>True (with the id) when this key names a sponsor speaker session.</summary>
    public static bool TryParse(string? subjectKey, out int sponsorSessionId)
    {
        sponsorSessionId = 0;
        return subjectKey is not null
               && subjectKey.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
               && int.TryParse(subjectKey[Prefix.Length..], out sponsorSessionId);
    }
}

/// <summary>
/// §908 — WHICH WORDING THIS POST GETS. Pure, and deliberately not random.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-06: <i>"a catalog of samples, which you can randomize to make new posts.
/// this way it will be a mix of many different wordings"</i>.</para>
///
/// <para>🔴 <b>"Randomize" here must NOT mean <see cref="Random"/>, and that is the one decision
/// this class exists to hold.</b> The planner discards and re-plans its un-accepted proposals on
/// every tick (§848.2, every 5 minutes). A real random draw would therefore re-word every
/// unapproved post continuously: he would read a post, leave it, come back and find it saying
/// something else — and the queue would never settle enough to approve. §824.2E already refused
/// randomness for the same reason when choosing DATES; this is the same rule applied to WORDS.</para>
///
/// <para>🔑 <b>Varied ACROSS posts, fixed WITHIN a post.</b> The index comes from a stable hash of
/// the subject and occurrence, so the Azure track's second post always draws the same wording while
/// the Identity track's first draws a different one. Re-planning is a no-op, exactly as it is for
/// the schedule. (Same property <see cref="Domain.SoMePost.ActionPhrase"/> gets by being stored —
/// here it comes free, because the input never changes.)</para>
///
/// <para>⚠️ The pool's ORDER is part of the answer, so the catalog is read
/// <see cref="Domain.SoMeBodySample.SortOrder"/>-first: re-importing the same file in the same
/// order leaves every existing post's wording untouched, while appending a wording only affects
/// posts whose hash lands on it.</para>
/// </remarks>
public static class SoMeBodySampleCatalog
{
    /// <summary>
    /// Pick the wording for one post, or null when the pool is empty (⇒ caller falls back to the
    /// single template body, which is the pre-§908 behaviour).
    /// </summary>
    /// <param name="bodies">The pool, already ordered by <c>SortOrder</c>.</param>
    /// <param name="subjectKey">The post's subject — a track, a session id, a sponsor.</param>
    /// <param name="occurrence">1st or 2nd announcement of that subject.</param>
    public static string? Pick(
        IReadOnlyList<string> bodies, string? subjectKey, int occurrence)
    {
        ArgumentNullException.ThrowIfNull(bodies);
        if (bodies.Count == 0) return null;

        // The same hash the scheduler uses to pick an hour (§824.2E), for the same reason: stable
        // across runs, across processes and across restarts. A .NET string hash is randomised per
        // process and would silently reshuffle every wording on every deploy.
        var index = (int)(SoMeSchedulePlanner.StableHash($"{subjectKey}|{occurrence}")
                          % (uint)bodies.Count);

        return bodies[index];
    }
}
