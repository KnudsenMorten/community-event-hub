using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CommunityHub.Core.Integrations.Graphics;

/// <summary>
/// Orchestrates the SoMe-graphics lifecycle (REQUIREMENTS §18): generate (composite
/// + store on SharePoint, upsert the <see cref="GraphicAsset"/> by its STABLE key),
/// the speaker RELEASE gate, the organizer OVERRULE (replace bytes, keep the stable
/// key/path/URL), the visibility queries (speaker sees only RELEASED, the sponsor
/// surface NEVER sees sponsor graphics), and the LinkedIn/X share-DRAFT builder.
///
/// Every external touch goes through a seam: <see cref="ISharePointFileStore"/>,
/// <see cref="ISpeakerPictureFetcher"/>, <see cref="ISocialShareGateway"/>. With the
/// null defaults nothing is faked — the row is upserted with the computed stable
/// path / intended URL so the engine + gates are fully testable offline.
/// </summary>
public sealed class GraphicsService
{
    private readonly CommunityHubDbContext _db;
    private readonly GraphicCompositor _compositor;
    private readonly ISharePointFileStore _store;
    private readonly ISpeakerPictureFetcher _pictureFetcher;
    private readonly ISocialShareGateway _share;
    private readonly GraphicsSharePointOptions _spOptions;

    public GraphicsService(
        CommunityHubDbContext db,
        GraphicCompositor compositor,
        ISharePointFileStore store,
        ISpeakerPictureFetcher pictureFetcher,
        ISocialShareGateway share,
        IOptions<GraphicsSharePointOptions> spOptions)
    {
        _db = db;
        _compositor = compositor;
        _store = store;
        _pictureFetcher = pictureFetcher;
        _share = share;
        _spOptions = spOptions.Value;
    }

    // ===================================================================
    //  Sessionize picture: fetch DOWN + store on SharePoint (step 2)
    // ===================================================================

    /// <summary>
    /// Fetch the speaker picture from its (Sessionize-provided) URL and store the
    /// BYTES on SharePoint under a stable per-speaker path — so the hub holds the
    /// stored copy, not just a foreign URL. Returns the stored picture's path/URL,
    /// or null when there is no picture URL / the fetch fails / no live store is
    /// wired. Pure fetch+store; does NOT run the Sessionize import.
    /// </summary>
    public async Task<StoredFile?> FetchAndStoreSpeakerPictureAsync(
        int eventId, int participantId, string? pictureUrl, CancellationToken ct = default)
    {
        var image = await _pictureFetcher.FetchAsync(pictureUrl, ct);
        if (image is null) return null;

        var relativePath = $"Pictures/speaker-{participantId}";
        // Keep the source extension via content type; default png.
        var ext = image.ContentType.Contains("png", StringComparison.OrdinalIgnoreCase) ? ".png"
                : image.ContentType.Contains("gif", StringComparison.OrdinalIgnoreCase) ? ".gif"
                : ".jpg";
        relativePath += ext;

        if (!_store.CanStore)
        {
            // No live store wired (◻): we fetched bytes but cannot persist them —
            // report the intended path; nothing is faked.
            return new StoredFile(relativePath, string.Empty, null);
        }

        return await _store.StoreAsync(relativePath, image.Content, image.ContentType, ct);
    }

    // ===================================================================
    //  Generate graphics (composite + store + upsert row, gated)
    // ===================================================================

    /// <summary>
    /// Generate (or regenerate) a SPEAKER graphic: composite the template + photo +
    /// name, store the PNG on SharePoint at the stable per-speaker path, and upsert
    /// the <see cref="GraphicAsset"/> row. The graphic is created
    /// <see cref="GraphicAssetStatus.Generated"/> (NOT released — hidden from the
    /// speaker until an organizer releases it). An existing OVERRULED row is left
    /// untouched unless <paramref name="force"/> is set, so a human replacement is
    /// not clobbered by a re-run.
    /// </summary>
    public async Task<GraphicAsset> GenerateSpeakerGraphicAsync(
        int eventId, int participantId, byte[] templatePng, byte[]? photoPng, string speakerName,
        bool force = false, CancellationToken ct = default)
    {
        var key = GraphicStableKey.ForSpeaker(participantId);
        var existing = await FindByKeyAsync(eventId, key, ct);
        if (existing is { IsOrganizerOverridden: true } && !force) return existing;

        var png = _compositor.ComposeSpeakerGraphic(templatePng, photoPng, speakerName);
        return await StoreAndUpsertAsync(
            eventId, key, GraphicAssetType.Speaker, png, existing,
            participantId: participantId, sessionId: null, sponsorCompanyId: null,
            subfolder: "Speakers", ct);
    }

    /// <summary>
    /// Generate a SPONSOR graphic (template + logo). INTERNAL-ONLY — never shown in
    /// the sponsor view (enforced by <see cref="GetSponsorFacingAsync"/> /
    /// <see cref="GetInternalSponsorGraphicsAsync"/>). Same overrule protection.
    /// </summary>
    public async Task<GraphicAsset> GenerateSponsorGraphicAsync(
        int eventId, string sponsorCompanyId, byte[] templatePng, byte[] logoPng,
        bool force = false, CancellationToken ct = default)
    {
        var key = GraphicStableKey.ForSponsor(sponsorCompanyId);
        var existing = await FindByKeyAsync(eventId, key, ct);
        if (existing is { IsOrganizerOverridden: true } && !force) return existing;

        var png = _compositor.ComposeSponsorGraphic(templatePng, logoPng);
        return await StoreAndUpsertAsync(
            eventId, key, GraphicAssetType.Sponsor, png, existing,
            participantId: null, sessionId: null, sponsorCompanyId: sponsorCompanyId,
            subfolder: "Sponsors", ct);
    }

    /// <summary>
    /// Generate a per-SESSION graphic for one speaker on a session (template +
    /// photo + name + session title). Created <see cref="GraphicAssetStatus.Generated"/>
    /// (gated like speaker graphics). Same overrule protection.
    /// </summary>
    public async Task<GraphicAsset> GenerateSessionGraphicAsync(
        int eventId, int sessionId, int participantId, byte[] templatePng, byte[]? photoPng,
        string speakerName, string sessionTitle, bool force = false, CancellationToken ct = default)
    {
        var key = GraphicStableKey.ForSession(sessionId, participantId);
        var existing = await FindByKeyAsync(eventId, key, ct);
        if (existing is { IsOrganizerOverridden: true } && !force) return existing;

        var png = _compositor.ComposeSessionGraphic(templatePng, photoPng, speakerName, sessionTitle);
        return await StoreAndUpsertAsync(
            eventId, key, GraphicAssetType.Session, png, existing,
            participantId: participantId, sessionId: sessionId, sponsorCompanyId: null,
            subfolder: "Sessions", ct);
    }

    // ===================================================================
    //  Pull session graphics FROM SharePoint (operator pre-uploaded)
    // ===================================================================

    /// <summary>The outcome of a <see cref="PullSessionGraphicsAsync"/> run.</summary>
    /// <param name="Matched">Active sessions whose title matched an uploaded file (one graphic each).</param>
    /// <param name="Unmatched">Active sessions (with a configured folder) that found NO matching file.</param>
    /// <param name="TracksMatched">DISTINCT tracks whose name matched a file in the track-graphics
    /// folder (REQUIREMENTS §158) — one shared graphic per track, regardless of how many sessions /
    /// speakers reference it. Zero when the track folder is unset (inert).</param>
    /// <param name="Retired">§326af: assets removed because their SharePoint file is gone.</param>
    public sealed record PullSessionGraphicsResult(
        int Matched, int Unmatched, int TracksMatched = 0, int Retired = 0);

    /// <summary>
    /// PULL operator-uploaded session graphics from SharePoint and surface them
    /// through the existing organizer-review → speaker-download flow (REQUIREMENTS §18).
    /// The operator has already uploaded one graphic per session into the configured
    /// MasterClass / Sessions folders; this matches each ACTIVE session (with at least
    /// one speaker) to the file whose NAME (sans extension), SLUGIFIED, equals the
    /// SESSION TITLE slug (case-insensitive). A <see cref="SessionType.MasterClass"/>
    /// session is matched against <see cref="GraphicsSharePointOptions.MasterClassFolderPath"/>;
    /// every other session against <see cref="GraphicsSharePointOptions.SessionsFolderPath"/>.
    /// For a match it UPSERTS (by stable key) one <see cref="GraphicAsset"/> per speaker
    /// on the session — Type=Session, Status=Generated (the review gate) — pointing at
    /// the SharePoint file (no compositor involved).
    ///
    /// REQUIREMENTS §158 — the THIRD pull source: each active session that ALSO has a
    /// <see cref="CommunityHub.Core.Domain.Session.Track"/> is matched, IN ADDITION, against
    /// <see cref="GraphicsSharePointOptions.TrackGraphicsFolderPath"/> by its TRACK-name slug
    /// (one shared file per track) and gets a Type=Track graphic per speaker — distinguishable
    /// from the session graphic and released through the SAME gate. The track folder is empty ⇒
    /// the track pull stays inert.
    ///
    /// Idempotent: a re-pull updates the rows in place. INERT: a no-op returning zero when the
    /// store cannot read or no folder is configured; a (session) folder with no match leaves the
    /// session counted unmatched.
    /// </summary>
    public async Task<PullSessionGraphicsResult> PullSessionGraphicsAsync(
        int eventId, CancellationToken ct = default)
    {
        // INERT until configured — never throws, never fakes a call.
        if (!_store.CanRead)
        {
            return new PullSessionGraphicsResult(0, 0);
        }

        var mcFolder = _spOptions.MasterClassFolderPath;
        var sessFolder = _spOptions.SessionsFolderPath;
        var trackFolder = _spOptions.TrackGraphicsFolderPath;
        if (string.IsNullOrWhiteSpace(mcFolder)
            && string.IsNullOrWhiteSpace(sessFolder)
            && string.IsNullOrWhiteSpace(trackFolder))
        {
            return new PullSessionGraphicsResult(0, 0); // no folder configured ⇒ inert
        }

        // Active (non-service) sessions in this edition that have at least one speaker.
        var sessions = await _db.Sessions
            .Where(s => s.EventId == eventId
                        && !s.IsServiceSession
                        && s.SessionSpeakers.Any())
            .Select(s => new
            {
                s.Id,
                s.Title,
                s.Type,
                s.Track,
                SpeakerIds = s.SessionSpeakers.Select(ss => ss.ParticipantId).ToList(),
            })
            .ToListAsync(ct);

        // §326af: every drive-item id seen LIVE in the folders listed below — the input to
        // the stale-asset heal at the end. Declared before ListBySlugAsync, which fills it.
        var liveItemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Cache each folder's listing (slug→file) so a folder is listed at most ONCE.
        var folderCache = new Dictionary<string, IReadOnlyDictionary<string, SharePointFileRef>>(
            StringComparer.OrdinalIgnoreCase);

        async Task<IReadOnlyDictionary<string, SharePointFileRef>?> ListBySlugAsync(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return null;
            if (folderCache.TryGetValue(folder, out var cached)) return cached;

            var files = await _store.ListAsync(folder, ct);
            var bySlug = new Dictionary<string, SharePointFileRef>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
            {
                liveItemIds.Add(f.ItemId);                                       // §326af
                var nameSlug = Slug(StripExtension(f.Name));
                if (!string.IsNullOrEmpty(nameSlug)) bySlug.TryAdd(nameSlug, f); // first wins
            }
            folderCache[folder] = bySlug;
            return bySlug;
        }

        var matched = 0;
        var unmatched = 0;
        // DISTINCT track slugs that matched a file — so two sessions sharing a track count once.
        var matchedTrackSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var s in sessions)
        {
            // ----- title-matched SESSION graphic (MasterClass vs Sessions folder) -----
            var folder = s.Type == SessionType.MasterClass ? mcFolder : sessFolder;
            if (!string.IsNullOrWhiteSpace(folder)) // folder configured for this type
            {
                var listing = await ListBySlugAsync(folder);
                var titleSlug = Slug(s.Title);
                if (listing is not null
                    && !string.IsNullOrEmpty(titleSlug)
                    && listing.TryGetValue(titleSlug, out var file))
                {
                    matched++;
                    var sharePointPath = $"{folder.Trim('/')}/{file.Name}";
                    foreach (var participantId in s.SpeakerIds)
                    {
                        await UpsertPulledSessionGraphicAsync(eventId, s.Id, participantId, file, sharePointPath, ct);
                    }
                }
                else
                {
                    unmatched++;
                }
            }

            // ----- track-matched TRACK graphic (§158, third pull source) -----
            // ADDITIONAL to the session graphic; matched by the session's Track name, not its title.
            if (!string.IsNullOrWhiteSpace(trackFolder) && !string.IsNullOrWhiteSpace(s.Track))
            {
                var trackListing = await ListBySlugAsync(trackFolder);
                var trackSlug = Slug(s.Track!);
                if (trackListing is not null
                    && !string.IsNullOrEmpty(trackSlug)
                    && trackListing.TryGetValue(trackSlug, out var trackFile))
                {
                    matchedTrackSlugs.Add(trackSlug);
                    var trackPath = $"{trackFolder.Trim('/')}/{trackFile.Name}";
                    foreach (var participantId in s.SpeakerIds)
                    {
                        await UpsertPulledTrackGraphicAsync(
                            eventId, s.Id, participantId, trackSlug, trackFile, trackPath, ct);
                    }
                }
            }
        }

        // §326af (operator 2026-07-25: "we need it to automatically detect that when the
        // SharePoint file is gone from the folder"): RETIRE assets whose backing file has
        // been deleted. The pull used to be add-only, so a graphic deleted in SharePoint
        // stayed "released" forever and speakers kept seeing a dead card.
        //
        // SCOPE GUARD: only assets that live in a folder we actually LISTED this pass are
        // considered — a folder that isn't configured (or wasn't read) can never retire
        // anything. An EMPTY listing is treated as a real emptiness here (unlike the §301b
        // Zoho heal): emptying the folder is exactly how the operator un-publishes, and the
        // damage from a transient empty read self-corrects — the next pull re-creates and
        // re-releases the asset from the file that is still there.
        var retired = 0;
        var listedFolders = folderCache.Keys
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim('/'))
            .ToList();
        if (listedFolders.Count > 0)
        {
            var candidates = await _db.GraphicAssets
                .Where(g => g.EventId == eventId
                            && (g.Type == GraphicAssetType.Session || g.Type == GraphicAssetType.Track)
                            && g.StorageItemId != null && g.StorageItemId != ""
                            && g.SharePointPath != null)
                .ToListAsync(ct);

            var gone = candidates
                .Where(g => listedFolders.Any(f =>
                    g.SharePointPath!.TrimStart('/').StartsWith(f + "/", StringComparison.OrdinalIgnoreCase)))
                .Where(g => !liveItemIds.Contains(g.StorageItemId!))
                .ToList();

            if (gone.Count > 0)
            {
                // The file is gone ⇒ so is the graphic. Removing the row (rather than
                // flipping a status) is what keeps the speaker page, the organizer board
                // and the share buttons consistent — and the pull re-creates the asset if
                // the operator puts a file back.
                _db.GraphicAssets.RemoveRange(gone);
                await _db.SaveChangesAsync(ct);
                retired = gone.Count;   // reported by the caller (job log + audit)
            }
        }

        return new PullSessionGraphicsResult(matched, unmatched, matchedTrackSlugs.Count, retired);
    }

    // ===================================================================
    //  Release gate (step 3) — organizer releases to the speaker
    // ===================================================================

    /// <summary>
    /// RELEASE a graphic to the speaker (organizer action). Flips
    /// <see cref="GraphicAssetStatus.Generated"/> → <see cref="GraphicAssetStatus.Released"/>
    /// and stamps who/when. Until this happens the speaker cannot see the graphic.
    /// </summary>
    public async Task<GraphicAsset> ReleaseAsync(
        int eventId, int graphicAssetId, string organizerEmail, CancellationToken ct = default)
    {
        var asset = await _db.GraphicAssets
            .FirstOrDefaultAsync(g => g.EventId == eventId && g.Id == graphicAssetId, ct)
            ?? throw new InvalidOperationException($"No graphic asset {graphicAssetId} in event {eventId}.");

        asset.Status = GraphicAssetStatus.Released;
        asset.ReleasedAt = DateTimeOffset.UtcNow;
        asset.ReleasedByEmail = organizerEmail;
        asset.UpdatedAt = asset.ReleasedAt;
        await _db.SaveChangesAsync(ct);
        return asset;
    }

    /// <summary>Un-release (pull back) a graphic — back to Generated/hidden.</summary>
    public async Task<GraphicAsset> UnreleaseAsync(
        int eventId, int graphicAssetId, CancellationToken ct = default)
    {
        var asset = await _db.GraphicAssets
            .FirstOrDefaultAsync(g => g.EventId == eventId && g.Id == graphicAssetId, ct)
            ?? throw new InvalidOperationException($"No graphic asset {graphicAssetId} in event {eventId}.");

        asset.Status = GraphicAssetStatus.Generated;
        asset.ReleasedAt = null;
        asset.ReleasedByEmail = null;
        asset.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return asset;
    }

    /// <summary>
    /// What a bulk release actually did. §436: the CALLER has to be able to notify the
    /// speakers whose graphics just became visible, and re-deriving "who was released"
    /// afterwards is impossible — every row now reads Released, including the ones that
    /// already did. So the release reports it, at the one place that knows.
    /// </summary>
    /// <param name="Count">How many assets flipped Generated → Released.</param>
    /// <param name="SpeakerIds">The DISTINCT participants those assets belong to (a
    /// sponsor/unassigned asset contributes nobody).</param>
    public sealed record ReleasedGraphics(int Count, IReadOnlyList<int> SpeakerIds)
    {
        public static readonly ReleasedGraphics None = new(0, Array.Empty<int>());
    }

    /// <summary>
    /// BULK-release every generated, non-sponsor (speaker/session) graphic for an edition that is
    /// still <see cref="GraphicAssetStatus.Generated"/>. Used by the SharePoint sync (the operator
    /// already curated by placing the finished file in the folder, so a pulled graphic is released
    /// straight to the speaker) and by the organizer "Release all" action. Sponsor graphics stay
    /// internal-only and are never touched.
    /// </summary>
    public async Task<ReleasedGraphics> ReleaseAllGeneratedAsync(
        int eventId, string releasedByEmail, CancellationToken ct = default)
    {
        var pending = await _db.GraphicAssets
            .Where(g => g.EventId == eventId
                        && g.Status == GraphicAssetStatus.Generated
                        && g.Type != GraphicAssetType.Sponsor)
            .ToListAsync(ct);
        if (pending.Count == 0) return ReleasedGraphics.None;

        var now = DateTimeOffset.UtcNow;
        foreach (var asset in pending)
        {
            asset.Status = GraphicAssetStatus.Released;
            asset.ReleasedAt = now;
            asset.ReleasedByEmail = releasedByEmail;
            asset.UpdatedAt = now;
        }
        await _db.SaveChangesAsync(ct);
        return new ReleasedGraphics(
            pending.Count,
            pending.Where(a => a.ParticipantId is not null)
                .Select(a => a.ParticipantId!.Value)
                .Distinct()
                .ToList());
    }

    // ===================================================================
    //  Overrule (step 3) — organizer replaces the bytes, keep the link
    // ===================================================================

    /// <summary>
    /// OVERRULE a graphic with an organizer-supplied PNG. Replaces the bytes on
    /// SharePoint at the SAME stable path (so the hub→SharePoint link/URL stays
    /// identical and never breaks), marks the row
    /// <see cref="GraphicAsset.IsOrganizerOverridden"/> so a future regenerate does
    /// not clobber it. The stable key / path / file name are unchanged.
    /// </summary>
    public async Task<GraphicAsset> OverruleAsync(
        int eventId, int graphicAssetId, byte[] replacementPng, CancellationToken ct = default)
    {
        var asset = await _db.GraphicAssets
            .FirstOrDefaultAsync(g => g.EventId == eventId && g.Id == graphicAssetId, ct)
            ?? throw new InvalidOperationException($"No graphic asset {graphicAssetId} in event {eventId}.");

        var keyBefore = asset.StableKey;
        var pathBefore = asset.SharePointPath;

        if (_store.CanStore && asset.SharePointPath is not null)
        {
            // Strip the configured root prefix is unnecessary — StoreAsync takes the
            // relative path; we stored under "<subfolder>/<file>", which is what the
            // path's tail is. Recompute the same relative path from the stable key.
            var relative = RelativePathFor(asset);
            var stored = await _store.StoreAsync(relative, replacementPng, GraphicCompositor.PngContentType, ct);
            asset.SharePointPath = stored.Path;
            asset.SharePointUrl = stored.WebUrl;
            asset.StorageItemId = stored.ItemId;
        }

        // THE CONTRACT: the stable key never changes on an overrule.
        asset.StableKey = keyBefore;
        if (!_store.CanStore) asset.SharePointPath = pathBefore; // unchanged when no live store
        asset.IsOrganizerOverridden = true;
        asset.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return asset;
    }

    // ===================================================================
    //  Visibility queries (steps 3,4,5)
    // ===================================================================

    /// <summary>
    /// The graphics a SPEAKER may see: only their OWN, only RELEASED, and NEVER
    /// sponsor graphics. Both speaker + per-session graphics are included.
    /// </summary>
    public async Task<IReadOnlyList<GraphicAsset>> GetSpeakerVisibleAsync(
        int eventId, int participantId, CancellationToken ct = default) =>
        await _db.GraphicAssets
            .Where(g => g.EventId == eventId
                        && g.ParticipantId == participantId
                        && g.Status == GraphicAssetStatus.Released
                        && g.Type != GraphicAssetType.Sponsor)
            .OrderBy(g => g.Type).ThenBy(g => g.Id)
            .ToListAsync(ct);

    /// <summary>
    /// The organizer review queue: graphics generated but NOT yet released (the
    /// gate). Optionally filtered by type.
    /// </summary>
    public async Task<IReadOnlyList<GraphicAsset>> GetReviewQueueAsync(
        int eventId, GraphicAssetType? type = null, CancellationToken ct = default)
    {
        var q = _db.GraphicAssets
            .Where(g => g.EventId == eventId && g.Status == GraphicAssetStatus.Generated);
        if (type is not null) q = q.Where(g => g.Type == type);
        return await q.OrderBy(g => g.Type).ThenBy(g => g.Id).ToListAsync(ct);
    }

    /// <summary>
    /// INTERNAL-ONLY sponsor graphics (organizers' SoMe posts). For the organizer
    /// surface only — NEVER call this from a sponsor-facing page.
    /// </summary>
    public async Task<IReadOnlyList<GraphicAsset>> GetInternalSponsorGraphicsAsync(
        int eventId, CancellationToken ct = default) =>
        await _db.GraphicAssets
            .Where(g => g.EventId == eventId && g.Type == GraphicAssetType.Sponsor)
            .OrderBy(g => g.SponsorCompanyId).ThenBy(g => g.Id)
            .ToListAsync(ct);

    /// <summary>
    /// What a SPONSOR may see for their company — DELIBERATELY EMPTY of sponsor
    /// graphics (those are internal-only, step 4). Returns an empty list always;
    /// exists as the single, named place the sponsor surface asks, so the
    /// internal-only invariant is impossible to violate by accident.
    /// </summary>
    public Task<IReadOnlyList<GraphicAsset>> GetSponsorFacingAsync(
        int eventId, string sponsorCompanyId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<GraphicAsset>>(Array.Empty<GraphicAsset>());

    // ===================================================================
    //  Share drafts (step 5) — LinkedIn/X DRAFT, never auto-post
    // ===================================================================

    /// <summary>Whether per-user OAuth posting is wired (false by default → download/draft only).</summary>
    public bool CanPostToSocial => _share.CanPost;

    /// <summary>One speaker graphic's bytes, ready to stream as a download.</summary>
    public sealed record SpeakerGraphicFile(byte[] Content, string ContentType, string FileName);

    /// <summary>
    /// SERVER-PROXIED download of ONE of the signed-in speaker's OWN released graphics (§160).
    /// Verifies ownership (their participant id), the release gate (Released), and that it is not a
    /// sponsor graphic, then streams the bytes from SharePoint via the app's creds — so the speaker
    /// gets the file WITHOUT any SharePoint permission (the raw SharePoint URL 'Access Denied'd
    /// them). Returns null when the asset isn't theirs / not released / not stored / the store
    /// can't read.
    /// </summary>
    public async Task<SpeakerGraphicFile?> GetSpeakerGraphicFileAsync(
        int eventId, int participantId, int graphicAssetId, CancellationToken ct = default)
    {
        var asset = await _db.GraphicAssets.FirstOrDefaultAsync(g =>
            g.EventId == eventId
            && g.Id == graphicAssetId
            && g.ParticipantId == participantId
            && g.Status == GraphicAssetStatus.Released
            && g.Type != GraphicAssetType.Sponsor, ct);
        if (asset?.StorageItemId is null || !_store.CanRead) return null;

        byte[]? bytes;
        try { bytes = await _store.DownloadAsync(asset.StorageItemId, ct); }
        catch { return null; }
        if (bytes is null || bytes.Length == 0) return null;

        var name = string.IsNullOrWhiteSpace(asset.FileName) ? $"graphic-{asset.Id}.png" : asset.FileName!;
        var ext = System.IO.Path.GetExtension(name).ToLowerInvariant();
        var ctype = ext switch
        {
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            _ => "application/octet-stream",
        };
        return new SpeakerGraphicFile(bytes, ctype, name);
    }

    // ===================================================================
    //  Public OG image for the public session-detail page (§172)
    // ===================================================================

    /// <summary>
    /// Resolve the released, STORED SoMe session-graphic to use as the PUBLIC OpenGraph
    /// image for a session's public detail page (§172), scoped to the ACTIVE edition (the
    /// same gate the public <c>/Sessions/{id}</c> page uses). Picks ONE graphic
    /// DETERMINISTICALLY (lowest <see cref="GraphicAsset.Id"/>) among the session's
    /// RELEASED, <see cref="GraphicAssetType.Session"/> graphics that have stored bytes.
    /// Returns null when there is no active event, the session isn't in it / is a service
    /// session, or no released, stored session graphic exists. NEVER returns a draft /
    /// unreleased or a sponsor graphic — released promo graphics are public by design, the
    /// rest are not.
    /// </summary>
    private async Task<GraphicAsset?> FindPublicSessionOgGraphicAsync(int sessionId, CancellationToken ct)
    {
        var activeId = await _db.Events
            .Where(e => e.IsActive)
            .OrderByDescending(e => e.Id)
            .Select(e => (int?)e.Id)
            .FirstOrDefaultAsync(ct);
        if (activeId is null) return null;

        // Same public gate as the session-detail page: the id must be in the active
        // edition and NOT a service session (breaks/lunch are never publicly addressable).
        var inEdition = await _db.Sessions.AnyAsync(
            s => s.Id == sessionId && s.EventId == activeId.Value && !s.IsServiceSession, ct);
        if (!inEdition) return null;

        return await _db.GraphicAssets
            .Where(g => g.EventId == activeId.Value
                        && g.SessionId == sessionId
                        && g.Type == GraphicAssetType.Session     // never a sponsor / track graphic
                        && g.Status == GraphicAssetStatus.Released // THE GATE — drafts ⇒ 404
                        && g.StorageItemId != null)                // only what we can actually stream
            .OrderBy(g => g.Id)                                    // deterministic pick (lowest id)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// True iff a PUBLIC OG session-graphic exists for the session (so the public detail
    /// page emits its <c>og:image</c> only when the image can actually be served). §172.
    /// </summary>
    public async Task<bool> HasPublicSessionOgGraphicAsync(int sessionId, CancellationToken ct = default) =>
        await FindPublicSessionOgGraphicAsync(sessionId, ct) is not null;

    /// <summary>
    /// Stream the PUBLIC OG session-graphic bytes for the no-auth social-card endpoint
    /// (§172): the released, stored session graphic (lowest id) for a session in the active
    /// edition, fetched from SharePoint with the app's creds so social crawlers (which have
    /// no SharePoint permission and can't use the auth proxy) can render the shared link's
    /// preview card. A draft/unreleased, sponsor, non-graphic or unknown session ⇒ null
    /// (404). Returns null when none / not stored / the store can't read.
    /// </summary>
    public async Task<SpeakerGraphicFile?> GetPublicSessionOgGraphicAsync(int sessionId, CancellationToken ct = default)
    {
        var asset = await FindPublicSessionOgGraphicAsync(sessionId, ct);
        if (asset?.StorageItemId is null || !_store.CanRead) return null;

        byte[]? bytes;
        try { bytes = await _store.DownloadAsync(asset.StorageItemId, ct); }
        catch { return null; }
        if (bytes is null || bytes.Length == 0) return null;

        var name = string.IsNullOrWhiteSpace(asset.FileName) ? $"session-{sessionId}.png" : asset.FileName!;
        var ext = System.IO.Path.GetExtension(name).ToLowerInvariant();
        // ONLY serve a known image type — never a non-graphic file (defence in depth, even
        // though a Session GraphicAsset is always an image).
        var ctype = ext switch
        {
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            _ => null,
        };
        if (ctype is null) return null;
        return new SpeakerGraphicFile(bytes, ctype, name);
    }

    /// <summary>
    /// Build the "I'm speaking at ELDK27" LinkedIn DRAFT for a speaker. The text
    /// carries the event date(s), the ticket URL <c>eldk27.expertslive.dk</c> and
    /// the speaker's session info. ALWAYS a draft the speaker finalizes + posts —
    /// NEVER an auto-post.
    /// </summary>
    public SocialShareDraft BuildSpeakingAnnouncementDraft(
        string eventDisplayName, string eventDates, string ticketUrl,
        string speakerName, string? sessionTitle, string? graphicUrl,
        SocialNetwork network = SocialNetwork.LinkedIn)
    {
        var sessionLine = string.IsNullOrWhiteSpace(sessionTitle)
            ? string.Empty
            : $" with my session \"{sessionTitle.Trim()}\"";

        // Operator-authored format (2026-06-28): a multi-line post with the date, an agenda
        // line and a ticket line (both the public event site), then the hashtags.
        var text =
            $"I'm speaking at {eventDisplayName}{sessionLine} on {eventDates}!\n\n"
            + $"Check out the agenda on {ticketUrl}\n\n"
            + $"Get your ticket: {ticketUrl}\n\n"
            + "#ELDK27 #ExpertsLiveDK";

        return _share.BuildDraft(network, text, graphicUrl);
    }

    /// <summary>
    /// Build a generic per-session share draft (download/share a pre-staged session
    /// graphic). Draft only — never an auto-post.
    /// </summary>
    public SocialShareDraft BuildSessionShareDraft(
        string eventDisplayName, string ticketUrl, string sessionTitle,
        string? graphicUrl, SocialNetwork network = SocialNetwork.LinkedIn)
    {
        var text =
            $"Catch my session \"{sessionTitle.Trim()}\" at {eventDisplayName}. "
            + $"Tickets: {ticketUrl} #ELDK27 #ExpertsLive";
        return _share.BuildDraft(network, text, graphicUrl);
    }

    /// <summary>
    /// Build a PER-SESSION promote DRAFT (§172) — the speaker shares THIS session/master-class
    /// in the same voice as <see cref="BuildSpeakingAnnouncementDraft"/>, but the post text
    /// carries the PUBLIC session URL (<paramref name="sessionUrl"/>, e.g.
    /// <c>{baseUrl}/Sessions/{id}</c>). The URL is put IN THE TEXT (not BuildDraft's image
    /// slot) on purpose: LinkedIn/X text-intents can't attach an image, so the graphic gets
    /// INTO the post via the OpenGraph card LinkedIn/X render when they crawl that URL — the
    /// session-detail page serves the matching <c>og:image</c>. Draft only — never an auto-post.
    ///
    /// §196: the session URL is the ONLY link in the post. The earlier "Get your ticket: …" line
    /// was dropped because LinkedIn/X card just ONE URL — with two links the platform picked the
    /// generic ticket URL (and its OG card), so the session graphic never showed. One link ⇒ the
    /// platform cards the session page and renders ITS og:image (the session graphic).
    /// </summary>
    public SocialShareDraft BuildSessionPromoteDraft(
        string eventDisplayName, string eventDates,
        string sessionTitle, string sessionUrl, SocialNetwork network = SocialNetwork.LinkedIn,
        // §326z: the ticket/agenda site the prefill's CTA points at (the session URL stays
        // the card source). Defaulted so existing callers/tests keep compiling.
        string ticketUrl = "eldk27.expertslive.dk")
    {
        // §281 (operator 2026-07-10): the promotion post uses this exact format/layout, and the
        // link IN THE TEXT is the AGENDA & TICKETS site (https://eldk27.expertslive.dk).
        // §318c (operator bug 2026-07-24): the SESSION URL rides as the intent's CARD URL —
        // the composer opens with the session page's OpenGraph card (= the session graphic),
        // which §281's agenda-link text had lost (§196 documented that trap).
        var text =
            $"I'm speaking at {eventDisplayName} with my session: \"{sessionTitle.Trim()}\"\n\n"
            + $"📅 {eventDates}\n"
            + "📍 Bella Center, Copenhagen\n\n"
            + "Two days of deep technical sessions and real-life experience:\n"
            + "• Ask your questions directly to the experts\n"
            + "• Meet the Microsoft product teams\n"
            + "• Network with peers & friends\n"
            + "• Fun and lots of laughing\n\n"
            + "👉 Agenda & tickets: https://eldk27.expertslive.dk\n\n"
            + "Hope to see you there! :-)\n\n"
            + "#ELDK27 #ExpertsLiveDK";
        // §326y (operator's PLAN B, 2026-07-25: "share the image as url … but then we need
        // to also upload the text"): the composer is prefilled with a COMPACT post that
        // CONTAINS the public session URL — LinkedIn keeps the text AND renders that URL's
        // OpenGraph card, which is this session's graphic. Both appear, so the speaker just
        // reviews and clicks Post. The prefill MUST stay under LinkedIn's ~300-char trim
        // (the §322q full-text prefill was cut mid-bullet and lost the URL, hence no card),
        // so the FULL §281 text below rides the clipboard for anyone who wants it verbatim.
        // A NATIVE image upload would need the member token (§326w — ruled out).
        return _share.BuildDraft(
            network, text, graphicUrl: null, cardUrl: sessionUrl,
            intentText: PrefillText(eventDisplayName, eventDates, sessionTitle, sessionUrl, ticketUrl));
    }

    /// <summary>
    /// Build a PER-TRACK promote DRAFT (§172) for a track graphic card. A track graphic isn't
    /// one session, so the link points at the PUBLIC sessions list filtered to that track
    /// (<paramref name="trackUrl"/>, e.g. <c>{baseUrl}/Sessions?FilterTrack={track}</c>).
    /// Same voice / draft-only contract as <see cref="BuildSessionPromoteDraft"/>.
    /// </summary>
    public SocialShareDraft BuildTrackPromoteDraft(
        string eventDisplayName, string eventDates, string ticketUrl,
        string track, string trackUrl, SocialNetwork network = SocialNetwork.LinkedIn)
    {
        var text =
            $"I'm speaking in the {track.Trim()} track at {eventDisplayName} on {eventDates}!\n\n"
            + $"See the sessions: {trackUrl}\n\n"
            + $"Get your ticket: {ticketUrl}\n\n"
            + "#ELDK27 #ExpertsLiveDK";
        // §326y: same as the session draft — ampersand-safe prefill carrying the URL early,
        // so the composer shows the text AND the card; clipboard keeps the verbatim text.
        return _share.BuildDraft(
            network, text, graphicUrl: null, cardUrl: trackUrl,
            intentText: PrefillText(eventDisplayName, eventDates, $"the {track.Trim()} track", trackUrl, ticketUrl));
    }

    /// <summary>
    /// §326y (operator's PLAN B, 2026-07-25) — the text LinkedIn's composer is PREFILLED
    /// with. Same voice + content as the verbatim §281 body (which stays on the clipboard),
    /// with two deliberate differences, both forced by how LinkedIn handles a share link:
    /// <list type="number">
    /// <item><b>No literal <c>&amp;</c>.</b> The live composer cut the post at
    /// "…Network with peers" — exactly at the <c>&amp;</c> of "peers &amp; friends" — and
    /// lost everything after it INCLUDING the URL (hence no card). LinkedIn re-parses the
    /// decoded text as a query string, so an ampersand terminates it. "and" is used
    /// instead; the clipboard copy keeps the operator's exact "&amp;".</item>
    /// <item><b>The link sits EARLY</b> (right after the date/venue block, ~140 chars in)
    /// and is the ONLY URL: LinkedIn cards the first URL it finds, and this one's
    /// OpenGraph image IS the session graphic — so the picture shows. Placing it early
    /// also means it survives even if LinkedIn additionally trims long prefills.</item>
    /// </list>
    /// Result: the speaker clicks once and reviews a post that already carries the text
    /// and the picture — no manual attach, no app consent (§326w).
    /// </summary>
    private static string PrefillText(
        string eventDisplayName, string eventDates, string what, string url, string ticketUrl) =>
        $"I'm speaking at {eventDisplayName} with my session: \"{what.Trim()}\"\n\n"
        + $"📅 {eventDates}\n"
        + "📍 Bella Center, Copenhagen\n\n"
        + $"👉 Agenda and tickets: {AbsoluteTicketUrl(ticketUrl)}\n\n"
        + "Two days of deep technical sessions and real-life experience:\n"
        + "• Ask your questions directly to the experts\n"
        + "• Meet the Microsoft product teams\n"
        + "• Network with peers and friends\n"
        + "• Fun and lots of laughing\n\n"
        + "Hope to see you there! :-)\n\n"
        // §326ac (operator 2026-07-25): the raw company-page URL is GONE. A share link can
        // only carry PLAIN TEXT, and a LinkedIn @-mention is a structured annotation
        // (urn:li:organization:…) that only the API — or the author picking the page from
        // the composer's dropdown — can create; a prefilled "@ExpertsLiveDK" would be dead
        // characters. So the page is not tagged automatically; the page-tag hint lives
        // under the Share button, and the #ExpertsLiveDK hashtag still links its feed.
        + "#ELDK27 #ExpertsLiveDK\n"
        // §326ab: NO "My session" prefix — the session URL is the LAST line, bare. It must
        // stay: LinkedIn reads the preview picture (the OG image) from THIS url.
        + url;

    /// <summary>§326z: the community's LinkedIn company page, tagged in the closing line
    /// (operator 2026-07-25). A plain-text intent cannot carry a real @-mention (that needs
    /// the API), so the page URL is appended — LinkedIn renders it as a link to the page.</summary>
    private const string CompanyPageUrl = "https://www.linkedin.com/company/expertslivedk";

    /// <summary>The ticket/agenda site as a clickable absolute URL (config often stores the
    /// bare host, e.g. "eldk27.expertslive.dk").</summary>
    private static string AbsoluteTicketUrl(string ticketUrl)
    {
        var t = (ticketUrl ?? string.Empty).Trim();
        if (t.Length == 0) return "https://eldk27.expertslive.dk";
        return t.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? t : $"https://{t}";
    }

    // ----- internals -------------------------------------------------------

    private Task<GraphicAsset?> FindByKeyAsync(int eventId, string key, CancellationToken ct) =>
        _db.GraphicAssets.FirstOrDefaultAsync(g => g.EventId == eventId && g.StableKey == key, ct);

    /// <summary>
    /// Upsert (by stable key) the <see cref="GraphicAsset"/> for a PULLED session
    /// graphic — same upsert-by-key shape as <see cref="StoreAndUpsertAsync"/>, but the
    /// SharePoint location comes from the operator-uploaded file (no compositor, no
    /// store write). A NEW row is created <see cref="GraphicAssetStatus.Generated"/>
    /// (the review gate); a re-pull refreshes the file pointer in place and PRESERVES
    /// the release status. An organizer OVERRULE is never clobbered.
    /// </summary>
    private async Task<GraphicAsset> UpsertPulledSessionGraphicAsync(
        int eventId, int sessionId, int participantId,
        SharePointFileRef file, string sharePointPath, CancellationToken ct)
    {
        var key = GraphicStableKey.ForSession(sessionId, participantId);
        var existing = await FindByKeyAsync(eventId, key, ct);

        // Respect a human replacement — a pull must not overwrite an organizer overrule.
        if (existing is { IsOrganizerOverridden: true }) return existing;

        var now = DateTimeOffset.UtcNow;
        if (existing is null)
        {
            var asset = new GraphicAsset
            {
                EventId = eventId,
                Type = GraphicAssetType.Session,
                StableKey = key,
                ParticipantId = participantId,
                SessionId = sessionId,
                Status = GraphicAssetStatus.Generated, // THE GATE — never auto-released
                SharePointPath = sharePointPath,
                SharePointUrl = string.IsNullOrEmpty(file.WebUrl) ? null : file.WebUrl,
                StorageItemId = file.ItemId,
                FileName = file.Name,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.GraphicAssets.Add(asset);
            await _db.SaveChangesAsync(ct);
            return asset;
        }

        // Idempotent re-pull — refresh the file pointer, keep the stable key + status.
        existing.SharePointPath = sharePointPath;
        existing.SharePointUrl = string.IsNullOrEmpty(file.WebUrl) ? existing.SharePointUrl : file.WebUrl;
        existing.StorageItemId = file.ItemId;
        existing.FileName = file.Name;
        existing.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        return existing;
    }

    /// <summary>
    /// Upsert (by stable key) the <see cref="GraphicAsset"/> for a PULLED TRACK graphic
    /// (REQUIREMENTS §158). Same shape as <see cref="UpsertPulledSessionGraphicAsync"/> but
    /// keyed by (track, speaker) via <see cref="GraphicStableKey.ForTrack"/> and tagged
    /// <see cref="GraphicAssetType.Track"/> so the speaker page can LABEL it as the track
    /// graphic and it never collides with the session graphic. <paramref name="sessionId"/>
    /// is recorded as a REPRESENTATIVE session (so the page can resolve the track name); the
    /// stable key keeps a speaker on two same-track sessions to ONE row. Created
    /// <see cref="GraphicAssetStatus.Generated"/> (the gate); a re-pull refreshes the file
    /// pointer in place and PRESERVES the release status. An organizer OVERRULE is never clobbered.
    /// </summary>
    private async Task<GraphicAsset> UpsertPulledTrackGraphicAsync(
        int eventId, int sessionId, int participantId,
        string trackSlug, SharePointFileRef file, string sharePointPath, CancellationToken ct)
    {
        var key = GraphicStableKey.ForTrack(trackSlug, participantId);
        var existing = await FindByKeyAsync(eventId, key, ct);

        // Respect a human replacement — a pull must not overwrite an organizer overrule.
        if (existing is { IsOrganizerOverridden: true }) return existing;

        var now = DateTimeOffset.UtcNow;
        if (existing is null)
        {
            var asset = new GraphicAsset
            {
                EventId = eventId,
                Type = GraphicAssetType.Track,
                StableKey = key,
                ParticipantId = participantId,
                SessionId = sessionId,                  // representative session (resolves the track name)
                Status = GraphicAssetStatus.Generated,  // THE GATE — never auto-released
                SharePointPath = sharePointPath,
                SharePointUrl = string.IsNullOrEmpty(file.WebUrl) ? null : file.WebUrl,
                StorageItemId = file.ItemId,
                FileName = file.Name,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.GraphicAssets.Add(asset);
            await _db.SaveChangesAsync(ct);
            return asset;
        }

        // Idempotent re-pull — refresh the file pointer, keep the stable key + status.
        existing.SessionId = sessionId;
        existing.SharePointPath = sharePointPath;
        existing.SharePointUrl = string.IsNullOrEmpty(file.WebUrl) ? existing.SharePointUrl : file.WebUrl;
        existing.StorageItemId = file.ItemId;
        existing.FileName = file.Name;
        existing.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        return existing;
    }

    /// <summary>Drop a file extension (the last <c>.ext</c>) before slugifying the name.</summary>
    private static string StripExtension(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return string.Empty;
        var dot = fileName.LastIndexOf('.');
        return dot > 0 ? fileName[..dot] : fileName;
    }

    /// <summary>
    /// Title / file-name slug — lower-cased, letters+digits kept, every run of anything
    /// else (spaces, dashes, punctuation) collapsed to a SINGLE dash, with leading /
    /// trailing dashes trimmed. The SAME function slugs BOTH the session title and the
    /// uploaded file name, so an operator can name the file with spaces
    /// (<c>Cloud Native Talk.png</c>) OR dashes (<c>cloud-native-talk.png</c>) and either
    /// matches the title "Cloud Native Talk". Follows the established private <c>Slug</c>
    /// convention elsewhere in Core (SpeakerDeadlineSeeder, etc.), hardened for matching.
    /// </summary>
    private static string Slug(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var sb = new System.Text.StringBuilder(text.Length);
        var prevDash = false;
        foreach (var ch in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                prevDash = false;
            }
            else if (!prevDash && sb.Length > 0)
            {
                sb.Append('-');
                prevDash = true;
            }
        }
        var slug = sb.ToString().TrimEnd('-');
        return slug.Length > 80 ? slug[..80] : slug;
    }

    /// <summary>The relative store path for an asset (subfolder/file), derived from its key/type.</summary>
    private static string RelativePathFor(GraphicAsset asset)
    {
        var subfolder = asset.Type switch
        {
            GraphicAssetType.Speaker => "Speakers",
            GraphicAssetType.Sponsor => "Sponsors",
            GraphicAssetType.Session => "Sessions",
            GraphicAssetType.Track => "Tracks",
            _ => "Other",
        };
        var file = asset.FileName ?? GraphicStableKey.FileName(asset.StableKey);
        return $"{subfolder}/{file}";
    }

    /// <summary>
    /// Store the PNG (when a live store is wired) and create/update the asset row by
    /// stable key. The row is always created <see cref="GraphicAssetStatus.Generated"/>
    /// for a NEW asset (the gate). A regenerate of an existing, non-overruled asset
    /// refreshes the bytes/URL but PRESERVES its release status (a released graphic
    /// stays released after a benign re-render).
    /// </summary>
    private async Task<GraphicAsset> StoreAndUpsertAsync(
        int eventId, string key, GraphicAssetType type, byte[] png, GraphicAsset? existing,
        int? participantId, int? sessionId, string? sponsorCompanyId, string subfolder,
        CancellationToken ct)
    {
        var fileName = GraphicStableKey.FileName(key);
        var relativePath = $"{subfolder}/{fileName}";

        string? path = relativePath;
        string? url = null;
        string? itemId = null;

        if (_store.CanStore)
        {
            var stored = await _store.StoreAsync(relativePath, png, GraphicCompositor.PngContentType, ct);
            path = stored.Path;
            url = stored.WebUrl;
            itemId = stored.ItemId;
        }

        var now = DateTimeOffset.UtcNow;
        if (existing is null)
        {
            var asset = new GraphicAsset
            {
                EventId = eventId,
                Type = type,
                StableKey = key,
                ParticipantId = participantId,
                SessionId = sessionId,
                SponsorCompanyId = sponsorCompanyId,
                Status = GraphicAssetStatus.Generated,   // THE GATE — never auto-released
                SharePointPath = path,
                SharePointUrl = url,
                StorageItemId = itemId,
                FileName = fileName,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.GraphicAssets.Add(asset);
            await _db.SaveChangesAsync(ct);
            return asset;
        }

        // Regenerate in place — refresh bytes/URL, keep the stable key + release
        // status. (Overruled assets are short-circuited before we get here unless
        // force was set; a forced regenerate clears the overrule flag.)
        existing.SharePointPath = path;
        existing.SharePointUrl = url ?? existing.SharePointUrl;
        existing.StorageItemId = itemId ?? existing.StorageItemId;
        existing.FileName = fileName;
        existing.IsOrganizerOverridden = false;
        existing.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        return existing;
    }
}
