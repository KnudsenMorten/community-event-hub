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
    /// §918 — approve template-built posts automatically, once they are far enough out.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-06: <i>"as they run from the templates, i see no reason why we should
    /// not auto-approve them"</i> — with 79 posts held and one approved, approving them one at a
    /// time was not a workflow.</para>
    /// <para>🔒 <b>OFF by default</b>, and it stays a choice: §824.8 Q2 ("publish only after your
    /// approval") is the safety model, and turning it on is him deciding to delegate that approval
    /// to a rule he can read.</para>
    /// </remarks>
    public bool AutoApproveEnabled { get; set; }

    /// <summary>
    /// §918 — how many days ahead a post must be scheduled before it may auto-approve.
    /// </summary>
    /// <remarks>
    /// <para>🔴 <b>This is the guard that stops §889.1 happening at scale.</b> He approved #495, its
    /// 09:00 slot had already passed, and it published <b>thirty seconds later</b>. Auto-approving a
    /// whole queue with no lead time would do that to every post whose time has gone by — a burst of
    /// publications, at once, with no chance to look.</para>
    /// <para>🔒 The lead time is also the REVIEW WINDOW: nothing can publish for at least this many
    /// days after it is approved, so there is always time to catch and un-approve it.</para>
    /// </remarks>
    public int AutoApproveLeadDays { get; set; } = 7;

    /// <summary>
    /// §927 — session TITLE patterns that must never be announced. One per line, <c>*</c> wildcard.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-06: <i>"exclude option with title filters must be build like ask the
    /// experts*"</i>. His case: the Ask-the-Experts sessions are a format, not a talk — they have no
    /// abstract to announce and he covers them in one Type 5 post of his own.</para>
    /// <para>🔑 <b>A setting, not a heuristic.</b> §909 refused to infer "test" from a title because
    /// nobody had chosen that rule; this one he writes and can see, so a wrong exclusion is one edit
    /// away from being right. Matching lives in <see cref="Integrations.SoMeTitleExclusions"/>.</para>
    /// <para>🔒 Empty means <b>exclude nothing</b> — never "match everything".</para>
    /// </remarks>
    public string? ExcludedSessionTitlePatterns { get; set; }

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

    /// <summary>
    /// §928 — the date the MASTER CLASS announcements start. Null = no window; master-class posts
    /// are spread across the campaign like any other session.
    /// </summary>
    /// <remarks>
    /// <para>🔑 Operator 2026-08-06: <i>"reschedule master classes to last week august"</i>. For
    /// ELDK27 the value is <b>2026-08-24</b> — the Monday of the last full week of August.</para>
    ///
    /// <para>🔒 <b>This is a WINDOW OPENING, not a floor, and the difference is the whole feature.</b>
    /// <see cref="SpeakerAnnouncementFrom"/> says "not before this" and lets the generic spread
    /// choose the day; that is what put the master classes one at a time from August to February.
    /// He asked for them TOGETHER, in one named week — so the date is an INSTRUCTION (§908's
    /// explicit round), and the posts fill forward from it as densely as
    /// <see cref="MaxPostsPerDay"/> allows instead of being spread.</para>
    ///
    /// <para>⚠️ It moves posts he has ALREADY APPROVED, in BOTH directions — a master class sitting
    /// in December comes back to August. That is deliberate and it is the half §928 warned about:
    /// auto-approval (§918) freezes a post against re-planning, so a window that only steered NEW
    /// posts would leave the queue disagreeing with the rule that produced it (§901). Nothing is
    /// re-composed and nothing loses its approval; only the slot changes.</para>
    ///
    /// <para>🔒 A window can only ever be as dense as the day ceiling: master classes do not evict
    /// the sponsor and event posts already holding slots that week, they fill around them. Nine
    /// master classes at <c>MaxPostsPerDay = 2</c> need five weekdays, so a busy week pushes the
    /// tail into the following one rather than breaking §843.3.</para>
    /// </remarks>
    public DateOnly? MasterClassAnnouncementFrom { get; set; }

    /// <summary>
    /// §925.2 — the day the Call for Speakers CLOSES. Null = the intake is not modelled and track
    /// readiness falls back to §925's session-arrival signal alone.
    /// </summary>
    /// <remarks>
    /// <para>🔑 Operator 2026-08-05: <i>"call for speakers ends 31 aug 2026 and then we spend 1 week
    /// deciding who is selected"</i>. For ELDK27 the value is <b>2026-08-31</b>.</para>
    ///
    /// <para>🔴 <b>This is what §925's settle signal was missing, and the gap was measured rather
    /// than reasoned about (§925.1).</b> "This track's newest session is six weeks old" was read as
    /// <i>the line-up has finished</i> when it actually meant <i>the intake has not started</i>: all
    /// eight tracks held only their confirmed master classes from June, so every one of them scored
    /// as SETTLED while the CfS was still open. A quiet period cannot tell a batch that has ENDED
    /// from one that has not BEGUN — the two look identical from inside a single track.</para>
    ///
    /// <para>🔑 <b>The missing fact is edition-wide, and no track can know it.</b> Whether the intake
    /// has landed is a property of the whole import, so the settle signal now measures the newest
    /// session in the EDITION as well as in the track, and neither can be earlier than this date.
    /// A track's own quiet period still applies on top, so a track that keeps receiving stragglers
    /// after the wave waits longer than one that does not — §925's per-track behaviour is refined,
    /// not replaced.</para>
    ///
    /// <para>⚠️ <b>The honest residual limit.</b> This makes the campaign wait for the intake; it
    /// cannot make a broken import produce one. If the Sessionize sync were dead, the tracks would
    /// still become announceable a settle period after this date, holding only whatever CEH already
    /// had. That failure is a silent job, and silent jobs are what the job-silence alerting exists to
    /// catch — it is not something a scheduling rule can detect from the inside.</para>
    ///
    /// <para>🔒 Deliberately NOT "the import job has run since this date". A run that imported
    /// NOTHING is not evidence that the line-up arrived, so a run marker would answer a question
    /// nobody asked. The arrival of sessions is the fact; the job running is only a rumour of it.</para>
    /// </remarks>
    public DateOnly? CallForSpeakersClosesOn { get; set; }

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

    /// <summary>
    /// §885 <c>{Action_catalog_random}</c> — a prestaged list of call-to-action phrases (text and
    /// emoji), ONE PER LINE, from which each post is given one at random.
    /// </summary>
    /// <remarks>
    /// <para>🔑 <b>The point is variety across the campaign</b>: 80+ planned posts should not all
    /// close with the same sentence. Blank lines are ignored, so the box can be grouped readably.</para>
    /// <para>🔒 <b>The phrase is rolled ONCE per post and stored</b> on
    /// <see cref="SoMePost.ActionPhrase"/> — never re-rolled at render time. A variable that changed
    /// on every render would make the preview disagree with what publishes, which is exactly the
    /// defect §863.4 was built to stop.</para>
    /// <para>⚠️ Phrases must avoid <c>@ [ ] ( )</c> — §326k escapes them as little-text-format
    /// control characters, so they would publish with visible backslashes.</para>
    /// </remarks>
    public string? ActionCatalog { get; set; }

    /// <summary>
    /// §888.2 <c>{EventVenueCityCountry}</c> — the city and country as a post should read them,
    /// e.g. <c>Copenhagen, Denmark</c>.
    /// </summary>
    /// <remarks>
    /// 🔒 <b>Stored, not derived.</b> <c>Event.VenueName</c> holds only "Bella Center Copenhagen" —
    /// no city and no country — so splitting one out of it would be a GUESS printed on a live post,
    /// which §824.16 forbids outright. He types it once instead.
    /// <para>It lives here beside <c>{EventTags}</c> rather than on the Event because it is post
    /// COPY: the wording is a social-media choice ("Copenhagen, Denmark" vs "København"), not a fact
    /// about the venue (operator 2026-08-06: *"its own field on some settings"*).</para>
    /// </remarks>
    public string? EventVenueCityCountry { get; set; }

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
