using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Reminders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// ===========================================================================
//  CommunityHub.OneShot - DEV CLI that runs one hub job once and exits.
//  Mirrors CommunityHub.Jobs/Program.cs's DI exactly so what runs here
//  matches what runs in Azure Functions. Drives:
//     pull-sponsors    - the sponsor-order pull (WooCommerce -> tasks)
//
//  Usage:
//     dotnet run --project tools/CommunityHub.OneShot -- pull-sponsors
//  Env:
//     DOTNET_ENVIRONMENT=Development         (loads appsettings.Development.json,
//                                             which sets Email__RedirectAllTo)
//     Sql__ConnectionStringTemplate          (required)
//     Sql__AdminPassword                     (required)
//     WooCommerce__ConsumerKey               (required when pull-sponsors)
//     WooCommerce__ConsumerSecret            (required when pull-sponsors)
//     Email__RedirectAllTo                   (DEV only - defaults via
//                                             appsettings.Development.json to
//                                             mok@expertslive.dk)
// ===========================================================================

// Known commands. Adding a new command = add it here + add a switch arm
// below. Validate before any DI bootstrap so an unknown command doesn't
// trip the SQL / WooCommerce config checks.
var knownCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "pull-sponsors",
    "watch-uploads",
    "import-speakers",
    "import-plan",
    "send-sample-emails",
};

if (args.Length == 0 || args[0] is "-h" or "--help" || !knownCommands.Contains(args[0]))
{
    if (args.Length > 0 && !knownCommands.Contains(args[0]) && args[0] is not ("-h" or "--help"))
    {
        Console.Error.WriteLine($"Unknown command: {args[0]}");
    }
    Console.Error.WriteLine("Usage: communityhub-oneshot <command>");
    Console.Error.WriteLine("Commands:");
    Console.Error.WriteLine("  pull-sponsors    Run the WooCommerce sponsor-order pull once.");
    Console.Error.WriteLine("  watch-uploads    Poll provisioned SharePoint upload folders + email recipients on file changes.");
    Console.Error.WriteLine("  import-speakers  Pull speakers from the Sessionize v2 view API into the active edition.");
    Console.Error.WriteLine("  import-plan --file <path.csv> [--dry-run]");
    Console.Error.WriteLine("                   Import a semicolon volunteer-plan CSV into the active edition");
    Console.Error.WriteLine("                   (buckets from Responsible Team; upserts by name, so re-running is safe).");
    Console.Error.WriteLine("  send-sample-emails --to <addr> [--sponsor <name>]");
    Console.Error.WriteLine("                   Render EVERY shipped email template with sample 2LINKIT data and send each (subjects prefixed [SAMPLE]).");
    return 1;
}

var command = args[0];

var builder = Host.CreateApplicationBuilder(args);

// Lets DOTNET_ENVIRONMENT=Development pick up appsettings.Development.json
// (which sets Email__RedirectAllTo=mok@expertslive.dk). User secrets carry
// the per-developer SQL admin password + WooCommerce REST creds.
builder.Configuration
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables();

var config = builder.Configuration;
var services = builder.Services;

// ---------------------------------------------------------------------------
// EF Core - SAME wiring as CommunityHub.Jobs/Program.cs.
// ---------------------------------------------------------------------------
var sqlTemplate = config["Sql:ConnectionStringTemplate"]
    ?? throw new InvalidOperationException("Sql:ConnectionStringTemplate is not configured.");
// §327k — ENTRA TOKEN path. PROD SQL is Entra-only (no SQL logins exist), so the
// user/password wiring below can only ever reach a server that still has one. When
// CEH_SQL_ACCESS_TOKEN is set (from `az account get-access-token --resource
// https://database.windows.net/`), authenticate with that instead. Passed by ENVIRONMENT,
// never config and never a file, so no credential is left on disk.
var sqlAccessToken = Environment.GetEnvironmentVariable("CEH_SQL_ACCESS_TOKEN");

if (!string.IsNullOrWhiteSpace(sqlAccessToken))
{
    services.AddDbContext<CommunityHubDbContext>(options =>
    {
        var conn = new Microsoft.Data.SqlClient.SqlConnection(sqlTemplate)
        {
            AccessToken = sqlAccessToken,
        };
        options.UseSqlServer(conn, sql =>
            sql.EnableRetryOnFailure(
                maxRetryCount: 6,
                maxRetryDelay: TimeSpan.FromSeconds(30),
                errorNumbersToAdd: null));
    });
}
else
{
    var sqlPassword = config["Sql:AdminPassword"]
        ?? throw new InvalidOperationException(
            "Sql:AdminPassword is not configured and no CEH_SQL_ACCESS_TOKEN was supplied. "
            + "PROD SQL is Entra-only — supply a database.windows.net access token.");
    var sqlUser = config["Sql:AdminUser"] ?? "communityhubadmin";
    var connectionString = $"{sqlTemplate}User ID={sqlUser};Password={sqlPassword};";

    services.AddDbContext<CommunityHubDbContext>(options =>
        options.UseSqlServer(connectionString, sql =>
            sql.EnableRetryOnFailure(
                maxRetryCount: 6,
                maxRetryDelay: TimeSpan.FromSeconds(30),
                errorNumbersToAdd: null)));
}

services.AddSingleton(TimeProvider.System);

// ---------------------------------------------------------------------------
// Email - registered so future commands (reminders) can use it. In DEV the
// appsettings.Development.json bound here sets Email:RedirectAllTo=
// mok@expertslive.dk so any outbound mail is rerouted to the operator inbox.
// ---------------------------------------------------------------------------
services.Configure<EmailOptions>(config.GetSection(EmailOptions.SectionName));
services.AddSingleton<IEmailSender, BrevoEmailSender>();
services.Configure<EmailTemplateOptions>(config.GetSection(EmailTemplateOptions.SectionName));
// Resolve the email-template directory to an ABSOLUTE path that exists, walking
// up from the CWD then the binary dir to find <root>/templates/emails. The
// templates are NOT copied to the OneShot output, so a bare relative default
// only works when CWD == repo root; this makes `send-sample-emails` robust to
// being launched from anywhere. Honors an explicit EmailTemplates:TemplateDirectory.
services.PostConfigure<EmailTemplateOptions>(o =>
{
    if (Directory.Exists(o.TemplateDirectory)) return;
    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "templates", "emails");
            if (File.Exists(Path.Combine(candidate, "_layout.html")))
            {
                o.TemplateDirectory = candidate;
                return;
            }
            dir = dir.Parent;
        }
    }
});
services.AddSingleton<EmailTemplateProvider>();

// ---------------------------------------------------------------------------
// WooCommerce + sponsor pipeline - identical to CommunityHub.Jobs.
// ---------------------------------------------------------------------------
var wooOptions = new WooCommerceOptions();
config.GetSection(WooCommerceOptions.SectionName).Bind(wooOptions);
services.AddSingleton(wooOptions);
services.AddHttpClient<WooCommerceClient>();

var sponsorConfigOptions = new SponsorConfigOptions();
config.GetSection(SponsorConfigOptions.SectionName).Bind(sponsorConfigOptions);
services.AddSingleton(sponsorConfigOptions);
services.AddSingleton<SponsorConfigLoader>();

// Event-edition facts + cross-cutting placeholders.
var eventConfigOptions = new EventConfigOptions();
config.GetSection(EventConfigOptions.SectionName).Bind(eventConfigOptions);
services.AddSingleton(eventConfigOptions);
services.AddSingleton<EventEditionConfigLoader>();

// Company Manager (sponsor contact sync) -- chained into the pull so a
// freshly-onboarded sponsor's coordinators get Participant rows automatically.
var cmOptions = new CompanyManagerOptions();
config.GetSection(CompanyManagerOptions.SectionName).Bind(cmOptions);
services.AddSingleton(cmOptions);
// Bounded, jittered transient-fault retry (5xx/408/429/timeout) for Company Manager calls.
services.AddHttpClient<CompanyManagerClient>()
    .AddHttpMessageHandler(() => new CommunityHub.Core.Integrations.TransientFaultRetryHandler());
services.AddScoped<SponsorContactSyncService>();

// SharePoint upload-folder provisioning + watcher (used by pull-sponsors to
// pre-create per-task folders, and by watch-uploads to email recipients on
// file changes).
var sharePointOptions = new SharePointUploadOptions();
config.GetSection(SharePointUploadOptions.SectionName).Bind(sharePointOptions);
services.AddSingleton(sharePointOptions);
services.AddHttpClient<SharePointUploadClient>();
services.AddScoped<SponsorUploadWatchService>();

services.AddScoped<SponsorOrderPullService>();

// ---------------------------------------------------------------------------
// §327k — volunteer plan import (import-plan). The HEURISTIC guidance generator is
// used deliberately rather than the LLM one: an import must be deterministic and must
// not depend on an AI endpoint being reachable or on what it happens to return.
// ---------------------------------------------------------------------------
services.AddSingleton(new CommunityHub.Core.Volunteers.TaskGuidanceOptions());
services.AddSingleton<CommunityHub.Core.Volunteers.HeuristicTaskGuidanceGenerator>();
services.AddScoped<CommunityHub.Core.Volunteers.ITaskGuidanceGenerator>(sp =>
    sp.GetRequiredService<CommunityHub.Core.Volunteers.HeuristicTaskGuidanceGenerator>());
services.AddScoped<CommunityHub.Core.Volunteers.VolunteerPlanImportService>();

// ---------------------------------------------------------------------------
// Sessionize speaker import via the v2 view API - same wiring as the Jobs app.
// ---------------------------------------------------------------------------
var sessionizeOptions = new SessionizeApiOptions();
config.GetSection(SessionizeApiOptions.SectionName).Bind(sessionizeOptions);
services.AddSingleton(sessionizeOptions);
services.AddHttpClient<SessionizeApiClient>();
services.AddScoped<WelcomeEmailService>();
services.AddScoped<SessionizeImportService>();
// Sessions are pulled from the same v2 view API and linked to speakers.
services.AddScoped<SessionImportService>();
services.AddScoped<SessionizeApiImportService>();

using var host = builder.Build();
var logger = host.Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger("OneShot");

logger.LogInformation(
    "OneShot starting: env={Env}, command={Cmd}, RedirectAllTo={Redirect}",
    builder.Environment.EnvironmentName,
    command,
    string.IsNullOrEmpty(wooOptions.BaseUrl) ? "(no Woo cfg)" : "(see EmailOptions)");

switch (command)
{
    case "pull-sponsors":
        {
            using var scope = host.Services.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<SponsorOrderPullService>();
            var result = await svc.RunAsync(CancellationToken.None);
            logger.LogInformation(
                "pull-sponsors result: orders={Orders}, tasksCreated={Created}, companiesSynced={Co}, contactsCreated={CC}, contactsUpdated={CU}, ran={Ran}, skipReason={Skip}",
                result.OrdersFetched, result.TasksCreated, result.ContactSyncCompanies,
                result.ContactsCreated, result.ContactsUpdated,
                result.RanToCompletion, result.SkipReason ?? "<none>");
            return result.RanToCompletion ? 0 : 2;
        }

    case "send-sample-emails":
        {
            // CLI-ONLY review path (REQUIREMENTS §7c): render every shipped email
            // template with sample 2LINKIT data + send each. Never runs in
            // build/test — only when explicitly invoked here.
            var to = GetArg(args, "--to") ?? string.Empty;
            var sponsor = GetArg(args, "--sponsor") ?? "2LINKIT";
            // Optional: send only ONE template (its key, e.g. "welcome-sponsor"),
            // for reviewing a single mail after an edit instead of the full set.
            var only = GetArg(args, "--only");

            var templates = host.Services.GetRequiredService<EmailTemplateProvider>();
            var sender = host.Services.GetRequiredService<IEmailSender>();
            var templateOptions = host.Services
                .GetRequiredService<Microsoft.Extensions.Options.IOptions<EmailTemplateOptions>>()
                .Value;

            return await CommunityHub.OneShot.SendSampleEmailsCommand.RunAsync(
                templates, sender, templateOptions, to, sponsor, logger, CancellationToken.None, only);
        }

    case "watch-uploads":
        {
            using var scope = host.Services.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<SponsorUploadWatchService>();
            var result = await svc.RunAsync(CancellationToken.None);
            logger.LogInformation(
                "watch-uploads result: folders={Loc}, files={Files}, new={New}, changed={Chg}, mails={Sent}, errors={Err}",
                result.LocationsChecked, result.FilesObserved, result.FilesNew,
                result.FilesChanged, result.NotificationsSent, result.Errors);
            return 0;
        }

    case "import-speakers":
        {
            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CommunityHubDbContext>();
            var activeEventId = await db.Events
                .Where(e => e.IsActive)
                .Select(e => (int?)e.Id)
                .FirstOrDefaultAsync(CancellationToken.None);
            if (activeEventId is null)
            {
                logger.LogWarning("import-speakers: no active event in DB.");
                return 2;
            }

            var svc = scope.ServiceProvider.GetRequiredService<SessionizeApiImportService>();
            // sendWelcome:false -- DEV pull never emails speakers.
            var result = await svc.ImportAsync(
                activeEventId.Value, CancellationToken.None, sendWelcome: false);
            logger.LogInformation(
                "import-speakers result: fetched={Fetched}, created={Created}, updated={Updated}, skipped={Skipped}, warnings={Warn}, error={Err}",
                result.Fetched, result.Created, result.Updated, result.Skipped,
                result.Warnings.Count, result.Error ?? "<none>");
            foreach (var w in result.Warnings) logger.LogWarning("  {Warning}", w);
            if (result.Sessions is { } sx)
            {
                logger.LogInformation(
                    "import-speakers sessions: fetched={Fetched}, created={Created}, updated={Updated}, links=+{LinksCreated}/-{LinksRemoved}, error={Err}",
                    sx.Fetched, sx.Created, sx.Updated, sx.LinksCreated, sx.LinksRemoved, sx.Error ?? "<none>");
                foreach (var w in sx.Warnings) logger.LogWarning("  {Warning}", w);
            }
            return result.Error is null ? 0 : 2;
        }

    // §327k — import a volunteer plan CSV without the browser upload on
    // /Organizer/BucketAllocation. Deliberately reuses the SHIPPED parser + import service
    // rather than writing SQL: the upsert-by-name semantics, the ExternalKey identity and
    // the name-matching (which never invents people) are the behaviour that makes a
    // re-import safe, and a hand-rolled INSERT would have none of it.
    case "import-plan":
        {
            var fileArg = GetArg(args, "--file");
            if (string.IsNullOrWhiteSpace(fileArg) || !File.Exists(fileArg))
            {
                Console.Error.WriteLine("import-plan: --file <path.csv> is required and must exist.");
                return 1;
            }
            var dryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);

            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CommunityHubDbContext>();
            var activeEventId = await db.Events
                .Where(e => e.IsActive)
                .Select(e => (int?)e.Id)
                .FirstOrDefaultAsync(CancellationToken.None);
            if (activeEventId is null)
            {
                logger.LogWarning("import-plan: no active event in DB.");
                return 2;
            }

            var csv = await File.ReadAllTextAsync(fileArg, CancellationToken.None);
            var plan = new CommunityHub.Core.Volunteers.VolunteerPlanParser().Parse(csv);

            logger.LogInformation(
                "import-plan: parsed {Tasks} task(s) into {Buckets} bucket(s) from {File}; teams: {Teams}",
                plan.Tasks.Count, plan.Buckets.Count, Path.GetFileName(fileArg),
                string.Join(", ", plan.Tasks.Select(t => t.ResponsibleTeam ?? "(none)")
                                            .Distinct().OrderBy(t => t)));

            if (dryRun)
            {
                logger.LogInformation("import-plan: --dry-run, nothing was written.");
                return 0;
            }

            var import = scope.ServiceProvider
                .GetRequiredService<CommunityHub.Core.Volunteers.VolunteerPlanImportService>();
            var res = await import.ImportAsync(
                activeEventId.Value, plan, fillGuidance: true, CancellationToken.None);

            logger.LogInformation(
                "import-plan result: buckets={Buckets}, tasks={Tasks}, assignmentsLinked={Links}, "
                + "guidanceFilled={Guidance}, namesUnmatched={Unmatched}",
                res.BucketsCreated, res.TasksCreated, res.AssignmentsLinked,
                res.GuidanceFilled, res.NamesUnmatched);
            foreach (var n in res.UnmatchedNames) logger.LogWarning("  unmatched name: {Name}", n);
            return 0;
        }

    default:
        // Unreachable - the top-of-file guard already rejected unknown commands.
        // Kept so the switch is exhaustive if a new command id is added to
        // knownCommands but its arm is forgotten.
        Console.Error.WriteLine($"Command '{command}' is not wired in Program.cs.");
        return 1;
}

// --- CLI helpers ------------------------------------------------------------
// Read a "--flag value" argument (case-insensitive flag). Returns null when the
// flag is absent or has no following value.
static string? GetArg(string[] argv, string flag)
{
    for (var i = 0; i < argv.Length - 1; i++)
    {
        if (string.Equals(argv[i], flag, StringComparison.OrdinalIgnoreCase))
        {
            return argv[i + 1];
        }
    }
    return null;
}
