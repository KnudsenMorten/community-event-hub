using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Tasks.Definitions;

/// <summary>
/// §684.15 — a named audience predicate, from a CLOSED set.
/// </summary>
/// <remarks>
/// <para>🔒 §456 was a real incident: an exhibitor-speaker saw every SPONSOR task. Audience must be
/// EXPLICIT and TESTABLE, never a side effect of which seeder happened to run. Today the same idea
/// is spread across <c>DeadlineAllowedByEntitlement</c>, tier lists in JSON, and <c>if</c> statements
/// in several seeders — three places to get it wrong and no way to ask the question.</para>
///
/// <para>A closed enum means "which tasks does a Gold exhibitor speaker see?" is answerable by a
/// unit test instead of by running two jobs.</para>
/// </remarks>
public enum TaskAudiencePredicate
{
    /// <summary>The company has a booth.</summary>
    HasBooth = 0,

    /// <summary>The company bought a speaking session.</summary>
    HasSponsorSession = 1,

    /// <summary>Speaker delivering a Master Class.</summary>
    IsMasterClassSpeaker = 2,

    /// <summary>
    /// Travelling from outside Denmark (§143).
    /// </summary>
    /// <remarks>
    /// 🔒 A speaker with NO country set yet counts as non-Denmark, i.e. they DO get the task. That
    /// asymmetry is deliberate and pre-dates this model: withholding a travel-reimbursement task
    /// from someone whose country we simply have not asked for yet would quietly cost them money.
    /// </remarks>
    NonDenmark = 3,

    // ── ENTITLEMENT GATES (§686.2 phase 2) ────────────────────────────────────
    // A logistics task is seeded only when the speaker is entitled to the underlying item. A
    // sponsor-self-funded or organizer-funded speaker must not be given a hotel/dinner/swag/lunch
    // task they cannot act on. Non-logistics tasks (the presentation uploads) are NEVER
    // entitlement-gated — every speaker presents.

    /// <summary>Entitled to a hotel room.</summary>
    EntitledToHotel = 4,

    /// <summary>Entitled to a seat at the appreciation dinner.</summary>
    EntitledToAppreciationDinner = 5,

    /// <summary>Entitled to swag / a speaker gift.</summary>
    EntitledToSwag = 6,

    /// <summary>Entitled to lunch on the pre-day.</summary>
    EntitledToLunchPreDay = 7,
}

/// <summary>Who a task is for. Roles AND every predicate must hold.</summary>
public sealed record TaskAudience(
    IReadOnlyList<ParticipantRole> Roles,
    IReadOnlyList<TaskAudiencePredicate> Requires)
{
    public static TaskAudience For(ParticipantRole role, params TaskAudiencePredicate[] requires) =>
        new(new[] { role }, requires);
}

/// <summary>§684.11 — where a task's due date comes from. It always LANDS on the same column.</summary>
/// <remarks>
/// Today the SOURCE differs (sponsor JSON <c>dueDate</c>, speaker JSON, C# literals) while the
/// DESTINATION is already one column: §400/§410, the overdue badges and the deadlines surface all
/// read <c>ParticipantTask.DueDate</c> generically. This is a source unification, NOT a deadline
/// redesign — nothing downstream changes.
/// </remarks>
public abstract record TaskDue
{
    private TaskDue() { }

    /// <summary>No deadline.</summary>
    public sealed record None : TaskDue;

    /// <summary>A fixed calendar date.</summary>
    public sealed record Fixed(DateOnly Date) : TaskDue;

    /// <summary>N days before the edition's start date.</summary>
    public sealed record EventMinus(int Days) : TaskDue;

    /// <summary>
    /// A named rule from the edition config's <c>deadlineRules</c> — the sponsor rules
    /// (<c>tvRequest</c>, <c>boothShipment</c>, …) that already resolve <c>eventMinus</c> and
    /// <c>contractPlus</c> bases. Keeps the DATES per edition while the definition stays in code.
    /// </summary>
    public sealed record FromConfig(string RuleName) : TaskDue;
}

/// <summary>§684.12 — reminder cadence. Selection only; the machinery is unchanged.</summary>
/// <remarks>
/// 🔒 <c>TaskReminderBuilder</c> already chases open <c>ParticipantTask</c> rows generically and
/// recipients resolve via <c>SponsorRecipientResolver</c> (ALL event coordinators, §597.3).
/// <b>Already unified — do not rebuild.</b>
/// </remarks>
public enum TaskReminderCadence
{
    /// <summary>The standard due-date chase.</summary>
    Standard = 0,

    /// <summary>Never chased.</summary>
    None = 1,
}

/// <summary>
/// §684.13 — HOW a task completes. Declared, not implied by which renderer you happened to land in.
/// </summary>
public abstract record TaskCompletion
{
    private TaskCompletion() { }

    /// <summary>Completes when a file exists for the task (§603). No manual tick (§602.5).</summary>
    public sealed record Artefact(string Kind) : TaskCompletion;

    /// <summary>Completes when the linked form/step is submitted (§648 <c>FormStepKey</c>).</summary>
    public sealed record Form(string StepKey) : TaskCompletion;

    /// <summary>
    /// Completes when the participant chose an option (§670/§676). 🔒 "Not interested" is a REAL
    /// recorded state, distinct from "never answered".
    /// </summary>
    public sealed record Decision(string Key) : TaskCompletion;

    /// <summary>
    /// §687.8 — completes when the sponsor has actually BOUGHT something in
    /// <paramref name="Category"/> from the webshop.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-07-29: <i>"it is important that some tasks cannot be completed without
    /// actual order or upload … then dont show the mark completed state and use this validation as
    /// the real tasks is completed or not … technically a furniture order should auto-close that
    /// task"</i>. Booth furniture is the case: the task's own copy warns that ordering is what
    /// RESERVES the furniture, so a sponsor who ticks it without ordering turns up to an empty
    /// booth.</para>
    ///
    /// <para>🔒 <b>Auto-close is NOT a lock.</b> A derived-Done task stays open, readable and
    /// actionable — <i>"still give sponsor the opportunity to see the state and go in a order more
    /// furnitures"</i>. Done means "you have some", never "you may not order more".</para>
    ///
    /// <para>🔒 <b>A failed webshop lookup must NEVER reopen a completed task.</b> An outage would
    /// otherwise silently un-complete every furniture task and re-chase every sponsor — the §664.1
    /// shape, at scale. "Could not check" leaves the stored state exactly as it is.</para>
    /// </remarks>
    /// <param name="Category">The WooCommerce product category, e.g. <c>"Booth Furniture"</c>.</param>
    public sealed record Purchase(string Category) : TaskCompletion;

    /// <summary>
    /// The participant ticks it. 🔒 <b>Must be the EXCEPTION and visible as such</b> — §600.3:
    /// completion is DERIVED wherever it can be. A self-declared "Mark complete" on an
    /// artefact-backed task is how a sponsor closed the wall task with no artwork uploaded.
    /// </summary>
    public sealed record Manual : TaskCompletion;
}

/// <summary>
/// The SharePoint upload folder a task provisions, when it receives a file.
/// </summary>
/// <remarks>
/// <para>🔒 <b>This is not a rendering concern, which is why it lives on the DEFINITION and not in
/// the body.</b> The order pull provisions a per-task SharePoint folder BEFORE rendering, persists
/// it as a <c>SponsorUploadLocation</c> so the upload watcher knows where to poll and whom to
/// notify, and exposes its anonymous edit link to the body as <c>{{<see cref="Placeholder"/>}}</c>.
/// Migrating the sponsor-wall task without carrying this across would have silently stopped
/// provisioning the folder — the task would still render, the upload button would still be there,
/// and nothing would be watching where the file landed.</para>
///
/// <para>Mirrors <c>SponsorTaskUploadDefinition</c> in the JSON exactly, so the migration is a move
/// rather than a redesign.</para>
/// </remarks>
/// <param name="Subfolder">Subfolder under <c>{root}/{companyName}/</c>, e.g. "SPONSORWALL".</param>
/// <param name="Placeholder">Placeholder key the body names, sans braces, e.g. "wallFolderUrl".</param>
/// <param name="NotifyEmails">Who is told when a file arrives. Empty = nobody.</param>
/// <param name="NotifySubject">Subject template; <c>{{companyName}}</c> resolves per notification.</param>
public sealed record TaskUpload(
    string Subfolder,
    string Placeholder,
    IReadOnlyList<string> NotifyEmails,
    string NotifySubject);

/// <summary>
/// §684.7 — ONE task definition. The ROLE is an attribute, not a type.
/// </summary>
/// <remarks>
/// <para>Per §602.4 (<i>"i dont care about this: … i would rather deploy again"</i>) definitions ship
/// in CODE: diffable, reviewable, covered by build-failing tests. Changing copy means a deploy, and
/// that is the settled decision (§684.25 Q1) — there is no organizer task editor.</para>
///
/// <para>ONE registry of these replaces <b>2 JSON files, 9+ seeders, 3 row renderers and 4 implied
/// completion semantics</b>, all of which already produce rows in the SAME table. The data was
/// always unified; everything that produced and rendered it was not. That gap is the whole
/// change.</para>
/// </remarks>
/// <param name="Key">
/// Stable identity, e.g. <c>"sponsor.tv-rental"</c>. 🔒 This replaces the ad-hoc
/// <c>sponsor:{companyId}:{title-slug}</c> <c>SourceKey</c> slugs. Per-participant rows still carry
/// a scoped <c>SourceKey</c> derived from this — see <c>SponsorTaskKeys</c>.
/// </param>
/// <param name="BodyRef">
/// Path under <c>config/tasks/&lt;edition&gt;/</c>, without the <c>.md</c> — e.g.
/// <c>"sponsor/tv-rental"</c>. Prose lives in FILES (§684.14): 20 000 characters belongs in neither a
/// C# literal nor JSON, and <c>config/content/&lt;edition&gt;/*.md</c> is the proven precedent.
/// </param>
public sealed record TaskDefinition(
    string Key,
    TaskAudience Audience,
    string Title,
    TaskDue Due,
    TaskReminderCadence Reminders,
    TaskCompletion Completion,
    string BodyRef,
    bool IsMandatory = true,
    TaskUpload? Upload = null);
