using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
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

        var sqlTemplate = config["Sql:ConnectionStringTemplate"]
                          ?? throw new InvalidOperationException(
                              "Sql:ConnectionStringTemplate is not configured.");
        var sqlPassword = config["Sql:AdminPassword"]
                          ?? throw new InvalidOperationException(
                              "Sql:AdminPassword is not configured.");
        var sqlUser = config["Sql:AdminUser"] ?? "communityhubadmin";
        var connectionString =
            $"{sqlTemplate}User ID={sqlUser};Password={sqlPassword};";

        // EnableRetryOnFailure: silently retries Azure SQL Serverless cold-start
        // (error 40613) - jobs wait ~30-60s instead of failing.
        services.AddDbContext<CommunityHubDbContext>(options =>
            options.UseSqlServer(connectionString, sql =>
                sql.EnableRetryOnFailure(
                    maxRetryCount:  6,
                    maxRetryDelay:  TimeSpan.FromSeconds(30),
                    errorNumbersToAdd: null)));

        services.AddSingleton(TimeProvider.System);

        services.Configure<EmailOptions>(
            config.GetSection(EmailOptions.SectionName));
        services.AddSingleton<IEmailSender, BrevoEmailSender>();

        // --- Email templates (branded reminder rendering) -------------------
        services.Configure<EmailTemplateOptions>(
            config.GetSection(EmailTemplateOptions.SectionName));
        services.AddSingleton<EmailTemplateProvider>();

        services.AddScoped<ReminderEngine>();
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
    })
    .Build();

host.Run();
