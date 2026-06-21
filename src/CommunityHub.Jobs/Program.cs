using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using Microsoft.AspNetCore.DataProtection;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Reminders;
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
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        var config = context.Configuration;

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
        services.AddScoped<CommunityHub.Core.Reminders.WelcomeWithLoginEmailService>();
        services.AddScoped<CommunityHub.Core.Reminders.AttendeeWelcomeProvisioningService>();

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

        // --- Sessionize (speaker import via v2 view API) -------------------
        var sessionizeOptions = new SessionizeApiOptions();
        config.GetSection(SessionizeApiOptions.SectionName).Bind(sessionizeOptions);
        services.AddSingleton(sessionizeOptions);
        services.AddHttpClient<SessionizeApiClient>();
        // Excel parser + welcome path are shared with the upload route.
        services.AddSingleton<SessionizeExcelParser>();
        services.AddScoped<WelcomeEmailService>();
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
        services.AddScoped<SessionizeApiImportService>();

        // --- Company Manager (sponsor contact source of truth) -------------
        var cmOptions = new CompanyManagerOptions();
        config.GetSection(CompanyManagerOptions.SectionName).Bind(cmOptions);
        services.AddSingleton(cmOptions);
        services.AddHttpClient<CompanyManagerClient>();
        services.AddScoped<SponsorContactSyncService>();

        // --- SharePoint (per-sponsor upload folders + change watcher) ------
        var sharePointOptions = new SharePointUploadOptions();
        config.GetSection(SharePointUploadOptions.SectionName).Bind(sharePointOptions);
        services.AddSingleton(sharePointOptions);
        services.AddHttpClient<SharePointUploadClient>();
        services.AddScoped<SponsorUploadWatchService>();

        // The single sponsor-pull engine, shared with CommunityHub.OneShot.
        services.AddScoped<SponsorOrderPullService>();

        // --- Zoho (attendee reconciliation, CONTEXT.md 9z) ------------------
        var zohoOptions = new ZohoOptions();
        config.GetSection(ZohoOptions.SectionName).Bind(zohoOptions);
        services.AddSingleton(zohoOptions);
        services.AddSingleton<AttendeeReconciler>();
        services.AddHttpClient<ZohoClient>();

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
        services.AddSingleton<CommunityHub.Core.Integrations.ILinkedInPostPublisher,
            CommunityHub.Core.Integrations.NullLinkedInPostPublisher>();
        services.AddScoped<CommunityHub.Core.Integrations.SoMeSettingsService>();
        services.AddScoped<CommunityHub.Core.Integrations.SoMeDispatchService>();
    })
    .Build();

host.Run();
