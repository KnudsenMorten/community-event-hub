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
    public sealed record PullSessionGraphicsResult(int Matched, int Unmatched);

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
    /// the SharePoint file (no compositor involved). Idempotent: a re-pull updates the
    /// rows in place. INERT: a no-op returning zero when the store cannot read or no
    /// folder is configured; a folder with no match leaves the session counted unmatched.
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
        if (string.IsNullOrWhiteSpace(mcFolder) && string.IsNullOrWhiteSpace(sessFolder))
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
                SpeakerIds = s.SessionSpeakers.Select(ss => ss.ParticipantId).ToList(),
            })
            .ToListAsync(ct);

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
                var nameSlug = Slug(StripExtension(f.Name));
                if (!string.IsNullOrEmpty(nameSlug)) bySlug.TryAdd(nameSlug, f); // first wins
            }
            folderCache[folder] = bySlug;
            return bySlug;
        }

        var matched = 0;
        var unmatched = 0;

        foreach (var s in sessions)
        {
            var folder = s.Type == SessionType.MasterClass ? mcFolder : sessFolder;
            if (string.IsNullOrWhiteSpace(folder)) continue; // folder not configured for this type — skip

            var listing = await ListBySlugAsync(folder);
            if (listing is null) continue;

            var titleSlug = Slug(s.Title);
            if (string.IsNullOrEmpty(titleSlug) || !listing.TryGetValue(titleSlug, out var file))
            {
                unmatched++;
                continue;
            }

            matched++;
            var sharePointPath = $"{folder.Trim('/')}/{file.Name}";
            foreach (var participantId in s.SpeakerIds)
            {
                await UpsertPulledSessionGraphicAsync(eventId, s.Id, participantId, file, sharePointPath, ct);
            }
        }

        return new PullSessionGraphicsResult(matched, unmatched);
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
