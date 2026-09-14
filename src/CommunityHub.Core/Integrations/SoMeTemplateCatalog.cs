namespace CommunityHub.Core.Integrations;

/// <summary>
/// §824.1 — the five SoMe announcement categories, numbered exactly as the operator numbered them.
/// </summary>
/// <remarks>
/// 🔒 The numbers are HIS ("Type 1: SpeakerTracks … Type 5: Event posts"). Keeping them means a
/// conversation about "type 4" and a row in the database mean the same thing without a translation
/// table in someone's head. Distinct from <see cref="Domain.SoMePostType"/>, which is the older
/// queue-row discriminator (Sponsor/Speaker/AdHoc) and whose values are already persisted.
/// </remarks>
public enum SoMeTemplateKind
{
    /// <summary>Type 1 — one post per TRACK, listing that track's speakers. Scheduled 2×.</summary>
    SpeakerTracks = 1,

    /// <summary>Type 2 — one post per SESSION. Master classes → technical → panels → sponsor sessions.</summary>
    Session = 2,

    /// <summary>Type 3 — one post per sponsor TIER, listing that tier's companies. Scheduled 2×.</summary>
    SponsorCategory = 3,

    /// <summary>Type 4 — one post per SPONSOR company. Scheduled 2×.</summary>
    Sponsor = 4,

    /// <summary>Type 5 — a general event post, graphic + text.</summary>
    EventPost = 5,
}

/// <summary>
/// §824.2C — the shipped DEFAULT body for each of the five post types.
/// </summary>
/// <remarks>
/// <para>These are modelled line for line on the three ELDK26 posts the operator supplied (§824.4),
/// because those samples are the real specification: they are what his audience has already seen, and
/// the structure in them (the ✨-wrapped headline, the tag block, the trailing "Organizers:" line) is
/// the house style rather than decoration.</para>
///
/// <para>🔑 <b>Defaults, not law.</b> §824.2C requires an EDITOR — he must be able to change any of
/// this per edition without a deploy. This catalog is the starting content that editor opens with,
/// exactly as <c>EmailTemplateCatalog</c> ships defaults the Email Templates page can override.</para>
///
/// <para>⚠️ <b>Deliberately NOT reproduced from the samples:</b> the <c>lnkd.in</c> shortened link
/// (that is what LinkedIn's own composer produces when a human pastes a URL — CEH posts the real
/// <c>{EventSystemUrl}</c>, §824.8b), and the ELDK26 tag list (superseded by <c>{EventTags}</c>).</para>
/// </remarks>
public static class SoMeTemplateCatalog
{
    /// <summary>Blank line between blocks — LinkedIn renders a single newline as a tight wrap.</summary>
    public const string Break = "\n\n";

    /// <summary>
    /// The trailing block every ELDK26 post ends with: the tags, then the organizer credit.
    /// </summary>
    /// <remarks>
    /// Shared rather than repeated in all five templates: it is one thing, and five copies is five
    /// places to forget when he changes it.
    /// </remarks>
    public const string Footer =
        "{EventTags}"
        + Break
        + "{EventNameShort} Organizers:"
        // 🔴 §935 — {Organizers}, which is HIS spelling. Operator 2026-08-07: *"some refer to some
        // other {OrganizerLinkedUrl} link, which must be changed to {Organizer}"*.
        //
        // 🔑 The two are the SAME value — SoMePostComposer maps both to the organizer credit — so
        // this changes no published text. What it removes is a body that names one thing two ways:
        // 75 of 76 unpublished posts carried the long spelling purely because this line emitted it.
        + "\n{Organizers}";

    /// <summary>
    /// §1224 — the line that tags a session's speakers. One constant, because the migration that
    /// retro-fitted the stored session wordings and posts inserts exactly this text.
    /// </summary>
    public const string SessionSpeakersLine = "🎤 With {Speakers}";

    /// <summary>The shipped default body for one post type.</summary>
    public static string DefaultBody(SoMeTemplateKind kind) => kind switch
    {
        // Sample: "✨ Track Speakers: AI ✨" … "🤘 Meet our tech legends: <names>"
        SoMeTemplateKind.SpeakerTracks =>
            "✨ Track Speakers: {TrackName} ✨"
            + "\n{IntroText}"
            + Break
            + "🔗 Jump on board: {EventSystemUrl}"
            + Break
            // §1224 — {Speakers} (tagged), not {SpeakerNames} (plain). Operator 2026-09-14: "yes, tag
            // speakers in track posts too". His imported track wordings already did; this default —
            // what a new edition starts from — was the last untagged one.
            + "🤘 Meet our tech legends: {Speakers}"
            + Break
            + Footer,

        // Sample: "✨ Session Announcement: <title> ✨" … "🗓️ Mark it down, <dates>, at <venue>!"
        SoMeTemplateKind.Session =>
            "✨ Session Announcement: {SessionTitle} ✨"
            + Break
            + "{IntroText}"
            + Break
            // 🔴 §1224 — THE SPEAKERS ARE TAGGED. Operator 2026-09-14: *"the {speakers} were not
            // included, so none of the speakers were tagged"* — two master class posts went out that
            // day naming nobody. This body had no speaker token at all, and nor did any of the 38
            // imported session wordings. {Speakers} mentions whoever LinkedIn allows and names the
            // rest; it is REQUIRED (SoMeEmptyVariableGate), so a session with no resolvable speaker
            // is held rather than published with a bare "With" line.
            + SessionSpeakersLine
            + Break
            + "Don't miss out on all the fun - secure your FOMO-free seat NOW! 🚀👉 {EventSystemUrl}"
            + Break
            + "🗓️ Mark it down, {EventDates}, at {EventVenue}!"
            + Break
            + Footer,

        // Type 3 has no ELDK26 sample; built from the Type 4 shape, which is its closest relative.
        SoMeTemplateKind.SponsorCategory =>
            "✨ Our {SponsorTier} sponsors ✨"
            + Break
            + "{IntroText}"
            + Break
            + "{SponsorList}"
            + Break
            + "See them all at {EventSystemUrl} — {EventDates}, {EventVenue}."
            + Break
            + Footer,

        // Sample: "✨ Sponsor announcement: Truesec ✨" / "…as a Gold sponsor to the upcoming …"
        SoMeTemplateKind.Sponsor =>
            "✨ Sponsor announcement: {SponsorName} ✨"
            + "\nWe are very proud to announce {SponsorLinkedInUrl} as a {SponsorTier} sponsor to the "
            + "upcoming {EventDisplayName} on {EventDates}."
            + Break
            + "{SponsorSocialMediaCompanyDescription}"
            + Break
            + "Learn more at {SponsorWebsite}."
            // §884.3 — the sponsor's own people, on their own line after a BLANK one (operator
            // 2026-08-06). 🔒 §824.15's renderer drops a line whose variables all resolve empty, so a
            // sponsor with no mentionable contact simply has no "Tag:" line — it never publishes a
            // bare label. Both variables mention EVERY signer / coordinator, not just the first.
            + Break
            + "Tag: {SponsorSigner} {SponsorEventCoordinators}"
            + Break
            + "{SponsorHashtag} " + Footer,

        // Type 5 carries a graphic, and THE COPY IS HIS — imported from the event-post deck (§828).
        //
        // 🔴 §834.5: this template used to lead with "{IntroText}" and had NO placeholder for the
        // post body at all, so a composed Type 5 post was an AI intro, a link and a footer — the
        // words he actually wrote could not appear. {EventPostBody} is the post; everything else is
        // the frame around it.
        //
        // 🔒 {IntroText} is deliberately ABSENT here, unlike the other four. For types 1–4 a
        // generated intro introduces generated content, which is the point. Putting a generated
        // sentence above HIS finished copy would invert who is writing the post.
        // 🔴 §834.6 — IT ADDS ONLY THE ORGANIZER CREDIT, and deliberately NOT the shared Footer.
        //
        // Measured against the real deck once the first 45 Type 5 posts were composed on PROD:
        // ALL 27 of his posts already end with the #ELDK27 tag block, and 23 of 27 already carry
        // the event URL in their own words ("👉 Read the abstracts now: …"). Using the shared
        // Footer here printed the ENTIRE hashtag block TWICE and the link twice — visible only by
        // reading a composed post, never by a test of the template.
        //
        // The deck is FINISHED COPY. The one thing it never carries is the organizer credit line
        // (0 of 27), so that is the only thing this template adds.
        SoMeTemplateKind.EventPost =>
            "{EventPostBody}"
            + Break
            + "{EventNameShort} Organizers:"
            + "\n{Organizers}",   // §935 — his spelling, same value (see Footer above)

        _ => "{IntroText}" + Break + Footer,
    };

    /// <summary>A short human label per type, for the editor's template picker.</summary>
    public static string Title(SoMeTemplateKind kind) => kind switch
    {
        SoMeTemplateKind.SpeakerTracks   => "Type 1 — Speaker tracks",
        SoMeTemplateKind.Session         => "Type 2 — Session announcement",
        SoMeTemplateKind.SponsorCategory => "Type 3 — Sponsor category (tier)",
        SoMeTemplateKind.Sponsor         => "Type 4 — Sponsor announcement",
        SoMeTemplateKind.EventPost       => "Type 5 — Event post",
        _ => kind.ToString(),
    };

    /// <summary>
    /// Every placeholder this catalog can emit, so the editor can list them and a renderer can be
    /// checked against the set rather than against whatever a template happens to mention today.
    /// </summary>
    public static readonly IReadOnlyList<string> KnownVariables = new[]
    {
        // --- his six, named in §824.3 ---------------------------------------------------
        "{SponsorLinkedInUrl}", "{SponsorSocialMediaCompanyDescription}", "{OrganizerLinkedInUrls}",
        "{IntroText}", "{EventSystemUrl}", "{EventTags}",
        // --- the ones the ELDK26 samples require but he did not list ---------------------
        // Named here rather than invented at render time: a template that mentions a variable the
        // resolver does not know renders the literal "{TrackName}" into a live post.
        "{TrackName}", "{SpeakerNames}", "{SessionTitle}", "{SessionAbstract}",
        // §908 — HIS names for two of the above, taken from the wording catalogs he wrote. Both
        // spellings resolve to one value; listing them here is what stops the template editor
        // flagging his own samples as using unknown placeholders.
        "{SpeakerTrack}", "{SessionTeaserTextAI}",
        // §858.16 — {Speakers} is {SpeakerNames} with real LinkedIn MENTIONS for whoever follows
        // the page, and plain names for the rest (LinkedIn will not mention a non-follower, so the
        // fallback is enforced by them, not chosen by us). {SpeakerNames} stays as the deliberately
        // untagged spelling for copy that does not want mentions — two spellings, one rule (§858.4).
        "{Speakers}",
        // 🔴 §935 — {Organizers} is to {OrganizerLinkedInUrls} exactly what {Speakers} is to
        // {SpeakerNames}: his spelling for the same value, mentioned where LinkedIn allows it.
        //
        // ⚠️ IT WAS MISSING FROM THIS LIST, and the shipped-template test caught it the moment the
        // Footer started using it — `UnknownPlaceholders` would have let a literal "{Organizers}"
        // publish to the company page. That is the guard this list exists for, doing its job.
        "{Organizers}",
        // §884.3 — the sponsor's signer(s) and event coordinator(s), mentioned where LinkedIn allows
        // it and named in plain text otherwise. Measured follower rates: signers 7/14, coordinators
        // 6/15 — so roughly half of each will render as text, which is why the fallback is reported.
        "{SponsorSigner}", "{SponsorEventCoordinators}",
        // §885 — one call-to-action phrase drawn per post from the operator's own catalog, so a
        // campaign of 80+ posts does not close the same way every time. Rolled ONCE at creation and
        // stored on the post: varied across the campaign, fixed within a post.
        "{Action_catalog_random}",
        // §888.2 — his preferred names. Same values as {EditionCode} / {EventDisplayName}, which stay
        // registered until the stored bodies that use them are migrated.
        "{EventNameShort}", "{EventNameLong}",
        // §888.2 — typed on SoMe settings; there is no city/country on the Event to derive it from.
        "{EventVenueCityCountry}",
        "{SponsorName}", "{SponsorTier}",
        // §834.5 — Type 5's own copy, imported from his event-post deck (§828). Without these the
        // Type 5 template had no way to render the post he wrote.
        "{EventPostTitle}", "{EventPostBody}",
        "{SponsorList}", "{SponsorWebsite}", "{SponsorHashtag}",
        // 🔴 §933 — {EditionCode} IS RETIRED and is deliberately absent from this list. Operator
        // 2026-08-07: *"replace {EditionCode} to {EventNameShort} … i dont like that name and prefer
        // {EventNameShort} and {EventNameLong} instead"*. It named an internal concept (the edition
        // key) in a list of things a reader recognises — the event's short name and its long one.
        //
        // 🔒 It still RESOLVES (see SoMeVariableResolver) — retiring a token from the offered list
        // must never turn a body that already contains it into literal "{EditionCode}" on LinkedIn.
        // The stored bodies were rewritten by migration, so this is belt and braces for anything
        // pasted from an old post.
        "{EventDisplayName}", "{EventDates}", "{EventVenue}",
    };
}
