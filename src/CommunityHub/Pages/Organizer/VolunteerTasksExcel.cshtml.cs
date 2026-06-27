using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Volunteers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// ORGANIZER "Tasks — Excel" (§151): export EVERY volunteer task to .xlsx for bulk
/// editing, then import the edited sheet back. Upsert is keyed on the immutable
/// <see cref="VolunteerTask.ExternalKey"/> (column 1) — a row WITH an id updates that
/// task, a row with a BLANK id creates a new one (and gets a fresh key + a generated
/// description). All work goes through <see cref="VolunteerTaskExcelService"/>.
///
/// The service is built from already-registered dependencies (DbContext, the guidance
/// generator, the clock) so this page needs no extra DI wiring.
/// </summary>
[Authorize]
public class VolunteerTasksExcelModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly VolunteerTaskExcelService _excel;
    private readonly ILogger<VolunteerTasksExcelModel> _logger;

    public VolunteerTasksExcelModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        ITaskGuidanceGenerator guidance,
        TimeProvider clock,
        ILogger<VolunteerTasksExcelModel> logger)
    {
        _participant = participant;
        _excel = new VolunteerTaskExcelService(db, guidance, clock);
        _logger = logger;
    }

    public bool AccessDenied { get; private set; }
    public string? Notice { get; private set; }
    public bool IsError { get; private set; }

    /// <summary>The last import summary, when returning from an upload.</summary>
    public TaskExcelImportResult? Result { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }
        await Task.CompletedTask;
        return Page();
    }

    public async Task<IActionResult> OnGetExportAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        var bytes = await _excel.ExportAllAsync(me.EventId, ct);
        return File(bytes, VolunteerTaskExcelService.ContentType, "volunteer-tasks.xlsx");
    }

    public async Task<IActionResult> OnPostImportAsync(IFormFile? taskFile, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        if (taskFile is null || taskFile.Length == 0)
        {
            IsError = true;
            Notice = "Choose an .xlsx file to import.";
            return Page();
        }
        var ext = Path.GetExtension(taskFile.FileName).ToLowerInvariant();
        if (ext != ".xlsx" && ext != ".xlsm")
        {
            IsError = true;
            Notice = "Upload an Excel .xlsx file (the .xls binary format is not supported).";
            return Page();
        }

        try
        {
            await using var stream = taskFile.OpenReadStream();
            Result = await _excel.ImportAsync(me.EventId, stream, ct);
            Notice = $"Import complete: {Result.Created} created, {Result.Updated} updated"
                     + (Result.Skipped > 0 ? $", {Result.Skipped} skipped (unknown id)." : ".");
            return Page();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Volunteer task Excel import failed.");
            IsError = true;
            Notice = "Import failed: " + ex.Message;
            return Page();
        }
    }
}
