using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
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
var sqlPassword = config["Sql:AdminPassword"]
    ?? throw new InvalidOperationException("Sql:AdminPassword is not configured.");
var sqlUser = config["Sql:AdminUser"] ?? "communityhubadmin";
var connectionString = $"{sqlTemplate}User ID={sqlUser};Password={sqlPassword};";

services.AddDbContext<CommunityHubDbContext>(options =>
    options.UseSqlServer(connectionString, sql =>
        sql.EnableRetryOnFailure(
            maxRetryCount: 6,
            maxRetryDelay: TimeSpan.FromSeconds(30),
            errorNumbersToAdd: null)));

services.AddSingleton(TimeProvider.System);

// ---------------------------------------------------------------------------
// Email - registered so future commands (reminders) can use it. In DEV the
// appsettings.Development.json bound here sets Email:RedirectAllTo=
// mok@expertslive.dk so any outbound mail is rerouted to the operator inbox.
// ---------------------------------------------------------------------------
services.Configure<EmailOptions>(config.GetSection(EmailOptions.SectionName));
services.AddSingleton<IEmailSender, BrevoEmailSender>();
services.Configure<EmailTemplateOptions>(config.GetSection(EmailTemplateOptions.SectionName));
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
services.AddHttpClient<CompanyManagerClient>();
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

    default:
        // Unreachable - the top-of-file guard already rejected unknown commands.
        // Kept so the switch is exhaustive if a new command id is added to
        // knownCommands but its arm is forgotten.
        Console.Error.WriteLine($"Command '{command}' is not wired in Program.cs.");
        return 1;
}
