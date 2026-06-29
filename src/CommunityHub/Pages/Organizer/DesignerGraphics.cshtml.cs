using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Graphics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Organizer trigger console for the EXTERNAL-DESIGNER graphics pipeline (REQUIREMENTS §165).
/// One button RUNS the pipeline (pull speaker photos NAMED BY NAME with speaker-upload-wins →
/// build per-session / per-master-class / per-track folders) and reports the counts; a second
/// downloads the Excel BRIEF (one row per session: facts + speaker names + folder link + photo
/// file names). Reuses the existing job/trigger shape (cf. the §18 SharePoint graphics pull on
/// <c>/Organizer/Graphics</c>).
///
/// Organizer-only. Everything here is ORGANIZER-facing — a speaker is never shown a SharePoint
/// link. Inert + safe when the store / folder paths are unconfigured (the Run button reports
/// "not configured" and writes nothing). Mobile-first (~360px).
/// </summary>
[Authorize]
public class DesignerGraphicsModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly ExternalDesignerGraphicsService _designer;

    public DesignerGraphicsModel(
        ICurrentParticipantAccessor participant,
        ExternalDesignerGraphicsService designer)
    {
        _participant = participant;
        _designer = designer;
    }

    public bool AccessDenied { get; private set; }
    public string? Message { get; private set; }
    public string? Error { get; private set; }

    /// <summary>True when the live store can write — the Run button does real work.</summary>
    public bool Configured { get; private set; }

    /// <summary>The last run's counts (null until the organizer presses Run this request).</summary>
    public DesignerPipelineResult? LastRun { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        Configured = _designer.CanManage;
        return Page();
    }

    public async Task<IActionResult> OnPostRunAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        Configured = _designer.CanManage;
        if (!Configured)
        {
            Message = "The designer-graphics SharePoint folders are not configured yet — nothing was "
                + "written. Set the Graphics:SharePoint site + the Speaker-photos / Sessions / "
                + "Master-class / Tracks folder paths to enable the pipeline.";
            return Page();
        }

        LastRun = await _designer.RunPipelineAsync(me.EventId, ct);
        Message = $"Pipeline run complete: {LastRun.PhotosPulled} photo(s) pulled "
            + $"({LastRun.UploadsPreserved} speaker upload(s) preserved, {LastRun.PhotosSkipped} skipped), "
            + $"{LastRun.SessionFolders} session / {LastRun.MasterClassFolders} master-class / "
            + $"{LastRun.TrackFolders} track folder(s) built. Download the brief below.";
        return Page();
    }

    /// <summary>
    /// Download the Excel brief (a GET handler so it is a plain link). Built on demand from the
    /// current edition state — always reflects the latest photos / folders. Organizer-only.
    /// </summary>
    public async Task<IActionResult> OnGetBriefAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        var bytes = await _designer.BuildBriefAsync(me.EventId, ct);
        return File(bytes, ExternalDesignerGraphicsService.BriefContentType, ExternalDesignerGraphicsService.BriefFileName);
    }
}
