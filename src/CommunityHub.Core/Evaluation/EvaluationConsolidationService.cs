using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations.DocLibrary;
using CommunityHub.Core.Integrations.Graphics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Evaluation;

/// <summary>What one consolidation run did.</summary>
/// <param name="Copied">Per-session result PDFs copied into the event folder this run.</param>
/// <param name="Unchanged">Already present and identical — copied nothing, correctly.</param>
public sealed record EvaluationConsolidationResult(
    bool Ran,
    string? InactiveReason,
    int Copied,
    int Unchanged,
    bool SummaryWritten,
    bool Notified,
    IReadOnlyList<string> Failures)
{
    public static EvaluationConsolidationResult Inactive(string reason) =>
        new(false, reason, 0, 0, false, false, []);
}

/// <summary>
/// §6.6 — POST-EVENT consolidation of the session evaluations.
/// </summary>
/// <remarks>
/// <para>Work order §6.6, verbatim: copy all PDF results to
/// <c>Event/Evaluations/During the event/SessionEvaluations</c> — <b>a copy, not a move</b>; the
/// per-speaker results stay in <c>Speakers/SessionEvaluations/Result</c> — generate a
/// <b>combined summary PDF</b> across all sessions into the same folder, and notify
/// <c>info@expertslive.dk</c>.</para>
///
/// <para>🔒 <b>A COPY, and the word matters.</b> A speaker reaches their own report through the
/// speaker folder; moving the file would break every one of those links to build a convenience view
/// for organizers. The two folders answer different questions — "my session" and "the whole event" —
/// and both have to keep working.</para>
///
/// <para>🔒 <b>AFTER the event, never during.</b> Evaluations arrive while the event is running and
/// reports are superseded as late data lands (§747). Consolidating mid-event would publish a
/// snapshot into a folder whose whole purpose is to be the record, and mail somebody to say the
/// final results were ready when they were not.</para>
///
/// <para>⚠️ <b>Idempotent, and it mails only on CHANGE.</b> The run is safe to repeat daily: the
/// publisher rewrites a file only when its content key differs, and the notification goes out only
/// when something actually moved. A daily "your evaluation results are consolidated" mail about
/// nothing is how a real notification stops being read.</para>
/// </remarks>
public sealed class EvaluationConsolidationService
{
    /// <summary>§6.6 — who is told when the consolidation produces something new.</summary>
    public const string NotifyAddress = "info@expertslive.dk";

    private readonly CommunityHubDbContext _db;
    private readonly ISharePointFileStore _store;
    private readonly IDocLibraryPathResolver _paths;
    private readonly DocLibraryFilePublisher _publisher;
    private readonly EvaluationScoreService _scores;
    private readonly EvaluationSummaryPdfService _summary;
    private readonly IEmailSender _email;
    private readonly IEmailContextAccessor? _emailContext;
    private readonly TimeProvider _clock;
    private readonly ILogger<EvaluationConsolidationService>? _log;

    public EvaluationConsolidationService(
        CommunityHubDbContext db,
        ISharePointFileStore store,
        IDocLibraryPathResolver paths,
        DocLibraryFilePublisher publisher,
        EvaluationScoreService scores,
        EvaluationSummaryPdfService summary,
        IEmailSender email,
        IEmailContextAccessor? emailContext = null,
        TimeProvider? clock = null,
        ILogger<EvaluationConsolidationService>? log = null)
    {
        _db = db;
        _store = store;
        _paths = paths;
        _publisher = publisher;
        _scores = scores;
        _summary = summary;
        _email = email;
        _emailContext = emailContext;
        _clock = clock ?? TimeProvider.System;
        _log = log;
    }

    public async Task<EvaluationConsolidationResult> RunAsync(
        int eventId, CancellationToken ct = default)
    {
        var evt = await _db.Events.AsNoTracking()
            .Where(e => e.Id == eventId)
            .Select(e => new { e.Code, e.DisplayName, e.EndDate })
            .FirstOrDefaultAsync(ct);

        if (evt is null) return EvaluationConsolidationResult.Inactive("No such edition.");

        // 🔒 The post-event gate. The day AFTER the last day — an event that ends today is still
        // collecting responses this evening.
        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        if (today <= evt.EndDate)
        {
            return EvaluationConsolidationResult.Inactive(
                $"The event has not ended yet (ends {evt.EndDate:yyyy-MM-dd}); consolidation runs after it.");
        }

        if (!_publisher.CanPublish || !_store.CanRead)
        {
            return EvaluationConsolidationResult.Inactive(
                "The document library is not readable/writable on this host.");
        }

        if (!_paths.TryResolve(DocLibraryPaths.SessionEvaluationResults, out var sourceFolder)
            || string.IsNullOrWhiteSpace(sourceFolder))
        {
            return EvaluationConsolidationResult.Inactive(
                "No folder is configured for the per-session evaluation results.");
        }

        var copied = 0;
        var unchanged = 0;
        var failures = new List<string>();

        var states = await _db.LogisticsFileStates
            .Where(s => s.EventId == eventId && s.PathKey == DocLibraryPaths.EventEvalDuringSessions)
            .ToListAsync(ct);
        var byName = states.ToDictionary(s => s.FileName, StringComparer.OrdinalIgnoreCase);

        var now = _clock.GetUtcNow();

        // ---- 1. copy every published result PDF -------------------------------------------
        var sourceFiles = await _store.ListAsync(sourceFolder, ct);
        foreach (var f in sourceFiles
                     .Where(f => f.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            try
            {
                var bytes = await _store.DownloadAsync(f.ItemId, ct);
                if (bytes is null || bytes.Length == 0)
                {
                    // ⚠️ Named, not skipped silently: a result that cannot be read is a result the
                    // event record will be missing, and an empty folder looks identical to a quiet one.
                    failures.Add($"{f.Name}: could not be downloaded from the speaker folder.");
                    continue;
                }

                var file = new GeneratedFile(
                    f.Name, bytes, "application/pdf",
                    GeneratedFile.KeyOf([Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))]),
                    LogisticsHeadline.Of(1, "report", "reports"));

                byName.TryGetValue(f.Name, out var state);
                var result = await _publisher.PublishAsync(
                    DocLibraryPaths.EventEvalDuringSessions, file, state?.ContentKey, ct);

                if (!result.Ok)
                {
                    failures.Add($"{f.Name}: {result.Error}");
                    continue;
                }

                if (result.Written) copied++; else unchanged++;
                Remember(ref state, eventId, file, now, result.WebUrl);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add($"{f.Name}: {ex.Message}");
            }
        }

        // ---- 2. the combined summary across all sessions -----------------------------------
        var summaryWritten = false;
        try
        {
            var summaryFile = await BuildSummaryAsync(eventId, evt.Code, evt.DisplayName, now, ct);
            byName.TryGetValue(summaryFile.FileName, out var summaryState);

            var result = await _publisher.PublishAsync(
                DocLibraryPaths.EventEvalDuringSessions, summaryFile, summaryState?.ContentKey, ct);

            if (!result.Ok) failures.Add($"{summaryFile.FileName}: {result.Error}");
            else
            {
                summaryWritten = result.Written;
                Remember(ref summaryState, eventId, summaryFile, now, result.WebUrl);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failures.Add($"the combined summary could not be built — {ex.Message}");
        }

        await _db.SaveChangesAsync(ct);

        // ---- 3. notify, ONLY when something actually moved ---------------------------------
        var notified = false;
        if (copied > 0 || summaryWritten)
        {
            notified = await NotifyAsync(eventId, evt.DisplayName, copied, summaryWritten, ct);
        }

        _log?.LogInformation(
            "§6.6 evaluation consolidation: {Copied} copied, {Unchanged} unchanged, "
            + "summary {Summary}, notified {Notified}, {Failed} failed.",
            copied, unchanged, summaryWritten ? "rewritten" : "unchanged", notified, failures.Count);

        return new EvaluationConsolidationResult(
            true, null, copied, unchanged, summaryWritten, notified, failures);
    }

    private void Remember(
        ref LogisticsFileState? state, int eventId, GeneratedFile file,
        DateTimeOffset now, string? webUrl)
    {
        if (state is null)
        {
            state = new LogisticsFileState
            {
                EventId = eventId,
                PathKey = DocLibraryPaths.EventEvalDuringSessions,
                FileName = file.FileName,
            };
            _db.LogisticsFileStates.Add(state);
        }

        state.ContentKey = file.ContentKey;
        state.PublishedAt = now;
        state.Headline = file.Headline;
        if (!string.IsNullOrWhiteSpace(webUrl)) state.WebUrl = webUrl;
    }

    private async Task<GeneratedFile> BuildSummaryAsync(
        int eventId, string eventCode, string eventName, DateTimeOffset now, CancellationToken ct)
    {
        var sessions = await _scores.ListAsync(eventId, ct: ct);
        var (pooled, _, _) = await _scores.ForEventAsync(eventId, ct: ct);

        var rows = sessions
            .Select(s => new EvaluationSummaryPdfService.Row(
                s.Title, s.Room, s.ScheduledStart, s.Score.Responses, s.Score.Score))
            .ToList();

        var bytes = _summary.Render(new EvaluationSummaryPdfService.SummaryData(
            eventName, now, rows, pooled.Score, pooled.Responses));

        // 🔒 The key is the FIGURES, never the PDF bytes. A PDF carries a creation timestamp, so a
        // byte hash would differ on every render and mail somebody daily about nothing — the exact
        // §770.3 trap that made the logistics files hash their data instead.
        var key = GeneratedFile.KeyOf(
            rows.Select(r => $"{r.Title}|{r.Responses}|{r.Score}")
                .Append($"pooled:{pooled.Score}|{pooled.Responses}"));

        return new GeneratedFile(
            EvaluationSummaryPdfService.FileNameFor(eventCode),
            bytes, "application/pdf", key,
            LogisticsHeadline.Of(rows.Count, "session", "sessions"));
    }

    private async Task<bool> NotifyAsync(
        int eventId, string eventName, int copied, bool summaryWritten, CancellationToken ct)
    {
        var parts = new List<string>();
        if (copied > 0) parts.Add($"{copied} session report(s) copied");
        if (summaryWritten) parts.Add("the combined all-session summary rebuilt");

        var body =
            $"<p>The post-event evaluation consolidation for <strong>{System.Net.WebUtility.HtmlEncode(eventName)}</strong> "
            + "has updated the event evaluation folder.</p>"
            + $"<p>{System.Net.WebUtility.HtmlEncode(string.Join(", ", parts))}.</p>"
            + "<p>The files are in <strong>Event/Evaluations/During the event/SessionEvaluations</strong>. "
            + "The per-speaker results are untouched in their own folder — this is a copy, not a move.</p>";

        try
        {
            using (_emailContext?.Set(new EmailContext(
                "evaluation-consolidated", eventId, null, "Organizers",
                TemplateName: "evaluation-consolidated",
                RingExempt: true)))
            {
                await _email.SendAsync(
                    NotifyAddress,
                    $"Evaluation results consolidated — {eventName}",
                    body, ct);
            }
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed notification must not undo a successful consolidation — the files are
            // published and correct either way.
            _log?.LogWarning(ex, "§6.6: the consolidation notification could not be sent.");
            return false;
        }
    }
}
