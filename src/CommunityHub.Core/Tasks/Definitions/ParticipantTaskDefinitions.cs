using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Tasks.Definitions;

/// <summary>
/// §708.11 — the GENERIC-ROLE task definitions (volunteer / organizer / media / event partner),
/// migrated out of <c>WizardStepTaskSeeder.MapStep</c>'s hardcoded titles and descriptions.
/// </summary>
/// <remarks>
/// <para>Operator 2026-07-30: <i>"continue migration pr role for others"</i>, clarified as
/// <i>"rgr migration i mean to new task structure"</i>. Sponsor (§684–§686) and Speaker (§708) came
/// first; this is the third and last set of per-participant onboarding tasks.</para>
///
/// <para>🔒 <b>TITLES ARE VERBATIM from <c>MapStep</c>, and here that is not a convenience — it is the
/// ONLY JOIN.</b> These rows do NOT key off their title (their <c>SourceKey</c> is
/// <c>hotel-form:{pid}</c>, <c>profile:{pid}</c> … , fixed per step), so a re-title cannot orphan
/// them the way it would a speaker row. But <c>TaskBodyService.DefinitionFor</c> matches a row to its
/// definition BY TITLE SLUG, so a title that differs by one character means the row silently keeps
/// rendering its stored prose and the new body is never seen. Migrate first; rename later, visibly.</para>
///
/// <para>🔒 <b>The bodies DROP the "Open the … form" button that the descriptions carried.</b> The
/// form is now embedded in the row (§708.10), and §708.2a decided this exact case: <i>"where the form
/// is EMBEDDED in the task, there is no link at all — the form is simply there."</i> Leaving the
/// button would put a link to the form directly above the form.</para>
///
/// <para>⚠️ <b>TRAVEL IS DELIBERATELY NOT HERE, and the reason is a real trap.</b> The generic travel
/// task is titled <i>"Submit travel reimbursement"</i> — the SAME title as
/// <see cref="SpeakerTaskDefinitions"/>'s <c>speaker.travel</c>. Since <c>DefinitionFor</c> resolves
/// by title slug across the WHOLE registry, adding it would make two definitions answer to one slug
/// and a volunteer could be shown the SPEAKER body (which talks about speaker reimbursement rules).
/// Rendering the wrong body is worse than rendering none, which is the rule §707.43 already settled
/// for ambiguous prefixes. Fixing it properly means making the match role-aware — a change to shared
/// matching that sponsors and speakers also run through, so it is its own piece of work, not a
/// footnote to this one. Travel therefore stays on the legacy path and keeps its stored description.</para>
///
/// <para>🔒 <b>Party, Master Class and the Calendar-email step stay OUT.</b> §707.57b keeps party and
/// master class on their own surfaces; §313 made the calendar step optional and seeds NO task for it.
/// Speaker details is speaker-only and is owned by the speaker set.</para>
/// </remarks>
public static class ParticipantTaskDefinitions
{
    /// <summary>
    /// The four roles the generic Get-Started wizard serves (<c>RoleWizardService.Handles</c>).
    /// </summary>
    /// <remarks>
    /// 🔒 Kept in the SAME order and membership as that method. If a fifth role ever joins the generic
    /// wizard, the audience matrix test fails here rather than the role quietly getting no tasks.
    /// </remarks>
    public static readonly IReadOnlyList<ParticipantRole> GenericRoles = new[]
    {
        ParticipantRole.Volunteer,
        ParticipantRole.Organizer,
        ParticipantRole.Media,
        ParticipantRole.EventPartner,
    };

    /// <summary>Every migrated generic-role definition.</summary>
    public static IReadOnlyList<TaskDefinition> All { get; } = new[]
    {
        // ── ONBOARDING — every generic role, no entitlement, no deadline ────────────────────
        //
        // Completion is Form(...): FormTaskReconciler already closes these from the real saved
        // data and REOPENS them if the answer goes away, both ways (§173e). They were never
        // manually tickable on this page — _ParticipantTaskRow hid the toggle for exactly these
        // keys — so Form() states in the model what the row was already enforcing in markup.
        new TaskDefinition(
            Key: "participant.profile",
            Audience: TaskAudience.ForRoles(GenericRoles),
            Title: "Your profile",
            Due: new TaskDue.None(),
            Reminders: TaskReminderCadence.Standard,
            Completion: new TaskCompletion.Form("profile"),
            BodyRef: "participant/profile"),

        new TaskDefinition(
            Key: "participant.accept",
            Audience: TaskAudience.ForRoles(GenericRoles),
            Title: "Code of Conduct & Privacy",
            Due: new TaskDue.None(),
            Reminders: TaskReminderCadence.Standard,
            Completion: new TaskCompletion.Form("accept"),
            BodyRef: "participant/accept"),

        // §109 — joining happens in Signal, outside the hub, so there is nothing to observe and
        // Manual is correct (the same reasoning as the speaker's Help-Promote task). The audience
        // comes from the signal-groups CONFIG via IsSignalInScope, never from a role list frozen
        // into C#.
        new TaskDefinition(
            Key: "participant.signal",
            Audience: TaskAudience.ForRoles(
                GenericRoles, TaskAudiencePredicate.IsSignalInScope),
            Title: "Join Signal groups",
            Due: new TaskDue.None(),
            Reminders: TaskReminderCadence.Standard,
            Completion: new TaskCompletion.Manual(),
            BodyRef: "participant/signal"),

        // §234 (7a) — VOLUNTEERS ONLY, and on its own key: it previously reused volunteer-form:,
        // which belongs to the SHIFTS wizard, cross-wiring two different live forms' completion
        // signals. The role restriction is the audience's job now rather than an `if` in the
        // wizard service.
        new TaskDefinition(
            Key: "participant.availability",
            Audience: TaskAudience.For(ParticipantRole.Volunteer),
            Title: "Complete your day availability",
            Due: new TaskDue.EventMinus(30),
            Reminders: TaskReminderCadence.Standard,
            Completion: new TaskCompletion.Form("availability"),
            BodyRef: "participant/availability"),

        // ── LOGISTICS — entitlement-gated, completion DERIVED from the submitted form ───────
        //
        // 🔒 The EventMinus offsets are the SAME StartDate − N the owning form services stamp
        // (Hotel −30, Dinner −21, Lunch −21, Swag −21), so it does not matter which side seeds a
        // row first and no existing due date moves. Changing one here would silently re-date live
        // rows and shift every reminder behind them.
        new TaskDefinition(
            Key: "participant.hotel",
            Audience: TaskAudience.ForRoles(
                GenericRoles, TaskAudiencePredicate.EntitledToHotel),
            Title: "Complete the Hotel form",
            Due: new TaskDue.EventMinus(30),
            Reminders: TaskReminderCadence.Standard,
            Completion: new TaskCompletion.Form("hotel"),
            BodyRef: "participant/hotel"),

        new TaskDefinition(
            Key: "participant.dinner",
            Audience: TaskAudience.ForRoles(
                GenericRoles, TaskAudiencePredicate.EntitledToAppreciationDinner),
            Title: "Complete the Appreciation Dinner RSVP",
            Due: new TaskDue.EventMinus(21),
            Reminders: TaskReminderCadence.Standard,
            Completion: new TaskCompletion.Form("dinner"),
            BodyRef: "participant/dinner"),

        new TaskDefinition(
            Key: "participant.lunch",
            Audience: TaskAudience.ForRoles(
                GenericRoles, TaskAudiencePredicate.EntitledToLunchPreDay),
            Title: "Complete the Lunch logistics form",
            Due: new TaskDue.EventMinus(21),
            Reminders: TaskReminderCadence.Standard,
            Completion: new TaskCompletion.Form("lunch"),
            BodyRef: "participant/lunch"),

        new TaskDefinition(
            Key: "participant.swag",
            Audience: TaskAudience.ForRoles(
                GenericRoles, TaskAudiencePredicate.EntitledToSwag),
            Title: "Complete the Swag preferences form",
            Due: new TaskDue.EventMinus(21),
            Reminders: TaskReminderCadence.Standard,
            Completion: new TaskCompletion.Form("swag"),
            BodyRef: "participant/swag"),
    };
}
