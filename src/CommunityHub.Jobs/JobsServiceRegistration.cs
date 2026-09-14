using CommunityHub.Core.Config;
using CommunityHub.Core.Diagnostics;
using CommunityHub.Core.Data;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.AspNetCore.DataProtection;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Reminders;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CommunityHub.Jobs;

/// <summary>
/// EVERY service registration the Functions (jobs) host makes - lifted OUT of Program.cs so it can
/// be built by something other than Azure.
/// </summary>
/// <remarks>
/// <para>🔴 §784.15 - this exists because of a PROD fault. <c>EvaluationReportPublishJob</c> failed
/// 18 times in a row on <i>"Unable to resolve service for type 'EvaluationQrService'"</i>: the
/// service was registered in the WEB host and never here. It was the same defect as §783.10, one
/// host over, reintroduced the same day it was fixed.</para>
///
/// <para>🔒 <b>Why the registrations had to MOVE rather than be mirrored in a test.</b> The web
/// host's guard is <c>ValidateOnBuild</c>, which the Functions worker has no equivalent for - the
/// worker resolves a function's constructor PER INVOCATION, so a missing registration cannot fail at
/// startup there. It fails on the first timer tick, minutes after a deploy has already reported
/// success. The only guard that works is a test that builds THIS container and activates every job,
/// and such a test is worthless against a hand-copied mirror of these lines: the mirror would be
/// updated by whoever remembered, which is precisely the person who did not forget in the first
/// place. One list, two callers - <see cref="Register"/> is called by <c>Program.cs</c> in Azure and
/// by <c>JobDependenciesResolveTests</c> on a laptop.</para>
///
/// <para>⚠️ Keep this a PURE registration method: no <c>host.Run()</c>, no connecting, no I/O beyond
/// reading configuration. The test calls it for real, so anything that dials out here would dial out
/// on every test run.</para>
/// </remarks>
public static class JobsServiceRegistration
{
    /// <summary>Registers the complete jobs-host service graph into <paramref name="services"/>.</summary>
    public static void Register(IServiceCollection services, IConfiguration config)
    {

        // --- Application Insights (isolated worker) + webhook-secret log hygiene -----
        // Wire the worker's own AI telemetry pipeline so the redacting initializer below
        // is actually applied. WebhookSecretRedactingTelemetryInitializer strips the Zoho
        // webhook shared secret (?token=…, §128) out of telemetry URLs so it never lands
        // in App Insights. CAVEAT: the Functions HOST emits its own RequestTelemetry for
        // the HTTP trigger which does NOT pass through this worker pipeline and cannot be
        // intercepted from worker DI; the durable host-side fix is to deliver the secret
        // via the X-Webhook-Secret header (ZohoOrderWebhook already validates it) so it is
        // never in a URL at all. See WebhookSecretRedactingTelemetryInitializer.
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();
        services.AddSingleton<ITelemetryInitializer, WebhookSecretRedactingTelemetryInitializer>();

        // In Azure we authenticate to SQL with the Functions app's
        // system-assigned managed identity (passwordless). For local dev a SQL
        // login+password can be supplied via Sql:AdminPassword as a fallback.
        // The presence of Sql:AdminPassword decides which path is used.
        var sqlTemplate = config["Sql:ConnectionStringTemplate"]
                          ?? throw new InvalidOperationException(
                              "Sql:ConnectionStringTemplate is not configured.");
        var sqlPassword = config["Sql:AdminPassword"];
        string connectionString;
        if (!string.IsNullOrWhiteSpace(sqlPassword))
        {
            // Local-dev fallback: SQL login + password.
            var sqlUser = config["Sql:AdminUser"] ?? "communityhubadmin";
            connectionString = $"{sqlTemplate}User ID={sqlUser};Password={sqlPassword};";
        }
        else
        {
            // Azure: passwordless via the app's system-assigned managed identity.
            connectionString = $"{sqlTemplate}Authentication=Active Directory Managed Identity;";
        }

        // EnableRetryOnFailure: silently retries Azure SQL Serverless cold-start
        // (error 40613) - jobs wait ~30-60s instead of failing.
        services.AddDbContext<CommunityHubDbContext>(options =>
            options.UseSqlServer(connectionString, sql =>
                sql.EnableRetryOnFailure(
                    maxRetryCount:  6,
                    maxRetryDelay:  TimeSpan.FromSeconds(30),
                    errorNumbersToAdd: null)));

        services.AddSingleton(TimeProvider.System);

        // --- Feature customization & controlled rollout (REQUIREMENTS §23) ---
        // The same gate the web app uses; the jobs consult it so a disabled
        // advanced feature no-ops however it is triggered (timer included).
        services.AddScoped<CommunityHub.Core.Settings.FeatureGateService>();
        // Effective-ring resolver — schedulers/jobs gate per-resource on the same
        // ring rule the GUI uses (a job only processes a resource whose effective
        // ring ≤ the feature's released ring).
        services.AddScoped<CommunityHub.Core.Settings.RingResolver>();
        // Unified audit trail (REQUIREMENTS §24): jobs record their runs as Engine
        // events + the daily purge job enforces retention.
        services.AddScoped<CommunityHub.Core.Audit.IAuditTrail, CommunityHub.Core.Audit.AuditTrailService>();

        services.Configure<EmailOptions>(
            config.GetSection(EmailOptions.SectionName));
        // Central audit-log path (10a-3): jobs send through the same
        // LoggingEmailSender decorator as the web app so EmailLog captures
        // scheduled reminders + step-reset reminders too.
        services.AddSingleton<IEmailContextAccessor, EmailContextAccessor>();
        // §219 (Risk-4): paces bulk email loops (reminder batch, attendee welcome loops)
        // under Brevo's per-second rate limit. Reads Email:BulkSendDelayMs (default 150ms).
        services.AddSingleton<IBulkSendPacer>(sp => new BulkSendPacer(
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<EmailOptions>>(),
            sp.GetService<TimeProvider>()));
        // §234: delivered-vs-dropped seam. The SCOPED instance (injected into the
        // ledger callers: welcome services, ReminderEngine) installs a per-job-run
        // outcome holder; the SINGLETON senders write into it via Detached()
        // instances, so a ring-dropped send is never stamped/ledgered as sent and
        // is retried on a later run once rings widen.
        services.AddScoped<IEmailDeliveryOutcome, EmailDeliveryOutcome>();
        // Ring-gate every send at the sender (REQUIREMENTS §23): opens a scope per
        // send for RingResolver + FeatureGateService and reads the active edition
        // from the ambient EmailContext. Covers the reminder/digest job paths too.
        // Audience control is rings-only (no allowlist): ring gate + DEV
        // RedirectAllTo + global KillSwitch.
        services.AddSingleton<BrevoEmailSender>(sp => new BrevoEmailSender(
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<EmailOptions>>(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IEmailContextAccessor>(),
            sp.GetService<Microsoft.Extensions.Logging.ILogger<BrevoEmailSender>>(),
            EmailDeliveryOutcome.Detached()));
        services.AddSingleton<IEmailSender>(sp => new LoggingEmailSender(
            sp.GetRequiredService<BrevoEmailSender>(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IEmailContextAccessor>(),
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<EmailOptions>>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<Microsoft.Extensions.Logging.ILogger<LoggingEmailSender>>(),
            EmailDeliveryOutcome.Detached()));
        // Ops/engine ALERT mail (ring-exempt so it reaches the developer mailbox, which is
        // not a ring-gated participant). Used by EngineErrorAlertMiddleware + the engines.
        // §702 — the [DEV]/[PROD] tag on every alert subject. 🔒 NOT IHostEnvironment: the Functions
        // apps set no environment variable at all, so it defaults to "Production" in BOTH editions
        // (verified 2026-07-29). WEBSITE_SITE_NAME is the safety net that makes the tag correct even
        // before Hub__EnvironmentLabel is deployed.
        services.AddSingleton(sp => new CommunityHub.Core.Diagnostics.HubEnvironment(
            sp.GetRequiredService<IConfiguration>()["Hub:EnvironmentLabel"],
            Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME")));
        services.AddSingleton<EngineAlertSender>();
        // RULE (operator 2026-07-23): every CEH-made Zoho Backstage write notifies
        // info@expertslive.dk (publish/delete is manual in Backstage). Rides the
        // ring-exempt EngineAlertSender above; batched per run, unthrottled.
        services.AddSingleton<ZohoChangeNotifier>();

        // --- Email system services (10a) -----------------------------------
        services.AddScoped<ParticipantEmailService>();
        // §655 — retries mail that failed for a SELF-CORRECTING reason (throttle, timeout), three
        // attempts across an hour, never a bad address (FailedMailRetryJob).
        services.AddScoped<FailedMailRetryService>();
        services.AddScoped<OnboardingStepResetEmailService>();
        services.AddScoped<SpeakerQuestionDigestService>();
        // §879: the two organizer-review ops mails to info@expertslive.dk — speakers held from the
        // Zoho flow (every 10 min, once per CHANGE) and volunteers awaiting review (weekly).
        services.AddScoped<CommunityHub.Core.Email.OrganizerReviewMailService>();
        services.AddScoped<OrganizerActionItemService>();
        // Welcome-grant housekeeping (WelcomeGrantPruneJob).
        services.AddScoped<CommunityHub.Core.Auth.WelcomeGrantAdminService>();
        // Master Class waitlist: offer-expiry backstop + promotion email.
        services.AddScoped<CommunityHub.Core.Reminders.MasterClassSignupService>();
        services.AddScoped<CommunityHub.Core.Email.MasterClassPromotionEmailService>();
        services.AddScoped<CommunityHub.Core.Email.MasterClassEmailService>();
        // Ticket-id-keyed attendee sync (reassignment transfers the MC; cancel frees it).
        services.AddScoped<CommunityHub.Core.Reminders.AttendeeTicketSyncService>();

        // --- Attendee welcome auto-provisioning (feature `attendee-welcome`, OFF) ---
        // Creates active login-capable Attendee Participants for 2-day holders +
        // sends a magic-link welcome. The auto-login token is minted HERE but
        // redeemed by the WEB /Login/Magic page, so DataProtection MUST match the
        // web exactly (same persisted SQL key ring + application name) or the token
        // cannot be unprotected. IEnvironmentInfo backs the Core service's (unused-
        // here) DEV guard; the provisioning send path bypasses it deliberately.
        services.AddDataProtection()
            .PersistKeysToDbContext<CommunityHubDbContext>()
            .SetApplicationName("CommunityHub-EventHub");
        services.AddSingleton<CommunityHub.Core.Email.IEnvironmentInfo, CommunityHub.Jobs.JobsEnvironmentInfo>();
        services.AddScoped<CommunityHub.Core.Auth.IWelcomeAutoLoginTokenService,
            CommunityHub.Core.Auth.WelcomeAutoLoginTokenService>();
        // §169 personal email magic-link, so reminders sent from the Jobs host carry
        // the recipient's auto-login link (resolved per-send by EmailTemplateProvider).
        services.AddScoped<CommunityHub.Core.Auth.IEmailMagicLinkService,
            CommunityHub.Core.Auth.EmailMagicLinkService>();
        services.AddScoped<CommunityHub.Core.Reminders.WelcomeWithLoginEmailService>();
        services.AddScoped<CommunityHub.Core.Reminders.AttendeeWelcomeProvisioningService>();
        // §208: the NEW 1-day attendee welcome (one task: Party signup; carries the magic link).
        services.AddScoped<CommunityHub.Core.Reminders.AttendeeOneDayWelcomeEmailService>();

        // Welcome-email options: auto-login link DISABLED by default (operator
        // "disable welcome mail with login"); bound from the WelcomeEmail config
        // section so it can be re-enabled per environment without a code change.
        services.Configure<CommunityHub.Core.Reminders.WelcomeEmailOptions>(
            config.GetSection(CommunityHub.Core.Reminders.WelcomeEmailOptions.SectionName));
        services.AddSingleton(sp =>
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CommunityHub.Core.Reminders.WelcomeEmailOptions>>().Value);

        // --- Email templates (branded reminder rendering) -------------------
        services.Configure<EmailTemplateOptions>(
            config.GetSection(EmailTemplateOptions.SectionName));
        services.AddSingleton<EmailTemplateProvider>();

        services.AddScoped<ReminderEngine>();
        // §436: the shared "your session graphics are ready — Open Help Promote" mail.
        // Registered in BOTH hosts: the sync job + daily sweep call it here, the
        // organizer's Release click calls it in the web host.
        services.AddScoped<CommunityHub.Core.Integrations.Graphics.SpeakerGraphicsReadyNotifier>();
        // Universal sponsor-email audience rule (REQUIREMENTS §7c): the shared
        // coordinator-only recipient resolver, consumed by TaskReminderBuilder so
        // sponsor task reminders go to coordinators (not signer-only assignees).
        // Audience is resolved READ-ONLY from e-conomic ERP Role-2 data
        // (ISponsorErpCoordinatorSource, registered below) with the manual flag as
        // an additive override + fail-soft fallback to the CM default. Explicit
        // factory so the ERP-source ctor is always chosen.
        services.AddScoped<SponsorRecipientResolver>(sp => new SponsorRecipientResolver(
            sp.GetRequiredService<CommunityHub.Core.Data.CommunityHubDbContext>(),
            sp.GetService<CommunityHub.Core.Email.ISponsorErpCoordinatorSource>()));
        services.AddScoped<TaskReminderBuilder>();
        // §177/§206: the crew + attendee party-RSVP cadence — every 2 weeks from welcome.
        services.AddScoped<CommunityHub.Core.Reminders.AttendeePartyReminderBuilder>();
        // §207: the 2-day attendee Master Class selection cadence — every 2 weeks from welcome.
        services.AddScoped<CommunityHub.Core.Reminders.AttendeeMasterClassReminderBuilder>();

        // §250: the biweekly Get-Started-incomplete digest. It enumerates open steps via
        // the WIZARD SERVICES (the same source the wizard pages render — never the task
        // table), so those services + the SignalGroupsProvider they consult are registered
        // for the Jobs host too (mirroring the web app's registrations).
        var signalGroupsOptions = new CommunityHub.Core.Config.SignalGroupsOptions();
        config.GetSection(CommunityHub.Core.Config.SignalGroupsOptions.SectionName)
            .Bind(signalGroupsOptions);
        services.AddSingleton(signalGroupsOptions);
        services.AddSingleton<CommunityHub.Core.Config.SignalGroupsProvider>();
        services.AddScoped<CommunityHub.Forms.SpeakerWizardService>();
        services.AddScoped<CommunityHub.Forms.RoleWizardService>();
        services.AddScoped<CommunityHub.Forms.AttendeeWizardService>();
        services.AddScoped<CommunityHub.Forms.SponsorWizardService>();
        // §1085 — the one role→wizard map, shared by the completion sweep and the status board.
        services.AddScoped<CommunityHub.Forms.WizardProgressReader>();
        services.AddScoped<CommunityHub.Core.Reminders.GetStartedDigestBuilder>();
        // §994 — runs immediately before the digest, so an organizer added since the last pass is
        // chaseable on this one (they get no welcome mail, so nothing else would ever anchor them).
        services.AddScoped<CommunityHub.Core.Reminders.OrganizerWelcomeAnchorSeeder>();
        // §746 — the completion notice was hooked ONLY to /Forms/Wizard, so anyone finishing on a
        // standalone form page (/Forms/Hotel, /Forms/Lunch, …) was never reported. The sweep
        // observes completion instead of depending on where it happened; the wizard hook stays for
        // instant delivery on the common path.
        services.AddScoped<CommunityHub.Core.Reminders.GetStartedCompletionNotifier>();
        services.AddScoped<CommunityHub.Core.Reminders.GetStartedCompletionSweep>();

        // --- Speaker deadline seeding ---------------------------------------
        var speakerDeadlineOptions = new SpeakerDeadlineOptions();
        config.GetSection(SpeakerDeadlineOptions.SectionName)
            .Bind(speakerDeadlineOptions);
        services.AddSingleton(speakerDeadlineOptions);
        services.AddScoped<SpeakerDeadlineSeeder>();
        // §326b: the one-shot speaker Get-Started deadline reminder — reads the
        // getStartedDeadline block of the SAME speaker-deadlines config file.
        services.AddScoped<CommunityHub.Core.Reminders.GetStartedDeadlineReminderBuilder>();
        // §326bs: hotel release-deadline warning (needs the allotment board for its numbers).
        services.AddScoped<CommunityHub.Core.Organizer.HotelAllotmentService>();
        services.AddScoped<CommunityHub.Core.Reminders.HotelCutoffReminderBuilder>();
        // §1127 — chases sponsors whose WEBSHOP website or LinkedIn is blank (weekly until fixed).
        services.AddScoped<CommunityHub.Core.Reminders.SponsorWebshopLinksReminderBuilder>();

        // §164: party sign-up task seeding — ensures the staff-role "party sign-up"
        // tasks exist so the reminder run below nags anyone who hasn't answered Yes/No.
        services.AddScoped<PartyTaskSeeder>();
        // §207: the 2-day attendee "Select your Master Class" task seeding.
        services.AddScoped<CommunityHub.Core.Config.AttendeeMasterClassTaskSeeder>();

        // --- WooCommerce (sponsor pipeline) ---------------------------------
        var wooOptions = new WooCommerceOptions();
        config.GetSection(WooCommerceOptions.SectionName).Bind(wooOptions);
        services.AddSingleton(wooOptions);
        services.AddHttpClient<WooCommerceClient>()
            // §649 — a WooCommerce 429 used to crash the WHOLE pull (observed 2026-07-29 08:30).
            // That job now carries sponsor contacts into CEH, the Zoho provisioning AND the task
            // form-links, so one rate-limit reply cost far more than it should. The handler already
            // treats 429 as transient; WooCommerce simply never had it.
            .AddHttpMessageHandler(() => new CommunityHub.Core.Integrations.TransientFaultRetryHandler())
            .AddCredentialFailureAlert("WooCommerce");

        // --- Sponsor task config (JSON-driven task expansion) ---------------
        var sponsorConfigOptions = new SponsorConfigOptions();
        config.GetSection(SponsorConfigOptions.SectionName)
            .Bind(sponsorConfigOptions);
        services.AddSingleton(sponsorConfigOptions);
        services.AddSingleton<SponsorConfigLoader>();

        // --- Event-edition facts + placeholders (substituted into tasks) ----
        var eventConfigOptions = new EventConfigOptions();
        config.GetSection(EventConfigOptions.SectionName).Bind(eventConfigOptions);
        services.AddSingleton(eventConfigOptions);
        services.AddSingleton<EventEditionConfigLoader>();
        // §299.8/b7 + §299.6/b5: session length/level options + the room registry
        // (pure config) — the import stamps LevelCode and raises warn-only
        // unknown-room warnings; the push log-warns on unknown rooms.
        services.AddScoped<CommunityHub.Core.Config.SessionOptionsService>();
        services.AddScoped<CommunityHub.Core.Config.RoomRegistryService>();

        // --- Admin-editable config overrides (HYBRID config model, Phase 1) -
        // The jobs share the same effective-config path as the web app: shipped
        // JSON default deep-merged with the per-edition SQL ConfigOverride. No
        // row ⇒ shipped default unchanged. IMemoryCache backs the override store.
        services.AddMemoryCache();
        var integrationsConfigOptions = new IntegrationsConfigOptions();
        config.GetSection(IntegrationsConfigOptions.SectionName).Bind(integrationsConfigOptions);
        services.AddSingleton(integrationsConfigOptions);
        services.AddSingleton<IntegrationsConfigLoader>();
        services.AddScoped<ConfigOverrideStore>();
        // Per-edition editable email templates (§25h): job sends honor overrides too.
        services.AddScoped<CommunityHub.Core.Email.EmailTemplateOverrideStore>();
        // §515 — per-template release rings. The jobs host sends most participant mail, so a ring
        // registered only in the web app would be ignored by every scheduled send.
        services.AddScoped<CommunityHub.Core.Email.EmailTemplateRingService>();

        // §707.11 — the per-mail repeat interval. THIS host is where the reminder builders run, so
        // without it every cadence would silently fall back to the shipped default.
        services.AddScoped<CommunityHub.Core.Email.EmailReminderCadenceService>();

        // --- Sessionize (speaker import via v2 view API) -------------------
        var sessionizeOptions = new SessionizeApiOptions();
        config.GetSection(SessionizeApiOptions.SectionName).Bind(sessionizeOptions);
        services.AddSingleton(sessionizeOptions);
        services.AddHttpClient<SessionizeApiClient>()
            // §649 — same treatment, same reason. Read-only pull, so a retry cannot double-write.
            .AddHttpMessageHandler(() => new CommunityHub.Core.Integrations.TransientFaultRetryHandler())
            .AddCredentialFailureAlert("Sessionize");
        // Welcome path is shared with the API import route.
        services.AddScoped<WelcomeEmailService>();
        // Desired-state sponsor welcome reconcile (SponsorWelcomeReconcileJob).
        services.AddScoped<CommunityHub.Core.Reminders.SponsorWelcomeEmailService>();
        // ⚰️ §819 — SponsorProvisioningStallDetector RETIRED. It existed to make the §816 welcome
        // guard's silence visible; that guard is deleted, so there is no blockage left to report and
        // the per-company folder it waited for is never created. Do not re-register it.
        services.AddScoped<CommunityHub.Core.Settings.FeatureSettingsService>();
        services.AddScoped<SessionizeImportService>();
        // Sessions are pulled from the same v2 view API and linked to speakers.
        services.AddScoped<SessionImportService>();
        // Pluggable SESSION source (default Sessionize; Zoho Backstage when enabled).
        services.AddScoped<CommunityHub.Core.Integrations.Sessions.ISessionSource,
            CommunityHub.Core.Integrations.Sessions.SessionizeSessionSource>();
        services.AddScoped<CommunityHub.Core.Integrations.Sessions.ISessionSource,
            CommunityHub.Core.Integrations.Sessions.BackstageSessionSource>();
        services.AddScoped<CommunityHub.Core.Integrations.Sessions.SessionSourceSettingsService>();
        services.AddScoped<CommunityHub.Core.Integrations.Sessions.SessionSourceResolver>();
        // §58 NEVER-AUTO-DELETE: after each Sessionize import, alert the operator about
        // speakers/sessions that disappeared from Sessionize (it never deletes them). Uses
        // the ring-exempt EngineAlertSender (registered above) so the ops mail delivers.
        services.AddScoped<SessionizeDisappearanceDetector>();
        // §999 — the CEH-vs-Sessionize deviation mail (the other half of the import no longer
        // overwriting CEH-owned fields). Registered in BOTH hosts for the §786.6 reason.
        services.AddScoped<CommunityHub.Core.Reminders.SessionizeDeviationNotifier>();
        services.AddScoped<SessionizeApiImportService>();

        // --- Company Manager (sponsor contact source of truth) -------------
        var cmOptions = new CompanyManagerOptions();
        config.GetSection(CompanyManagerOptions.SectionName).Bind(cmOptions);
        services.AddSingleton(cmOptions);
        // Bounded, jittered transient-fault retry (5xx/408/429/timeout) so a momentary
        // upstream blip from Company Manager doesn't crash a reconcile (2026-06-27 incident).
        services.AddHttpClient<CompanyManagerClient>()
            .AddHttpMessageHandler(() => new CommunityHub.Core.Integrations.TransientFaultRetryHandler())
            .AddCredentialFailureAlert("Company Manager");
        services.AddScoped<SponsorContactSyncService>();
        // "Alert only on 2 consecutive failures" gate for background jobs.
        services.AddScoped<CommunityHub.Core.Diagnostics.JobFailureTracker>();
        // Central wrapper used by EngineErrorAlertMiddleware so the same consecutive-failure
        // gate covers EVERY function uniformly (not just the one job that self-gates).
        services.AddScoped<CommunityHub.Core.Diagnostics.EngineFailureAlertGate>();
        // §545(b) — the per-run scratchpad where a job says "I ran and deliberately did nothing,
        // and here is why". SCOPED is load-bearing: one instance per function invocation, so two
        // concurrent jobs cannot read each other's outcome. EngineErrorAlertMiddleware reads it
        // after a clean run; a job that reports nothing is left entirely alone.
        services.AddScoped<CommunityHub.Core.Diagnostics.JobActivityReporter>();

        // --- SharePoint (per-sponsor upload folders + change watcher) ------
        var sharePointOptions = new SharePointUploadOptions();
        config.GetSection(SharePointUploadOptions.SectionName).Bind(sharePointOptions);
        services.AddSingleton(sharePointOptions);
        // §102: SharePoint folder provisioning can be slow (per-folder Graph
        // walk + createLink). Raise the default 100s HttpClient timeout so a
        // single slow Graph call doesn't TaskCanceled-fail the WooCommerce pull.
        services.AddHttpClient<SharePointUploadClient>(c =>
            c.Timeout = TimeSpan.FromMinutes(5))
            // §598 is why this one matters most: the client secret was DEAD in BOTH environments
            // and nothing said so — uploads simply stopped landing.
            .AddCredentialFailureAlert("SharePoint");
        // §598 — verifies stored artefacts still exist in SharePoint (SponsorArtefactVerifyJob).
        services.AddScoped<CommunityHub.Uploads.SponsorArtefactVerifier>();
        // §623 — the CEH↔Zoho speaker gap report (SpeakerGapReportJob).
        services.AddScoped<CommunityHub.Core.Integrations.SpeakerZohoGapReporter>();
        // §545 — detects jobs that run green while doing nothing (JobSilenceAlertJob).
        services.AddScoped<CommunityHub.Core.Diagnostics.JobSilenceDetector>();

        // --- SoMe graphics store (§18/§158): the SharePoint PULL + auto-release sync job
        // needs GraphicsService + the LIVE Graph file store, same gating as the web app
        // (live store only when Graphics:SharePoint is configured; else the null store).
        services.AddSingleton<CommunityHub.Core.Integrations.Graphics.GraphicCompositor>();
        services.AddHttpClient<
            CommunityHub.Core.Integrations.Graphics.ISpeakerPictureFetcher,
            CommunityHub.Core.Integrations.Graphics.HttpSpeakerPictureFetcher>();
        services.AddSingleton<
            CommunityHub.Core.Integrations.Graphics.ISocialShareGateway,
            CommunityHub.Core.Integrations.Graphics.DraftOnlySocialShareGateway>();
        services.Configure<CommunityHub.Core.Integrations.Graphics.GraphicsSharePointOptions>(
            config.GetSection(CommunityHub.Core.Integrations.Graphics.GraphicsSharePointOptions.SectionName));

        // §768 Phase 2 — the DocLibrary registry + resolver. 🔒 Registered in BOTH hosts: the jobs
        // host is where the sweeps run, and it is the host whose settings were found missing six of
        // the eighteen legacy keys. One root here means that class of gap cannot recur.
        services.Configure<CommunityHub.Core.Integrations.DocLibrary.DocLibraryOptions>(
            config.GetSection(CommunityHub.Core.Integrations.DocLibrary.DocLibraryOptions.SectionName));
        services.AddSingleton<
            CommunityHub.Core.Integrations.DocLibrary.IDocLibraryPathResolver,
            CommunityHub.Core.Integrations.DocLibrary.DocLibraryPathResolver>();

        // §769 — the operator's saved path edits, made on the web host's Paths page. 🔒 Registered
        // HERE TOO, and this is the half that is easy to forget: the sweeps run in THIS process, so
        // without it an edited folder would move for every page and for none of the jobs — the
        // writer/reader split that both §767 and §768 turned on. Cross-process the edit lands within
        // DocLibraryOverrideCache.Ttl (30s), not instantly; the page says so out loud.
        services.AddSingleton<CommunityHub.Core.Integrations.DocLibrary.DocLibraryOverrideCache>();

        // §6.4 — the logistics files. The producers are pure; the publisher is stateless; the run
        // service owns the content keys and the mail schedule.
        // 🔒 LogisticsRecipients routes EVERY logistics mail to the operator until he approves the
        // reports (LogisticsMail:ApprovedForRealRecipients). The recipients are external.
        services.AddSingleton(sp =>
        {
            var o = new CommunityHub.Core.Integrations.DocLibrary.LogisticsRecipients();
            config.GetSection(CommunityHub.Core.Integrations.DocLibrary.LogisticsRecipients.SectionName)
                .Bind(o);
            return o;
        });
        services.AddScoped<CommunityHub.Core.Integrations.DocLibrary.DocLibraryFilePublisher>();
        services.AddScoped<CommunityHub.Core.Integrations.DocLibrary.SwagLogisticsProducer>();
        services.AddScoped<CommunityHub.Core.Integrations.DocLibrary.FoodLogisticsProducer>();
        services.AddScoped<CommunityHub.Core.Integrations.DocLibrary.LunchLogisticsProducer>();
        // §1086 — the ONE lunch calculation, shared with the organizer pages in the web host.
        services.AddScoped<CommunityHub.Core.Integrations.DocLibrary.LunchHeadcountService>();
        // 🔒 The expo files ask the SAME purchase service the sponsor's own task and the organizer's
        // logistics panel use (§687/§666), so the number a sponsor sees and the number he orders
        // cannot drift. BOTH hosts need it, or activation fails at RUNTIME — which a build cannot
        // catch.
        //
        // ⚠️ §783.10 — this comment used to read "It is registered in the WEB host already". That
        // was FALSE: the web host had only the CONCRETE class, never this interface mapping, and
        // /Organizer/Logistics 500'd in production for it. Do not assert where ANOTHER host stands;
        // say what THIS host needs. An unverified claim about somewhere else reads as a check that
        // was performed, and stops the next person from looking.
        services.AddScoped<CommunityHub.Core.Integrations.SponsorPurchaseSummaryService>();
        services.AddScoped<CommunityHub.Core.Integrations.ISponsorPurchaseSummary>(sp =>
            sp.GetRequiredService<CommunityHub.Core.Integrations.SponsorPurchaseSummaryService>());
        services.AddScoped<CommunityHub.Core.Integrations.DocLibrary.ExpoLogisticsProducer>();
        services.AddScoped<CommunityHub.Core.Integrations.DocLibrary.HotelLogisticsProducer>();
        services.AddScoped<CommunityHub.Core.Integrations.DocLibrary.LogisticsRunService>();
        services.AddScoped<CommunityHub.Core.Integrations.DocLibrary.LogisticsArtifactsService>();

        // §6.5 — the post-event survey summaries. 🔒 The definition SOURCE is the seam: the JSON
        // loader lives in the web project, so this host reads the same files through its own
        // implementation rather than referencing the web app.
        services.AddSingleton<CommunityHub.Core.Surveys.ISurveyDefinitionSource,
            SurveyDefinitionFileSource>();
        services.AddScoped<CommunityHub.Core.Surveys.SurveyQuestionSummaryService>();
        services.AddScoped<CommunityHub.Core.Surveys.SurveySummaryFileProducer>();
        services.AddScoped<CommunityHub.Core.Surveys.SurveySummaryPublishService>();

        // §6.6 — post-event evaluation consolidation.
        services.AddScoped<CommunityHub.Core.Evaluation.EvaluationSummaryPdfService>();
        services.AddScoped<CommunityHub.Core.Evaluation.EvaluationConsolidationService>();

        var graphicsSpOptions = new CommunityHub.Core.Integrations.Graphics.GraphicsSharePointOptions();
        config.GetSection(CommunityHub.Core.Integrations.Graphics.GraphicsSharePointOptions.SectionName)
            .Bind(graphicsSpOptions);
        if (graphicsSpOptions.IsConfigured)
            services.AddScoped<
                CommunityHub.Core.Integrations.Graphics.ISharePointFileStore,
                CommunityHub.Core.Integrations.Graphics.GraphSharePointFileStore>();
        else
            services.AddSingleton<
                CommunityHub.Core.Integrations.Graphics.ISharePointFileStore,
                CommunityHub.Core.Integrations.Graphics.NullSharePointFileStore>();
        services.AddScoped<CommunityHub.Core.Integrations.Graphics.GraphicsService>();

        // §767 phase 1 — the GIF bundle sweep (per track, per sponsor tier), run from
        // SpeakerGraphicsSyncJob's existing 15-minute slot rather than a second timer.
        // Registered HERE and not in the web app: it is a job dependency, and a service the
        // scheduler cannot resolve fails the whole host at startup rather than at the timer.
        services.AddScoped<CommunityHub.Core.Integrations.Graphics.SoMeBundleBuildService>();

        // --- §750 C7: publish + notify session evaluation reports -------------
        // EvaluationReportPublishJob runs every 5 minutes and needs the whole chain: derive the
        // report version, render the PDF, store it, and mail its speakers.
        // 🔑 These are registered here as well as in the web host because the JOB host is a separate
        // process with its own container — a service registered only in Program.cs of the web app
        // resolves fine in tests and throws at runtime here, and a Functions timer failure is quiet
        // unless somebody is reading the job log.
        // ⚠️ The SharePoint store above falls back to NullSharePointFileStore when the graphics
        // options are unconfigured, so this chain stays INERT rather than failing — which is exactly
        // why the job reports WHICH switch is off instead of returning silently.
        services.AddScoped<CommunityHub.Core.Evaluation.EvaluationScoreService>();
        services.AddScoped<CommunityHub.Core.Evaluation.EvaluationReportBuilder>();
        services.AddScoped<CommunityHub.Core.Evaluation.EvaluationReportService>();
        services.AddSingleton<CommunityHub.Core.Evaluation.SessionQrCodeService>();
        services.AddScoped<CommunityHub.Core.Integrations.Graphics.SessionEvalsQrService>();
        services.AddScoped<CommunityHub.Core.Integrations.Graphics.SessionEvalPdfService>();
        services.AddScoped<CommunityHub.Core.Evaluation.EvaluationArtifactPublishService>();
        // 🔴 §784.15 — MISSING, and it broke PROD: EvaluationReportPublishJob failed 18 times in a
        // row with "Unable to resolve service for type 'EvaluationQrService'".
        //
        // 🔒 I did this to myself, and it is the SAME defect as §783.10 one host over. §783.8 added
        // the QR publish to that job, which needs this service to mint tokens — it was registered in
        // the WEB host (Program.cs:1405) and never here. §783.10's fix was `ValidateOnBuild` in the
        // web host; the Functions host has no equivalent, so the identical mistake sailed straight
        // through the deploy and only surfaced on the timer's first tick.
        //
        // ⚠️ A Functions host cannot use ValidateOnBuild the same way — the worker resolves each
        // function's constructor per invocation — so the guard here is JobDependenciesResolveTests,
        // which builds the real container and activates every job type.
        services.AddScoped<CommunityHub.Core.Evaluation.EvaluationQrService>();
        services.AddScoped<CommunityHub.Core.Reminders.EvaluationReportReadyMailService>();
        services.AddScoped<CommunityHub.Core.Evaluation.EvaluationReportDebounceService>();

        // The single sponsor-pull engine, shared with CommunityHub.OneShot.
        services.AddScoped<SponsorOrderPullService>();

        // --- Zoho (attendee reconciliation, CONTEXT.md 9z) ------------------
        var zohoOptions = new ZohoOptions();
        config.GetSection(ZohoOptions.SectionName).Bind(zohoOptions);
        services.AddSingleton(zohoOptions);
        // §525 — SINGLETON: this host is where the burst came from. ~21 timer jobs each minted
        // their own access token instead of sharing one, tripping Zoho's refresh-grant rate limit
        // so every sync failed with "token refresh failed" against a perfectly valid credential.
        // §1142 — THE SINGLETON WAS NECESSARY AND NOT SUFFICIENT, and this host is why.
        //
        // 🔴 Measured 2026-08-27: this app ran **5,520 distinct instances in 24 hours** (~150–250
        // per hour). A singleton lives as long as its process, so every cold instance that touched
        // Zoho minted its OWN access token. Zoho keeps at most 10 active access tokens per refresh
        // token and evicts the oldest — so tokens were retired out from under instances still
        // holding them, producing sporadic mid-life 401s (§1141).
        //
        // 🔑 The shared store makes "one token" true across the fleet instead of within one
        // process, which is what §525 always intended.
        services.AddSingleton<IZohoTokenStore>(sp =>
            new SqlZohoTokenStore(
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetService<Microsoft.Extensions.Logging.ILogger<SqlZohoTokenStore>>()));
        services.AddSingleton(sp => new ZohoAccessTokenCache(
            clock: sp.GetService<TimeProvider>(),
            store: sp.GetRequiredService<IZohoTokenStore>(),
            credentialKey: ZohoAccessTokenCache.CredentialKeyFor(
                zohoOptions.ClientId, zohoOptions.RefreshToken)));
        services.AddHttpClient<ZohoClient>();

        // §59: delta-approval queue — sync engines ENQUEUE detected changes here for the
        // operator to approve/reject in /Organizer/SyncQueue (never auto-applied). The push
        // services are LAZILY resolved on apply (a CehToZoho Update pushes to Zoho on approve);
        // resolving them here is safe because the push services capture the queue lazily, not
        // in their constructor — so there is no construction cycle.
        services.AddScoped(sp => new CommunityHub.Core.Integrations.Sessions.SyncDeltaQueueService(
            sp.GetRequiredService<CommunityHub.Core.Data.CommunityHubDbContext>(),
            clock: sp.GetService<TimeProvider>(),
            audit: sp.GetService<CommunityHub.Core.Audit.IAuditTrail>(),
            alerts: sp.GetService<CommunityHub.Core.Email.EngineAlertSender>(),
            sender: sp.GetService<CommunityHub.Core.Email.IEmailSender>(),
            context: sp.GetService<CommunityHub.Core.Email.IEmailContextAccessor>(),
            templates: sp.GetService<CommunityHub.Core.Email.EmailTemplateProvider>(),
            sessionPush: sp.GetService<CommunityHub.Core.Integrations.Sessions.SessionBackstagePushService>(),
            speakerPush: sp.GetService<CommunityHub.Core.Integrations.Sessions.SpeakerBackstagePushService>()));

        // §38e: session time/location change detection (Backstage agenda diff). A real change
        // is ENQUEUED to the §59 delta-approval queue (not auto-applied/emailed inline). Run by
        // SessionChangeDetectionJob (hourly).
        services.AddScoped<CommunityHub.Core.Integrations.Sessions.SessionChangeDetectionService>();

        // §764: copies each community/guest speaker's photo into the shared SharePoint speakers
        // folder, alongside the sponsor-uploaded ones. Idempotent by SOURCE URL; the §340-H write
        // guard is what keeps DEV from writing at all. Run by SpeakerPhotoArchiveJob (daily).
        services.AddScoped<CommunityHub.Core.Integrations.Graphics.SpeakerPhotoArchiveService>();

        // §1145 — the volunteer half of the same idea. The speaker archive back-fills its name
        // aliases on every sweep; volunteers had the alias written only at signup, so anyone who
        // uploaded before §1132 stayed id-only for ever. Run by VolunteerPhotoAliasBackfillJob.
        services.AddScoped<CommunityHub.Core.Integrations.Graphics.VolunteerPhotoAliasBackfillService>();

        // §754: the SIGNAGE agenda mirror — pulls the COMPLETE Backstage agenda (talks, master
        // classes, breaks, registration, lunch, party) into AgendaActivities every 5 minutes for
        // the venue screens. READ-ONLY against Zoho, and it never applies an empty pull. Run by
        // SignageAgendaSyncJob; gated on the signage-agenda-sync feature.
        services.AddScoped<CommunityHub.Core.Signage.SignageAgendaSyncService>();

        // §38e/§58: SPEAKER change detection (Backstage speakers diff) — the speaker analogue.
        // A real change (name/tagline/bio/country/social) is ENQUEUED to the §59 delta-approval
        // queue (never auto-applied, never emails, never deletes). Gated per-edition on the
        // SPEAKER sync direction == stage 3 (ZohoToCeh) + the speaker-change-alerts feature. Run
        // by SpeakerChangeDetectionJob (hourly, :50).
        // 🗑 §754.5: the old "inert until the speaker READ scope is granted" note is DELETED — the
        // Backstage credentials have every permission CEH needs. The feature switch is the gate.
        services.AddScoped<CommunityHub.Core.Integrations.Sessions.SpeakerChangeDetectionService>();

        // §57/§58 STAGE 2 (CehToZoho) push engines: create/update Zoho Backstage agenda
        // sessions + create speakers from CEH. Gated per-edition on the session/speaker sync
        // direction == stage 2. Run by SessionBackstagePushJob (hourly); inert at the default
        // stage 1 and at stage 3 (the §38e read engine).
        // §59: the push services ENQUEUE updates of already-linked records to the delta queue
        // (instead of pushing inline) and the queue pushes them on approve. A LAZY
        // Func<SyncDeltaQueueService> breaks the otherwise-circular queue↔push DI graph.
        services.AddScoped(sp => new CommunityHub.Core.Integrations.Sessions.SessionBackstagePushService(
            sp.GetRequiredService<CommunityHub.Core.Data.CommunityHubDbContext>(),
            sp.GetRequiredService<ZohoClient>(),
            sp.GetRequiredService<ZohoOptions>(),
            tokenOverride: null,
            queueFactory: () => sp.GetRequiredService<CommunityHub.Core.Integrations.Sessions.SyncDeltaQueueService>(),
            // §299.6/b5: warn-only unknown-room logging against the config room registry.
            logger: sp.GetService<Microsoft.Extensions.Logging.ILogger<CommunityHub.Core.Integrations.Sessions.SessionBackstagePushService>>(),
            rooms: sp.GetService<CommunityHub.Core.Config.RoomRegistryService>(),
            // Operator 2026-07-23: every successful Zoho write notifies info@expertslive.dk.
            zohoChanges: sp.GetService<CommunityHub.Core.Email.ZohoChangeNotifier>(),
            // INCIDENT FIX 2026-07-24: session creates attach ONLY approved + ring-eligible
            // speaker e-mails (an attached e-mail makes Zoho create + INVITE the speaker).
            gate: sp.GetRequiredService<CommunityHub.Core.Settings.FeatureGateService>()));
        services.AddScoped(sp => new CommunityHub.Core.Integrations.Sessions.SpeakerBackstagePushService(
            sp.GetRequiredService<CommunityHub.Core.Data.CommunityHubDbContext>(),
            sp.GetRequiredService<ZohoClient>(),
            sp.GetRequiredService<ZohoOptions>(),
            tokenOverride: null,
            queueFactory: () => sp.GetRequiredService<CommunityHub.Core.Integrations.Sessions.SyncDeltaQueueService>(),
            // Operator 2026-07-23: every successful Zoho write notifies info@expertslive.dk.
            zohoChanges: sp.GetService<CommunityHub.Core.Email.ZohoChangeNotifier>(),
            // Stage-2 go-live: only APPROVED speakers inside the backstage-speaker-sync
            // released ring (Ring1 today) are pushed; everyone else is held.
            gate: sp.GetRequiredService<CommunityHub.Core.Settings.FeatureGateService>()));

        // STAGE 4b: create/link Zoho sponsor + exhibitor records from webshop data
        // after the order pull (replaces the legacy PowerShell sync). Run by
        // WooCommercePullJob, gated by 'sponsor-zoho-provision'.
        services.AddScoped<SponsorZohoProvisionService>();
        // §1165 — the swag catalogue: holds (and their expiry sweep) and credits.
        services.AddScoped<SwagCatalogHoldService>();
        services.AddScoped<SwagCatalogCreditService>();
        services.AddScoped<SponsorSwagCatalogService>();
        // ProvisionAsync delegates the §41b blank-only Zoho←CEH social/web reconcile for
        // ALREADY-LINKED companies to SyncAsync (same code the sponsor save uses), so the
        // sync service must be resolvable here too.
        services.AddScoped<SponsorZohoSyncService>();

        // --- Sponsor leads pipeline (nightly CRM pull + delta digests) ------
        services.AddSingleton<CommunityHub.Core.Integrations.Sponsors.SponsorLeadScreeningService>();
        services.AddScoped<CommunityHub.Core.Integrations.Sponsors.SponsorLeadSyncService>();

        // --- TESTMODE -------------------------------------------------------
        var testModeOptions = new TestModeOptions();
        config.GetSection(TestModeOptions.SectionName).Bind(testModeOptions);
        services.AddSingleton(testModeOptions);

        // --- §340-H EXTERNAL WRITES (env default + per-edition organizer override) ---
        // The JOBS host is where most third-party writes actually happen, so this
        // registration matters more here than in the web app. SCOPED, because the guard
        // reads the organizer's override from the DB and caches it for the scope.
        var externalWriteOptions = new CommunityHub.Core.Integrations.ExternalWriteOptions();
        config.GetSection(CommunityHub.Core.Integrations.ExternalWriteOptions.SectionName)
            .Bind(externalWriteOptions);
        services.AddSingleton(externalWriteOptions);
        services.AddScoped<CommunityHub.Core.Integrations.IExternalWriteGuard,
            CommunityHub.Core.Integrations.ExternalWriteGuard>();
        // §1037 — pass the whole options object: the banner names EACH system now, because one
        // word can no longer describe a host that may write to e-conomic but never to Zoho.
        Console.WriteLine(CommunityHub.Core.Integrations.ExternalWriteGuard.StartupBanner(
            externalWriteOptions));

        // --- Backstage exhibitor sync --------------------------------------
        var backstageSyncOptions = new BackstageSyncOptions();
        config.GetSection(BackstageSyncOptions.SectionName)
            .Bind(backstageSyncOptions);
        services.AddSingleton(backstageSyncOptions);

        // The exhibitor API is TESTMODE or live, decided by the TESTMODE flag.
        var backstageExhibitorOptions = new BackstageExhibitorOptions();
        config.GetSection(BackstageExhibitorOptions.SectionName)
            .Bind(backstageExhibitorOptions);
        services.AddSingleton(backstageExhibitorOptions);

        if (testModeOptions.Enabled)
        {
            services.AddSingleton<IBackstageExhibitorApi,
                TestModeBackstageExhibitorApi>();
        }
        else
        {
            // Live: needs an HttpClient and the Zoho token source.
            services.AddHttpClient<IBackstageExhibitorApi,
                LiveBackstageExhibitorApi>();
        }
        services.AddSingleton<BackstageSyncService>();

        // --- e-conomic ERP + sponsor webshop (REQUIREMENTS §7a) -------------
        // Customer create/sync + CVR validation + contact/role + webshop sync +
        // order create with an FX/currency check. The ERP write client is
        // TESTMODE or live, decided by the TESTMODE flag (same pattern as the
        // Backstage exhibitor API). Live wiring is ◻ until e-conomic + webshop
        // creds/endpoints are configured; until then everything records
        // WouldCreate and never fakes a call.
        var economicErpOptions = new CommunityHub.Core.Integrations.Erp.EconomicErpOptions();
        config.GetSection(CommunityHub.Core.Integrations.Erp.EconomicErpOptions.SectionName)
            .Bind(economicErpOptions);
        services.AddSingleton(economicErpOptions);

        // ERP→webshop reconcile (the scheduled ErpSyncCustomerContactJob, 30-min timer):
        // mirror the web app's registration so the job can resolve the sync service.
        // All other deps (CompanyManagerClient/Options, EmailSender) are already above.
        services.AddHttpClient<CommunityHub.Core.Integrations.Erp.IEconomicContactAdminClient,
            CommunityHub.Core.Integrations.Erp.LiveEconomicContactAdminClient>()
            .AddCredentialFailureAlert("e-conomic");
        services.AddScoped<CommunityHub.Core.Integrations.Erp.EconomicContactAdminService>();
        // 🔴 §1114 — THE DEACTIVATION CASCADE, WITHOUT WHICH TWO FEATURES SILENTLY DO NOTHING.
        //
        // `ErpWebshopContactSyncService` takes `ParticipantDeactivationService` as an OPTIONAL
        // parameter, and only the WEB host registered it. So in this host it arrived null on every
        // run and both branches that depend on it returned at their first line:
        //   • §502 orphan pruning — while the mail it sends said *"Already done automatically: the
        //     matching hub participant was deactivated"*. It was not. The mail asserted an action
        //     that never happened, which is worse than the missing action.
        //   • §1112's de-sponsored sweep — which is how it was caught: it was deployed, the job ran
        //     for 92 s against real data, and it changed nothing.
        //
        // 🔑 Third time this session (§1110, §1113, this): **an optional dependency turns a missing
        // registration into a null instead of a startup error.** Nothing throws, nothing logs, and
        // the feature is simply absent. `JobDependenciesResolveTests` (§784.15) activates every
        // [Function] against this registration and passed throughout — an optional parameter is
        // satisfied by null, so resolution was never in doubt. Optionality has to be paid for with a
        // test that asserts the dependency IS there where it matters.
        services.AddScoped<CommunityHub.Core.Organizer.ParticipantDeactivationService>();
        services.AddScoped<CommunityHub.Core.Integrations.Erp.ErpWebshopContactSyncService>();

        // Read-only e-conomic ROLE source (REQUIREMENTS §7c): resolves the
        // sponsor-email coordinator audience from e-conomic contact role data
        // (Role 2 = event coordinator) because Company Manager cannot hold
        // per-user roles. Opt-in (EconomicRoles:Enabled, default false) +
        // fail-soft: disabled/unreachable/empty falls back to the CM default
        // coordinator. STRICTLY READ-ONLY (GETs only). When disabled the Null
        // source returns null so the resolver uses the manual-flag fallback.
        var economicRolesOptions = new CommunityHub.Core.Integrations.Erp.EconomicRolesOptions();
        config.GetSection(CommunityHub.Core.Integrations.Erp.EconomicRolesOptions.SectionName)
            .Bind(economicRolesOptions);
        services.AddSingleton(economicRolesOptions);
        if (economicRolesOptions.Enabled)
        {
            services.AddHttpClient<CommunityHub.Core.Integrations.Erp.IEconomicRoleClient,
                CommunityHub.Core.Integrations.Erp.EconomicRoleClient>();
            services.AddScoped<CommunityHub.Core.Email.ISponsorErpCoordinatorSource,
                CommunityHub.Core.Email.SponsorErpCoordinatorSource>();
        }
        else
        {
            services.AddSingleton<CommunityHub.Core.Integrations.Erp.IEconomicRoleClient,
                CommunityHub.Core.Integrations.Erp.NullEconomicRoleClient>();
            services.AddSingleton<CommunityHub.Core.Email.ISponsorErpCoordinatorSource,
                CommunityHub.Core.Email.NullSponsorErpCoordinatorSource>();
        }

        if (testModeOptions.Enabled)
        {
            services.AddSingleton<CommunityHub.Core.Integrations.Erp.IEconomicErpClient,
                CommunityHub.Core.Integrations.Erp.TestModeEconomicErpClient>();
        }
        else
        {
            services.AddHttpClient<CommunityHub.Core.Integrations.Erp.IEconomicErpClient,
                CommunityHub.Core.Integrations.Erp.LiveEconomicErpClient>();
        }

        // External CVR register lookup (◻ disabled by default → offline gate only).
        var cvrLookupOptions = new CommunityHub.Core.Integrations.Erp.ExternalCvrLookupOptions();
        config.GetSection(CommunityHub.Core.Integrations.Erp.ExternalCvrLookupOptions.SectionName)
            .Bind(cvrLookupOptions);
        services.AddSingleton(cvrLookupOptions);
        services.AddHttpClient<CommunityHub.Core.Integrations.Erp.IExternalCvrLookup,
            CommunityHub.Core.Integrations.Erp.ExternalCvrLookup>();
        services.AddSingleton<CommunityHub.Core.Integrations.Erp.ICvrValidator,
            CommunityHub.Core.Integrations.Erp.CvrValidator>();

        // FX rate provider (◻ disabled by default → known-currency gate only).
        var fxOptions = new CommunityHub.Core.Integrations.Erp.FxRateOptions();
        config.GetSection(CommunityHub.Core.Integrations.Erp.FxRateOptions.SectionName)
            .Bind(fxOptions);
        services.AddSingleton(fxOptions);
        services.AddHttpClient<CommunityHub.Core.Integrations.Erp.IFxRateProvider,
            CommunityHub.Core.Integrations.Erp.FxRateProvider>();

        services.AddScoped<CommunityHub.Core.Integrations.Erp.EconomicCustomerSyncService>();
        services.AddScoped<CommunityHub.Core.Integrations.Erp.EconomicOrderCreationService>();

        // --- §786 webshop order → e-conomic DRAFT invoice ---------------------------------------
        // The C# replacement for the operator's hourly VM script. THIS host is where
        // WebshopInvoiceJob runs, so this is the registration that matters most — and it is exactly
        // the shape §784.15 exists to catch, so it is also covered by JobDependenciesResolveTests.
        // The client follows the TESTMODE flag like the exhibitor + ERP clients above: a TestMode
        // environment records WouldCreate and never reaches e-conomic.
        if (testModeOptions.Enabled)
        {
            services.AddSingleton<CommunityHub.Core.Integrations.Erp.IEconomicInvoiceClient,
                CommunityHub.Core.Integrations.Erp.TestModeEconomicInvoiceClient>();
        }
        else
        {
            services.AddHttpClient<CommunityHub.Core.Integrations.Erp.IEconomicInvoiceClient,
                CommunityHub.Core.Integrations.Erp.LiveEconomicInvoiceClient>()
                .AddCredentialFailureAlert("e-conomic");
        }
        // §788 — the shared DRY-RUN switch for BOTH invoice modules.
        // 🔒 FromConfiguration, NOT .Bind(): Bind THROWS on a blank or unparseable bool, so a typo'd
        // Invoicing:DryRun app setting would take this host down on its first tick. The reader keeps
        // dry run ON instead — a typo costs a held invoice, not an outage.
        services.AddSingleton(
            CommunityHub.Core.Integrations.Erp.InvoicingOptions.FromConfiguration(config));
        services.AddScoped<CommunityHub.Core.Integrations.Erp.WebshopDraftInvoiceService>();
        // §787 — the coupon half, registered in BOTH hosts (see the web Program.cs note). 🔒 New job
        // services go HERE, never back in the Program.cs lambda — §784.15's JobDependenciesResolveTests
        // activates every [Function] class against this registration, so a miss goes red on a laptop
        // instead of on the operator's timer.
        services.AddScoped<CommunityHub.Core.Integrations.Erp.CouponDraftInvoiceService>();
        services.AddScoped<CommunityHub.Core.Integrations.Erp.CouponMappingAlertService>();
        // §795.2/§795.3 — the prepaid billing chase and the "drafts were created" notice, resolved
        // by CouponInvoiceJob and WebshopInvoiceJob, which run in THIS host.
        services.AddScoped<CommunityHub.Core.Integrations.Erp.CouponDiscoveryService>();
        // §1093 — one monitor link per billing customer, created by the coupon sweep.
        services.AddScoped<CommunityHub.Core.Integrations.Erp.CouponCustomerMonitorProvisioner>();
        // §1094 — the partner's fortnightly usage report and the balances behind it.
        // ⚠️ AttendeeMonitorQuery was registered ONLY in the web host: the report service needs it
        // here too, and without this the job would resolve nothing and the mail would never send —
        // silently, because the whole block is fail-soft.
        services.AddScoped<CommunityHub.Core.Integrations.AttendeeMonitorQuery>();
        services.AddScoped<CommunityHub.Core.Integrations.MonitorPrepaidBalanceQuery>();
        services.AddScoped<CommunityHub.Core.Integrations.Erp.CouponUsageReportService>();
        services.AddScoped<CommunityHub.Core.Integrations.Erp.CouponPrepaidBillingReminderService>();
        services.AddScoped<CommunityHub.Core.Integrations.Erp.CouponPrepaidLowBalanceAlertService>();
        services.AddScoped<CommunityHub.Core.Integrations.Erp.DraftInvoiceCreatedNotifier>();
        services.AddScoped<CommunityHub.Core.Integrations.Erp.InvoiceProblemNotifier>();

        // --- LinkedIn company-page SoMe scheduling queue (REQUIREMENTS §19) --
        // The SoMeDispatchJob publishes due, Active, Queued posts and sends the
        // T-5-minute speaker pre-alert. GATED: the publisher defaults to the
        // no-op Null publisher (CanPublish=false) so nothing posts until a live
        // publisher is wired AND posting is enabled with a company page. The
        // company-page id is operator config (NOT a secret); the LinkedIn OAuth
        // token is a Key Vault secret (read by the live publisher — never in the
        // repo). Swap in a live ILinkedInPostPublisher here once wired.
        // LinkedIn live publisher (§19/§31) — wired but INERT by default: registered
        // only when enabled AND credentialed; LinkedIn:DryRun (default true) then still
        // holds every post (logs intent, posts nothing). Unconfigured ⇒ Null no-op.
        var liOptions = new CommunityHub.Core.Integrations.LinkedInOptions();
        config.GetSection(CommunityHub.Core.Integrations.LinkedInOptions.SectionName).Bind(liOptions);
        services.AddSingleton(liOptions);
        // §324: live publisher whenever Enabled — the org token comes from the DB
        // (minted via /Organizer/LinkedInConnect), else the options credentials.
        services.AddHttpClient<CommunityHub.Core.Integrations.LinkedInTokenStore>();
        if (liOptions.Enabled)
        {
            services.AddHttpClient<CommunityHub.Core.Integrations.ILinkedInPostPublisher,
                CommunityHub.Core.Integrations.LiveLinkedInPostPublisher>()
                .AddCredentialFailureAlert("LinkedIn");
        }
        else
        {
            services.AddSingleton<CommunityHub.Core.Integrations.ILinkedInPostPublisher,
                CommunityHub.Core.Integrations.NullLinkedInPostPublisher>();
        }
        services.AddScoped<CommunityHub.Core.Integrations.SoMeSettingsService>();
        services.AddScoped<CommunityHub.Core.Integrations.SoMeDispatchService>();
        // §858.16 — speaker→mention resolution. Registered unconditionally (unlike the publisher):
        // the client reports "not connected" honestly rather than throwing, and the job is gated on
        // the LinkedIn feature anyway. 🔒 This host is the ONLY place that calls the lookup — the
        // endpoint carries a DAY throttle and a member URN never changes, so it is a cache fill.
        services.AddHttpClient<CommunityHub.Core.Integrations.LinkedInPeopleTypeaheadClient>()
            .AddCredentialFailureAlert("LinkedIn");
        services.AddScoped<CommunityHub.Core.Integrations.SpeakerMentionResolutionService>();
        // 🔒 §844 — the media library, HERE as well as in the web host. The DISPATCHER resolves a
        // post's graphic or video by file name through it, and the dispatcher runs in THIS host: web
        // registration alone would deploy green and then publish every §828/§844 post text-only,
        // silently, on the first tick. [[ceh-di-two-hosts]].
        services.AddScoped<CommunityHub.Core.Integrations.SoMeGraphicLibrary>();
        services.AddScoped<CommunityHub.Core.Integrations.SoMeCadenceService>();
        // 🔒 §850 — the dispatcher RUNS HERE and re-checks eligibility at send time, so a post
        // approved before the sponsor's text was cleared cannot publish. Web-only registration
        // would leave that check silently absent on the one host that does the sending.
        services.AddScoped<CommunityHub.Core.Integrations.SoMeApprovalGate>();
        // §918 — auto-approval, run by SoMeScheduleJob after planning. No-op unless switched on.
        services.AddScoped<CommunityHub.Core.Integrations.SoMeAutoApproveService>();
        // §824.21 — the announcement planner (SoMeScheduleJob) and the two services it composes
        // with. 🔒 These are ALSO registered in the web host for the template editor; both hosts need
        // them now that a JOB composes posts. [[ceh-di-two-hosts]]: registering in one host only
        // deploys green and fails on the first tick.
        services.AddScoped<CommunityHub.Core.Integrations.SoMeTemplateService>();
        services.AddScoped<CommunityHub.Core.Integrations.SoMeVariableResolver>();
        // §864 — token resolution at publish time. Registered in BOTH hosts (see web Program.cs).
        services.AddScoped<CommunityHub.Core.Integrations.SoMePostComposer>();
        // §1170 — the human subject label for the auto-approval notice and the 24h digest. It was
        // registered in the WEB host only, so the mails the JOBS host sends fell back to raw subject
        // keys ("session:1") — the optional dependency was silently null exactly where it mattered.
        services.AddScoped<CommunityHub.Core.Integrations.SoMeSubjectLabeller>();
        services.AddScoped<CommunityHub.Core.Integrations.SoMeScheduleService>();
        // §824.2D — the AI intro writer, on the SAME Azure OpenAI configuration the AiHelper uses:
        // one endpoint, one key, one place to switch off. The web host binds these already; the JOBS
        // host had no OpenAI registration at all, and the scheduler runs HERE.
        // 🔒 Unconfigured is a supported state, not a failure — `IsConfigured` is false, the
        // generator returns null, and posts are composed without an intro.
        var openAi = new CommunityHub.Core.Assistant.OpenAiOptions();
        config.GetSection(CommunityHub.Core.Assistant.OpenAiOptions.SectionName).Bind(openAi);
        services.AddSingleton(openAi);
        // 🔒 §906 — BOUNDED, because the planner makes ONE CALL PER POST. At the default 100-second
        // HttpClient timeout, a dead endpoint costs 78 × 100s and the timer function dies long
        // before it finishes. 15s is generous for one paragraph; exceeding it means the endpoint is
        // unwell, and a post composed without its intro is the correct outcome (§824.2D).
        services.AddHttpClient<CommunityHub.Core.Integrations.SoMeIntroGenerator>(
            c => c.Timeout = TimeSpan.FromSeconds(15));
        // §1060(l) — the eligibility judge, on the same options and bounded for the same reason.
        // 🔒 Its budget is per SESSION, not per post, and the sweep skips anything already eligible
        // with unchanged text — so a slow endpoint costs the sweep, never the dispatcher.
        services.AddHttpClient<CommunityHub.Core.Integrations.SoMeTextEligibilityJudge>(
            c => c.Timeout = TimeSpan.FromSeconds(15));
        services.AddScoped<CommunityHub.Core.Integrations.SoMeTextEligibilitySweep>();
        // §1060(b) — the announcement notices to speakers and sponsors.
        services.AddScoped<CommunityHub.Core.Integrations.SoMeAnnouncementNotifier>();
        // §1060(j) — the daily "what publishes in the next 24 hours" digest to info@.
        services.AddScoped<CommunityHub.Core.Integrations.SoMeNext24HoursDigest>();
        // §1077 — volume-package qualification (the sweep computes only; the SEND is the job's, and
        // sits behind VolumePackageApprovalMailService.FeatureKey).
        services.AddScoped<CommunityHub.Core.Integrations.VolumePackageQualificationService>();
        services.AddScoped<CommunityHub.Core.Integrations.VolumePackageSweep>();
        services.AddScoped<CommunityHub.Core.Email.VolumePackageApprovalMailService>();
        // §1077 stage 4 — the weekly chase (behind its own switch, default OFF).
        services.AddScoped<CommunityHub.Core.Email.VolumePackageReminderService>();
        // §1080 — the campaign batch sender. 🔒 The unsubscribe secret is required here too: no
        // signed link, no send (MailCampaignService.Gate refuses).
        services.AddScoped<CommunityHub.Core.Email.MailAudienceResolver>();
        services.AddScoped(sp => new CommunityHub.Core.Email.MailSuppressionService(
            sp.GetRequiredService<CommunityHub.Core.Data.CommunityHubDbContext>(),
            config["Email:UnsubscribeSecret"],
            sp.GetRequiredService<TimeProvider>()));
        services.AddScoped<CommunityHub.Core.Email.MailCampaignService>();
        // §304: pending-speaker approval — the import job mails info@ immediately
        // when new speakers arrive held from the Zoho flow.
        services.AddScoped<CommunityHub.Core.Organizer.SpeakerApprovalService>();
    }
}
