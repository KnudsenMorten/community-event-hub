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

    /// <summary>Entitled to claim travel reimbursement.</summary>
    EntitledToTravelReimbursement = 8,

    /// <summary>
    /// §299 6.2 — the speaker is an EXHIBITOR's speaker (<c>SpeakerCategory.Sponsor</c>).
    /// </summary>
    /// <remarks>
    /// 🔒 <b>Stated positively and EXCLUDED at the definition</b> (see <see cref="TaskAudience"/>),
    /// because the rule is a subtraction: §458 <i>"sponsor speaker category should not get the task
    /// 'Help promote'"</i> and §456 <i>"only task relevant for a sponsor (exhibitor) speaker is the
    /// task for upload final presentation"</i>. Written as a requirement it would need inverting on
    /// every OTHER definition, which is how a new task silently defaults to the wrong audience.
    /// </remarks>
    IsSponsorCategorySpeaker = 9,

    /// <summary>
    /// §708.11 — §109: this participant's role is in scope for the Signal groups, per the
    /// signal-groups CONFIG (volunteers + event partners get chat + broadcast, media broadcast only,
    /// organizers are out of scope).
    /// </summary>
    /// <remarks>
    /// 🔒 <b>A predicate, not a role list on the definition</b> — the membership lives in config and
    /// the operator changes it there. Writing today's answer as <c>Roles</c> would freeze a config
    /// value into C# and quietly stop tracking it, which is the evergreen rule (CLAUDE.md) inverted.
    /// The seeder supplies the fact by asking the same config the wizard asks.
    /// </remarks>
    IsSignalInScope = 10,
}

/// <summary>Who a task is for. Roles AND every <see cref="Requires"/> hold; no <see cref="Excludes"/> does.</summary>
/// <remarks>
/// <para>🔒 <b>§708.3 — <see cref="Excludes"/> exists because some audience rules are SUBTRACTIONS.</b>
/// The seeder this replaces expressed §299 6.2 / §456 as slug substring matching
/// (<c>IsLogisticsSlug</c> + "contains <c>final</c> AND <c>presentation</c>"), which is the exact
/// §707.43 / §707.54a failure family: a title-derived string test that stops matching when a title
/// changes or a slug truncates, silently and in the permissive direction.</para>
///
/// <para>Expressing the same rule as a NEGATIVE predicate on the two definitions it applies to keeps
/// it visible at the definition and testable by the audience matrix — a new task cannot inherit it by
/// accident, and cannot lose it to a rename.</para>
/// </remarks>
public sealed record TaskAudience(
    IReadOnlyList<ParticipantRole> Roles,
    IReadOnlyList<TaskAudiencePredicate> Requires,
    IReadOnlyList<TaskAudiencePredicate> Excludes)
{
    public static TaskAudience For(ParticipantRole role, params TaskAudiencePredicate[] requires) =>
        new(new[] { role }, requires, Array.Empty<TaskAudiencePredicate>());

    /// <summary>
    /// §708.11 — the same audience for SEVERAL roles. The generic-role tasks (profile, Code of
    /// Conduct, the logistics forms) are genuinely one task offered to four roles, not four tasks.
    /// </summary>
    /// <remarks>
    /// 🔒 <c>Roles</c> was already a LIST — every caller so far just happened to pass one. This adds
    /// the factory, not the capability, so <c>Satisfies</c> is untouched and the sponsor/speaker sets
    /// cannot be affected by it.
    /// </remarks>
    public static TaskAudience ForRoles(
        IReadOnlyList<ParticipantRole> roles, params TaskAudiencePredicate[] requires) =>
        new(roles, requires, Array.Empty<TaskAudiencePredicate>());

    /// <summary>The same audience, minus anyone satisfying <paramref name="excludes"/>.</summary>
    public TaskAudience Except(params TaskAudiencePredicate[] excludes) =>
        this with { Excludes = excludes };
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

    /// <summary>
    /// §708 step 1 — an ABSOLUTE date from <c>config/speaker-deadlines.&lt;edition&gt;.json</c>,
    /// looked up by that entry's stable <c>key</c>.
    /// </summary>
    /// <remarks>
    /// <para>🔒 <b>A SECOND source, deliberately — not <see cref="Fixed"/>.</b> Speaker deadlines have
    /// never been event-relative: they are absolute calendar dates the organizers negotiate per
    /// edition, so there is no <c>deadlineRules</c> expression that produces them and
    /// <see cref="FromConfig"/> cannot reach them. The tempting shortcut is <see cref="Fixed"/>, which
    /// would bake <c>2027-01-20</c> into evergreen C# — breaking the CLAUDE.md rule that a new edition
    /// is a new config file, never a code change. §708 names this out explicitly: <i>"Do NOT use
    /// <c>TaskDue.Fixed</c>"</i>.</para>
    ///
    /// <para>🔒 <b>Keyed, not title-matched.</b> The key is stable across a re-title; matching on the
    /// title would put the date back on the same string-derived join that §707.43 and §707.54a both
    /// failed on. A key that resolves to nothing leaves the task UNDATED — which drops it out of the
    /// due-day chase silently — so <c>SpeakerTaskDefinitionTests</c> makes an unresolved key a build
    /// failure rather than a live surprise.</para>
    /// </remarks>
    public sealed record FromSpeakerConfig(string DeadlineKey) : TaskDue;
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
    /// §687.5 — completes only when the sponsor BOTH <see cref="TaskDecisionAnswer.Accepted"/>
    /// decision <paramref name="DecisionKey"/> AND actually bought something in
    /// <paramref name="Category"/>. Declining completes it on its own.
    /// </summary>
    /// <remarks>
    /// <para>🔒 <b>TWO GATES, and they are different things.</b> Operator 2026-07-29: <i>"app
    /// packaging is also a product they must have bought to get that service"</i>. §676 made the
    /// attendee-bag task a DECISION, which records INTENT — but intent is not delivery. <b>A sponsor
    /// can say "we would like to contribute" and never buy the packaging</b>, and then their
    /// brochures sit in a box nobody packs. A task reading <i>"you answered: we would like to
    /// contribute"</i> while nothing was bought is exactly the false confidence §687.3 exists to
    /// remove.</para>
    ///
    /// <para>🔑 <b>Declining completes immediately.</b> "Not interested" is a real, recorded answer
    /// (§670) and there is nothing left to buy — holding that task open would chase a sponsor for a
    /// decision they already made.</para>
    ///
    /// <para>Same two safety rules as <see cref="Purchase"/>: a failed lookup never reopens, and a
    /// derived-Done task stays readable and actionable.</para>
    /// </remarks>
    /// <param name="DecisionKey">The decision this task records, e.g. <c>"attendeeBag"</c>.</param>
    /// <param name="Category">The WooCommerce category that must contain a purchase.</param>
    public sealed record DecisionAndPurchase(
        string DecisionKey, string Category) : TaskCompletion;

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
