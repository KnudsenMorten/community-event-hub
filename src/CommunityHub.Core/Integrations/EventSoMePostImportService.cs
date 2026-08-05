using System.Text;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.DocLibrary;
using CommunityHub.Core.Integrations.Graphics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations;

/// <summary>What an import did to ONE post. Every slug in the deck gets exactly one of these.</summary>
public enum EventSoMePostImportOutcome
{
    /// <summary>The slug was new — the post was created.</summary>
    Created = 0,

    /// <summary>
    /// The slug already existed and its overwrite tick was OFF, so the stored post was left exactly
    /// as it was. §828.1 — the DEFAULT, and reported rather than silently skipped.
    /// </summary>
    SkippedNotAllowed = 1,

    /// <summary>The slug existed and its tick was ON, so the stored post was replaced.</summary>
    Overwritten = 2,

    /// <summary>The block could not be read; the reason is on the line.</summary>
    Failed = 3,
}

/// <summary>One line of the run report — what happened to one slug, and why.</summary>
public sealed record EventSoMePostImportLine(
    string Slug,
    EventSoMePostImportOutcome Outcome,
    string Detail);

/// <summary>
/// The full report of one import run. ⚠️ §828.2(3) is explicit that a SILENT importer is the failure
/// mode here: he needs to read "4 created, 6 left alone", not "import complete".
/// </summary>
public sealed record EventSoMePostImportReport(
    bool Succeeded,
    string SourceFileName,
    IReadOnlyList<EventSoMePostImportLine> Lines,
    IReadOnlyList<string> Problems)
{
    public int Created => Lines.Count(l => l.Outcome == EventSoMePostImportOutcome.Created);
    public int Skipped => Lines.Count(l => l.Outcome == EventSoMePostImportOutcome.SkippedNotAllowed);
    public int Overwritten => Lines.Count(l => l.Outcome == EventSoMePostImportOutcome.Overwritten);
    public int Failed => Lines.Count(l => l.Outcome == EventSoMePostImportOutcome.Failed);

    /// <summary>A one-line summary in his terms — the sentence the organizer page leads with.</summary>
    public string Summary =>
        Succeeded
            ? $"{Created} created, {Skipped} left alone, {Overwritten} overwritten"
              + (Failed > 0 ? $", {Failed} failed" : string.Empty)
            : "Import did not run — " + (Problems.FirstOrDefault() ?? "unknown reason") + ".";
}

/// <summary>
/// §828 — IMPORTS THE EVENT-POST DECK INTO THE POST REPO.
///
/// <para>The markdown file in the document library is a <b>DROP-BOX, not a mirror</b>: he lands a
/// deck, CEH imports it, and <b>the database owns the words from that moment on</b>. He can land
/// another file later and the same rules apply again (§828, correcting §824.24).</para>
///
/// <para>🔒 <b>THE RULE THAT MATTERS MOST (§828.1): AN IMPORTED POST IS NEVER OVERWRITTEN unless
/// that specific post's <see cref="EventSoMePost.AllowImportOverwrite"/> tick is on.</b> The key is
/// the SLUG. Re-importing a known slug is a REPORTED no-op — never a silent skip, and never a silent
/// replacement. The reason is concrete: an imported post is something he EDITS in the post editor
/// (§824.17), and a second import that quietly replaced it would throw the edit away.</para>
///
/// <para>The tick is <b>consumed</b> by the overwrite that uses it: allowing a replacement is a
/// deliberate act on one post, not a mode that stays on and re-flattens his edits at the next
/// import.</para>
/// </summary>
public sealed class EventSoMePostImportService
{
    private readonly CommunityHubDbContext _db;
    private readonly ISharePointFileStore _store;
    private readonly IDocLibraryPathResolver _paths;
    private readonly ILogger<EventSoMePostImportService> _log;

    public EventSoMePostImportService(
        CommunityHubDbContext db,
        ISharePointFileStore store,
        IDocLibraryPathResolver paths,
        ILogger<EventSoMePostImportService> log)
    {
        _db = db;
        _store = store;
        _paths = paths;
        _log = log;
    }

    /// <summary>True when the deck can actually be fetched — the page offers the button on this.</summary>
    public bool CanRead =>
        _store.CanRead && _paths.TryResolve(DocLibraryPaths.EventSoMeTextFile, out var p)
        && !string.IsNullOrWhiteSpace(p);

    /// <summary>
    /// Reads the deck from <c>DocLibraryPaths.EventSoMeTextFile</c> and applies it to
    /// <paramref name="eventId"/>. Returns the report; never throws for content reasons.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b><c>EventSoMeTextFile</c> is a FILE, not a folder</b> — the only such entry in the
    /// registry (§824.24). The store lists FOLDERS, so the resolved path is split into its folder and
    /// leaf name here, and the leaf is matched in the listing. Listing the full file path would
    /// return an empty result that reads exactly like "the deck is not there yet".
    /// </remarks>
    public async Task<EventSoMePostImportReport> ImportAsync(
        int eventId, string? byEmail = null, CancellationToken ct = default)
    {
        if (!_paths.TryResolve(DocLibraryPaths.EventSoMeTextFile, out var fullPath)
            || string.IsNullOrWhiteSpace(fullPath))
        {
            return Failed(string.Empty,
                "the event-post deck path is not configured in the document-library registry");
        }

        var normalized = fullPath.Replace('\\', '/').Trim('/');
        var cut = normalized.LastIndexOf('/');
        var folder = cut > 0 ? normalized[..cut] : string.Empty;
        var fileName = cut >= 0 ? normalized[(cut + 1)..] : normalized;

        if (!_store.CanRead)
        {
            return Failed(fileName, "the document library is not wired for reads on this environment");
        }

        try
        {
            var files = await _store.ListAsync(folder, ct);
            var match = files.FirstOrDefault(
                f => f.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                return Failed(fileName, $"'{fileName}' was not found in '{folder}'");
            }

            var bytes = await _store.DownloadAsync(match.ItemId, ct);
            if (bytes is null || bytes.Length == 0)
            {
                return Failed(fileName, $"'{fileName}' downloaded as empty");
            }

            // The deck is authored with emoji and en-dashes throughout, so the encoding is not
            // incidental: read it as UTF-8 explicitly rather than inheriting a machine default.
            var markdown = new UTF8Encoding(false).GetString(bytes).TrimStart('﻿');

            return await ApplyAsync(eventId, markdown, fileName, byEmail, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "§828: could not read the event-post deck at {Path}.", fullPath);
            return Failed(fileName, $"could not read '{fullPath}': {ex.Message}");
        }
    }

    private static EventSoMePostImportReport Failed(string fileName, string problem) =>
        new(false, fileName, Array.Empty<EventSoMePostImportLine>(), new[] { problem });

    /// <summary>
    /// Applies already-fetched deck text. Split out from <see cref="ImportAsync"/> so the import
    /// RULES can be tested without SharePoint — the §828.1 behaviour is the part worth pinning.
    /// </summary>
    public async Task<EventSoMePostImportReport> ApplyAsync(
        int eventId,
        string markdown,
        string sourceFileName,
        string? byEmail = null,
        CancellationToken ct = default)
    {
        var parsed = EventSoMeDeckParser.Parse(markdown);
        var lines = new List<EventSoMePostImportLine>();

        var existing = await _db.EventSoMePosts
            .Include(p => p.Occurrences)
            .Where(p => p.EventId == eventId)
            .ToListAsync(ct);

        var bySlug = existing.ToDictionary(p => p.Slug, StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.UtcNow;

        foreach (var post in parsed.Posts)
        {
            if (!bySlug.TryGetValue(post.Slug, out var row))
            {
                var created = new EventSoMePost
                {
                    EventId = eventId,
                    Slug = post.Slug,
                    Title = post.Title,
                    Body = post.Body,
                    AllowImportOverwrite = false,   // 🔒 §828.1 — the default, always.
                    ImportedAt = now,
                    SourceFileName = sourceFileName,
                    CreatedAt = now,
                    LastUpdatedByEmail = byEmail,
                };

                ReplaceOccurrences(created, post);
                _db.EventSoMePosts.Add(created);
                bySlug[post.Slug] = created;

                lines.Add(new EventSoMePostImportLine(
                    post.Slug, EventSoMePostImportOutcome.Created,
                    $"created with {post.Occurrences.Count} dated run(s)"));
                continue;
            }

            if (!row.AllowImportOverwrite)
            {
                // 🔒 The default path, and the one that protects his edits. REPORTED, not silent.
                lines.Add(new EventSoMePostImportLine(
                    post.Slug, EventSoMePostImportOutcome.SkippedNotAllowed,
                    "already imported and overwrite is not allowed — left exactly as it is. "
                    + "Tick 'Allow import to overwrite' on this post to replace it."));
                continue;
            }

            row.Title = post.Title;
            row.Body = post.Body;
            row.SourceFileName = sourceFileName;
            row.LastOverwrittenAt = now;
            row.UpdatedAt = now;
            row.LastUpdatedByEmail = byEmail;

            // 🔒 The tick is CONSUMED by the overwrite it authorised. Leaving it on would turn a
            // one-time permission into a standing one, and the next deck would silently flatten
            // whatever he edited in between — the exact loss §828.1 exists to prevent.
            row.AllowImportOverwrite = false;

            _db.EventSoMePostOccurrences.RemoveRange(row.Occurrences);
            row.Occurrences.Clear();
            ReplaceOccurrences(row, post);

            lines.Add(new EventSoMePostImportLine(
                post.Slug, EventSoMePostImportOutcome.Overwritten,
                $"replaced (tick was on, now cleared); {post.Occurrences.Count} dated run(s)"));
        }

        await _db.SaveChangesAsync(ct);

        var report = new EventSoMePostImportReport(true, sourceFileName, lines, parsed.Problems);

        _log.LogInformation(
            "§828 event-post import from {File} for event {EventId}: {Summary}.",
            sourceFileName, eventId, report.Summary);

        return report;
    }

    private static void ReplaceOccurrences(EventSoMePost row, EventSoMeDeckPost post)
    {
        var seq = 1;
        foreach (var o in post.Occurrences.OrderBy(o => o.Date))
        {
            row.Occurrences.Add(new EventSoMePostOccurrence
            {
                Sequence = seq++,
                PostDate = o.Date,
                GraphicFileName = o.GraphicFileName,
                SourcePhotoFileName = o.SourcePhotoFileName,
            });
        }
    }
}
