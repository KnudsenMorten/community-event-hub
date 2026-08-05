using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CommunityHub.Core.Integrations.Graphics;

/// <summary>
/// §767 — builds the edition's promotion artwork: the TRACK and sponsor TIER/TYPE GIF bundles
/// (phase 1), and the per-SESSION and per-SPONSOR graphics (phase 2).
/// </summary>
/// <remarks>
/// <para>🔒 <b>Phase order was the operator's, not a convenience.</b> 2026-08-01: <i>"we start with
/// tracks and category. then per session and per sponsor"</i> — because the bundles are what he
/// posts first. Phase 1 shipped and was confirmed in prod on 2026-08-02 before phase 2 was
/// written.</para>
///
/// <para>The five outputs (Round 11): session single (PNG) · session multi (GIF) · sponsor single
/// (PNG) · sponsor category (GIF) · speaker track (GIF). <b>Single vs multi is not a separate
/// subject</b> — it is one session graphic rendered two ways, and the EXTENSION carries which, so a
/// second speaker joining updates the file instead of creating a rival one.</para>
///
/// <para>🔑 <b>Rebuild is keyed on a hash of the composed inputs</b>, never on "does a graphic
/// exist". Keying on existence freezes the first render for ever — the §764.1 defect — and would
/// mean a speaker joining or leaving a track (<i>"when chg happens like add or remove of speaker …
/// they must be build"</i>) never reached the artwork. The speaker set IS part of the hash, so an
/// add or a remove rebuilds by construction, with nothing to remember to trigger.</para>
///
/// <para>⚠️ The hash lives in memory only until <c>GraphicAsset.InputHash</c> and its migration land
/// (§767): today the sweep compares against the CURRENT file's own recorded state, so a run with no
/// changes still re-renders. That is deliberate and cheap — a wrong picture costs more than a
/// redundant render — and is the first thing to fix when the column arrives.</para>
///
/// <para>INERT and SAID SO: no template folder, no readable store, or no event ⇒ it logs the reason
/// and returns zero. A silent zero reads exactly like success, which is the §757/§764 trap.</para>
///
/// <para>It never releases and never sends. Releasing stays the organizer's click, and the
/// "your promo material is ready" mail stays behind its ring (deliberately at 1).</para>
/// </remarks>
public sealed class SoMeBundleBuildService
{
    private readonly CommunityHubDbContext _db;
    private readonly GraphicsService _graphics;
    private readonly ISharePointFileStore _store;
    private readonly GraphicsSharePointOptions _options;
    private readonly ILogger<SoMeBundleBuildService> _log;

    private readonly DocLibrary.IDocLibraryPathResolver _paths;

    /// <summary>§768 — the three folders this sweep READS, resolved from the registry.</summary>
    /// <remarks>
    /// ⚠️ All three were app settings that pointed at folders the reorganisation renamed or deleted,
    /// and every one of them fails the SAME quiet way: an unreadable folder lists empty, and empty
    /// reads as "nothing to build". The template one is the loudest available symptom — it at least
    /// logs "sweep inert" and names the folder — and even that took a live outage to notice.
    /// </remarks>
    private string TemplateFolder =>
        _paths.TryResolve(DocLibrary.DocLibraryPaths.EventGraphicsTemplate, out var p) ? p : string.Empty;

    private string PhotosFolder =>
        _paths.TryResolve(DocLibrary.DocLibraryPaths.SpeakerPhotos, out var p) ? p : string.Empty;

    private string SponsorLogosFolder =>
        _paths.TryResolve(DocLibrary.DocLibraryPaths.SponsorLogoWeb, out var p) ? p : string.Empty;

    public SoMeBundleBuildService(
        CommunityHubDbContext db,
        GraphicsService graphics,
        ISharePointFileStore store,
        IOptions<GraphicsSharePointOptions> options,
        DocLibrary.IDocLibraryPathResolver paths,
        ILogger<SoMeBundleBuildService> log)
    {
        _paths = paths;
        _db = db;
        _graphics = graphics;
        _store = store;
        _options = options.Value;
        _log = log;
    }

    /// <summary>What one sweep did. Zeroes are a valid, logged outcome.</summary>
    /// <param name="TrackBundles">Track GIFs built.</param>
    /// <param name="SponsorBundles">Sponsor tier/type GIFs built.</param>
    /// <param name="TracksSkippedNoPhoto">Tracks where NOT ONE speaker had a photo.</param>
    /// <param name="Inert">True when nothing could run at all (no store / no template).</param>
    /// <param name="SessionGraphics">§767 phase 2 — per-session PNG/GIFs (re)built.</param>
    /// <param name="SponsorGraphics">§767 phase 2 — per-sponsor PNGs (re)built.</param>
    /// <param name="SessionsLeftToUpload">
    /// Sessions the generator did NOT touch because the operator had already uploaded artwork for
    /// them. Counted, never silent: it is the difference between "nothing to do" and "held back".
    /// </param>
    /// <param name="SessionsSkippedNoPhoto">Sessions where NOT ONE speaker had a photo.</param>
    public sealed record BundleResult(
        int TrackBundles, int SponsorBundles, int TracksSkippedNoPhoto, bool Inert = false,
        int SessionGraphics = 0, int SponsorGraphics = 0,
        int SessionsLeftToUpload = 0, int SessionsSkippedNoPhoto = 0);

    /// <summary>Build every graphic the edition's five-output taxonomy calls for.</summary>
    /// <remarks>
    /// Phase 1 = the track GIFs and the sponsor tier/type GIFs. Phase 2 = the per-session graphic
    /// (PNG single / GIF multi) and the per-sponsor PNG. They run in ONE sweep off ONE listing of
    /// the photo folder — listing it per family would cost four Graph round-trips to answer the
    /// same question.
    /// </remarks>
    public async Task<BundleResult> BuildAsync(int eventId, CancellationToken ct = default)
    {
        if (!_store.CanRead || !_store.CanStore)
        {
            _log.LogInformation(
                "§767 bundles: the graphics store is not readable/writable — sweep inert (event {EventId}).",
                eventId);
            return new BundleResult(0, 0, 0, Inert: true);
        }

        var (template, eventLogo) = await LoadTemplateAsync(ct);
        if (template is null)
        {
            _log.LogInformation(
                "§767 bundles: no template found in '{Folder}' — sweep inert (event {EventId}). "
                + "Drop the event photograph (and the white logo) there to activate it.",
                TemplateFolder, eventId);
            return new BundleResult(0, 0, 0, Inert: true);
        }

        // ONE listing of the speaker-photo folder for every family that needs a face.
        var photos = await ListPhotosAsync(PhotosFolder, ct);

        // 🔑 An EMPTY index and a folder full of files nothing matches read identically downstream —
        // both end as "waiting for a photo". Say which one it is, in the folder's own terms.
        //
        // ⚠️ §768.16 — the "by name" count DROPS to the number of human-dropped §165 files (0 in the
        // live folder today, where it used to equal the id count). That is the change working, not a
        // regression: archive-written files are indexed by id alone now. The id count is the one to
        // watch.
        _log.LogInformation(
            "§767 bundles: speaker-photo folder '{Folder}' indexed {ById} by participant id, "
            + "{BySlug} by operator-dropped name.",
            PhotosFolder, photos.ById.Count, photos.BySlug.Count);

        var unmatched = new List<string>();

        var tracks = await BuildTrackBundlesAsync(eventId, template, eventLogo, photos, unmatched, ct);
        var sessions = await BuildSessionGraphicsAsync(eventId, template, eventLogo, photos, unmatched, ct);
        var sponsors = await BuildSponsorBundlesAsync(eventId, template, eventLogo, ct);

        if (unmatched.Count > 0)
        {
            // NAMED, not counted: "3 speakers had no photo" sent the last investigation to the wrong
            // half of the system. A name and an id can be looked up in the folder in ten seconds.
            _log.LogInformation(
                "§767 bundles: no photo matched for {Count} speaker(s): {Speakers}.",
                unmatched.Distinct().Count(), string.Join(", ", unmatched.Distinct().Take(30)));
        }

        // 🔑 REBUILT vs ALREADY CURRENT, not one merged number. The merged one said "7 track GIF(s)"
        // on every run for ever — including runs that wrote nothing — so it reported neither work
        // done nor work skipped. A steady "0 rebuilt, 7 already current" is the healthy state here,
        // and it stays legible when it changes.
        _log.LogInformation(
            "§767 bundles: {Tracks} track GIF(s) rebuilt, {TracksCurrent} already current, "
            + "{Sessions} session graphic(s) rebuilt, {SessionsCurrent} already current, "
            + "{Sponsors} sponsor grouping GIF(s) rebuilt, {SponsorSingles} sponsor graphic(s) rebuilt, "
            + "{NoPhoto} track(s) + {SessionsNoPhoto} session(s) waiting for a photo, "
            + "{Uploaded} session(s) left to the operator's own upload (event {EventId}).",
            tracks.Built, tracks.Current, sessions.Built, sessions.Current,
            sponsors.Bundles, sponsors.Singles,
            tracks.NoPhoto, sessions.NoPhoto, sessions.LeftToUpload, eventId);

        return new BundleResult(
            tracks.Built, sponsors.Bundles, tracks.NoPhoto,
            SessionGraphics: sessions.Built, SponsorGraphics: sponsors.Singles,
            SessionsLeftToUpload: sessions.LeftToUpload, SessionsSkippedNoPhoto: sessions.NoPhoto);
    }

    // ---- tracks ------------------------------------------------------------------------

    private async Task<(int Built, int Current, int NoPhoto)> BuildTrackBundlesAsync(
        int eventId, byte[] template, byte[]? eventLogo, PhotoIndex photos,
        List<string> unmatched, CancellationToken ct)
    {
        // Same "active session" filter the pull uses, so the two never disagree about what counts.
        var rows = await _db.Sessions
            .AsNoTracking()
            .Where(s => s.EventId == eventId
                        && !s.IsServiceSession
                        && s.Track != null && s.Track != ""
                        && s.SessionSpeakers.Any())
            .SelectMany(s => s.SessionSpeakers.Select(ss => new
            {
                Track = s.Track!,
                ss.ParticipantId,
                Name = ss.Participant.FullName,
            }))
            .ToListAsync(ct);

        if (rows.Count == 0) return (0, 0, 0);

        var built = 0;
        var current = 0;
        var noPhoto = 0;

        foreach (var track in rows.GroupBy(r => r.Track, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();

            // DISTINCT speakers, ordered by participant id — a speaker on two sessions in the same
            // track appears ONCE, and the frame order is stable between runs so an unchanged track
            // produces an unchanged file.
            var speakers = track
                .GroupBy(r => r.ParticipantId)
                .OrderBy(g => g.Key)
                .Select(g => g.First())
                .ToList();

            var frames = new List<(byte[]? Photo, string Name)>();
            foreach (var speaker in speakers)
            {
                var photo = await TryPhotoAsync(photos, speaker.ParticipantId, speaker.Name, ct);
                if (photo is null or { Length: 0 })
                    unmatched.Add($"{speaker.Name} (#{speaker.ParticipantId})");
                frames.Add((photo, speaker.Name));
            }

            if (frames.All(f => f.Photo is null or { Length: 0 }))
            {
                // Every frame would be an empty ring. Waiting beats publishing a row of blanks.
                noPhoto++;
                continue;
            }

            // The speaker SET is the hash — so an add or a remove rebuilds by construction, which is
            // exactly what "when chg happens like add or remove of speaker they must be build" asks
            // for, with nothing to remember to trigger.
            var hash = HashOf(
                DesignVersion, "track", track.Key,
                string.Join("|", speakers.Select(s => $"{s.ParticipantId}:{s.Name}")));

            var outcome = await _graphics.GenerateTrackBundleAsync(
                eventId, Slug(track.Key), track.Key, template, frames, eventLogo, hash, ct: ct);
            if (outcome.Rendered) built++; else current++;
        }

        return (built, current, noPhoto);
    }

    // ---- sessions (phase 2) ------------------------------------------------------------

    /// <summary>
    /// §767 phase 2 — ONE graphic per session: PNG for a single speaker, GIF for two or more.
    /// </summary>
    /// <remarks>
    /// <para>🔒 <b>A session the operator has already uploaded artwork for is LEFT ALONE.</b> The
    /// SharePoint pull writes per-speaker <c>session:{id}:speaker:{pid}</c> rows for files he
    /// dropped in the Sessions / MasterClass folders, and those are deliberate human artwork. The
    /// generated key (<c>session:{id}</c>) cannot collide with them, so nothing is overwritten
    /// either way — but generating anyway would put a second, machine-made graphic next to his on
    /// the same session, which is the one outcome nobody asked for. Held sessions are COUNTED and
    /// logged, so "we generated nothing" never looks like "there was nothing to generate".</para>
    ///
    /// <para>⚠️ The speaker set is in the hash, so the 1→2-speaker flip (PNG→GIF) rebuilds by
    /// construction. It writes a NEW file name — which is precisely why a queued SoMe post must
    /// point at the asset ROW and not a copied path (§767 Round 8, still open).</para>
    /// </remarks>
    private async Task<(int Built, int Current, int NoPhoto, int LeftToUpload)> BuildSessionGraphicsAsync(
        int eventId, byte[] template, byte[]? eventLogo, PhotoIndex photos,
        List<string> unmatched, CancellationToken ct)
    {
        // The same "active session" filter the pull and the track sweep use, so the three never
        // disagree about what counts as a session.
        var sessions = await _db.Sessions
            .AsNoTracking()
            .Where(s => s.EventId == eventId
                        && !s.IsServiceSession
                        && s.SessionSpeakers.Any())
            .Select(s => new
            {
                s.Id,
                s.Title,
                Speakers = s.SessionSpeakers
                    .Select(ss => new { ss.ParticipantId, Name = ss.Participant.FullName })
                    .ToList(),
            })
            .ToListAsync(ct);

        if (sessions.Count == 0) return (0, 0, 0, 0);

        // Sessions that already carry the operator's OWN uploaded artwork (a pulled per-speaker row).
        var uploaded = await _db.GraphicAssets
            .AsNoTracking()
            .Where(g => g.EventId == eventId
                        && g.Type == GraphicAssetType.Session
                        && g.SessionId != null
                        && g.ParticipantId != null)
            .Select(g => g.SessionId!.Value)
            .Distinct()
            .ToListAsync(ct);
        var uploadedIds = uploaded.ToHashSet();

        var built = 0;
        var current = 0;
        var noPhoto = 0;

        foreach (var session in sessions)
        {
            ct.ThrowIfCancellationRequested();

            if (uploadedIds.Contains(session.Id)) continue;

            // DISTINCT speakers, ordered by participant id — stable frame order between runs, so an
            // unchanged line-up hashes the same and produces the same file.
            var speakers = session.Speakers
                .GroupBy(s => s.ParticipantId)
                .OrderBy(g => g.Key)
                .Select(g => g.First())
                .ToList();

            var frames = new List<(byte[]? Photo, string Name)>();
            foreach (var speaker in speakers)
            {
                var photo = await TryPhotoAsync(photos, speaker.ParticipantId, speaker.Name, ct);
                if (photo is null or { Length: 0 })
                    unmatched.Add($"{speaker.Name} (#{speaker.ParticipantId})");
                frames.Add((photo, speaker.Name));
            }

            if (frames.All(f => f.Photo is null or { Length: 0 }))
            {
                // Every frame would be an empty ring. Waiting beats publishing a row of blanks.
                noPhoto++;
                continue;
            }

            // The TITLE is in the hash as well as the line-up: it is printed on the graphic, so a
            // renamed session is a changed picture even when the same people are on it.
            // 🔒 Via SessionGraphicInputHash — the ONE formula, shared with the organizer grid's
            // "stale" check (§6.2). Inlining it here again is how the two would drift apart.
            var hash = SessionGraphicInputHash(
                session.Id, session.Title,
                speakers.Select(s => (s.ParticipantId, s.Name)));

            var outcome = await _graphics.GenerateSessionBundleAsync(
                eventId, session.Id, session.Title ?? string.Empty, template, frames,
                eventLogo, hash, ct: ct);
            if (outcome.Rendered) built++; else current++;
        }

        return (built, current, noPhoto, uploadedIds.Count);
    }

    // ---- sponsors ----------------------------------------------------------------------

    private async Task<(int Bundles, int Singles)> BuildSponsorBundlesAsync(
        int eventId, byte[] template, byte[]? eventLogo, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(SponsorLogosFolder))
        {
            _log.LogInformation(
                "§767 bundles: no sponsor-logo folder configured — sponsor grouping bundles inert "
                + "(track bundles still ran).");
            return (0, 0);
        }

        var sponsors = await _db.SponsorInfos
            .AsNoTracking()
            .Where(s => s.EventId == eventId && s.Status == SponsorStatus.Active)
            .Select(s => new { s.SponsorCompanyId, s.CompanyName, s.SponsorPackage })
            .ToListAsync(ct);

        if (sponsors.Count == 0) return (0, 0);

        var logoFiles = await _store.ListAsync(SponsorLogosFolder, ct);

        // Resolve + download each sponsor's logo ONCE: the tier bundle and that sponsor's own
        // graphic are the same bytes, and this folder is a Graph call per file.
        var resolvedLogos = new Dictionary<string, (byte[] Bytes, string FileName)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var sponsor in sponsors.Where(s => !string.IsNullOrWhiteSpace(s.CompanyName)))
        {
            if (resolvedLogos.ContainsKey(sponsor.CompanyName!)) continue;

            var resolved = ResolveNewestSponsorLogo(logoFiles, sponsor.CompanyName!);
            if (resolved is null) continue;

            var bytes = await TrySponsorLogoAsync(logoFiles, sponsor.CompanyName!, ct);
            if (bytes is not { Length: > 0 }) continue;

            resolvedLogos[sponsor.CompanyName!] = (bytes, resolved.Name);
        }

        var built = 0;
        var singles = 0;

        // §767 phase 2 — the SINGLE-sponsor graphic, one PNG per sponsor. The caption states the
        // tier in the singular ("Platinum sponsor"), matching the grouping bundles' own label so
        // the two read as one family rather than two designs.
        foreach (var sponsor in sponsors
                     .Where(s => !string.IsNullOrWhiteSpace(s.CompanyName)
                                 && !string.IsNullOrWhiteSpace(s.SponsorCompanyId))
                     .OrderBy(s => s.CompanyName, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();

            if (!resolvedLogos.TryGetValue(sponsor.CompanyName!, out var logo)) continue;

            var hash = HashOf(
                DesignVersion, "sponsor", sponsor.SponsorCompanyId!,
                sponsor.SponsorPackage.ToString(), logo.FileName);

            var outcome = await _graphics.GenerateSponsorSingleAsync(
                eventId, sponsor.SponsorCompanyId!, template, logo.Bytes,
                $"{sponsor.SponsorPackage} sponsor", eventLogo, hash, ct: ct);
            if (outcome.Rendered) singles++;
        }

        // TIER groupings. ⚠️ Type groupings (beverage, ice-cream, …) are the SECOND grouping over the
        // same sponsors and come from the webshop category (§767 decision 2) — not yet wired, and
        // deliberately absent rather than faked from the package.
        foreach (var tier in sponsors
                     .Where(s => !string.IsNullOrWhiteSpace(s.CompanyName))
                     .GroupBy(s => s.SponsorPackage)
                     .OrderByDescending(g => g.Key))
        {
            ct.ThrowIfCancellationRequested();

            var logos = new List<byte[]>();
            var signature = new List<string>();
            foreach (var sponsor in tier.OrderBy(s => s.CompanyName, StringComparer.OrdinalIgnoreCase))
            {
                if (!resolvedLogos.TryGetValue(sponsor.CompanyName!, out var logo)) continue;

                logos.Add(logo.Bytes);
                // 🔑 The resolved FILE NAME carries the version, so "new logo generates new version"
                // becomes a hash change and the grouping rebuilds. Keying on the company alone would
                // leave a re-uploaded logo invisible for ever.
                signature.Add(logo.FileName);
            }

            if (logos.Count == 0) continue;

            var hash = HashOf(DesignVersion, "sponsor-tier", tier.Key.ToString(), string.Join("|", signature));

            var outcome = await _graphics.GenerateSponsorCategoryBundleAsync(
                eventId, "tier", Slug(tier.Key.ToString()), $"{tier.Key} sponsors",
                template, logos, eventLogo, hash, ct: ct);
            if (outcome.Rendered) built++;
        }

        return (built, singles);
    }

    // ---- asset loading -----------------------------------------------------------------

    /// <summary>
    /// The template photograph and the white event mark, both from the Template folder.
    /// </summary>
    /// <remarks>
    /// <para>§769 — the operator may NAME both files on the organizer paths page
    /// (<see cref="DocLibrary.DocLibraryFileNames"/>). With no setting the original heuristic stands:
    /// the mark is any file whose name contains "logo" and the template is the first other image.
    /// That guess is right today and wrong the moment a third image lands in the folder, or the
    /// background is named "…logo-backdrop.png" — silently, in the artwork.</para>
    ///
    /// <para>The real file is <c>Template.JPG</c>, so .jpg must be accepted as readily as .png —
    /// assuming a lower-case .png is how §767 found the loader would have missed it entirely.</para>
    /// </remarks>
    private async Task<(byte[]? Template, byte[]? Logo)> LoadTemplateAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(TemplateFolder)) return (null, null);

        var files = await _store.ListAsync(TemplateFolder, ct);
        var images = files
            .Where(f => IsImage(f.Name))
            .ToList();

        // A NAMED file wins over the guess — and an exact name that matches nothing is reported by
        // the log line below rather than silently falling back, because falling back would render
        // the wrong artwork and call it a success.
        var namedLogo = Named(DocLibrary.DocLibraryFileNames.TemplateWordmark, images);
        var namedTemplate = Named(DocLibrary.DocLibraryFileNames.TemplateBackground, images);

        var logoRef = namedLogo ?? images.FirstOrDefault(
            f => f.Name.Contains("logo", StringComparison.OrdinalIgnoreCase));
        var templateRef = namedTemplate ?? images.FirstOrDefault(f => f != logoRef);

        var template = templateRef is null ? null : await _store.DownloadAsync(templateRef.ItemId, ct);
        var logo = logoRef is null ? null : await _store.DownloadAsync(logoRef.ItemId, ct);
        return (template, logo);
    }

    /// <summary>
    /// The image named for this key — the operator's setting if there is one, otherwise the §783.11
    /// SHIPPED DEFAULT. Null when neither resolves, which is logged rather than swallowed.
    /// </summary>
    /// <remarks>
    /// 🔒 §783.11 — the shipped default sits between the setting and the old guess, so the build
    /// asks for a REAL FILE BY NAME even on an installation that has never opened the paths page.
    /// Operator: <i>"it must be named specific to avoid critical mistake"</i>. The guess is still the
    /// last resort, because a folder that genuinely holds different assets must keep working.
    /// </remarks>
    private SharePointFileRef? Named(string key, IReadOnlyList<SharePointFileRef> images)
    {
        var isExplicit = _paths.TryResolveFileName(key, out var wanted)
                         && !string.IsNullOrWhiteSpace(wanted);
        if (!isExplicit) wanted = DocLibrary.DocLibraryFileNames.DefaultFor(key);
        if (string.IsNullOrWhiteSpace(wanted)) return null;

        var hit = images.FirstOrDefault(
            f => string.Equals(f.Name, wanted, StringComparison.OrdinalIgnoreCase));

        if (hit is null)
        {
            _log.LogWarning(
                "§769/§783.11: '{Key}' resolves to '{Wanted}' ({Source}), but the template folder "
                + "'{Folder}' has no such file — falling back to the built-in guess, which may pick "
                + "the WRONG asset. Fix the name on the paths page.",
                key, wanted, isExplicit ? "set on the paths page" : "shipped default", TemplateFolder);
        }

        return hit;
    }

    /// <summary>A folder of speaker photos, indexed by BOTH keys that folder is known to carry.</summary>
    /// <remarks>
    /// 🔒 <b>The archive does NOT use the §165 designer convention.</b> Assuming it did is what made
    /// the first live sweep match NOTHING while looking perfectly healthy — four prod runs of
    /// <i>"0 track GIF(s) … 7 track(s) waiting for a photo"</i>, no error anywhere. The real folder
    /// (verified against SharePoint 2026-08-01, 22 files) held
    /// <c>speaker-photo-Morten-Knudsen-73.jpg</c>: the shape
    /// <see cref="SpeakerPhotoArchiveService"/> and the sponsor-session form both wrote. So index the
    /// PARTICIPANT ID out of it as well as the name — never one or the other.
    ///
    /// <para>§768.16: every writer now writes <c>speaker-photo-{id}</c>, legacy
    /// <c>speaker-photo-{Name}-{id}</c> files stay in the folder (a sponsor-uploaded photo is never
    /// re-archived), and <b>the name index now holds ONLY human-dropped §165 files</b> — see
    /// <c>ListPhotosAsync</c> for why an archive file must not claim a name key.</para>
    /// </remarks>
    private sealed record PhotoIndex(
        IReadOnlyDictionary<int, SharePointFileRef> ById,
        IReadOnlyDictionary<string, SharePointFileRef> BySlug)
    {
        public static readonly PhotoIndex Empty = new(
            new Dictionary<int, SharePointFileRef>(),
            new Dictionary<string, SharePointFileRef>(StringComparer.OrdinalIgnoreCase));

        public int Count => ById.Count + BySlug.Count;
    }

    /// <summary>Index a folder of images by participant id AND by name slug.</summary>
    private async Task<PhotoIndex> ListPhotosAsync(string folder, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(folder)) return PhotoIndex.Empty;

        var files = await _store.ListAsync(folder, ct);
        var byId = new Dictionary<int, SharePointFileRef>();
        var bySlug = new Dictionary<string, SharePointFileRef>(StringComparer.OrdinalIgnoreCase);

        // 🔒 §768.16 — the ids claimed by the CURRENT `speaker-photo-{id}` convention. While legacy
        // `speaker-photo-{Name}-{id}` files remain in the folder one speaker can have TWO, and
        // "first wins" would resolve to whichever Graph happened to list first. The id-only file is
        // what a writer produced most recently, so it WINS — deterministically, not by listing
        // order. The legacy file is the orphan (§6.9 disposes of it), never the source.
        var claimedByCurrent = new HashSet<int>();

        foreach (var f in files.Where(f => IsImage(f.Name)))
        {
            var bare = StripExtension(f.Name);

            // 🔒 §768.16 — an archive-written file is indexed by ID ONLY, whichever convention it
            // carries. It used to be indexed by its name TOO, which was free while both keys pointed
            // at the same file. They no longer do: a legacy file registering "morten-knudsen" would
            // BEAT the speaker's current `speaker-photo-73.jpg`, because the name pass runs first
            // (a human's file wins) — and it would beat it for precisely the speakers mid-rename.
            // The name key belongs to human-dropped §165 files alone.
            if (SpeakerPhotoFileName.TryParse(bare, out var participantId, out var namePart))
            {
                if (namePart.Length == 0)
                {
                    if (claimedByCurrent.Add(participantId)) byId[participantId] = f;
                }
                else if (!claimedByCurrent.Contains(participantId))
                {
                    byId.TryAdd(participantId, f);                      // first legacy file wins
                }
                continue;
            }

            // Anything else is a HUMAN's file — the §165 "Firstname-Lastname.jpg" convention.
            var slug = Slug(bare);
            if (!string.IsNullOrEmpty(slug)) bySlug.TryAdd(slug, f);
        }

        return new PhotoIndex(byId, bySlug);
    }

    /// <summary>
    /// Find one speaker's photo — a human's named file first, then the participant id, then a PREFIX
    /// match on the name.
    /// </summary>
    /// <remarks>
    /// <para>🔒 <b>A human's file wins</b>: a photo an operator or designer dropped in under the §165
    /// name is deliberate, so it is tried before the archived copy. The id pass then catches everyone
    /// else exactly — and it survives a RENAME, where the name pass silently would not.</para>
    ///
    /// <para>The prefix pass is kept last so an exact name never loses to a longer one.</para>
    /// </remarks>
    private async Task<byte[]?> TryPhotoAsync(
        PhotoIndex photos, int participantId, string name, CancellationToken ct)
    {
        if (photos.Count == 0) return null;

        SharePointFileRef? file = null;

        var slug = string.IsNullOrWhiteSpace(name) ? string.Empty : Slug(name);
        if (!string.IsNullOrEmpty(slug)) photos.BySlug.TryGetValue(slug, out file);

        if (file is null && participantId > 0) photos.ById.TryGetValue(participantId, out file);

        if (file is null && !string.IsNullOrEmpty(slug))
        {
            file = photos.BySlug
                .Where(kv => kv.Key.StartsWith(slug, StringComparison.OrdinalIgnoreCase))
                .OrderBy(kv => kv.Key.Length)
                .Select(kv => kv.Value)
                .FirstOrDefault();
        }

        if (file is null) return null;

        try
        {
            return await _store.DownloadAsync(file.ItemId, ct);
        }
        catch (Exception ex)
        {
            // One unreadable file must not lose the whole bundle — the other frames are still worth
            // publishing, and the miss is visible in the log rather than as a silent gap.
            _log.LogWarning(ex, "§767 bundles: could not download '{File}' for '{Name}'.", file.Name, name);
            return null;
        }
    }

    /// <summary>
    /// One sponsor's SoMe logo: the NEWEST version of <c>SoMeBrandingLogo_{Sponsor}_v{N}.png</c>.
    /// </summary>
    /// <remarks>
    /// <para>🔒 The sponsor upload path (<c>SponsorUploadKinds</c>, kind <c>"some"</c>) writes
    /// <c>{Prefix}{Sponsor}_v{N}{ext}</c> into the SoMe-branding folder — NOT
    /// <c>"{Company} - {file}"</c> like the collection folder. Operator 2026-08-01: <i>"the logic
    /// today uploads to the some branding folder … must match"</i>. Matching on the collection
    /// convention found nothing at all, silently: every bundle would simply have had no logos.</para>
    ///
    /// <para>🔑 <b>The VERSION is the change signal.</b> Operator: <i>"remember new logo generates
    /// new version so SoMe must be regenerated"</i> — a re-upload writes <c>_v{N+1}</c> rather than
    /// overwriting, so taking the highest N means a new logo is picked up on the next sweep by
    /// construction. The resolved NAME (version included) is what a rebuild hash must be keyed on:
    /// same name ⇒ nothing changed; new version ⇒ rebuild.</para>
    /// </remarks>
    private async Task<byte[]?> TrySponsorLogoAsync(
        IReadOnlyList<SharePointFileRef> files, string companyName, CancellationToken ct)
    {
        var resolved = ResolveNewestSponsorLogo(files, companyName);
        if (resolved is null) return null;

        try
        {
            return await _store.DownloadAsync(resolved.ItemId, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex, "§767 bundles: could not download sponsor logo '{File}' for '{Co}'.",
                resolved.Name, companyName);
            return null;
        }
    }

    /// <summary>
    /// Pick the highest-versioned Web logo for a company. Public for the test that pins the naming
    /// contract — it is the one thing here that silently returns nothing when it drifts.
    /// </summary>
    /// <remarks>
    /// <para>🔒 §768.14 — this now asks <see cref="CommunityHub.Uploads.SponsorUploadNaming"/> the
    /// same question the upload writers answer when they NAME the file. It previously took the name
    /// apart with its own heuristic: split a trailing <c>_v{N}</c>, then discard "the upload prefix"
    /// by dropping everything up to the FIRST underscore. That guess was load-bearing and
    /// undocumented — a sponsor whose name contained an underscore lost its logo, and any change to
    /// the writers' prefix would have broken the match with no error raised anywhere. Build and
    /// parse share one file now: if the contract changes, both move together or neither compiles.</para>
    ///
    /// <para>⚠️ A miss here is SILENT — the sponsor simply gets no graphic, and the sweep logs a
    /// healthy run. Never "fix" a missing sponsor graphic by loosening this matcher; check that the
    /// file in <c>Sponsors/Logo/Web</c> is named to the contract.</para>
    /// </remarks>
    public static SharePointFileRef? ResolveNewestSponsorLogo(
        IReadOnlyList<SharePointFileRef> files, string companyName)
    {
        if (files.Count == 0 || string.IsNullOrWhiteSpace(companyName)) return null;

        SharePointFileRef? best = null;
        var bestVersion = 0;

        foreach (var file in files.Where(f => IsImage(f.Name)))
        {
            if (!CommunityHub.Uploads.SponsorUploadNaming.Matches(
                    file.Name, "some", companyName, out var version)) continue;
            if (version <= bestVersion) continue;

            best = file;
            bestVersion = version;
        }

        return best;
    }

    /// <summary>
    /// Bump when the LOCKED DESIGN changes, so every graphic rebuilds ONCE on the next sweep.
    /// </summary>
    /// <remarks>
    /// Without it a design change would only reach subjects that happen to alter something
    /// afterwards, leaving the line-up split across two looks — invisible until two posts sit side
    /// by side.
    /// </remarks>
    /// <remarks>
    /// <c>767.2</c> (2026-08-02) — the bottom strip (<c>9-10 FEB 2027 · COPENHAGEN, DENMARK</c>) was
    /// never set on the SWEEP's renderer, only on the approved examples, so the seven track GIFs
    /// built on 2026-08-01 carry a blank strip. Bumping rebuilds them ONCE; without the bump they
    /// would keep their old hash and the corrected design would reach nothing.
    /// </remarks>
    /// <remarks>
    /// 🔒 <c>768.0</c> (2026-08-02) — <b>the write root moved INSIDE the event tree.</b> Generated
    /// artwork used to land at drive-root <c>Graphics/…</c>, outside the folder structure the
    /// operator curates; it now resolves through the DocLibrary registry to
    /// <c>…/Speakers/Graphics-SoMe/…</c> and <c>…/Sponsors/Graphics-SoMe/…</c>.
    ///
    /// <para>⚠️ <b>THE BUMP IS THE WHOLE POINT — a path change rebuilds NOTHING on its own.</b>
    /// <see cref="GraphicAsset.InputHash"/> is computed from the CONTENT inputs (design version,
    /// speaker set, title, logo version); the DESTINATION is not part of it. So without this bump
    /// every graphic would hash identically at the new location, the guard would answer "already
    /// current", and the sweep would write not one file — while logging a perfectly healthy
    /// <c>0 rebuilt, N already current</c>. Same trap as 767.2, one level up.</para>
    ///
    /// <para>⇒ Verify a root move by LISTING THE NEW FOLDER, never by reading rebuilt counts.</para>
    /// </remarks>
    public const string DesignVersion = "768.0";

    /// <summary>
    /// §6.2 — the input hash of ONE SESSION's promo graphic.
    /// </summary>
    /// <remarks>
    /// <para>🔒 <b>PUBLIC so the organizer grid can ask "is this stale?" using the SAME formula the
    /// sweep rebuilds on.</b> Computing it with a second, similar-looking hash would be the §767
    /// failure in its purest form: the page would confidently report "current" about a graphic the
    /// very next sweep replaces — or flag a permanent "stale" that no rebuild ever clears, because
    /// the page and the sweep never agreed on the number.</para>
    ///
    /// <para>⇒ <b>"Stale" therefore means exactly one thing: the next sweep WILL rebuild this.</b>
    /// That is a promise the page can keep, because it is the same comparison the sweep makes.</para>
    ///
    /// <para>🔒 <b>It NORMALISES the speaker set itself</b> — distinct by participant id, ordered by
    /// participant id — rather than trusting the caller to have done it. The ordering is part of the
    /// hash, so a caller that passed the same people in a different order would compute a different
    /// number and the grid would report a permanent, unfixable "stale". Putting the rule inside the
    /// function removes that whole class of bug instead of documenting it.</para>
    /// </remarks>
    public static string SessionGraphicInputHash(
        int sessionId, string? title, IEnumerable<(int ParticipantId, string Name)> speakers) =>
        HashOf(
            DesignVersion, "session", sessionId.ToString(), title ?? string.Empty,
            string.Join("|", speakers
                .GroupBy(s => s.ParticipantId)
                .OrderBy(g => g.Key)
                .Select(g => $"{g.Key}:{g.First().Name}")));

    /// <summary>
    /// §6.2 — is a generated graphic STALE: was its source changed after it was generated?
    /// </summary>
    /// <remarks>
    /// <para>🔒 <b>Stale means the next sweep WILL rebuild it.</b> This is the same comparison the
    /// sweep makes (<c>existing.InputHash == inputHash</c>), so the badge is a promise the page can
    /// keep. Anything looser would be a warning the organizer cannot clear by rebuilding.</para>
    ///
    /// <para>⚠️ <b>Two refusals, and they matter more than the positive case.</b></para>
    /// <list type="bullet">
    /// <item><b>A NULL stored hash is UNKNOWN, never stale.</b> Rows predating §767 and anything
    /// PULLED from the library have no recorded inputs, and the engine already refuses to regenerate
    /// those — so flagging them would show a permanent warning nothing can clear.</item>
    /// <item><b>Organizer-overruled artwork is never stale.</b> A human deliberately replaced the
    /// generated picture and the engine will not touch it. Calling that "stale" tells the operator
    /// his own artwork is out of date and invites him to destroy it.</item>
    /// </list>
    /// </remarks>
    public static bool IsGraphicStale(
        string? storedInputHash, bool organizerOverridden, string? currentInputHash)
    {
        if (organizerOverridden) return false;
        if (string.IsNullOrWhiteSpace(storedInputHash)) return false;
        if (string.IsNullOrWhiteSpace(currentInputHash)) return false;

        return !string.Equals(storedInputHash, currentInputHash, StringComparison.Ordinal);
    }

    /// <summary>A stable hash of everything composed into a graphic.</summary>
    private static string HashOf(params string[] parts) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join("", parts))))[..32];

    private static bool IsImage(string fileName) =>
        fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);

    private static string StripExtension(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        return dot <= 0 ? fileName : fileName[..dot];
    }

    /// <summary>Lower-case, non-alphanumerics collapsed to '-' — the same shape the pull slugs to.</summary>
    private static string Slug(string value)
    {
        var chars = value.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        return string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
    }
}
