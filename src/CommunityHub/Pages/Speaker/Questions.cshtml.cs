using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Speaker;

/// <summary>
/// SPEAKER view of the attendee questions for THEIR sessions. A speaker sees only
/// the questions on sessions they are linked to (scope enforced server-side in
/// <see cref="SessionQuestionService"/>), and can respond. A response is then
/// visible to the OTHER speakers on the same session (and organizers), so
/// co-speakers coordinate — which is exactly what this page renders: the full
/// per-session question thread, including any co-speaker's response. Mobile-first.
///
/// Only Speaker / MasterclassSpeaker reach the content; any other role gets a
/// friendly message rather than a 403 so the nav stays simple.
/// </summary>
[Authorize]
public class QuestionsModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly SessionQuestionService _svc;

    public QuestionsModel(ICurrentParticipantAccessor participant, SessionQuestionService svc)
    {
        _participant = participant;
        _svc = svc;
    }

    public static readonly ParticipantRole[] EligibleRoles =
    {
        ParticipantRole.Speaker,
        ParticipantRole.MasterclassSpeaker,
    };

    public bool AccessDenied { get; private set; }
    public ParticipantRole Role { get; private set; }
    public string? Notice { get; private set; }
    [BindProperty(SupportsGet = true)] public string? Msg { get; set; }

    public List<SessionThread> Threads { get; private set; } = new();

    public sealed record SessionThread(
        Session Session,
        IReadOnlyList<string> SpeakerNames,
        List<SessionQuestion> Questions);

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

    public async Task<IActionResult> OnPostRespondAsync(int questionId, string responseText, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!EligibleRoles.Contains(me.Role)) return Forbid();

        var actor = new SessionQuestionService.ActorContext(
            me.ParticipantId, me.Email, me.Role, me.EventId);
        try
        {
            await _svc.RespondAsync(actor, questionId, responseText, SessionQuestionStatus.Answered, ct);
            return RedirectToPage(new { Msg = "Response sent — your co-speakers can see it too." });
        }
        catch (SessionQuestionValidationException ex) { return RedirectToPage(new { Msg = ex.Message }); }
        catch (SessionQuestionAccessDeniedException) { return Forbid(); }
    }

    private async Task LoadAsync(CurrentParticipant me, CancellationToken ct)
    {
        var actor = new SessionQuestionService.ActorContext(
            me.ParticipantId, me.Email, me.Role, me.EventId);

        var mySessions = await _svc.LoadMySessionsAsync(actor, ct);
        var threads = new List<SessionThread>();
        foreach (var session in mySessions)
        {
            var questions = await _svc.LoadForSessionAsync(actor, session.Id, ct);
            var speakers = session.SessionSpeakers
                .Select(ss => ss.Participant?.FullName)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .OrderBy(n => n)
                .ToList();
            threads.Add(new SessionThread(session, speakers, questions));
        }
        // Sessions with open questions first, then by title.
        Threads = threads
            .OrderByDescending(t => t.Questions.Count(q => q.Status == SessionQuestionStatus.Open))
            .ThenBy(t => t.Session.Title)
            .ToList();
    }
}
