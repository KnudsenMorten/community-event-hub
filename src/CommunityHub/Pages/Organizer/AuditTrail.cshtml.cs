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

    /// <summary>
    /// §784.10 — show <see cref="AuditCategory.Engine"/> rows? OFF by default.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-03: <i>"by default i need a filter to not show Engine category. it
    /// should be a tick off to Show Engine related actions. i am drowning in events in audit"</i>.</para>
    ///
    /// <para>🔑 The engine writes on every tick of every job; a human acts a few dozen times a day.
    /// Mixed together, the rows that answer "who did this?" are buried under rows that answer
    /// "did the timer fire?" — and this page exists for the first question. §784.11 is the proof:
    /// a suspected mass-mailing incident was resolved in seconds by six `UserAction` rows, which had
    /// to be found among the engine's.</para>
    ///
    /// <para>🔒 Excluded from the DEFAULT VIEW, never from the RECORD. The rows are still written,
    /// still exported by the CSV when asked for, and one tick away. An audit trail that DROPS
    /// categories would be a different and much worse thing.</para>
    /// </remarks>
    [BindProperty(SupportsGet = true)] public bool ShowEngine { get; set; }

    /// <summary>
    /// §1061 — show mail a rollout ring deliberately withheld. OFF by default.
    /// </summary>
    /// <remarks>
    /// Operator 2026-08-11, mid ticket-sale: <i>"this is just noise now and makes it hard to view and
    /// follow"</i> — one run held 18 mails and sent 2, so the holds WERE the page.
    /// 🔒 Hidden, never discarded: it is the evidence that answers "why did this speaker not get the
    /// mail?", and that question is asked precisely when the rows are inconvenient.
    /// </remarks>
    [BindProperty(SupportsGet = true)] public bool ShowDropped { get; set; }

    /// <summary>
    /// §1061 — multi-select categories. Operator: <i>"in the filter, i want to have a multi-select of
    /// categories, so I can filter on specific"</i>.
    /// </summary>
    /// <remarks>
    /// ⚠️ <see cref="Category"/> (singular) is deliberately KEPT and still honoured when this is
    /// empty, so every existing link, bookmark and saved export URL keeps working. A filter rename
    /// that silently changes what an old URL returns is worse than no rename.
    /// </remarks>
    [BindProperty(SupportsGet = true)] public string[]? Cats { get; set; }

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

        // 🔴 §1061 — RING-DROPPED MAIL IS HIDDEN BY DEFAULT. Operator 2026-08-11: *"ring-gated mails
        // that logs as failed dont show on the audit page in the default view. i need to have a
        // 'Show Dropped mails (ring-gated)' feature"*.
        //
        // 🔑 It is not noise to be deleted — it is the evidence that the gate is working, and he
        // needs it when he asks "why did this speaker not get the mail?". So it is HIDDEN, never
        // discarded, and one checkbox brings it back.
        // ⚠️ Note the ordering: this applies REGARDLESS of the category filter, unlike ShowEngine
        // below. Picking "Email" from the list is asking about mail, not asking to see every
        // policy hold — and with 18 drops to 2 sends, the holds would be the whole page.
        if (!ShowDropped)
        {
            q = q.Where(e => e.Outcome != AuditOutcome.Dropped);
        }

        // §1061 — MULTI-SELECT categories. `Category` (singular) is kept so every existing link,
        // bookmark and export URL still works; `Cats` is the new multi-valued form and wins when
        // present.
        var chosen = (Cats ?? [])
            .Select(c => Enum.TryParse<AuditCategory>(c, out var v) ? (AuditCategory?)v : null)
            .Where(v => v is not null)
            .Select(v => v!.Value)
            .Distinct()
            .ToList();

        if (chosen.Count > 0)
        {
            q = q.Where(e => chosen.Contains(e.Category));
        }
        else if (!string.IsNullOrWhiteSpace(Category)
            && Enum.TryParse<AuditCategory>(Category, out var cat))
        {
            q = q.Where(e => e.Category == cat);
        }
        else if (!ShowEngine)
        {
            // §784.10 — hide the engine's own rows by DEFAULT so human actions are readable.
            //
            // 🔒 Skipped when an explicit Category is chosen: picking "Engine" from the category
            // list must SHOW Engine rows, not silently return nothing. A filter that quietly
            // contradicts the filter next to it is worse than no filter.
            q = q.Where(e => e.Category != AuditCategory.Engine);
        }

        if (Days > 0)
        {
            var since = DateTimeOffset.UtcNow.AddDays(-Days);
            q = q.Where(e => e.OccurredUtc >= since);
        }

        if (!string.IsNullOrWhiteSpace(Filter))
        {
            var f = Filter.Trim();
            // §331: Detail is searched too — it is where a destructive action records WHAT it
            // destroyed (e.g. the vacated volunteer shifts), so "which shifts did we lose?"
            // must be answerable by typing the shift name here.
            q = q.Where(e => e.ActorEmail.Contains(f)
                || e.Summary.Contains(f)
                || e.Action.Contains(f)
                || (e.Detail != null && e.Detail.Contains(f))
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

    /// <summary>
    /// The export's CSV body — rows in, file out. §1136a made it PUBLIC so the column contract can be
    /// pinned directly, the same reason §403 exposed <c>HotelFormService.StayWindowFor</c>: it is
    /// pure, and a timezone in an export is exactly the kind of thing that is wrong silently.
    /// </summary>
    public static string BuildCsv(IReadOnlyList<AuditEntry> rows)
    {
        // §1136a (operator 2026-08-25: *"export can also be in local time, please"*, then *"i dont
        // like the (Copenhagen) or OccuredCopenhagen - change to Local instead of Copenhagen"*) —
        // the export leads with LOCAL time, matching what the page now shows by default.
        //
        // 🔑 The column is named for the ROLE it plays ("the local reading"), not for the city that
        // happens to define it this year. The zone itself is still Europe/Copenhagen and still comes
        // from one place; the header just stops asserting a city to whoever opens the file.
        //
        // 🔒 UTC IS KEPT, not replaced. Two reasons, and both matter for an AUDIT export:
        //   • Nothing is lost. An export reconciled against an older one still has the exact column
        //     it had before, under the same name — so the two files can still be compared.
        //   • A timezone is only safe when it is LABELLED. Silently shifting the values under the
        //     old `OccurredUtc` header would make every historic export disagree with every new one
        //     with nothing on the page or in the file to explain why.
        //
        // ⚠️ This does change the COLUMN ORDER: the local column is first, so a reader that keys on
        // POSITION rather than header name shifts by one. Appending it last would have avoided that
        // but buried the column he asked for behind twelve others, in a file whose whole purpose is
        // being read by a human in Excel.
        var sb = new StringBuilder();
        sb.AppendLine("OccurredLocal,OccurredUtc,Category,Action,Actor,OnBehalfOf,Role,Outcome,Source,TargetType,TargetId,Summary,Detail,Path");
        foreach (var r in rows)
        {
            // 🔒 The SAME authority the page uses (SoMeDisplayTime.ToDanish, §844.5), so an exported
            // timestamp can never disagree with the one on screen for the same row.
            sb.Append(C(CommunityHub.Core.Integrations.SoMeDisplayTime
                          .ToDanish(r.OccurredUtc).ToString("yyyy-MM-dd HH:mm:ss"))).Append(',')
              .Append(C(r.OccurredUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss"))).Append(',')
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
              .Append(C(r.Detail)).Append(',')
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
