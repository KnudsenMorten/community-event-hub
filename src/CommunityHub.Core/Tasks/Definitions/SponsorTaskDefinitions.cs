using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Tasks.Definitions;

/// <summary>
/// §686.2 phase 1 — the SPONSOR task definitions, migrated out of
/// <c>config/sponsor.eldk27.json</c> <c>taskSets</c>.
/// </summary>
/// <remarks>
/// <para><b>Why sponsor first</b> (§686.3): it is the hardest set, which is the point. Sponsor tasks
/// are the only ones using uploads, forms, coupons, tier conditionality, external buttons with
/// interstitials, shipping addresses AND live webshop data. A model that carries sponsor carries
/// everything else. Speaker deadlines are eight short bodies — going first they would leave the
/// model under-specified and it would have to be reworked when sponsor arrived.</para>
///
/// <para>🔒 <b>TV Rental is migrated FIRST and alone</b> (§684.16 / §686.2 step 2). It is the proving
/// case because it needs the <c>:::data</c> provider — [§666], the sponsor's actual TV count,
/// resolved at RENDER time with the three-state contract. If the model can express that task, it can
/// express the rest; so it must render correctly in ALL THREE flavours before anything else moves
/// (§686.2 step 3).</para>
///
/// <para><b>The remaining ten sponsor bodies follow in §686.2 step 4</b>, at which point
/// <c>taskSets</c> is deleted from <c>config/sponsor.eldk27.json</c> — and only when it is empty and
/// green (§686.2 step 5). Each step ships green, and no step may leave two sources of truth for one
/// task (§684.16): that is how the §683 override layer ended up write-only — built, plausible, and
/// connected to nothing.</para>
/// </remarks>
public static class SponsorTaskDefinitions
{
    /// <summary>Every migrated sponsor definition.</summary>
    public static IReadOnlyList<TaskDefinition> All { get; } = new[]
    {
        // ── §666 / §684.16 — THE PROVING CASE ────────────────────────────────
        // Optional (a paid add-on), booth-only, chased on the standard cadence, and completed by
        // hand: buying a TV happens in the external webshop, so nothing in the hub can DERIVE it.
        // Manual is the exception here for the right reason (§684.13) — there is no artefact and no
        // form that could prove it.
        new TaskDefinition(
            Key: "sponsor.tv-rental",
            Audience: TaskAudience.For(ParticipantRole.Sponsor, TaskAudiencePredicate.HasBooth),
            Title: "TV Rental for booth-presentations",
            Due: new TaskDue.FromConfig("tvRequest"),
            Reminders: TaskReminderCadence.Standard,
            Completion: new TaskCompletion.Manual(),
            BodyRef: "sponsor/tv-rental",
            IsMandatory: false),

        // ── EVERY SPONSOR ────────────────────────────────────────────────────
        //
        // 🔒 §1081 — "sponsor.initial-onboarding" IS RETIRED. DO NOT RE-ADD IT.
        //
        // Operator 2026-08-13: *"i think that initial onboarding is legacy before we had get started
        // wizard … it is being replaced by get started"*. The definition itself said so — due
        // `FromConfig("sponsorDescription")`, `Completion: Manual()`, and auto-closed by
        // SponsorOrderPullService the moment a `CompanyDescription` was saved. That is exactly the
        // Get Started "company" step, written before the wizard existed.
        //
        // 🔑 Keeping both meant one missing description produced TWO chases — the Get Started digest
        // naming the blank fields AND this task's own reminder cadence — in different words, about
        // the same fact. Measured on prod when this was decided: all 8 past-due copies belonged to
        // companies whose description was blank, i.e. every one was a duplicate of the wizard step.
        //
        // Nothing is lost. The obligation is stricter now (the company step also requires the SoMe
        // branding text, and the short description for exhibitors), it is chased on the §232 digest
        // cadence, and `/Organizer/SponsorDeliverables` still shows the "Contract & onboarding"
        // stage — that stage never read this task for its state, only for its deadline.
        //
        // Rows already raised are retired (State=Done, ClosedReason=SupersededByGetStarted) by the
        // event-wide sweep in SponsorOrderPullService. Closed, never deleted — his instruction.

        // ── [§676] and [§670] — THE SHARED TWO-BUTTON DECISION ───────────────
        //
        // ONE component, not two implementations: the `decision` block (§684.8), the `Decision`
        // completion kind (§684.13), the stored answer and the auto-completion are all shared. §676
        // is explicit that it must NOT be a copy of §670 — and the only thing that actually differs
        // is the delivery question, which lives in each body's own prose rather than in a second
        // component. The attendee bag cannot offer "bring it to the event" (the bags are packed in
        // advance, so bringing it is always too late), and its body simply does not say so.
        //
        // 🔒 Both answers COMPLETE the task — operator §670: "mark complete will automatically be
        // set once they choose one of the 2 buttons, so no need to show that" — and declining is a
        // RECORDED answer, distinct from never answering, so organizers can tell them apart.
        new TaskDefinition(
            Key: "sponsor.attendee-bag-content",
            Audience: TaskAudience.For(ParticipantRole.Sponsor),
            Title: "Brochures, competition flyer or swags for {{expectedAttendees}} attendee bags",
            Due: new TaskDue.FromConfig("attendeeBagShipment"),
            Reminders: TaskReminderCadence.Standard,
            // §687.5 — BOTH gates. The decision records INTENT; the packaging product is what
            // actually gets their material into the bags. A sponsor who answers "we would like to
            // contribute" and never buys it has brochures sitting in a box nobody packs, while the
            // task reads done — the false confidence §687.3 exists to remove. Declining still
            // completes on its own (§670): there is nothing left to buy.
            Completion: new TaskCompletion.DecisionAndPurchase(
                "attendeeBag", "Attendee Bag Packaging"),
            BodyRef: "sponsor/attendee-bag-content",
            IsMandatory: false),

        new TaskDefinition(
            Key: "sponsor.event-app-game",
            Audience: TaskAudience.For(ParticipantRole.Sponsor),
            Title: "Event App Game - Extra Exposure Opportunity",
            Due: new TaskDue.FromConfig("appGameSignUp"),
            Reminders: TaskReminderCadence.Standard,
            Completion: new TaskCompletion.Decision("appGame"),
            BodyRef: "sponsor/event-app-game",
            IsMandatory: false),

        // ── SPONSORS WHO BOUGHT A SPEAKING SESSION ───────────────────────────
        // §648 — completion is DERIVED from the form, so there is no self-declared "Mark complete"
        // to close this with nothing filled in.
        new TaskDefinition(
            Key: "sponsor.session-description",
            Audience: TaskAudience.For(
                ParticipantRole.Sponsor, TaskAudiencePredicate.HasSponsorSession),
            Title: "Submit session description",
            Due: new TaskDue.FromConfig("session"),
            Reminders: TaskReminderCadence.Standard,
            Completion: new TaskCompletion.Form("session"),
            BodyRef: "sponsor/session-description"),

        // ── BOOTH SPONSORS ───────────────────────────────────────────────────
        // §603/§602.5 — artefact-backed, so no manual tick: a self-declared "Mark complete" here is
        // exactly how a sponsor closed the wall task with no artwork uploaded.
        new TaskDefinition(
            Key: "sponsor.wall-design",
            Audience: TaskAudience.For(ParticipantRole.Sponsor, TaskAudiencePredicate.HasBooth),
            Title: "Upload sponsor wall design in vector format",
            Due: new TaskDue.FromConfig("wallUpload"),
            Reminders: TaskReminderCadence.Standard,
            Completion: new TaskCompletion.Artefact("sponsorwall"),
            BodyRef: "sponsor/sponsor-wall-design",
            // 🗑 §784.14 — THE `Upload` BLOCK IS RETIRED (operator 2026-08-03).
            //
            // *"That tree /Sponsors/Sponsor Upload is retired and shouldn't be pre-created at all.
            // logos are now placed under /Sponsors/Logo/Web and Sponsors/Logo/Print - that is the
            // new path. retire the old logic and delete the old folders"*.
            //
            // 🔒 This was the LAST definition still declaring a `Subfolder`, and it is what drove
            // `ProvisionUploadFoldersAsync` to create `{Company}/SPONSORWALL` under the retired root
            // on every order pull — the empty folders he found beneath each exhibitor. With no
            // upload definitions left, that loop now has nothing to create.
            //
            // ⚠️ The TASK is unchanged and still artefact-backed (`Artefact("sponsorwall")`) — there
            // is still no manual tick, which §603 added because a sponsor once closed this task with
            // no artwork uploaded. What changed is WHERE the artwork lands: since §783.4 it goes
            // through the wizard into Sponsors/Logo/…, which writes a `wall` upload AUDIT row. Both
            // completion checks read that audit (see SponsorOrderPullService +
            // SponsorDeliverablesService), so retiring the folder does not retire the evidence.
            //
            // 🔒 DO NOT reintroduce a `Subfolder` here to "fix" a missing folder link. The folder is
            // gone on purpose; a task that points at it would send sponsors somewhere deleted.
            Upload: null),

        new TaskDefinition(
            Key: "sponsor.booth-layout",
            Audience: TaskAudience.For(ParticipantRole.Sponsor, TaskAudiencePredicate.HasBooth),
            Title: "Choose your booth layout (table, chairs)",
            Due: new TaskDue.FromConfig("boothLayout"),
            Reminders: TaskReminderCadence.Standard,
            // §687.8 — DERIVED FROM THE ORDER, not self-declared. The body itself warns "you must
            // place the order even if the furniture is already included — that is what reserves it
            // for you", so a tick without an order is precisely the lie §600.3 forbids: the sponsor
            // believes they are done and turns up to an empty booth.
            Completion: new TaskCompletion.Purchase("Booth Furniture"),
            BodyRef: "sponsor/booth-layout"),

        new TaskDefinition(
            Key: "sponsor.register-booth-members",
            Audience: TaskAudience.For(ParticipantRole.Sponsor, TaskAudiencePredicate.HasBooth),
            Title: "Register booth members",
            Due: new TaskDue.FromConfig("registerBoothMembers"),
            Reminders: TaskReminderCadence.Standard,
            Completion: new TaskCompletion.Manual(),
            BodyRef: "sponsor/register-booth-members"),

        new TaskDefinition(
            Key: "sponsor.pre-event-shipment",
            Audience: TaskAudience.For(ParticipantRole.Sponsor, TaskAudiencePredicate.HasBooth),
            // §688.12 — "stand" renamed to "booth" for consistency (operator 2026-07-29).
            // ⚠️ A TITLE change moves the SourceKey slug (see SponsorTaskKeys), so the old row is
            // pruned and a fresh one seeded. Acceptable ONLY because §684.23's licence still holds —
            // the population is test users — and it is a CONSCIOUS choice, not an accident. It
            // resets completion state on this task and the re-seeded row is un-chased, so the
            // reminder job may treat it as new. 🔒 Once real sponsors hold task rows this rename
            // would need the key decoupled from the title first (§688.13).
            Title: "Pre-Event Shipment of packages to booth in Expo",
            Due: new TaskDue.FromConfig("boothShipment"),
            Reminders: TaskReminderCadence.Standard,
            Completion: new TaskCompletion.Manual(),
            BodyRef: "sponsor/pre-event-shipment",
            IsMandatory: false),

        new TaskDefinition(
            Key: "sponsor.lead-scan-app",
            Audience: TaskAudience.For(ParticipantRole.Sponsor, TaskAudiencePredicate.HasBooth),
            Title: "Validate booth members have lead scan app + exhibitor guide",
            Due: new TaskDue.FromConfig("leadAppBoothMembers"),
            Reminders: TaskReminderCadence.Standard,
            Completion: new TaskCompletion.Manual(),
            BodyRef: "sponsor/lead-scan-app"),

        new TaskDefinition(
            Key: "sponsor.download-leads",
            Audience: TaskAudience.For(ParticipantRole.Sponsor, TaskAudiencePredicate.HasBooth),
            Title: "Download leads and inquiries",
            Due: new TaskDue.FromConfig("downloadLeads"),
            Reminders: TaskReminderCadence.Standard,
            Completion: new TaskCompletion.Manual(),
            BodyRef: "sponsor/download-leads"),
    };
}
