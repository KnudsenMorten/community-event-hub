using System.Text;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using CommunityHub.Export;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Full attendee browser for organizers. The organizer Index card only shows
/// reconciliation MISMATCHES; this page shows the whole reconciled attendee
/// set (synced hourly from Zoho Backstage + Bookings) with filters, search
/// and a CSV export for on-site lists / BI. Read-only by design: attendees
/// are owned by the reconcile job, never edited in the hub (CONTEXT.md 9z).
/// </summary>
[Authorize]
public class AttendeesModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;

    public AttendeesModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant)
    {
        _db = db;
        _participant = participant;
    }

    public bool AccessDenied { get; private set; }
    public List<Core.Domain.Attendee> Attendees { get; private set; } = new();

    // --- Summary tiles --------------------------------------------------
    public int TotalCount { get; private set; }
    public int TwoDayCount { get; private set; }
    /// <summary>§707.36 — active 1-day (non-2-day) ticket holders, so the tiles ADD UP.</summary>
    public int OneDayCount { get; private set; }
    public int BookedCount { get; private set; }
    public int MismatchCount { get; private set; }
    public DateTimeOffset? LastSyncedAt { get; private set; }

    // --- Filters (GET-bound so links/bookmarks keep state) ---------------
    [BindProperty(SupportsGet = true)] public string? Ticket { get; set; }
    [BindProperty(SupportsGet = true)] public string? Booking { get; set; }
    [BindProperty(SupportsGet = true)] public bool MismatchOnly { get; set; }
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }

    // --- Sort + paging (GET-bound) ---------------------------------------
    /// <summary>Sort column key: name | email | ticket | booking. Default name.</summary>
    [BindProperty(SupportsGet = true)] public string Sort { get; set; } = "name";
    /// <summary>true = descending. Default false (A→Z).</summary>
    [BindProperty(SupportsGet = true)] public bool Desc { get; set; }
    [BindProperty(SupportsGet = true)] public int PageNo { get; set; } = 1;

    public GridPage Paging { get; private set; }

    /// <summary>Direction the link for <paramref name="col"/> should request:
    /// flip when this column is already the active ascending sort, else ascending.</summary>
    public bool NextDescFor(string col) => Sort == col && !Desc;

    /// <summary>Little ▲/▼ glyph appended to the active sort header.</summary>
    public string SortIndicator(string col) => Sort != col ? "" : (Desc ? " ▼" : " ▲");

    /// <summary>ARIA <c>aria-sort</c> value for a column header.</summary>
    public string AriaSort(string col) => Sort != col ? "none" : (Desc ? "descending" : "ascending");

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        await LoadSummaryAsync(me.EventId, ct);

        var filtered = BuildQuery(me.EventId);
        var matched = await filtered.CountAsync(ct);
        Paging = GridPaging.Resolve(PageNo, GridPaging.DefaultPageSize, matched);

        Attendees = await ApplySort(filtered)
            .Skip(Paging.Skip).Take(Paging.PageSize)
            .ToListAsync(ct);
        return Page();
    }

    /// <summary>Stable, deterministic ordering for the chosen sort column.</summary>
    private IQueryable<Core.Domain.Attendee> ApplySort(IQueryable<Core.Domain.Attendee> q)
    {
        IOrderedQueryable<Core.Domain.Attendee> ordered = Sort switch
        {
            "email"   => Desc ? q.OrderByDescending(a => a.Email)         : q.OrderBy(a => a.Email),
            // §1065 — sortable, so "show me everyone from one company" is one click. Without this the
            // header would render a sort link that silently did nothing.
            "company" => Desc ? q.OrderByDescending(a => a.CompanyName)   : q.OrderBy(a => a.CompanyName),
            "ticket"  => Desc ? q.OrderByDescending(a => a.TicketStatus)  : q.OrderBy(a => a.TicketStatus),
            "booking" => Desc ? q.OrderByDescending(a => a.BookingStatus) : q.OrderBy(a => a.BookingStatus),
            _         => Desc ? q.OrderByDescending(a => a.LastName)      : q.OrderBy(a => a.LastName),
        };
        // Id tiebreak so paging is deterministic across calls.
        return ordered.ThenBy(a => a.Id);
    }

    /// <summary>CSV export of the CURRENT filter selection (not always-all,
    /// so an organizer can export e.g. just the mismatches).</summary>
    public async Task<IActionResult> OnGetExportAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var csv = await BuildExportCsvAsync(me.EventId, ';', ct);

        // UTF-8 BOM so Excel detects the encoding (Danish names).
        var bytes = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(csv)).ToArray();
        return File(bytes, "text/csv", "attendees.csv");
    }

    /// <summary>Same export as <see cref="OnGetExportAsync"/> (current filter)
    /// but delivered as a native Excel (.xlsx) workbook. Reuses the EXACT same row
    /// builder (<see cref="BuildExportCsvAsync"/>) so columns and rows are identical;
    /// only the file format differs. NOTE: the on-screen CSV download is
    /// semicolon-delimited (an existing download contract we must not change), but
    /// <see cref="CsvToXlsx"/> parses comma-delimited RFC-4180 — so the workbook is
    /// fed a comma-delimited rendering of the SAME rows/columns/values.</summary>
    public async Task<IActionResult> OnGetExportXlsxAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var csv = await BuildExportCsvAsync(me.EventId, ',', ct);
        return File(CsvToXlsx.Build(csv, "Attendees"), CsvToXlsx.ContentType, "attendees.xlsx");
    }

    /// <summary>Builds the attendee export (header + one line per row) for the
    /// CURRENT filter selection. Single source of truth shared by the CSV and the
    /// XLSX download handlers so both contain identical columns/rows. The delimiter
    /// is a parameter so the CSV handler keeps its semicolon contract while the
    /// xlsx feed uses commas (what <see cref="CsvToXlsx"/> parses into columns);
    /// fields are escaped against whichever delimiter is in use.</summary>
    private async Task<string> BuildExportCsvAsync(int eventId, char delimiter, CancellationToken ct)
    {
        var rows = await BuildQuery(eventId)
            .OrderBy(a => a.LastName).ThenBy(a => a.FirstName)
            .ToListAsync(ct);

        var sb = new StringBuilder();
        sb.AppendLine(string.Join(delimiter,
            // §1065 — Company travels into the EXPORT too. The grid and the file must answer the
            // same questions, or he filters on screen and then cannot reproduce it in Excel.
            "FirstName", "LastName", "Email", "Company", "TicketStatus", "TicketClass",
            // §326bp: the column said "Mismatch" and meant "has not chosen a Master Class yet".
            // Renamed in the EXPORT too — the operator reads this file, and a header he has to
            // decode is the same defect as a label he has to decode.
            "BookingStatus", "MasterClass", "MasterClassNotChosen", "LastSyncedUtc"));
        foreach (var a in rows)
        {
            sb.AppendLine(string.Join(delimiter,
                Csv(a.FirstName, delimiter), Csv(a.LastName, delimiter), Csv(a.Email, delimiter),
                Csv(a.CompanyName, delimiter),
                // §1077: the LABEL, for the §326bp reason — the operator reads this file, and a
                // value he has to decode ("Other" = 1-day) is the same defect as a header he has
                // to decode. The grid and the file say the same word.
                a.TicketStatus.Label(), Csv(a.TicketClassName, delimiter),
                // §1077 — the label here too: the export and the grid must say the same words, or
                // he filters on screen and cannot reproduce it in Excel (the §1065 rule).
                a.BookingStatus.Label(), Csv(a.MasterClassName, delimiter),
                a.HasReconciliationMismatch ? "YES" : "",
                a.LastSyncedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm")));
        }
        return sb.ToString();
    }

    private IQueryable<Core.Domain.Attendee> BuildQuery(int eventId)
    {
        // The ACTIVE mirror set only (§128): soft-cancelled rows are kept for history
        // but excluded from the organizer attendee view + counts (CEH == Zoho active set).
        var q = _db.Attendees.Where(a => a.EventId == eventId && a.MirrorState == MirrorState.Active);

        if (Enum.TryParse<TicketStatus>(Ticket, out var ts))
            q = q.Where(a => a.TicketStatus == ts);
        if (Enum.TryParse<MasterClassBookingStatus>(Booking, out var bs))
            q = q.Where(a => a.BookingStatus == bs);
        if (MismatchOnly)
            q = q.Where(a => a.HasReconciliationMismatch);
        if (!string.IsNullOrWhiteSpace(Search))
        {
            var s = Search.Trim();
            q = q.Where(a => a.Email.Contains(s)
                             || a.FirstName.Contains(s)
                             || a.LastName.Contains(s)
                             // §1065 — COMPANY is searchable. Operator 2026-08-11: *"search must
                             // support ability to search in company as well"*. It is the field an
                             // organizer actually has in hand ("who came from 2linkIT?") and the one
                             // a sponsor asks about, and the grid showed it nowhere.
                             || (a.CompanyName != null && a.CompanyName.Contains(s))
                             || (a.MasterClassName != null && a.MasterClassName.Contains(s)));
        }
        return q;
    }

    private async Task LoadSummaryAsync(int eventId, CancellationToken ct)
    {
        var all = _db.Attendees.Where(a => a.EventId == eventId && a.MirrorState == MirrorState.Active);
        TotalCount    = await all.CountAsync(ct);
        TwoDayCount   = await all.CountAsync(a => a.TicketStatus == TicketStatus.TwoDay, ct);
        // §707.36 (operator 2026-07-30: *"i need a field so i can see 1-day tickets also as
        // counter"*). The page counted 2-day only, so "2 attendees / 1 2-day ticket" left the
        // other one unexplained — the reader has to do the subtraction and hope it is a 1-day.
        // `Other` is precisely "an active ticket that is not 2-day" (AttendeeTicketSyncService
        // maps the class that way), which for this edition is the 1-day class.
        OneDayCount   = await all.CountAsync(a => a.TicketStatus == TicketStatus.Other, ct);
        BookedCount   = await all.CountAsync(a => a.BookingStatus != MasterClassBookingStatus.NotBooked, ct);
        MismatchCount = await all.CountAsync(a => a.HasReconciliationMismatch, ct);
        LastSyncedAt  = TotalCount == 0
            ? null
            : await all.MaxAsync(a => (DateTimeOffset?)a.LastSyncedAt, ct);
    }

    /// <summary>Outcome of the last §355 reset, shown as a flash on the page.</summary>
    public string? ResetMessage { get; private set; }
    public bool ResetIsError { get; private set; }

    /// <summary>
    /// §355 — put ONE attendee back to "freshly synced" so onboarding can be re-tested
    /// (operator 2026-07-26). The three switches are INDEPENDENT: re-testing the party flow must
    /// not have to destroy a Master Class seat.
    ///
    /// <para>Guarded on <see cref="OrganizerAuth.IsRealOrganizer"/> (§337): it re-sends mail and
    /// re-opens another person's tasks, so an acting-as session must not be able to run it.</para>
    /// </summary>
    [CommunityHub.Audit.Audit("Reset attendee onboarding",
        Category = CommunityHub.Core.Domain.AuditCategory.Admin, TargetType = "Attendee")]
    public async Task<IActionResult> OnPostResetOnboardingAsync(
        int attendeeId, bool resetWelcome, bool resetMasterClass, bool resetParty,
        [FromServices] Core.Organizer.AttendeeOnboardingResetService reset,
        CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        // 🔒 §707.40 — REFUSE for a non-2-day holder, server-side. The button is hidden for them,
        // but hiding a control is not enforcing it: a stale tab or a hand-made POST still reaches
        // this handler, and it would then reset a "Master Class seat" and a "selection invite" that
        // a 1-day holder never had. The §700 lesson — a reachable POST endpoint the UI no longer
        // offers is still an endpoint.
        // `ResendSelectionInvite` already refuses the same case inside SendSelectionInviteAsync.
        var isTwoDay = await _db.Attendees
            .Where(a => a.EventId == me.EventId && a.Id == attendeeId)
            .Select(a => a.TicketStatus == TicketStatus.TwoDay)
            .FirstOrDefaultAsync(ct);
        if (!isTwoDay)
        {
            ResetMessage = "Not reset — onboarding, the Master Class seat and the selection invite "
                + "do not apply to a 1-day ticket holder (by design: no Master Class, no hub login).";
            ResetIsError = true;
            await LoadSummaryAsync(me.EventId, ct);
            var q = BuildQuery(me.EventId);
            var n = await q.CountAsync(ct);
            Paging = GridPaging.Resolve(PageNo, GridPaging.DefaultPageSize, n);
            Attendees = await ApplySort(q).Skip(Paging.Skip).Take(Paging.PageSize).ToListAsync(ct);
            return Page();
        }

        var r = await reset.ResetAsync(
            me.EventId, attendeeId, resetWelcome, resetMasterClass, resetParty, ct);

        ResetMessage = r.Detail;
        ResetIsError = !r.Ok;

        await LoadSummaryAsync(me.EventId, ct);
        var filtered = BuildQuery(me.EventId);
        var matched = await filtered.CountAsync(ct);
        Paging = GridPaging.Resolve(PageNo, GridPaging.DefaultPageSize, matched);
        Attendees = await ApplySort(filtered).Skip(Paging.Skip).Take(Paging.PageSize).ToListAsync(ct);
        return Page();
    }

    /// <summary>
    /// 🔒 §707.14 — RE-SEND THE MASTER CLASS SELECTION INVITE for one attendee.
    /// </summary>
    /// <remarks>
    /// Operator 2026-07-30: *"how do i resend the welcome mail for an attendee — i have a welcome
    /// mail button — is that equivalent for attendees"*. It was not, and there was no lever at all:
    /// <list type="bullet">
    /// <item>*Send/Resend welcome email* sends the GENERIC welcome and is idempotent — once sent it
    /// no-ops.</item>
    /// <item>The reset-welcome path branches on role and, for an Attendee, calls
    /// <c>AttendeeOneDayWelcomeEmailService</c>, retired by §299 OPEN-26 and hard-wired to return
    /// false.</item>
    /// </list>
    /// A 2-day attendee's REAL welcome is <c>masterclass-selection-invite</c> (§241), and until now
    /// the only way to re-send it was editing <c>MasterClassInviteSentAt</c> in the database.
    ///
    /// <para>Uses <c>force: true</c> so an already-invited attendee is re-sent deliberately. The
    /// §707.14 deactivated-login guard still applies underneath — a re-send to a switched-off login
    /// is refused rather than delivering a magic-link button that cannot resolve.</para>
    ///
    /// <para>Guarded on <see cref="OrganizerAuth.IsRealOrganizer"/> (§337): it sends real mail, so
    /// an acting-as session must not be able to run it.</para>
    /// </remarks>
    [CommunityHub.Audit.Audit("Re-send Master Class selection invite",
        Category = CommunityHub.Core.Domain.AuditCategory.Admin, TargetType = "Attendee")]
    public async Task<IActionResult> OnPostResendSelectionInviteAsync(
        int attendeeId,
        [FromServices] Core.Email.MasterClassEmailService mcEmail,
        CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        try
        {
            var baseUrl = $"{Request.Scheme}://{Request.Host}";
            var sent = await mcEmail.SendSelectionInviteAsync(attendeeId, baseUrl, force: true, ct);
            ResetMessage = sent
                ? "Selection invite re-sent."
                : "Not sent — the attendee is not an active 2-day holder, has no email, or their "
                  + "login is deactivated (a magic link would not work). Re-activate the login first.";
            ResetIsError = !sent;
        }
        catch (Exception ex)
        {
            ResetMessage = $"Could not re-send the selection invite: {ex.Message}";
            ResetIsError = true;
        }

        await LoadSummaryAsync(me.EventId, ct);
        var filtered = BuildQuery(me.EventId);
        var matched = await filtered.CountAsync(ct);
        Paging = GridPaging.Resolve(PageNo, GridPaging.DefaultPageSize, matched);
        Attendees = await ApplySort(filtered).Skip(Paging.Skip).Take(Paging.PageSize).ToListAsync(ct);
        return Page();
    }

    /// <summary>CSV field for the given <paramref name="delimiter"/>: quote when the
    /// value contains the delimiter, a quote or a newline. Defaults to the original
    /// semicolon so the on-screen CSV download stays byte-identical.</summary>
    private static string Csv(string? v, char delimiter = ';')
    {
        if (string.IsNullOrEmpty(v)) return string.Empty;
        return v.Contains(delimiter) || v.Contains('"') || v.Contains('\n')
            ? '"' + v.Replace("\"", "\"\"") + '"'
            : v;
    }
}
