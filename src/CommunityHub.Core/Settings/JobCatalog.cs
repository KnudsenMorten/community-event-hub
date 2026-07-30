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
    JobSystem System = JobSystem.Platform)
{
    /// <summary>§510 — true when the operator may set this job's frequency from the UI.</summary>
    public bool IsIntervalDriven => DefaultIntervalMinutes is > 0;

    /// <summary>
    /// §510 — the floor. Nothing runs more often than the base tick offers, so the UI must
    /// REJECT a smaller number rather than accept one that silently does nothing.
    /// </summary>
    public const int BaseTickMinutes = 5;
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
            FeatureKey: FeatureCatalog.WebhookDrainKey, System: JobSystem.Zoho),

        new JobDescriptor("SoMeDispatchJob", "Social media dispatch", "Every 15 minutes", "0 */15 * * * *",
            "CEH → LinkedIn: publishes due company-page posts, and e-mails the speaker pre-alert.",
            FeatureKey: "some-scheduling", System: JobSystem.Social),

        new JobDescriptor("AttendeeBackstageSyncJob", "Attendee sync (Zoho Backstage)", "Every 10 minutes", "0 */10 * * * *",
            "Zoho Backstage → CEH: mirrors attendees + orders, and keeps tickets and Master Class "
            + "seats in step with them.",
            FeatureKey: "attendee-reconcile", System: JobSystem.Zoho),

        // §642 — display names only; the function names remain the live health-marker keys.
        new JobDescriptor("WelcomeReconcileJob", "Welcome e-mails: anyone still owed one", "Every 10 minutes", "0 */10 * * * *",
            "CEH → e-mail (Brevo): sends the welcome to anyone eligible who has not had it yet — "
            + "including people who became eligible later, e.g. when you widen a ring.",
            FeatureKey: "welcome-email", System: JobSystem.Email),

        // §655 — the second chance for mail that failed for a reason that fixes itself. Cadence
        // matches FailedMailRetryService.MinGapBetweenAttempts so each tick advances a message by
        // exactly one attempt.
        new JobDescriptor("FailedMailRetryJob", "Retry mail that failed for a temporary reason", "Every 20 minutes", "0 */20 * * * *",
            "CEH → e-mail (Brevo): re-sends messages that failed because we were throttled or the "
            + "send was interrupted — 3 attempts across about an hour. Never retries a bad address.",
            FeatureKey: "outbound-email", System: JobSystem.Email),

        new JobDescriptor("SponsorWelcomeReconcileJob", "Sponsor welcome e-mails: anyone still owed one", "Every 15 minutes", "0 */15 * * * *",
            "CEH → e-mail (Brevo): welcomes every sponsor company's event coordinators, once their "
            + "SharePoint upload folders exist so the mail can point them somewhere real.",
            FeatureKey: "welcome-email", System: JobSystem.Email),

        // §335: deliberately NOT FeatureKey-gated — a broken upload folder must surface whether
        // or not welcome mails are switched on yet.
        new JobDescriptor("SponsorProvisioningStallJob", "Sponsor provisioning stall check", "Daily 07:20 UTC", "0 20 7 * * *",
            "CEH ↔ SharePoint: raises an Action-queue item for a booth company still waiting for its upload folder, and clears it when the folder appears.", System: JobSystem.WebshopAndErp),

        // §595 — NOT the ERP customer/contact sync (that is ErpSyncCustomerContactJob below). This is
        // the WEBSHOP ORDER pull, left at 15 on his correction: "but the webshop pull is different
        // job … there is a different job that handles the customer + contact sync from erp".
        // §642 — HOP 2 of the ERP→CEH chain, which the old title ("Webshop order pull") hid: this
        // is what actually creates the sponsor CONTACTS in CEH, and it also provisions them in Zoho.
        new JobDescriptor("WooCommercePullJob", "Webshop → CEH: orders, sponsor contacts + folders", "Every 15 minutes", "0 */15 * * * *",
            "Webshop (WooCommerce) → CEH: pulls completed sponsor orders and expands them into "
            + "tasks; creates/updates each company's CONTACTS in CEH from Company Manager so they "
            + "can sign in; creates the sponsor + exhibitor records in Zoho; captures the company's "
            + "public name; and provisions the SharePoint upload folders.",
            FeatureKey: "sponsor-order-pull", System: JobSystem.WebshopAndErp),

        new JobDescriptor("SponsorUploadWatchJob", "Sponsor upload watch", "Every 15 minutes", "0 */15 * * * *",
            "SharePoint → CEH: watches the sponsor upload folders and notifies when a company uploads a file.",
            FeatureKey: "sponsor-upload-watch", System: JobSystem.SharePoint),

        // §545 — NOT FeatureKey-gated and deliberately so: this is the watchdog that notices when
        // something ELSE has gone quiet, so it must not be silenceable by the same class of switch.
        new JobDescriptor("JobSilenceAlertJob", "Silent-job watchdog", "Daily 06:40 UTC", "0 40 6 * * *",
            "CEH (internal): reports background jobs that are running green while doing nothing, which "
            + "the failure alerts cannot see because they do not fail.", System: JobSystem.Platform),

        // §623 — NOT FeatureKey-gated: a speaker detail missing in Backstage must surface whether or
        // not any speaker feature is switched on. Hash-deduped, so a daily run that finds the same
        // set sends nothing — one mail when a gap appears or changes, never one per run.
        new JobDescriptor("SpeakerGapReportJob", "Speaker detail gaps (Backstage)", "Daily 06:20 UTC", "0 20 6 * * *",
            "CEH → e-mail (Brevo): mails the organizers which speaker details CEH holds that Zoho "
            + "Backstage is missing, since the speakers API cannot be updated.", System: JobSystem.Zoho),

        // §598 — deliberately NOT FeatureKey-gated: a file that has vanished must surface whether or
        // not any sponsor feature is switched on. Daily is the right cadence — files rarely vanish,
        // the check costs one folder listing per artefact kind, and a task reverting a few hours
        // later is harmless. Polling hard would spend API budget to detect something rare.
        // §707.17 (operator 2026-07-30: *"change to run every 15 min … otherwise will the portal
        // show wrong file if i deleted it manually"*) — daily 05:10 → every 15 minutes. He is right
        // about the consequence: this job is the ONLY thing that notices a file deleted directly in
        // SharePoint, so at a daily cadence the portal could show a sponsor's task as satisfied by
        // an artefact that no longer exists, for up to 24 hours.
        new JobDescriptor("SponsorArtefactVerifyJob", "Sponsor file verification", "Every 15 minutes", "0 */15 * * * *",
            "SharePoint → CEH: confirms every logo/artwork CEH believes was uploaded still exists, "
            + "and reverts the task when a file has been deleted.", System: JobSystem.SharePoint),

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

        new JobDescriptor("SessionizeImportJob", "Sessionize import", "Every 10 minutes", "0 */5 * * * *",
            "Sessionize → CEH: pulls accepted speakers + their sessions and upserts them.",
            FeatureKey: "sessionize-import", DefaultIntervalMinutes: 10, System: JobSystem.Sessionize),

        new JobDescriptor("SponsorLeadsJob", "Sponsor leads pull", "Hourly, at :15", "0 15 * * * *",
            "Zoho CRM → CEH: pulls sponsor leads and inquiries.",
            FeatureKey: "sponsor-leads", System: JobSystem.WebshopAndErp),

        // 🔒 §707.8 — was "Every 10 minutes" while the cron said */5. The Jobs page renders the
        // WORDS, so it stated a cadence the host does not run. Pinned now by
        // JobCadenceWordsMatchCronTests so the two can never drift apart again.
        new JobDescriptor("SessionChangeDetectionJob", "Session change detection", "Every 5 minutes", "0 */5 * * * *",
            "Zoho Backstage → CEH: detects session time/room changes and queues them for approval.",
            FeatureKey: "session-change-alerts", System: JobSystem.Zoho),

        new JobDescriptor("SpeakerGraphicsSyncJob", "Speaker graphics sync", "Every 15 min, at :10/:25/:40/:55", "0 10,25,40,55 * * * *",
            "Blob storage → CEH: syncs speaker promo graphics so the speaker pages stay current.", System: JobSystem.SharePoint),

        // §543b — the Zoho session/speaker sync is now INTERVAL-DRIVEN (§510), so the operator can
        // retune it himself. He asked three times, with 1,500 attendees waiting on an agenda that
        // had not appeared: "what is the frequency for zoho session / speaker sync",
        // "it should run every 10 min!!!", "change to every 10 min Speaker change detection".
        // These three were clock-anchored for no real reason — nothing about them is tied to a
        // wall-clock time, they simply poll — so converting them costs nothing and hands over the
        // dial. 10 minutes is his stated cadence; he can change it with no deploy.
        new JobDescriptor("SessionBackstagePushJob", "Session push to Backstage", "Every 10 minutes", "0 */5 * * * *",
            "CEH → Zoho Backstage: pushes sessions and speakers (creates only; the Backstage API cannot update).",
            DefaultIntervalMinutes: 10, System: JobSystem.Zoho),

        new JobDescriptor("SpeakerChangeDetectionJob", "Speaker change detection", "Every 10 minutes", "0 */5 * * * *",
            "Zoho Backstage → CEH: detects speaker-profile changes and queues them for approval.",
            FeatureKey: "speaker-change-alerts", DefaultIntervalMinutes: 10, System: JobSystem.Zoho),

        new JobDescriptor("WelcomeGrantPruneJob", "Welcome link prune", "Daily 03:30", "0 30 3 * * *",
            "CEH (internal): expires used/old welcome auto-login grants so a stale link cannot sign anyone in.", System: JobSystem.Platform),

        new JobDescriptor("AuditPurgeJob", "Audit purge", "Daily 04:00", "0 0 4 * * *",
            "CEH (internal): deletes audit rows past the retention window.", System: JobSystem.Platform),

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
        new JobDescriptor("SponsorZohoReconcileJob", "Zoho sync: sponsors + exhibitors", "Every 10 minutes", "0 */5 * * * *",
            "CEH → Zoho Backstage: sends changed sponsor + exhibitor details (description, website, "
            + "social links), sets the event coordinator from Company Manager, and clears a dead "
            + "link so the record is created again if it was deleted in Zoho.",
            FeatureKey: "backstage-sync", DefaultIntervalMinutes: 10, System: JobSystem.Zoho),

        new JobDescriptor("ReminderJob", "Reminder engine", "Daily 08:00", "0 0 8 * * *",
            "CEH → e-mail (Brevo): the daily reminder pass — tasks, deadlines, digests, party/Master Class chasers, hotel cut-offs.",
            FeatureKey: "reminder-jobs", System: JobSystem.Email),

        // §436: the mail now goes out ON RELEASE (the organizer's Release click, and the
        // quarter-hourly SharePoint sync which pulls AND auto-releases). This daily pass is the
        // SAFETY NET, and the description says so — an operations page still claiming this is
        // when speakers hear about it would be the same silent drift §435 caught in the schedules.
        // §707.17 (operator 2026-07-30: *"run it every 30 min"*) — was daily 08:30 UTC. It is the
        // catch-up for the live "graphic released" mail, and a safety net that waits a day is not
        // much of a safety net.
        new JobDescriptor("SpeakerGraphicsReadyJob", "Speaker graphics ready (catch-up)", "Every 30 minutes", "0 */30 * * * *",
            "Safety net for the Help Promote mail: a speaker is told as soon as a graphic is "
            + "RELEASED, so this pass only picks up anyone the live release missed.",
            FeatureKey: "speaker-graphics-promote", System: JobSystem.Email),

        // Both are MANUAL-ONLY in practice: "0 0 0 1 1 *" is 1 January, i.e. effectively
        // never on a timer — they exist to be triggered by hand from the admin endpoint.

        new JobDescriptor("SessionPushPilotJob", "Session push pilot", "Manual only", "0 0 0 1 1 *",
            "One-shot pilot of the session push, for testing a Backstage push safely.", System: JobSystem.Zoho),
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
