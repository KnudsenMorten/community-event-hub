
using CommunityHub.Core.Integrations.Graphics;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations.DocLibrary;

/// <summary>What publishing one logistics file did.</summary>
/// <param name="Changed">
/// True when the DATA differed and the file was written. <b>This is the value the mail schedule
/// keys on</b> — work-order §6.4: <i>"unchanged input produces an identical file and, for
/// change-triggered mail, no send"</i>.
/// </param>
/// <param name="WebUrl">
/// §6.3 — where the file actually is, so the organizer page can link straight to it instead of
/// telling somebody a folder name to go and find. Null when the file could not be published, and
/// null is honest: a link that 404s is worse than no link.
/// </param>
public sealed record DocLibraryPublishResult(
    string FileName, string Folder, bool Changed, bool Written, string? Error, string? WebUrl = null)
{
    public bool Ok => Error is null;
}

/// <summary>
/// §6.4 — writes a generated logistics file into the document library, and knows whether it actually
/// CHANGED.
/// </summary>
/// <remarks>
/// <para>🔑 <b>"Changed" is the whole point of this class.</b> The files rebuild daily; the mails go
/// weekly, and the hotel mail goes on CHANGE. Without a reliable "did this differ?", the
/// change-triggered mail becomes a daily mail to an external hotel contact — the fastest way to
/// teach a venue to ignore CEH. So the publisher compares the bytes it is about to write with the
/// bytes already there and reports the truth.</para>
///
/// <para>⚠️ <b>Comparing the DATA KEY, not the file bytes.</b> An .xlsx is a ZIP whose document
/// properties and per-entry timestamps are not reproducible, so two builds from identical data
/// differ — a byte comparison reports "changed" every single day. A test caught this. What it
/// trades away is stated on PublishAsync.</para>
///
/// <para>🔒 <b>A deterministic CONTENT KEY is the producer's job.</b> It is hashed from the rows
/// that were written — stable order, stable formatting — so it changes when, and only when, the
/// answer changes. Each producer's tests pin exactly that.</para>
/// </remarks>
public sealed class DocLibraryFilePublisher
{
    private readonly ISharePointFileStore _store;
    private readonly IDocLibraryPathResolver _paths;
    private readonly ILogger<DocLibraryFilePublisher>? _log;

    public DocLibraryFilePublisher(
        ISharePointFileStore store,
        IDocLibraryPathResolver paths,
        ILogger<DocLibraryFilePublisher>? log = null)
    {
        _store = store;
        _paths = paths;
        _log = log;
    }

    /// <summary>True when the library can be both read and written on this host.</summary>
    public bool CanPublish => _store.CanStore && _store.CanRead;

    /// <summary>
    /// Publish one file to a registered folder, writing ONLY when the DATA differs.
    /// </summary>
    /// <param name="previousKey">
    /// The <see cref="GeneratedFile.ContentKey"/> from the last successful publish, or null when
    /// this file has never been published. The CALLER owns that memory — the job that runs this
    /// keeps it — so the publisher stays stateless and testable.
    /// </param>
    /// <remarks>
    /// 🔒 <b>Corrected after the tests failed.</b> This originally compared the FILE BYTES against
    /// the library copy, which reads as the more honest check and is not: an <c>.xlsx</c> is a ZIP,
    /// and neither ClosedXML's document properties nor the per-entry timestamps are reproducible, so
    /// two builds from identical data differ. Every file would have reported "changed" every day and
    /// §6.4's change-triggered hotel mail would have gone to an external contact daily.
    ///
    /// <para>⚠️ <b>What this trades away, stated plainly:</b> a file hand-edited IN the library is
    /// no longer detected, because the key describes our data and not their copy. The next real data
    /// change overwrites it. A generated file is not a place to keep hand edits — but somebody will
    /// try, so this is written down rather than discovered.</para>
    ///
    /// <para>A file that has VANISHED from the library is still rewritten even when the key matches:
    /// "we published it once" is not the same as "it is there".</para>
    /// </remarks>
    /// <param name="force">
    /// §6.3 "Generate now" — rewrite the file even when the data has not changed.
    /// <para>⚠️ <b>A forced write is still not a CHANGE.</b> It reports <c>Written</c> but not
    /// <c>Changed</c>, so pressing the button does not mail an external hotel contact a rooming list
    /// identical to the one they already have. The button exists to repair a file somebody edited or
    /// deleted by hand, and repairing a file is not news.</para>
    /// </param>
    public async Task<DocLibraryPublishResult> PublishAsync(
        string pathKey, GeneratedFile file, string? previousKey, CancellationToken ct = default,
        bool force = false)
    {
        var fileName = file.FileName;
        var content = file.Content;
        var contentType = file.ContentType;
        if (!_paths.TryResolve(pathKey, out var folder) || string.IsNullOrWhiteSpace(folder))
        {
            return new DocLibraryPublishResult(
                fileName, string.Empty, false, false,
                $"No folder is configured for '{pathKey}'.");
        }

        if (!CanPublish)
        {
            return new DocLibraryPublishResult(
                fileName, folder, false, false,
                "The document library is not writable on this host.");
        }

        try
        {
            var dataUnchanged = previousKey is not null
                                && string.Equals(previousKey, file.ContentKey, StringComparison.Ordinal);

            // Cheap: one folder listing, no download. "Published once" is not "still there".
            var existing = await FindAsync(folder, fileName, ct);
            var present = existing is not null;

            if (dataUnchanged && present && !force)
            {
                // The steady state, and it must be CHEAP and SILENT: no write, no mail, no noise.
                _log?.LogDebug("§6.4 logistics: '{File}' is unchanged — not rewritten.", fileName);
                return new DocLibraryPublishResult(
                    fileName, folder, false, false, null, existing!.WebUrl);
            }

            var stored = await _store.UploadToFolderAsync(folder, fileName, content, contentType, ct);

            _log?.LogInformation(
                "§6.4 logistics: '{File}' {What} in '{Folder}'.",
                fileName,
                !present ? "created" : "updated",
                folder);

            // ⚠️ A file restored because it had VANISHED — or rewritten on a FORCED regenerate — is
            // reported as Written but NOT as Changed: the data did not change, so the venue does not
            // need another mail about it.
            return new DocLibraryPublishResult(
                fileName, folder, !dataUnchanged, true, null,
                string.IsNullOrWhiteSpace(stored.WebUrl) ? existing?.WebUrl : stored.WebUrl);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One file's failure must not stop the rest of the run — the other ten still have to
            // reach the venue. The error is DATA so the job can report it.
            _log?.LogWarning(ex, "§6.4 logistics: '{File}' could not be published.", fileName);
            return new DocLibraryPublishResult(fileName, folder, false, false, ex.Message);
        }
    }

    /// <summary>
    /// The file as it currently sits in the folder, or null. One listing, no download.
    /// </summary>
    /// <remarks>
    /// This replaced a bool <c>ExistsAsync</c>: the same listing already carries the file's
    /// <c>WebUrl</c>, so throwing it away and then needing a link for §6.3 would have meant a second
    /// round-trip to learn something we had just been told.
    /// </remarks>
    private async Task<SharePointFileRef?> FindAsync(
        string folder, string fileName, CancellationToken ct)
    {
        var files = await _store.ListAsync(folder, ct);
        return files.FirstOrDefault(
            f => string.Equals(f.Name, fileName, StringComparison.OrdinalIgnoreCase));
    }
}
