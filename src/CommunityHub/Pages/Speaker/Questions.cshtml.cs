using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Speaker;

/// <summary>
/// SPEAKER view of the <b>Master Class Group Q&amp;A</b> (§136, operator 2026-06-27).
///
/// Previously this stacked every session's 1:1 <see cref="SessionQuestion"/> threads on
/// one page (which read as if questions were "shared across Master Classes"). 1:1
/// questions are now disabled; this page is repurposed into a clear, PER-MASTER-CLASS
/// view: one clearly separated, labelled section for EACH master class the speaker
/// presents, showing THAT master class's Group Q&amp;A
/// (<see cref="MasterClassComment"/>, scoped per <see cref="Session"/> id) with the
/// ability to REPLY. Questions and replies are never merged across master classes.
///
/// Co-speakers on the same master class share the same board (it is keyed by session,
/// not by speaker); organizers retain visibility via the master class landing page. All
/// access + reply rules are enforced server-side in <see cref="MasterClassPrepService"/>.
/// Mobile-first. Only Speakers reach the content; any other role gets a friendly message.
/// </summary>
[Authorize]
public class QuestionsModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly MasterClassPrepService _prep;

    public QuestionsModel(ICurrentParticipantAccessor participant, MasterClassPrepService prep)
    {
        _participant = participant;
        _prep = prep;
    }

    public static readonly ParticipantRole[] EligibleRoles =
    {
        ParticipantRole.Speaker,
    };

    public bool AccessDenied { get; private set; }
    public ParticipantRole Role { get; private set; }
    public string? Notice { get; private set; }
    public string? Error { get; private set; }
    [BindProperty(SupportsGet = true)] public string? Msg { get; set; }

    /// <summary>One master class + its OWN Group Q&amp;A thread (never merged with others).</summary>
    public List<MasterClassBoard> Boards { get; private set; } = new();

    public sealed record MasterClassBoard(
        Session Session,
        IReadOnlyList<string> SpeakerNames,
        List<MasterClassComment> Comments);

    // Reply form binding.
    [BindProperty] public string? ReplyBody { get; set; }
    [BindProperty] public int? ParentCommentId { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        Role = me.Role;
        if (!EligibleRoles.Contains(me.Role)) { AccessDenied = true; return Page(); }

        Notice = Msg;
        await LoadAsync(me, ct);
        return Page();
    }

    /// <summary>
    /// Post a speaker REPLY (or top-level comment) onto ONE master class's Group Q&amp;A.
    /// The board is identified by <paramref name="sessionId"/>; an optional
    /// <see cref="ParentCommentId"/> threads it under an attendee's question. The service
    /// re-checks that the poster is a speaker linked to THIS master class, so a reply can
    /// never land on a master class the speaker doesn't present.
    /// </summary>
    public async Task<IActionResult> OnPostReplyAsync(int sessionId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!EligibleRoles.Contains(me.Role)) return Forbid();

        try
        {
            await _prep.AddParticipantCommentAsync(
                me.EventId, sessionId, me.ParticipantId, me.Role,
                ReplyBody ?? string.Empty, ParentCommentId, ct);
            return RedirectToPage(new { Msg = "Reply posted to the Master Class Group Q&A." });
        }
        catch (MasterClassPrepAccessDeniedException) { return Forbid(); }
        catch (MasterClassPrepValidationException ex)
        {
            return RedirectToPage(new { Msg = ex.Message });
        }
    }

    private async Task LoadAsync(CurrentParticipant me, CancellationToken ct)
    {
        var masterClasses = await _prep.LoadSpeakerMasterClassesAsync(me.EventId, me.ParticipantId, ct);
        var boards = new List<MasterClassBoard>();
        foreach (var mc in masterClasses)
        {
            // Per-session board: each master class's Q&A is loaded separately, so the
            // threads are never merged across master classes.
            var comments = await _prep.LoadCommentsAsync(me.EventId, mc.Id, ct);
            var speakers = mc.SessionSpeakers
                .Select(ss => ss.Participant?.FullName)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .OrderBy(n => n)
                .ToList();
            boards.Add(new MasterClassBoard(mc, speakers, comments));
        }
        // Master classes with unanswered activity first (most comments), then by title.
        Boards = boards
            .OrderByDescending(b => b.Comments.Count)
            .ThenBy(b => b.Session.Title)
            .ToList();
    }
}
