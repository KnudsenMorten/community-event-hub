namespace CommunityHub.Core.Integrations.DocLibrary;

/// <summary>What a registered path is for, and how urgently it must resolve.</summary>
public enum DocLibraryPathStatus
{
    /// <summary>In use today. 🔒 <b>Must resolve at startup or the host fails fast.</b></summary>
    Active = 0,

    /// <summary>The folder exists; the feature that uses it is not built yet. Unset is fine.</summary>
    Planned = 1,

    /// <summary>Registered so the name is reserved and documented, but nothing reads or writes it.</summary>
    Reserved = 2,
}

/// <summary>Which way data flows, for the organizer Paths page and the audit.</summary>
[Flags]
public enum DocLibraryDirection
{
    None = 0,
    Read = 1,
    Write = 2,
    Upload = 4,
    Delete = 8,
}

/// <summary>One registered document-library location.</summary>
/// <param name="Key">
/// The configuration key, e.g. <c>SpeakerPhotos</c> — bound as <c>DocLibrary:Paths:SpeakerPhotos</c>.
/// </param>
/// <param name="DefaultRelativePath">
/// Where it sits <b>under the configured root</b>. ⚠️ This is a documented default, NOT a silent
/// fallback: <see cref="DocLibraryOptions"/> only uses it when the key is absent from configuration,
/// and an <see cref="DocLibraryPathStatus.Active"/> key that resolves to nothing fails startup.
/// </param>
/// <param name="Area">Grouping for the organizer page.</param>
/// <param name="Direction">How the product uses it.</param>
/// <param name="FileNamePattern">The naming contract, or null when the folder is enumerated.</param>
/// <param name="CorrelationKey">The identity a stored file carries (REQUIREMENTS §768 / work-order §5.5).</param>
/// <param name="Notes">Why it exists, and anything that has previously gone wrong with it.</param>
public sealed record DocLibraryPathDefinition(
    string Key,
    string DefaultRelativePath,
    string Area,
    DocLibraryDirection Direction,
    DocLibraryPathStatus Status = DocLibraryPathStatus.Active,
    string? FileNamePattern = null,
    string? CorrelationKey = null,
    string? Notes = null);

/// <summary>
/// THE REGISTRY — every document-library folder the product knows about, in one place.
/// </summary>
/// <remarks>
/// <para>🔒 <b>This replaces a scatter of eighteen discrete option properties and a second, rival
/// set in the edition config.</b> Two systems defining the same folders is what let the paths drift
/// out of step with the library in the first place (REQUIREMENTS §768; audit baseline §1), and a
/// path that lives in Azure app settings alone leaves no diff and no review trail.</para>
///
/// <para>🔑 <b>Every path is RELATIVE TO ONE ROOT.</b> That is the whole point: PROD and DEV differ
/// only in <see cref="DocLibraryOptions.RootFolderPath"/>
/// (<c>General/Events/ELDK 2027/EventHub</c> vs <c>General/DEVELOPMENT/EventHub</c>), and every
/// folder beneath carries the SAME name in both. Operator 2026-08-02: <i>"you create the structure
/// so it has same folder names as prod - just under the dev root"</i>. A path that hard-codes its
/// own root cannot follow an environment, which is precisely how CEH became non-generic.</para>
///
/// <para>⚠️ <b>Vendor-neutral by rule.</b> The product says <c>DocLibrary</c>, never "SharePoint" —
/// the same convention as <c>ExternalEventSystem</c> (Zoho), <c>ERPSystem</c> (e-conomic),
/// <c>CallForSpeakersSystem</c> (Sessionize). SharePoint is one implementation of this interface,
/// named only in the adapter that talks to Graph.</para>
///
/// <para>⚠️ <b>Adding a path? Add it HERE first.</b> A folder that only exists as a string at a call
/// site is invisible to the organizer Paths page, to the startup validation and to the next audit.</para>
/// </remarks>
public static class DocLibraryPaths
{
    // ---- AI & finance -------------------------------------------------------------------
    public const string AiGrounding = nameof(AiGrounding);
    public const string TravelReimbursement = nameof(TravelReimbursement);

    // ---- speakers -----------------------------------------------------------------------
    public const string SpeakerSessionGraphics = nameof(SpeakerSessionGraphics);
    public const string SpeakerTrackGraphics = nameof(SpeakerTrackGraphics);
    public const string SpeakerPhotos = nameof(SpeakerPhotos);
    public const string SpeakerAvInstructions = nameof(SpeakerAvInstructions);
    public const string SessionPresentationsPreview = nameof(SessionPresentationsPreview);
    public const string SessionPresentationsFinal = nameof(SessionPresentationsFinal);
    public const string SessionEvaluationQr = nameof(SessionEvaluationQr);
    public const string SessionEvaluationResults = nameof(SessionEvaluationResults);
    public const string SpeakerTemplate = nameof(SpeakerTemplate);

    // ---- sponsors -----------------------------------------------------------------------
    // ⚰️ §822 — SponsorUploadRoot RETIRED 2026-08-04 (operator: *"retire this legacy"*). Nothing
    // creates per-company upload folders any more, so the key had no reader left. Do not re-add it.
    public const string SponsorBoothCollateral = nameof(SponsorBoothCollateral);
    public const string SponsorExhibitorWall = nameof(SponsorExhibitorWall);
    public const string SponsorLogoWeb = nameof(SponsorLogoWeb);
    public const string SponsorLogoPrint = nameof(SponsorLogoPrint);
    public const string SponsorGraphicsSponsors = nameof(SponsorGraphicsSponsors);
    public const string SponsorGraphicsCategories = nameof(SponsorGraphicsCategories);

    // ---- event: SoMe (§824.2A) ------------------------------------------------------------
    /// <summary>Graphics for Type 5 event posts.</summary>
    public const string EventSoMeGraphics = nameof(EventSoMeGraphics);

    /// <summary>The event post copy deck — a FILE, not a folder.</summary>
    public const string EventSoMeTextFile = nameof(EventSoMeTextFile);

    /// <summary>§844 — videos for EVENT (Type 5) posts, where a video replaces the graphic.</summary>
    public const string EventSoMeVideos = nameof(EventSoMeVideos);

    /// <summary>§844.2 — videos for SESSION (Type 2) posts.</summary>
    public const string SpeakerSessionVideos = nameof(SpeakerSessionVideos);

    /// <summary>§844.2 — videos for SPONSOR (Type 4) posts.</summary>
    public const string SponsorVideos = nameof(SponsorVideos);

    // ---- event: volume-package group photo (§1077 stage 3) ---------------------------------
    /// <summary>§1077 — the WEB logo a volume-package company uploads in its wizard.</summary>
    public const string GroupPhotoLogoWeb = nameof(GroupPhotoLogoWeb);

    /// <summary>§1077 — the same in print quality.</summary>
    public const string GroupPhotoLogoPrint = nameof(GroupPhotoLogoPrint);

    // ---- event: the media crew's own libraries (§1078) -------------------------------------
    /// <summary>§1078 — the press/photo crew's picture library, managed in the hub.</summary>
    public const string MediaPictures = nameof(MediaPictures);

    /// <summary>§1078 — the same for video.</summary>
    public const string MediaVideo = nameof(MediaVideo);

    // ---- event: evaluations -------------------------------------------------------------
    public const string EventEvalDuringSessions = nameof(EventEvalDuringSessions);
    public const string EventEvalAfterSponsor = nameof(EventEvalAfterSponsor);
    public const string EventEvalAfterAttendee = nameof(EventEvalAfterAttendee);
    public const string EventEvalAfterSpeaker = nameof(EventEvalAfterSpeaker);

    // ---- event: logistics ---------------------------------------------------------------
    public const string SwagAward = nameof(SwagAward);
    public const string SwagPolo = nameof(SwagPolo);
    public const string SwagCredly = nameof(SwagCredly);
    public const string Hotel = nameof(Hotel);
    public const string BcFood = nameof(BcFood);
    public const string BcExpo = nameof(BcExpo);

    // ---- event: brand, venue, volunteers ------------------------------------------------
    public const string EventGraphicsTemplate = nameof(EventGraphicsTemplate);
    public const string EventLogoPack = nameof(EventLogoPack);
    public const string VenueRoot = nameof(VenueRoot);
    public const string VenueGoodToKnow = nameof(VenueGoodToKnow);
    public const string VenueWayfinding = nameof(VenueWayfinding);
    public const string VolunteerPhotos = nameof(VolunteerPhotos);

    private const DocLibraryDirection RW = DocLibraryDirection.Read | DocLibraryDirection.Write;

    /// <summary>Every registered path, in organizer-page order.</summary>
    public static readonly IReadOnlyList<DocLibraryPathDefinition> All = new[]
    {
        // ---- AI & finance ---------------------------------------------------------------
        new DocLibraryPathDefinition(
            AiGrounding, "ExtraAIGroundingInfo", "AI", DocLibraryDirection.Read,
            Notes: "Drop-folder for Community Helper grounding material (Word/Excel/PDF). The "
                 + "pipeline, the instructions and the model backend all already exist — only the "
                 + "folder it enumerates is in scope."),

        new DocLibraryPathDefinition(
            TravelReimbursement, "Finance/Travel Reimbursement", "Finance",
            DocLibraryDirection.Upload,
            FileNamePattern: "claim-{claimId}-{seq}.{ext}", CorrelationKey: "claim:{id}",
            Notes: "Up to 5 files per claim, PDF or image. The claim id must appear in the file name "
                 + "AND in the organizer notification, so a payment can be reconciled to a claim."),

        // ---- speakers -------------------------------------------------------------------
        new DocLibraryPathDefinition(
            SpeakerSessionGraphics, "Speakers/Graphics-SoMe/Sessions", "Speakers", RW,
            FileNamePattern: "session-{id}.png (1 speaker) · session-{id}.gif (2+)",
            CorrelationKey: "session:{id}",
            Notes: "🔑 ONE folder for master classes AND technical sessions (operator 2026-08-02: "
                 + "\"i dont see a need to split\"). Also holds operator-uploaded artwork, which the "
                 + "pull matches by session TITLE slug — generated files are named by id, so the two "
                 + "cannot collide."),

        new DocLibraryPathDefinition(
            SpeakerTrackGraphics, "Speakers/Graphics-SoMe/SpeakerTracks", "Speakers", RW,
            FileNamePattern: "track-{slug}.gif", CorrelationKey: "track:{name}",
            Notes: "⚠️ Keyed on the track NAME — there is no stable track id (Session.Track is free "
                 + "text). A track rename desynchronises writer and reader with no error."),

        new DocLibraryPathDefinition(
            SpeakerPhotos, "Speakers/Photos", "Speakers",
            DocLibraryDirection.Read | DocLibraryDirection.Write
                | DocLibraryDirection.Upload | DocLibraryDirection.Delete,
            FileNamePattern: "speaker-photo-{speakerId}.{ext}", CorrelationKey: "speaker:{id}",
            Notes: "Four writers (Sessionize import copy, the photo archive job, sponsor upload, "
                 + "organizer upload) and the sole photo source for every generated graphic. All of "
                 + "them compose the name through SpeakerPhotoFileName.Build — §768.16, operator: "
                 + "\"use id only (not speaker name and id)\". 🔒 The extension follows the SOURCE — "
                 + "a JPEG must not be written under a .png name. ⚠️ Legacy speaker-photo-{Name}-{id} "
                 + "files stay READABLE: a sponsor-uploaded photo is never re-archived, so those "
                 + "names persist indefinitely."),

        new DocLibraryPathDefinition(
            SpeakerAvInstructions, "Speakers/PicturesInstructions", "Speakers",
            DocLibraryDirection.Read,
            Notes: "AV page images. 🔒 Static file names INCLUDING their typos — 'hdmi-swiitcher.jpeg' "
                 + "is the real name. Operator: \"use the filenames in folders. it is typos\"."),

        new DocLibraryPathDefinition(
            SessionPresentationsPreview, "Speakers/Presentations/Preview", "Speakers",
            DocLibraryDirection.Upload | DocLibraryDirection.Read,
            CorrelationKey: "session:{id}", Notes: "PDF / PPT / PPTX."),

        new DocLibraryPathDefinition(
            SessionPresentationsFinal, "Speakers/Presentations/Final", "Speakers",
            DocLibraryDirection.Upload | DocLibraryDirection.Read,
            CorrelationKey: "session:{id}",
            Notes: "Both decks feed the attendee session-overview download. ⚠️ The precedence rule "
                 + "when BOTH exist is unconfirmed — read it, do not invent one."),

        new DocLibraryPathDefinition(
            SessionEvaluationQr, "Speakers/SessionEvaluations/QR", "Speakers", RW,
            CorrelationKey: "session:{id}",
            Notes: "Stored by session id; DELIVERED renamed to the session TITLE — an internal id "
                 + "must never reach a speaker."),

        new DocLibraryPathDefinition(
            SessionEvaluationResults, "Speakers/SessionEvaluations/Result", "Speakers", RW,
            CorrelationKey: "session:{id}",
            Notes: "Also the attachment source for the speaker results mail. Same rename-on-delivery "
                 + "rule as the QR."),

        new DocLibraryPathDefinition(
            SpeakerTemplate, "Speakers/SpeakerTemplate", "Speakers", DocLibraryDirection.Read,
            Notes: "Presentation template. Offered to Community and Guest speakers only — sponsors "
                 + "use their own company template and see no download link."),

        // ---- sponsors -------------------------------------------------------------------
        // ⚰️ §822 — the SponsorUploadRoot row is GONE from the settings page. It read "Per-company
        // upload folders are created beneath this root", and nothing creates them any more. An
        // editable path with no reader is worse than none: it invites someone to re-point it and
        // expect folders to appear — the §326bx "control that governs nothing" defect, with a
        // SharePoint tree attached.

        new DocLibraryPathDefinition(
            SponsorBoothCollateral, "Sponsors/Booth Collateral", "Sponsors",
            DocLibraryDirection.Upload | DocLibraryDirection.Read,
            Notes: "Sponsor Get Started wizard; source for the booth-collateral sync to the external "
                 + "event system."),

        new DocLibraryPathDefinition(
            SponsorExhibitorWall, "Sponsors/Exhibitor Wall", "Sponsors", DocLibraryDirection.Upload,
            FileNamePattern: "{sponsorName}-exhibitor-wall-{version}.{ext}",
            CorrelationKey: "sponsor:{name} + version:{version}",
            Notes: "Any format. Reviewers are notified on each upload."),

        new DocLibraryPathDefinition(
            SponsorLogoWeb, "Sponsors/Logo/Web", "Sponsors",
            DocLibraryDirection.Upload | DocLibraryDirection.Read,
            FileNamePattern: "{sponsorName}-logo-web-{version}.png",
            CorrelationKey: "sponsor:{name} + version:{version}",
            Notes: "🔒 PNG only. ONE file now serves BOTH the promotion graphics and the external "
                 + "event system — the separate lead-system logo is retired, and no geometry "
                 + "constraint applies (operator 2026-08-02)."),

        new DocLibraryPathDefinition(
            SponsorLogoPrint, "Sponsors/Logo/Print", "Sponsors", DocLibraryDirection.Upload,
            FileNamePattern: "{sponsorName}-logo-print-{version}.{ext}",
            CorrelationKey: "sponsor:{name} + version:{version}",
            Notes: "Vector only: EPS, AI, PDF."),

        new DocLibraryPathDefinition(
            SponsorGraphicsSponsors, "Sponsors/Graphics-SoMe/Sponsors", "Sponsors", RW,
            FileNamePattern: "sponsor-{companyId}.png", CorrelationKey: "sponsor:{companyId}",
            Notes: "Per-sponsor promotion graphic. Internal-only — never shown in a sponsor's view."),

        new DocLibraryPathDefinition(
            SponsorGraphicsCategories, "Sponsors/Graphics-SoMe/SponsorCategories", "Sponsors", RW,
            FileNamePattern: "sponsor-tier-{slug}.gif",
            CorrelationKey: "sponsor-tier:{slug} / sponsor-type:{slug}",
            Notes: "Grouping graphic, one frame per member. Keys on TIER today; type groupings await "
                 + "the webshop category and are deliberately absent rather than faked."),

        // ---- event: SoMe (§824.2A, operator 2026-08-04) -----------------------------------
        new DocLibraryPathDefinition(
            EventSoMeGraphics, "Event/SoMe/Graphics-SoMe", "Event",
            DocLibraryDirection.Read | DocLibraryDirection.Upload,
            Notes: "Graphics for Type 5 EVENT posts — the general announcements he writes himself. "
                 + "Distinct from the speaker/session and sponsor graphics folders, which are "
                 + "generated per subject; these are hand-made and picked per post."),

        // §844 (operator 2026-08-05: "we will also provide videos which will replace graphics for
        // some post. i need a repo llocation for some videos"). Beside the graphics folder because
        // it is the same job — the artwork a Type 5 post carries — just a different medium.
        // ⚠️ UPLOAD is included in the direction: he lands videos here himself, and §844 requires
        // the actual MOVIE FILE rather than a URL, since LinkedIn plays native video in-feed and
        // renders a mere link as a preview card.
        // 🔒 §844.2 — THREE folders, not one: videos are for EVENT, SESSION and SPONSOR posts
        // (operator: "videos are for event,sessions and sponsors"), and they mirror the three
        // graphics folders rather than pooling into a flat one — the same per-area split §768
        // already settled. Tracks (Type 1) and sponsor categories (Type 3) get no video.
        new DocLibraryPathDefinition(
            EventSoMeVideos, "Event/SoMe/Videos-SoMe", "Event",
            DocLibraryDirection.Read | DocLibraryDirection.Upload,
            Notes: "Videos for TYPE 5 event posts. VIDEO IS PREFERRED over the graphic (§844.2); the "
                 + "graphic is the fallback when no video exists. Published natively — a URL would "
                 + "post as a link preview, not a video."),

        new DocLibraryPathDefinition(
            SpeakerSessionVideos, "Speakers/Videos-SoMe/Sessions", "Speakers",
            DocLibraryDirection.Read | DocLibraryDirection.Upload,
            Notes: "Videos for TYPE 2 session posts. Mirrors SpeakerSessionGraphics; video wins over "
                 + "the graphic when present (§844.2)."),

        // §1077 stage 3 (operator 2026-08-11: "SharePoint folders to register in DocLibraryPaths:
        // …/Event/GroupPhotos/Web and …/Event/GroupPhotos/Print (he has pre-staged them)").
        // 🔑 The company uploads its logo IN THE WIZARD, through a token link, and the bytes are
        // written with the APP's credentials — the uploader has no SharePoint access and never
        // receives a link to one (the §160 rule, same as sponsors and speakers).
        new DocLibraryPathDefinition(
            GroupPhotoLogoWeb, "Event/GroupPhotos/Web", "Event",
            DocLibraryDirection.Read | DocLibraryDirection.Upload,
            FileNamePattern: "vp-{companyId}-web.{ext}", CorrelationKey: "volume-package:{id}",
            Notes: "Web-quality logo for the volume-package keynote mention and announcement. Named "
                 + "by COMPANY ID, so a re-upload replaces rather than accumulating near-duplicates "
                 + "nobody can tell apart at print time."),

        new DocLibraryPathDefinition(
            GroupPhotoLogoPrint, "Event/GroupPhotos/Print", "Event",
            DocLibraryDirection.Read | DocLibraryDirection.Upload,
            FileNamePattern: "vp-{companyId}-print.{ext}", CorrelationKey: "volume-package:{id}",
            Notes: "The print-quality counterpart. Same naming rule and the same reason."),

        // §1078 (operator 2026-08-11) — the MEDIA CREW's own two libraries, pre-staged by him.
        // 🔑 DELETE is in the direction on purpose: "full permissions to add/delete files". The hub
        // does it with the APP's credentials (his words: "not in their user context, but through the
        // app context"), which is also why these are hub pages rather than the two SharePoint links
        // he pasted — a link opens in the visitor's own session and needs their own tenant account.
        new DocLibraryPathDefinition(
            MediaPictures, "Event/Media/Pictures", "Event",
            DocLibraryDirection.Read | DocLibraryDirection.Upload | DocLibraryDirection.Delete,
            Notes: "The press/photo crew's picture library, managed on /Media/Pictures by the Media "
                 + "role and organizers. Files are listed, uploaded, downloaded and deleted through "
                 + "the hub — media people never get a SharePoint link (the §160 rule)."),

        new DocLibraryPathDefinition(
            MediaVideo, "Event/Media/Video", "Event",
            DocLibraryDirection.Read | DocLibraryDirection.Upload | DocLibraryDirection.Delete,
            Notes: "The same for video, on /Media/Videos. ⚠️ Uploads are STREAMED (§455): a whole "
                 + "video must never sit in the web app's memory."),

        new DocLibraryPathDefinition(
            SponsorVideos, "Sponsors/Videos-SoMe/Sponsors", "Sponsors",
            DocLibraryDirection.Read | DocLibraryDirection.Upload,
            Notes: "Videos for TYPE 4 sponsor posts. Mirrors SponsorGraphicsSponsors; video wins "
                 + "over the graphic when present (§844.2)."),

        // ⚠️ A FILE, not a folder — the only entry in this registry that is. It is his copy deck for
        // event posts, and the registry is where every other SoMe location already lives, so putting
        // it anywhere else would be the "two rival systems" split §768 exists to prevent.
        new DocLibraryPathDefinition(
            EventSoMeTextFile, "Event/SoMe/Text-SoMe/ELDK27-LinkedIn-posts.md", "Event",
            DocLibraryDirection.Read, DocLibraryPathStatus.Active,
            Notes: "The event post copy deck (a single .md file) — a DROP-BOX that IS imported into "
                 + "the Type 5 post repo, keyed by the slug the file itself states (§828). He can "
                 + "land a new deck later; an existing post is never overwritten unless its per-post "
                 + "tick is on. 🔴 This note used to say 'read-only, not imported' (§824.24) — he "
                 + "corrected that within the hour: 'we need to import them into the db and store in "
                 + "the post repo'. Do not restore the read-only reading."),

        // ---- event: evaluations ---------------------------------------------------------
        new DocLibraryPathDefinition(
            EventEvalDuringSessions, "Event/Evaluations/During the event/SessionEvaluations",
            "Event", DocLibraryDirection.Write, DocLibraryPathStatus.Planned,
            Notes: "A SECOND COPY of every session evaluation result — not a replacement — plus a "
                 + "combined summary across all sessions, so post-event analysis sits together."),

        new DocLibraryPathDefinition(
            EventEvalAfterSponsor, "Event/Evaluations/After the event/Sponsor", "Event",
            DocLibraryDirection.Write, DocLibraryPathStatus.Planned),
        new DocLibraryPathDefinition(
            EventEvalAfterAttendee, "Event/Evaluations/After the event/Attendee", "Event",
            DocLibraryDirection.Write, DocLibraryPathStatus.Planned),
        new DocLibraryPathDefinition(
            EventEvalAfterSpeaker, "Event/Evaluations/After the event/Speaker", "Event",
            DocLibraryDirection.Write, DocLibraryPathStatus.Planned),

        // ---- event: logistics -----------------------------------------------------------
        new DocLibraryPathDefinition(
            SwagAward, "Event/Swag/Award", "Event", DocLibraryDirection.Write,
            DocLibraryPathStatus.Planned, FileNamePattern: "{eventShortName}-award.xlsx"),
        new DocLibraryPathDefinition(
            SwagPolo, "Event/Swag/Polo", "Event", DocLibraryDirection.Write,
            DocLibraryPathStatus.Planned, FileNamePattern: "{eventShortName}-polo.xlsx"),
        new DocLibraryPathDefinition(
            SwagCredly, "Event/Swag/Credly", "Event", DocLibraryDirection.Write,
            DocLibraryPathStatus.Planned,
            FileNamePattern: "{eventShortName}-credly-{roleName}.xlsx + .csv",
            Notes: "One pair per role."),
        new DocLibraryPathDefinition(
            Hotel, "Event/Hotel", "Event", DocLibraryDirection.Write, DocLibraryPathStatus.Planned,
            FileNamePattern: "{eventShortName}-hotel-{hotelName}.xlsx",
            Notes: "One file per hotel engagement; from 3 weeks out a change mails that hotel's own "
                 + "contact, which already exists in CEH."),
        new DocLibraryPathDefinition(
            BcFood, "Event/BC/Food", "Event", DocLibraryDirection.Write,
            DocLibraryPathStatus.Planned,
            Notes: "Six files, rebuilt daily, mailed weekly to the venue."),
        new DocLibraryPathDefinition(
            BcExpo, "Event/BC/Expo", "Event", DocLibraryDirection.Write,
            DocLibraryPathStatus.Planned,
            Notes: "TV + furniture rental, built from webshop orders, rebuilt daily."),

        // ---- event: brand, venue, volunteers --------------------------------------------
        new DocLibraryPathDefinition(
            EventGraphicsTemplate, "Event/Graphics/Template", "Event", DocLibraryDirection.Read,
            Notes: "🔒 The background photograph + white wordmark every generated graphic composes "
                 + "onto. Both FILE NAMES must be settings too, not literals."),

        new DocLibraryPathDefinition(
            EventLogoPack, "Event/LogoPack", "Event", DocLibraryDirection.Read,
            Notes: "Every logo reference in the product resolves here, including the zipped pack."),

        new DocLibraryPathDefinition(
            VenueRoot, "Venue", "Venue", DocLibraryDirection.Read,
            Notes: "The parent of the venue galleries. The venue image proxy maps an ALLOWLISTED "
                 + "key to a subfolder beneath this root — the allowlist is a security boundary "
                 + "(it is what stops a crafted key reading an arbitrary folder), so it stays in "
                 + "code; only the root is configuration."),

        new DocLibraryPathDefinition(
            VenueGoodToKnow, "Venue/Good to know", "Venue", DocLibraryDirection.Read,
            Notes: "⚠️ Mixed extension casing (.JPG/.PNG/.png/.jpg) and spaces in names — matching "
                 + "MUST be case-insensitive. Contains 'SesssonFeedback.jpg' (three s's), which is "
                 + "the real name and stays."),

        new DocLibraryPathDefinition(
            VenueWayfinding, "Venue/Wayfinding", "Venue", DocLibraryDirection.Read,
            Notes: "Floor plans linked from event-info pages. Same casing hazard."),

        new DocLibraryPathDefinition(
            VolunteerPhotos, "Volunteers/Photo", "Volunteers",
            DocLibraryDirection.Upload | DocLibraryDirection.Delete,
            Notes: "Uploaded in volunteer sign-up. Subject to photo cleanup on deactivation."),
    };

    private static readonly Dictionary<string, DocLibraryPathDefinition> ByKey =
        All.ToDictionary(d => d.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>The definition for a key, or null when the key is not registered.</summary>
    public static DocLibraryPathDefinition? Find(string key) =>
        string.IsNullOrWhiteSpace(key) ? null : ByKey.GetValueOrDefault(key);

    /// <summary>Keys that MUST resolve for the host to be considered correctly configured.</summary>
    public static IEnumerable<DocLibraryPathDefinition> Required =>
        All.Where(d => d.Status == DocLibraryPathStatus.Active);
}
