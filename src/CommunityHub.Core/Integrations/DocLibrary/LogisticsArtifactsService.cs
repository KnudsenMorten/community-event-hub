using CommunityHub.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Integrations.DocLibrary;

/// <summary>One generated §3.5 file, as the organizer page shows it.</summary>
/// <param name="Headline">The producer's figure, e.g. <c>"42 seats"</c>. Null on rows published
/// before headlines existed — shown as "—", never as a zero.</param>
/// <param name="WebUrl">Where it is. Null ⇒ the page offers no link rather than a broken one.</param>
/// <param name="LastMailedTo">The RESOLVED address, so an organizer sees the review mailbox while
/// the reports are unapproved and is never left thinking a hotel has already been written to.</param>
public sealed record LogisticsArtifact(
    string FileName,
    string Folder,
    string? Headline,
    string? WebUrl,
    DateTimeOffset PublishedAt,
    DateTimeOffset? LastMailedAt,
    string? LastMailedTo);

/// <summary>The last automatic run, as the organizer page shows it.</summary>
/// <param name="Problems">One per line; empty ⇒ a clean run.</param>
public sealed record LogisticsRunStatus(
    DateTimeOffset RanAt,
    int Published,
    int Unchanged,
    int Mailed,
    bool Ok,
    IReadOnlyList<string> Problems);

/// <summary>What `/Organizer/Logistics` renders for §6.3.</summary>
/// <param name="Run">Null ⇒ the job has never completed a run for this edition.</param>
/// <param name="MailsAreHeld">
/// True while the reports are unapproved. Stated on the page because "mailed to
/// mok@expertslive.dk" without the reason looks like a misconfiguration rather than the safety gate
/// it is.
/// </param>
public sealed record LogisticsArtifactsView(
    IReadOnlyList<LogisticsArtifact> Artifacts,
    LogisticsRunStatus? Run,
    bool MailsAreHeld);

/// <summary>
/// §6.3 — what the organizer Logistics page needs to show: every generated §3.5 file with its
/// figure, its link and its timestamps, plus the last automatic run's status.
/// </summary>
/// <remarks>
/// <para>🔒 <b>It reads the STATE, it does not rebuild the files.</b> Opening a page must never
/// write to the document library or take the time to build eleven workbooks. What the page shows is
/// what the last run actually published — which is also the only honest answer to "what is in the
/// library right now".</para>
///
/// <para>⚠️ <b>A file with no state row is not shown as empty — it is not shown at all.</b> The
/// producers decide what exists (a role with nobody wanting a Credly badge produces no file), so a
/// hardcoded list of expected artifacts would invent rows for files that are correctly absent, and
/// then permanently show them as missing.</para>
/// </remarks>
public sealed class LogisticsArtifactsService
{
    private readonly CommunityHubDbContext _db;
    private readonly IDocLibraryPathResolver _paths;
    private readonly LogisticsRecipients _recipients;

    public LogisticsArtifactsService(
        CommunityHubDbContext db,
        IDocLibraryPathResolver paths,
        LogisticsRecipients recipients)
    {
        _db = db;
        _paths = paths;
        _recipients = recipients;
    }

    public async Task<LogisticsArtifactsView> BuildAsync(int eventId, CancellationToken ct = default)
    {
        var states = await _db.LogisticsFileStates
            .Where(s => s.EventId == eventId)
            .OrderBy(s => s.PathKey)
            .ThenBy(s => s.FileName)
            .ToListAsync(ct);

        var artifacts = states
            .Select(s => new LogisticsArtifact(
                s.FileName,
                _paths.TryResolve(s.PathKey, out var folder) ? folder : s.PathKey,
                string.IsNullOrWhiteSpace(s.Headline) ? null : s.Headline,
                string.IsNullOrWhiteSpace(s.WebUrl) ? null : s.WebUrl,
                s.PublishedAt,
                s.LastMailedAt,
                s.LastMailedTo))
            .ToList();

        var summary = await _db.LogisticsRunSummaries
            .FirstOrDefaultAsync(s => s.EventId == eventId, ct);

        var run = summary is null
            ? null
            : new LogisticsRunStatus(
                summary.RanAt, summary.Published, summary.Unchanged, summary.Mailed, summary.Ok,
                string.IsNullOrWhiteSpace(summary.Problems)
                    ? []
                    : summary.Problems.Split('\n', StringSplitOptions.RemoveEmptyEntries
                                                    | StringSplitOptions.TrimEntries));

        return new LogisticsArtifactsView(
            artifacts, run, !_recipients.ApprovedForRealRecipients);
    }
}
