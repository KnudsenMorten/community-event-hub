using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Tasks.Definitions;

/// <summary>
/// §708 — the SPEAKER task definitions, migrated out of
/// <c>config/speaker-deadlines.eldk27.json</c>'s <c>deadlines</c> array.
/// </summary>
/// <remarks>
/// <para><b>Why this set, and why now.</b> Operator: <i>"what we did for /Sponsor/Tasks yesterday
/// (new design) could also be implemented for /Speaker/Tasks"</i>. §686.3 deliberately migrated
/// sponsor FIRST because it was the hardest set — uploads, forms, coupons, tier conditionality,
/// live webshop data — so that speaker would <i>"fall out cheaply"</i> (§684.5 constraint 1). This is
/// that pay-off: eight definitions and eight bodies, no new machinery except the date fork.</para>
///
/// <para>🔥 <b>Tasks 7 and 8 are the point of the whole exercise.</b> §707.57d photographed the
/// defect on PROD: <c>/Speaker/Tasks</c> showing <i>"Upload final presentation"</i> with a
/// <c>✓ done</c> badge while <c>/Speaker</c> showed <i>"Final: not uploaded yet"</i> for the same
/// speaker at the same moment. The task said done and the file did not exist — §603's lie, possible
/// only because the task was <c>Manual</c> and nothing checked. <see cref="TaskCompletion.Artefact"/>
/// removes the tick by construction (<c>TaskArtefactRules.AllowsManualCompletion</c> excludes it), so
/// the badge follows the FILE and cannot disagree with it again.</para>
///
/// <para>🔒 <b>Party and Master Class are OUT OF SCOPE</b> (§707.57b — <i>"you dont need to migrate
/// party or master class … they are ok as is"</i>). Recorded here so a later reader does not
/// "finish the job".</para>
///
/// <para>🔒 <b>TITLES ARE VERBATIM from the JSON and MUST NOT BE RENAMED HERE.</b> The per-speaker
/// <c>SourceKey</c> is <c>speakerdl:{id}:{slug(title)}</c>, so a title edit moves the key: the old row
/// is pruned as an orphan, a fresh one is created, and the reminder ledger treats it as never
/// chased. <c>SponsorTaskKeys</c> spells out the same rule for sponsors and
/// <c>SpeakerTaskDefinitionTests</c> pins these eight slugs against the ones observed on PROD
/// (§340-F). Migrate first, rename afterwards as its own visible change.</para>
/// </remarks>
public static class SpeakerTaskDefinitions
{
    /// <summary>Every migrated speaker definition.</summary>
    public static IReadOnlyList<TaskDefinition> All { get; } = new[]
    {
        // ── LOGISTICS (§708 1–4) — entitlement-gated, completion DERIVED from the form ──────
        //
        // 🔒 The entitlement predicate replaces SpeakerDeadlineSeeder's DeadlineAllowedByEntitlement,
        // which asked `slug.Contains("hotel")` and friends. Same rule, stated at the definition
        // instead of inferred from the title — so it survives a rename, and the audience matrix can
        // answer "which tasks does an organizer-funded speaker see?" without running a job.
        //
        // Completion moves from a self-declared tick to Form(...): FormTaskReconciler already closes
        // these from the real submitted data, so the tick was only ever a way to claim a form was
        // filled in when it was not.
        new TaskDefinition(
            Key: "speaker.hotel",
            Audience: TaskAudience.For(
                ParticipantRole.Speaker, TaskAudiencePredicate.EntitledToHotel),
            Title: "Hotel",
            Due: new TaskDue.FromSpeakerConfig("hotel"),
            Reminders: TaskReminderCadence.Standard,
            Completion: new TaskCompletion.Form("hotel"),
            BodyRef: "speaker/hotel"),

        new TaskDefinition(
            Key: "speaker.dinner",
            Audience: TaskAudience.For(
                ParticipantRole.Speaker, TaskAudiencePredicate.EntitledToAppreciationDinner),
            Title: "Appreciation Dinner",
            Due: new TaskDue.FromSpeakerConfig("dinner"),
            Reminders: TaskReminderCadence.Standard,
            Completion: new TaskCompletion.Form("dinner"),
            BodyRef: "speaker/dinner"),

        new TaskDefinition(
            Key: "speaker.swag",
            Audience: TaskAudience.For(
                ParticipantRole.Speaker, TaskAudiencePredicate.EntitledToSwag),
            Title: "Swag / Speaker gift",
            Due: new TaskDue.FromSpeakerConfig("swag"),
            Reminders: TaskReminderCadence.Standard,
            Completion: new TaskCompletion.Form("swag"),
            BodyRef: "speaker/swag"),

        new TaskDefinition(
            Key: "speaker.lunch",
            Audience: TaskAudience.For(
                ParticipantRole.Speaker, TaskAudiencePredicate.EntitledToLunchPreDay),
            Title: "Pre-day Lunch",
            Due: new TaskDue.FromSpeakerConfig("lunch"),
            Reminders: TaskReminderCadence.Standard,
            Completion: new TaskCompletion.Form("lunch"),
            BodyRef: "speaker/lunch"),

        // ── §143 / §253 G10 — TRAVEL, and the one task that MUST stay manual ────────────────
        //
        // 🔒 Manual is correct here and it is the exception §684.13 exists to make visible. The copy
        // says "Not claiming? Just mark this task complete — there is nothing to submit", so an
        // OPT-OUT is a real answer. Deriving completion from a submitted claim would trap every
        // speaker who has nothing to claim in a task they can never close, and then chase them for
        // it. §600.3's "derive wherever you can" does not mean "derive where there is nothing to
        // observe".
        //
        // Audience: outside Denmark (§143 — a speaker whose country is not set yet counts as
        // non-Denmark and DOES get it, deliberately: withholding it would quietly cost them money)
        // AND actually entitled to reimbursement (§253 G10 / §299 6.2 — a Guest or sponsor-funded
        // speaker has no such entitlement).
        new TaskDefinition(
            Key: "speaker.travel",
            Audience: TaskAudience.For(
                ParticipantRole.Speaker,
                TaskAudiencePredicate.NonDenmark,
                TaskAudiencePredicate.EntitledToTravelReimbursement),
            Title: "Submit travel reimbursement",
            Due: new TaskDue.FromSpeakerConfig("travel"),
            Reminders: TaskReminderCadence.Standard,
            Completion: new TaskCompletion.Manual(),
            BodyRef: "speaker/travel"),

        // ── §314 / §458 — PROMOTION. Manual, and NOT for an exhibitor's speaker ─────────────
        //
        // Manual because the act happens on LinkedIn: there is nothing in the hub to observe.
        //
        // 🔒 §458 (operator 2026-07-27): "sponsor speaker category should not get the task 'Help
        // promote'". §314 had exempted it on the reasoning that every speaker should promote their
        // session; his decision is that an exhibitor speaker's promotion is the sponsor's own
        // business — and §457 hid their menu entry, so leaving the TASK would mean e-mail reminders
        // for something with no page behind it.
        new TaskDefinition(
            Key: "speaker.promote",
            Audience: TaskAudience.For(ParticipantRole.Speaker)
                .Except(TaskAudiencePredicate.IsSponsorCategorySpeaker),
            Title: "Help to promote your session(s)",
            Due: new TaskDue.FromSpeakerConfig("promote"),
            Reminders: TaskReminderCadence.Standard,
            Completion: new TaskCompletion.Manual(),
            BodyRef: "speaker/promote"),

        // ── 🔥 §707.57d — THE TWO UPLOAD TASKS. The reason this migration exists ─────────────
        //
        // Artefact-backed, so there is no "Mark complete" and no "Reopen task": the state follows
        // the deck. The upload control is EMBEDDED in the task (§688.12 pattern, like the sponsor
        // booth-members and logos editors) rather than linking out to /Speaker — the task becomes
        // the place the work is DONE, which is §600's original complaint about the old format.
        //
        // 🔒 A speaker's decks are per SESSION, so "done" means EVERY one of their sessions has a
        // deck of that kind — see SpeakerArtefactRules. One deck out of two sessions reading as
        // done would be the same lie in a smaller size.
        //
        // §456 (operator 2026-07-27): "only task relevant for a sponsor (exhibitor) speaker is the
        // task for upload final presentation" — so PREVIEW excludes them and FINAL does not. That
        // asymmetry is exactly why it is stated per definition; the seeder expressed it as
        // `slug.Contains("final") && slug.Contains("presentation")`, which a rename would break
        // silently and in the permissive direction.
        new TaskDefinition(
            Key: "speaker.preview-presentation",
            Audience: TaskAudience.For(ParticipantRole.Speaker)
                .Except(TaskAudiencePredicate.IsSponsorCategorySpeaker),
            Title: "Upload preview presentation",
            Due: new TaskDue.FromSpeakerConfig("preview-presentation"),
            Reminders: TaskReminderCadence.Standard,
            Completion: new TaskCompletion.Artefact("preview"),
            BodyRef: "speaker/preview-presentation"),

        new TaskDefinition(
            Key: "speaker.final-presentation",
            // 🔑 NO exclusion — §456's single exception. An exhibitor speaker still owns their
            // final deck; it is the one deliverable of theirs the organizers need.
            Audience: TaskAudience.For(ParticipantRole.Speaker),
            Title: "Upload final presentation",
            Due: new TaskDue.FromSpeakerConfig("final-presentation"),
            Reminders: TaskReminderCadence.Standard,
            Completion: new TaskCompletion.Artefact("final"),
            BodyRef: "speaker/final-presentation"),
    };
}
