using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Quizzes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Games;

/// <summary>
/// The §171 per-topic leaderboard — the top N by best score (tie-break: fastest
/// total time) PLUS the signed-in player's own rank even when they fall outside the
/// top N. Display names are privacy-safe (first name + last initial), never email.
/// </summary>
[Authorize]
public class LeaderboardModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly CommunityHubDbContext _db;
    private readonly QuizLeaderboardService _leaderboard;

    public LeaderboardModel(
        ICurrentParticipantAccessor participant, CommunityHubDbContext db,
        QuizLeaderboardService leaderboard)
    {
        _participant = participant;
        _db = db;
        _leaderboard = leaderboard;
    }

    public string? Topic { get; private set; }
    public string Title { get; private set; } = "Leaderboard";
    public bool NoQuiz { get; private set; }
    public QuizLeaderboardView? Board { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? topic, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        Topic = topic;

        var quiz = string.IsNullOrWhiteSpace(topic)
            ? null
            : await _db.Quizzes.FirstOrDefaultAsync(
                q => q.EventId == me.EventId && q.Slug == topic.Trim(), ct);
        if (quiz is null) { NoQuiz = true; return Page(); }
        Title = quiz.Title;

        Board = await _leaderboard.GetAsync(me.EventId, quiz.Id, me.ParticipantId, ct: ct);
        return Page();
    }

    /// <summary>Format a millisecond total as a friendly "Xm Ys" / "Ys" string.</summary>
    public static string FormatElapsed(long ms)
    {
        var totalSeconds = (int)Math.Round(ms / 1000.0);
        var m = totalSeconds / 60;
        var s = totalSeconds % 60;
        return m > 0 ? $"{m}m {s}s" : $"{s}s";
    }
}
