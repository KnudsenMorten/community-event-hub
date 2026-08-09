namespace CommunityHub.Core.Settings;

/// <summary>
/// §645 — the SYSTEM a job belongs to, which is how the operator actually looks for one ("the Zoho
/// one", "the webshop one").
/// </summary>
/// <remarks>
/// Operator 2026-07-29: *"can we reorder / restructure the list per system (source), so it becomes
/// easier to understand and quickly find the job to run now — it is in no order"*. Declaration
/// order below IS the display order on the Jobs page.
/// </remarks>
public enum JobSystem
{
    /// <summary>Zoho Backstage — sessions, speakers, sponsors, exhibitors, attendees.</summary>
    Zoho = 0,
    /// <summary>The webshop + Company Manager + e-conomic (ERP) company-and-money chain.</summary>
    WebshopAndErp = 1,
    /// <summary>Sessionize — the call for speakers.</summary>
    Sessionize = 2,
    /// <summary>SharePoint — sponsor uploads and speaker graphics.</summary>
    SharePoint = 3,
    /// <summary>Outbound e-mail — welcomes, reminders, digests.</summary>
    Email = 4,
    /// <summary>LinkedIn and social scheduling.</summary>
    Social = 5,
    /// <summary>Housekeeping, health, and anything that watches the hub itself.</summary>
    Platform = 6,
}

/// <summary>
/// §327 — one background job, described for an ORGANIZER rather than for a developer.
/// </summary>
/// <param name="FunctionName">
/// The Azure Functions name, exactly as declared in <c>[Function("…")]</c>. This is the
/// join key to everything else (the pause middleware, the throttle override, the logs), and
/// a completeness test asserts it matches a real function.
/// </param>
/// <param name="Title">Short human name for the page.</param>
/// <param name="Cadence">The SHIPPED schedule in words, e.g. "Every 10 minutes".</param>
/// <param name="Cron">
/// The NCrontab expression compiled into the job's <c>[TimerTrigger]</c>. Azure Functions
/// binds this at STARTUP, so it is shown read-only — see <see cref="JobCatalog"/> remarks.
/// </param>
/// <param name="What">What the job does, in one plain sentence.</param>
/// <param name="FeatureKey">
/// The <see cref="FeatureCatalog"/> switch that turns this job off, or null when the job is
/// always on. Lets the page say "off because its feature is off" instead of leaving an
/// organizer guessing why nothing happened.
/// </param>
/// <param name="HealthKey">
/// The <c>JobHealthMarker.JobKey</c> this job reports under, when it reports at all — the
/// source of "last succeeded". Null when the job keeps no health marker.
/// </param>
public sealed record JobDescriptor(
    string FunctionName,
    string Title,
    string Cadence,
    string Cron,
    string What,
    string? FeatureKey = null,
    string? HealthKey = null,
    /// <summary>
    /// §510 — set ONLY on interval-driven jobs, and it changes what <see cref="Cron"/> means.
    /// When present the cron is merely a fast BASE TICK and THIS is the real cadence: the
    /// operator edits it on /Organizer/Jobs and it applies with no deploy, because
    /// <c>JobsPauseMiddleware</c> skips every tick until this many minutes have passed.
    ///
    /// <para><b>Null means the schedule is clock-anchored</b> ("every day at 07:20 UTC", "every
    /// hour at :00"). Those must NOT be converted — a tick+interval would drift the send away
    /// from the hour. The Jobs page hides the frequency box for them rather than showing a
    /// control that cannot work, which was the §509 complaint to begin with.</para>
    /// </summary>
    int? DefaultIntervalMinutes = null,
    /// <summary>
    /// §645 — which system this job is about, so the Jobs page groups instead of showing one flat
    /// list in catalog order. Defaults to <see cref="JobSystem.Platform"/>, and a test asserts every
    /// job declares one deliberately rather than falling into that default.
    /// </summary>
    JobSystem System = JobSystem.Platform,
    /// <summary>
    /// §878 — how often the host OFFERS this job a chance to run, which is therefore the smallest
    /// frequency the operator can set. 5 for everything except the webhook drain.
    ///
    /// <para>🔒 It is per-job because a single global floor would have to slow
    /// <c>ZohoWebhookDrainJob</c> — the real-time attendee leg, ticking every MINUTE — down to 5
    /// minutes just to give it the same control every other job has. Per-job keeps the page
    /// consistent (every job has a box) without crippling the one job that needs to be fast.</para>
    /// </summary>
    // NOTE: the literal 5 (not the DefaultTickMinutes const below) — a record's positional
    // parameter default cannot reference a member of the record it is declaring.
    int TickMinutes = 5)
{
    /// <summary>§510 — true when the operator may set this job's frequency from the UI.</summary>
    public bool IsIntervalDriven => DefaultIntervalMinutes is > 0;

    /// <summary>
    /// 🔴 §1009b — the cadence to DISPLAY: derived from the number that actually paces the job,
    /// so it cannot disagree with it. Falls back to the hand-written <see cref="Cadence"/> for
    /// clock-anchored jobs, where the cron really is the schedule ("daily at 07:20 UTC").
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-09: *"if this is just text update, why dont you just take the value
    /// for the setting instead of having these not relevant times when they are static text"*. He
    /// is right, and it removes the whole class of bug rather than the twenty instances of it.</para>
    ///
    /// <para><b>What it replaces.</b> <see cref="Cadence"/> was hand-written text, and §869.3's bulk
    /// conversion set <c>DefaultIntervalMinutes: 10</c> on nearly every job without touching it — so
    /// the Jobs page printed "Hourly" over a 10-minute job on <b>20 jobs</b>. A test could only ever
    /// have reported that drift; deriving the text means there is nothing to drift.</para>
    ///
    /// <para>⚠️ <b>This is the SHIPPED default.</b> An operator override
    /// (<c>JobRunState.MinIntervalMinutes</c>) is what actually runs, and only the page can know it
    /// — see <c>EffectiveCadenceWords</c>.</para>
    /// </remarks>
    public string CadenceWords =>
        DefaultIntervalMinutes is { } m && m > 0 ? DescribeInterval(m) : Cadence;

    /// <summary>
    /// §1009b — the cadence in words for a given interval, so the page, the catalog and any test
    /// all phrase it identically.
    /// </summary>
    public static string DescribeInterval(int minutes) => minutes switch
    {
        <= 0 => "Every tick",
        1 => "Every minute",
        60 => "Hourly",
        1440 => "Daily",
        10080 => "Weekly",
        < 60 => $"Every {minutes} minutes",
        _ when minutes % 1440 == 0 => $"Every {minutes / 1440} days",
        _ when minutes % 60 == 0 => $"Every {minutes / 60} hours",
        _ => $"Every {minutes} minutes",
    };

    /// <summary>
    /// §510/§878 — the floor for THIS job. Nothing runs more often than its base tick offers, so
    /// the UI must REJECT a smaller number rather than accept one that silently does nothing.
    /// </summary>
    public int BaseTickMinutes => TickMinutes;

    /// <summary>The base tick every job uses unless it declares otherwise.</summary>
    public const int DefaultTickMinutes = 5;
}

/// <summary>
/// §327 (operator 2026-07-25: "do i have a page with all the jobs running, so i can manually
/// reschedule frequency … verify also the page contains all the jobs i actually have") — the
/// single, VERIFIED inventory of every background job.
///
/// <para><b>Why a hand-written catalog and not reflection at runtime?</b> The web app does not
/// load the Functions assembly, so it cannot discover the jobs itself. The catalog is written
/// once here and <b>a test asserts it matches the real functions exactly</b> — every
/// <c>[TimerTrigger]</c> in <c>CommunityHub.Jobs</c> must appear here with the same cron, and
/// nothing may appear here that is not a real function. A stale page is therefore a failing
/// build, not a surprise on the day.</para>
///
/// <para><b>Why the cron is read-only.</b> Azure Functions binds a <c>[TimerTrigger]</c>
/// expression when the host STARTS. Nothing the web app writes at runtime can change it — a
/// page that appeared to edit it would be lying. Changing a shipped cadence is a code change
/// plus a deploy. What CAN be changed at runtime is a per-job <b>minimum interval</b>: the
/// pause middleware skips an invocation that arrives sooner than the operator allows, which
/// makes a job run LESS often without a deploy. It can never make one run more often than its
/// cron already fires.</para>
/// </summary>
public static class JobCatalog
{
    /// <summary>Every background job, in the order the page lists them (most frequent first).</summary>
    public static readonly IReadOnlyList<JobDescriptor> All = new[]
    {
        new JobDescriptor("ZohoWebhookDrainJob", "Zoho webhook drain", "Every minute", "0 * * * * *",
            "Zoho Backstage → CEH: drains queued Zoho webhook events (the real-time leg of the attendee sync).",
            // 🔒 §878 — a 1-MINUTE base tick, so this keeps its real-time cadence AND gets the same
            // box as every other job. A global 5-minute floor would have slowed the live attendee
            // leg 5× purely to make the page uniform.
            FeatureKey: FeatureCatalog.WebhookDrainKey, DefaultIntervalMinutes: 1,
            System: JobSystem.Zoho, TickMinutes: 1),

        // §824.21 — plans the campaign; publishes nothing. Every post it creates is queued INACTIVE,
        // and only an ACTIVE queued post is dispatched, so the planning can run daily while nothing
        // reaches the company page until he approves a row (§824.8 Q2).
        new JobDescriptor("SoMeScheduleJob", "Social media announcement planner", "Daily", "0 */5 * * * *",
            "Works out which speaker-track, session, sponsor and sponsor-tier announcements are still missing, when each should go out (weekdays, 11:00 or 14:30 Danish time), composes it from this edition's template — and queues it HELD for your approval.",
            FeatureKey: "some-scheduling", DefaultIntervalMinutes: 10, System: JobSystem.Social),

        // §869.3 — converted. The cron is now only the base tick; 15 is the shipped cadence and
        // he can retune it on the page. A post is due at a MINUTE, so the dial bounds how late a
        // due post can go out — not when it is allowed to.
        new JobDescriptor("SoMeDispatchJob", "Social media dispatch", "Every 15 minutes", "0 */5 * * * *",
            "CEH → LinkedIn: publishes due company-page posts, and e-mails the speaker pre-alert.",
            FeatureKey: "some-scheduling", DefaultIntervalMinutes: 10, System: JobSystem.Social),

        // §869.3 — converted. Pure polling: nothing about it is tied to a wall-clock time.
        new JobDescriptor("AttendeeBackstageSyncJob", "Attendee sync (Zoho Backstage)", "Every 10 minutes", "0 */5 * * * *",
            "Zoho Backstage → CEH: mirrors attendees + orders, and keeps tickets and Master Class "
            + "seats in step with them.",
            FeatureKey: "attendee-reconcile", DefaultIntervalMinutes: 10, System: JobSystem.Zoho),

        // §642 — display names only; the function names remain the live health-marker keys.
        // §869.3 — converted. A catch-up sweep, so the interval is exactly what it means: how
        // quickly someone who became eligible gets their welcome.
        new JobDescriptor("WelcomeReconcileJob", "Welcome e-mails: anyone still owed one", "Every 10 minutes", "0 */5 * * * *",
            "CEH → e-mail (Brevo): sends the welcome to anyone eligible who has not had it yet — "
            + "including people who became eligible later, e.g. when you widen a ring.",
            FeatureKey: "welcome-email", DefaultIntervalMinutes: 10, System: JobSystem.Email),

        // §655 — the second chance for mail that failed for a reason that fixes itself. Cadence
        // matches FailedMailRetryService.MinGapBetweenAttempts so each tick advances a message by
        // exactly one attempt.
        // §869.3 — converted, with one caveat worth knowing before you turn the dial DOWN:
        // FailedMailRetryService enforces its own 20-minute gap PER MESSAGE, so setting this below
        // 20 does not retry anything faster — it only adds passes that find nothing eligible (and
        // enough of those in a row will earn the "DOING NOTHING" badge, correctly). Turning it UP
        // genuinely spaces the attempts out.
        new JobDescriptor("FailedMailRetryJob", "Retry mail that failed for a temporary reason", "Every 20 minutes", "0 */5 * * * *",
            "CEH → e-mail (Brevo): re-sends messages that failed because we were throttled or the "
            + "send was interrupted — 3 attempts across about an hour. Never retries a bad address.",
            FeatureKey: "outbound-email", DefaultIntervalMinutes: 10, System: JobSystem.Email),

        // §869.3 — converted. Same catch-up shape as WelcomeReconcileJob.
        new JobDescriptor("SponsorWelcomeReconcileJob", "Sponsor welcome e-mails: anyone still owed one", "Every 15 minutes", "0 */5 * * * *",
            "CEH → e-mail (Brevo): welcomes every sponsor company's event coordinators, once their "
            + "SharePoint upload folders exist so the mail can point them somewhere real.",
            FeatureKey: "welcome-email", DefaultIntervalMinutes: 10, System: JobSystem.Email),

        // ⚰️ §819 — "Sponsor provisioning stall check" (§335) RETIRED 2026-08-04, operator-raised.
        // It reported ONE thing: that the §816 welcome guard was holding a Gold+ booth company until
        // its per-company SharePoint upload folder appeared. §816 DELETED that guard, and the flat
        // upload structure means the folder is never created — so the condition could never clear
        // and the item it raised (*"its welcome e-mail is still blocked"*) would have been false
        // from its first run onwards. Measured: 4 Gold+ companies in PROD, 3 holding legacy folder
        // rows from June, and Glueckkanja one day short of the threshold when this was caught.
        // ⚠️ Do not resurrect it "just to watch the folders" — an alert nobody can act on is how the
        // Action queue stops being read.

        // §595 — NOT the ERP customer/contact sync (that is ErpSyncCustomerContactJob below). This is
        // the WEBSHOP ORDER pull, left at 15 on his correction: "but the webshop pull is different
        // job … there is a different job that handles the customer + contact sync from erp".
        // §642 — HOP 2 of the ERP→CEH chain, which the old title ("Webshop order pull") hid: this
        // is what actually creates the sponsor CONTACTS in CEH, and it also provisions them in Zoho.
        new JobDescriptor("WooCommercePullJob", "Webshop → CEH: orders, sponsor contacts + folders", "Every 15 minutes", "0 */5 * * * *",
            "Webshop (WooCommerce) → CEH: pulls completed sponsor orders and expands them into "
            + "tasks; creates/updates each company's CONTACTS in CEH from Company Manager so they "
            + "can sign in; creates the sponsor + exhibitor records in Zoho; captures the company's "
            + "public name; and provisions the SharePoint upload folders.",
            // §869.3 — converted. It polls the webshop; no hop downstream is anchored to a clock.
            FeatureKey: "sponsor-order-pull", DefaultIntervalMinutes: 10, System: JobSystem.WebshopAndErp),

        // 🔑 §869.3 — converted. HE NAMED THIS ONE: it was in the screenshot showing "fixed time —
        // set in code" with no input beside neighbours that had a box.
        new JobDescriptor("SponsorUploadWatchJob", "Sponsor upload watch", "Every 15 minutes", "0 */5 * * * *",
            "SharePoint → CEH: watches the sponsor upload folders and notifies when a company uploads a file.",
            FeatureKey: "sponsor-upload-watch", DefaultIntervalMinutes: 10, System: JobSystem.SharePoint),

        // §545 — NOT FeatureKey-gated and deliberately so: this is the watchdog that notices when
        // something ELSE has gone quiet, so it must not be silenceable by the same class of switch.
        new JobDescriptor("JobSilenceAlertJob", "Silent-job watchdog", "Daily", "0 */5 * * * *",
            "CEH (internal): reports background jobs that are running green while doing nothing, which "
            + "the failure alerts cannot see because they do not fail.",
            DefaultIntervalMinutes: 1440, System: JobSystem.Platform),   // §878.5 — his list

        // §623 — NOT FeatureKey-gated: a speaker detail missing in Backstage must surface whether or
        // not any speaker feature is switched on. Hash-deduped, so a daily run that finds the same
        // set sends nothing — one mail when a gap appears or changes, never one per run.
        new JobDescriptor("SpeakerGapReportJob", "Speaker detail gaps (Backstage)", "Daily", "0 */5 * * * *",
            "CEH → e-mail (Brevo): mails the organizers which speaker details CEH holds that Zoho "
            + "Backstage is missing, since the speakers API cannot be updated.",
            FeatureKey: "speaker-gap-report", DefaultIntervalMinutes: 1440, System: JobSystem.Zoho),  // §878.5

        // §598 — deliberately NOT FeatureKey-gated: a file that has vanished must surface whether or
        // not any sponsor feature is switched on. Daily is the right cadence — files rarely vanish,
        // the check costs one folder listing per artefact kind, and a task reverting a few hours
        // later is harmless. Polling hard would spend API budget to detect something rare.
        // §707.17 (operator 2026-07-30: *"change to run every 15 min … otherwise will the portal
        // show wrong file if i deleted it manually"*) — daily 05:10 → every 15 minutes. He is right
        // about the consequence: this job is the ONLY thing that notices a file deleted directly in
        // SharePoint, so at a daily cadence the portal could show a sponsor's task as satisfied by
        // an artefact that no longer exists, for up to 24 hours.
        // 🔑 §869.3 — converted. HE NAMED THIS ONE TOO, in the same screenshot.
        new JobDescriptor("SponsorArtefactVerifyJob", "Sponsor file verification", "Every 15 minutes", "0 */5 * * * *",
            "SharePoint → CEH: confirms every logo/artwork CEH believes was uploaded still exists, "
            + "and reverts the task when a file has been deleted.",
            DefaultIntervalMinutes: 10, System: JobSystem.SharePoint),

        // §510 PILOT — the cron is only a 5-minute BASE TICK; DefaultIntervalMinutes is the REAL
        // cadence and the operator can change it on /Organizer/Jobs with no deploy.
        // §595 (operator 2026-07-28: "it must run every 10 min") — 30 → 10. This is the ERP →
        // Company Manager / webshop CONTACT + CUSTOMER sync he was looking for on the Jobs page.
        // 🔒 §595 — RENAMED from "ErpWebshopReconcileJob" (operator 2026-07-28: *"i hate the word
        // for that job - why now ErpSyncCustomerContact"*). "Reconcile" said nothing about what it
        // moves; he could not find it on the Jobs page when looking for the ERP customer/contact
        // sync, which is exactly what it does.
        //
        // ⚠️ The FeatureKey and HealthKey are DELIBERATELY UNCHANGED. They are live DB keys —
        // `FeatureSettings.FeatureKey` gates whether this job runs at all, and
        // `JobHealthMarkers.JobKey` carries its history. Renaming those would silently DISABLE the
        // job and orphan its health record; renaming the function name alone is cosmetic and safe.
        // §642 — the title says where the CREATE/UPDATE hop stops. It was "ERP sync: customers +
        // contacts", which read as if that hop reaches CEH; it does not.
        //
        // 🔒 §707.17 — BUT "it stops at Company Manager" WAS WRONG AS A WHOLE-JOB STATEMENT, and the
        // sentence contradicted itself two clauses later. Operator 2026-07-30: *"i thought that the
        // erp sync of customers + contacts goes to BOTH CM and CEH — ceh is not mentioned"*. He is
        // right, and the code agrees: `ErpWebshopContactSyncService` looks up the CEH Participant by
        // email and calls `ParticipantDeactivationService.DeactivateAsync` (§502/§253). So the job
        // WRITES TO CEH — just not in the direction the title describes. Say both hops plainly
        // instead of a "stops at" that is only half true.
        new JobDescriptor("ErpSyncCustomerContactJob", "ERP → Company Manager (+ CEH offboarding)", "Every 10 minutes", "0 */5 * * * *",
            "e-conomic (ERP) → Company Manager / webshop: syncs CUSTOMERS + CONTACTS so invoicing "
            + "and the webshop agree on who the company is. ADDING people stops at Company Manager — "
            + "the webshop order pull is what carries them on into CEH as sign-ins. REMOVING reaches "
            + "CEH directly: a hub participant who is no longer a contact in e-conomic is "
            + "DEACTIVATED here (never deleted, and reversible by re-activating them in the hub).",
            FeatureKey: "erp-webshop-reconcile", HealthKey: "erp-webshop-reconcile",
            DefaultIntervalMinutes: 10, System: JobSystem.WebshopAndErp),

        // §825 — HOURLY (operator 2026-08-04). This job drains the CEH→Zoho hand-entry queue AND
        // sends the pending-speaker notice; at 10 minutes it produced five near-identical notices in
        // half an hour. The cron is only the base tick — DefaultIntervalMinutes is the cadence.
        new JobDescriptor("SessionizeImportJob", "Sessionize import", "Hourly", "0 */5 * * * *",
            "Sessionize → CEH: pulls accepted speakers + their sessions and upserts them, and notifies about speakers still pending approval.",
            FeatureKey: "sessionize-import", DefaultIntervalMinutes: 10, System: JobSystem.Sessionize),

        // §869.3 — converted. The ":15" was only spacing away from the other hourly jobs, not a
        // time anything depends on; a lead is not less pullable at :20.
        new JobDescriptor("SponsorLeadsJob", "Sponsor leads pull", "Hourly", "0 */5 * * * *",
            "Zoho CRM → CEH: pulls sponsor leads and inquiries.",
            FeatureKey: "sponsor-leads", DefaultIntervalMinutes: 10, System: JobSystem.WebshopAndErp),

        // §786 — the C# replacement for the operator's hourly VM script
        // (Sync-Webshop-Orders-Create-ERP-Invoice.ps1). Ships with its feature OFF: turning it on IS
        // the cutover, and the script must be switched off FIRST (§786.2). At :40 so a pass reads
        // orders the 15-minute webshop pull has finished landing.
        new JobDescriptor("WebshopInvoiceJob", "Webshop → e-conomic: draft invoices", "Hourly", "0 */5 * * * *",
            "Webshop (WooCommerce) → e-conomic: turns each completed sponsor order into a DRAFT "
            + "invoice — never a booked one, so nothing reaches a customer until a human books it. "
            + "An order is invoiced ONCE: every draft carries the web order number as its reference, "
            + "and an order already on any draft or booked invoice is skipped. Att person and Your "
            + "reference are the company's default signer. An order with no ERP customer number, no "
            + "price, or a currency with no exchange rate is reported and left uninvoiced rather "
            + "than guessed at.",
            // ⚠️ §878 — the old ":40" existed to sit AFTER the webshop pull. On an interval that
            // phase is no longer guaranteed. It is safe: an order that has not landed yet is simply
            // not invoiced this pass and is picked up on the next one — the job skips what it
            // cannot price rather than guessing (see its own description above).
            FeatureKey: "webshop-erp-invoicing", HealthKey: "webshop-erp-invoicing",
            DefaultIntervalMinutes: 10, System: JobSystem.WebshopAndErp),

        // §787 — the coupon half. At :50, clear of the order sync AND of the webshop invoicing at
        // :40, so a pass reads orders that have finished landing. ⚠️ Unlike the webshop job there is
        // no live script to race: the retired coupon script has never run (§787.5).
        new JobDescriptor("CouponInvoiceJob", "Coupon tickets → e-conomic: draft invoices", "Hourly", "0 */5 * * * *",
            "Claimed Backstage COUPON tickets → e-conomic: one DRAFT invoice per coupon, never a "
            + "booked one. Reads the orders CEH already mirrors, so it makes no Zoho call. Bills the "
            + "ticket's full price, not the discounted total — a coupon that covers a ticket "
            + "entirely leaves the total at zero, and billing that would invoice the partner 0.00. "
            + "A coupon nobody has mapped to an e-conomic customer is never guessed at: it is left "
            + "uninvoiced and the organizer is e-mailed a link to the page that fixes it.",
            FeatureKey: "coupon-erp-invoicing", HealthKey: "coupon-erp-invoicing",
            DefaultIntervalMinutes: 10, System: JobSystem.WebshopAndErp),

        // 🔒 §707.8 — was "Every 10 minutes" while the cron said */5. The Jobs page renders the
        // WORDS, so it stated a cadence the host does not run. Pinned now by
        // JobCadenceWordsMatchCronTests so the two can never drift apart again.
        // §869.3 — converted. Its cron was ALREADY the base tick, so this is purely the dial being
        // handed over: same 5-minute behaviour, now settable. Its twin
        // (SpeakerChangeDetectionJob) had been interval-driven since §543b — the pair reading
        // differently on the page was itself part of his complaint.
        // 🔴🔴 §1020 — PERMANENTLY INERT. `SessionChangeDetectionService` returns before reading
        // anything (Zoho→CEH is off in code, with no switch — operator: *"we cannot have anyone turn
        // this on by mistake"*), so this job ticks and does nothing.
        //
        // 🔒 The FeatureKey is REMOVED rather than repointed: `session-change-alerts` now controls
        // the SPEAKER MAIL only, and leaving it here would make the Jobs page say this job is "off
        // because its feature is off" — implying that turning the mail on would start the sync.
        //
        // ⚠️ The job is kept rather than deleted: §634 — retiring one leaves an orphan PROD health
        // marker that the watchdog then reports as a silent job. Its description says plainly that
        // it does nothing, which is cheaper than that alarm and honest on the page.
        new JobDescriptor("SessionChangeDetectionJob", "Session change detection (retired)", "Every 5 minutes", "0 */5 * * * *",
            "RETIRED (§1020): Zoho Backstage → CEH session sync is permanently off in code — the hub "
            + "owns the schedule. This job still ticks but does nothing. Signage is unaffected.",
            DefaultIntervalMinutes: 10, System: JobSystem.Zoho),

        // §754 — the venue screens. The 5-minute cadence is the operator's own spec (§4: a change
        // made in Backstage must reach the screens "within one polling cycle"), and it is the one
        // number a passer-by can measure: a moved session that is still on the wall is visibly wrong.
        // Interval-driven so he can retune it on the day without a deploy.
        new JobDescriptor("SignageAgendaSyncJob", "Signage agenda sync", "Every 5 minutes", "0 */5 * * * *",
            "Zoho Backstage → CEH: mirrors the complete agenda (talks, breaks, meals, party) for the venue screens.",
            FeatureKey: "signage-agenda-sync", DefaultIntervalMinutes: 10, System: JobSystem.Zoho),

        // §764 (operator 2026-08-01: "automatically download the speakers (community + guest) photo
        // and put it here … so all speaker pictures are in 1 place"). Daily is generous for input
        // that changes when a speaker updates their picture — which is once, if ever — and each pass
        // costs one string comparison per already-archived speaker.
        new JobDescriptor("SpeakerPhotoArchiveJob", "Speaker photos to SharePoint", "Daily", "0 */5 * * * *",
            "CEH → SharePoint: copies each community/guest speaker's photo into the shared speakers folder, alongside the sponsor-uploaded ones.",
            DefaultIntervalMinutes: 10, System: JobSystem.SharePoint),

        // §6.4 / §770.12 — rebuilds every §3.5 logistics file and mails what is due (food and expo
        // weekly, each hotel ON CHANGE from three weeks out). Daily is the work order's own cadence;
        // the MAIL cadences live in the run service, not in this cron.
        // 🔒 Every send is routed to the operator's review mailbox until he approves the reports.
        new JobDescriptor("LogisticsFilesJob", "Logistics files to SharePoint", "Daily", "0 */5 * * * *",
            "CEH → SharePoint: rebuilds the venue, swag, hotel and expo spreadsheets, and mails the ones that are due.",
            DefaultIntervalMinutes: 1440, System: JobSystem.SharePoint),   // §878.5 — his list

        // §6.5 — the three post-event survey summaries, rebuilt daily into their §3.4 folders.
        // 🔒 This job NEVER mails: the work order says "no notification mail for these three", so the
        // summaries are read in the library rather than pushed at anybody.
        new JobDescriptor("SurveySummaryFilesJob", "Post-event survey summaries", "Daily", "0 */5 * * * *",
            "CEH → SharePoint: rebuilds the attendee, speaker and sponsor post-event survey summaries. Sends no mail.",
            DefaultIntervalMinutes: 1440, System: JobSystem.SharePoint),   // §878.5 — his list

        // §6.6 — POST-EVENT consolidation. Idle (and says so) until the event has ended, then copies
        // every session result into the event folder and rebuilds the combined summary.
        // 🔒 A COPY, never a move: the per-speaker results stay where every speaker's link points.
        new JobDescriptor("EvaluationConsolidationJob", "Evaluation consolidation (post-event)", "Daily", "0 */5 * * * *",
            "CEH → SharePoint: after the event, copies every session evaluation result into the event folder, rebuilds the all-session summary PDF, and notifies the organizers when something changed.",
            DefaultIntervalMinutes: 1440, System: JobSystem.SharePoint),   // §878.5 — his list

        // 🔑 §869.3 — converted. THIS IS THE ROW FROM HIS SCREENSHOT: it read
        // "0 10,25,40,55 * * * · set in code" with no input, which is what made him ask.
        // The four-slot list was only spacing; nothing depends on landing at :10.
        new JobDescriptor("SpeakerGraphicsSyncJob", "Speaker graphics sync", "Every 15 minutes", "0 */5 * * * *",
            "Blob storage → CEH: syncs speaker promo graphics so the speaker pages stay current.",
            DefaultIntervalMinutes: 10, System: JobSystem.SharePoint),

        // §543b — the Zoho session/speaker sync is now INTERVAL-DRIVEN (§510), so the operator can
        // retune it himself. He asked three times, with 1,500 attendees waiting on an agenda that
        // had not appeared: "what is the frequency for zoho session / speaker sync",
        // "it should run every 10 min!!!", "change to every 10 min Speaker change detection".
        // These three were clock-anchored for no real reason — nothing about them is tied to a
        // wall-clock time, they simply poll — so converting them costs nothing and hands over the
        // dial. 10 minutes is his stated cadence; he can change it with no deploy.
        // §868.5 — operator 2026-08-05: "i dont see any background service that creates a speaker.
        // maybe extend the text so it says that". He was hunting for the job that CREATES a speaker
        // in Backstage and could not find it, because this one reads as being about sessions. It is
        // the speaker-creating job: SessionBackstagePushJob runs SpeakerBackstagePushService first
        // (a speaker must exist before a session can reference them), and that service is the only
        // caller of ZohoClient.CreateSpeakerAsync. Naming it here is the whole fix.
        new JobDescriptor("SessionBackstagePushJob", "Session + speaker push to Backstage", "Every 10 minutes", "0 */5 * * * *",
            "CEH → Zoho Backstage: CREATES SPEAKERS in Backstage (this is the only job that does), "
            + "then pushes sessions and links their speakers. Creates only — the Backstage API cannot "
            + "update an existing speaker or session, so later edits must be made in Backstage.",
            DefaultIntervalMinutes: 10, System: JobSystem.Zoho),

        new JobDescriptor("SpeakerChangeDetectionJob", "Speaker change detection", "Every 10 minutes", "0 */5 * * * *",
            "Zoho Backstage → CEH: detects speaker-profile changes and queues them for approval.",
            FeatureKey: "speaker-change-alerts", DefaultIntervalMinutes: 10, System: JobSystem.Zoho),

        // §746 — the completion notice was hooked ONLY to /Forms/Wizard, so anyone finishing on a
        // standalone form page was never reported. This observes completion instead of depending on
        // where it happened. The 5-minute pass is audit-driven (an index seek on
        // (EventId, OccurredUtc); usually nobody has moved), with a full sweep at minute 0 for
        // completions nobody clicked for themselves.
        new JobDescriptor("GetStartedCompletionSweepJob", "Get Started completion notices", "Every 5 minutes", "0 */5 * * * *",
            "CEH (internal): reports anyone who has just reached 100% on Get Started, wherever they finished.",
            // 🔒 §878 — converted, and the job's own full-sweep logic had to be fixed FIRST: it used
            // to read `UtcNow.Minute < 5` to pick the hourly deep sweep, which a drifting interval
            // would eventually stop satisfying — silently, with the job still green (§875.4). It now
            // tracks elapsed time instead of the tick minute.
            FeatureKey: "getstarted-complete-notice", DefaultIntervalMinutes: 10, System: JobSystem.Platform),

        // §750 C7 (operator 2026-07-31: "wire the job to run every 5 min"). 🔑 The tick is NOT what
        // decides when a report goes out — the 30-minute quiet period is. It only bounds how long
        // after a session settles we notice, so 5 minutes keeps that bound small; each pass derives
        // one version per session and almost always does nothing.
        new JobDescriptor("EvaluationReportPublishJob", "Session evaluation reports", "Every 5 minutes", "0 */5 * * * *",
            "CEH (internal): publishes a session's evaluation PDF once its feedback has gone quiet for 30 minutes, and emails its speakers a link.",
            // §869.3 — converted. The tick never decided when a report goes out (the 30-minute
            // quiet period does); it only bounds how long after a session settles we notice. So
            // the dial means exactly that bound, which is a fair thing to hand over.
            FeatureKey: "session-eval-email", DefaultIntervalMinutes: 10, System: JobSystem.Platform),

        // ⚰️ §879 — "PendingApprovalsDigestJob" WAS HERE, and is now these TWO jobs (operator
        // 2026-08-05: *"speaker held in the queue. i need that to run every 10 min and be notified
        // after 10 min. volunteers awaiting review should go out weekly. we need to split these as
        // they are very different"*).
        //
        // 🔑 One mail carried two unrelated populations at one cadence, so a single dial had to be
        // wrong for one of them — and it was wrong for the one that mattered. They were never one
        // queue in the data either (§756: QueueRows filters Role == Volunteer), so merging them was
        // a presentation choice, not a fact about the system.
        //
        // ✅ §878.6 IS DISCHARGED BY THE SPLIT. The old job was pinned to 1440 — alone among the
        // jobs — because at 10 minutes it would have mailed ~144×/day: `EngineAlertSender` only
        // dedupes when a throttleKey is passed and this mail passed null (§765 removed that on
        // purpose, so the job's dial would be the single control). The speaker half now carries the
        // CONTENT HASH that was the condition for dropping to 10, held durably in
        // JobRunState.LastContentHash rather than in the alert sender's memory.
        new JobDescriptor("SpeakersHeldJob", "Speakers held from the Zoho flow", "Every 10 minutes", "0 */5 * * * *",
            "CEH (internal): mails the ops mailbox when a speaker is held from the Zoho flow (no category set), with one-click approve-all buttons. Speaks once per CHANGE, not once per run, and is silent while nothing moves.",
            FeatureKey: "digest-emails", DefaultIntervalMinutes: 10, System: JobSystem.Platform),

        new JobDescriptor("VolunteersAwaitingReviewJob", "Volunteers awaiting review", "Weekly", "0 */5 * * * *",
            "CEH (internal): the weekly list of volunteers sitting in the pre-selection queue. Sends nothing when the queue is empty.",
            // No content hash here on purpose: a weekly mail about an untouched queue IS the
            // reminder. Hashing it would go quiet exactly when nobody has got round to the queue.
            FeatureKey: "digest-emails", DefaultIntervalMinutes: 10080, System: JobSystem.Platform),

        // §858.16c — the ONLY caller of LinkedIn's people lookup. Daily because the input changes
        // when the speaker list does or when someone follows the page, and because the endpoint
        // carries a DAY throttle. Re-running is cheap: resolved speakers are skipped without a call.
        new JobDescriptor("SpeakerMentionResolutionJob", "LinkedIn mentions (speakers + sponsors)", "Every 30 minutes", "0 */5 * * * *",
            "CEH (internal): looks up each speaker's and sponsor contact's LinkedIn person id so posts can TAG them instead of just naming them. Anyone already resolved is skipped without a call, so a new person is picked up within half an hour. LinkedIn only allows mentioning people who FOLLOW the page, so the rest stay as plain names — the run reports how many of each.",
            FeatureKey: "linkedin-queue", DefaultIntervalMinutes: 30, System: JobSystem.Social),

        new JobDescriptor("WelcomeGrantPruneJob", "Welcome link prune", "Daily", "0 */5 * * * *",
            "CEH (internal): expires used/old welcome auto-login grants so a stale link cannot sign anyone in.",
            DefaultIntervalMinutes: 1440, System: JobSystem.Platform),   // §878.5 — his list

        new JobDescriptor("AuditPurgeJob", "Audit purge", "Daily", "0 */5 * * * *",
            "CEH (internal): deletes audit rows past the retention window.",
            DefaultIntervalMinutes: 1440, System: JobSystem.Platform),   // §878.5 — his list

        // §543c — daily → operator-editable, default 10 minutes (operator 2026-07-28: "it should
        // run every 10 min"). I held this back over the coordinator mail, which is sent on EVERY
        // run for an exhibitor that is missing and cannot be created — at 10 minutes that repeats
        // 144×/day. He resolved it from operational knowledge: "i am not worried as the api works
        // flawless in creating the exhibitor … it has been working for 1-2 months now". With the
        // create path working, a missing exhibitor is CREATED on the first run and exists on the
        // next, so that mail is self-limiting (one per exhibitor) and the repeat case does not
        // arise. It also lands in the organizer's own inbox, never a sponsor's.
        // 🔒 §641 — "BackstageSyncJob" WAS HERE and is now RETIRED (operator 2026-07-29). It had
        // been a no-op for its entire deployed life (§637: BackstageSync:Enabled never set, while
        // the 'backstage-sync' FEATURE was on, so it rendered as a healthy job). Its [Function]
        // attribute is gone and its health marker was deleted in the same change (§634's rule:
        // retiring a job leaves an orphan the watchdog will report). Do NOT re-add it — the row
        // below is the live sponsor sync, and two of them would risk Zoho's 3-attempt cap (§640.1).

        // §640 — the scheduled sponsor/exhibitor reconcile he asked for ("add the timer over
        // SponsorZohoSyncService"), at the §542 cadence, on the path already proven in production.
        // 🔒 §642 — the FUNCTION NAME stays as it is. It is the live `JobHealthMarkers.JobKey`, and
        // §595 renaming one is exactly what left the orphan §634 had to clean up. Only the DISPLAY
        // name and description change, which is all the operator ever sees.
        // §825 — HOURLY (operator 2026-08-04). This is the other source of the `[CEH→Zoho]` queue
        // mails: at 10 minutes the pair of them delivered five near-identical notices in half an
        // hour on 3 Aug. The cron is only the base tick; DefaultIntervalMinutes below is the cadence.
        new JobDescriptor("SponsorZohoReconcileJob", "Zoho sync: sponsors + exhibitors", "Hourly", "0 */5 * * * *",
            "CEH → Zoho Backstage: sends changed sponsor + exhibitor details (description, website, "
            + "social links), sets the event coordinator from Company Manager, and clears a dead "
            + "link so the record is created again if it was deleted in Zoho.",
            FeatureKey: "backstage-sync", DefaultIntervalMinutes: 10, System: JobSystem.Zoho),

        // 🔑 §878 — THE ROW IN HIS SCREENSHOT. It read "Every day at 08:00 UTC · set in code" with a
        // paragraph of my reasoning where a field should be, and he marked it WRONG while marking
        // the four boxed rows around it CORRECT: *"make all background jobs consistent"*.
        //
        // ⚠️ I argued against this twice and he has now ruled twice, so it is built: a 1440-minute
        // interval is spaced from the LAST RUN, not anchored to 08:00, so the send time creeps
        // later by up to one base tick per day. He accepts that; the alternative is the two-class
        // page he rejected. If the creep ever matters, anchor the daily ones to a stored
        // time-of-day — do NOT bring back a row with no control on it.
        new JobDescriptor("ReminderJob", "Reminder engine", "Daily", "0 */5 * * * *",
            // §879.2 — "digests" was here; the word is banned wherever he reads it.
            "CEH → e-mail (Brevo): the daily reminder pass — tasks, deadlines, open-question round-ups, party/Master Class chasers, hotel cut-offs.",
            FeatureKey: "reminder-jobs", DefaultIntervalMinutes: 10, System: JobSystem.Email),

        // §436: the mail now goes out ON RELEASE (the organizer's Release click, and the
        // quarter-hourly SharePoint sync which pulls AND auto-releases). This daily pass is the
        // SAFETY NET, and the description says so — an operations page still claiming this is
        // when speakers hear about it would be the same silent drift §435 caught in the schedules.
        // §707.17 (operator 2026-07-30: *"run it every 30 min"*) — was daily 08:30 UTC. It is the
        // catch-up for the live "graphic released" mail, and a safety net that waits a day is not
        // much of a safety net.
        // 🔑 §869.3a — converted. HE NAMED THIS ONE: *"same for this - i must be able to control
        // timer so it runs every 10 min"*. It is a pure catch-up sweep, so the interval is
        // precisely what it sounds like — how long the safety net waits before it looks.
        new JobDescriptor("SpeakerGraphicsReadyJob", "Speaker graphics ready (catch-up)", "Every 30 minutes", "0 */5 * * * *",
            "Safety net for the Help Promote mail: a speaker is told as soon as a graphic is "
            + "RELEASED, so this pass only picks up anyone the live release missed.",
            FeatureKey: "speaker-graphics-promote", DefaultIntervalMinutes: 10, System: JobSystem.Email),

        // ⚰️ §878 — "SessionPushPilotJob" WAS HERE and is RETIRED (operator 2026-08-05: *"i have no
        // idea what this is doing … i have a feeling it should be deleted (not used)"*).
        // He is right on every count: it was hard-guarded off behind `StageTwoPilot:Allowed`, parked
        // on an annual cron so it never fired by itself, and its job — push ONE session to Backstage
        // to prove the path — has been done by the real SessionBackstagePushJob plus "Run now" since
        // stage-2 go-live on 2026-07-23 (which is also the date of its one and only success).
        // Its `[Function]` attribute, its class and its PROD health marker all went in this change
        // (§634: retiring a job leaves an orphan the watchdog then reports). Do NOT re-add it.
        // 🔑 Removing it is also what makes §878 exact: EVERY job in this catalog now has a
        // frequency box, with no exception to explain.
    };

    /// <summary>Look up a job by its Functions name, or null when it is not in the catalog.</summary>
    public static JobDescriptor? Find(string functionName) =>
        All.FirstOrDefault(j => string.Equals(j.FunctionName, functionName, StringComparison.Ordinal));

    /// <summary>
    /// §645 — the heading for a system group, named after the thing the operator recognises rather
    /// than after our internal enum.
    /// </summary>
    public static string SystemLabel(JobSystem system) => system switch
    {
        JobSystem.Zoho          => "Zoho Backstage",
        JobSystem.WebshopAndErp => "Webshop, Company Manager & e-conomic",
        JobSystem.Sessionize    => "Sessionize",
        JobSystem.SharePoint    => "SharePoint",
        JobSystem.Email         => "E-mail",
        JobSystem.Social        => "LinkedIn & social",
        _                       => "Platform & housekeeping",
    };

    /// <summary>
    /// §645 — one line saying what this group of jobs is FOR, so the heading is not just a label.
    /// </summary>
    public static string SystemBlurb(JobSystem system) => system switch
    {
        JobSystem.Zoho          => "Sessions, speakers, sponsors and exhibitors going out to Backstage — and attendees and orders coming back.",
        JobSystem.WebshopAndErp => "The company-and-money chain: e-conomic to Company Manager, and the webshop into the hub.",
        JobSystem.Sessionize    => "Bringing submitted sessions and speakers into the hub.",
        JobSystem.SharePoint    => "Sponsor uploads and speaker graphics, and checking the files are still there.",
        JobSystem.Email         => "Welcomes, reminders and digests that go out to people.",
        JobSystem.Social        => "Scheduled LinkedIn posts for the company page.",
        _                       => "Jobs that watch the hub itself, and tidy up after it.",
    };
}
