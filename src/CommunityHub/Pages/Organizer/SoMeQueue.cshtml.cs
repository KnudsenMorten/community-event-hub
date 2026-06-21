using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Organizer curation UI for the LinkedIn company-page SoMe scheduling queue
/// (REQUIREMENTS §19): list scheduled posts (the social-media calendar),
/// fine-tune text, Preview (render the exact post that will publish),
/// Active/Inactive toggle, reschedule, and ad-hoc one-off compose. Organizer-only,
/// mobile-first, a11y.
/// </summary>
[Authorize]
public class SoMeQueueModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly SoMeQueueService _queue;

    public SoMeQueueModel(ICurrentParticipantAccessor participant, SoMeQueueService queue)
    {
        _participant = participant;
        _queue = queue;
    }

    public bool AccessDenied { get; private set; }
    public string? Message { get; private set; }

    public IReadOnlyList<SoMePost> Posts { get; private set; } = Array.Empty<SoMePost>();

    /// <summary>The post being previewed (when the Preview handler ran), else null.</summary>
    public SoMePostPreview? Preview { get; private set; }
    public int? PreviewPostId { get; private set; }

    // Ad-hoc compose fields.
    [BindProperty] public string? AdHocText { get; set; }
    [BindProperty] public string? AdHocImageRef { get; set; }
    [BindProperty] public DateTime? AdHocScheduledAt { get; set; }

    // Edit fields.
    [BindProperty] public int PostId { get; set; }
    [BindProperty] public string? EditText { get; set; }
    [BindProperty] public string? EditImageRef { get; set; }
    [BindProperty] public DateTime? RescheduleAt { get; set; }
    [BindProperty] public bool SetActive { get; set; }

    private CurrentParticipant? Guard()
    {
        var me = _participant.Current;
        if (me is null) return null;
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return null; }
        return me;
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>Create an ad-hoc one-off post directly into the queue.</summary>
    public async Task<IActionResult> OnPostAdHocAsync(CancellationToken ct)
    {
        var me = Guard();
        if (me is null) return AccessDenied ? Page() : RedirectToPage("/Login");

        if (string.IsNullOrWhiteSpace(AdHocText) || AdHocScheduledAt is null)
        {
            Message = "An ad-hoc post needs text and a scheduled date/time.";
        }
        else
        {
            await _queue.CreateAdHocPostAsync(
                me.EventId, AdHocText!, AdHocImageRef,
                new DateTimeOffset(AdHocScheduledAt.Value, TimeSpan.Zero),
                tags: null, byEmail: me.Email, ct);
            Message = "Ad-hoc post added to the queue.";
        }
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>Fine-tune a post's text + image (a non-blank override wins).</summary>
    public async Task<IActionResult> OnPostEditAsync(CancellationToken ct)
    {
        var me = Guard();
        if (me is null) return AccessDenied ? Page() : RedirectToPage("/Login");

        var ok = await _queue.EditAsync(me.EventId, PostId, EditText, EditImageRef, me.Email, ct);
        Message = ok ? "Post updated." : "Post not found.";
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>Toggle the Active/Inactive flag (an Inactive post never publishes).</summary>
    public async Task<IActionResult> OnPostToggleActiveAsync(CancellationToken ct)
    {
        var me = Guard();
        if (me is null) return AccessDenied ? Page() : RedirectToPage("/Login");

        var ok = await _queue.SetActiveAsync(me.EventId, PostId, SetActive, me.Email, ct);
        Message = ok ? (SetActive ? "Post activated." : "Post deactivated (won't publish).") : "Post not found.";
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>Reschedule a post to a new date/time.</summary>
    public async Task<IActionResult> OnPostRescheduleAsync(CancellationToken ct)
    {
        var me = Guard();
        if (me is null) return AccessDenied ? Page() : RedirectToPage("/Login");

        if (RescheduleAt is null)
        {
            Message = "Pick a date/time to reschedule.";
        }
        else
        {
            var ok = await _queue.RescheduleAsync(
                me.EventId, PostId, new DateTimeOffset(RescheduleAt.Value, TimeSpan.Zero), me.Email, ct);
            Message = ok ? "Post rescheduled." : "Post not found.";
        }
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>Render the exact post that will publish (the Preview button).</summary>
    public async Task<IActionResult> OnPostPreviewAsync(CancellationToken ct)
    {
        var me = Guard();
        if (me is null) return AccessDenied ? Page() : RedirectToPage("/Login");

        Preview = await _queue.PreviewAsync(me.EventId, PostId, ct);
        PreviewPostId = PostId;
        if (Preview is null) Message = "Post not found.";
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    private async Task LoadAsync(int eventId, CancellationToken ct)
    {
        Posts = await _queue.ListAsync(eventId, ct);
    }
}
