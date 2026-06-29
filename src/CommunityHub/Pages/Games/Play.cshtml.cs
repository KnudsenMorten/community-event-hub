using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Quizzes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Games;

/// <summary>
/// The §171 play page — one question at a time, a per-question countdown, and a
/// server-scored reveal (correct option + why) after each answer. Everything that
/// matters is server-authoritative: the engine draws the questions, shuffles the
/// options per attempt, stamps shown→answered from its OWN clock and computes the
/// speed score. The browser only ever sees the prompt + the shuffled options and
/// posts back a displayed-option position — it cannot supply a score or a time.
/// </summary>
[Authorize]
public class PlayModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly QuizPlayService _play;

    public PlayModel(ICurrentParticipantAccessor participant, QuizPlayService play)
    {
        _participant = participant;
        _play = play;
    }

    public string? Topic { get; private set; }
    public string Title { get; private set; } = "Quiz";

    /// <summary>True when there is no active quiz for this topic (or it has no questions).</summary>
    public bool NoQuiz { get; private set; }

    /// <summary>The current question to answer (null when revealing or finished).</summary>
    public QuizStep? Step { get; private set; }

    /// <summary>The reveal after an answer (null on a fresh question render).</summary>
    public QuizAnswerResult? Reveal { get; private set; }

    /// <summary>True when the attempt is complete (all questions answered).</summary>
    public bool Finished { get; private set; }

    /// <summary>The final attempt score, shown on the completion screen.</summary>
    public int FinalScore { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? topic, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        Topic = topic;

        var quiz = await ResolveQuizAsync(me.EventId, topic, ct);
        if (quiz is null) { NoQuiz = true; return Page(); }
        Title = quiz.Title;

        var attempt = await _play.GetOrStartAttemptAsync(me.EventId, me.ParticipantId, quiz.Topic, ct);
        if (attempt is null) { NoQuiz = true; return Page(); }

        Step = await _play.GetCurrentStepAsync(attempt.Id, me.ParticipantId, ct);
        if (Step is null)
        {
            // No unanswered question left ⇒ the attempt is complete.
            Finished = true;
            FinalScore = attempt.Score;
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAnswerAsync(
        string? topic, int attemptId, int questionId, int selectedIndex, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        Topic = topic;

        var quiz = await ResolveQuizAsync(me.EventId, topic, ct);
        if (quiz is null) { NoQuiz = true; return Page(); }
        Title = quiz.Title;

        var result = await _play.SubmitAnswerAsync(attemptId, me.ParticipantId, questionId, selectedIndex, ct);
        if (result is null)
        {
            // Stale/duplicate/forged submit — re-render the current state.
            return RedirectToPage("/Games/Play", new { topic });
        }

        Reveal = result;
        return Page();
    }

    private async Task<Quiz?> ResolveQuizAsync(int eventId, string? topic, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(topic)) return null;
        return await _play.GetActiveQuizBySlugAsync(eventId, topic.Trim(), ct);
    }
}
