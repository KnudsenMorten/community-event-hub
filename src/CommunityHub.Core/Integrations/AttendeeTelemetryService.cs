using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations;

/// <summary>One aggregate slice (label + count).</summary>
public sealed record TelemetrySlice(string Label, int Count);

/// <summary>One titled breakdown table.</summary>
public sealed record TelemetryTable(string Title, IReadOnlyList<TelemetrySlice> Slices);

/// <summary>One day on the sales-over-time graph (daily + running total).</summary>
public sealed record TelemetryDay(DateOnly Day, int Count, int Cumulative);

/// <summary>A preset sponsor analysis segment (dropdown option).</summary>
public sealed record TelemetrySegment(string Key, string Label);

/// <summary>
/// Anonymous, AGGREGATE-ONLY attendee analytics from the Zoho Backstage API (never the
/// filtered local table). A sponsor picks a SEGMENT (e.g. "Denmark only", "2-day ticket")
/// and gets headline metrics (count, % of total, % on a 2-day pre-day ticket), a
/// sales-over-time graph, and breakdown tables — all for that segment. No individual data.
/// </summary>
public sealed record AttendeeTelemetry(
    string SegmentKey,
    string SegmentLabel,
    int TotalAll,
    int SegmentCount,
    int PctOfTotal,
    int Pct2DayInSegment,
    IReadOnlyList<TelemetryDay> Daily,
    IReadOnlyList<TelemetryTable> Tables,
    DateTimeOffset GeneratedAtUtc);

public sealed class AttendeeTelemetryService
{
    private const string RawCacheKey = "attendee-telemetry-raw-v1";
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);
    private const int MaxDistinctForBreakdown = 30;

    /// <summary>The 10 sponsor-relevant analysis segments shown in the dropdown.</summary>
    public static readonly IReadOnlyList<TelemetrySegment> Segments = new[]
    {
        new TelemetrySegment("all", "All attendees"),
        new TelemetrySegment("dk", "Denmark only"),
        new TelemetrySegment("intl", "International (outside Denmark)"),
        new TelemetrySegment("twoday", "2-day ticket (pre-day / Master Class)"),
        new TelemetrySegment("oneday", "1-day ticket only"),
        new TelemetrySegment("decision", "Decision-makers (C-level / managers)"),
        new TelemetrySegment("architect", "Architects & consultants"),
        new TelemetrySegment("developer", "Developers & engineers"),
        new TelemetrySegment("itpro", "IT professionals & admins"),
        new TelemetrySegment("groups", "Group buyers (companies with 2+ tickets)"),
    };

    private static readonly Regex TwoDayRx = new(@"2.?day|two.?day|pre.?day|master", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DecisionRx = new(@"chief|\bc[ei]o\b|\bcto\b|\bciso\b|\bcio\b|founder|owner|head of|director|\bvp\b|manager|lead\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ArchitectRx = new(@"architect|consultant|advisor", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DeveloperRx = new(@"developer|engineer|programmer|\bdev\b|software", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ItProRx = new(@"administrator|\bit\b|sysadmin|system|specialist|technician|operations|support|devops|cloud", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ZohoClient _zoho;
    private readonly ZohoOptions _options;
    private readonly IMemoryCache _cache;
    private readonly ILogger<AttendeeTelemetryService> _log;

    public AttendeeTelemetryService(
        ZohoClient zoho, ZohoOptions options, IMemoryCache cache, ILogger<AttendeeTelemetryService> log)
    {
        _zoho = zoho;
        _options = options;
        _cache = cache;
        _log = log;
    }

    public async Task<AttendeeTelemetry?> GetAsync(string? segmentKey = null, CancellationToken ct = default)
    {
        var all = await GetRawAsync(ct);
        if (all is null) return null;

        var seg = Segments.FirstOrDefault(s => s.Key == segmentKey) ?? Segments[0];
        var companyCounts = all
            .Where(a => !string.IsNullOrWhiteSpace(a.CompanyName))
            .GroupBy(a => a.CompanyName!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        Func<BackstageAttendee, bool> pred = seg.Key switch
        {
            "dk" => IsDk,
            "intl" => a => !IsDk(a),
            "twoday" => Is2Day,
            "oneday" => a => !Is2Day(a) && !string.IsNullOrWhiteSpace(a.TicketClassName),
            "decision" => a => DecisionRx.IsMatch(a.JobTitle ?? ""),
            "architect" => a => ArchitectRx.IsMatch(a.JobTitle ?? ""),
            "developer" => a => DeveloperRx.IsMatch(a.JobTitle ?? ""),
            "itpro" => a => ItProRx.IsMatch(a.JobTitle ?? ""),
            "groups" => a => !string.IsNullOrWhiteSpace(a.CompanyName) && companyCounts.TryGetValue(a.CompanyName!.Trim(), out var n) && n >= 2,
            _ => _ => true,
        };

        var segData = all.Where(pred).ToList();
        var total = all.Count;
        var count = segData.Count;
        var twoDay = segData.Count(Is2Day);

        return new AttendeeTelemetry(
            SegmentKey: seg.Key,
            SegmentLabel: seg.Label,
            TotalAll: total,
            SegmentCount: count,
            PctOfTotal: total > 0 ? (int)Math.Round(100.0 * count / total) : 0,
            Pct2DayInSegment: count > 0 ? (int)Math.Round(100.0 * twoDay / count) : 0,
            Daily: BuildDaily(segData),
            Tables: BuildTables(segData),
            GeneratedAtUtc: DateTimeOffset.UtcNow);
    }

    private async Task<List<BackstageAttendee>?> GetRawAsync(CancellationToken ct)
    {
        if (!_options.Enabled) return null;
        if (_cache.TryGetValue(RawCacheKey, out List<BackstageAttendee>? cached) && cached is not null) return cached;
        try
        {
            var token = await _zoho.GetAccessTokenAsync(ct);
            if (string.IsNullOrWhiteSpace(token)) return null;
            var list = (await _zoho.GetBackstageAttendeesAsync(token!, ct)).ToList();
            _cache.Set(RawCacheKey, list, Ttl);
            return list;
        }
        catch (Exception ex) { _log.LogWarning(ex, "AttendeeTelemetry: Zoho pull failed."); return null; }
    }

    private static bool IsDk(BackstageAttendee a) =>
        string.Equals(a.CountryCode, "DK", StringComparison.OrdinalIgnoreCase)
        || (a.Country ?? "").Contains("denmark", StringComparison.OrdinalIgnoreCase)
        || (a.Country ?? "").Contains("danmark", StringComparison.OrdinalIgnoreCase);

    private static bool Is2Day(BackstageAttendee a) => TwoDayRx.IsMatch(a.TicketClassName ?? "");

    private static List<TelemetryDay> BuildDaily(IReadOnlyList<BackstageAttendee> data)
    {
        var byDay = new SortedDictionary<DateOnly, int>();
        foreach (var a in data)
        {
            if (string.IsNullOrWhiteSpace(a.CreatedTimeRaw)) continue;
            if (DateTime.TryParseExact(a.CreatedTimeRaw, "MM/dd/yyyy HH:mm:ss",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
                || DateTime.TryParse(a.CreatedTimeRaw, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
            {
                var d = DateOnly.FromDateTime(dt);
                byDay[d] = byDay.TryGetValue(d, out var n) ? n + 1 : 1;
            }
        }
        var result = new List<TelemetryDay>(); var run = 0;
        foreach (var (d, n) in byDay) { run += n; result.Add(new TelemetryDay(d, n, run)); }
        return result;
    }

    private static List<TelemetryTable> BuildTables(IReadOnlyList<BackstageAttendee> data)
    {
        static string Norm(string? s) => string.IsNullOrWhiteSpace(s) ? "—" : s.Trim();
        List<TelemetrySlice> Slices(Func<BackstageAttendee, string> sel, int top = 0, bool dropBlank = false)
        {
            var q = data.GroupBy(sel).Select(g => new TelemetrySlice(g.Key, g.Count()))
                .Where(s => !dropBlank || s.Label != "—")
                .OrderByDescending(s => s.Count).ThenBy(s => s.Label, StringComparer.OrdinalIgnoreCase);
            return (top > 0 ? q.Take(top) : q).ToList();
        }

        var tables = new List<TelemetryTable> { new("By ticket type", Slices(a => Norm(a.TicketClassName))) };
        foreach (var t in CustomFieldTables(data)) tables.Add(t);
        tables.Add(new("Where attendees are from", Slices(a => Norm(a.Country ?? a.CountryCode))));
        var roles = Slices(a => Norm(a.JobTitle), dropBlank: true);
        if (roles.Count > 0) tables.Add(new("Job roles", roles));
        var companies = Slices(a => Norm(a.CompanyName), top: 15, dropBlank: true);
        if (companies.Count > 0) tables.Add(new("Top companies", companies));
        return tables;
    }

    private static List<TelemetryTable> CustomFieldTables(IReadOnlyList<BackstageAttendee> data)
    {
        var byField = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in data)
        {
            if (string.IsNullOrWhiteSpace(a.CustomFieldsJson)) continue;
            Dictionary<string, string>? fields;
            try { fields = JsonSerializer.Deserialize<Dictionary<string, string>>(a.CustomFieldsJson!); } catch { continue; }
            if (fields is null) continue;
            foreach (var (k, v) in fields)
            {
                if (string.IsNullOrWhiteSpace(v)) continue;
                if (!byField.TryGetValue(k, out var counts)) byField[k] = counts = new(StringComparer.OrdinalIgnoreCase);
                counts[v.Trim()] = counts.TryGetValue(v.Trim(), out var n) ? n + 1 : 1;
            }
        }
        var tables = new List<(int Answered, TelemetryTable Table)>();
        foreach (var (field, counts) in byField)
        {
            if (counts.Count == 0 || counts.Count > MaxDistinctForBreakdown) continue;
            var slices = counts.Select(kv => new TelemetrySlice(kv.Key, kv.Value))
                .OrderByDescending(s => s.Count).ThenBy(s => s.Label, StringComparer.OrdinalIgnoreCase).ToList();
            tables.Add((counts.Values.Sum(), new TelemetryTable(Pretty(field), slices)));
        }
        return tables.OrderByDescending(t => t.Answered).Select(t => t.Table).ToList();
    }

    private static string Pretty(string raw)
    {
        var s = raw.Replace('_', ' ').Replace('-', ' ').Trim();
        return s.Length == 0 ? raw : char.ToUpperInvariant(s[0]) + s[1..];
    }
}
