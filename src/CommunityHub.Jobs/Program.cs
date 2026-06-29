using CommunityHub.Jobs;
using CommunityHub.Core.Config;
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
using Microsoft.Extensions.Hosting;

// ===========================================================================
//  CommunityHub.Jobs - Azure Functions (scheduler) entry point.
//  Timer-only worker. Registers the same EF Core data model + email +
//  reminder engine as the web app (all from CommunityHub.Core) so the jobs
//  and the web app share one model and one set of integration logic.
// ===========================================================================

var host = new HostBuilder()
    // Middleware order matters: EngineErrorAlertMiddleware is OUTERMOST so it catches
    // any unhandled exception (incl. one in the pause check) and emails the developer;
    // JobsPauseMiddleware is the org-admin "pause all jobs" master switch — one central
    // guard so every timer job no-ops before doing any work when paused.
    .ConfigureFunctionsWorkerDefaults(worker =>
    {
        worker.UseMiddleware<EngineErrorAlertMiddleware>();
        worker.UseMiddleware<JobsPauseMiddleware>();
    })
    .ConfigureServices((context, services) =>
    {
        var config = context.Configuration;

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
        // Ring-gate every send at the sender (REQUIREMENTS §23): opens a scope per
        // send for RingResolver + FeatureGateService and reads the active edition
        // from the ambient EmailContext. Covers the reminder/digest job paths too.
        // The ring gate sits in front of the existing allowlist/redirect (intact).
        services.AddSingleton<BrevoEmailSender>(sp => new BrevoEmailSender(
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<EmailOptions>>(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IEmailContextAccessor>(),
            sp.GetService<Microsoft.Extensions.Logging.ILogger<BrevoEmailSender>>()));
        services.AddSingleton<IEmailSender>(sp => new LoggingEmailSender(
            sp.GetRequiredService<BrevoEmailSender>(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IEmailContextAccessor>(),
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<EmailOptions>>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<Microsoft.Extensions.Logging.ILogger<LoggingEmailSender>>()));
        // Ops/engine ALERT mail (ring-exempt so it reaches the developer mailbox, which is
        // not a ring-gated participant). Used by EngineErrorAlertMiddleware + the engines.
        services.AddSingleton<EngineAlertSender>();

        // --- Email system services (10a) -----------------------------------
        services.AddScoped<ParticipantEmailService>();
        services.AddScoped<OnboardingStepResetEmailService>();
        services.AddScoped<SpeakerQuestionDigestService>();
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

        // --- Speaker deadline seeding ---------------------------------------
        var speakerDeadlineOptions = new SpeakerDeadlineOptions();
        config.GetSection(SpeakerDeadlineOptions.SectionName)
            .Bind(speakerDeadlineOptions);
        services.AddSingleton(speakerDeadlineOptions);
        services.AddScoped<SpeakerDeadlineSeeder>();

        // §164: party sign-up task seeding — ensures the staff-role "party sign-up"
        // tasks exist so the reminder run below nags anyone who hasn't answered Yes/No.
        services.AddScoped<PartyTaskSeeder>();

        // --- WooCommerce (sponsor pipeline) ---------------------------------
        var wooOptions = new WooCommerceOptions();
        config.GetSection(WooCommerceOptions.SectionName).Bind(wooOptions);
        services.AddSingleton(wooOptions);
        services.AddHttpClient<WooCommerceClient>();

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

        // --- Sessionize (speaker import via v2 view API) -------------------
        var sessionizeOptions = new SessionizeApiOptions();
        config.GetSection(SessionizeApiOptions.SectionName).Bind(sessionizeOptions);
        services.AddSingleton(sessionizeOptions);
        services.AddHttpClient<SessionizeApiClient>();
        // Welcome path is shared with the API import route.
        services.AddScoped<WelcomeEmailService>();
        // Desired-state sponsor welcome reconcile (SponsorWelcomeReconcileJob).
        services.AddScoped<CommunityHub.Core.Reminders.SponsorWelcomeEmailService>();
        // One-shot email-feature enable (EnableEmailFeaturesJob, admin-triggered).
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
        services.AddScoped<SessionizeApiImportService>();

        // --- Company Manager (sponsor contact source of truth) -------------
        var cmOptions = new CompanyManagerOptions();
        config.GetSection(CompanyManagerOptions.SectionName).Bind(cmOptions);
        services.AddSingleton(cmOptions);
        // Bounded, jittered transient-fault retry (5xx/408/429/timeout) so a momentary
        // upstream blip from Company Manager doesn't crash a reconcile (2026-06-27 incident).
        services.AddHttpClient<CompanyManagerClient>()
            .AddHttpMessageHandler(() => new CommunityHub.Core.Integrations.TransientFaultRetryHandler());
        services.AddScoped<SponsorContactSyncService>();
        // "Alert only on 2 consecutive failures" gate for background jobs.
        services.AddScoped<CommunityHub.Core.Diagnostics.JobFailureTracker>();
        // Central wrapper used by EngineErrorAlertMiddleware so the same consecutive-failure
        // gate covers EVERY function uniformly (not just the one job that self-gates).
        services.AddScoped<CommunityHub.Core.Diagnostics.EngineFailureAlertGate>();

        // --- SharePoint (per-sponsor upload folders + change watcher) ------
        var sharePointOptions = new SharePointUploadOptions();
        config.GetSection(SharePointUploadOptions.SectionName).Bind(sharePointOptions);
        services.AddSingleton(sharePointOptions);
        // §102: SharePoint folder provisioning can be slow (per-folder Graph
        // walk + createLink). Raise the default 100s HttpClient timeout so a
        // single slow Graph call doesn't TaskCanceled-fail the WooCommerce pull.
        services.AddHttpClient<SharePointUploadClient>(c =>
            c.Timeout = TimeSpan.FromMinutes(5));
        services.AddScoped<SponsorUploadWatchService>();

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

        // The single sponsor-pull engine, shared with CommunityHub.OneShot.
        services.AddScoped<SponsorOrderPullService>();

        // --- Zoho (attendee reconciliation, CONTEXT.md 9z) ------------------
        var zohoOptions = new ZohoOptions();
        config.GetSection(ZohoOptions.SectionName).Bind(zohoOptions);
        services.AddSingleton(zohoOptions);
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

        // §38e/§58: SPEAKER change detection (Backstage speakers diff) — the speaker analogue.
        // A real change (name/tagline/bio/country/social) is ENQUEUED to the §59 delta-approval
        // queue (never auto-applied, never emails, never deletes). Gated per-edition on the
        // SPEAKER sync direction == stage 3 (ZohoToCeh) + the speaker-change-alerts feature. Run
        // by SpeakerChangeDetectionJob (hourly, :50). Inert until the speaker READ scope is
        // granted (Zoho:SpeakerReadEnabled).
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
            queueFactory: () => sp.GetRequiredService<CommunityHub.Core.Integrations.Sessions.SyncDeltaQueueService>()));
        services.AddScoped(sp => new CommunityHub.Core.Integrations.Sessions.SpeakerBackstagePushService(
            sp.GetRequiredService<CommunityHub.Core.Data.CommunityHubDbContext>(),
            sp.GetRequiredService<ZohoClient>(),
            sp.GetRequiredService<ZohoOptions>(),
            tokenOverride: null,
            queueFactory: () => sp.GetRequiredService<CommunityHub.Core.Integrations.Sessions.SyncDeltaQueueService>()));

        // STAGE 4b: create/link Zoho sponsor + exhibitor records from webshop data
        // after the order pull (replaces the legacy PowerShell sync). Run by
        // WooCommercePullJob, gated by 'sponsor-zoho-provision'.
        services.AddScoped<SponsorZohoProvisionService>();
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

        // ERP→webshop reconcile (the scheduled ErpWebshopReconcileJob, 30-min timer):
        // mirror the web app's registration so the job can resolve the sync service.
        // All other deps (CompanyManagerClient/Options, EmailSender) are already above.
        services.AddHttpClient<CommunityHub.Core.Integrations.Erp.IEconomicContactAdminClient,
            CommunityHub.Core.Integrations.Erp.LiveEconomicContactAdminClient>();
        services.AddScoped<CommunityHub.Core.Integrations.Erp.EconomicContactAdminService>();
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
        if (liOptions.Enabled && liOptions.HasCredentials)
        {
            services.AddHttpClient<CommunityHub.Core.Integrations.ILinkedInPostPublisher,
                CommunityHub.Core.Integrations.LiveLinkedInPostPublisher>();
        }
        else
        {
            services.AddSingleton<CommunityHub.Core.Integrations.ILinkedInPostPublisher,
                CommunityHub.Core.Integrations.NullLinkedInPostPublisher>();
        }
        services.AddScoped<CommunityHub.Core.Integrations.SoMeSettingsService>();
        services.AddScoped<CommunityHub.Core.Integrations.SoMeDispatchService>();
    })
    .Build();

host.Run();
