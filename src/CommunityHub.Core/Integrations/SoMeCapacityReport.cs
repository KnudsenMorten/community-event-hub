using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Integrations;

/// <summary>One month's seats, and what is sitting in them.</summary>
public sealed record SoMeCapacityMonth(
    int Year, int Month, int PostingDays, int Seats, int Taken)
{
    public int Free => Math.Max(0, Seats - Taken);
    public bool Oversold => Taken > Seats;
    public string Label => new DateOnly(Year, Month, 1).ToString("MMM yyyy");
}

/// <summary>What one category still wants from the calendar.</summary>
public sealed record SoMeCapacityDemand(
    SoMeAnnouncementCategory Category, string Label,
    int Subjects, int Rounds, int Planned)
{
    /// <summary>Subjects × rounds — the posts this category needs in total.</summary>
    public int Wanted => Math.Max(0, Subjects) * Math.Max(0, Rounds);

    /// <summary>Still to be placed.</summary>
    public int Outstanding => Math.Max(0, Wanted - Planned);
}

/// <summary>The whole picture: seats, what holds them, and what still wants one.</summary>
public sealed record SoMeCapacityResult(
    DateOnly From, DateOnly To,
    int PostsPerDay, int PostingDays, int Seats, int Taken,
    IReadOnlyList<SoMeCapacityMonth> Months,
    IReadOnlyList<SoMeCapacityDemand> Demand)
{
    public int Outstanding => Demand.Sum(d => d.Outstanding);
    public int Free => Math.Max(0, Seats - Taken);
    public int Shortfall => Math.Max(0, Outstanding - Free);
    public bool Fits => Shortfall == 0;

    /// <summary>
    /// The posts-per-day that WOULD fit everything, so the answer is a number rather than "add more".
    /// </summary>
    /// <remarks>
    /// ⚠️ Capped at the number of preferred times: a further post would have to share a minute with
    /// another or break the 08:00–16:00 rule, and silently doing either is worse than saying it does
    /// not fit (§842.7).
    /// </remarks>
    public int PostsPerDayNeeded =>
        PostingDays <= 0
            ? PostsPerDay
            : Math.Min(
                SoMeSchedulePlanner.PreferredTimes.Length,
                (int)Math.Ceiling((double)(Taken + Outstanding) / PostingDays));

    /// <summary>True when even the maximum slots per day cannot hold the campaign.</summary>
    public bool NeedsMoreThanSlotsAllow =>
        PostingDays > 0
        && (double)(Taken + Outstanding) / PostingDays > SoMeSchedulePlanner.PreferredTimes.Length;
}

/// <summary>
/// 🔴 §1199 — CAPACITY vs. DEMAND. You cannot sell the same seat twice.
/// </summary>
/// <remarks>
/// <para>Operator 2026-09-12: <i>"i need to have a overview whre I can see the calculation between
/// capacity (available some post timeslot vs. planned /scheduled), otherwise i dont know if the
/// planner must be extended to more timeslots per day as example. we cannot sell the same seat twice
/// like an airplane company. we have capacity vs. requirement and cannot scehdule two at the same
/// time"</i>.</para>
///
/// <para>🔑 <b>The planner already knows when it runs out — it just never says so usefully.</b> A run
/// reports "no room 2" and names the subjects it could not place (§842.5), which tells him it failed
/// but not by how much, not where, and not what would fix it. This is the arithmetic behind that
/// number: seats, who holds them, who still wants one, and the posts-per-day that would make it fit.</para>
///
/// <para>⚠️ <b>A seat is a DAY × a TIME, and the constraints are already in the planner:</b> weekdays
/// only, the four preferred times, at most <c>MaxPostsPerDay</c> of them used, and nothing across the
/// §1179 holiday blackout. Counting anything else would produce a capacity the planner would refuse
/// to use.</para>
///
/// <para>🔒 <b>Forecast counts are the caller's</b> — he asked to model 42 sponsors and 66 sessions
/// before those numbers are final. Nothing is stored: this answers "what if", and a saved prediction
/// would become a second, stale copy of a number the data will soon hold for real.</para>
/// </remarks>
public sealed class SoMeCapacityReport
{
    private readonly CommunityHubDbContext _db;

    public SoMeCapacityReport(CommunityHubDbContext db) => _db = db;

    /// <summary>The counts to model with. Null = use what the edition actually holds today.</summary>
    public sealed record Forecast(
        int? Tracks = null, int? MasterClasses = null, int? TechnicalSessions = null,
        int? SponsorSessions = null, int? Tiers = null, int? Sponsors = null);

    public async Task<SoMeCapacityResult> BuildAsync(
        int eventId, DateOnly from, Forecast? forecast = null, CancellationToken ct = default)
    {
        forecast ??= new Forecast();

        var ev = await _db.Events
            .Where(e => e.Id == eventId)
            .Select(e => new { e.StartDate })
            .FirstOrDefaultAsync(ct);

        var to = ev?.StartDate ?? from.AddYears(1);

        var settings = await _db.SoMeSettings
            .Where(s => s.EventId == eventId)
            .Select(s => new { s.MaxPostsPerDay })
            .FirstOrDefaultAsync(ct);

        var perDay = Math.Clamp(
            settings?.MaxPostsPerDay ?? 2, 1, SoMeSchedulePlanner.PreferredTimes.Length);

        // --- the seats -------------------------------------------------------------------------
        // 🔑 Exactly the planner's own rules, or the number would describe a calendar it will not use.
        var days = new List<DateOnly>();
        for (var day = from; day < to; day = day.AddDays(1))
        {
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            if (SoMeBlackout.IsBlackedOut(day)) continue;
            days.Add(day);
        }

        // --- who holds one ---------------------------------------------------------------------
        // ⚠️ PUBLISHED posts count. §1144 treats them as obstacles because their slot is spent —
        // a seat cannot be sold twice, and that includes one already flown.
        var scheduled = await _db.SoMePosts
            .Where(p => p.EventId == eventId && !p.IsDeleted)
            .Select(p => p.ScheduledAtUtc)
            .ToListAsync(ct);

        var takenByDay = scheduled
            .Select(utc => DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTime(utc, SoMeSchedulePlanner.DanishTime).DateTime))
            .Where(d => d >= from && d < to)
            .GroupBy(d => d)
            .ToDictionary(g => g.Key, g => g.Count());

        var months = days
            .GroupBy(d => (d.Year, d.Month))
            .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month)
            .Select(g => new SoMeCapacityMonth(
                g.Key.Year, g.Key.Month,
                PostingDays: g.Count(),
                Seats: g.Count() * perDay,
                Taken: g.Sum(d => takenByDay.GetValueOrDefault(d))))
            .ToList();

        // --- who still wants one ---------------------------------------------------------------
        var rules = await new SoMeCategoryRules(_db).GetAllAsync(eventId, ct);
        var counts = await SubjectCountsAsync(eventId, ct);
        var plannedByCategory = await PlannedByCategoryAsync(eventId, ct);

        int Subjects(SoMeAnnouncementCategory c) => c switch
        {
            SoMeAnnouncementCategory.SpeakerTracks => forecast.Tracks ?? counts.Tracks,
            SoMeAnnouncementCategory.MasterClasses => forecast.MasterClasses ?? counts.MasterClasses,
            SoMeAnnouncementCategory.TechnicalSessions =>
                forecast.TechnicalSessions ?? counts.TechnicalSessions,
            SoMeAnnouncementCategory.SponsorSpeakerSessions =>
                forecast.SponsorSessions ?? counts.SponsorSessions,
            SoMeAnnouncementCategory.SponsorTiers => forecast.Tiers ?? counts.Tiers,
            SoMeAnnouncementCategory.Sponsors => forecast.Sponsors ?? counts.Sponsors,
            _ => counts.EventPosts,
        };

        var demand = SoMeCategoryRules.All
            .Select(c =>
            {
                var rule = rules.TryGetValue(c, out var r) ? r : null;
                var rounds = rule is { Enabled: true } ? Math.Max(1, rule.Rounds) : 0;

                // ⚠️ Type 5 is one post per dated run in his deck, not subjects × rounds.
                if (c == SoMeAnnouncementCategory.EventPosts) rounds = 1;

                return new SoMeCapacityDemand(
                    c, SoMeCategoryRules.Label(c),
                    Subjects(c), rounds, plannedByCategory.GetValueOrDefault(c));
            })
            .ToList();

        return new SoMeCapacityResult(
            from, to, perDay,
            PostingDays: days.Count,
            Seats: days.Count * perDay,
            Taken: takenByDay.Values.Sum(),
            months, demand);
    }

    private sealed record Counts(
        int Tracks, int MasterClasses, int TechnicalSessions, int SponsorSessions,
        int Tiers, int Sponsors, int EventPosts);

    /// <summary>
    /// What the edition holds TODAY, used wherever no forecast is given.
    /// </summary>
    /// <remarks>
    /// 🔒 Excluded sessions and test sponsors are left out, so the count matches what the planner
    /// would actually announce (§1178) rather than what the tables happen to contain.
    /// </remarks>
    private async Task<Counts> SubjectCountsAsync(int eventId, CancellationToken ct)
    {
        var excluded = await new SoMeSubjectScope(_db).ExcludedSessionIdsAsync(eventId, ct);

        var sessions = await _db.Sessions
            .Where(s => s.EventId == eventId && !s.IsServiceSession)
            .Select(s => new { s.Id, s.Type, s.Track })
            .ToListAsync(ct);

        var live = sessions.Where(s => !excluded.Contains(s.Id)).ToList();

        var testCompanies = await TestDataScope.TestSponsorCompanyIdsAsync(_db, eventId, ct);

        var sponsors = await _db.SponsorInfos
            .Where(s => s.EventId == eventId && s.SponsorCompanyId != null)
            .Select(s => new { s.SponsorCompanyId, s.SponsorPackage })
            .ToListAsync(ct);

        var realSponsors = sponsors
            .Where(s => !testCompanies.Contains(s.SponsorCompanyId!))
            .ToList();

        return new Counts(
            Tracks: live.Where(s => !string.IsNullOrWhiteSpace(s.Track))
                .Select(s => s.Track!).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            MasterClasses: live.Count(s => s.Type == SessionType.MasterClass),
            TechnicalSessions: live.Count(s =>
                s.Type is SessionType.TechnicalSession or SessionType.Keynote
                    or SessionType.PanelDiscussion),
            SponsorSessions: await _db.SponsorSessions.CountAsync(x => x.EventId == eventId, ct),
            Tiers: realSponsors.Select(s => s.SponsorPackage).Distinct().Count(),
            Sponsors: realSponsors.Select(s => s.SponsorCompanyId).Distinct().Count(),
            EventPosts: await _db.EventSoMePostOccurrences
                .CountAsync(o => o.Post.EventId == eventId, ct));
    }

    /// <summary>
    /// How many posts each category ALREADY has, so "outstanding" is what is genuinely left.
    /// </summary>
    /// <remarks>
    /// ⚠️ Type 2's three categories all write <c>TemplateKind.Session</c>, so they are separated by
    /// SUBJECT KEY — a sponsor speaker session carries the <c>sponsorsession:</c> prefix, and the
    /// rest are split by their session's type.
    /// </remarks>
    private async Task<Dictionary<SoMeAnnouncementCategory, int>> PlannedByCategoryAsync(
        int eventId, CancellationToken ct)
    {
        var posts = await _db.SoMePosts
            .Where(p => p.EventId == eventId && !p.IsDeleted)
            .Select(p => new { p.TemplateKind, p.SubjectKey })
            .ToListAsync(ct);

        var masterClassIds = await _db.Sessions
            .Where(s => s.EventId == eventId && s.Type == SessionType.MasterClass)
            .Select(s => s.Id)
            .ToListAsync(ct);

        var mcKeys = masterClassIds.Select(id => $"session:{id}").ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<SoMeAnnouncementCategory, int>();

        foreach (var p in posts)
        {
            var category = p.TemplateKind switch
            {
                SoMeTemplateKind.SpeakerTracks => SoMeAnnouncementCategory.SpeakerTracks,
                SoMeTemplateKind.SponsorCategory => SoMeAnnouncementCategory.SponsorTiers,
                SoMeTemplateKind.Sponsor => SoMeAnnouncementCategory.Sponsors,
                SoMeTemplateKind.EventPost => SoMeAnnouncementCategory.EventPosts,
                SoMeTemplateKind.Session when p.SubjectKey is { } k
                    && k.StartsWith(SoMeSponsorSessionKey.Prefix, StringComparison.OrdinalIgnoreCase)
                    => SoMeAnnouncementCategory.SponsorSpeakerSessions,
                SoMeTemplateKind.Session when p.SubjectKey is { } k && mcKeys.Contains(k)
                    => SoMeAnnouncementCategory.MasterClasses,
                SoMeTemplateKind.Session => SoMeAnnouncementCategory.TechnicalSessions,
                _ => (SoMeAnnouncementCategory?)null,
            };

            if (category is { } c) result[c] = result.GetValueOrDefault(c) + 1;
        }

        return result;
    }
}
