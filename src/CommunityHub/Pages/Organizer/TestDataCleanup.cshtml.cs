using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Organizer-gated go-live test-data cleanup (REQUIREMENTS §1). Lists every
/// <see cref="Participant.IsTestUser"/> row in the edition with what would happen
/// to it (hard-deleted when clean, deactivated when it has engagement), and a
/// confirm-gated action that runs the cleanup via <see cref="TestDataCleanupService"/>.
/// The service reuses the proven safe per-participant delete semantics, so a test
/// row can never orphan real data, and real participants are never touched.
/// </summary>
[Authorize]
public class TestDataCleanupModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly TestDataCleanupService _cleanup;

    public TestDataCleanupModel(
        ICurrentParticipantAccessor participant,
        TestDataCleanupService cleanup)
    {
        _participant = participant;
        _cleanup = cleanup;
    }

    public bool AccessDenied { get; private set; }
    public TestDataCleanupService.CleanupPreview? Preview { get; private set; }
    public string? DoneMessage { get; private set; }
    /// <summary>§334 — set when the typed confirmation was missing or wrong; nothing ran.</summary>
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        Preview = await _cleanup.PreviewAsync(me.EventId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostCleanupAsync(string? confirmPhrase, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        // §334: a bulk delete/deactivate across every row flagged as test data. The preview
        // makes it honest; the typed phrase makes it deliberate — and the check is here, on the
        // server, so it cannot be skipped by posting the handler directly.
        if (!TypedConfirmation.Matches(confirmPhrase, TypedConfirmation.ConfirmPhrase))
        {
            Error = TypedConfirmation.Rejection(
                TypedConfirmation.ConfirmPhrase, "run the test-data cleanup");
            Preview = await _cleanup.PreviewAsync(me.EventId, ct);
            return Page();
        }

        var result = await _cleanup.CleanupAsync(me.EventId, ct);
        DoneMessage = $"{result.HardDeleted}|{result.Deactivated}";

        // Re-read the preview so the page reflects the post-cleanup state (the
        // remaining deactivated test rows still list, hard-deleted ones are gone).
        Preview = await _cleanup.PreviewAsync(me.EventId, ct);
        return Page();
    }
}
