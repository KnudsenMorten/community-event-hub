using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.DocLibrary;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Logistics hub — the Organizer-gated landing that fans out to the feature pages, and (§6.3) the
/// place the GENERATED §3.5 files are listed: filename, link, when it was last generated, its
/// headline figure, and the last automatic run's status.
/// </summary>
/// <remarks>
/// <para>🔒 <b>Rendering this page never writes to the document library.</b> It reads what the last
/// run published. Building eleven workbooks on a page load would be slow, and worse, it would show
/// something other than what is actually in the library.</para>
///
/// <para>Auth mirrors /Organizer/SponsorAdmin/Index: signed-in Organizer only, otherwise
/// <see cref="AccessDenied"/> = true (a friendly notice, not a 401).</para>
/// </remarks>
[Authorize]
public class LogisticsModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly CommunityHubDbContext _db;
    private readonly LogisticsArtifactsService _artifacts;
    private readonly LogisticsRunService _run;

    public LogisticsModel(
        ICurrentParticipantAccessor participant,
        CommunityHubDbContext db,
        LogisticsArtifactsService artifacts,
        LogisticsRunService run)
    {
        _participant = participant;
        _db = db;
        _artifacts = artifacts;
        _run = run;
    }

    public bool AccessDenied { get; private set; }

    /// <summary>§6.3 — the generated files and the last run. Null when there is no active edition.</summary>
    public LogisticsArtifactsView? Files { get; private set; }

    /// <summary>Outcome of a "Generate now", shown once above the table.</summary>
    public string? Flash { get; private set; }

    public bool FlashIsError { get; private set; }

    /// <summary>§6.8 — outcome of an on-demand AI-grounding refresh.</summary>
    public string? GroundingFlash { get; private set; }

    public bool GroundingFlashIsError { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var gate = Gate();
        if (gate is not null) return gate;

        await LoadAsync(ct);
        return Page();
    }

    /// <summary>§6.3 "Generate now" — rebuild one artifact and overwrite it in the library.</summary>
    /// <remarks>
    /// ⚠️ <b>It does not mail.</b> The daily run owns the mail schedule; a button that also sent
    /// would let anybody reach an external venue contact by clicking twice.
    /// </remarks>
    public async Task<IActionResult> OnPostGenerateAsync(string? fileName, CancellationToken ct)
    {
        var gate = Gate();
        if (gate is not null) return gate;

        var eventId = await ActiveEventIdAsync(ct);
        if (eventId is null)
        {
            Flash = "There is no active edition.";
            FlashIsError = true;
            return Page();
        }

        var result = await _run.RegenerateOneAsync(eventId.Value, fileName ?? string.Empty, ct);

        FlashIsError = !result.Ok;
        Flash = result.Ok
            ? $"{fileName} was regenerated — {result.Headline}."
            : $"{fileName} was NOT regenerated: {result.Error}";

        await LoadAsync(ct);
        return Page();
    }

    /// <summary>
    /// §6.8 — rebuild the AI Community Helper's grounding from the document library, now.
    /// </summary>
    /// <remarks>
    /// The work order puts this control <i>"in the organizer menu alongside the §6.3 'Generate now'
    /// buttons"</i>, which is this page. It is the same shape of action — go and re-read the
    /// document library on request — so it belongs beside them rather than on a page of its own.
    /// </remarks>
    public async Task<IActionResult> OnPostRefreshGroundingAsync(
        [FromServices] CommunityHub.Core.Assistant.IAiHelperSharePointGroundingProvider? grounding,
        CancellationToken ct)
    {
        var gate = Gate();
        if (gate is not null) return gate;

        if (grounding is null)
        {
            GroundingFlash = "AI grounding is not wired on this host.";
            GroundingFlashIsError = true;
            await LoadAsync(ct);
            return Page();
        }

        var result = await grounding.RefreshAsync(ct);

        GroundingFlashIsError = !result.Ok;
        GroundingFlash = result.Ok
            // 🔑 The COUNT, not just "done" — he presses this right after dropping a file in and
            // wants to know it was picked up.
            ? $"AI grounding refreshed — {result.Documents} document(s) read from the library."
            : $"AI grounding was NOT refreshed: {result.Error}";

        await LoadAsync(ct);
        return Page();
    }

    private IActionResult? Gate()
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }
        return null;
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var eventId = await ActiveEventIdAsync(ct);
        if (eventId is null) return;
        Files = await _artifacts.BuildAsync(eventId.Value, ct);
    }

    private Task<int?> ActiveEventIdAsync(CancellationToken ct) =>
        _db.Events.Where(e => e.IsActive).Select(e => (int?)e.Id).FirstOrDefaultAsync(ct);
}
