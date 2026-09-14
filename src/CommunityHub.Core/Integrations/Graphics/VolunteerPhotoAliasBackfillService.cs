using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations.Graphics;

/// <summary>
/// §1145 — give every PRE-§1132 volunteer photo its missing name alias.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-28: <i>"the volunteers that signed up before that change only have one
/// file with id. do we have a service that fixes that"</i>. We did not — for volunteers.</para>
///
/// <para>🔴 <b>Speakers had this and volunteers were missed.</b> §1132 added the
/// <c>volunteer-photo-{Name}-{id}</c> alias beside the authoritative
/// <c>volunteer-photo-{id}</c> file, but only at UPLOAD time (<c>Volunteer/Signup</c>), whereas
/// <see cref="SpeakerPhotoArchiveService"/> got a <c>HasAlias</c> check that back-fills the whole
/// roster on every sweep. So a convention introduced on 2026-08-25 silently applied only to people
/// who signed up after it — leaving the earlier volunteers permanently id-only.</para>
///
/// <para>🔑 <b>A COPY, not a re-fetch — and that is the real difference from the speaker sweep.</b>
/// A speaker's photo has an upstream (Sessionize) the archive can pull again. A volunteer's has
/// none: the file in SharePoint IS the original. So this reads the existing bytes and writes them
/// back under the alias name. Nothing is deleted, nothing is renamed, and the id file — the one
/// every other part of the system resolves by — is never touched.</para>
///
/// <para>🔒 <b>Idempotent by construction.</b> A volunteer who already has an alias is skipped, so
/// the sweep converges to zero work and can run on a timer for ever.</para>
/// </remarks>
public sealed class VolunteerPhotoAliasBackfillService
{
    private readonly CommunityHubDbContext _db;
    private readonly ISharePointFileStore _store;
    private readonly DocLibrary.IDocLibraryPathResolver _paths;
    private readonly ILogger<VolunteerPhotoAliasBackfillService>? _log;

    public VolunteerPhotoAliasBackfillService(
        CommunityHubDbContext db,
        ISharePointFileStore store,
        DocLibrary.IDocLibraryPathResolver paths,
        ILogger<VolunteerPhotoAliasBackfillService>? log = null)
    {
        _db = db;
        _store = store;
        _paths = paths;
        _log = log;
    }

    /// <summary>What one backfill pass did. Zeroes are a valid, logged outcome.</summary>
    /// <param name="Written">Aliases actually created this run.</param>
    /// <param name="AlreadyPresent">Volunteers whose alias was already there.</param>
    /// <param name="Unnamed">
    /// Id files whose participant could not be named (row gone, or the name sanitises to nothing).
    /// Counted rather than retried for ever — there is no alias to write for these.
    /// </param>
    /// <param name="Failed">Copies that threw. Never fatal: the next run tries again.</param>
    /// <param name="Inactive">The folder is not configured / not readable — nothing ran.</param>
    public sealed record BackfillResult(
        int Written, int AlreadyPresent, int Unnamed, int Failed, bool Inactive = false);

    /// <summary>
    /// §1145 — which id-only photos are missing their alias, given a folder listing and the names.
    /// </summary>
    /// <remarks>
    /// <para>Pure and public so the sweep and its tests answer the SAME question — the
    /// <c>SessionGraphicInputHash</c> pattern.</para>
    ///
    /// <para>🔑 <b>The alias is matched WITHOUT its extension</b>, for the reason
    /// <see cref="SpeakerPhotoArchiveService"/> learned the hard way: the alias carries whatever the
    /// upload served (.jpg/.png/.jpeg), so comparing against a guessed extension reports every alias
    /// missing and re-copies the whole folder on every run.</para>
    /// </remarks>
    public static IReadOnlyList<int> ParticipantsMissingAlias(
        IEnumerable<string> fileNames, IReadOnlyDictionary<int, string?> namesById)
    {
        var names = fileNames.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();

        // Every id the folder carries, from BOTH shapes (the alias also ends in the id).
        var withId = new List<(string Name, int Id)>();
        foreach (var n in names)
        {
            if (VolunteerPhotoFileName.TryParse(n, out var id) && id > 0) withId.Add((n, id));
        }

        var missing = new List<int>();
        foreach (var id in withId.Select(x => x.Id).Distinct())
        {
            if (!namesById.TryGetValue(id, out var fullName)) continue;

            var stem = VolunteerPhotoFileName.BuildAlias(id, fullName, ".x");
            if (stem is null) continue;              // name sanitises to nothing ⇒ no alias exists
            stem = stem[..^2];                       // drop the ".x" placeholder extension

            var hasAlias = names.Any(n =>
                n.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase));

            if (!hasAlias) missing.Add(id);
        }

        return missing;
    }

    /// <summary>Write the missing aliases for one edition.</summary>
    public async Task<BackfillResult> RunAsync(int eventId, CancellationToken ct = default)
    {
        if (!_store.CanRead || !_store.CanStore)
        {
            _log?.LogInformation("§1145 volunteer photo alias backfill: store not readable/writable — inert.");
            return new BackfillResult(0, 0, 0, 0, Inactive: true);
        }

        if (!_paths.TryResolve(DocLibrary.DocLibraryPaths.VolunteerPhotos, out var folder)
            || string.IsNullOrWhiteSpace(folder))
        {
            _log?.LogInformation("§1145 volunteer photo alias backfill: no volunteer-photo folder — inert.");
            return new BackfillResult(0, 0, 0, 0, Inactive: true);
        }

        IReadOnlyList<SharePointFileRef> files;
        try { files = await _store.ListAsync(folder, ct); }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "§1145: could not list '{Folder}' — nothing back-filled.", folder);
            return new BackfillResult(0, 0, 0, 0, Inactive: true);
        }

        if (files.Count == 0) return new BackfillResult(0, 0, 0, 0);

        var namesById = await _db.Participants
            .Where(p => p.EventId == eventId && p.Role == ParticipantRole.Volunteer)
            .Select(p => new { p.Id, p.FullName })
            .ToDictionaryAsync(x => x.Id, x => (string?)x.FullName, ct);

        var fileNames = files.Select(f => f.Name).ToList();
        var missing = ParticipantsMissingAlias(fileNames, namesById);

        var alreadyPresent = fileNames
            .Select(n => VolunteerPhotoFileName.TryParse(n, out var id) ? id : 0)
            .Where(id => id > 0 && namesById.ContainsKey(id))
            .Distinct()
            .Count() - missing.Count;

        var written = 0;
        var unnamed = 0;
        var failed = 0;

        foreach (var id in missing)
        {
            ct.ThrowIfCancellationRequested();

            // The SOURCE is the id file — the authoritative one. Its extension is whatever the
            // volunteer uploaded, and the alias must carry the same one.
            var source = files.FirstOrDefault(f =>
                VolunteerPhotoFileName.TryParse(f.Name, out var fid) && fid == id
                && !CarriesAName(f.Name, id));

            if (source is null) { unnamed++; continue; }

            var ext = System.IO.Path.GetExtension(source.Name);
            var alias = VolunteerPhotoFileName.BuildAlias(id, namesById.GetValueOrDefault(id), ext);
            if (alias is null) { unnamed++; continue; }

            try
            {
                var bytes = await _store.DownloadAsync(source.ItemId, ct);
                if (bytes is not { Length: > 0 })
                {
                    // 🔒 NEVER write an empty alias. A 0-byte photo beside a good one is worse than
                    // no alias: it looks like a delivered file and shows as a broken image.
                    _log?.LogWarning(
                        "§1145: '{File}' downloaded empty — no alias written for volunteer {Id}.",
                        source.Name, id);
                    failed++;
                    continue;
                }

                await _store.UploadToFolderAsync(folder, alias, bytes, ContentTypeFor(ext), ct);
                written++;

                _log?.LogInformation(
                    "§1145: wrote alias '{Alias}' from '{Source}' for volunteer {Id}.",
                    alias, source.Name, id);
            }
            catch (Exception ex)
            {
                // Best-effort per file: one bad photo must not stop the rest of the folder.
                _log?.LogWarning(ex, "§1145: could not write alias for volunteer {Id}.", id);
                failed++;
            }
        }

        return new BackfillResult(written, Math.Max(0, alreadyPresent), unnamed, failed);
    }

    /// <summary>Is this the NAME-carrying alias for <paramref name="id"/> rather than the id file?</summary>
    private static bool CarriesAName(string fileName, int id)
    {
        var bare = fileName;
        var dot = bare.LastIndexOf('.');
        if (dot > 0) bare = bare[..dot];

        // The id file's whole body is the id; the alias has a name in front of it.
        return !bare.Equals(
            $"{VolunteerPhotoFileName.Prefix}{id}", StringComparison.OrdinalIgnoreCase);
    }

    private static string ContentTypeFor(string? extension) =>
        (extension ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => "image/jpeg",
        };
}
