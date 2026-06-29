using CommunityHub.Auth;
using CommunityHub.Core.Config;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Quizzes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Organizer authoring for the §171 attendee "fun IT games" quizzes — view/add/edit/
/// disable quizzes and their questions. The shipped starter pool is seeded
/// idempotently on first view and is fully editable here. Organizer-gated.
/// </summary>
[Authorize]
public class QuizzesModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly QuizAuthoringService _authoring;
    private readonly QuizSeeder _seeder;

    public QuizzesModel(
        ICurrentParticipantAccessor participant, QuizAuthoringService authoring, QuizSeeder seeder)
    {
        _participant = participant;
        _authoring = authoring;
        _seeder = seeder;
    }

    public bool AccessDenied { get; private set; }
    public IReadOnlyList<QuizSummary> Quizzes { get; private set; } = Array.Empty<QuizSummary>();
    public Quiz? SelectedQuiz { get; private set; }
    public string? Message { get; private set; }

    public async Task<IActionResult> OnGetAsync(int? quizId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        await _seeder.SeedAsync(me.EventId, ct);   // idempotent — guarantees a starter pool
        await LoadAsync(me.EventId, quizId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostUpdateQuizAsync(
        int quizId, string title, bool isActive, int questionsPerAttempt,
        int perQuestionSeconds, int basePoints, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        await _authoring.UpdateQuizAsync(
            me.EventId, quizId, title, isActive, questionsPerAttempt, perQuestionSeconds, basePoints, ct);
        return RedirectToPage(new { quizId });
    }

    public async Task<IActionResult> OnPostAddQuestionAsync(
        int quizId, string prompt, string? opt0, string? opt1, string? opt2, string? opt3,
        int correctIndex, string? explanation, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        var id = await _authoring.AddQuestionAsync(
            me.EventId, quizId, prompt, Options(opt0, opt1, opt2, opt3), correctIndex, explanation ?? "", ct);
        if (id is null) Message = "Could not add the question — check the prompt, at least two options, and a valid correct answer.";
        return RedirectToPage(new { quizId });
    }

    public async Task<IActionResult> OnPostUpdateQuestionAsync(
        int quizId, int questionId, string prompt, string? opt0, string? opt1, string? opt2, string? opt3,
        int correctIndex, string? explanation, bool isActive, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        var ok = await _authoring.UpdateQuestionAsync(
            me.EventId, questionId, prompt, Options(opt0, opt1, opt2, opt3), correctIndex, explanation ?? "", isActive, ct);
        if (!ok) Message = "Could not save the question — check the prompt, at least two options, and a valid correct answer.";
        return RedirectToPage(new { quizId });
    }

    public async Task<IActionResult> OnPostToggleQuestionAsync(int quizId, int questionId, bool isActive, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        await _authoring.SetQuestionActiveAsync(me.EventId, questionId, isActive, ct);
        return RedirectToPage(new { quizId });
    }

    private async Task LoadAsync(int eventId, int? quizId, CancellationToken ct)
    {
        Quizzes = await _authoring.GetQuizzesAsync(eventId, ct);
        if (quizId is int id)
            SelectedQuiz = await _authoring.GetQuizAsync(eventId, id, ct);
    }

    // Collect the four option fields, trimming TRAILING blanks (so a 2- or 3-option
    // question is fine) while preserving the order CorrectIndex refers to.
    private static List<string> Options(string? a, string? b, string? c, string? d)
    {
        var raw = new[] { a, b, c, d }.Select(x => (x ?? string.Empty).Trim()).ToList();
        while (raw.Count > 0 && raw[^1].Length == 0) raw.RemoveAt(raw.Count - 1);
        return raw;
    }
}
