using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations;

/// <summary>One aggregate slice (label + count).</summary>
public sealed record TelemetrySlice(string Label, int Count);

/// <summary>One titled breakdown table. <paramref name="OrganizerOnly"/> tables (§69)
/// are rendered ONLY on the organizer surface — never on the public or sponsor pages.</summary>
public sealed record TelemetryTable(string Title, IReadOnlyList<TelemetrySlice> Slices, bool OrganizerOnly = false);

/// <summary>One day on the sales-over-time graph (daily + running total).</summary>
public sealed record TelemetryDay(DateOnly Day, int Count, int Cumulative);

/// <summary>A preset sponsor analysis segment (dropdown option).</summary>
public sealed record TelemetrySegment(string Key, string Label);

/// <summary>
/// A topic the dashboard can be filtered by (e.g. Country, Job role, Ticket type, or
/// a registration custom field) plus the distinct values seen for it — drives the
/// "filter by topic" dropdowns (§36).
/// </summary>
public sealed record TelemetryFilterDimension(string Key, string Label, IReadOnlyList<string> Values);

/// <summary>
/// Anonymous, AGGREGATE-ONLY attendee analytics from the CEH SQL mirror of the Zoho
/// Backstage dataset (REQUIREMENTS §127 — never live Zoho, never the filtered local
/// table). Only the ACTIVE mirror set is counted (soft-cancelled rows are excluded so
/// totals match Zoho's active set). A sponsor picks a SEGMENT (e.g. "Denmark only",
/// "2-day ticket") and gets headline metrics (count, % of total, % on a 2-day pre-day
/// ticket), a sales-over-time graph, and breakdown tables — all for that segment. No
/// individual data.
/// </summary>
public sealed record AttendeeTelemetry(
    string SegmentKey,
    string SegmentLabel,
    int TotalAll,
    int SegmentCount,
    int PctOfTotal,
    int Pct2DayInSegment,
    // Dashboard headline KPIs (2026-06-28) — computed over ALL attendees, not the segment:
    int Pct2DayAll,        // % who bought a 2-day ticket
    int WordOfMouthCount,  // people who heard via word of mouth
    int FirstTimerCount,   // first-timers ("No, ELDK27 is my first…")
    IReadOnlyList<TelemetryDay> Daily,
    IReadOnlyList<TelemetryTable> Tables,
    DateTimeOffset GeneratedAtUtc,
    string? FilterKey = null,
    string? FilterValue = null,
    IReadOnlyList<TelemetryFilterDimension>? FilterDimensions = null,
    // The last-successful-sync timestamp from the Sync phase (REQUIREMENTS §127/§69) —
    // what the "Updated <t> UTC" footer shows, NOT the wall-clock the page rendered.
    // Null when the mirror has never been synced for this edition.
    DateTimeOffset? LastSyncAtUtc = null);

public sealed class AttendeeTelemetryService
{
    // SQL is cheap, so the cache is only a short de-dupe for bursts of page loads. The
    // key embeds the last-sync timestamp (see GetRawAsync) so a fresh sync busts it
    // immediately — the panel always reflects the latest mirror state (REQUIREMENTS §127).
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
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
    };

    // The "2-day" (Master-Class eligibility) definition is UNIFIED through the single
    // MasterClassTicketPolicy (REQUIREMENTS §125) — see Is2Day. The old telemetry-only
    // regex was removed so all three call sites can no longer drift apart.
    private static readonly Regex DecisionRx = new(@"chief|\bc[ei]o\b|\bcto\b|\bciso\b|\bcio\b|founder|owner|head of|director|\bvp\b|manager|lead\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ArchitectRx = new(@"architect|consultant|advisor", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DeveloperRx = new(@"developer|engineer|programmer|\bdev\b|software", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ItProRx = new(@"administrator|\bit\b|sysadmin|system|specialist|technician|operations|support|devops|cloud", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly CommunityHubDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly ILogger<AttendeeTelemetryService> _log;

    public AttendeeTelemetryService(
        CommunityHubDbContext db, IMemoryCache cache, ILogger<AttendeeTelemetryService> log)
    {
        _db = db;
        _cache = cache;
        _log = log;
    }

    public async Task<AttendeeTelemetry?> GetAsync(
        string? segmentKey = null, string? filterKey = null, string? filterValue = null,
        bool isOrganizer = false, CancellationToken ct = default)
    {
        // Aggregate from the CEH SQL mirror, scoped to the active edition (§127). No
        // active event ⇒ nothing to show (the panel renders its "not available" notice).
        var eventId = await _db.Events.Where(e => e.IsActive)
            .Select(e => (int?)e.Id).FirstOrDefaultAsync(ct);
        if (eventId is not int ev) return null;

        // The last-successful-sync stamp drives both the footer (§69) and the cache key.
        var lastSync = await _db.SyncRuns
            .Where(s => s.EventId == ev && s.Key == SyncRun.AttendeeBackstageKey)
            .Select(s => (DateTimeOffset?)s.LastSuccessAt)
            .FirstOrDefaultAsync(ct);

        var all = await GetRawAsync(ev, lastSync, ct);

        var dimensions = BuildFilterDimensions(all);
        // A filter only counts when its value is one the data actually has.
        var activeFilterKey = !string.IsNullOrWhiteSpace(filterKey)
            && !string.IsNullOrWhiteSpace(filterValue)
            && dimensions.Any(d => d.Key == filterKey && d.Values.Contains(filterValue!, StringComparer.OrdinalIgnoreCase))
            ? filterKey : null;
        var activeFilterValue = activeFilterKey is null ? null : filterValue;

        var seg = Segments.FirstOrDefault(s => s.Key == segmentKey) ?? Segments[0];

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
            _ => _ => true,
        };

        // Segment first, then the optional topic filter (Country / Job role / Ticket /
        // a custom field), so a sponsor can drill e.g. "Denmark" + "Job role = Architect".
        var segData = all.Where(pred)
            .Where(a => MatchesFilter(a, activeFilterKey, activeFilterValue))
            .ToList();
        var total = all.Count;
        var count = segData.Count;
        var twoDay = segData.Count(Is2Day);

        // Dashboard KPIs over the WHOLE attendee base (not the segment): 2-day %, word-of-mouth
        // count, and first-timer count. After the 2026-06-28 field-label swap, single_choice_3 is
        // the "how did you hear" answer and multiple_choice is the "attended before?" answer.
        var twoDayAll = all.Count(Is2Day);
        var wordOfMouth = all.Count(a =>
            (DimensionValue(a, "cf:single_choice_3") ?? "").Contains("word of mouth", StringComparison.OrdinalIgnoreCase));
        var firstTimers = all.Count(a =>
        {
            var v = DimensionValue(a, "cf:multiple_choice") ?? "";
            return v.Contains("my first", StringComparison.OrdinalIgnoreCase)
                || v.StartsWith("no", StringComparison.OrdinalIgnoreCase);
        });

        return new AttendeeTelemetry(
            SegmentKey: seg.Key,
            SegmentLabel: seg.Label,
            TotalAll: total,
            SegmentCount: count,
            PctOfTotal: total > 0 ? (int)Math.Round(100.0 * count / total) : 0,
            Pct2DayInSegment: count > 0 ? (int)Math.Round(100.0 * twoDay / count) : 0,
            Pct2DayAll: total > 0 ? (int)Math.Round(100.0 * twoDayAll / total) : 0,
            WordOfMouthCount: wordOfMouth,
            FirstTimerCount: firstTimers,
            Daily: BuildDaily(segData),
            Tables: BuildTables(segData, isOrganizer),
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            FilterKey: activeFilterKey,
            FilterValue: activeFilterValue,
            FilterDimensions: dimensions,
            LastSyncAtUtc: lastSync);
    }

    /// <summary>The value of a filterable dimension for one attendee (null = no value).</summary>
    private static string? DimensionValue(BackstageAttendee a, string key)
    {
        if (key == "country") return string.IsNullOrWhiteSpace(a.Country) ? a.CountryCode : a.Country;
        if (key == "role") return a.JobTitle;
        if (key == "ticket") return a.TicketClassName;
        if (key.StartsWith("cf:", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(a.CustomFieldsJson))
        {
            try
            {
                var fields = JsonSerializer.Deserialize<Dictionary<string, string>>(a.CustomFieldsJson!);
                if (fields is not null && fields.TryGetValue(key[3..], out var v)) return v;
            }
            catch { /* ignore malformed */ }
        }
        return null;
    }

    private static bool MatchesFilter(BackstageAttendee a, string? key, string? value)
    {
        if (key is null || value is null) return true;
        var v = DimensionValue(a, key);
        return v is not null && v.Trim().Equals(value.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Build the "filter by topic" dimensions from the full data: Ticket type, Country,
    /// Job role and each registration custom field, each with its distinct values
    /// (≤ <see cref="MaxDistinctForBreakdown"/> so a free-text field never floods the dropdown).
    /// </summary>
    private static List<TelemetryFilterDimension> BuildFilterDimensions(IReadOnlyList<BackstageAttendee> all)
    {
        var dims = new List<TelemetryFilterDimension>();

        void AddFixed(string key, string label, Func<BackstageAttendee, string?> sel)
        {
            var values = all.Select(sel)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v!.Trim())
                .GroupBy(v => v, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Key).ToList();
            if (values.Count is > 0 and <= MaxDistinctForBreakdown)
                dims.Add(new TelemetryFilterDimension(key, label, values));
        }

        // Friendly headers matching the old telemetry system.
        AddFixed("ticket", "Ticket type", a => a.TicketClassName);
        AddFixed("country", "Resident of Attendee", a => string.IsNullOrWhiteSpace(a.Country) ? a.CountryCode : a.Country);
        AddFixed("role", "Job role of attendees", a => a.JobTitle);

        // Custom fields (single/multiple choice) become filters too.
        var byField = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in all)
        {
            if (string.IsNullOrWhiteSpace(a.CustomFieldsJson)) continue;
            Dictionary<string, string>? fields;
            try { fields = JsonSerializer.Deserialize<Dictionary<string, string>>(a.CustomFieldsJson!); } catch { continue; }
            if (fields is null) continue;
            foreach (var (k, val) in fields)
            {
                if (string.IsNullOrWhiteSpace(val)) continue;
                if (!byField.TryGetValue(k, out var counts)) byField[k] = counts = new(StringComparer.OrdinalIgnoreCase);
                counts[val.Trim()] = counts.TryGetValue(val.Trim(), out var n) ? n + 1 : 1;
            }
        }
        foreach (var (field, counts) in byField)
        {
            if (counts.Count is 0 or > MaxDistinctForBreakdown) continue;
            var values = counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => kv.Key).ToList();
            dims.Add(new TelemetryFilterDimension($"cf:{field}", FieldLabel(field), values));
        }

        return dims;
    }

    /// <summary>
    /// The ACTIVE attendee rows for the edition, read from the CEH SQL mirror
    /// (Attendee joined to its Order) — NOT live Zoho (REQUIREMENTS §127). Soft-cancelled
    /// rows (<see cref="MirrorState.Cancelled"/>) are excluded so the totals match Zoho's
    /// active set. Mapped onto the existing <see cref="BackstageAttendee"/> shape so all
    /// downstream aggregation (segments, tables, filters, daily graph) is unchanged. The
    /// sales-over-time timestamp uses the order's Zoho creation time when known, else the
    /// mirror row's creation time. Cached briefly, keyed by the last-sync stamp so a fresh
    /// sync is reflected immediately.
    /// </summary>
    private async Task<List<BackstageAttendee>> GetRawAsync(
        int eventId, DateTimeOffset? lastSync, CancellationToken ct)
    {
        var cacheKey = $"attendee-telemetry-raw|{eventId}|{lastSync?.UtcTicks ?? 0}";
        if (_cache.TryGetValue(cacheKey, out List<BackstageAttendee>? cached) && cached is not null)
            return cached;

        var rows = await _db.Attendees
            .AsNoTracking()
            .Where(a => a.EventId == eventId && a.MirrorState == MirrorState.Active)
            .Select(a => new
            {
                a.BackstageTicketId,
                a.OrderId,
                a.Email,
                a.FirstName,
                a.LastName,
                a.TicketClassName,
                a.CompanyName,
                a.JobTitle,
                a.Phone,
                a.Country,
                a.CountryCode,
                a.City,
                a.Postcode,
                a.TaxId,
                a.CustomFieldsJson,
                a.CreatedAt,
                OrderCreatedAt = a.Order != null ? a.Order.SourceCreatedAt : null,
            })
            .ToListAsync(ct);

        var list = rows.Select(r =>
        {
            var created = r.OrderCreatedAt ?? r.CreatedAt;
            return new BackstageAttendee(
                TicketId: r.BackstageTicketId ?? string.Empty,
                OrderId: r.OrderId ?? string.Empty,
                Email: r.Email,
                FirstName: r.FirstName,
                LastName: r.LastName,
                TicketClassName: r.TicketClassName ?? string.Empty,
                Attending: true,
                CompanyName: r.CompanyName,
                JobTitle: r.JobTitle,
                Phone: r.Phone,
                Country: r.Country,
                CountryCode: r.CountryCode,
                City: r.City,
                Postcode: r.Postcode,
                TaxId: r.TaxId,
                CustomFieldsJson: r.CustomFieldsJson,
                // Re-emit in the format BuildDaily already parses, so the daily/sales graph
                // logic is shared with the legacy path unchanged.
                CreatedTimeRaw: created.UtcDateTime.ToString("MM/dd/yyyy HH:mm:ss", CultureInfo.InvariantCulture));
        }).ToList();

        _cache.Set(cacheKey, list, Ttl);
        _log.LogDebug(
            "AttendeeTelemetry: read {Count} active mirror rows for event {EventId} (lastSync {LastSync:o}).",
            list.Count, eventId, lastSync);
        return list;
    }

    private static bool IsDk(BackstageAttendee a) =>
        string.Equals(a.CountryCode, "DK", StringComparison.OrdinalIgnoreCase)
        || (a.Country ?? "").Contains("denmark", StringComparison.OrdinalIgnoreCase)
        || (a.Country ?? "").Contains("danmark", StringComparison.OrdinalIgnoreCase);

    private static bool Is2Day(BackstageAttendee a) =>
        CommunityHub.Core.Domain.MasterClassTicketPolicy.IncludesMasterClass(a.TicketClassName);

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

    /// <summary>
    /// Build the breakdown tables for a segment. <paramref name="isOrganizer"/> gates
    /// DEFENSE-IN-DEPTH (§69): the OrganizerOnly "Top companies" aggregate is never even
    /// CONSTRUCTED for a non-organizer caller (public / sponsor), so the sensitive table
    /// can't leak off-surface. The render-time gate in the panel partial stays too.
    /// </summary>
    private static List<TelemetryTable> BuildTables(IReadOnlyList<BackstageAttendee> data, bool isOrganizer)
    {
        static string Norm(string? s) => string.IsNullOrWhiteSpace(s) ? "—" : s.Trim();
        List<TelemetrySlice> Slices(Func<BackstageAttendee, string> sel, int top = 0, bool dropBlank = false)
        {
            var q = data.GroupBy(sel).Select(g => new TelemetrySlice(g.Key, g.Count()))
                .Where(s => !dropBlank || s.Label != "—")
                .OrderByDescending(s => s.Count).ThenBy(s => s.Label, StringComparer.OrdinalIgnoreCase);
            return (top > 0 ? q.Take(top) : q).ToList();
        }

        var tables = new List<TelemetryTable> { new("Ticket type", Slices(a => Norm(a.TicketClassName))) };
        foreach (var t in CustomFieldTables(data)) tables.Add(t);
        tables.Add(new("Resident of Attendee", Slices(a => Norm(a.Country ?? a.CountryCode))));
        var roles = Slices(a => Norm(a.JobTitle), dropBlank: true);
        if (roles.Count > 0) tables.Add(new("Job role of attendees", roles));
        // §69 — "Top companies" reveals which companies' people are registered, so it is
        // ORGANIZER-ONLY. Defense-in-depth: only assemble it for an organizer caller; for
        // public/sponsor callers the aggregate is never built (not just hidden at render).
        if (isOrganizer)
        {
            var companies = Slices(a => Norm(a.CompanyName), top: 15, dropBlank: true);
            if (companies.Count > 0) tables.Add(new("Top companies", companies, OrganizerOnly: true));
        }
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
            tables.Add((counts.Values.Sum(), new TelemetryTable(FieldLabel(field), slices)));
        }
        return tables.OrderByDescending(t => t.Answered).Select(t => t.Table).ToList();
    }

    private static string Pretty(string raw)
    {
        var s = raw.Replace('_', ' ').Replace('-', ' ').Trim();
        return s.Length == 0 ? raw : char.ToUpperInvariant(s[0]) + s[1..];
    }

    /// <summary>
    /// Friendly section headers matching the OLD telemetry system (operator 2026-06-25 — the
    /// raw Zoho custom-field keys "single_choice"/"single_choice_1/2/3"/"multiple_choice" must
    /// read as the old human headers). Mapped by VALUE SET from the old charts. If a mapping
    /// looks wrong on a field, adjust it here. Unknown keys fall back to <see cref="Pretty"/>.
    /// </summary>
    private static readonly Dictionary<string, string> FriendlyFieldLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["single_choice"]   = "Attendee interest / primary track",
        ["single_choice_1"] = "Job role of attendees",
        ["single_choice_2"] = "Type of attendee",
        // Swapped 2026-06-28: single_choice_3 actually holds the "how did you hear" answer
        // (e.g. "Word of mouth"), and multiple_choice holds the "first time?" answer — the
        // labels were on the wrong keys, so the two cards showed each other's data.
        ["single_choice_3"] = "How did attendee learn about the event?",
        ["multiple_choice"] = "Have you attended ELDK before?",
    };

    /// <summary>Friendly label for a raw Zoho custom-field key (space/dash/underscore-insensitive).</summary>
    private static string FieldLabel(string raw)
    {
        var key = raw.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');
        return FriendlyFieldLabels.TryGetValue(key, out var friendly) ? friendly : Pretty(raw);
    }
}
