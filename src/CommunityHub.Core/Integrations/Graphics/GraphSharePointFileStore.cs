using Microsoft.Extensions.Options;

namespace CommunityHub.Core.Integrations.Graphics;

/// <summary>
/// Operator config for the SoMe-graphics SharePoint file store (REQUIREMENTS §18).
/// Bound from <c>Graphics:SharePoint</c>. Defaults keep the store INERT: the
/// per-edition site / drive / root folder are operator-entered (placeholder in
/// committed config), so <see cref="IsConfigured"/> is false and the live store is
/// not selected. SPN credentials are NOT here — they live in
/// <see cref="SharePointUploadOptions"/> (deployment-scoped, Key Vault).
/// </summary>
public sealed class GraphicsSharePointOptions
{
    public const string SectionName = "Graphics:SharePoint";

    /// <summary>Master on/off. DEFAULTS FALSE — the null store is used until an operator opts in.</summary>
    public bool Enabled { get; set; }

    /// <summary>SharePoint site URL the graphics are stored on (operator config; placeholder in public files).</summary>
    public string SiteUrl { get; set; } = string.Empty;

    /// <summary>Document library / drive name (blank = the site default drive).</summary>
    public string DriveName { get; set; } = string.Empty;

    /// <summary>Root folder under the drive for all generated graphics (e.g. <c>/Graphics</c>).</summary>
    public string RootFolderPath { get; set; } = "Graphics";

    /// <summary>
    /// §467 — the largest deck (BYTES) the embedded Office viewer will render. Above this the
    /// listing greys out "View" with a reason and leaves "Download" working, instead of sending
    /// the attendee to Microsoft's "File too large — the file specified is larger than what the
    /// Office Viewers are configured to support" page.
    ///
    /// <para>CONFIG, not a constant, on purpose: Microsoft has changed this ceiling before and it
    /// differs per viewer/tenant. Default 10 MB — deliberately CONSERVATIVE, since wrongly
    /// greying out a viewable deck merely costs a download, while wrongly offering an unviewable
    /// one reproduces the exact error this exists to prevent. Set 0 to disable the check.</para>
    /// </summary>
    public long OfficeViewerMaxBytes { get; set; } = 10L * 1024 * 1024;

    // ⚰️ §768 — MasterClassFolderPath and SessionsFolderPath REMOVED.
    //
    // They were TWO settings for what turned out to be ONE folder: master classes and technical
    // sessions share `Speakers/Graphics-SoMe/Sessions` (operator: "i dont see a need to split"), and
    // the pull now resolves that through DocLibraryPaths.SpeakerSessionGraphics.
    //
    // 🔒 Deleted rather than left as unused aliases. An unread path setting still LOOKS
    // authoritative in the Azure portal, and the next person to find one pointing at a stale folder
    // will "fix" it — changing nothing, and believing they have.

    /// <summary>
    /// Drive-relative folder the operator uploads pre-made per-TRACK promo graphics into —
    /// the THIRD pull source (REQUIREMENTS §158), e.g. <c>…/Grahics-SoMe/Tracks</c>. Holds
    /// ONE file per track, named by the track (e.g. <c>Security.png</c>); the pull matches
    /// each active session that HAS a <see cref="CommunityHub.Core.Domain.Session.Track"/>
    /// to the file whose name slug equals the TRACK-name slug (case-insensitive) and gives
    /// every speaker on that track the shared graphic, IN ADDITION to their title-matched
    /// session graphic. DISTINCT from <see cref="TracksFolderPath"/> (the §165 external-designer
    /// BUILD root) — this is a finished-graphics PULL source. EMPTY by default ⇒ the track
    /// pull is INERT (nothing listed) until an operator configures it.
    /// </summary>
    public string TrackGraphicsFolderPath { get; set; } = string.Empty;

    /// <summary>
    /// Drive-relative ROOT folder the EXTERNAL-DESIGNER pipeline (REQUIREMENTS §165) writes
    /// each speaker's PHOTO into, NAMED BY THE SPEAKER (e.g. <c>Firstname-Lastname.jpg</c>) —
    /// e.g. <c>General/Events/ELDK 2027/EventHub/Speakers/Photos</c>. The pull fetches each
    /// speaker's current photo URL (Sessionize <c>profilePicture</c> OR the speaker's own
    /// hub-edited photo, the latter WINS) and drops a name-keyed copy here so a designer has
    /// a tidy "photos by name" folder. EMPTY by default ⇒ the photo pull is INERT (nothing
    /// fetched / written) until an operator configures it.
    /// </summary>
    public string SpeakerPhotosFolderPath { get; set; } = string.Empty;

    /// <summary>
    /// §767 — drive-relative folder holding the SoMe TEMPLATE: the event photograph the graphics are
    /// composed on, and (optionally) the white event logo dropped beside it.
    /// </summary>
    /// <remarks>
    /// 🔒 Operator 2026-08-01: <i>"just put it in template folder"</i>. The background and the mark
    /// are ASSETS HE OWNS — replacing either is a file drop here, never a deploy. The loader takes
    /// the first image it finds as the template (the real file is <c>Template.JPG</c>, so it must
    /// accept .jpg as well as .png) and any file whose name contains <c>logo</c> as the mark.
    /// EMPTY ⇒ the §767 build sweep is INERT and says so in the log.
    /// </remarks>
    public string TemplateFolderPath { get; set; } = string.Empty;

    /// <summary>
    /// §767 — drive-relative folder holding every sponsor logo in one place, the collection folder
    /// <c>SponsorUploadWatchService</c> fills (files named <c>"{Company} - {file}"</c>).
    /// </summary>
    /// <remarks>
    /// Used by the sponsor CATEGORY bundles to find each sponsor's mark by company-name prefix.
    /// EMPTY ⇒ the sponsor bundles are inert (and logged as such); the track bundles still run.
    /// </remarks>
    public string SponsorLogosFolderPath { get; set; } = string.Empty;

    // ⚰️ §768 — TracksFolderPath REMOVED with the §165 external-designer pipeline it belonged to.
    // Nothing reads it: the designer build folders are gone, and the §158 TRACK PULL uses the
    // separate TrackGraphicsFolderPath above (still present, still unconfigured by design).

    /// <summary>
    /// Drive-relative folder holding the per-ROOM session-evaluation QR codes
    /// (REQUIREMENTS §124) — e.g.
    /// <c>General/Events/ELDK 2027/EventHub/Speakers/SessionEvals-QR</c>. Files are
    /// named with the room in the name (<c>Room-16-Floor-1-Device13.png</c>); a speaker
    /// downloads the QR for their session's ROOM, and an org-admin uploads / replaces
    /// the files here. EMPTY by default ⇒ the feature is INERT (nothing listed,
    /// nothing to download / upload) until an operator configures it.
    /// </summary>
    public string SessionEvalsQrFolderPath { get; set; } = string.Empty;

    /// <summary>
    /// Drive-relative ROOT folder holding the per-edition VENUE images
    /// (REQUIREMENTS §146) — e.g.
    /// <c>General/Events/ELDK 2027/EventHub/Venue</c>. The server-proxied
    /// <see cref="VenueImageService"/> maps an allowlisted folder key
    /// (<c>wayfinding</c>/<c>good-to-know</c>/<c>evaluations</c>/<c>expo</c>) to a
    /// SUBFOLDER under this root and streams the bytes through CEH using the app's
    /// own SharePoint credentials (end users never get a SharePoint link). EMPTY by
    /// default ⇒ the live venue proxy is INERT (nothing listed / downloaded) and the
    /// committed wwwroot fallback images are used until an operator configures it.
    /// </summary>
    public string VenueRootFolderPath { get; set; } = string.Empty;

    /// <summary>
    /// Drive-relative folder of operator-dropped AI-helper GROUNDING documents
    /// (md / txt / docx / pdf / xlsx — REQUIREMENTS §152) — e.g.
    /// <c>General/Events/ELDK 2027/EventHub/ExtraAIGroundingInfo</c>. The
    /// <see cref="CommunityHub.Core.Assistant.SharePointGroundingProvider"/> reads this
    /// folder through the app's own creds, extracts each file's text and grounds the AI
    /// Community Helper for ALL roles; edits propagate within the provider's cache TTL
    /// (~15 min) with no deploy. EMPTY by default ⇒ the §152 grounding source is INERT
    /// (nothing listed / read) until an operator configures it.
    /// </summary>
    public string GroundingFolderPath { get; set; } = string.Empty;

    /// <summary>
    /// Drive-relative folder holding the speaker PRESENTATION TEMPLATE (§153) — e.g.
    /// <c>General/Events/ELDK 2027/EventHub/Speakers/SpeakerTemplate</c> (§326ae — moved
    /// under /EventHub so every hub-served document sits in one tree). The server-proxied
    /// <see cref="SpeakerTemplateService"/> streams the single template file (e.g.
    /// <c>ELDK27_PPT_Template.potx</c>) through CEH with the app's own credentials, so the
    /// speaker gets a DIRECT download instead of a link to the SharePoint site. EMPTY by
    /// default ⇒ the proxy is INERT and the page falls back to the configured template URL.
    /// </summary>
    public string SpeakerTemplateFolderPath { get; set; } = string.Empty;

    /// <summary>
    /// §326f-c: drive-relative folder holding the operator's LOGO-PACK zip (e.g.
    /// <c>General/Events/ELDK 2027/EventHub/ELDK</c>). The server-proxied
    /// <see cref="LogoPackService"/> streams the first zip there through CEH with the app
    /// registration's credentials (consistent with every other SharePoint read — no share
    /// link, no sharing-scope surprises). EMPTY by default ⇒ /logo-pack/download 404s.
    /// </summary>
    public string LogoPackFolderPath { get; set; } = string.Empty;

    /// <summary>
    /// Drive-relative folder the operator drops the FINAL per-session EVALUATION PDFs into
    /// (REQUIREMENTS §166) — e.g.
    /// <c>General/Events/ELDK 2027/EventHub/Speakers/SessionEvals-PDF</c>. The
    /// <see cref="SessionEvalPdfService"/> writes one deterministic file per session
    /// (<c>session-{id}.pdf</c>) here on an organizer upload and streams it back to the
    /// session's speakers through a HUB PROXY (<c>/session-eval/{id}/download</c>) using the
    /// app's own credentials — speakers never get a SharePoint link. EMPTY by default ⇒ the
    /// feature is INERT (nothing uploaded / streamed; the page shows a "not configured" note)
    /// until an operator configures it.
    /// </summary>
    public string SessionEvalPdfFolderPath { get; set; } = string.Empty;

    /// <summary>
    /// §322: drive-relative folder the speaker PREVIEW presentations land in — e.g.
    /// <c>General/Events/ELDK 2027/EventHub/Speakers/Presentations/Preview</c>. Speakers
    /// upload IN THE HUB (/Speaker/Presentations) and <see cref="SpeakerPresentationService"/>
    /// writes the file here under the APP REGISTRATION's credentials — speakers have no
    /// SharePoint access and must never get a SharePoint link (the §160 rule). EMPTY by
    /// default ⇒ the upload is INERT (the page shows a "not configured" note).
    /// </summary>
    public string PresentationPreviewFolderPath { get; set; } = string.Empty;

    /// <summary>§322: drive-relative folder for the speaker FINAL presentations — e.g.
    /// <c>…/Speakers/Presentations/Final</c>. Same contract as
    /// <see cref="PresentationPreviewFolderPath"/>.</summary>
    public string PresentationFinalFolderPath { get; set; } = string.Empty;

    /// <summary>True when enabled AND a site URL is present (the live store can run).</summary>
    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(SiteUrl);
}

/// <summary>
/// Live Microsoft Graph-backed file store. Delegates to the existing
/// <see cref="SharePointUploadClient"/> (Graph auth + folder ensure + byte
/// upload/delete). Selected over the <see cref="NullSharePointFileStore"/> only
/// when <see cref="GraphicsSharePointOptions.IsConfigured"/> AND the upload client
/// has SPN credentials — otherwise the null store is registered (see Program.cs).
/// </summary>
public sealed class GraphSharePointFileStore : ISharePointFileStore
{
    private readonly SharePointUploadClient _client;
    private readonly GraphicsSharePointOptions _options;

    // §340-H: the environment-level external-write switch. Optional so existing
    // constructions/tests are unchanged (null ⇒ allow); DI supplies the real guard.
    private readonly IExternalWriteGuard _writes;

    public GraphSharePointFileStore(
        SharePointUploadClient client, IOptions<GraphicsSharePointOptions> options,
        IExternalWriteGuard? writes = null)
    {
        _client = client;
        _options = options.Value;
        _writes = writes ?? new AllowAllExternalWrites();
    }

    /// <summary>
    /// §340-H — refuse an upload/delete when this host may not write to third-party systems.
    ///
    /// <para>THROWS rather than no-opping: <see cref="StoreAsync"/> and
    /// <see cref="UploadToFolderAsync"/> must return a <see cref="StoredFile"/> carrying a
    /// real path and itemId, and inventing one would make callers persist a
    /// <c>StorageItemId</c> pointing at a file that was never written — the §326k
    /// "missing picture" failure, but silent. Deletes throw for symmetry: a delete that
    /// quietly did nothing leaves a file the caller believes is gone.</para>
    ///
    /// <para>READS (<c>ListAsync</c> / <c>DownloadAsync</c>) are deliberately NOT gated.</para>
    ///
    /// <para>🔒 <b>§768 — THE DOCUMENT LIBRARY IS AN EXPLICIT EXCEPTION TO §612.</b> Operator
    /// 2026-08-02: <i>"612. allow sharepoint write. exception to external writes rule … as we now
    /// have dev env in sharepoint"</i>.</para>
    ///
    /// <para>Why the exception is safe, and why it was NOT safe before. §612's rule exists for a
    /// concrete reason he gave: a DEV run writing to a third-party record system creates
    /// <i>"2 set of everything in zoho"</i> and can e-mail real people. That danger is about
    /// systems of RECORD shared with production. The document library is not one — it is the
    /// event's own file store, and <b>DEV now writes into its own isolated root</b>
    /// (<c>General/DEVELOPMENT/EventHub</c>). A DEV file lands in a DEV folder and reaches nobody.
    /// Before that root existed, the only thing standing between a DEV run and the live artwork was
    /// this guard — which is exactly why it stayed shut until today.</para>
    ///
    /// <para>⚠️ <b>The isolation is now carried by the ROOT, not by this switch.</b> Point a DEV
    /// host's <c>DocLibrary:RootFolderPath</c> at the live tree and it will happily overwrite real
    /// artwork. That single setting is the safety boundary — treat it as one.</para>
    ///
    /// <para>Everything else the guard covers — the external event system, the ERP, mail — remains
    /// blocked in DEV exactly as §612 requires. This exempts the file store and nothing besides.</para>
    /// </summary>
    private Task EnsureMayWriteAsync(string operation, CancellationToken ct)
    {
        _ = operation;
        _ = ct;
        return Task.CompletedTask;
    }

    public bool CanStore => _options.IsConfigured && _client.IsConfigured;

    public async Task<StoredFile> StoreAsync(
        string relativePath, byte[] content, string contentType, CancellationToken ct = default)
    {
        await EnsureMayWriteAsync(nameof(StoreAsync), ct);
        var (path, webUrl, itemId) = await _client.UploadFileAsync(
            _options.SiteUrl, _options.DriveName, _options.RootFolderPath,
            relativePath, content, contentType, ct);
        return new StoredFile(path, webUrl, itemId);
    }

    public async Task DeleteAsync(string relativePath, CancellationToken ct = default)
    {
        await EnsureMayWriteAsync(nameof(DeleteAsync), ct);
        await _client.DeleteFileAsync(
            _options.SiteUrl, _options.DriveName, _options.RootFolderPath, relativePath, ct);
    }

    public bool CanRead => _options.IsConfigured && _client.IsConfigured;

    public async Task<IReadOnlyList<SharePointFileRef>> ListAsync(
        string relativeFolder, CancellationToken ct = default)
    {
        if (!CanRead || string.IsNullOrWhiteSpace(relativeFolder))
        {
            return Array.Empty<SharePointFileRef>();
        }

        // The configured folder paths are drive-relative (NOT under RootFolderPath),
        // so they are passed straight to the client's folder listing. A missing folder
        // yields an empty list (the client tolerates 404) — the pull stays inert.
        var files = await _client.ListFolderFilesAsync(
            _options.SiteUrl, _options.DriveName, relativeFolder, ct);

        return files
            // §769.4 — the modified stamp travels with the file now: the organizer's session view
            // needs WHEN a deck arrived, not only that one exists.
            .Select(f => new SharePointFileRef(
                f.ItemId, f.Name, f.WebUrl ?? string.Empty, f.SizeBytes, f.LastModifiedUtc))
            .ToList();
    }

    public Task<byte[]?> DownloadAsync(string itemId, CancellationToken ct = default) =>
        !CanRead || string.IsNullOrWhiteSpace(itemId)
            ? Task.FromResult<byte[]?>(null)
            : _client.DownloadItemContentAsync(_options.SiteUrl, _options.DriveName, itemId, ct);

    public async Task<StoredFile> UploadToFolderAsync(
        string relativeFolder, string fileName, byte[] content, string contentType,
        CancellationToken ct = default)
    {
        await EnsureMayWriteAsync(nameof(UploadToFolderAsync), ct);
        // The folder is DRIVE-RELATIVE (like ListAsync), so pass an empty root and let
        // the relative path carry "<folder>/<file>" — the bytes land in the folder the
        // operator configured, not under the graphics RootFolderPath.
        var relative = $"{relativeFolder.Trim('/')}/{fileName}";
        var (path, webUrl, itemId) = await _client.UploadFileAsync(
            _options.SiteUrl, _options.DriveName, rootFolderPath: string.Empty,
            relative, content, contentType, ct);
        return new StoredFile(path, webUrl, itemId);
    }

    /// <summary>
    /// §455 — the REAL streamed upload: the file flows browser → Graph a chunk at a time, so peak
    /// memory is ONE chunk instead of the whole deck held twice. Same drive-relative folder
    /// contract as <see cref="UploadToFolderAsync"/>; this override is what makes the interface's
    /// buffering default irrelevant wherever a live store is wired.
    /// </summary>
    public async Task<StoredFile> UploadStreamToFolderAsync(
        string relativeFolder, string fileName, System.IO.Stream content, long contentLength,
        string contentType, CancellationToken ct = default)
    {
        await EnsureMayWriteAsync(nameof(UploadStreamToFolderAsync), ct);
        var relative = $"{relativeFolder.Trim('/')}/{fileName}";
        var (path, webUrl, itemId) = await _client.UploadFileStreamAsync(
            _options.SiteUrl, _options.DriveName, rootFolderPath: string.Empty,
            relative, content, contentLength, contentType, ct);
        return new StoredFile(path, webUrl, itemId);
    }

    public async Task DeleteFromFolderAsync(
        string relativeFolder, string fileName, CancellationToken ct = default)
    {
        await EnsureMayWriteAsync(nameof(DeleteFromFolderAsync), ct);
        await _client.DeleteFileAsync(
            _options.SiteUrl, _options.DriveName, rootFolderPath: string.Empty,
            $"{relativeFolder.Trim('/')}/{fileName}", ct);
    }
}
