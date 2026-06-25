using CommunityHub.Auth;
using CommunityHub.Core.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Content Studio (REQUIREMENTS §31) — the organizer tool that generates marketing
/// content from live hub data: a <b>ticket-sales</b> momentum update (from the
/// aggregate attendee telemetry) and a <b>master-class</b> announcement (each class
/// with its speaker(s) + abstract). It previews the WordPress draft body AND the
/// LinkedIn short text, and — gated by the <c>content-studio</c> feature — creates a
/// WordPress <b>DRAFT</b> the operator validates in wp-admin before publishing.
/// v1 never publishes anything. Organizer-only.
/// </summary>
[Authorize]
public class ContentStudioModel : PageModel
{
    private readonly ContentStudioService _studio;
    private readonly ICurrentParticipantAccessor _participant;

    public ContentStudioModel(ContentStudioService studio, ICurrentParticipantAccessor participant)
    {
        _studio = studio;
        _participant = participant;
    }

    [BindProperty(SupportsGet = true)] public ContentKind Kind { get; set; } = ContentKind.TicketSales;

    public bool AccessDenied { get; private set; }
    public bool WordPressReady => _studio.WordPressReady;
    public GeneratedContent? Preview { get; private set; }
    public string? Message { get; private set; }
    public bool MessageIsError { get; private set; }
    public string? DraftEditUrl { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = Guard();
        if (me is null) return AccessDenied ? Page() : RedirectToPage("/Login");
        Preview = await _studio.PreviewAsync(me.EventId, Kind, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostCreateDraftAsync(CancellationToken ct)
    {
        var me = Guard();
        if (me is null) return AccessDenied ? Page() : RedirectToPage("/Login");

        var result = await _studio.CreateWordPressDraftAsync(me.EventId, Kind, ct);
        Message = result.Message;
        MessageIsError = !result.Created;
        DraftEditUrl = result.EditUrl;

        Preview = await _studio.PreviewAsync(me.EventId, Kind, ct);
        return Page();
    }

    private CurrentParticipant? Guard()
    {
        var me = _participant.Current;
        if (me is null) return null;
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return null; }
        return me;
    }
}
