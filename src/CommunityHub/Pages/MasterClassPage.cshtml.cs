using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages;

/// <summary>
/// Master Class attendee LANDING PAGE (FEATURE 2): <c>/MasterClassPage/{sessionId}</c>.
/// Shows the speaker-authored PREP content, the Q&amp;A comment thread (public within
/// the MC), and the 1:1 private-question form + the attendee's own answered questions.
///
/// <b>Who can see it:</b> an attendee with a CONFIRMED <see cref="MasterClassSignup"/>
/// for the session (reached by the emailed per-attendee bearer token <c>?t=</c>, or as
/// a normally signed-in attendee resolved by email), OR a signed-in speaker linked to
/// the master class / an organizer (they can always view their own MC). Everyone else
/// gets a friendly "not available" state.
///
/// All comment + ask actions re-check the gate server-side in
/// <see cref="MasterClassPrepService"/>. Mobile-first (~360px) + a11y (semantic
/// headings, labelled textareas, <c>role="status"</c> messages).
/// </summary>
[AllowAnonymous]
public class MasterClassPageModel : PageModel
{
    private readonly MasterClassPrepService _prep;
    private readonly MasterClassSignupService _signups;
    private readonly ICurrentParticipantAccessor _participant;

    public MasterClassPageModel(
        MasterClassPrepService prep,
        MasterClassSignupService signups,
        ICurrentParticipantAccessor participant)
    {
        _prep = prep;
        _signups = signups;
        _participant = participant;
    }

    public int SessionId { get; private set; }
    public string Token { get; private set; } = string.Empty;

    /// <summary>True when the session is not a master class / not found in the edition.</summary>
    public bool NotFoundState { get; private set; }

    /// <summary>True when there IS such a master class but this viewer may not see it.</summary>
    public bool AccessDenied { get; private set; }

    public MasterClassPrepService.LandingView? View { get; private set; }
    public IReadOnlyList<MasterClassComment> Comments { get; private set; } =
        Array.Empty<MasterClassComment>();
    /// <summary>The viewing attendee's own 1:1 questions (when an attendee is viewing).</summary>
    public IReadOnlyList<SessionQuestion> MyQuestions { get; private set; } =
        Array.Empty<SessionQuestion>();

    /// <summary>True when the viewer is the confirmed ATTENDEE (can ask a 1:1 question).</summary>
    public bool IsAttendeeViewer { get; private set; }
    /// <summary>True when the viewer is a linked speaker / organizer of this MC.</summary>
    public bool IsParticipantViewer { get; private set; }

    public string? Message { get; private set; }
    public string? Error { get; private set; }

    [BindProperty] public string? CommentBody { get; set; }
    [BindProperty] public int? ParentCommentId { get; set; }
    [BindProperty] public string? QuestionText { get; set; }

    // Resolved viewer identity for the request (one of attendee / participant).
    private CommunityHub.Core.Domain.Attendee? _attendee;
    private CurrentParticipant? _me;
    private int _eventId;

    public async Task<IActionResult> OnGetAsync(int sessionId, string? t, string? msg, CancellationToken ct)
    {
        SessionId = sessionId;
        Token = t ?? string.Empty;
        Message = msg;
        await ResolveAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostCommentAsync(int sessionId, string? t, CancellationToken ct)
    {
        SessionId = sessionId;
        Token = t ?? string.Empty;
        if (!await ResolveAsync(ct)) return Page();

        try
        {
            if (IsAttendeeViewer && _attendee is not null)
                await _prep.AddAttendeeCommentAsync(
                    _eventId, sessionId, _attendee.Id, CommentBody ?? string.Empty, ParentCommentId, ct);
            else if (IsParticipantViewer && _me is not null)
                await _prep.AddParticipantCommentAsync(
                    _eventId, sessionId, _me.ParticipantId, _me.Role, CommentBody ?? string.Empty, ParentCommentId, ct);
            else
                throw new MasterClassPrepAccessDeniedException("You may not comment on this master class.");

            return Redirect(SelfUrl("Comment posted."));
        }
        catch (MasterClassPrepAccessDeniedException ex) { Error = ex.Message; }
        catch (MasterClassPrepValidationException ex) { Error = ex.Message; }

        await ReloadContentAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAskAsync(int sessionId, string? t, CancellationToken ct)
    {
        SessionId = sessionId;
        Token = t ?? string.Empty;
        if (!await ResolveAsync(ct)) return Page();

        if (!IsAttendeeViewer || _attendee is null)
        {
            Error = "Only a confirmed attendee can ask a 1:1 question.";
            await ReloadContentAsync(ct);
            return Page();
        }

        try
        {
            await _prep.AskPrivateQuestionAsync(_eventId, sessionId, _attendee.Id, QuestionText ?? string.Empty, ct);
            return Redirect(SelfUrl("Your private question was sent to the speakers."));
        }
        catch (MasterClassPrepAccessDeniedException ex) { Error = ex.Message; }
        catch (MasterClassPrepValidationException ex) { Error = ex.Message; }

        await ReloadContentAsync(ct);
        return Page();
    }

    /// <summary>
    /// Resolve the viewer + the master class, set the access flags, and load the page
    /// content. Returns true when the viewer may see the page (and content is loaded).
    /// </summary>
    private async Task<bool> ResolveAsync(CancellationToken ct)
    {
        // Resolve the attendee first (emailed token, else a signed-in attendee by email),
        // then a signed-in speaker/organizer.
        _attendee = await _signups.ResolveByTokenAsync(Token, ct);
        _me = _participant.Current;
        if (_attendee is null && _me is not null)
            _attendee = await _signups.ResolveByEmailAsync(_me.EventId, _me.Email, ct);

        // Pick the edition scope: the attendee's edition, else the signed-in user's.
        _eventId = _attendee?.EventId ?? _me?.EventId ?? 0;
        if (_eventId == 0) { AccessDenied = true; return false; }

        View = await _prep.GetLandingAsync(_eventId, SessionId, ct);
        if (View is null) { NotFoundState = true; return false; }

        // Attendee path: a CONFIRMED seat unlocks view + comment + ask.
        if (_attendee is not null
            && await _prep.AttendeeHasConfirmedSeatAsync(_eventId, SessionId, _attendee.Id, ct))
        {
            IsAttendeeViewer = true;
        }

        // Participant path: a linked speaker / organizer can always view their own MC.
        if (_me is not null
            && await _prep.CanParticipantViewAsync(_eventId, SessionId, _me.ParticipantId, _me.Role, ct))
        {
            IsParticipantViewer = true;
        }

        if (!IsAttendeeViewer && !IsParticipantViewer) { AccessDenied = true; return false; }

        await ReloadContentAsync(ct);
        return true;
    }

    private async Task ReloadContentAsync(CancellationToken ct)
    {
        if (View is null) return;
        Comments = await _prep.LoadCommentsAsync(_eventId, SessionId, ct);
        MyQuestions = IsAttendeeViewer && _attendee is not null
            ? await _prep.LoadMyPrivateQuestionsAsync(_eventId, SessionId, _attendee.Id, ct)
            : Array.Empty<SessionQuestion>();
    }

    /// <summary>The page's own URL with the bearer token preserved + a status message.</summary>
    private string SelfUrl(string msg)
    {
        var url = Url.Page("/MasterClassPage", null,
            new { sessionId = SessionId, t = string.IsNullOrEmpty(Token) ? null : Token, msg },
            Request.Scheme);
        return url ?? $"/MasterClassPage/{SessionId}";
    }
}
