namespace CommunityHub.Core.Domain;

/// <summary>
/// What a queued LinkedIn company-page post is about (REQUIREMENTS §19). Drives
/// the compliance-aware tag set (sponsor posts tag the signer + event coordinator
/// + sponsor company; speaker posts tag organizers only — the LinkedIn API cannot
/// tag external speakers) and the optional auto-generated text/branding.
/// </summary>
public enum SoMePostType
{
    /// <summary>A post promoting a sponsor company.</summary>
    Sponsor = 0,

    /// <summary>A post promoting a speaker / their session.</summary>
    Speaker = 1,

    /// <summary>A one-off organizer-composed post (no linked sponsor/speaker).</summary>
    AdHoc = 2,
}

/// <summary>
/// §844.3 — which medium a post carries, and therefore which library <c>ImageRef</c> resolves from.
/// </summary>
/// <remarks>
/// 🔒 §844.2 (operator 2026-08-05: <i>"we prefer videos more than graphics, but fallback is graphics
/// for us"</i>) — video is the PREFERRED medium where one exists; a graphic is the fallback, not a
/// downgrade. Videos apply to EVENT, SESSION and SPONSOR posts only.
/// </remarks>
public enum SoMePostMediaKind
{
    /// <summary>A still image from the graphics library. The default, and what every pre-§844 row is.</summary>
    Graphic = 0,

    /// <summary>A video from the videos library, published natively (never as a link).</summary>
    Video = 1,
}

/// <summary>
/// §848.2 — WHOSE DECISION A POST IS: the planner's proposal, or his accepted schedule.
/// </summary>
/// <remarks>
/// <para>🔑 Operator 2026-08-05: <i>"i want to have 2 stages of planning: proposed and scheduled …
/// initialy the planner proposes a schedule, then i decide - and then things are locked down"</i>.</para>
///
/// <para>🔒 This is what lets the planner improve a campaign over months. §824.21a's "adds, never
/// curates" exists because the scheduler could not tell its own guess from his decision, so it had to
/// treat everything as precious. With the distinction explicit, a PROPOSAL can be re-planned freely
/// while a SCHEDULED post is untouchable — and his decisions become safer than they were, because
/// "locked" is now a property of the row rather than a convention.</para>
/// </remarks>
public enum SoMePostPlanState
{
    /// <summary>
    /// The planner's suggestion. It may be moved, replaced or dropped on any later run as sessions,
    /// sponsors and graphics arrive. Nothing here has been decided by a human.
    /// </summary>
    Proposed = 0,

    /// <summary>
    /// 🔒 HE HAS ACCEPTED IT. The planner never re-plans, moves or removes this post again — only he
    /// does, in the editor or the queue.
    /// </summary>
    Scheduled = 1,
}

/// <summary>The lifecycle status of one queued post.</summary>
public enum SoMePostStatus
{
    /// <summary>Scheduled, not yet published. The dispatcher publishes due, Active, Queued posts.</summary>
    Queued = 0,

    /// <summary>Successfully published to the LinkedIn company page.</summary>
    Published = 1,

    /// <summary>A publish attempt failed; the error is recorded (never silently dropped).</summary>
    Failed = 2,
}
