using CommunityHub.Auth;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace CommunityHub.Pages.Forms;

/// <summary>
/// Self-service "request a change" page for an already-submitted form once the
/// edition lock date has passed and the form itself is read-only
/// (REQUIREMENTS §21 Participant: "Edit-after-submit / request-change path once a
/// form is deadline-locked"). The participant types what they need changed; the
/// request lands on the EXISTING organizer Action Queue (no new table, no email)
/// via <see cref="FormChangeRequestService"/>. Every locked Forms/* page links here.
/// </summary>
[Authorize]
public class RequestChangeModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly FormChangeRequestService _changes;
    private readonly IStringLocalizer<SharedResource> _loc;

    public RequestChangeModel(
        ICurrentParticipantAccessor participant,
        FormChangeRequestService changes,
        IStringLocalizer<SharedResource> loc)
    {
        _participant = participant;
        _changes = changes;
        _loc = loc;
    }

    /// <summary>Which form this request is about (route/query token, defaults General).</summary>
    [BindProperty(SupportsGet = true)]
    public string Topic { get; set; } = nameof(FormTopic.General);

    [BindProperty]
    public string Message { get; set; } = string.Empty;

    public string TopicLabel { get; private set; } = string.Empty;

    /// <summary>True once the form was submitted successfully (show the toast + hide the form).</summary>
    public bool Submitted { get; private set; }

    /// <summary>The participant's existing OPEN change requests, newest first.</summary>
    public IReadOnlyList<(string Summary, DateTimeOffset At)> OpenRequests { get; private set; }
        = Array.Empty<(string, DateTimeOffset)>();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        SetTopicLabel();
        await LoadOpenRequestsAsync(me.EventId, me.ParticipantId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        SetTopicLabel();

        var topic = FormChangeRequestService.ParseTopic(Topic);
        var result = await _changes.SubmitAsync(
            me.EventId, me.ParticipantId, topic, Message, ct);

        if (!result.Accepted)
        {
            // The only participant-correctable failure is an empty / too-long
            // message; everything else is a generic problem message.
            var error = result.FailureReason switch
            {
                "empty"    => _loc["ReqChange.ErrEmpty"].Value,
                "too-long" => _loc["ReqChange.ErrTooLong"].Value,
                _          => _loc["ReqChange.ErrGeneric"].Value,
            };
            ModelState.AddModelError(nameof(Message), error);
            await LoadOpenRequestsAsync(me.EventId, me.ParticipantId, ct);
            return Page();
        }

        Submitted = true;
        Message = string.Empty;
        await LoadOpenRequestsAsync(me.EventId, me.ParticipantId, ct);
        return Page();
    }

    private void SetTopicLabel() =>
        TopicLabel = FormChangeRequestService.TopicLabel(
            FormChangeRequestService.ParseTopic(Topic));

    private async Task LoadOpenRequestsAsync(int eventId, int participantId, CancellationToken ct)
    {
        var items = await _changes.GetOpenForParticipantAsync(eventId, participantId, ct);
        OpenRequests = items
            .Select(a => (a.Summary, a.UpdatedAt ?? a.CreatedAt))
            .ToList();
    }
}
