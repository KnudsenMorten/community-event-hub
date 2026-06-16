using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// ORGANIZER view of the attendee questions per session: ALL questions across the
/// edition (organizers see everything), grouped by session, with the ability to
/// respond / close any of them and to copy each session's PUBLIC ask link (the
/// unguessable <c>/sessions/{token}/ask</c> URL to share with attendees). All
/// reads/mutations go through <see cref="SessionQuestionService"/>, which enforces
/// the permission model server-side. Mobile-first (~360px).
/// </summary>
[Authorize]
public class SessionQuestionsModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly SessionQuestionService _svc;

    public SessionQuestionsModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        SessionQuestionService svc)
    {
        _db = db;
        _participant = participant;
        _svc = svc;
    }

    public bool AccessDenied { get; private set; }
    public string? Notice { get; private set; }
    [BindProperty(SupportsGet = true)] public string? Msg { get; set; }

    /// <summary>Questions grouped by their session, newest activity first.</summary>
    public List<SessionGroup> Groups { get; private set; } = new();

    public int OpenCount { get; private set; }
    public int TotalCount { get; private set; }

    public sealed record SessionGroup(
        Session Session,
        IReadOnlyList<string> SpeakerNames,
        string? PublicAskPath,
        List<SessionQuestion> Questions);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        Notice = Msg;
        await LoadAsync(me, ct);
        return Page();
    }

    private SessionQuestionService.ActorContext? Actor()
    {
        var me = _participant.Current;
        if (me is null || me.Role != ParticipantRole.Organizer) return null;
        return new SessionQuestionService.ActorContext(
            me.ParticipantId, me.Email, me.Role, me.EventId);
    }

    private async Task<IActionResult> RunAsync(Func<SessionQuestionService.ActorContext, Task<string>> op)
    {
        var actor = Actor();
        if (actor is null) return Forbid();
        try
        {
            var msg = await op(actor.Value);
            return RedirectToPage(new { Msg = msg });
        }
        catch (SessionQuestionValidationException ex) { return RedirectToPage(new { Msg = ex.Message }); }
        catch (SessionQuestionAccessDeniedException) { return Forbid(); }
    }

    public Task<IActionResult> OnPostRespondAsync(int questionId, string responseText, CancellationToken ct)
        => RunAsync(async a => { await _svc.RespondAsync(a, questionId, responseText, SessionQuestionStatus.Answered, ct); return "Response sent."; });

    public Task<IActionResult> OnPostCloseAsync(int questionId, CancellationToken ct)
        => RunAsync(async a => { await _svc.CloseAsync(a, questionId, ct); return "Question closed."; });

    /// <summary>Mint (or reuse) the session's public ask token, then return to the list.</summary>
    public async Task<IActionResult> OnPostGenerateLinkAsync(int sessionId, CancellationToken ct)
    {
        var actor = Actor();
        if (actor is null) return Forbid();
        // Only mint links for sessions in this edition.
        if (!await _db.Sessions.AnyAsync(s => s.Id == sessionId && s.EventId == actor.Value.EventId, ct))
            return Forbid();
        await _svc.EnsurePublicTokenAsync(sessionId, ct);
        return RedirectToPage(new { Msg = "Public ask link ready." });
    }

    private async Task LoadAsync(CurrentParticipant me, CancellationToken ct)
    {
        var actor = new SessionQuestionService.ActorContext(
            me.ParticipantId, me.Email, me.Role, me.EventId);

        var all = await _svc.LoadAllForEventAsync(actor, ct);
        TotalCount = all.Count;
        OpenCount = all.Count(q => q.Status == SessionQuestionStatus.Open);

        // Sessions that have at least one question, with speakers loaded.
        var sessionIds = all.Select(q => q.SessionId).Distinct().ToList();
        var sessions = await _db.Sessions
            .Where(s => s.EventId == me.EventId && sessionIds.Contains(s.Id))
            .Include(s => s.SessionSpeakers).ThenInclude(ss => ss.Participant)
            .ToDictionaryAsync(s => s.Id, ct);

        Groups = all
            .GroupBy(q => q.SessionId)
            .Select(g =>
            {
                sessions.TryGetValue(g.Key, out var session);
                var speakers = session?.SessionSpeakers
                    .Select(ss => ss.Participant?.FullName)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Select(n => n!)
                    .OrderBy(n => n)
                    .ToList() ?? new List<string>();
                var path = string.IsNullOrWhiteSpace(session?.PublicToken)
                    ? null
                    : $"/sessions/{session!.PublicToken}/ask";
                return new SessionGroup(
                    session!, speakers, path,
                    g.OrderBy(q => q.Status).ThenByDescending(q => q.CreatedAt).ToList());
            })
            .Where(grp => grp.Session is not null)
            .OrderByDescending(grp => grp.Questions.Count(q => q.Status == SessionQuestionStatus.Open))
            .ThenBy(grp => grp.Session.Title)
            .ToList();
    }
}
