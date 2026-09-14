using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Settings;
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
    int WordOfMouthPct,    // §181: word-of-mouth as % of ALL attendees (0 when no attendees)
    int FirstTimerCount,   // first-timers ("No, ELDK27 is my first…")
    int FirstTimerPct,     // §181: first-timers as % of ALL attendees (0 when no attendees)
    IReadOnlyList<TelemetryDay> Daily,
    IReadOnlyList<TelemetryTable> Tables,
    DateTimeOffset GeneratedAtUtc,
    string? FilterKey = null,
    string? FilterValue = null,
    IReadOnlyList<TelemetryFilterDimension>? FilterDimensions = null,
    // The last-successful-sync timestamp from the Sync phase (REQUIREMENTS §127/§69) —
    // what the "Updated <t> UTC" footer shows, NOT the wall-clock the page rendered.
    // Null when the mirror has never been synced for this edition.
    DateTimeOffset? LastSyncAtUtc = null,

    /// <summary>
    /// §1063 — how often the attendee mirror refreshes, in minutes, so the footer can say when the
    /// next one is due. Null when the cadence cannot be determined.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-11: <i>"how often does the attendee telemetry refresh. we need to show
    /// that in the footer, so people can see next refresh time - it shows only Updated 11 Aug 2026
    /// 06:14 UTC"</i>.</para>
    ///
    /// <para>🔴 <b>READ from the job's configuration, never typed into the view.</b> The interval is
    /// an operator setting on <c>/Organizer/Jobs</c> — a "10 minutes" hard-coded in the footer becomes
    /// a lie the first time he changes it, and it is the kind of lie nobody notices (§1056's stale
    /// "25 timer jobs" in the architecture diagram).</para>
    /// </remarks>
    int? RefreshEveryMinutes = null);

public sealed class AttendeeTelemetryService
{
    // SQL is cheap, so the cache is only a short de-dupe for bursts of page loads. The
    // key embeds the last-sync timestamp (see GetRawAsync) so a fresh sync busts it
    // immediately — the panel always reflects the latest mirror state (REQUIREMENTS §127).
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    private const int MaxDistinctForBreakdown = 30;

    /// <summary>§1063 — the job whose cadence the footer quotes. The join key to JobCatalog.</summary>
    private const string AttendeeSyncJobName = "AttendeeBackstageSyncJob";

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

    /// <summary>
    /// §1147 — the ERP, used ONLY to name the country behind a coupon. Optional and fail-soft.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-28: <i>"here we need to lookup against the coupon module, where you
    /// can fetch the customer details incl. country"</i>.</para>
    ///
    /// <para>🔒 Null-tolerant on purpose: a host without the ERP wired, or an ERP that is down,
    /// must render the page with "Not given" rather than fail. This is a label on a chart — it may
    /// never cost anyone the page.</para>
    /// </remarks>
    private readonly Erp.IEconomicInvoiceClient? _erp;

    public AttendeeTelemetryService(
        CommunityHubDbContext db, IMemoryCache cache, ILogger<AttendeeTelemetryService> log,
        Erp.IEconomicInvoiceClient? erp = null)
    {
        _db = db;
        _cache = cache;
        _log = log;
        _erp = erp;
    }

    /// <summary>
    /// §1147 — the label for one attendee's country: their own, else the coupon customer's, else
    /// "Not given".
    /// </summary>
    /// <remarks>
    /// <para>🔒 <b>Order matters and is not negotiable:</b> a country the attendee actually gave us
    /// always wins. The coupon customer's country is a stand-in for a value we do not have, so it
    /// must never override one we do.</para>
    ///
    /// <para>⚠️ <b>"Not given", never "free/coupon order".</b> Every blank measured on 2026-08-28
    /// came from a coupon order — but that is an observation about this week's data, not a rule. A
    /// paid order with an incomplete address lands here too, and the label would then be a
    /// confident lie. The honest word is the one that survives the next case.</para>
    ///
    /// <para>Pure and public so the sweep and its tests answer the same question.</para>
    /// </remarks>
    public static string ResolveCountryLabel(string? own, string? viaCoupon)
    {
        if (!string.IsNullOrWhiteSpace(own)) return own.Trim();
        if (!string.IsNullOrWhiteSpace(viaCoupon)) return viaCoupon.Trim();
        return "Not given";
    }

    /// <summary>
    /// §1147 — for attendees with no billing country, the country of the COUPON's ERP customer.
    /// </summary>
    /// <remarks>
    /// <para>🔑 <b>Why a blank happens at all, traced end to end.</b> An attendee's country is
    /// inherited from the ORDER's billing address, and Zoho collects no billing address on a FREE
    /// order — there is nothing to invoice, so it never asks. Verified on order 14880000004455013:
    /// total 0, 100% discount, promo <c>ELDK27-CBS</c>, every billing field null, three tickets.
    /// Nothing was lost in our mapping; the data does not exist upstream.</para>
    ///
    /// <para>🔒 <b>DISPLAY ONLY — his decision, 2026-08-28.</b> The resolved value is the COUPON
    /// CUSTOMER's country, not the attendee's own, so it is never written to
    /// <c>Attendee.Country</c>: exports and on-site lists keep the honest blank, and only this
    /// chart shows the derived value. A derivation stored in a data column stops looking like a
    /// derivation the moment somebody exports it.</para>
    ///
    /// <para>🔒 Reuses <see cref="Erp.CouponClaimExtractor"/> rather than re-reading promo codes out
    /// of the order JSON — §759, one definition of "which coupon is this ticket on".</para>
    ///
    /// <para>⚠️ Cached for an hour and only consulted when a blank actually exists, so a normal
    /// event does ZERO ERP calls: every paid order already carries its country.</para>
    /// </remarks>
    private async Task<IReadOnlyDictionary<string, string>> CouponCountryByTicketAsync(
        int eventId, IReadOnlyCollection<string> ticketIds, CancellationToken ct)
    {
        if (ticketIds.Count == 0 || _erp is null) return new Dictionary<string, string>();

        var cacheKey = $"attendee-telemetry-couponcountry|{eventId}";
        if (_cache.TryGetValue(cacheKey, out Dictionary<string, string>? hit) && hit is not null)
            return hit;

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            // The coupon settings carry the ERP customer; the order JSON carries which coupon a
            // ticket was bought on.
            var coupons = await _db.CouponInvoicingSettings
                .AsNoTracking()
                .Where(c => c.EventId == eventId && c.ErpCustomerNumber != null)
                .Select(c => new { c.CouponName, c.ErpCustomerNumber })
                .ToListAsync(ct);
            if (coupons.Count == 0) return map;

            var erpByCoupon = coupons
                .GroupBy(c => c.CouponName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().ErpCustomerNumber!.Value,
                              StringComparer.OrdinalIgnoreCase);

            var wanted = ticketIds.ToHashSet(StringComparer.Ordinal);
            var orders = await _db.Orders
                .AsNoTracking()
                .Where(o => o.EventId == eventId && o.RawJson != null)
                .Select(o => o.RawJson)
                .ToListAsync(ct);

            // ticket -> coupon, for the tickets we still need a country for.
            var couponByTicket = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var raw in orders)
            {
                foreach (var claim in Erp.CouponClaimExtractor.FromOrderJson(raw))
                {
                    if (wanted.Contains(claim.TicketId) && !couponByTicket.ContainsKey(claim.TicketId))
                        couponByTicket[claim.TicketId] = claim.CouponName;
                }
            }
            if (couponByTicket.Count == 0) return map;

            // One ERP read per DISTINCT customer, not per ticket.
            var countryByCustomer = new Dictionary<int, string?>();
            foreach (var ticket in couponByTicket)
            {
                if (!erpByCoupon.TryGetValue(ticket.Value, out var customerNumber)) continue;

                if (!countryByCustomer.TryGetValue(customerNumber, out var country))
                {
                    var detail = await _erp.GetCustomerAsync(customerNumber, ct);
                    country = detail?.Country;
                    countryByCustomer[customerNumber] = country;
                }

                if (!string.IsNullOrWhiteSpace(country)) map[ticket.Key] = country!.Trim();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 🔒 A chart label is never worth a failed page render.
            _log.LogWarning(ex, "§1147: could not resolve coupon countries; showing 'Not given'.");
            return new Dictionary<string, string>();
        }

        _cache.Set(cacheKey, map, TimeSpan.FromHours(1));
        return map;
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

        // §1063 — the cadence the footer quotes: the operator's own interval for the sync job if he
        // has set one, else the catalog's default. 🔒 Never a literal in the view.
        // ⚠️ `MinIntervalMinutes` is 0 when untouched, which means "no limit, the cron decides" — NOT
        // "every 0 minutes". Falling back to the catalog default is what makes the footer truthful on
        // a system nobody has reconfigured.
        var configured = await _db.JobRunStates
            .Where(j => j.FunctionName == AttendeeSyncJobName)
            .Select(j => (int?)j.MinIntervalMinutes)
            .FirstOrDefaultAsync(ct);

        var refreshMinutes = configured is > 0
            ? configured
            : JobCatalog.All.FirstOrDefault(j => j.FunctionName == AttendeeSyncJobName)?.DefaultIntervalMinutes;

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

        // §1147 — only for the attendees that actually have no country. On a normal event this list
        // is empty and no ERP call is made at all.
        var countryless = segData
            .Where(a => string.IsNullOrWhiteSpace(a.Country) && string.IsNullOrWhiteSpace(a.CountryCode))
            .Select(a => a.TicketId)
            .ToList();
        var couponCountry = await CouponCountryByTicketAsync(ev, countryless, ct);

        return new AttendeeTelemetry(
            SegmentKey: seg.Key,
            SegmentLabel: seg.Label,
            TotalAll: total,
            SegmentCount: count,
            PctOfTotal: total > 0 ? (int)Math.Round(100.0 * count / total) : 0,
            Pct2DayInSegment: count > 0 ? (int)Math.Round(100.0 * twoDay / count) : 0,
            Pct2DayAll: total > 0 ? (int)Math.Round(100.0 * twoDayAll / total) : 0,
            WordOfMouthCount: wordOfMouth,
            // §181: every headline card is a PERCENTAGE of the whole attendee base, never a raw
            // count. Divide-by-zero guarded (0 attendees ⇒ 0%; the panel shows "—" in that case).
            WordOfMouthPct: total > 0 ? (int)Math.Round(100.0 * wordOfMouth / total) : 0,
            FirstTimerCount: firstTimers,
            FirstTimerPct: total > 0 ? (int)Math.Round(100.0 * firstTimers / total) : 0,
            Daily: BuildDaily(segData),
            Tables: BuildTables(segData, isOrganizer, couponCountry),
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            FilterKey: activeFilterKey,
            FilterValue: activeFilterValue,
            FilterDimensions: dimensions,
            LastSyncAtUtc: lastSync,
            RefreshEveryMinutes: refreshMinutes);
    }

    /// <summary>
    /// §1064 — the heading for the FREE-TEXT job title, which is NOT the curated "Job role" question.
    /// </summary>
    /// <remarks>
    /// <para>🔴 <b>Two different cards carried the identical heading "Job role of attendees".</b>
    /// Operator 2026-08-11, looking at one of them: <i>"where are these data coming from - i dont
    /// recognize them … i bet it is the title"</i>. He was right.</para>
    ///
    /// <list type="bullet">
    /// <item><b>The curated question</b> — custom field <c>single_choice_2</c>, a fixed dropdown
    /// (Modern Workplace Specialist · Security Specialist · Management …), headed "Job role of
    /// attendees". ⚠️ §1217: this said <c>single_choice_1</c>, which actually holds the interest /
    /// primary track (Security · Intune · Azure) — the labels were rotated.</item>
    /// <item><b>This one</b> — Zoho's <c>contact.designation</c>, FREE TEXT the attendee types
    /// themselves, which is why it reads CEO · IT-sikkerhedskonsulent · M365 consultant · Systems
    /// Programmer, in two languages and no fixed vocabulary.</item>
    /// </list>
    ///
    /// <para>🔑 <b>Nothing was wrong with the data — the label was claiming to be a different
    /// question.</b> Same defect shape as §1052, where a caption made a correctly-gated table look
    /// like a leak: the figures were right and the sentence above them was not. ⚠️ Two panels may
    /// never share a heading; that is the thing to keep, not this particular wording.</para>
    /// </remarks>
    public const string JobTitleLabel = "Job title (typed by the attendee)";

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
                if (fields is not null && fields.TryGetValue(key[3..], out var v))
                    return MultiSelectAnswer.Collapse(v);   // §1062 — most-recent edition wins
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
        AddFixed("role", JobTitleLabel, a => a.JobTitle);

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
                // §1062 — a multi-select answer counts as ONE option (the most recent edition), so
                // the filter dropdown offers real options rather than every ticked combination.
                var one = MultiSelectAnswer.Collapse(val);
                if (one.Length == 0) continue;
                counts[one] = counts.TryGetValue(one, out var n) ? n + 1 : 1;
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
    private static List<TelemetryTable> BuildTables(
        IReadOnlyList<BackstageAttendee> data, bool isOrganizer,
        IReadOnlyDictionary<string, string>? couponCountry = null)
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

        // 🔑 §1066 — ORDER IS LAYOUT. Operator 2026-08-11: *"reorder them so top companies (for
        // organizers only) are shown next to the job titles, as they will be long in length. and then
        // resident move down"*.
        //
        // The page renders these two-up, so a table's NEIGHBOUR is decided by its position in this
        // list. Job title and Top companies are both free-text and both long (16 and 12 rows on a
        // 31-attendee event); "Resident of Attendee" is usually ONE row. Pairing a 16-row card with a
        // 1-row card leaves a column of whitespace as tall as the long one — which is exactly what he
        // was looking at.
        // ⇒ The two long ones sit together and the short one drops below, where being alone costs
        // nothing.
        var roles = Slices(a => Norm(a.JobTitle), dropBlank: true);
        if (roles.Count > 0) tables.Add(new(JobTitleLabel, roles));
        // 🛑 §1052 — "Top companies" IS ORGANIZER-ONLY, AND THAT IS SETTLED. DO NOT REMOVE IT.
        //
        // Operator 2026-08-10, in one exchange: *"we cannot expose company names due to gdpr - this
        // is public page (remove)"* → measured → *"i confirm it what you wrote"* → *"organizers is
        // ok to see it, reverse"* → **"everybody must be able to see the page, but nobody except
        // organizers can see top companies"**. That final sentence is the requirement, and it is
        // EXACTLY what §69 already implemented. The table was briefly deleted and restored.
        //
        // 🔑 THE REPORT WAS REAL; THE DIAGNOSIS WAS THE PAGE'S OWN BLURB. All three telemetry
        // surfaces share this panel and the same H1 "Who's coming to Experts Live Denmark", so the
        // only thing distinguishing them was the organizer page's claim of parity — "Same figures as
        // the public sponsor telemetry page" — which was FALSE precisely because of this table.
        // Seeing company names under that sentence is a correct inference from a wrong premise.
        // ⇒ The fix was the sentence, not the feature. Fixing the data would have destroyed a
        // capability to correct a caption.
        //
        // MEASURED on PROD the same day, and the reason the removal was reversed: an anonymous GET
        // of /attendee-telemetry returned 200 with no "Top companies" and zero occurrences of
        // "compan" in the body. Both /attendee-telemetry and /Sponsor/Telemetry hard-code
        // isOrganizer:false, so the aggregate is never CONSTRUCTED for them — not merely hidden.
        if (isOrganizer)
        {
            var companies = Slices(a => Norm(a.CompanyName), top: 15, dropBlank: true);
            if (companies.Count > 0) tables.Add(new("Top companies", companies, OrganizerOnly: true));
        }

        // §1066 — LAST, so the two long free-text cards above pair with each other. ⚠️ For a
        // NON-organizer there is no Top companies card, so this pairs with Job title instead — still
        // correct, because the page then has one long card rather than two.
        // 🔑 §1147 — a blank COUNTRY says something specific, so it says it.
        //
        // Operator 2026-08-28, on an em-dash slice of 3: *"how can country be blank"*. Traced end to
        // end: the country is inherited from the ORDER's billing address, and Backstage collects no
        // billing address on a FREE order — there is nothing to invoice, so it never asks. Confirmed
        // in the mirror (order 14880000004455013: total 0, discount 100%, every billing field null,
        // 3 tickets) and in his own Backstage screenshot. Nothing was lost in our mapping; the data
        // does not exist upstream.
        //
        // ⚠️ Labelled "Not given" and NOT "free/coupon order". Every blank measured today came from
        // a coupon order, but that is an observation about this week's data, not a rule — a paid
        // order with an incomplete address would land here too, and the label would then be a
        // confident lie. The honest word is the one that survives the next case.
        //
        // 🔒 And deliberately NOT inferred from the purchaser's phone prefix or e-mail domain. Both
        // would be right for this order (+45 / cbs.dk) and wrong the moment somebody abroad buys for
        // a Danish attendee. A guess printed as data is worse than a blank, because it stops
        // looking like a question.
        //
        // 🔒 The row is KEPT, never dropped: these are three real attendees, and a country breakdown
        // that quietly sums to fewer people than the event has is the more expensive error.
        // 🔒 The coupon customer's country is used ONLY when the attendee has none of their own,
        // and only here — never written to the mirror (his decision, see the resolver's remarks).
        string CountryOf(BackstageAttendee a) => ResolveCountryLabel(
            a.Country ?? a.CountryCode,
            couponCountry is not null && couponCountry.TryGetValue(a.TicketId, out var c) ? c : null);
        tables.Add(new("Resident of Attendee", Slices(CountryOf)));

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
                // 🔴 §1062 — ONE COUNT PER RESPONDENT, in the most recent edition they ticked.
                // Operator 2026-08-11: *"if he ticked eldk26 and others like eldk25 or eldk24, then
                // add count to eldk26 … if he ticked only eldk24, then add it to eldk24"*.
                // 🔑 Counting every ticked option instead would make the panel's total exceed the
                // attendee count and its percentages exceed 100% — arithmetically defensible, and an
                // unreadable card with no obvious denominator.
                var one = MultiSelectAnswer.Collapse(v);
                if (one.Length == 0) continue;
                counts[one] = counts.TryGetValue(one, out var n) ? n + 1 : 1;
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
        // 🔴 §1217 — these three were ROTATED one step. Operator 2026-09-12, on the PROD page: *"these
        // 3 are wrong … modern workplace specialist = job role of attendee. security, intune, azure,
        // etc = attendee interest / primary track, type of attendee = internal it dept, microsoft
        // partner, etc"*. Mapped by the ANSWERS each key actually holds in PROD, not by key order:
        //   single_choice   → Internal IT department · Microsoft Partner · Vendor / Exhibitor / Sponsor
        //   single_choice_1 → Security · Intune · Azure · Microsoft 365 …
        //   single_choice_2 → Modern Workplace Specialist · Security Specialist · Management …
        ["single_choice"]   = "Type of attendee",
        ["single_choice_1"] = "Attendee interest / primary track",
        ["single_choice_2"] = "Job role of attendees",
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
