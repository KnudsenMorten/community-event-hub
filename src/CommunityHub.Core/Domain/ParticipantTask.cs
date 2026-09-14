namespace CommunityHub.Core.Domain;

/// <summary>State of a task in the hub.</summary>
public enum TaskState
{
    Open = 0,
    InProgress = 1,
    Done = 2
}

/// <summary>
/// WHY a task reached <see cref="TaskState.Done"/> (§332, closing the §326bj dashboard bug).
/// Null on every task closed by a person actually doing the work — this only ever labels the
/// closures the SYSTEM made on someone's behalf.
///
/// Deliberately a separate column rather than a fourth <see cref="TaskState"/> value: 150 sites
/// compare TaskState and 50 of them mean "not Done ⇒ still to do". An `Abandoned` enum member
/// would silently flip all 50 — and any one missed would put an abandoned task back into the
/// open lists AND back into the §81 due-date reminder track, i.e. mailing deadline chasers to
/// people who have LEFT the event. This column changes nothing by default; the completion
/// ratios opt in to excluding it.
/// </summary>
public enum TaskClosedReason
{
    /// <summary>Closed by the §253 deactivation cascade because the assignee left — the work
    /// was NOT done. Excluded from completion ratios; re-opened verbatim on re-activation.</summary>
    AbandonedOnDeactivation = 1,

    /// <summary>
    /// §1081 — RETIRED: the task predates the Get Started wizard and the wizard now owns the same
    /// obligation. The work was not abandoned and it was not necessarily done — the task simply
    /// stopped being the place the question is asked.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-13: <i>"i think that initial onboarding is legacy before we had get
    /// started wizard - i propose we delete that tasks for any existing + new sponsors. it is being
    /// replaced by get started"</i> — and then, on how: <i>"auto-close the task (never delete)"</i>.</para>
    ///
    /// <para>🔒 <b>Closed, never deleted.</b> The row carries its own history — when it was raised,
    /// its deadline, and for four companies the fact that somebody completed it. Deleting would
    /// throw that away irreversibly to achieve exactly what a close achieves reversibly. It is the
    /// §502 leaver rule applied to a task: retire it, keep the record.</para>
    /// </remarks>
    SupersededByGetStarted = 2,

    /// <summary>
    /// §1082 — the CATALOG stopped producing this task, so it was retired: a definition was removed,
    /// or a title was renamed and its old slug left this row behind. <b>The work was neither done nor
    /// abandoned — the question simply stopped being asked.</b>
    /// </summary>
    /// <remarks>
    /// <para>🔴 <b>This exists because the prunes used to DELETE.</b> On 2026-08-13 the sponsor orphan
    /// prune destroyed 15 rows, four of them completed, the first time a definition was removed.
    /// Operator: <i>"when you implement a guard, do we agree that you dont delete, but close them (as
    /// they were inactive)"</i> — and he is right, because the hub already says so everywhere else:
    /// §502 deactivates a leaver rather than deleting them, §253 tombstones, and his instruction on
    /// the onboarding task was *"auto-close (never delete)"*.</para>
    ///
    /// <para>🔒 <b>A retirement is not a completion, and must never be counted as one.</b> Use
    /// <see cref="Tasks.TaskClosure.IsSystemClosed"/> to filter these out of anything a participant
    /// reads as "what I have done" — otherwise a sponsor is congratulated for nine things they never
    /// saw.</para>
    /// </remarks>
    RetiredFromCatalog = 3,
}

/// <summary>
/// §670 / §676 — how a participant answered a two-button <c>Decision</c> task.
/// </summary>
/// <remarks>
/// 🔒 <see cref="Declined"/> is a POSITIVE answer, not an absence. "Not interested" completes the
/// task exactly as accepting does — operator §670: <i>"mark complete will automatically be set once
/// they choose one of the 2 buttons"</i> — while remaining distinguishable from never having
/// answered, which is what the organizer roll-up needs.
/// </remarks>
public enum TaskDecisionAnswer
{
    /// <summary>They opted in.</summary>
    Accepted = 1,

    /// <summary>They said no. Recorded, not merely dismissed.</summary>
    Declined = 2,
}

/// <summary>
/// A task or deliverable shown on a participant's hub. Covers volunteer
/// shift-jobs, speaker deadlines, and sponsor deliverables alike - the
/// reminder engine (Stage 6) reads these. Scoped to an edition by EventId.
/// </summary>
public class ParticipantTask
{
    /// <summary>
    /// §682 — the storage limit for <see cref="Description"/>, and the ONE place it is
    /// stated. The mapping and every seeder guard read this constant, so the check and
    /// the column can never drift apart.
    ///
    /// Raised from 2000 to 4000 on 2026-07-29 after the §675 DSV shipment copy (2135
    /// characters, supplied verbatim by the operator) overflowed the column and took the
    /// whole WooCommerce pull job down with it. 4000 matches VolunteerTask.Description,
    /// which was bumped for the same reason in §151: these are authored guidance bodies,
    /// not one-line labels.
    /// </summary>
    public const int DescriptionMaxLength = 4000;

    /// <summary>§682 — the storage limit for <see cref="Title"/>. Same contract as above.</summary>
    public const int TitleMaxLength = 300;

    public int Id { get; set; }

    // --- Edition scope ------------------------------------------------------
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    // --- Assignment ---------------------------------------------------------
    /// <summary>
    /// The participant responsible. Null = unassigned (e.g. a sponsor-company
    /// task not yet given to a specific contact - see CONTEXT.md section 9).
    /// </summary>
    public int? AssignedParticipantId { get; set; }
    public Participant? AssignedParticipant { get; set; }

    // --- Task content -------------------------------------------------------
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>Optional due date - drives deadline reminders.</summary>
    public DateOnly? DueDate { get; set; }

    public TaskState State { get; set; } = TaskState.Open;

    /// <summary>
    /// Mandatory vs optional. Mandatory tasks are part of the agreed
    /// deliverables (logo upload, booth layout, session description, etc.);
    /// optional ones are paid add-ons / nice-to-haves (attendee-bag insert,
    /// TV rental, app-game prize, etc.). UI surfaces an "Optional" badge
    /// when false; reminder cadence may differ in future. Default true so
    /// pre-existing tasks (which never had this flag) keep the safer
    /// "treat as deliverable" semantics.
    /// </summary>
    public bool IsMandatory { get; set; } = true;

    /// <summary>
    /// Optional source tag, e.g. which task-set generated it ("allSponsors",
    /// "boothPlatinum", a volunteer import). Useful for idempotent re-runs.
    /// </summary>
    public string? SourceKey { get; set; }

    /// <summary>
    /// For a sponsor task (one created from a WooCommerce order): the
    /// company id (the order's _cm_company_id). Lets a sponsor contact see
    /// and edit only their own company's tasks. Null for non-sponsor tasks.
    /// </summary>
    public string? SponsorCompanyId { get; set; }

    /// <summary>
    /// §648 — the wizard STEP this task is completed in, e.g. <c>"session"</c>. Null for a task
    /// finished some other way (an upload, or by hand).
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-07-29, looking at "Submit session description": *"in the new solution i
    /// need to have the forms + ability to enter up to 3 names+linkedin+session description+upload
    /// picture. we have this as part of get started at some point"*.</para>
    ///
    /// <para><b>He was right — the FORM already existed</b> (§292/§356: title, abstract, track and
    /// three speakers, each with name, e-mail, LinkedIn and a photo upload). What was missing was
    /// any way for a TASK to point at it, so the task still read *"Email it all to
    /// info@expertslive.dk"* and someone retyped it all by hand. This field is that pointer: the
    /// task renders a button straight to <c>/Forms/Wizard?step={FormStepKey}</c>.</para>
    /// </remarks>
    public string? FormStepKey { get; set; }

    /// <summary>
    /// §670 / §676 / §684.13 — the participant's answer to a <c>Decision</c> task, or null when they
    /// have not answered.
    /// </summary>
    /// <remarks>
    /// <para>🔒 <b>"Declined" is a REAL recorded state, and that is the entire point.</b> Operator
    /// §670: organizers need to know who declined versus who never answered — today both look like
    /// "not complete", so there is no way to tell a sponsor who said no from one who has not read the
    /// task. <see cref="TaskState.Done"/> alone cannot express it: both answers complete the task.</para>
    ///
    /// <para>A separate nullable column rather than a new <see cref="TaskState"/> member, for the
    /// same reason §332 gave <see cref="ClosedReason"/> its own column: ~150 sites compare TaskState
    /// and about a third of them mean "not Done ⇒ still to do". A new enum member would silently
    /// flip all of them.</para>
    /// </remarks>
    public TaskDecisionAnswer? DecisionAnswer { get; set; }

    /// <summary>When <see cref="DecisionAnswer"/> was given. Null while unanswered.</summary>
    public DateTimeOffset? DecisionAnsweredAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    /// §688.11 — WHO completed this task, when a person did.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-07-29: <i>"for a completed task, add relevant information like session
    /// name + completed tasks date and by whom"</i>. A SPONSOR task belongs to a COMPANY and several
    /// contacts can act on it, so "who" is the question actually asked when someone says "I thought
    /// you were doing that" — and <see cref="CompletedAt"/> alone cannot answer it.</para>
    ///
    /// <para>🔒 <b>NULL is honest, not missing.</b> It stays null for every row completed before this
    /// column existed, and for completions no person performed — a purchase-derived close (§687.8)
    /// is the system observing an order, not somebody ticking a box. <b>Nothing is backfilled:</b> a
    /// guessed name on a historical row would be worse than an empty one, because it would be
    /// believed.</para>
    /// </remarks>
    public int? CompletedByParticipantId { get; set; }

    /// <summary>
    /// §332: null for a task somebody actually COMPLETED; set when the system closed it on
    /// their behalf (today: the assignee was deactivated). Lets a completion ratio tell
    /// "40 of 390 done" from "40 of 390 done, 22 of which nobody ever did".
    /// </summary>
    public TaskClosedReason? ClosedReason { get; set; }
}
