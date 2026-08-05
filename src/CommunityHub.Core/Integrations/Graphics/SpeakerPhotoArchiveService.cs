using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations.Graphics;

/// <summary>
/// §764 — copies every COMMUNITY and GUEST speaker's photo into the one SharePoint folder, so all
/// speaker pictures live in a single place.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-01: <i>"automatically download the speakers (community + guest) photo and
/// put it here … make sure that the sponsor speaker photo upload lands there as well so all speaker
/// pictures are in 1 place"</i>.</para>
///
/// <para><b>The sponsor half was the config move</b> (§764: <c>speakerPhotoFolderPath</c> now points
/// at <c>EventHub/Speakers/Photos</c>), so a sponsor-uploaded photo already lands here. This service
/// is the other half: community and guest speakers have no upload step — their picture is a
/// Sessionize CDN URL — so CEH fetches it and puts a copy in the same folder, under the same naming
/// convention the sponsor upload uses. One folder, one convention, whoever the speaker is.</para>
///
/// <para>🔑 <b>Why this is not the existing import-time copy.</b> <c>SessionizeImportService</c>
/// already fetches a picture, but only during an import, only for a speaker whose
/// <c>PhotoSharePointPath</c> is still empty — so it happens ONCE and never again — into the
/// graphics root under a machine name (<c>speaker-42.jpg</c>). That is a cache for the designer
/// feed, not the folder he opens and looks at. This service re-checks continuously, follows a
/// CHANGED photo, and writes a human-readable name.</para>
///
/// <para>🔒 <b>Idempotent by SOURCE URL, not by "have we ever stored one".</b>
/// <see cref="SpeakerProfile.PhotoArchivedFromUrl"/> records the exact URL the stored copy came
/// from, so a run costs one cheap comparison per speaker and re-downloads only when the speaker
/// actually changes their picture. Keying on "is a path set" (the import's rule) is what froze the
/// first photo for ever; keying on the run would re-pull every speaker from the Sessionize CDN every
/// cycle, which is both wasteful and rude to someone else's server.</para>
///
/// <para>🔒 <b><see cref="SpeakerProfile.PhotoUrl"/> is NEVER rewritten here.</b> The sponsor upload
/// re-points it at the hub proxy because there the upload IS the source. Doing that here would make
/// the next run read our own proxy URL as the speaker's photo source and archive our own file back
/// on top of itself — a loop that would look like it was working.</para>
/// </remarks>
public sealed class SpeakerPhotoArchiveService
{
    /// <summary>The system name used in write-guard refusals and logs.</summary>
    /// <remarks>⚠️ NOT named <c>System</c>: a const by that name shadows the namespace, and every
    /// <c>System.*</c> reference in this file then fails to resolve.</remarks>
    private const string SystemName = "SharePoint";

    private readonly CommunityHubDbContext _db;
    private readonly SharePointUploadClient _sp;
    private readonly EventEditionConfigLoader _cfg;
    private readonly EventConfigOptions _cfgOptions;
    private readonly IExternalWriteGuard _writes;
    private readonly TimeProvider _clock;
    private readonly EngineAlertSender? _alerts;
    private readonly ILogger<SpeakerPhotoArchiveService>? _log;
    private readonly HttpClient _http;

    // Overridable UPLOAD seam (default = the real SharePoint client), mirroring the §38e engine's
    // pull seam: a test drives the archive rules — which speakers, when to re-fetch, what the stamp
    // means — without standing up Graph auth. Production wiring leaves it null.
    private readonly Func<string, byte[], string, CancellationToken, Task>? _uploadOverride;

    private readonly DocLibrary.IDocLibraryPathResolver? _paths;

    public SpeakerPhotoArchiveService(
        CommunityHubDbContext db, SharePointUploadClient sp,
        EventEditionConfigLoader cfg, EventConfigOptions cfgOptions,
        IExternalWriteGuard? writes = null, TimeProvider? clock = null,
        EngineAlertSender? alerts = null, ILogger<SpeakerPhotoArchiveService>? log = null,
        HttpClient? http = null,
        Func<string, byte[], string, CancellationToken, Task>? uploadOverride = null,
        DocLibrary.IDocLibraryPathResolver? paths = null)
    {
        _paths = paths;
        _db = db; _sp = sp; _cfg = cfg; _cfgOptions = cfgOptions;
        _writes = writes ?? new AllowAllExternalWrites();
        _clock = clock ?? TimeProvider.System;
        _alerts = alerts; _log = log;
        _http = http ?? Shared;
        _uploadOverride = uploadOverride;
    }

    // Low volume (tens of speakers, once a day); one client per process is plenty.
    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>The outcome of one archive pass.</summary>
    /// <param name="Skipped">Already archived from this exact URL — the steady state.</param>
    /// <param name="NoPhoto">Community/guest speakers with no photo at all — a real gap, counted.</param>
    /// <param name="Uncategorized">
    /// Speakers with no category yet (§299 6.1). NOT archived and NOT an error: CEH does not know
    /// whether they are community, guest or sponsor, and guessing a folder for them would be a
    /// decision this service is not entitled to make.
    /// </param>
    /// <param name="AlreadyStored">
    /// §764.1 — the photo was UPLOADED through CEH, so it is already in this very folder and there
    /// is nothing to fetch. Counted separately and NEVER reported as a failure.
    /// </param>
    public sealed record Result(
        bool Ran, string? InactiveReason,
        int Archived, int Skipped, int Failed, int NoPhoto, int Uncategorized,
        int AlreadyStored = 0)
    {
        public static Result Inactive(string reason) => new(false, reason, 0, 0, 0, 0, 0);
    }

    /// <summary>
    /// §764.1 — is this photo already held by CEH, rather than out on somebody else's server?
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-01: <i>"you try to use same method for speakers with sponsorCategory -
    /// here you must remember that we dont use sessionize and the photo is uploaded instead, so file
    /// exist already in the case"</i>.</para>
    ///
    /// <para>🔑 <b>CATEGORY WAS THE WRONG SIGNAL.</b> This service originally decided by speaker
    /// category (community/guest in, sponsor out) and treated that as equivalent to "has an external
    /// photo URL". It is not: when a photo is UPLOADED, §665 rewrites <c>PhotoUrl</c> to the hub
    /// proxy route, so the "source" becomes CEH's own URL — which this job then tried to download
    /// and reported as a failure, for a file already sitting in the destination folder. A rule keyed
    /// on category is always one mis-categorisation away from that.</para>
    ///
    /// <para>The right question is about the URL, not the person: fetch only what lives elsewhere.</para>
    /// </remarks>
    public static bool IsAlreadyStoredByCeh(string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri)) return true;   // relative ⇒ ours
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return true;
        // The §665 proxy route — the upload case, whatever host it is served from.
        if (uri.AbsolutePath.StartsWith(SpeakerPhotoUrl.Route, StringComparison.OrdinalIgnoreCase))
            return true;
        // Downloading out of SharePoint to upload back into SharePoint is a no-op with extra steps
        // (and anonymously it just fails).
        return uri.Host.Contains("sharepoint.com", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<Result> RunAsync(int eventId, CancellationToken ct = default)
    {
        SharePointEditionConfig? sp;
        try { sp = _cfg.Load(_cfgOptions.EventConfigPath).SharePoint; }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "SpeakerPhotoArchive: edition config unreadable.");
            return Result.Inactive("The edition configuration could not be read.");
        }

        // §768.14 — the folder is a registry key now, resolved under the single configured root.
        // 🔒 It MUST be the same key SpeakerPhotoService reads, or this writes where nothing looks.
        string? folder = null;
        if (_paths is not null
            && _paths.TryResolve(DocLibrary.DocLibraryPaths.SpeakerPhotos, out var resolved))
            folder = resolved.Trim().Trim('/');

        if (sp is null || string.IsNullOrWhiteSpace(folder))
            return Result.Inactive("No speaker-photo folder is configured for this edition.");

        // 🔒 Asked ONCE, up front. The guard is what keeps DEV from writing to SharePoint at all
        // (§340-H), and it LOGS its refusal — but asking per speaker would produce one refusal line
        // per speaker per run on DEV, which is how a real signal gets buried.
        if (!await _writes.AllowAsync(SystemName, nameof(SpeakerPhotoArchiveService), ct))
            return Result.Inactive(
                "External writes are disabled on this host, so no photo was copied to SharePoint.");

        // Community + Guest only. A SPONSOR speaker's photo is UPLOADED through CEH and already
        // lands in this same folder (§764), so fetching one would be a second writer for a file that
        // already exists — and the upload is the better source, being what the sponsor chose.
        var candidates = await _db.SpeakerProfiles
            .Where(p => p.EventId == eventId)
            .Join(_db.Participants.Where(x => x.EventId == eventId && x.IsActive),
                  p => p.ParticipantId, x => x.Id,
                  (p, x) => new { Profile = p, x.FullName })
            .ToListAsync(ct);

        int archived = 0, skipped = 0, failed = 0, noPhoto = 0, uncategorized = 0, alreadyStored = 0;
        var failures = new List<string>();

        foreach (var c in candidates)
        {
            var p = c.Profile;

            if (p.Category is null) { uncategorized++; continue; }
            if (p.Category is not (SpeakerCategory.Community or SpeakerCategory.Guest)) continue;

            var source = p.PhotoUrl?.Trim();
            if (string.IsNullOrWhiteSpace(source)) { noPhoto++; continue; }

            // §764.1 — an UPLOADED photo is already in this folder. Nothing to fetch, and it is not
            // a failure: mailing him about a file that is present and correct is what went wrong on
            // the first live run.
            if (IsAlreadyStoredByCeh(source!)) { alreadyStored++; continue; }

            // The steady state: this exact picture is already in the folder, UNDER ITS CURRENT NAME.
            //
            // 🔒 §768.16 — the second half of that condition is load-bearing. The rule used to be
            // "same source URL ⇒ nothing to do", which is right while the convention holds still and
            // WRONG the day it changes: the photo has not changed, so the job would never touch the
            // file again and every legacy `speaker-photo-{Name}-{id}` name would be frozen for ever.
            // Asking whether the STORED file is on the current convention re-fetches each one exactly
            // once, then returns to the cheap steady state.
            if (string.Equals(p.PhotoArchivedFromUrl, source, StringComparison.Ordinal)
                && SpeakerPhotoFileName.IsCurrentConvention(p.PhotoSharePointPath))
            {
                skipped++;
                continue;
            }

            try
            {
                var stored = await FetchAndUploadAsync(sp, folder!, p, source!, ct);
                if (stored is null) { failed++; failures.Add($"{c.FullName}: the photo could not be downloaded"); continue; }

                p.PhotoSharePointPath = stored;
                p.PhotoArchivedFromUrl = source;
                p.PhotoArchivedAt = _clock.GetUtcNow();
                archived++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One speaker's bad URL must never stop the rest — and the reason is carried into
                // the batch report rather than only into a log nobody reads.
                failed++;
                failures.Add($"{c.FullName}: {ex.Message}");
                _log?.LogWarning(ex, "SpeakerPhotoArchive: {Name} failed.", c.FullName);
            }
        }

        if (archived > 0) await _db.SaveChangesAsync(ct);

        if (failures.Count > 0) await ReportFailuresAsync(eventId, failures, ct);

        _log?.LogInformation(
            "SpeakerPhotoArchive: archived {Archived}, unchanged {Skipped}, failed {Failed}, "
            + "no photo {NoPhoto}, uncategorized {Uncategorized}, already stored {AlreadyStored}.",
            archived, skipped, failed, noPhoto, uncategorized, alreadyStored);

        return new Result(true, null, archived, skipped, failed, noPhoto, uncategorized, alreadyStored);
    }

    private async Task<string?> FetchAndUploadAsync(
        SharePointEditionConfig sp, string folder,
        SpeakerProfile profile, string source, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(source, ct);
        if (!resp.IsSuccessStatusCode) return null;

        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length == 0) return null;

        var contentType = resp.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
        var ext = ExtensionFor(contentType);

        // 🔑 The SAME shape the sponsor upload writes, composed by the SAME function. They share a
        // folder, so a second naming convention would make it look like two systems had been pointed
        // at one place — exactly the impression §764 exists to remove. §768.16: the id alone names
        // the file; the extension follows the source.
        var fileName = SpeakerPhotoFileName.Build(profile.ParticipantId, ext);

        if (_uploadOverride is not null)
        {
            await _uploadOverride(fileName, bytes, contentType, ct);
        }
        else
        {
            using var upload = new MemoryStream(bytes);
            await _sp.UploadFileStreamAsync(
                sp.SiteUrl, sp.DriveName, folder, fileName, upload, bytes.Length, contentType, ct);
        }

        return fileName;
    }

    private async Task ReportFailuresAsync(int eventId, List<string> failures, CancellationToken ct)
    {
        if (_alerts is null) return;

        var html = "<p>These speaker photos could not be copied into the SharePoint speakers "
                   + "folder. CEH will try again on the next run — nothing else is affected.</p><ul>"
                   + string.Join("", failures.Select(f =>
                       $"<li>{System.Net.WebUtility.HtmlEncode(f)}</li>"))
                   + "</ul>";

        // ONE batched mail, throttled per edition: a per-speaker-per-run alert about a dead CDN link
        // would arrive daily for ever. DEV-silent (§752.9) — DEV does not write to SharePoint at all.
        await _alerts.AlertAsync(
            $"Speaker photos: {failures.Count} could not be copied to SharePoint", html, ct,
            throttleKey: $"speaker-photo-archive-{eventId}", devSilent: true);
    }

    private static string ExtensionFor(string contentType) =>
        contentType.Contains("png", StringComparison.OrdinalIgnoreCase) ? ".png"
        : contentType.Contains("gif", StringComparison.OrdinalIgnoreCase) ? ".gif"
        : contentType.Contains("webp", StringComparison.OrdinalIgnoreCase) ? ".webp"
        : ".jpg";

    // §768.16 — the name sanitiser is GONE, not left unused. A speaker's name is no longer part of
    // any file name (SpeakerPhotoFileName.Build), and a spare sanitiser sitting here is how the
    // second convention gets reintroduced.
}
