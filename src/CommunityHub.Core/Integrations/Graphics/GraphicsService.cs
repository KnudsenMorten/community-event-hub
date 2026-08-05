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
    private readonly DocLibrary.IDocLibraryPathResolver _paths;

    public GraphicsService(
        CommunityHubDbContext db,
        GraphicCompositor compositor,
        ISharePointFileStore store,
        ISpeakerPictureFetcher pictureFetcher,
        ISocialShareGateway share,
        IOptions<GraphicsSharePointOptions> spOptions,
        DocLibrary.IDocLibraryPathResolver paths)
    {
        _db = db;
        _compositor = compositor;
        _store = store;
        _pictureFetcher = pictureFetcher;
        _share = share;
        _spOptions = spOptions.Value;
        _paths = paths;
    }

    // ⚰️ §768 — the four pre-§767 compositor generators were DELETED here.
    //   FetchAndStoreSpeakerPictureAsync · GenerateSpeakerGraphicAsync ·
    //   GenerateSponsorGraphicAsync · GenerateSessionGraphicAsync
    //
    // Verified before removal: ZERO production callers (tests only), and ZERO rows of the two
    // asset types they wrote — GraphicAssetType.Speaker and .Track have no rows in production.
    // They were the old GraphicCompositor path, superseded by the renderer-based generators
    // below (session bundle / track bundle / sponsor single / sponsor category).
    //
    // 🔑 They could not simply be repointed: they wrote to Pictures/, Speakers/ and a
    // per-speaker Sessions/ shape the canonical registry gives no home to. Keeping them alive
    // would have meant registering folders nothing should ever write to — inventing paths to
    // keep dead code compiling.

    // ===================================================================
    //  §767 — GIF BUNDLES (tracks, sponsor groupings)
    // ===================================================================

    /// <summary>
    /// §767 — build the ONE GIF bundle for a track: a frame per speaker, in the locked design.
    /// </summary>
    /// <remarks>
    /// Organizer-facing promotion material for the event's own channels, so it is stored and gated
    /// like everything else but is NOT per-speaker and is never released to one.
    /// An overruled bundle is left alone unless <paramref name="force"/> — a human's replacement wins.
    /// </remarks>
    public async Task<BundleOutcome> GenerateTrackBundleAsync(
        int eventId, string trackSlug, string trackName, byte[] template,
        IReadOnlyList<(byte[]? Photo, string Name)> speakers, byte[]? eventLogo = null,
        string? inputHash = null, bool force = false, CancellationToken ct = default)
    {
        if (speakers.Count == 0)
            throw new ArgumentException("A track graphic needs at least one speaker.", nameof(speakers));

        var key = GraphicStableKey.ForTrackGraphic(trackSlug);
        var existing = await FindByKeyAsync(eventId, key, ct);
        if (existing is { IsOrganizerOverridden: true } && !force)
            return new BundleOutcome(existing, Rendered: false);

        // §767 — nothing composed into this track changed, so do not re-render it. A NULL stored
        // hash means "unknown inputs" (a pulled or pre-§767 row), which is not the same as stale.
        if (!force && inputHash is not null && existing?.InputHash == inputHash)
            return new BundleOutcome(existing, Rendered: false);

        var renderer = SoMeGraphicRenderer.LockedDesign();
        renderer.EventLogo = eventLogo;
        renderer.Subtitle = trackName;
        ApplyEventStrip(renderer);
        var gif = renderer.RenderSpeakerGif(template, speakers);

        var asset = await StoreAndUpsertAsync(
            eventId, key, GraphicAssetType.TrackBundle, gif, existing,
            participantId: null, sessionId: null, sponsorCompanyId: null,
            ct, isGif: true, inputHash: inputHash);
        return new BundleOutcome(asset, Rendered: true);
    }

    /// <summary>
    /// §767 — the bottom strip: <c>9-10 FEB 2027 · COPENHAGEN, DENMARK</c>, and nothing else.
    /// </summary>
    /// <remarks>
    /// 🔒 <b>Dates and location ONLY — the event NAME is deliberately absent.</b> Operator
    /// 2026-08-01: <i>"i am worried of too much text makes it cramped and noicy"</i>. He was right,
    /// and it was a collision rather than a preference: name + dates + location ran under the SPEAKER
    /// plate. The wordmark top-left already says the name, so the name is what got subtracted.
    ///
    /// <para>⚠️ <b>Why this exists as a call rather than a renderer default:</b> the strip was set on
    /// the EXAMPLE renders the operator approved, and on nothing else. Every track GIF the live sweep
    /// wrote on 2026-08-01 therefore shipped with a BLANK strip — no dates, no location — while the
    /// examples he signed off showed both. Producing artwork that differs from the approved sample is
    /// the failure this prevents; the two paths must set the same values, so they set them here.</para>
    /// </remarks>
    private static void ApplyEventStrip(SoMeGraphicRenderer renderer)
    {
        var brand = BrandingEventContext.Default;
        renderer.EventDates = brand.EventDates;
        renderer.EventLocation = brand.EventLocation;
    }

    /// <summary>
    /// §767 — build a sponsor GROUPING bundle: one frame per sponsor in a tier or a type.
    /// </summary>
    /// <param name="kind">
    /// <c>tier</c> or <c>type</c> — part of the stable key, because the two groupings share a key
    /// space and a name collision would otherwise silently overwrite one bundle with the other.
    /// </param>
    public async Task<BundleOutcome> GenerateSponsorCategoryBundleAsync(
        int eventId, string kind, string slug, string caption, byte[] template,
        IReadOnlyList<byte[]> logos, byte[]? eventLogo = null,
        string? inputHash = null, bool force = false, CancellationToken ct = default)
    {
        if (logos.Count == 0)
            throw new ArgumentException("A sponsor grouping needs at least one logo.", nameof(logos));

        var key = GraphicStableKey.ForSponsorCategory(kind, slug);
        var existing = await FindByKeyAsync(eventId, key, ct);
        if (existing is { IsOrganizerOverridden: true } && !force)
            return new BundleOutcome(existing, Rendered: false);

        // §767 — same members and the same logo VERSIONS, so there is nothing to re-render. A new
        // upload writes _v{N+1}, which changes the hash and brings the graphic with it.
        if (!force && inputHash is not null && existing?.InputHash == inputHash)
            return new BundleOutcome(existing, Rendered: false);

        var renderer = SoMeGraphicRenderer.LockedDesign();
        renderer.EventLogo = eventLogo;
        ApplyEventStrip(renderer);
        var gif = renderer.RenderSponsorGif(template, logos, caption);

        var asset = await StoreAndUpsertAsync(
            eventId, key, GraphicAssetType.SponsorCategory, gif, existing,
            participantId: null, sessionId: null, sponsorCompanyId: null,
            ct, isGif: true, inputHash: inputHash);
        return new BundleOutcome(asset, Rendered: true);
    }

    // ===================================================================
    //  §767 PHASE 2 — the per-SESSION graphic and the per-SPONSOR graphic
    // ===================================================================

    /// <summary>
    /// §767 phase 2 — build the ONE graphic for a session: <b>PNG for a single speaker, GIF (a frame
    /// each) for two or more</b>, keyed <c>session:{id}</c> with no speaker in the key.
    /// </summary>
    /// <remarks>
    /// <para>🔒 <b>Single vs multi is not a second graphic.</b> Operator 2026-08-01, Round 11:
    /// <i>"sessions are 2 types. single vs multi"</i> — but both are the same subject rendered two
    /// ways, and the EXTENSION carries which. So a second speaker joining a session UPDATES that
    /// session's graphic (png → gif) instead of creating a rival one.</para>
    ///
    /// <para>🔒 <b>A human's uploaded file wins, and it is a DIFFERENT ROW.</b> The SharePoint pull
    /// writes per-speaker <c>session:{id}:speaker:{pid}</c> rows for artwork the operator uploaded
    /// himself. This key cannot collide with those, so generating never overwrites an upload — the
    /// caller decides whether to generate at all (see
    /// <see cref="SoMeBundleBuildService"/>, which skips a session that already has one).</para>
    ///
    /// <para>⚠️ <b>ParticipantId stays NULL.</b> The row belongs to the session, not to a speaker —
    /// which is exactly why <see cref="GetSpeakerVisibleAsync"/> had to stop being a
    /// <c>ParticipantId</c> lookup and resolve session → speakers.</para>
    /// </remarks>
    public async Task<BundleOutcome> GenerateSessionBundleAsync(
        int eventId, int sessionId, string sessionTitle, byte[] template,
        IReadOnlyList<(byte[]? Photo, string Name)> speakers, byte[]? eventLogo = null,
        string? inputHash = null, bool force = false, CancellationToken ct = default)
    {
        if (speakers.Count == 0)
            throw new ArgumentException("A session graphic needs at least one speaker.", nameof(speakers));

        var key = GraphicStableKey.ForSessionGraphic(sessionId);
        var existing = await FindByKeyAsync(eventId, key, ct);
        if (existing is { IsOrganizerOverridden: true } && !force)
            return new BundleOutcome(existing, Rendered: false);

        // Nothing composed into this session changed. The speaker SET is in the hash, so an add or a
        // remove — including the one that flips PNG→GIF — rebuilds by construction.
        if (!force && inputHash is not null && existing?.InputHash == inputHash)
            return new BundleOutcome(existing, Rendered: false);

        var renderer = SoMeGraphicRenderer.LockedDesign();
        renderer.EventLogo = eventLogo;
        renderer.Subtitle = sessionTitle;
        ApplyEventStrip(renderer);

        var multi = speakers.Count > 1;
        var bytes = multi
            ? renderer.RenderSpeakerGif(template, speakers)
            : renderer.RenderSpeakerPng(template, speakers[0].Photo, speakers[0].Name);

        var asset = await StoreAndUpsertAsync(
            eventId, key, GraphicAssetType.Session, bytes, existing,
            participantId: null, sessionId: sessionId, sponsorCompanyId: null,
            ct, isGif: multi, inputHash: inputHash);
        return new BundleOutcome(asset, Rendered: true);
    }

    /// <summary>
    /// §767 phase 2 — the ONE graphic for a single sponsor (their logo on the locked design). PNG.
    /// </summary>
    /// <remarks>
    /// Same key as the pre-§767 sponsor graphic (<c>sponsor:{companyId}</c>) ON PURPOSE: it is the
    /// same subject, so this REPLACES that graphic's bytes rather than adding a second row and a
    /// second file for the same company. INTERNAL-ONLY like every sponsor graphic — never shown in
    /// the sponsor's own view.
    /// <para>🔑 The hash carries the resolved logo FILE NAME (its <c>_v{N}</c>), so a re-upload —
    /// which writes a new version rather than overwriting — rebuilds on the next sweep.</para>
    /// </remarks>
    public async Task<BundleOutcome> GenerateSponsorSingleAsync(
        int eventId, string sponsorCompanyId, byte[] template, byte[] logo,
        string? caption = null, byte[]? eventLogo = null,
        string? inputHash = null, bool force = false, CancellationToken ct = default)
    {
        var key = GraphicStableKey.ForSponsor(sponsorCompanyId);
        var existing = await FindByKeyAsync(eventId, key, ct);
        if (existing is { IsOrganizerOverridden: true } && !force)
            return new BundleOutcome(existing, Rendered: false);

        if (!force && inputHash is not null && existing?.InputHash == inputHash)
            return new BundleOutcome(existing, Rendered: false);

        var renderer = SoMeGraphicRenderer.LockedDesign();
        renderer.EventLogo = eventLogo;
        if (!string.IsNullOrWhiteSpace(caption)) renderer.SponsorCaption = caption;
        ApplyEventStrip(renderer);
        var png = renderer.RenderSponsorPng(template, logo);

        var asset = await StoreAndUpsertAsync(
            eventId, key, GraphicAssetType.Sponsor, png, existing,
            participantId: null, sessionId: null, sponsorCompanyId: sponsorCompanyId,
            ct, inputHash: inputHash);
        return new BundleOutcome(asset, Rendered: true);
    }

    /// <summary>
    /// §767 — what a bundle call actually DID: the row, and whether a new file was rendered.
    /// </summary>
    /// <remarks>
    /// 🔑 <b>Why the flag exists.</b> The sweep used to infer "built" from the returned row
    /// (<c>asset.InputHash == hash</c>) — which is true for an UP-TO-DATE bundle as well as a
    /// freshly rendered one, because the unchanged path returns the existing row. So the job logged
    /// <i>"7 track GIF(s)"</i> every 15 minutes for ever, on runs that wrote nothing (verified in
    /// PROD 2026-08-01 19:40: log said 7, SharePoint said 0 of 7 files touched, and the run took 4s
    /// against 18s for the real rebuild). A number that never changes cannot report anything, and it
    /// would have hidden the opposite fault — a genuine rebuild that failed to happen — just as well.
    /// Only the render path can honestly claim a build, so only it sets <c>Rendered</c>.
    /// </remarks>
    public sealed record BundleOutcome(GraphicAsset Asset, bool Rendered);

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

        // 🔒 §768 — ONE FOLDER for master classes AND technical sessions.
        //
        // Operator 2026-08-02: *"master class and technical sessions gor into same folder"*, and on
        // whether the two config keys should survive: *"i dont see a need to split"*. So the routing
        // that used to pick between two folders now resolves ONE registry key for both.
        //
        // 🔑 The per-type branch below is KEPT rather than flattened, and that is deliberate: it is
        // what makes the folder listing cache do the work. Both types resolve the same path, the
        // cache keys on the path, so the folder is enumerated EXACTLY ONCE per run — the same
        // property the two-folder version had, without a second folder to keep in step.
        var sessFolder = _paths.TryResolve(
            DocLibrary.DocLibraryPaths.SpeakerSessionGraphics, out var resolvedSessions)
            ? resolvedSessions
            : string.Empty;
        var mcFolder = sessFolder;

        // ⚰️ The §158 per-speaker TRACK pull stays on its legacy option, UNCONFIGURED everywhere.
        // §767 Round 9 ruled precisely this: *"No code removed … the pull branch stays where it is,
        // inert and unconfigured"* — because deleting it buys one string check and risks needing the
        // §158 behaviour back for an edition that does hand-made track stills. It gets no registry
        // key on purpose: a registered key is an invitation to fill it in, and the standing
        // instruction is *do not configure that folder for ELDK27*.
        var trackFolder = _spOptions.TrackGraphicsFolderPath;

        if (string.IsNullOrWhiteSpace(sessFolder) && string.IsNullOrWhiteSpace(trackFolder))
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
    /// <param name="includeEngineRendered">
    /// 🔒 <b>FALSE for the quarter-hourly sync. This is the §767 release gate.</b>
    /// <para>The sync releases in bulk because <i>placing a file in the SharePoint folder IS the
    /// curation</i> — a human chose that artwork. That reasoning covers PULLED rows and nothing
    /// else. §767 phase 2 makes the same job also RENDER session graphics, and releasing those on
    /// the same pass would put machine-made artwork on every speaker's Help Promote page fifteen
    /// minutes after a deploy, un-reviewed — the exact outcome the standing constraint
    /// <i>"never release, never send from the sweep; releasing is the organizer's click"</i>
    /// exists to prevent.</para>
    /// <para>The tell is <see cref="GraphicAsset.InputHash"/>: the engine always records what it
    /// composed from, a pulled or hand-placed file never can. Same signal the rebuild already
    /// trusts, read the other way round. The organizer's own "Release all" passes TRUE — there the
    /// click IS the curation, which is the whole distinction.</para>
    /// </param>
    public async Task<ReleasedGraphics> ReleaseAllGeneratedAsync(
        int eventId, string releasedByEmail, CancellationToken ct = default,
        bool includeEngineRendered = true)
    {
        var pending = await _db.GraphicAssets
            .Where(g => g.EventId == eventId
                        && g.Status == GraphicAssetStatus.Generated
                        && g.Type != GraphicAssetType.Sponsor
                        && (includeEngineRendered || g.InputHash == null))
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
            // §768: resolve the folder from the registry, exactly as generation does, so an overrule
            // lands on top of the generated file instead of beside it. (This is where an overruled
            // bundle used to be written into a stray "Other/" folder.)
            //
            // ⚠️ A RETIRED type (Speaker/Track) has no registered folder. Those have no rows in
            // production, but an overrule must never THROW at an organizer who is replacing a file
            // that does exist — so it falls back to where that row was originally written. Fixing a
            // row is not the moment to enforce a taxonomy.
            var folder = TryFolderFor(asset.Type) ?? LegacyFolderFor(asset.Type);
            var fileName = asset.FileName ?? GraphicStableKey.FileName(asset.StableKey);
            // The FILE decides the content type, not the caller's assumption. A GIF bundle overruled
            // as "image/png" is served as a PNG under a .gif name — the kind of mismatch that only
            // shows up in the one place it matters, on the post that carries it.
            var contentType = fileName.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)
                ? "image/gif"
                : GraphicCompositor.PngContentType;
            var stored = await _store.UploadToFolderAsync(
                folder, fileName, replacementPng, contentType, ct);
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
    /// <remarks>
    /// <para>🔒 <b>§767 phase 2 — "mine" is TWO things now, and it has to be.</b> It used to be one
    /// rule, <c>ParticipantId == me</c>. A phase-2 session graphic is keyed <c>session:{id}</c> and
    /// carries <b>no</b> <c>ParticipantId</c> — one file for the whole session, a frame per speaker —
    /// so under the old rule every speaker on it would see NOTHING while their graphic sat released
    /// on SharePoint. So a speaker sees:</para>
    /// <list type="number">
    /// <item>every released graphic carrying their own <c>ParticipantId</c> (speaker graphics, and
    ///   the per-speaker session/track rows the SharePoint pull still writes), and</item>
    /// <item>every released graphic for a SESSION THEY SPEAK ON, whoever it is keyed to.</item>
    /// </list>
    /// <para>⚠️ Rule 2 is a genuine widening: a session graphic reaches every speaker on that
    /// session, which is the point — it is their shared graphic, and it has their face on it. It is
    /// still gated on RELEASE and still never a sponsor graphic, so the trust boundary is unchanged.
    /// A speaker leaving a session stops seeing it on the next load, with nothing to clean up.</para>
    /// </remarks>
    public async Task<IReadOnlyList<GraphicAsset>> GetSpeakerVisibleAsync(
        int eventId, int participantId, CancellationToken ct = default) =>
        await _db.GraphicsVisibleToSpeaker(eventId, participantId)
            .OrderBy(g => g.Type).ThenBy(g => g.Id)
            .ToListAsync(ct);

    /// <summary>
    /// §784.12(a) — EVERY speaker-facing graphic that exists, whatever its status. This is what
    /// <c>/Organizer/Graphics</c> lists now that the review gate is retired: the page answers
    /// "what artwork exists and what is it of", not "what is waiting for my approval".
    /// </summary>
    /// <remarks>
    /// 🔒 <b>Why this replaced the review queue on that page rather than being an extra view.</b>
    /// The queue is <c>Status == Generated</c>. Auto-release makes that set permanently empty for
    /// speaker-facing artwork, so the page would have shown "Nothing waiting for review" for ever
    /// — a page that silently stops showing anything is worse than the gate it replaced.
    /// <see cref="GetReviewQueueAsync"/> stays for the sponsor-internal rows, which are still
    /// created <see cref="GraphicAssetStatus.Generated"/> and never reach a speaker.
    /// </remarks>
    public async Task<IReadOnlyList<GraphicAsset>> GetSpeakerFacingGraphicsAsync(
        int eventId, CancellationToken ct = default) =>
        await _db.GraphicAssets
            .Where(g => g.EventId == eventId && g.Type != GraphicAssetType.Sponsor)
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
        // §767 phase 2: the SAME ownership rule the page used to build the card — otherwise a
        // shared session graphic renders with a download button that 404s.
        var asset = await _db.GraphicsVisibleToSpeaker(eventId, participantId)
            .FirstOrDefaultAsync(g => g.Id == graphicAssetId, ct);
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

    /// <summary>
    /// 🔒 §784.12(a) — THE ONE PLACE that decides what status a NEWLY CREATED graphic gets.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-03: <i>"when SoMe Graphic service or Sessional Evaluation release
    /// files (graphics for SOMe promotion + QR code), then they should be published to speaker right
    /// away so they can see them"</i>. ⇒ <b>The review gate is retired for speaker-facing artwork.</b>
    /// It is created <see cref="GraphicAssetStatus.Released"/>, so <c>/Speaker/Graphics</c> shows it
    /// the moment it renders.</para>
    ///
    /// <para><b>This deliberately reverses the §603/§435-era rule, so here is WHY</b> — three sites
    /// used to say <i>"THE GATE — never auto-released"</i>, and without this note the next reader
    /// restores them. The gate existed so an organizer could vet artwork before a speaker saw it. He
    /// has decided the delay costs more than the vetting is worth: a speaker who cannot see their own
    /// promo graphic cannot promote the event. Vetting becomes <b>correct it after</b> (the organizer
    /// Replace, which keeps the link identical), not <b>approve it before</b>.</para>
    ///
    /// <para>⚠️ <b>SPONSOR graphics are NOT included and that is not an oversight.</b> They are
    /// internal-only — organizers' own SoMe material, never shown to the sponsor — so "publish it to
    /// the speaker at once" says nothing about them. They also feed
    /// <c>BrandingGraphicsProvider</c>, which gates on Released; auto-releasing them would change
    /// which artwork the branding surfaces pick up, which is a different decision nobody has made.</para>
    ///
    /// <para>🔒 Visibility still runs through <see cref="SpeakerGraphicVisibility"/> — this changes
    /// what status a row is BORN with, never who may see a released one.</para>
    /// </remarks>
    public static GraphicAssetStatus InitialStatusFor(GraphicAssetType type) =>
        type == GraphicAssetType.Sponsor
            ? GraphicAssetStatus.Generated
            : GraphicAssetStatus.Released;

    /// <summary>Who a row auto-released by <see cref="InitialStatusFor"/> is stamped as released by.</summary>
    public const string AutoReleasedBy = "system (auto-release §784.12a)";

    private Task<GraphicAsset?> FindByKeyAsync(int eventId, string key, CancellationToken ct) =>
        _db.GraphicAssets.FirstOrDefaultAsync(g => g.EventId == eventId && g.StableKey == key, ct);

    /// <summary>
    /// Upsert (by stable key) the <see cref="GraphicAsset"/> for a PULLED session
    /// graphic — same upsert-by-key shape as <see cref="StoreAndUpsertAsync"/>, but the
    /// SharePoint location comes from the operator-uploaded file (no compositor, no
    /// store write). A NEW row is created <see cref="GraphicAssetStatus.Released"/>
    /// (§784.12(a) — see <see cref="InitialStatusFor"/>); a re-pull refreshes the file pointer in
    /// place and PRESERVES the release status. An organizer OVERRULE is never clobbered.
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
                Status = InitialStatusFor(GraphicAssetType.Session),   // §784.12(a) — released on sight
                ReleasedAt = now,
                ReleasedByEmail = AutoReleasedBy,
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
    /// <see cref="GraphicAssetStatus.Released"/> (§784.12(a) — see <see cref="InitialStatusFor"/>);
    /// a re-pull refreshes the file pointer in place and PRESERVES the release status. An organizer
    /// OVERRULE is never clobbered.
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
                Status = InitialStatusFor(GraphicAssetType.Track),     // §784.12(a) — released on sight
                ReleasedAt = now,
                ReleasedByEmail = AutoReleasedBy,
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

    /// <summary>The registered DocLibrary key an asset type is stored under.</summary>
    /// <remarks>
    /// <para>⚠️ <b>Every LIVE type must be named here.</b> The §767 bundle types once fell through to
    /// a <c>_ => "Other"</c> default, so an organizer overruling a track or sponsor-category bundle
    /// wrote their replacement into <c>Other/</c> — a folder nobody looks in — and repointed the row
    /// at it, silently moving the live file out of the folder the LinkedIn helper step names. There
    /// is no catch-all now: an unmapped type throws.</para>
    ///
    /// <para>🔒 <c>Speaker</c> and <c>Track</c> are deliberately absent. Both are retired
    /// (§768; §767 Round 9), both have ZERO rows in production, and the canonical registry gives
    /// them no folder. Throwing beats inventing a path — and beats the old silent <c>Other/</c>.</para>
    /// </remarks>
    private static string PathKeyFor(GraphicAssetType type) => type switch
    {
        GraphicAssetType.Session => DocLibrary.DocLibraryPaths.SpeakerSessionGraphics,
        GraphicAssetType.TrackBundle => DocLibrary.DocLibraryPaths.SpeakerTrackGraphics,
        GraphicAssetType.Sponsor => DocLibrary.DocLibraryPaths.SponsorGraphicsSponsors,
        GraphicAssetType.SponsorCategory => DocLibrary.DocLibraryPaths.SponsorGraphicsCategories,
        _ => throw new DocLibrary.DocLibraryPathException(
            type.ToString(),
            "no document-library folder is registered for this asset type. Speaker and Track "
            + "graphics are retired (§768) and must not be written."),
    };

    /// <summary>The FULL drive-relative folder an asset lives in, resolved through the registry.</summary>
    private string FolderFor(GraphicAssetType type) => _paths.Resolve(PathKeyFor(type));

    /// <summary>The registered folder, or null for a type the registry does not cover.</summary>
    private string? TryFolderFor(GraphicAssetType type) => type switch
    {
        GraphicAssetType.Session or GraphicAssetType.TrackBundle
            or GraphicAssetType.Sponsor or GraphicAssetType.SponsorCategory => FolderFor(type),
        _ => null,
    };

    /// <summary>
    /// ⚰️ Where a RETIRED type's rows were originally written. Reachable only when overruling a
    /// legacy row — of which production has none.
    /// </summary>
    private static string LegacyFolderFor(GraphicAssetType type) => type switch
    {
        GraphicAssetType.Speaker => "Speakers",
        GraphicAssetType.Track => "Tracks",
        _ => "Other",
    };

    /// <summary>
    /// Store the PNG (when a live store is wired) and create/update the asset row by
    /// stable key. A NEW asset is created via <see cref="InitialStatusFor"/> — Released for
    /// speaker-facing artwork (§784.12(a)), Generated for the internal sponsor rows. A regenerate
    /// of an existing, non-overruled asset refreshes the bytes/URL but PRESERVES its release
    /// status (a released graphic stays released after a benign re-render).
    /// </summary>
    /// <summary>
    /// Store the bytes and upsert the row. <paramref name="isGif"/> carries the §767 PNG-vs-GIF rule
    /// through to the file name and the content type.
    /// </summary>
    /// <remarks>
    /// <para>⚠️ The extension is part of the STORED PATH, so a subject that flips PNG→GIF (a second
    /// speaker joins a session) writes a NEW file and leaves the old one behind. The upsert repoints
    /// the row, so the hub is correct either way — but the stale file is why the §326af retire sweep
    /// exists.</para>
    ///
    /// <para>🔒 <b>§768 — the folder now comes from the registry, and writes go INSIDE the
    /// configured root.</b> This used to take a bare subfolder name and store it beneath
    /// <c>RootFolderPath</c>, which defaulted to <c>Graphics</c> — the DRIVE ROOT, a sibling of the
    /// event tree. So every generated graphic lived outside the folder structure the operator
    /// actually curates. It now resolves the full drive-relative folder and uploads there, which is
    /// the same convention every READ already used.</para>
    /// </remarks>
    private async Task<GraphicAsset> StoreAndUpsertAsync(
        int eventId, string key, GraphicAssetType type, byte[] png, GraphicAsset? existing,
        int? participantId, int? sessionId, string? sponsorCompanyId,
        CancellationToken ct, bool isGif = false, string? inputHash = null)
    {
        var fileName = GraphicStableKey.FileName(key, isGif ? ".gif" : ".png");
        var folder = FolderFor(type);

        string? path = $"{folder}/{fileName}";
        string? url = null;
        string? itemId = null;

        if (_store.CanStore)
        {
            var contentType = isGif ? "image/gif" : GraphicCompositor.PngContentType;
            // UploadToFolderAsync takes a DRIVE-RELATIVE folder — the same convention ListAsync uses.
            // StoreAsync would re-prefix the legacy RootFolderPath and put the file back outside the tree.
            var stored = await _store.UploadToFolderAsync(folder, fileName, png, contentType, ct);
            path = stored.Path;
            url = stored.WebUrl;
            itemId = stored.ItemId;
        }

        return await UpsertRowAsync(
            eventId, key, type, existing, participantId, sessionId, sponsorCompanyId,
            fileName, path, url, itemId, inputHash, ct);
    }

    // ⚰️ §768 — LegacyStoreAndUpsertAsync deleted with the generators it served. It was the last
    // writer that wrote beneath the old drive-root RootFolderPath, OUTSIDE the event tree.

    /// <summary>Create or refresh the row. Shared by the live and legacy write paths.</summary>
    private async Task<GraphicAsset> UpsertRowAsync(
        int eventId, string key, GraphicAssetType type, GraphicAsset? existing,
        int? participantId, int? sessionId, string? sponsorCompanyId,
        string fileName, string? path, string? url, string? itemId, string? inputHash,
        CancellationToken ct)
    {
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
                // §784.12(a) — speaker-facing artwork is born Released; SPONSOR rows stay
                // Generated (internal-only, and they feed the branding gate).
                Status = InitialStatusFor(type),
                ReleasedAt = InitialStatusFor(type) == GraphicAssetStatus.Released ? now : null,
                ReleasedByEmail = InitialStatusFor(type) == GraphicAssetStatus.Released
                    ? AutoReleasedBy : null,
                SharePointPath = path,
                SharePointUrl = url,
                StorageItemId = itemId,
                FileName = fileName,
                InputHash = inputHash,
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
        // Only overwrite a KNOWN hash. A null here means this caller did not compute one, and it
        // must not erase what a previous run recorded — that would make the next sweep think the
        // graphic had never been hashed and re-render it for ever.
        if (inputHash is not null) existing.InputHash = inputHash;
        existing.IsOrganizerOverridden = false;
        existing.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        return existing;
    }
}
