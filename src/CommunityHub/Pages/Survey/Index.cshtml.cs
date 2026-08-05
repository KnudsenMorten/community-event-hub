using System.Security.Cryptography;
using System.Text;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Resources;
using CommunityHub.Core.Surveys;
using CommunityHub.Surveys;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Pages.Survey;

/// <summary>
/// PUBLIC anonymous survey wizard. Three steps, single Razor page:
///   1. Pick one technical track.
///   2. Rank three topic picks within that track.
///   3. Pick the desired session level for each of the three picks.
///
/// Step transitions are client-side (no server round-trip between steps);
/// only the final submit POSTs. Once submitted, the same page renders the
/// thank-you state with a link to the public results dashboard.
///
/// Catalog (tracks / topics / level examples) lives in JSON under
/// App_Data/Surveys/{slug}.json — editing the JSON does not require a code
/// change or a DB migration.
/// </summary>
[AllowAnonymous]
public class IndexModel : PageModel
{
    private readonly SurveyDefinitionProvider _definitions;
    private readonly CommunityHubDbContext _db;
    private readonly SurveySummaryService _summary;
    private readonly TimeProvider _clock;
    private readonly ILogger<IndexModel> _log;
    private readonly IStringLocalizer<SharedResource> _loc;

    public IndexModel(
        SurveyDefinitionProvider definitions,
        CommunityHubDbContext db,
        SurveySummaryService summary,
        TimeProvider clock,
        ILogger<IndexModel> log,
        IStringLocalizer<SharedResource> loc)
    {
        _definitions = definitions;
        _db = db;
        _summary = summary;
        _clock = clock;
        _log = log;
        _loc = loc;
    }

    // --- View state -------------------------------------------------------
    public SurveyDefinition? Survey { get; private set; }
    public string Slug { get; private set; } = string.Empty;
    public bool SubmittedOk { get; private set; }
    public string? ErrorMessage { get; private set; }

    /// <summary>
    /// True when an organizer has CLOSED this survey: the wizard is hidden and a
    /// friendly "closed" state is shown instead. Results stay viewable. A survey
    /// with no state row is OPEN (the historical default).
    /// </summary>
    public bool IsClosed { get; private set; }

    // --- Form binding (single POST on submit) -----------------------------
    [BindProperty] public string SelectedTrackId { get; set; } = string.Empty;
    [BindProperty] public string? Pick1TopicId { get; set; }
    [BindProperty] public string? Pick2TopicId { get; set; }
    [BindProperty] public string? Pick3TopicId { get; set; }
    [BindProperty] public SurveyLevel? Pick1Level { get; set; }
    [BindProperty] public SurveyLevel? Pick2Level { get; set; }
    [BindProperty] public SurveyLevel? Pick3Level { get; set; }
    [BindProperty] public string? Comment { get; set; }

    /// <summary>
    /// Honeypot. Hidden CSS-off-screen on the page. Humans cannot see it; bots
    /// fill every input. Any non-empty value -> silent 200 OK (no DB write).
    /// </summary>
    [BindProperty] public string? Website { get; set; }

    /// <summary>
    /// §6.5 — the answers of a QUESTION survey, posted as <c>Answers[questionId]</c>.
    /// </summary>
    /// <remarks>
    /// A dictionary rather than fixed properties, because the questions live in editable JSON: the
    /// form is generated from the definition, so the binding has to be too. A multi-choice question
    /// posts its selections comma-separated under the same key.
    /// </remarks>
    [BindProperty] public Dictionary<string, string?> Answers { get; set; } = new();

    /// <summary>§6.5 — what was wrong with the submitted answers, in the respondent's words.</summary>
    public IReadOnlyList<string> AnswerProblems { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(string slug, CancellationToken ct)
    {
        Slug = slug ?? string.Empty;
        Survey = _definitions.TryGet(Slug);
        if (Survey is null) return NotFound();
        IsClosed = !await _summary.IsOpenAsync(Slug, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string slug, CancellationToken ct)
    {
        Slug = slug ?? string.Empty;
        Survey = _definitions.TryGet(Slug);
        if (Survey is null) return NotFound();

        // Closed survey: never accept a submission. Show the closed state (the
        // wizard is hidden) and do not write anything. Defense-in-depth — the
        // public page hides the form when closed, but a hand-rolled POST must
        // also be rejected.
        if (!await _summary.IsOpenAsync(Slug, ct))
        {
            IsClosed = true;
            return Page();
        }

        // Honeypot. Pretend success without writing anything.
        if (!string.IsNullOrWhiteSpace(Website))
        {
            _log.LogInformation("Survey honeypot tripped for slug={Slug} from {Ip}", Slug, HttpContext.Connection.RemoteIpAddress);
            SubmittedOk = true;
            return Page();
        }

        // §6.5 — a QUESTION survey takes a different path entirely. Branching here rather than
        // teaching the wizard about ratings keeps the survey people are answering in production
        // exactly as it was.
        if (Survey.IsQuestionSurvey) return await SubmitQuestionSurveyAsync(ct);

        // --- Validate the wizard payload --------------------------------
        var track = Survey.FindTrack(SelectedTrackId);
        if (track is null)
        {
            ErrorMessage = _loc["Survey.ValTrack"];
            return Page();
        }

        var picks = new[]
        {
            (TopicId: Pick1TopicId, Level: Pick1Level, Rank: 1),
            (TopicId: Pick2TopicId, Level: Pick2Level, Rank: 2),
            (TopicId: Pick3TopicId, Level: Pick3Level, Rank: 3),
        };

        if (picks.Any(p => string.IsNullOrWhiteSpace(p.TopicId)))
        {
            ErrorMessage = _loc["Survey.ValRankThree"];
            return Page();
        }
        if (picks.Select(p => p.TopicId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3)
        {
            ErrorMessage = _loc["Survey.ValPicksDistinct"];
            return Page();
        }
        // All picked topic ids must belong to the selected track (defense in
        // depth — the wizard UI only shows in-track topics, but a hand-rolled
        // POST should not be able to mix tracks).
        var topicIdsInTrack = track.Topics.Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (picks.Any(p => !topicIdsInTrack.Contains(p.TopicId!)))
        {
            ErrorMessage = _loc["Survey.ValTopicNotInTrack"];
            return Page();
        }
        if (picks.Any(p => p.Level is null))
        {
            ErrorMessage = _loc["Survey.ValLevel"];
            return Page();
        }

        // --- Write the response -----------------------------------------
        var response = new SurveyResponse
        {
            SurveySlug      = Survey.Slug,
            SelectedTrackId = track.Id,
            Comment         = string.IsNullOrWhiteSpace(Comment) ? null : Comment.Trim(),
            SubmittedAt     = _clock.GetUtcNow(),
            IpHash          = HashIp(HttpContext.Connection.RemoteIpAddress?.ToString()),
            Picks = picks.Select(p => new SurveyResponsePick
            {
                Rank          = p.Rank,
                TopicId       = p.TopicId!,
                DesiredLevel  = p.Level!.Value,
            }).ToList(),
        };
        _db.SurveyResponses.Add(response);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            _log.LogWarning(ex, "Survey response DB write failed for slug={Slug}", Slug);
            ErrorMessage = _loc["Survey.ValSaveFailed"];
            return Page();
        }

        _log.LogInformation(
            "Survey response saved: slug={Slug} id={Id} track={TrackId} picks={Picks}",
            Slug, response.Id, response.SelectedTrackId,
            string.Join("|", response.Picks.Select(p => $"{p.Rank}:{p.TopicId}={p.DesiredLevel}")));

        SubmittedOk = true;
        return Page();
    }

    /// <summary>
    /// §6.5 — store one question survey's answers.
    /// </summary>
    /// <remarks>
    /// <para>🔒 <b>Validated server-side against the DEFINITION</b>, not against what the form
    /// offered. The page is anonymous and public, so the rendered inputs constrain nobody who is
    /// posting by hand — the rating range, the option ids and the text length are all re-checked
    /// here.</para>
    ///
    /// <para>⚠️ <b>A blank answer is stored as NO ROW, not as an empty one.</b> "Skipped" and
    /// "answered with nothing" are different facts, and a summary that counts empty rows as
    /// responses would report a participation rate nobody achieved.</para>
    /// </remarks>
    private async Task<IActionResult> SubmitQuestionSurveyAsync(CancellationToken ct)
    {
        var inputs = Survey!.Questions
            .Select(q => ToInput(q, Answers.TryGetValue(q.Id, out var raw) ? raw : null))
            .Where(a => a is not null)
            .Select(a => a!)
            .ToList();

        var problems = SurveyAnswerValidator.Validate(Survey.Questions, inputs);
        if (problems.Count > 0)
        {
            AnswerProblems = problems;
            return Page();
        }

        var response = new SurveyResponse
        {
            SurveySlug = Survey.Slug,
            // The wizard's columns are required but meaningless here; a question survey has no
            // track and its comment lives in its own free-text answers.
            SelectedTrackId = string.Empty,
            SubmittedAt = _clock.GetUtcNow(),
            IpHash = HashIp(HttpContext.Connection.RemoteIpAddress?.ToString()),
        };
        _db.SurveyResponses.Add(response);

        foreach (var a in inputs)
        {
            var q = Survey.Questions.First(
                x => string.Equals(x.Id, a.QuestionId, StringComparison.OrdinalIgnoreCase));

            _db.SurveyResponseAnswers.Add(new SurveyResponseAnswer
            {
                Response = response,
                QuestionId = q.Id,
                Kind = q.Kind,
                Rating = a.Rating,
                Text = string.IsNullOrWhiteSpace(a.Text) ? null : a.Text.Trim(),
                ChoiceIds = a.ChoiceIds is { Count: > 0 } ids ? string.Join(",", ids) : null,
            });
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            _log.LogWarning(ex, "Survey response DB write failed for slug={Slug}", Slug);
            ErrorMessage = _loc["Survey.ValSaveFailed"];
            return Page();
        }

        // 🔒 The log records COUNTS, never the answers. This survey's whole value is that nobody
        // can be identified from it, and a log line quoting free text would undo that.
        _log.LogInformation(
            "Survey response saved: slug={Slug} id={Id} answers={Answers}",
            Slug, response.Id, inputs.Count);

        SubmittedOk = true;
        return Page();
    }

    /// <summary>Turn one posted form value into a typed answer, or null when it was left blank.</summary>
    private static SurveyAnswerInput? ToInput(SurveyQuestion q, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        return q.Kind switch
        {
            SurveyQuestionKind.Rating =>
                int.TryParse(raw, out var r)
                    ? new SurveyAnswerInput(q.Id, Rating: r)
                    // ⚠️ Unparseable is NOT the same as blank: dropping it would silently accept a
                    // required question the respondent thinks they answered.
                    : new SurveyAnswerInput(q.Id, Rating: int.MinValue),

            SurveyQuestionKind.FreeText => new SurveyAnswerInput(q.Id, Text: raw),

            _ => new SurveyAnswerInput(q.Id, ChoiceIds:
                raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
        };
    }

    private static string? HashIp(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return null;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(ip));
        return Convert.ToHexString(bytes)[..32];
    }
}
