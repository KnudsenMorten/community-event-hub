using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Integrations;

/// <summary>One category's window for one round, already resolved to instants.</summary>
/// <param name="OpensUtc">Not before this. Null = as soon as the subject itself is ready.</param>
/// <param name="ClosesUtc">Not after this.</param>
public readonly record struct SoMeRoundWindow(DateTimeOffset? OpensUtc, DateTimeOffset ClosesUtc);

/// <summary>
/// 🔴 §1187 — THE RULES, IN ONE PLACE, READ THE SAME WAY BY EVERYONE.
/// </summary>
/// <remarks>
/// <para>Operator 2026-09-12: <i>"i think we need to add all the rules inside the some settings as it
/// is becoming too complex"</i> · <i>"basically we define the rules like start date, end date,
/// cadence inside the some settings and the some planner must recalculate if they are changed"</i>.</para>
///
/// <para>🔑 <b>This is the §1178 lesson built into a shape rather than remembered.</b> That morning
/// cost a day because one flag had four readers and no writer; the afternoon added ten date columns,
/// each read in one place and each one a chance to add an eleventh. One table, one reader, one
/// writer — and a category that has a rule cannot have the planner ignoring it.</para>
/// </remarks>
public sealed class SoMeCategoryRules
{
    private readonly CommunityHubDbContext _db;

    public SoMeCategoryRules(CommunityHubDbContext db) => _db = db;

    /// <summary>Every category the operator can schedule. Event posts are last: end date only.</summary>
    public static readonly IReadOnlyList<SoMeAnnouncementCategory> All =
    [
        SoMeAnnouncementCategory.SpeakerTracks,
        SoMeAnnouncementCategory.MasterClasses,
        SoMeAnnouncementCategory.TechnicalSessions,
        SoMeAnnouncementCategory.SponsorSpeakerSessions,
        SoMeAnnouncementCategory.SponsorTiers,
        SoMeAnnouncementCategory.Sponsors,
        SoMeAnnouncementCategory.EventPosts,
    ];

    /// <summary>His words for each category, used on the page and in the run messages.</summary>
    /// <remarks>
    /// 🔑 §1187 — operator 2026-09-12: <i>"consider to add sponsor speaker + master class as type
    /// 1a,b,c"</i>. The three Type 2 categories are LETTERED rather than all reading "Type 2": they
    /// are one template kind and three schedules, and three identical labels made the page ambiguous
    /// at exactly the point where you pick which one to change.
    /// </remarks>
    public static string Label(SoMeAnnouncementCategory c) => c switch
    {
        SoMeAnnouncementCategory.SpeakerTracks => "Type 1 — speaker tracks",
        SoMeAnnouncementCategory.MasterClasses => "Type 2a — master classes",
        SoMeAnnouncementCategory.TechnicalSessions => "Type 2b — technical sessions, keynotes and panels",
        SoMeAnnouncementCategory.SponsorSpeakerSessions => "Type 2c — sponsor speaker sessions",
        SoMeAnnouncementCategory.SponsorTiers => "Type 3 — sponsor tiers",
        SoMeAnnouncementCategory.Sponsors => "Type 4 — sponsors",
        _ => "Type 5 — event posts",
    };

    /// <summary>A one-line description of what each category covers, shown under its label.</summary>
    public static string Describes(SoMeAnnouncementCategory c) => c switch
    {
        SoMeAnnouncementCategory.SpeakerTracks =>
            "One post per track, naming that track's speakers. Waits for the line-up to settle.",
        SoMeAnnouncementCategory.MasterClasses =>
            "The confirmed master classes, announced together.",
        SoMeAnnouncementCategory.TechnicalSessions =>
            "Every other session on the programme. Ask-the-Experts sessions are never announced.",
        SoMeAnnouncementCategory.SponsorSpeakerSessions =>
            "A session a sponsor brings their own speaker to. Waits for them to name that speaker.",
        SoMeAnnouncementCategory.SponsorTiers =>
            "One post per tier. Waits for every sponsor in the tier to send their text and logo.",
        SoMeAnnouncementCategory.Sponsors =>
            "One post per sponsor company. Waits for their logo and promotion graphic.",
        _ => "Dates come from your imported deck and are used as written — only the end date applies.",
    };

    /// <summary>
    /// The shipped round count for a category, used when an edition has never saved a rule.
    /// </summary>
    /// <remarks>
    /// ⚠️ Sponsor speaker sessions are TWO where an ordinary session is one — that difference was
    /// previously a <c>Math.Max(sessionTimes, 2)</c> buried in the planner, which is exactly the kind
    /// of rule he can now see.
    /// </remarks>
    public static int DefaultRounds(SoMeAnnouncementCategory c) => c switch
    {
        SoMeAnnouncementCategory.SpeakerTracks => 3,
        SoMeAnnouncementCategory.MasterClasses => 1,
        SoMeAnnouncementCategory.TechnicalSessions => 1,
        SoMeAnnouncementCategory.SponsorSpeakerSessions => 2,
        SoMeAnnouncementCategory.SponsorTiers => 2,
        SoMeAnnouncementCategory.Sponsors => 2,
        _ => 1,
    };

    /// <summary>
    /// §1187 — every category's rule, creating the rows from the edition's EXISTING values the first
    /// time it is asked.
    /// </summary>
    /// <remarks>
    /// <para>🔒 <b>Seeded rather than migrated in SQL, and that is deliberate.</b> A data migration
    /// would have to reproduce the ten columns' fallback logic in hand-written SQL against a live
    /// database, with no test covering it. Seeding on first read runs the SAME C# the planner uses,
    /// is idempotent, and is exercised by every test that touches a rule.</para>
    ///
    /// <para>⚠️ <b>Nothing has to be re-entered.</b> Each category takes its round count from the old
    /// cadence row, its start from that category's old "from" column, and its end from the last round
    /// date where one was set.</para>
    /// </remarks>
    public async Task<IReadOnlyDictionary<SoMeAnnouncementCategory, SoMeCategoryRule>> GetAllAsync(
        int eventId, CancellationToken ct = default)
    {
        var rows = await _db.SoMeCategoryRules
            .Where(r => r.EventId == eventId)
            .ToListAsync(ct);

        var missing = All.Where(c => rows.TrueForAll(r => r.Category != c)).ToList();
        if (missing.Count > 0)
        {
            var seeded = await SeedAsync(eventId, missing, ct);
            rows.AddRange(seeded);
        }

        return rows.ToDictionary(r => r.Category);
    }

    /// <summary>
    /// §1195 — the dated rounds, per category: <c>category → (round → day)</c>.
    /// </summary>
    /// <remarks>
    /// ⚠️ Only rounds he has actually dated appear. An absent round is not "today" — it spreads, and
    /// <see cref="Windows"/> is the one place that decides what that means.
    /// </remarks>
    public async Task<IReadOnlyDictionary<SoMeAnnouncementCategory, IReadOnlyDictionary<int, DateOnly>>>
        RoundStartsAsync(int eventId, CancellationToken ct = default)
    {
        var rows = await _db.SoMeCategoryRounds
            .Where(r => r.EventId == eventId && r.StartsOn != null)
            .ToListAsync(ct);

        return rows
            .GroupBy(r => r.Category)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyDictionary<int, DateOnly>)g
                    .GroupBy(r => r.RoundNumber)
                    .ToDictionary(r => r.Key, r => r.First().StartsOn!.Value));
    }

    private async Task<List<SoMeCategoryRule>> SeedAsync(
        int eventId, IReadOnlyList<SoMeAnnouncementCategory> categories, CancellationToken ct)
    {
        var s = await _db.SoMeSettings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.EventId == eventId, ct);

        var cadence = await _db.SoMeCadenceSettings.AsNoTracking()
            .Where(c => c.EventId == eventId)
            .ToListAsync(ct);

        int RoundsFor(SoMeAnnouncementCategory c, SoMeTemplateKind kind)
        {
            var row = cadence.Find(x => x.Kind == kind);
            if (row is null) return DefaultRounds(c);
            return row.Enabled ? row.Occurrences : 0;
        }

        var created = new List<SoMeCategoryRule>();

        foreach (var category in categories)
        {
            // 🔑 Each category's OLD columns, so an edition that has been configured keeps its dates.
            var (rounds, startsOn, endsOn) = category switch
            {
                SoMeAnnouncementCategory.SpeakerTracks => (
                    RoundsFor(category, SoMeTemplateKind.SpeakerTracks),
                    s?.SpeakerAnnouncementFrom,
                    s?.SpeakerTracksRound3From ?? s?.SpeakerTracksRound2From),

                SoMeAnnouncementCategory.MasterClasses => (
                    RoundsFor(category, SoMeTemplateKind.Session),
                    s?.MasterClassAnnouncementFrom,
                    (DateOnly?)null),

                SoMeAnnouncementCategory.TechnicalSessions => (
                    RoundsFor(category, SoMeTemplateKind.Session),
                    s?.SessionAnnouncementFrom,
                    (DateOnly?)null),

                // ⚠️ Never had a column: its two rounds and its "-21 days" window were both hardcoded.
                SoMeAnnouncementCategory.SponsorSpeakerSessions => (
                    DefaultRounds(category), (DateOnly?)null, (DateOnly?)null),

                SoMeAnnouncementCategory.SponsorTiers => (
                    RoundsFor(category, SoMeTemplateKind.SponsorCategory),
                    s?.SponsorCategoryRound1From ?? s?.SponsorAnnouncementFrom,
                    s?.SponsorCategoryRound2From),

                SoMeAnnouncementCategory.Sponsors => (
                    RoundsFor(category, SoMeTemplateKind.Sponsor),
                    s?.SponsorAnnouncementFrom,
                    s?.SponsorRound2From),

                _ => (1, (DateOnly?)null, s?.EventPostWindowEndsOn),
            };

            var rule = new SoMeCategoryRule
            {
                EventId = eventId,
                Category = category,
                Enabled = rounds > 0,
                // 🔒 Clamped to at least 1: `Enabled` is how a category is turned off, so a 0 here
                // would be a second, silent way of saying it — and the two would drift.
                Rounds = Math.Max(1, rounds),
                StartsOn = startsOn,
                EndsOn = endsOn,
            };

            _db.SoMeCategoryRules.Add(rule);
            created.Add(rule);

            // 🔴 §1195 — SEED THE ROUNDS HE HAS ALREADY DATED, exactly. This is what keeps his
            // 28 Sep / 1 Dec / 15 Jan from being re-derived into something near-but-not-those.
            foreach (var (round, day) in RoundSeeds(category, s))
            {
                _db.SoMeCategoryRounds.Add(new SoMeCategoryRound
                {
                    EventId = eventId, Category = category, RoundNumber = round, StartsOn = day,
                });
            }
        }

        await _db.SaveChangesAsync(ct);
        return created;
    }

    /// <summary>
    /// §1195 — save one category's rule: how many rounds, when each starts, and when it closes.
    /// </summary>
    /// <remarks>
    /// <para>🔑 <b>Adding a round is just a bigger <paramref name="rounds"/>.</b> Operator
    /// 2026-09-12: <i>"where is the button to add a round if needed"</i> · <i>"when a some post has
    /// more rounds, it should be added into the planner and planned"</i>. The planner reads this
    /// number, so the next run plans the new round with nothing else to change.</para>
    ///
    /// <para>⚠️ Round rows BEYOND the new count are deleted, so lowering the number and raising it
    /// again does not resurrect a date he removed. The rule row itself is never deleted — an edition
    /// always has a rule, and `Enabled` is how a category is switched off.</para>
    /// </remarks>
    public async Task SaveAsync(
        int eventId, SoMeAnnouncementCategory category, bool enabled, int rounds,
        DateOnly? endsOn, IReadOnlyDictionary<int, DateOnly?> roundStarts,
        string? byEmail, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(roundStarts);

        // Ensures the row exists (and seeds the others) before it is edited.
        await GetAllAsync(eventId, ct);

        var rule = await _db.SoMeCategoryRules
            .FirstAsync(r => r.EventId == eventId && r.Category == category, ct);

        rule.Enabled = enabled;
        rule.Rounds = Math.Clamp(rounds, 1, MaxRounds);
        rule.EndsOn = endsOn;
        rule.UpdatedAt = DateTimeOffset.UtcNow;
        rule.LastUpdatedByEmail = byEmail;

        var existing = await _db.SoMeCategoryRounds
            .Where(r => r.EventId == eventId && r.Category == category)
            .ToListAsync(ct);

        foreach (var (round, day) in roundStarts)
        {
            if (round < 1 || round > rule.Rounds) continue;

            var row = existing.Find(r => r.RoundNumber == round);
            if (row is null)
            {
                _db.SoMeCategoryRounds.Add(new SoMeCategoryRound
                {
                    EventId = eventId, Category = category, RoundNumber = round, StartsOn = day,
                });
            }
            else
            {
                // Blanking a round date IS meaningful — it hands that round back to the spread.
                row.StartsOn = day;
            }
        }

        // ⚠️ Rounds that no longer exist take their dates with them.
        foreach (var orphan in existing.Where(r => r.RoundNumber > rule.Rounds))
        {
            _db.SoMeCategoryRounds.Remove(orphan);
        }

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>The per-round dates an edition already holds, in its old columns.</summary>
    private static IEnumerable<(int Round, DateOnly Day)> RoundSeeds(
        SoMeAnnouncementCategory category, Domain.SoMeSettings? s)
    {
        if (s is null) yield break;

        switch (category)
        {
            case SoMeAnnouncementCategory.SpeakerTracks:
                if (s.SpeakerAnnouncementFrom is { } t1) yield return (1, t1);
                if (s.SpeakerTracksRound2From is { } t2) yield return (2, t2);
                if (s.SpeakerTracksRound3From is { } t3) yield return (3, t3);
                break;

            case SoMeAnnouncementCategory.MasterClasses:
                if (s.MasterClassAnnouncementFrom is { } mc) yield return (1, mc);
                break;

            case SoMeAnnouncementCategory.TechnicalSessions:
                if (s.SessionAnnouncementFrom is { } ts) yield return (1, ts);
                break;

            case SoMeAnnouncementCategory.SponsorTiers:
                if ((s.SponsorCategoryRound1From ?? s.SponsorAnnouncementFrom) is { } c1)
                    yield return (1, c1);
                if (s.SponsorCategoryRound2From is { } c2) yield return (2, c2);
                break;

            case SoMeAnnouncementCategory.Sponsors:
                if (s.SponsorAnnouncementFrom is { } sp1) yield return (1, sp1);
                if (s.SponsorRound2From is { } sp2) yield return (2, sp2);
                break;
        }
    }

    /// <summary>
    /// §1187 — where each round of a category opens and closes.
    /// </summary>
    /// <remarks>
    /// <para>🔑 <b>Round <i>i</i> of <i>n</i> opens at <c>start + (i-1)·(end-start)/n</c>.</b> Three
    /// rounds between late September and the event give an announcement, a reminder and a final push
    /// without anyone naming three dates — which is the simplification he asked for.</para>
    ///
    /// <para>⚠️ A missing start is NOT treated as "now": it means the category has no floor and each
    /// subject goes as soon as it is ready (§920). Only the END is always resolved, because every
    /// post must land before the event whether or not he has said so.</para>
    ///
    /// <para>🔒 Monotonic by construction — round <i>i+1</i> never opens before round <i>i</i>.</para>
    /// </remarks>
    /// <summary>
    /// 🔴 §1207 — HOW MANY ROUNDS THIS CATEGORY REALLY HAS: the number he set, or the highest round
    /// he has DATED, whichever is larger.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-09-12: <i>"if i define 3 rounds, we must plan 3 rounds"</i> — reported as
    /// a bug, and it was one.</para>
    ///
    /// <para>🔴 <b>The failure, exactly.</b> §1195 seeds a new rule's <c>Rounds</c> from the OLD
    /// posting-frequency table (<c>SoMeCadenceSettings.Occurrences</c>) so a configured edition keeps
    /// its choice — while <c>RoundSeeds</c> seeds the round DATES from the settings columns,
    /// <c>SpeakerTracksRound3From</c> among them. On an edition whose frequency page still said
    /// <b>2</b>, that produced a rule with <c>Rounds = 2</c> AND a <c>SoMeCategoryRound</c> row for
    /// round <b>3</b> carrying his mid-January date. <see cref="Windows"/> iterated 1..Rounds, so
    /// round 3 was dropped: <b>a date he had entered, stored, displayed — and never planned.</b></para>
    ///
    /// <para>🔑 <b>The rule is now stated in one line instead of relying on two numbers agreeing.</b>
    /// Dating a round IS asking for it, so the count can never be lower than the rounds that have
    /// dates. §854's shape: the alternative was a warning telling him the two settings disagree,
    /// which makes the contradiction his problem to resolve on every edit.</para>
    ///
    /// <para>⚠️ It cannot get stuck high. <see cref="SaveAsync"/> DELETES round rows above the saved
    /// count, so lowering the number takes the extra dates with it and this returns the lower number
    /// on the very next read.</para>
    ///
    /// <para>🔒 A disabled category is still 0 — <c>Enabled</c> is how a category is switched off, and
    /// a date must not resurrect one he has turned off.</para>
    /// </remarks>
    public static int EffectiveRounds(
        SoMeCategoryRule rule, IReadOnlyDictionary<int, DateOnly>? roundStarts)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (!rule.Enabled) return 0;

        var dated = roundStarts is { Count: > 0 } ? roundStarts.Keys.Max() : 0;
        return Math.Clamp(Math.Max(rule.Rounds, dated), 0, MaxRounds);
    }

    /// <summary>The ceiling <see cref="SaveAsync"/> clamps to, shared so the two cannot drift.</summary>
    public const int MaxRounds = 12;

    /// <summary>
    /// 🔴 §1209 — WHICH CATEGORY A POST BELONGS TO: the 2a / 2b / 2c answer.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-09-12: <i>"can you update the type column to reflect the 2a, 2b, 2c names
    /// instead if type = 2"</i>.</para>
    ///
    /// <para>🔑 <b>Three schedules wearing one label.</b> <c>SoMeTemplateKind.Session</c> is a single
    /// template kind, but §1187 split it into three CATEGORIES with three rules — master classes from
    /// 14 Sep, other technical sessions from 28 Sep, sponsor speaker sessions twice and no later than
    /// two weeks out. A queue that prints "Type 2" on all of them cannot be read against the settings
    /// page that governs them, which is where he goes to change the very dates he is looking at.</para>
    ///
    /// <para>⚠️ The kind alone cannot answer this — it takes the SUBJECT. A sponsor speaker session
    /// is a <c>sponsorsession:</c> key (not a Sessions row at all), and the split between 2a and 2b is
    /// the session's <see cref="SessionType"/>. Hence <paramref name="masterClassSessionIds"/>: the
    /// caller resolves it once for a whole page, never once per row (§889).</para>
    /// </remarks>
    public static SoMeAnnouncementCategory? CategoryOf(
        SoMeTemplateKind? kind, string? subjectKey, IReadOnlySet<int>? masterClassSessionIds = null)
    {
        switch (kind)
        {
            case SoMeTemplateKind.SpeakerTracks: return SoMeAnnouncementCategory.SpeakerTracks;
            case SoMeTemplateKind.SponsorCategory: return SoMeAnnouncementCategory.SponsorTiers;
            case SoMeTemplateKind.Sponsor: return SoMeAnnouncementCategory.Sponsors;
            case SoMeTemplateKind.EventPost: return SoMeAnnouncementCategory.EventPosts;
            case SoMeTemplateKind.Session: break;
            default: return null;   // ad-hoc: it belongs to no category, and must not claim one.
        }

        if (SoMeSponsorSessionKey.TryParse(subjectKey, out _))
        {
            return SoMeAnnouncementCategory.SponsorSpeakerSessions;
        }

        // 🔒 Unknown ⇒ technical sessions, which is what the planner does with any session that is
        // not a master class. Guessing 2a instead would name the one category with its own earlier
        // window, and send him to the wrong rule.
        if (masterClassSessionIds is { Count: > 0 }
            && subjectKey is not null
            && subjectKey.StartsWith(SessionKeyPrefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(subjectKey[SessionKeyPrefix.Length..], out var sessionId)
            && masterClassSessionIds.Contains(sessionId))
        {
            return SoMeAnnouncementCategory.MasterClasses;
        }

        return SoMeAnnouncementCategory.TechnicalSessions;
    }

    /// <summary>The planner's session subject prefix (<c>SoMeAnnouncementQuery.SessionPrefix</c>).</summary>
    private const string SessionKeyPrefix = "session:";

    public static IReadOnlyDictionary<int, SoMeRoundWindow> Windows(
        SoMeCategoryRule rule, DateTimeOffset eventStartUtc,
        IReadOnlyDictionary<int, DateOnly>? roundStarts = null)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var closes = rule.EndsOn is { } end
            ? SoMeSchedulePlanner.ToUtc(end, new TimeOnly(23, 59, 59))
            : eventStartUtc;

        // ⚠️ A closing date after the event is not usable — nothing may publish once it has started.
        if (closes > eventStartUtc) closes = eventStartUtc;

        // 🔴 §1207 — A DATED ROUND IS A ROUND. See EffectiveRounds: iterating rule.Rounds alone is
        // what let a round he had dated be silently dropped.
        var rounds = Math.Max(1, EffectiveRounds(rule, roundStarts));
        var result = new Dictionary<int, SoMeRoundWindow>(rounds);

        static DateTimeOffset At(DateOnly d) =>
            SoMeSchedulePlanner.ToUtc(d, SoMeSchedulePlanner.PreferredTimes[0]);

        // 🔴 §1195 — A ROUND HE HAS DATED USES THAT DATE. Spreading every round evenly between two
        // ends is tidy and it silently discards what he actually dictated: tracks at 28 Sep / 1 Dec /
        // 15 Jan would become 28 Sep / ~14 Nov / ~30 Dec, the last of which lands in the Christmas
        // blackout and moves again. He named those rounds; naming them has to mean something.
        //
        // 🔑 An UNDATED round still spreads — between the round before it and the category's close —
        // so "rounds + two ends" remains the model for anyone who does not want to pick every date.
        // 🔒 §908's fallback for the FINAL round, kept rather than replaced by a plain spread: "one
        // month before the event" is what the last round has always meant, and for a two-round
        // category this reproduces it exactly. Generalised, it is simply where the last reminder
        // goes when he has not said.
        var lastFallback = closes.AddMonths(-1);

        DateTimeOffset? previous = rule.StartsOn is { } s ? At(s) : null;

        for (var round = 1; round <= rounds; round++)
        {
            DateTimeOffset? opens = null;

            if (roundStarts is not null && roundStarts.TryGetValue(round, out var named))
            {
                opens = At(named);
            }
            else if (round == 1)
            {
                opens = rule.StartsOn is { } first ? At(first) : null;
            }
            else if (previous is { } prev)
            {
                // The last undated round goes to §908's month-out mark; the ones between are spread
                // evenly across the gap to it, so three rounds read as announcement / reminder /
                // final push rather than bunching.
                var gap = lastFallback - prev;
                opens = round == rounds || gap <= TimeSpan.Zero
                    ? lastFallback
                    : prev + TimeSpan.FromTicks(gap.Ticks / Math.Max(1, rounds - round + 1));
            }

            // 🔒 Monotonic, whatever is typed: a reminder dated before its own announcement is worse
            // than one with no date at all.
            if (opens is { } o && previous is { } p && o < p) opens = p;

            result[round] = new SoMeRoundWindow(opens, closes);
            if (opens is not null) previous = opens;
        }

        return result;
    }
}
