namespace CommunityHub.Core.Domain;

/// <summary>
/// §1187 — THE SIX THINGS THE CAMPAIGN ANNOUNCES, as the operator names them.
/// </summary>
/// <remarks>
/// <para>🔑 <b>Finer than <c>SoMeTemplateKind</c>, and that is the whole reason it exists.</b> Type 2
/// is one template kind and THREE categories: operator 2026-09-12 — <i>"master class start date is a
/// category of technical sessions. they runs fist starting from 14. sept. and other technical
/// sessions (excluding ask the experts) runs from 28. sept."</i> A sponsor's own speaker session is a
/// third, because it is announced twice where an ordinary session runs once.</para>
///
/// <para>⚠️ Ask-the-Experts is NOT a category: it is never announced at all (excluded by type and by
/// his title patterns), and a category nobody can schedule would be a control that does nothing —
/// which is the §1178 defect this whole model exists to stop repeating.</para>
/// </remarks>
public enum SoMeAnnouncementCategory
{
    /// <summary>Type 1 — one post per speaker track.</summary>
    SpeakerTracks = 1,

    /// <summary>Type 2 — the master classes, announced together and earliest.</summary>
    MasterClasses = 2,

    /// <summary>Type 2 — keynotes, technical sessions and panels.</summary>
    TechnicalSessions = 3,

    /// <summary>Type 2 — a sponsor's own speaker session. Announced twice, unlike an ordinary one.</summary>
    SponsorSpeakerSessions = 4,

    /// <summary>Type 3 — one post per sponsor tier.</summary>
    SponsorTiers = 5,

    /// <summary>Type 4 — one post per sponsor company.</summary>
    Sponsors = 6,

    /// <summary>
    /// Type 5 — event posts. ⚠️ Cadence and start are NOT his to set here: the dates come from the
    /// imported deck (§828.7/§834.4) and are used as written. Only the END date applies, as the
    /// cut-off past which a dated post is skipped.
    /// </summary>
    EventPosts = 7,
}

/// <summary>
/// 🔴 §1187 — ONE RULE PER CATEGORY: how many rounds, and between which two dates.
/// </summary>
/// <remarks>
/// <para>Operator 2026-09-12: <i>"i think we need to add all the rules inside the some settings as it
/// is becoming too complex"</i> · <i>"basically we define the rules like start date, end date,
/// cadence inside the some settings and the some planner must recalculate if they are changed"</i> ·
/// <i>"go, category list is right, fold in the frequency page"</i>.</para>
///
/// <para>🔴 <b>What this replaces, and why it had to.</b> In one day the announcement dates grew to
/// TEN single-purpose columns — <c>SpeakerAnnouncementFrom</c>, <c>SpeakerTracksRound2From</c>,
/// <c>SpeakerTracksRound3From</c>, <c>MasterClassAnnouncementFrom</c>,
/// <c>SessionAnnouncementFrom</c>, <c>SponsorAnnouncementFrom</c>, <c>SponsorRound2From</c>,
/// <c>SponsorCategoryRound1From</c>, <c>SponsorCategoryRound2From</c>,
/// <c>EventPostWindowEndsOn</c> — one per type-and-round, with the round COUNT on a separate table
/// and a separate page. Every new requirement added a column, and the eleventh gap appeared before
/// the tenth was finished. <b>That shape does not converge</b>, and he said so:
/// <i>"it is becoming too complex"</i>.</para>
///
/// <para>🔑 <b>The rounds are SPREAD between the two dates.</b> Round <i>i</i> of <i>n</i> opens at
/// <c>start + (i-1)·(end-start)/n</c>, so three rounds between late September and the event give an
/// announcement, a reminder and a final push without anyone naming three dates. ⚠️ This is
/// deliberately an approximation of the per-round dates it replaces — he chose the simpler model, and
/// a per-round override can be added later if a category ever needs one.</para>
///
/// <para>🔒 <b>A rule can only ever DELAY a post, never hurry one.</b> Every readiness gate still
/// applies on top: a sponsor waits for their logo and graphic (§854/§846), a session for its abstract
/// and artwork (§926), a track for its line-up to settle (§925). The window says when the category is
/// allowed to start, not that anything is ready.</para>
///
/// <para>⚠️ <b>Absent means the shipped default</b>, exactly as <c>SoMeCadenceSetting</c> worked — an
/// edition that never opens the page behaves as it always did, and there is no seeding step to
/// forget. Rows are created from the existing values the first time the page or the planner asks.</para>
/// </remarks>
public class SoMeCategoryRule
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    public SoMeAnnouncementCategory Category { get; set; }

    /// <summary>
    /// Whether this category is announced at all. False stops it WITHOUT deleting anything — the
    /// posts it has already produced stay exactly as they are (§824.21a).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How many times each subject in this category is announced. 0 is equivalent to disabling it;
    /// the planner clamps rather than trusting it blindly.
    /// </summary>
    public int Rounds { get; set; } = 1;

    /// <summary>
    /// The day this category may start being announced. Null = as soon as each subject is ready.
    /// </summary>
    public DateOnly? StartsOn { get; set; }

    /// <summary>
    /// The last day it may be announced. Null = the day before the event.
    /// </summary>
    /// <remarks>
    /// 🔑 This is the half that never existed as a setting. The only end-date rule in the engine was
    /// a <c>-21 days</c> constant for sponsor speaker sessions whose comment claimed a 14-day
    /// guarantee the forward search never actually made — the search ceiling was the event start.
    /// Making it a real, per-category value is what turns that comment into a rule.
    /// </remarks>
    public DateOnly? EndsOn { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }
    public string? LastUpdatedByEmail { get; set; }
}
