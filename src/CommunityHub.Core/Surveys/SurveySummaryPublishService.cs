using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.DocLibrary;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Surveys;

/// <summary>What one survey-summary run did.</summary>
/// <param name="Skipped">Surveys deliberately not published, each with its reason — never silent.</param>
public sealed record SurveySummaryRunResult(
    bool Ran,
    string? InactiveReason,
    int Published,
    int Unchanged,
    IReadOnlyList<string> Skipped,
    IReadOnlyList<string> Failures)
{
    public static SurveySummaryRunResult Inactive(string reason) =>
        new(false, reason, 0, 0, [], []);
}

/// <summary>
/// §6.5 — rebuilds each post-event survey's summary DAILY and writes it to that survey's §3.4 folder.
/// </summary>
/// <remarks>
/// <para>Work order: <i>"Summaries rebuilt daily from responses so far, written to the three §3.4
/// folders"</i> and <i>"No notification mail for these three."</i></para>
///
/// <para>🔒 <b>NOTHING here sends mail, and that is a requirement rather than an omission.</b> The
/// logistics run mails on a schedule; this one deliberately does not, so the §3.4 summaries can never
/// become a stream of mail to anybody. The organizer reads them in the library.</para>
///
/// <para>🔒 <b>Republished only when the ANSWERS changed.</b> A daily identical rewrite would make
/// the library's "modified" stamp meaningless — and that stamp is how an organizer sees whether
/// anybody has responded since they last looked.</para>
///
/// <para>⚠️ <b>A survey with NO responses still gets a file.</b> An absent file reads as "the job is
/// broken"; a file saying zero responses reads as "nobody has answered yet", which is the fact.</para>
/// </remarks>
public sealed class SurveySummaryPublishService
{
    /// <summary>Which §3.4 folder each post-event survey's summary belongs in.</summary>
    /// <remarks>
    /// 🔒 The mapping is explicit rather than derived from the slug. A slug is editable content; a
    /// path key is a contract. Deriving one from the other would mean renaming a survey quietly
    /// redirected its summary — or, worse, silently stopped publishing it.
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, string> FolderBySlug =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["eldk27-post-attendee"] = DocLibraryPaths.EventEvalAfterAttendee,
            ["eldk27-post-speaker"] = DocLibraryPaths.EventEvalAfterSpeaker,
            ["eldk27-post-sponsor"] = DocLibraryPaths.EventEvalAfterSponsor,
        };

    private readonly CommunityHubDbContext _db;
    private readonly DocLibraryFilePublisher _publisher;
    private readonly ISurveyDefinitionSource _definitions;
    private readonly SurveyQuestionSummaryService _summaries;
    private readonly SurveySummaryFileProducer _producer;
    private readonly TimeProvider _clock;
    private readonly ILogger<SurveySummaryPublishService>? _log;

    public SurveySummaryPublishService(
        CommunityHubDbContext db,
        DocLibraryFilePublisher publisher,
        ISurveyDefinitionSource definitions,
        SurveyQuestionSummaryService summaries,
        SurveySummaryFileProducer producer,
        TimeProvider? clock = null,
        ILogger<SurveySummaryPublishService>? log = null)
    {
        _db = db;
        _publisher = publisher;
        _definitions = definitions;
        _summaries = summaries;
        _producer = producer;
        _clock = clock ?? TimeProvider.System;
        _log = log;
    }

    public async Task<SurveySummaryRunResult> RunAsync(int eventId, CancellationToken ct = default)
    {
        if (!_publisher.CanPublish)
            return SurveySummaryRunResult.Inactive(
                "The document library is not writable on this host.");

        var now = _clock.GetUtcNow();
        var published = 0;
        var unchanged = 0;
        var skipped = new List<string>();
        var failures = new List<string>();

        var states = await _db.LogisticsFileStates
            .Where(s => s.EventId == eventId)
            .ToListAsync(ct);
        var byName = states.ToDictionary(s => s.FileName, StringComparer.OrdinalIgnoreCase);

        foreach (var (slug, pathKey) in FolderBySlug.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var definition = _definitions.TryGet(slug);
            if (definition is null)
            {
                // ⚠️ Named, not swallowed. A definition that fails to load is exactly the §773.1
                // failure, and there it turned into a silent 404 nobody saw for a day.
                skipped.Add($"{slug}: no definition could be loaded, so no summary was written.");
                continue;
            }

            try
            {
                var summary = await _summaries.BuildAsync(definition, ct);
                var file = _producer.Build(definition, summary);

                byName.TryGetValue(file.FileName, out var state);
                var result = await _publisher.PublishAsync(pathKey, file, state?.ContentKey, ct);

                if (!result.Ok)
                {
                    failures.Add($"{file.FileName}: {result.Error}");
                    continue;
                }

                if (result.Written) published++; else unchanged++;

                if (state is null)
                {
                    state = new LogisticsFileState
                    {
                        EventId = eventId, PathKey = pathKey, FileName = file.FileName,
                    };
                    _db.LogisticsFileStates.Add(state);
                }

                state.ContentKey = file.ContentKey;
                state.PublishedAt = now;
                state.Headline = file.Headline;
                if (!string.IsNullOrWhiteSpace(result.WebUrl)) state.WebUrl = result.WebUrl;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One survey's failure must not stop the other two.
                failures.Add($"{slug}: {ex.Message}");
            }
        }

        await _db.SaveChangesAsync(ct);

        _log?.LogInformation(
            "§6.5 survey summaries: {Published} published, {Unchanged} unchanged, "
            + "{Skipped} skipped, {Failed} failed.",
            published, unchanged, skipped.Count, failures.Count);

        return new SurveySummaryRunResult(true, null, published, unchanged, skipped, failures);
    }
}
