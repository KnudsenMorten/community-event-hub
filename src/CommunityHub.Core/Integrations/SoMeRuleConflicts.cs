using System.Net;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations;

/// <summary>One broken planning rule, with the posts that break it.</summary>
/// <param name="Code">Stable identity of this exact conflict — the mail's de-duplication key.</param>
/// <param name="Rule">Which rule, in his words ("Publish-by date").</param>
/// <param name="Message">What is wrong, naming the subject and the dates.</param>
/// <param name="PostIds">The posts to open and move. Empty for a post that could not be placed at all.</param>
public sealed record SoMeRuleConflict(string Code, string Rule, string Message, IReadOnlyList<int> PostIds);

/// <summary>One post as the conflict check needs it.</summary>
public sealed record SoMeConflictPost(
    int Id, string SubjectKey, int Occurrence, SoMeAnnouncementCategory? Category,
    DateTimeOffset ScheduledAtUtc, bool Movable, string Label);

/// <summary>The rules the queue is checked against — the same values the planner reads.</summary>
public sealed record SoMeConflictRules(
    IReadOnlyDictionary<SoMeAnnouncementCategory, int> MinDaysBetweenRounds,
    IReadOnlyDictionary<SoMeAnnouncementCategory, IReadOnlyDictionary<int, DateOnly>> PublishBy,
    int? MaxSponsorPostsPerDay,
    int MaxPostsPerDay);

/// <summary>
/// 🔴 §1368 — CHECK THE QUEUE AGAINST THE PLANNING RULES, AND MAIL WHAT BREAKS THEM.
/// </summary>
/// <remarks>
/// <para>Operator 2026-10-26: <i>"we need to have an email if conflicts happens that breaks rules.
/// this will happen if we havent sold all sponsorship by dec 15, then i will move the dates"</i>.</para>
///
/// <para>🔑 <b>The planner relaxes a rule rather than drop a post</b> (§842.5: a sponsor is owed
/// two announcements), and he can move or approve any post by hand. Either way a rule can end up
/// broken in the queue with nobody told. So the check reads the QUEUE — approved, locked and
/// hand-moved posts included — not the planner's intentions.</para>
///
/// <para>🔒 Mailed once per distinct set of conflicts per day: a new conflict (or a moved post that
/// still conflicts) mails on the next tick; an unchanged set does not repeat every five minutes.</para>
/// </remarks>
public sealed class SoMeRuleConflictService
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;
    private readonly EngineAlertSender? _alerts;
    private readonly SoMeSubjectLabeller? _labeller;
    private readonly ILogger<SoMeRuleConflictService>? _log;
    private readonly string _hubUrl;

    public SoMeRuleConflictService(
        CommunityHubDbContext db, TimeProvider? clock = null, EngineAlertSender? alerts = null,
        SoMeSubjectLabeller? labeller = null, ILogger<SoMeRuleConflictService>? log = null,
        Microsoft.Extensions.Options.IOptions<EmailTemplateOptions>? branding = null)
    {
        _db = db;
        _clock = clock ?? TimeProvider.System;
        _alerts = alerts;
        _labeller = labeller;
        _log = log;
        _hubUrl = (branding?.Value.HubUrl ?? string.Empty).TrimEnd('/');
    }

    /// <summary>The conflicts in this edition's queue right now.</summary>
    public async Task<IReadOnlyList<SoMeRuleConflict>> FindAsync(
        int eventId, IReadOnlyCollection<string>? noRoom = null, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();

        var settings = await _db.SoMeSettings.AsNoTracking()
            .Where(s => s.EventId == eventId)
            .Select(s => new { s.MaxPostsPerDay, s.ExceptionPostsPerDay, s.MaxSponsorPostsPerDay })
            .FirstOrDefaultAsync(ct);

        var ruleSvc = new SoMeCategoryRules(_db);
        var rules = await ruleSvc.GetAllAsync(eventId, ct);
        var deadlines = await ruleSvc.RoundDeadlinesAsync(eventId, ct);

        var posts = await _db.SoMePosts.AsNoTracking()
            .Where(p => p.EventId == eventId && !p.IsDeleted
                        && p.SubjectKey != null && p.Occurrence != null && p.TemplateKind != null)
            .ToListAsync(ct);

        var masterClassIds = (await _db.Sessions
                .Where(s => s.EventId == eventId && s.Type == SessionType.MasterClass)
                .Select(s => s.Id)
                .ToListAsync(ct))
            .ToHashSet();

        IReadOnlyDictionary<int, string> labels = new Dictionary<int, string>();
        if (_labeller is not null)
        {
            try { labels = await _labeller.LabelsForAsync(eventId, posts, ct); }
            catch (Exception ex) { _log?.LogWarning(ex, "§1368: subject labels unavailable."); }
        }

        var input = posts.Select(p => new SoMeConflictPost(
                p.Id, p.SubjectKey!, p.Occurrence!.Value,
                SoMeCategoryRules.CategoryOf(p.TemplateKind, p.SubjectKey, masterClassIds),
                p.ScheduledAtUtc,
                Movable: p.Status == SoMePostStatus.Queued && p.PublishedAtUtc == null,
                Label: labels.TryGetValue(p.Id, out var l) && !string.IsNullOrWhiteSpace(l) ? l : p.SubjectKey!))
            .ToList();

        var normal = Math.Max(1, settings?.MaxPostsPerDay ?? 2);
        var conflictRules = new SoMeConflictRules(
            rules.Where(r => r.Value.MinDaysBetweenRounds is > 0)
                .ToDictionary(r => r.Key, r => r.Value.MinDaysBetweenRounds!.Value),
            deadlines,
            settings?.MaxSponsorPostsPerDay is > 0 and var sp ? sp : null,
            Math.Max(normal, settings?.ExceptionPostsPerDay ?? normal));

        // §1368 — "sponsor:36 #1" is an id, not something he can act on: name the subject where any of
        // its posts gives us a label ("Surveil (round 1)").
        var keyLabels = input
            .GroupBy(p => p.SubjectKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Label, StringComparer.Ordinal);
        var named = noRoom?.Select(item =>
        {
            var at = item.LastIndexOf(" #", StringComparison.Ordinal);
            return at > 0 && keyLabels.TryGetValue(item[..at], out var label) && label != item[..at]
                ? $"{label} (round {item[(at + 2)..]})"
                : item;
        }).ToList();

        return Find(input, conflictRules, now, named);
    }

    /// <summary>
    /// Find the conflicts, and mail them when the set is new today. Returns how many there are.
    /// </summary>
    public async Task<IReadOnlyList<SoMeRuleConflict>> CheckAndAlertAsync(
        int eventId, IReadOnlyCollection<string>? noRoom = null, CancellationToken ct = default)
    {
        var conflicts = await FindAsync(eventId, noRoom, ct);
        if (conflicts.Count == 0 || _alerts is null) return conflicts;

        _log?.LogWarning("§1368 SoMe rule conflicts: {Count} — {Items}", conflicts.Count,
            string.Join(" | ", conflicts.Take(20).Select(c => c.Message)));

        // 🔑 One mail per distinct set per day: the key changes when a conflict appears, disappears
        // or a post moves to a date that still conflicts — each of those is news.
        var fingerprint = SoMeSchedulePlanner.StableHash(
            string.Join("\n", conflicts.Select(c => c.Code).OrderBy(c => c, StringComparer.Ordinal)));
        var today = SoMeDisplayTime.ToDanish(_clock.GetUtcNow());
        var key = $"some-rule-conflicts:{eventId}:{today:yyyyMMdd}:{fingerprint:x8}";

        await _alerts.AlertAsync(
            $"SoMe planner: {conflicts.Count} post(s) break the planning rules",
            BuildBody(conflicts), ct,
            throttleKey: key,
            recipient: SoMeAutoApproveService.NoticeEmail);

        return conflicts;
    }

    /// <summary>The check itself. Pure: no database, no clock of its own.</summary>
    public static IReadOnlyList<SoMeRuleConflict> Find(
        IReadOnlyCollection<SoMeConflictPost> posts, SoMeConflictRules rules, DateTimeOffset nowUtc,
        IReadOnlyCollection<string>? noRoom = null)
    {
        ArgumentNullException.ThrowIfNull(posts);
        ArgumentNullException.ThrowIfNull(rules);

        var result = new List<SoMeRuleConflict>();
        static DateOnly DayOf(DateTimeOffset utc) => DateOnly.FromDateTime(SoMeDisplayTime.ToDanish(utc).DateTime);
        static string When(DateTimeOffset utc) => SoMeDisplayTime.ToDanish(utc).ToString("ddd d MMM HH:mm");

        // Only what can still be changed counts; a post already published is history.
        var upcoming = posts.Where(p => p.Movable && p.ScheduledAtUtc >= nowUtc).ToList();

        // 1 — a round published after its "publish by" date.
        foreach (var p in upcoming.OrderBy(p => p.ScheduledAtUtc))
        {
            if (p.Category is not { } c
                || !rules.PublishBy.TryGetValue(c, out var byRound)
                || !byRound.TryGetValue(p.Occurrence, out var by)) continue;

            if (p.ScheduledAtUtc <= SoMeCategoryRules.PublishByUtc(by)) continue;

            result.Add(new SoMeRuleConflict(
                $"by|{p.SubjectKey}|{p.Occurrence}|{p.ScheduledAtUtc.UtcTicks}",
                "Publish-by date",
                $"{p.Label} — round {p.Occurrence} is planned for {When(p.ScheduledAtUtc)}, after its "
                + $"publish-by date {by:d MMM yyyy} ({SoMeCategoryRules.Label(c)}).",
                [p.Id]));
        }

        // 2 — two rounds of one subject too close together, or in the wrong order.
        foreach (var subject in posts.GroupBy(p => p.SubjectKey, StringComparer.Ordinal))
        {
            var first = subject.First();
            if (first.Category is not { } c || !rules.MinDaysBetweenRounds.TryGetValue(c, out var min)) continue;

            var rounds = subject.OrderBy(p => p.Occurrence).ToList();
            for (var i = 0; i < rounds.Count; i++)
            for (var j = i + 1; j < rounds.Count; j++)
            {
                var a = rounds[i];
                var b = rounds[j];
                if (a.Occurrence == b.Occurrence) continue;
                // At least one of the pair must still be movable and ahead of us.
                if (!(a.Movable && a.ScheduledAtUtc >= nowUtc) && !(b.Movable && b.ScheduledAtUtc >= nowUtc)) continue;

                var gap = DayOf(b.ScheduledAtUtc).DayNumber - DayOf(a.ScheduledAtUtc).DayNumber;
                if (gap >= min) continue;

                var what = gap <= 0
                    ? $"round {b.Occurrence} ({When(b.ScheduledAtUtc)}) is planned BEFORE or on the same day as round {a.Occurrence} ({When(a.ScheduledAtUtc)})"
                    : $"rounds {a.Occurrence} and {b.Occurrence} are only {gap} day(s) apart ({When(a.ScheduledAtUtc)} and {When(b.ScheduledAtUtc)}); the minimum is {min}";

                result.Add(new SoMeRuleConflict(
                    $"gap|{a.SubjectKey}|{a.Occurrence}|{b.Occurrence}|{a.ScheduledAtUtc.UtcTicks}|{b.ScheduledAtUtc.UtcTicks}",
                    "Days between rounds",
                    $"{a.Label} — {what}.",
                    [a.Id, b.Id]));
            }
        }

        // 3 — too many sponsor posts, or too many posts, on one day.
        foreach (var day in posts.Where(p => p.ScheduledAtUtc >= nowUtc)
                     .GroupBy(p => DayOf(p.ScheduledAtUtc)).OrderBy(g => g.Key))
        {
            if (!day.Any(p => p.Movable)) continue;

            var sponsors = day.Where(p => SoMeSchedulePlanner.IsSponsor(p.SubjectKey)).ToList();
            if (rules.MaxSponsorPostsPerDay is { } cap && sponsors.Count > cap)
            {
                result.Add(new SoMeRuleConflict(
                    $"sponsorday|{day.Key:yyyyMMdd}|{string.Join(",", sponsors.Select(s => s.Id).Order())}",
                    "Sponsor posts per day",
                    $"{day.Key:ddd d MMM}: {sponsors.Count} sponsor posts on one day "
                    + $"({string.Join(", ", sponsors.Select(s => s.Label))}); the limit is {cap}.",
                    sponsors.Select(s => s.Id).ToList()));
            }

            if (day.Count() > rules.MaxPostsPerDay)
            {
                result.Add(new SoMeRuleConflict(
                    $"day|{day.Key:yyyyMMdd}|{string.Join(",", day.Select(s => s.Id).Order())}",
                    "Posts per day",
                    $"{day.Key:ddd d MMM}: {day.Count()} posts on one day; the most allowed is "
                    + $"{rules.MaxPostsPerDay}.",
                    day.Select(s => s.Id).ToList()));
            }
        }

        // 4 — wanted, but no seat anywhere before the event.
        foreach (var item in noRoom ?? [])
        {
            result.Add(new SoMeRuleConflict(
                $"noroom|{item}", "No room before the event",
                $"{item}: there is no free day left before the event for this post. "
                + "Allow more posts per day, or move other posts.",
                []));
        }

        return result;
    }

    private string BuildBody(IReadOnlyList<SoMeRuleConflict> conflicts)
    {
        string Href(string path) => string.IsNullOrEmpty(_hubUrl) ? path : _hubUrl + path;

        var rows = string.Join("", conflicts.Select(c =>
            "<tr>"
            + $"<td style=\"padding:6px 10px;border-bottom:1px solid #eee;white-space:nowrap;vertical-align:top;\"><strong>{WebUtility.HtmlEncode(c.Rule)}</strong></td>"
            + $"<td style=\"padding:6px 10px;border-bottom:1px solid #eee;\">{WebUtility.HtmlEncode(c.Message)}</td>"
            + "<td style=\"padding:6px 10px;border-bottom:1px solid #eee;white-space:nowrap;vertical-align:top;\">"
            + string.Join("<br>", c.PostIds.Select(id =>
                $"<a href=\"{WebUtility.HtmlEncode(Href($"/Organizer/SoMePostEditor?id={id}"))}\">Post #{id} &rarr;</a>"))
            + "</td></tr>"));

        return
            $"<p><strong>{conflicts.Count}</strong> social-media post(s) do not follow your planning rules. "
            + "Each line says what is wrong. To fix it, open the post and move it — or change the rule.</p>"
            + "<table style=\"border-collapse:collapse;font-size:14px;\">"
            + "<tr style=\"text-align:left;\">"
            + "<th style=\"padding:6px 10px;border-bottom:2px solid #ddd;\">Rule</th>"
            + "<th style=\"padding:6px 10px;border-bottom:2px solid #ddd;\">What is wrong</th>"
            + "<th style=\"padding:6px 10px;border-bottom:2px solid #ddd;\">Open</th>"
            + $"</tr>{rows}</table>"
            + $"<p>The rules are on <a href=\"{WebUtility.HtmlEncode(Href("/Organizer/SoMeSettings"))}\">SoMe settings</a>. "
            + "You get this mail again only when the list changes (at most once a day for the same list).</p>";
    }
}
