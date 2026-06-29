using CommunityHub.Auth;
using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Games;

/// <summary>
/// The attendee "fun IT games" home (REQUIREMENTS §171) — lists the three timed
/// learning quizzes (AI / Intune / Security), each with Play + Leaderboard links,
/// the player's best score so far, and a "resume" hint when an attempt is mid-flight.
/// Authed (attendee+); the starter pool is seeded idempotently on first view so a
/// fresh DB is immediately playable.
/// </summary>
[Authorize]
public class IndexModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly QuizSeeder _seeder;
    private readonly ILogger<IndexModel> _log;

    public IndexModel(
        CommunityHubDbContext db, ICurrentParticipantAccessor participant,
        QuizSeeder seeder, ILogger<IndexModel> log)
    {
        _db = db;
        _participant = participant;
        _seeder = seeder;
        _log = log;
    }

    public sealed record GameCard(
        string Slug, string Title, QuizTopic Topic, int QuestionCount,
        int? MyBestScore, bool HasInProgress);

    public IReadOnlyList<GameCard> Games { get; private set; } = Array.Empty<GameCard>();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        // Idempotent: guarantees the three starter quizzes exist for this edition.
        try { await _seeder.SeedAsync(me.EventId, ct); }
        catch (Exception ex) { _log.LogWarning(ex, "Quiz seeding failed for event {EventId}", me.EventId); }

        var quizzes = await _db.Quizzes
            .Where(q => q.EventId == me.EventId && q.IsActive)
            .OrderBy(q => q.SortOrder).ThenBy(q => q.Topic)
            .ToListAsync(ct);

        var cards = new List<GameCard>();
        foreach (var q in quizzes)
        {
            var questionCount = await _db.QuizQuestions.CountAsync(x => x.QuizId == q.Id && x.IsActive, ct);
            var best = await _db.QuizAttempts
                .Where(a => a.QuizId == q.Id && a.ParticipantId == me.ParticipantId && a.CompletedAt != null)
                .Select(a => (int?)a.Score)
                .OrderByDescending(s => s)
                .FirstOrDefaultAsync(ct);
            var inProgress = await _db.QuizAttempts.AnyAsync(
                a => a.QuizId == q.Id && a.ParticipantId == me.ParticipantId && a.CompletedAt == null, ct);
            cards.Add(new GameCard(q.Slug, q.Title, q.Topic, questionCount, best, inProgress));
        }
        Games = cards;
        return Page();
    }
}
