using System.Text;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Export;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// The UNIFIED AUDIT TRAIL view (REQUIREMENTS §24). Organizer-only. Shows every user
/// action + backend/engine event for the edition, filterable by category / actor /
/// action / time window, with usage counts (e.g. how many calendar syncs, emails) and
/// CSV + XLSX export. Read-only; the trail itself is append-only (written elsewhere).
/// </summary>
[Authorize]
public class AuditTrailModel : PageModel
{
    private const int PageSize = 300;

    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;

    public AuditTrailModel(CommunityHubDbContext db, ICurrentParticipantAccessor participant)
    {
        _db = db;
        _participant = participant;
    }

    public bool AccessDenied { get; private set; }

    [BindProperty(SupportsGet = true)] public string? Category { get; set; }
    [BindProperty(SupportsGet = true)] public string? Filter { get; set; }
    /// <summary>Look-back window in days (0 = all time). Defaults to 7.</summary>
    [BindProperty(SupportsGet = true)] public int Days { get; set; } = 7;

    public IReadOnlyList<AuditEntry> Rows { get; private set; } = Array.Empty<AuditEntry>();
    public int TotalCount { get; private set; }
    public bool Capped => TotalCount > PageSize;
    public IReadOnlyList<string> Categories { get; private set; } = Array.Empty<string>();
    /// <summary>Usage counts per category for the current window/filter (the "how many" view).</summary>
    public IReadOnlyList<(string Category, int Count)> CategoryCounts { get; private set; }
        = Array.Empty<(string, int)>();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer || me.IsActingAs) { AccessDenied = true; return Page(); }

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    public async Task<IActionResult> OnGetExportAsync(string? format, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer || me.IsActingAs) { AccessDenied = true; return Page(); }

        // Export the full filtered set (not just the page cap), newest-first.
        var rows = await BuildQuery(me.EventId).OrderByDescending(e => e.OccurredUtc).ToListAsync(ct);
        var csv = BuildCsv(rows);

        if (string.Equals(format, "xlsx", StringComparison.OrdinalIgnoreCase))
            return File(CsvToXlsx.Build(csv, "AuditTrail"), CsvToXlsx.ContentType, "audit-trail.xlsx");
        return File(Encoding.UTF8.GetBytes(csv), "text/csv", "audit-trail.csv");
    }

    private IQueryable<AuditEntry> BuildQuery(int eventId)
    {
        var q = _db.AuditEntries.AsNoTracking().Where(e => e.EventId == eventId);

        if (!string.IsNullOrWhiteSpace(Category)
            && Enum.TryParse<AuditCategory>(Category, out var cat))
            q = q.Where(e => e.Category == cat);

        if (Days > 0)
        {
            var since = DateTimeOffset.UtcNow.AddDays(-Days);
            q = q.Where(e => e.OccurredUtc >= since);
        }

        if (!string.IsNullOrWhiteSpace(Filter))
        {
            var f = Filter.Trim();
            q = q.Where(e => e.ActorEmail.Contains(f)
                || e.Summary.Contains(f)
                || e.Action.Contains(f)
                || (e.OnBehalfOf != null && e.OnBehalfOf.Contains(f)));
        }
        return q;
    }

    private async Task LoadAsync(int eventId, CancellationToken ct)
    {
        Categories = Enum.GetNames<AuditCategory>();

        var q = BuildQuery(eventId);
        TotalCount = await q.CountAsync(ct);

        // Usage counts per category (the "how many calendar syncs / emails" view).
        CategoryCounts = (await q
                .GroupBy(e => e.Category)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToListAsync(ct))
            .Select(x => (x.Key.ToString(), x.Count))
            .OrderByDescending(x => x.Count)
            .ToList();

        Rows = await q.OrderByDescending(e => e.OccurredUtc).Take(PageSize).ToListAsync(ct);
    }

    private static string BuildCsv(IReadOnlyList<AuditEntry> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("OccurredUtc,Category,Action,Actor,OnBehalfOf,Role,Outcome,Source,TargetType,TargetId,Summary,Path");
        foreach (var r in rows)
        {
            sb.Append(C(r.OccurredUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss"))).Append(',')
              .Append(C(r.Category.ToString())).Append(',')
              .Append(C(r.Action)).Append(',')
              .Append(C(r.ActorEmail)).Append(',')
              .Append(C(r.OnBehalfOf)).Append(',')
              .Append(C(r.ActorRole)).Append(',')
              .Append(C(r.Outcome.ToString())).Append(',')
              .Append(C(r.Source.ToString())).Append(',')
              .Append(C(r.TargetType)).Append(',')
              .Append(C(r.TargetId)).Append(',')
              .Append(C(r.Summary)).Append(',')
              .Append(C(r.Path)).Append('\n');
        }
        return sb.ToString();
    }

    // CSV-escape: wrap in quotes + double internal quotes (also neutralises a leading
    // =/+/-/@ so spreadsheets don't treat a value as a formula).
    private static string C(string? s)
    {
        s ??= string.Empty;
        if (s.Length > 0 && (s[0] is '=' or '+' or '-' or '@')) s = "'" + s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}
