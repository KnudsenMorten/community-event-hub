namespace CommunityHub.Core.Integrations;

/// <summary>One post the planner wants to exist, with the slot it should occupy.</summary>
/// <param name="Kind">Which of the five types.</param>
/// <param name="SubjectKey">
/// Stable identity of what the post is ABOUT — a track name, a session id, a sponsor company id, a
/// tier. Together with <see cref="Occurrence"/> it is what makes planning idempotent.
/// </param>
/// <param name="Occurrence">1-based: the 1st or the 2nd time this subject is announced.</param>
/// <param name="ScheduledAtUtc">The chosen slot, in UTC.</param>
public sealed record PlannedSoMePost(
    SoMeTemplateKind Kind,
    string SubjectKey,
    int Occurrence,
    DateTimeOffset ScheduledAtUtc);

/// <summary>Something the planner can announce, and how many times it should be announced.</summary>
/// <param name="Kind">Post type.</param>
/// <param name="SubjectKey">Stable id of the subject.</param>
/// <param name="Occurrences">How many posts this subject gets (his §824.1 schedule).</param>
/// <param name="EarliestUtc">
/// Not before this — e.g. a sponsor-speaker session cannot be announced before the sponsor has named
/// their speaker. Null means "as soon as the plan allows".
/// </param>
/// <param name="PromptFromUtc">
/// §851 — when THIS SUBJECT itself became ready, as opposed to a floor that applies to its whole
/// type. Only a subject with this set is announced PROMPTLY (§843.6); everything else is spread
/// across the window (§848.1).
///
/// <para>🔴 The distinction is not academic. §851's speaker gate puts <see cref="EarliestUtc"/> at
/// 7 Sep 2026 for <b>every</b> track and session at once. Treating that as "these just became ready"
/// would fire the promptness rule for all 29 of them and pile the entire speaker campaign into the
/// week after the gate — the §842.4 clustering defect, reintroduced through a different door.</para>
///
/// <para>⇒ <b>A type-wide floor spreads; an individual arrival hurries.</b> A sponsor whose graphic
/// was built yesterday is one subject becoming ready and should go out now; a date on which a whole
/// category unlocks is simply the start of that category's window.</para>
/// </param>
public sealed record SoMeSubject(
    SoMeTemplateKind Kind,
    string SubjectKey,
    int Occurrences,
    DateTimeOffset? EarliestUtc = null,
    DateTimeOffset? PromptFromUtc = null);

/// <summary>
/// §824.2E — decides WHEN each post goes out. Pure: no database, no clock of its own, no randomness
/// that varies between runs.
/// </summary>
/// <remarks>
/// <para>🔒 <b>Deterministic by construction, and that is the whole design.</b> The scheduler runs on
/// a timer, so it re-plans repeatedly. With a real <c>Random</c>, every run would move every
/// not-yet-published post to a different day — the queue would never settle, and a post he had read
/// and approved yesterday would be somewhere else today. Instead the slot is derived from a stable
/// hash of <c>(SubjectKey, Occurrence)</c>: still spread out and unpredictable-looking, but the SAME
/// answer every time, so re-planning is a no-op rather than a reshuffle.</para>
///
/// <para><b>His two rules, which are not in conflict once read properly</b> (§824.8 Q3): *"randomize
/// schedule time between 8-16:00, week-days"* is a GUARD RAIL — <i>"it was just so scheduler doesn't
/// post at 22:15"</i> — and *"randomize time to either 11:00 and 14:30"* is the PREFERENCE. So: pick
/// one of the two preferred times, on a weekday, and both sit inside the guard rail.</para>
///
/// <para>Times are chosen in <b>Danish local time</b> (§824.2F) and returned in UTC, so a post lands
/// at 11:00 Copenhagen whether or not the country is on summer time.</para>
/// </remarks>
public static class SoMeSchedulePlanner
{
    /// <summary>
    /// The preferred times of day, Danish local (§824.2E) — <b>his four real ELDK26 slots</b>.
    /// </summary>
    /// <remarks>
    /// 🔑 §845.2 — READ OFF THE ACTUAL ELDK26 MEDIA PLAN (243 posts), not chosen. By volume he used
    /// <b>09:00 (74 posts), 13:00 (74), 11:00 (31) and 14:30 (20)</b>.
    ///
    /// <para>🔴 <b>13:00 was one of his two most-used slots and was missing entirely</b> — the array
    /// held 11:00 and 14:30, plus 09:00 added in §842.7. A campaign planned without it would have
    /// avoided half of his real posting rhythm.</para>
    ///
    /// <para>§845.3 — four times also lets <c>MaxPostsPerDay</c> reach 4, which he did on 9 days of
    /// ELDK26 (and 3 on 29 days). Every entry sits inside the 08:00–16:00 guard rail.</para>
    ///
    /// <para>⚠️ Changing this array does NOT re-time anything already scheduled: stored
    /// <c>ScheduledAtUtc</c> values are untouched, and the planner picks a time only for a NEW post.</para>
    /// </remarks>
    public static readonly TimeOnly[] PreferredTimes =
        { new(9, 0), new(11, 0), new(13, 0), new(14, 30) };

    /// <summary>
    /// §843.6 — how long after a subject becomes ANNOUNCEABLE its first post may still sit at the
    /// normal rhythm before the exception ceiling is tried instead.
    /// </summary>
    /// <remarks>
    /// 🔑 Operator 2026-08-05: <i>"technically there are no delays after a sponsor signed, got
    /// onboarded, uploaded logos, graphics are build - then some publish can happen immediately
    /// after"</i> — so this is deliberately SHORT. It is not a tolerance he asked for; it is the
    /// slack needed to land on a weekday inside his posting times without thrashing the calendar.
    ///
    /// ⚠️ A sponsor waiting 61 days (§843.2) is not "slightly stale" — it is wrong, and this is what
    /// makes the exception reachable for exactly that case.
    /// </remarks>
    public const int FreshnessDays = 7;

    /// <summary>The guard rail he described: nothing outside this, ever.</summary>
    public static readonly TimeOnly EarliestTime = new(8, 0);
    public static readonly TimeOnly LatestTime = new(16, 0);

    /// <summary>
    /// Danish local time. IANA first (works cross-platform on .NET with ICU), Windows id as a
    /// fallback so a host without ICU data does not throw at scheduling time.
    /// </summary>
    public static TimeZoneInfo DanishTime
    {
        get
        {
            foreach (var id in new[] { "Europe/Copenhagen", "Romance Standard Time" })
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
                catch (TimeZoneNotFoundException) { }
                catch (InvalidTimeZoneException) { }
            }
            // 🔒 UTC rather than a throw: a missing timezone database must not stop the queue being
            // planned. An hour's drift on a marketing post is recoverable; a scheduler that dies is
            // an absence, and an absence is what nobody notices (§326cd).
            return TimeZoneInfo.Utc;
        }
    }

    /// <summary>
    /// Plan the posts that do not exist yet.
    /// </summary>
    /// <param name="subjects">Everything announceable, with how many posts each should get.</param>
    /// <param name="existing">
    /// Posts that already exist, <b>with the slots they occupy</b>.
    /// </param>
    /// <remarks>
    /// 🔒 <b>The times are part of this input, not just the keys.</b> The first version took only
    /// <c>(SubjectKey, Occurrence)</c>, and a test that added one sponsor to an existing plan caught
    /// what that costs: the planner skipped re-planning the old posts but had no idea WHEN they were,
    /// so a new arrival could be dropped straight on top of one already in the queue. Stability comes
    /// from persistence — an existing post is never re-planned — and that only works if the planner
    /// can see what persistence already decided.
    /// </remarks>
    /// <param name="fromUtc">Nothing is scheduled before this (normally "now").</param>
    /// <param name="eventStartUtc">The event itself — nothing is scheduled on or after it.</param>
    /// <param name="normalPerDay">
    /// 🔑 §843.3 — <b>THE EVERYDAY RHYTHM</b>, not a ceiling. Defaults to 2.
    /// </param>
    /// <param name="exceptionPerDay">
    /// 🔑 §843.3 — the ceiling used ONLY for posts that cannot fit at <paramref name="normalPerDay"/>.
    /// Null (the default) means "no exceptions" and the normal rhythm is also the hard cap.
    ///
    /// <para>⚠️ <b>Why two numbers and not one.</b> Operator 2026-08-05: <i>"i am worried that the
    /// planner will post 3 by default … we had a few days with exceptions as we ended with more posts
    /// than we had capacity for"</i>. Measured against the real 236-post campaign, a single cap
    /// becomes the rhythm: at 2 <b>100%</b> of days held exactly 2, and at 3 <b>81%</b> held 3.
    /// Raising one number therefore does not buy "occasional 3" — it changes what the page looks
    /// like every single day, which is a §843.1 attendee-experience change rather than a capacity
    /// tweak. His ELDK26 exceptions were days they had run OUT of room, not a chosen cadence.</para>
    /// </param>
    public static IReadOnlyList<PlannedSoMePost> Plan(
        IEnumerable<SoMeSubject> subjects,
        IReadOnlyCollection<(string SubjectKey, int Occurrence, DateTimeOffset ScheduledAtUtc)> existing,
        DateTimeOffset fromUtc,
        DateTimeOffset eventStartUtc,
        int normalPerDay = 2,
        int? exceptionPerDay = null)
    {
        ArgumentNullException.ThrowIfNull(subjects);
        ArgumentNullException.ThrowIfNull(existing);

        var already = existing.Select(e => (e.SubjectKey, e.Occurrence)).ToHashSet();

        var wanted = new List<(SoMeSubject Subject, int Occurrence)>();
        foreach (var s in subjects)
        {
            for (var n = 1; n <= Math.Max(0, s.Occurrences); n++)
            {
                if (!already.Contains((s.SubjectKey, n))) wanted.Add((s, n));
            }
        }

        // 🔑 §842.4 — THE TYPES ARE INTERLEAVED, NOT GROUPED. Operator 2026-08-05: "goal is to have
        // full control of when,what comes on linkedin so we mix speaker,sponsor,event messages and
        // also mix times".
        //
        // 🔴 This was `.OrderBy((int)Kind)`, and MEASURED against the real 112-post plan it produced
        // a badly clustered campaign: August held all 16 track posts, all 13 session posts and all 8
        // tier posts; September held all 29 sponsor posts; October–January held nothing but event
        // posts. Placement fills forward from the earliest free day, so ordering by Kind handed the
        // early weeks to whichever type happened to sort first.
        //
        // Each item instead gets a FRACTIONAL POSITION within its own kind — (i + ½) / count — so
        // every type stretches across the WHOLE campaign regardless of how many it has, and 30
        // sponsors therefore interleave with 13 sessions instead of following them. Ordering WITHIN
        // a kind is untouched, so §824.1's master-classes-before-technical-sessions still holds.
        // Deterministic: no clock, no randomness, same input ⇒ same plan.
        var kindCounts = wanted
            .GroupBy(w => w.Subject.Kind)
            .ToDictionary(g => g.Key, g => g.Count());

        var seen = new Dictionary<SoMeTemplateKind, int>();

        wanted = wanted
            .Select(w =>
            {
                var i = seen.GetValueOrDefault(w.Subject.Kind);
                seen[w.Subject.Kind] = i + 1;
                return (Item: w, Spread: (i + 0.5) / kindCounts[w.Subject.Kind]);
            })
            .OrderBy(x => x.Spread)
            // A subject with a hard earliest date still wins its tie, so a constrained one is never
            // pushed out by a free one. The rest is a stable tiebreak.
            .ThenBy(x => x.Item.Subject.EarliestUtc ?? DateTimeOffset.MinValue)
            .ThenBy(x => (int)x.Item.Subject.Kind)
            .ThenBy(x => x.Item.Occurrence)
            .ThenBy(x => x.Item.Subject.SubjectKey, StringComparer.Ordinal)
            .Select(x => x.Item)
            .ToList();

        // Seed the occupancy from what is ALREADY in the queue, so a new arrival is placed around
        // the existing posts instead of on top of one.
        var used = new Dictionary<DateOnly, int>();
        var taken = new HashSet<DateTimeOffset>();
        foreach (var e in existing)
        {
            taken.Add(e.ScheduledAtUtc);
            var d = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(e.ScheduledAtUtc, DanishTime).DateTime);
            used[d] = used.GetValueOrDefault(d) + 1;
        }

        var planned = new List<PlannedSoMePost>();

        // 🔑 §843.3 — TWO PASSES, so an exception is EARNED rather than available.
        //
        // Pass 1 places everything it can at the NORMAL rhythm. Only what is left over — the posts
        // that genuinely could not fit — gets a second pass at the higher ceiling. That reproduces
        // the ELDK26 shape (mostly normal days, a few busier ones) instead of flattening the whole
        // campaign to the cap, which is exactly what one number did: measured at 236 posts, a cap of
        // 3 put 3 posts on 81% of days.
        var leftovers = new List<(SoMeSubject Subject, int Occurrence)>();

        // 🔑 §848.1 — SPREAD ACROSS THE WHOLE PERIOD, BY DEFAULT.
        //
        // Operator 2026-08-05: "it is important that you spread out things in the whole period -
        // scheduler service must do that by default". Placing each post in the EARLIEST free slot
        // front-loads the campaign: measured on the live plan, August held 45 posts and September 38
        // while October–January carried only event posts.
        //
        // Each item is instead given a TARGET DATE from its position in the interleaved order,
        // spread evenly across the window, and placement starts looking from there. The spread value
        // is already computed above for the type mix, so the same number does both jobs: 0.0 lands
        // near the start of the window, 1.0 near the end.
        var windowStart = fromUtc;
        var windowDays = Math.Max(1, (eventStartUtc - windowStart).TotalDays);
        var index = 0;
        var count = Math.Max(1, wanted.Count);

        foreach (var (subject, occurrence) in wanted)
        {
            var earliest = subject.EarliestUtc is { } e && e > fromUtc ? e : fromUtc;

            // Where this post WANTS to sit, before availability is considered.
            var target = windowStart.AddDays(windowDays * index / count);
            index++;

            // 🔒 A LATE ARRIVAL IS NOT SPREAD — it goes promptly.
            //
            // §843.6: "no delays after a sponsor signed, got onboarded, uploaded logos, graphics are
            // build". A subject that becomes announceable DURING the campaign has already waited for
            // its prerequisites; pushing it further out to hit a spread target would reintroduce
            // exactly the lag §843.2 measured at 61 days.
            //
            // ⚠️ §851 — this reads PromptFromUtc, NOT EarliestUtc. A type-wide floor (the speaker
            // gate) sets EarliestUtc for every track and session at once, and treating that as "they
            // just became ready" would hurry all 29 into the week after the gate. An individual
            // arrival hurries; a category unlocking simply starts its window.
            var isLateArrival = subject.PromptFromUtc is { } ready && ready > fromUtc;

            var searchFrom = isLateArrival
                ? earliest
                : (target > earliest ? target : earliest);

            var slot = FindSlot(subject.SubjectKey, occurrence, searchFrom, eventStartUtc, used, taken, normalPerDay);

            // ⚠️ If nothing is free from the target onwards, fall back to searching from the
            // subject's own earliest date. Spreading is a PREFERENCE; placing the post at all is the
            // requirement, and a sponsor is contractual (§842.5).
            slot ??= FindSlot(subject.SubjectKey, occurrence, earliest, eventStartUtc, used, taken, normalPerDay);

            if (slot is null)
            {
                leftovers.Add((subject, occurrence));
                continue;
            }

            // 🔑 §843.6 — A LEFTOVER IS ALSO A POST THE NORMAL RHYTHM CAN ONLY PLACE FAR TOO LATE,
            // not merely one it cannot place at all.
            //
            // Operator 2026-08-05: "technically there are no delays after a sponsor signed, got
            // onboarded, uploaded logos, graphics are build - then some publish can happen
            // immediately after". Once a subject is READY there is no tolerated lag — so a slot
            // that sits more than FreshnessDays after it became announceable is treated as a miss
            // and retried at the exception ceiling.
            //
            // ⚠️ Without this the exception was unreachable for the case that needed it most: the
            // §843.2 sponsor who signed on 15 Nov and was placeable — just 61 days later — never
            // became a leftover, so the ceiling was never consulted (§843.5).
            //
            // 🔒 ONLY FOR A SUBJECT THAT BECOMES ANNOUNCEABLE *DURING* THE CAMPAIGN — its earliest
            // date must be in the FUTURE relative to this run.
            //
            // 🔴 Measured on the real PROD campaign, and this condition is why: without it, every
            // sponsor whose signup date sits in the PAST (the whole opening backlog) had its
            // earliest clamped to "now", so ANY slot more than a week out counted as stale. The
            // entire backlog went to the exception pass and the campaign came back with 9 days of
            // FOUR posts and 13 of three — the opposite of §843.3's "normal is 2".
            //
            // 🔑 His promptness rule is about a NEW arrival — "no delays after a sponsor signed, got
            // onboarded, uploaded logos, graphics are build" — not about the founding backlog, which
            // the campaign is deliberately spreading over months.
            if (subject.PromptFromUtc is { } readyAt
                && readyAt > fromUtc
                && slot.Value - earliest > TimeSpan.FromDays(FreshnessDays))
            {
                leftovers.Add((subject, occurrence));
                continue;
            }

            Place(subject, occurrence, slot.Value);
        }

        // Pass 2: the exception. Skipped entirely when no ceiling is offered, so "no exceptions"
        // stays expressible and the default behaviour is unchanged.
        var stillUnplaced = new List<(SoMeSubject Subject, int Occurrence)>();

        foreach (var (subject, occurrence) in leftovers)
        {
            var earliest = subject.EarliestUtc is { } e && e > fromUtc ? e : fromUtc;

            DateTimeOffset? slot = null;
            if (exceptionPerDay is { } ceiling && ceiling > normalPerDay)
            {
                slot = FindSlot(subject.SubjectKey, occurrence, earliest, eventStartUtc, used, taken, ceiling);
            }

            if (slot is null) { stillUnplaced.Add((subject, occurrence)); continue; }

            Place(subject, occurrence, slot.Value);
        }

        // 🔴 Pass 3: LATE IS BETTER THAN NEVER.
        //
        // A caught regression, and the test that caught it is worth keeping: the §843.6 freshness
        // rule sends a post that can only be placed FAR OUT to the exception pass — but if there is
        // no exception ceiling, or the ceiling is full too, that post would simply be DROPPED. It
        // was placeable all along, just not promptly.
        //
        // 🔒 For a sponsor that is a §842.5 CONTRACT BREACH manufactured by a freshness preference,
        // which is indefensible: being announced late is a quality problem, being announced never is
        // a legal one. So anything still unplaced gets its original normal-rhythm slot back.
        foreach (var (subject, occurrence) in stillUnplaced)
        {
            var earliest = subject.EarliestUtc is { } e && e > fromUtc ? e : fromUtc;
            var slot = FindSlot(subject.SubjectKey, occurrence, earliest, eventStartUtc, used, taken, normalPerDay);
            if (slot is null) continue;   // genuinely no room before the event — the caller reports it

            Place(subject, occurrence, slot.Value);
        }

        return planned;

        void Place(SoMeSubject subject, int occurrence, DateTimeOffset slot)
        {
            taken.Add(slot);
            var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(slot, DanishTime).DateTime);
            used[day] = used.GetValueOrDefault(day) + 1;
            planned.Add(new PlannedSoMePost(subject.Kind, subject.SubjectKey, occurrence, slot));
        }
    }

    private static DateTimeOffset? FindSlot(
        string subjectKey, int occurrence,
        DateTimeOffset earliestUtc, DateTimeOffset eventStartUtc,
        Dictionary<DateOnly, int> used, HashSet<DateTimeOffset> taken, int maxPerDay)
    {
        // The subject's own stable hash decides where it starts looking and which time it prefers, so
        // two different sponsors do not queue up on the same morning just because they were added in
        // the same minute.
        var seed = StableHash($"{subjectKey}|{occurrence}");
        var startOffset = (int)(seed % 7);            // spread the starting day across a week
        var timeIndex = (int)((seed / 7) % (uint)PreferredTimes.Length);

        var firstDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(earliestUtc, DanishTime).DateTime);
        var lastDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(eventStartUtc, DanishTime).DateTime);

        for (var offset = startOffset; ; offset++)
        {
            var day = firstDay.AddDays(offset);
            if (day >= lastDay)
            {
                // Wrapped past the event without finding room. Try from the very first allowed day
                // once, in case the stable offset skipped over the only free days.
                if (startOffset == 0) return null;
                offset = -1; startOffset = 0;
                continue;
            }

            if (IsWeekend(day)) continue;
            if (used.GetValueOrDefault(day) >= maxPerDay) continue;

            // Prefer this subject's own time, then the other one — never a third time, and never
            // outside the guard rail.
            for (var i = 0; i < PreferredTimes.Length; i++)
            {
                var time = PreferredTimes[(timeIndex + i) % PreferredTimes.Length];
                if (time < EarliestTime || time > LatestTime) continue;

                var utc = ToUtc(day, time);
                if (utc < earliestUtc || utc >= eventStartUtc) continue;
                if (taken.Contains(utc)) continue;

                return utc;
            }
        }
    }

    /// <summary>Danish local wall-clock time to UTC, honouring summer time.</summary>
    /// <remarks>
    /// ⚠️ A DST spring-forward makes some wall-clock times NOT EXIST. `ConvertTimeToUtc` throws on
    /// those, so an invalid local time is nudged forward an hour rather than taking the whole
    /// planning run down for one unlucky day.
    /// </remarks>
    internal static DateTimeOffset ToUtc(DateOnly day, TimeOnly time)
    {
        var local = day.ToDateTime(time, DateTimeKind.Unspecified);
        if (DanishTime.IsInvalidTime(local)) local = local.AddHours(1);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, DanishTime), TimeSpan.Zero);
    }

    private static bool IsWeekend(DateOnly d) =>
        d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    /// <summary>
    /// FNV-1a. Deliberately NOT <see cref="string.GetHashCode()"/>: that is randomised per process in
    /// .NET, so the "stable" slot would change every time the app restarted — which is exactly the
    /// reshuffling this planner exists to avoid.
    /// </summary>
    internal static uint StableHash(string s)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var c in s) { hash ^= c; hash *= 16777619u; }
            return hash;
        }
    }
}
