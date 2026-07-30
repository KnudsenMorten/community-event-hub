using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages;

/// <summary>
/// Master Class attendee LANDING PAGE (FEATURE 2): <c>/MasterClassPage/{sessionId}</c>.
/// Shows the speaker-authored PREP content and the Group Q&amp;A comment thread, which is
/// unique to (scoped to) THIS Master Class session.
///
/// <b>§136 (operator 2026-06-27):</b> the attendee 1:1 private-question form is removed;
/// <see cref="OnPostAsk"/> is now INERT (never writes a 1:1 question). The Group
/// Q&amp;A (<see cref="MasterClassComment"/>, per <see cref="SessionId"/>) is the only
/// attendee question channel.
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
    // §383. OPTIONAL so older test constructions keep compiling (the same pattern
    // EditParticipant uses for RoleChangeTaskReconciler); DI always supplies it at runtime.
    // Every use is null-conditional, so a test that omits it simply sends no notifications.
    private readonly MasterClassNotificationService? _notify;

    public MasterClassPageModel(
        MasterClassPrepService prep,
        MasterClassSignupService signups,
        ICurrentParticipantAccessor participant,
        MasterClassNotificationService? notify = null)
    {
        _prep = prep;
        _signups = signups;
        _participant = participant;
        _notify = notify;
    }

    /// <summary>§383 — is the viewer subscribed to "the speakers updated the instructions" (box 2)?</summary>
    public bool SubscribedInstructions { get; private set; } = true;

    /// <summary>§383 — is the viewer subscribed to "someone posted in the Q&amp;A" (box 3)?</summary>
    public bool SubscribedQandA { get; private set; } = true;

    public int SessionId { get; private set; }
    public string Token { get; private set; } = string.Empty;

    /// <summary>True when the session is not a master class / not found in the edition.</summary>
    public bool NotFoundState { get; private set; }

    /// <summary>True when there IS such a master class but this viewer may not see it.</summary>
    public bool AccessDenied { get; private set; }

    public MasterClassPrepService.LandingView? View { get; private set; }
    public IReadOnlyList<MasterClassComment> Comments { get; private set; } =
        Array.Empty<MasterClassComment>();

    /// <summary>True when the viewer is the confirmed ATTENDEE (can post to the Group Q&amp;A).</summary>
    public bool IsAttendeeViewer { get; private set; }
    /// <summary>True when the viewer is a linked speaker / organizer of this MC.</summary>
    public bool IsParticipantViewer { get; private set; }

    public string? Message { get; private set; }
    public string? Error { get; private set; }

    [BindProperty] public string? CommentBody { get; set; }
    [BindProperty] public int? ParentCommentId { get; set; }

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
            MasterClassComment posted;
            if (IsAttendeeViewer && _attendee is not null)
                posted = await _prep.AddAttendeeCommentAsync(
                    _eventId, sessionId, _attendee.Id, CommentBody ?? string.Empty, ParentCommentId, ct);
            else if (IsParticipantViewer && _me is not null)
                posted = await _prep.AddParticipantCommentAsync(
                    _eventId, sessionId, _me.ParticipantId, _me.Role, CommentBody ?? string.Empty, ParentCommentId, ct);
            else
                throw new MasterClassPrepAccessDeniedException("You may not comment on this master class.");

            // §383 — tell the rest of the class. AFTER the comment is safely stored, and swallowing
            // any failure: a mail problem must never lose a question that was already posted.
            try
            {
                if (_notify is not null)
                    await _notify.NotifyQandAAsync(
                        _eventId, sessionId, posted.AuthorDisplayName, posted.Body,
                    $"{Request.Scheme}://{Request.Host}",
                    actingParticipantId: posted.AuthorParticipantId,
                    actingAttendeeId: posted.AuthorAttendeeId, ct);
            }
            catch { /* the comment stands even if the notification fails */ }

            return Redirect(SelfUrl("Comment posted."));
        }
        catch (MasterClassPrepAccessDeniedException ex) { Error = ex.Message; }
        catch (MasterClassPrepValidationException ex) { Error = ex.Message; }

        await ReloadContentAsync(ct);
        return Page();
    }

    /// <summary>
    /// INERT (§136): attendee 1:1 questions are disabled. The form is gone, but guard
    /// the handler so any stray/replayed POST can NEVER create a 1:1
    /// <see cref="SessionQuestion"/>. We simply redirect back to the page (the Group
    /// Q&amp;A is the only question channel now).
    /// </summary>
    public IActionResult OnPostAsk(int sessionId, string? t)
    {
        // No DB touch at all — the channel is gone. Bounce back to the page with a note.
        return RedirectToPage("/MasterClassPage", null, new
        {
            sessionId,
            t = string.IsNullOrEmpty(t) ? null : t,
            msg = "1:1 questions are no longer available — please use the Group Q&A below.",
        });
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

        // §383 — the toggles. Default is SUBSCRIBED, and the service expresses that as "no opt-out
        // row", so a viewer who has never touched these reads as ON without anything being written.
        SubscribedInstructions = _notify is null || await _notify.IsSubscribedAsync(
            _eventId, SessionId, MasterClassNotificationKind.SpeakerInstructions,
            _me?.ParticipantId, _attendee?.Id, ct);
        SubscribedQandA = _notify is null || await _notify.IsSubscribedAsync(
            _eventId, SessionId, MasterClassNotificationKind.QandA,
            _me?.ParticipantId, _attendee?.Id, ct);
    }

    /// <summary>
    /// §383 — flip one of the two notification toggles for THIS viewer on THIS master class.
    ///
    /// <para>Scoped to whoever the gate already resolved: the handler never takes an identity from
    /// the POST, so nobody can mute somebody else. Attendees are keyed by their attendee row and
    /// speakers by their participant row, matching how the audience is built.</para>
    /// </summary>
    public async Task<IActionResult> OnPostSubscriptionAsync(
        int sessionId, string? t, MasterClassNotificationKind kind, bool subscribed, CancellationToken ct)
    {
        SessionId = sessionId;
        Token = t ?? string.Empty;
        if (!await ResolveAsync(ct)) return Page();

        if (_notify is not null) await _notify.SetSubscribedAsync(
            _eventId, sessionId, kind, _me?.ParticipantId, _attendee?.Id, subscribed, ct);

        return Redirect(SelfUrl(subscribed
            ? "You'll get e-mails about this."
            : "You won't get e-mails about this any more."));
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
