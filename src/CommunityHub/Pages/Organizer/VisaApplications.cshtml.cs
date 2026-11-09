using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Visa;
using CommunityHub.Export;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// §1374 — the organizer side of the visa form: everyone flagged "Needs visa" and whether they have
/// submitted, one application opened in full, the application ID (the nyidanmark.dk case number, shown
/// to the applicant on <c>/Forms/Visa</c>), and the Excel / PDF exports. Reading is for organizer
/// access; changes need a real organizer (not an acting-as session).
/// </summary>
[Authorize]
public class VisaApplicationsModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly VisaApplicationService _visa;
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;
    private readonly VisaLetterStore _letters;

    public VisaApplicationsModel(ICurrentParticipantAccessor participant, VisaApplicationService visa,
        CommunityHubDbContext db, TimeProvider clock, VisaLetterStore letters)
    {
        _participant = participant;
        _visa = visa;
        _db = db;
        _clock = clock;
        _letters = letters;
    }

    [BindProperty(SupportsGet = true)] public int? Show { get; set; }
    [BindProperty] public int ParticipantId { get; set; }
    [BindProperty] public string? ApplicationId { get; set; }
    /// <summary>§1383 — the PDF handed back to the applicant.</summary>
    [BindProperty] public IFormFile? Letter { get; set; }

    /// <summary>§1383 — uploaded letters by participant (no bytes).</summary>
    public IReadOnlyDictionary<int, VisaLetterInfo> Letters { get; private set; } = new Dictionary<int, VisaLetterInfo>();

    public bool AccessDenied { get; private set; }
    public bool CanEdit { get; private set; }
    public IReadOnlyList<VisaRow> Rows { get; private set; } = Array.Empty<VisaRow>();
    public IReadOnlyList<(int Id, string Label)> Candidates { get; private set; } = Array.Empty<(int, string)>();
    public VisaRow? Selected { get; private set; }
    public string? Message { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!me.Role.HasOrganizerAccess()) { AccessDenied = true; return Page(); }

        CanEdit = OrganizerAuth.IsRealOrganizer(me);
        await _visa.SyncTasksAsync(me.EventId, ct);   // §1374 — everyone flagged has their visa task
        Rows = await _visa.ListAsync(me.EventId, ct);
        Candidates = await _visa.CandidatesAsync(me.EventId, ct);
        Letters = await _letters.InfosAsync(me.EventId, ct);
        Selected = Show is int id ? Rows.FirstOrDefault(r => r.ParticipantId == id) : null;
        if (TempData["VisaMessage"] is string m) Message = m;
        return Page();
    }

    [CommunityHub.Audit.Audit("Flag a participant as needing a visa", TargetType = "Participant")]
    public Task<IActionResult> OnPostAddAsync(CancellationToken ct) => FlagAsync(true, ct);

    [CommunityHub.Audit.Audit("Remove a participant's needs-visa flag", TargetType = "Participant")]
    public Task<IActionResult> OnPostRemoveAsync(CancellationToken ct) => FlagAsync(false, ct);

    private async Task<IActionResult> FlagAsync(bool needsVisa, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var ok = await _visa.SetNeedsVisaAsync(me.EventId, ParticipantId, needsVisa, ct);
        TempData["VisaMessage"] = !ok ? "Participant not found."
            : needsVisa ? "Added — they now see \"Visa application\" in their menu."
            : "Removed — the form is hidden from them. Anything they already sent is kept.";
        return RedirectToPage();
    }

    [CommunityHub.Audit.Audit("Set a visa application ID", TargetType = "VisaApplication")]
    public async Task<IActionResult> OnPostApplicationIdAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var ok = await _visa.SetApplicationIdAsync(me.EventId, ParticipantId, ApplicationId, ct);
        TempData["VisaMessage"] = !ok ? "Participant not found."
            : string.IsNullOrWhiteSpace(ApplicationId) ? "Application ID cleared."
            : "Application ID saved — the applicant sees it at the top of their visa form.";
        return RedirectToPage(new { Show = ParticipantId });
    }

    /// <summary>
    /// §1383 — upload (or replace) the PDF for one applicant, typically the invitation letter once the
    /// application has gone to the Danish authorities. Real organizers only; the content must be a PDF.
    /// </summary>
    [CommunityHub.Audit.Audit("Upload a visa letter (PDF) for an applicant", TargetType = "VisaLetter")]
    public async Task<IActionResult> OnPostLetterAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        string message;
        if (Letter is null || Letter.Length == 0) message = "Choose a PDF file to upload.";
        else if (Letter.Length > VisaLetterStore.MaxBytes) message = $"The file is too large (max {VisaLetterStore.MaxBytes / (1024 * 1024)} MB).";
        else
        {
            using var ms = new MemoryStream();
            await Letter.CopyToAsync(ms, ct);
            var problem = await _letters.UploadAsync(me.EventId, ParticipantId, Letter.FileName, ms.ToArray(), ct);
            message = problem ?? "Letter uploaded — the applicant can download it from their visa form.";
        }
        TempData["VisaMessage"] = message;
        return RedirectToPage(new { Show = ParticipantId });
    }

    [CommunityHub.Audit.Audit("Remove a visa letter", TargetType = "VisaLetter")]
    public async Task<IActionResult> OnPostRemoveLetterAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        TempData["VisaMessage"] = await _letters.RemoveAsync(me.EventId, ParticipantId, ct)
            ? "Letter removed — the applicant no longer sees it." : "There was no letter to remove.";
        return RedirectToPage(new { Show = ParticipantId });
    }

    /// <summary>§1383 — an organizer opens the letter stored for one applicant.</summary>
    public async Task<IActionResult> OnGetLetterAsync(int id, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!me.Role.HasOrganizerAccess()) return Forbid();

        var letter = await _letters.GetAsync(me.EventId, id, ct);
        return letter is null ? NotFound() : File(letter.Content, "application/pdf", letter.FileName);
    }

    public async Task<IActionResult> OnGetXlsxAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!me.Role.HasOrganizerAccess()) return Forbid();

        var csv = VisaApplicationService.BuildCsv(await _visa.ListAsync(me.EventId, ct));
        return File(CsvToXlsx.Build(csv, "Visa applications"), CsvToXlsx.ContentType, "visa-applications.xlsx");
    }

    /// <summary>PDF of one applicant (<c>?id=</c>) or of everyone who submitted.</summary>
    public async Task<IActionResult> OnGetPdfAsync(int? id, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!me.Role.HasOrganizerAccess()) return Forbid();

        var rows = (await _visa.ListAsync(me.EventId, ct))
            .Where(r => id is int one ? r.ParticipantId == one : r.Submitted)
            .ToList();
        if (id is not null && rows.Count == 0) return NotFound();

        var eventName = await _db.Events.AsNoTracking().Where(e => e.Id == me.EventId)
            .Select(e => e.DisplayName).FirstOrDefaultAsync(ct) ?? "Event";
        var pdf = VisaApplicationPdf.Render(rows, eventName, _clock.GetUtcNow());
        var name = id is not null ? $"visa-application-{Slug(rows[0].FullName)}.pdf" : "visa-applications.pdf";
        return File(pdf, "application/pdf", name);
    }

    private static string Slug(string s)
    {
        var chars = s.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length == 0 ? "applicant" : slug;
    }
}
