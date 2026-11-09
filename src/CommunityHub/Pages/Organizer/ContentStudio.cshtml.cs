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
    private readonly WordPressPostRefreshService? _refresh;

    public ContentStudioModel(
        ContentStudioService studio, ICurrentParticipantAccessor participant,
        WordPressPostRefreshService? refresh = null)
    {
        _studio = studio;
        _participant = participant;
        _refresh = refresh;
    }

    // ---- §1379.8 — linked WordPress posts, "Refresh weekly" -------------------------------
    [BindProperty] public int? LinkId { get; set; }
    [BindProperty] public long LinkPostId { get; set; }
    [BindProperty] public string? LinkLabel { get; set; }
    [BindProperty] public string? LinkTemplate { get; set; }
    [BindProperty] public bool LinkRefreshWeekly { get; set; }

    public IReadOnlyList<CommunityHub.Core.Domain.WordPressRefreshPost> LinkedPosts { get; private set; } =
        Array.Empty<CommunityHub.Core.Domain.WordPressRefreshPost>();
    public IReadOnlyList<string> SupportedTokens => WordPressPostTokenRenderer.SupportedTokens;

    /// <summary>The row the preview belongs to (0 = the new-link form).</summary>
    public int? PreviewLinkId { get; private set; }
    public WordPressPostRender? LinkPreview { get; private set; }
    public string? LinkMessage { get; private set; }
    public bool LinkMessageIsError { get; private set; }

    public async Task<IActionResult> OnPostSaveLinkAsync(CancellationToken ct)
    {
        var me = Guard();
        if (me is null) return AccessDenied ? Page() : RedirectToPage("/Login");
        if (_refresh is not null)
        {
            var error = await _refresh.SaveAsync(
                me.EventId, LinkId, LinkPostId, LinkLabel, LinkTemplate, LinkRefreshWeekly, ct);
            LinkMessage = error ?? $"Saved the link to WordPress post {LinkPostId}.";
            LinkMessageIsError = error is not null;
        }
        return await ReloadAsync(me.EventId, ct);
    }

    public async Task<IActionResult> OnPostPreviewLinkAsync(CancellationToken ct)
    {
        var me = Guard();
        if (me is null) return AccessDenied ? Page() : RedirectToPage("/Login");
        if (_refresh is not null)
        {
            // Renders what is in the textarea (saved or not), so a template can be tried before saving.
            LinkPreview = await _refresh.PreviewAsync(me.EventId, LinkTemplate, ct);
            PreviewLinkId = LinkId ?? 0;
        }
        return await ReloadAsync(me.EventId, ct);
    }

    public async Task<IActionResult> OnPostRefreshNowAsync(CancellationToken ct)
    {
        var me = Guard();
        if (me is null) return AccessDenied ? Page() : RedirectToPage("/Login");
        if (_refresh is not null && LinkId is int id)
        {
            var result = await _refresh.RefreshNowAsync(me.EventId, id, DateTimeOffset.UtcNow, ct);
            LinkMessage = result.Message;
            LinkMessageIsError = result.Updated + result.Unchanged == 0;
        }
        return await ReloadAsync(me.EventId, ct);
    }

    public async Task<IActionResult> OnPostUnlinkAsync(CancellationToken ct)
    {
        var me = Guard();
        if (me is null) return AccessDenied ? Page() : RedirectToPage("/Login");
        if (_refresh is not null && LinkId is int id)
        {
            var removed = await _refresh.DeleteAsync(me.EventId, id, ct);
            LinkMessage = removed ? "Unlinked. The WordPress post itself is unchanged." : "That link no longer exists.";
            LinkMessageIsError = !removed;
        }
        return await ReloadAsync(me.EventId, ct);
    }

    private async Task<IActionResult> ReloadAsync(int eventId, CancellationToken ct)
    {
        Preview = await _studio.PreviewAsync(eventId, Kind, ct);
        await LoadLinkedAsync(eventId, ct);
        return Page();
    }

    private async Task LoadLinkedAsync(int eventId, CancellationToken ct)
    {
        if (_refresh is not null) LinkedPosts = await _refresh.ListAsync(eventId, ct);
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
        await LoadLinkedAsync(me.EventId, ct);
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

        return await ReloadAsync(me.EventId, ct);
    }

    private CurrentParticipant? Guard()
    {
        var me = _participant.Current;
        if (me is null) return null;
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return null; }
        return me;
    }
}
