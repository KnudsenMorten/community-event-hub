namespace CommunityHub.Core.Domain;

/// <summary>
/// Per-edition organizer settings for the LinkedIn company-page social-media
/// (SoMe) scheduling queue (REQUIREMENTS §19). One row per edition (upserted).
///
/// <b>Secret-clean:</b> this row holds only OPERATOR CONFIG that is NOT a secret —
/// the on/off toggle, the company-page URL / organization id (plain config, like
/// the Sessionize endpoint id), the designated speaker pre-alert organizer, and
/// the notification-array. The LinkedIn OAuth <b>access token is a SECRET</b> and
/// is NEVER stored here — it is read from the existing secret/config mechanism
/// (Key Vault, secret name <c>linkedin-some-access-token</c>) by the live
/// publisher only; placeholders only in committed files.
/// </summary>
public class SoMeSettings
{
    public int Id { get; set; }

    /// <summary>The edition these settings belong to. One row per edition.</summary>
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>
    /// Master enable/disable for SoMe posting (REQUIREMENTS §19). When false the
    /// dispatcher publishes nothing even if the publisher seam is wired. Defaults
    /// false — nothing posts until an organizer turns it on.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The LinkedIn company-page URL OR organization id the queue posts to
    /// (operator config, NOT a secret — like the Sessionize endpoint id). Real
    /// value entered in the UI / private config; committed files keep a
    /// placeholder. The live publisher resolves the organization URN from this.
    /// </summary>
    public string? CompanyPageUrlOrOrgId { get; set; }

    /// <summary>
    /// The designated organizer who receives the T-5-minute speaker pre-alert
    /// (REQUIREMENTS §19) so they can manually insert the speaker's real LinkedIn
    /// handle before a Speaker post publishes (the API can't tag external
    /// speakers). A single email address; blank disables the pre-alert.
    /// </summary>
    public string? SpeakerPreAlertOrganizerEmail { get; set; }

    /// <summary>
    /// Comma/semicolon/newline-separated list of organizer emails who get a
    /// notification when a post publishes (the "SoMe notification email array").
    /// Empty = no publish notifications even when <see cref="NotifyOnPublish"/>
    /// is on.
    /// </summary>
    public string? NotificationEmails { get; set; }

    /// <summary>
    /// Toggle for the publish-notification array (REQUIREMENTS §19). When false,
    /// no "a post was published" email is sent regardless of
    /// <see cref="NotificationEmails"/>. The T-5-minute speaker pre-alert is a
    /// separate, always-on-when-an-organizer-is-set channel.
    /// </summary>
    public bool NotifyOnPublish { get; set; } = true;

    /// <summary>
    /// §842.7/§843.3 — <b>THE EVERYDAY RHYTHM</b>: how many posts a normal day carries.
    /// </summary>
    /// <remarks>
    /// 🔒 DEFAULT 2. ⚠️ The name says "Max" for history; since §843.3 it is the NORMAL number, and
    /// <see cref="ExceptionPostsPerDay"/> is the ceiling. Measured on the real campaign, whatever
    /// this is set to becomes what almost every day looks like — at 2, 100% of days held exactly 2 —
    /// so changing it changes the page's everyday appearance, not just its capacity.
    /// </remarks>
    public int MaxPostsPerDay { get; set; } = 2;

    /// <summary>
    /// §843.3 — the ceiling a day may reach for posts that <b>cannot otherwise be placed</b>.
    /// </summary>
    /// <remarks>
    /// <para>🔑 Operator 2026-08-05: <i>"i am worried that the planner will post 3 by default … we
    /// had a few days with exceptions as we ended with more posts than we had capacity for"</i>. His
    /// busier ELDK26 days were days they had run OUT of room, not a chosen cadence — so this is an
    /// EXCEPTION, granted only to what the normal rhythm could not fit.</para>
    ///
    /// <para>🔒 Set equal to <see cref="MaxPostsPerDay"/> to forbid exceptions entirely. Capped in
    /// use by the number of <c>SoMeSchedulePlanner.PreferredTimes</c> (4): a further post would have
    /// to share a minute with another or break his 08:00–16:00 rule.</para>
    /// </remarks>
    public int ExceptionPostsPerDay { get; set; } = 3;

    /// <summary>
    /// §851 — the earliest date a SPEAKER-derived post may be scheduled: Type 1 (speaker tracks) and
    /// Type 2 (sessions). Null = no gate.
    /// </summary>
    /// <remarks>
    /// <para>🔑 Operator 2026-08-05: <i>"we run the call for speaker process now, and we wont have
    /// the complete list of speakers until 7th of sept 2026 … call for speakers ends 31 aug 2026 and
    /// then we spend 1 week deciding who is selected. planner must adjust to this"</i>. For ELDK27
    /// the value is <b>2026-09-07</b>.</para>
    ///
    /// <para>🔒 A SETTING, not a constant — the mechanism belongs to the edition, the date belongs to
    /// this one. A later edition has a different CfS deadline, and a hardcoded 2026 would gate
    /// nothing for it.</para>
    ///
    /// <para>⚠️ It gates SCHEDULING, not planning-in-principle: the posts are still created, they
    /// simply cannot land before the speakers are known. A track post lists its speakers, so
    /// publishing one earlier would announce a line-up that does not exist yet.</para>
    /// </remarks>
    public DateOnly? SpeakerAnnouncementFrom { get; set; }

    // --- §824.16: the three edition-level values every template ends with --------------------
    // They live HERE, per edition and editable, rather than in code: the tag block and the
    // organizer credit differ between editions (ELDK26's list is not ELDK27's, §824.3), and a
    // wording change to a line that appears on EVERY post must not need a deploy.

    /// <summary>
    /// §824.3 <c>{EventSystemUrl}</c> — the link every post points at, e.g.
    /// <c>https://eldk27.expertslive.dk</c>.
    /// </summary>
    /// <remarks>
    /// 🔒 The RAW url, not a shortener. The <c>lnkd.in</c> links in the ELDK26 samples are what
    /// LinkedIn's own composer produces when a human pastes a URL; reproducing them would put a
    /// second system in charge of the event's address (§824.8b).
    /// </remarks>
    public string? EventSystemUrl { get; set; }

    /// <summary>§824.3 <c>{EventTags}</c> — the standing hashtag block, one line.</summary>
    public string? EventTags { get; set; }

    /// <summary>
    /// §824.3 <c>{OrganizerLinkedInUrls}</c> — the four organizers as the credit line shows them.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Names, not mentions — measured, not assumed.</b> A LinkedIn mention needs the person's
    /// member URN, which cannot be derived from a public profile URL, and §824.14c confirmed even
    /// <c>/v2/userinfo</c> is denied to this app. His own ELDK26 posts carry the four as plain text
    /// for the same reason: LinkedIn's composer resolves an "@" a human types, and an API post has no
    /// such affordance.
    /// </remarks>
    public string? OrganizerCredits { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? LastUpdatedByEmail { get; set; }

    /// <summary>The notification emails split into a clean, non-blank list.</summary>
    public IReadOnlyList<string> NotificationEmailList =>
        SplitAddresses(NotificationEmails);

    /// <summary>Split a comma/semicolon/newline-separated address list into clean entries.</summary>
    public static IReadOnlyList<string> SplitAddresses(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? Array.Empty<string>()
            : raw.Split(new[] { ',', ';', '\n', '\r' },
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .Distinct(StringComparer.OrdinalIgnoreCase)
                 .ToArray();
}
