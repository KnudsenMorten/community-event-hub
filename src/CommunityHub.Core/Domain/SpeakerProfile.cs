namespace CommunityHub.Core.Domain;

/// <summary>
/// Who pays for this speaker's appreciation package (hotel/travel/swag/etc).
/// Drives the speaker-hat order entitlements in
/// <see cref="Entitlements.OrderEntitlements"/>.
/// </summary>
public enum SpeakerFunding
{
    /// <summary>Organizer-supported speaker: the full speaker appreciation set.</summary>
    Supported = 0,

    /// <summary>
    /// A speaker brought + paid for by a sponsor (e.g. a sponsor-session
    /// speaker): only the shared social items (dinner + main-day lunch), no
    /// polo/swag/award/hotel/travel.
    /// </summary>
    SponsorSelfFunded = 1,

    /// <summary>
    /// An organizer who is also speaking: contributes NOTHING from the speaker
    /// hat (they are excluded from speaker tallies); their Organizer-role
    /// entitlements still apply.
    /// </summary>
    Organizer = 2,
}

/// <summary>
/// Per-participant speaker profile. Used for the Speaker role only. Splits into:
///  - "Hub-collected" fields (the participant fills the speaker form):
///      Accreditation, IsFirstTimeSpeaker, Country, Gender
///  - "Sessionize-imported" fields (organizer uploads the Sessionize export):
///      Blog, LinkedIn, Twitter, Tagline, Biography
/// The Sessionize import only overwrites the *imported* fields. The
/// Hub-collected fields are authoritative -- a Sessionize import never
/// touches them, even when the Sessionize export ships its own Country /
/// Gender columns.
/// </summary>
public class SpeakerProfile
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    public int ParticipantId { get; set; }
    public Participant Participant { get; set; } = null!;

    // --- Hub-collected (authoritative; never touched by Sessionize import) -
    /// <summary>One of: "Microsoft Employee", "Microsoft Expert", "Microsoft MVP", "Microsoft Regional Director", "None".</summary>
    public string? Accreditation { get; set; }

    /// <summary>True if this is the participant's first time speaking at this event series.</summary>
    public bool? IsFirstTimeSpeaker { get; set; }

    public string? Country { get; set; }

    /// <summary>"Male" / "Female" / "Non-binary" / "Prefer not to say".</summary>
    public string? Gender { get; set; }

    /// <summary>True if the speaker is delivering a session on Pre-day (Master Class / workshop day).</summary>
    public bool SpeakingPreDay { get; set; }

    /// <summary>True if the speaker is delivering a session on the main conference day.</summary>
    public bool SpeakingMainDay { get; set; }

    /// <summary>
    /// Who funds this speaker's appreciation package — drives the speaker-hat
    /// order entitlements (<see cref="Entitlements.OrderEntitlements"/>).
    /// Defaults to <see cref="SpeakerFunding.Supported"/> (organizer-supported,
    /// full speaker set).
    /// </summary>
    public SpeakerFunding SpeakerFunding { get; set; } = SpeakerFunding.Supported;

    // --- Publish gate (hub-collected; the HARD GATE for the Backstage bio sync) -
    /// <summary>
    /// Organizer approval to make this speaker PUBLIC / visible in Zoho Backstage.
    /// DEFAULTS TO FALSE and stays false until the lineup is selected and an
    /// organizer explicitly flips it.
    ///
    /// This is the hard gate for the outbound speaker-bio sync
    /// (<see cref="Integrations.IBackstageSpeakerBioApi"/>): a speaker's bio is
    /// only ever pushed to Backstage's <i>public/visible</i> state when this is
    /// true. While false (the default for everyone right now), the sync pushes the
    /// bio to a <i>draft / hidden</i> state only — it NEVER exposes the speaker
    /// publicly. A Sessionize re-import never writes this field (it is
    /// hub-collected, like Accreditation), so an approval survives every re-import.
    /// </summary>
    public bool SelectedForPublish { get; set; }

    // --- Contact email override (hub-collected; never touched by import) ---
    /// <summary>
    /// Optional speaker-chosen address for ALL outbound mail and calendar
    /// invites. Many speakers do not use the email they registered with on
    /// Sessionize (the community email) for day-to-day calendar/mail, so they
    /// can set a preferred address here.
    ///
    /// The Sessionize / community email (<see cref="Participant.Email"/>) stays
    /// the IDENTITY and match key: it is what login and the Sessionize re-import
    /// match on, so it must never change. This override only redirects WHERE
    /// mail and .ics invites are addressed — see <see cref="EffectiveEmail"/>.
    ///
    /// Null/blank = no override; the participant's Sessionize address is used.
    /// A Sessionize re-import never writes this field (it is hub-collected, like
    /// Accreditation / Country), so the override survives every re-import.
    /// </summary>
    public string? ContactEmailOverride { get; set; }

    /// <summary>
    /// The address ALL outbound speaker mail + .ics calendar feeds must use:
    /// the override when set, otherwise the participant's Sessionize/community
    /// address. Resolve via <see cref="EffectiveEmailFor"/> when only the
    /// participant + profile (possibly null) are in hand.
    /// </summary>
    public string EffectiveEmail =>
        string.IsNullOrWhiteSpace(ContactEmailOverride)
            ? (Participant?.Email ?? string.Empty)
            : ContactEmailOverride.Trim();

    /// <summary>
    /// Resolve the effective contact address for a participant given their
    /// (possibly null) speaker profile. This is the single routing rule used by
    /// every mail / calendar caller: <c>override ?? Sessionize email</c>. Works
    /// for non-speakers too (no profile ⇒ the Sessionize address), so a caller
    /// that does not know the role can use it uniformly.
    /// </summary>
    public static string EffectiveEmailFor(
        string sessionizeEmail, string? contactEmailOverride) =>
        string.IsNullOrWhiteSpace(contactEmailOverride)
            ? (sessionizeEmail ?? string.Empty)
            : contactEmailOverride.Trim();

    // --- Speaker bio (seeded from Sessionize, then OWNED by the speaker) ---
    // These five fields are imported from Sessionize AND editable by the speaker
    // on their own page. The delta sync only fills a field that is empty and has
    // NOT been edited by the speaker (see <see cref="SpeakerEditedFields"/>); the
    // organizer "Full import" button force-refreshes them all and clears the
    // speaker-edited set.
    public string? Tagline { get; set; }
    public string? Biography { get; set; }
    public string? Blog { get; set; }
    public string? LinkedIn { get; set; }
    public string? Twitter { get; set; }

    /// <summary>
    /// Speaker photo URL. Imported from the Sessionize <c>profilePicture</c>
    /// field and editable by the speaker. Treated as one of the bio fields for
    /// delta-sync / speaker-edited tracking.
    /// </summary>
    public string? PhotoUrl { get; set; }

    // --- Speaker Details (operator 2026-06-24 §26c) -----------------------
    /// <summary>The Sessionize speaker id (GUID) this profile imported from; the import match/dedup key.</summary>
    public string? SessionizeSpeakerId { get; set; }

    /// <summary>The Zoho Backstage speaker id once created there — drives UPDATE-vs-CREATE on Save &amp; Sync.</summary>
    public string? BackstageSpeakerId { get; set; }

    /// <summary>First name (from Sessionize, editable). FullName stays on the Participant.</summary>
    public string? FirstName { get; set; }
    /// <summary>Last name (from Sessionize, editable).</summary>
    public string? LastName { get; set; }

    /// <summary>
    /// Comma-separated MS-accreditation categories (e.g. MVP categories) that map to
    /// Zoho speaker "Skills" on Save &amp; Sync. Multi-select; hub-collected.
    /// </summary>
    public string? MvpCategories { get; set; }

    /// <summary>
    /// The SharePoint-stored copy of the speaker picture (relative path, e.g.
    /// <c>Speakers/speaker-42.jpg</c>). Set when the Sessionize <c>profilePicture</c>
    /// is fetched + uploaded on import; <see cref="PhotoUrl"/> remains the display URL.
    /// </summary>
    public string? PhotoSharePointPath { get; set; }

    /// <summary>
    /// Comma-separated set of bio field names the SPEAKER has edited in the hub
    /// (the per-field "dirty" set). A delta Sessionize re-import must NEVER
    /// overwrite a field listed here — the speaker's edit is authoritative. The
    /// organizer "Full import" button is the deliberate override: it re-seeds
    /// from Sessionize and clears this set.
    ///
    /// Field tokens are the <see cref="BioFields"/> constants
    /// (<c>tagline,biography,blog,linkedin,twitter,photourl</c>). Use
    /// <see cref="IsSpeakerEdited"/> / <see cref="MarkSpeakerEdited"/> /
    /// <see cref="ClearSpeakerEdited"/> rather than parsing the string by hand.
    /// </summary>
    public string? SpeakerEditedFields { get; set; }

    /// <summary>When the speaker last edited any of their own bio fields.</summary>
    public DateTimeOffset? BioLastEditedBySpeakerAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? LastSessionizeImportAt { get; set; }

    // --- Speaker-edited tracking helpers ----------------------------------
    /// <summary>The canonical bio-field tokens used in <see cref="SpeakerEditedFields"/>.</summary>
    public static class BioFields
    {
        public const string Tagline = "tagline";
        public const string Biography = "biography";
        public const string Blog = "blog";
        public const string LinkedIn = "linkedin";
        public const string Twitter = "twitter";
        public const string PhotoUrl = "photourl";

        public static readonly string[] All =
            { Tagline, Biography, Blog, LinkedIn, Twitter, PhotoUrl };
    }

    private static HashSet<string> ParseEdited(string? csv) =>
        new(
            (csv ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => t.ToLowerInvariant()),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>True if the speaker has edited this bio field in the hub.</summary>
    public bool IsSpeakerEdited(string field) =>
        ParseEdited(SpeakerEditedFields).Contains(field.Trim().ToLowerInvariant());

    /// <summary>
    /// Mark a bio field as speaker-edited (idempotent). Stamps
    /// <see cref="BioLastEditedBySpeakerAt"/> with <paramref name="now"/>.
    /// </summary>
    public void MarkSpeakerEdited(string field, DateTimeOffset now)
    {
        var set = ParseEdited(SpeakerEditedFields);
        if (set.Add(field.Trim().ToLowerInvariant()))
        {
            SpeakerEditedFields = string.Join(',', BioFields.All.Where(set.Contains));
        }
        BioLastEditedBySpeakerAt = now;
    }

    /// <summary>
    /// Clear ALL speaker-edited markers (the organizer "Full import" override
    /// re-seeds every bio field from Sessionize, so the dirty set is reset).
    /// </summary>
    public void ClearSpeakerEdited()
    {
        SpeakerEditedFields = null;
        BioLastEditedBySpeakerAt = null;
    }
}
