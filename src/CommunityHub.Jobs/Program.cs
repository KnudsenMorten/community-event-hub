using CommunityHub.Jobs;
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
    // 🔴 §784.15 — THE REGISTRATIONS LIVE IN JobsServiceRegistration, NOT HERE.
    //
    // They were lifted out of this file so a TEST can build this exact container. The reason is a
    // production fault: EvaluationReportPublishJob failed 18 times in a row because a service was
    // registered in the web host and not in this one, and nothing on the path from `dotnet build` to
    // `>> prod jobs deployed` could see it — the worker resolves a function's constructor per
    // invocation, so a missing registration surfaces on the first timer tick, not at startup.
    //
    // ⚠️ Add new registrations THERE. A registration added back into this lambda would be invisible
    // to JobDependenciesResolveTests, which is the only guard this host has.
    .ConfigureServices((context, services) =>
        JobsServiceRegistration.Register(services, context.Configuration))
    // 🔒 §570 — WHY THE WORKER'S OWN LOGS WERE INVISIBLE IN APP INSIGHTS.
    //
    // `AddApplicationInsightsTelemetryWorkerService()` installs a DEFAULT LoggerFilterRule for
    // ApplicationInsightsLoggerProvider at **Warning**, so everything the isolated worker writes
    // below Warning is dropped BEFORE reaching the telemetry pipeline — every `LogInformation` in
    // every job, including the push job's "created X, updated Y, failed Z, skipped W" outcome line.
    // The web app has no such rule, which is exactly why ITS "triggered manually." line appeared
    // while nothing from inside the worker ever did.
    //
    // The filter lives in WORKER DI, so neither `host.json` nor the `AzureFunctionsJobHost__logging__*`
    // app settings can reach it — both were tried and correctly changed nothing (they configure the
    // HOST, not the worker).
    //
    // ⚠️ ORDER IS THE WHOLE FIX, AND GETTING IT WRONG LOOKS EXACTLY LIKE GETTING IT RIGHT.
    // `IServiceCollection.Configure<T>` actions run in REGISTRATION order. The first attempt at this
    // put `.ConfigureLogging(...)` BEFORE `.ConfigureServices(...)` in the builder chain, so the
    // removal ran FIRST and `AddApplicationInsightsTelemetryWorkerService()` then re-added its rule
    // afterwards — the code read correctly, compiled, deployed, and changed nothing. It MUST stay
    // after the ConfigureServices block that registers the AI worker service.
    .ConfigureLogging(logging =>
    {
        logging.Services.Configure<Microsoft.Extensions.Logging.LoggerFilterOptions>(options =>
        {
            var aiRule = options.Rules.FirstOrDefault(r => r.ProviderName
                == "Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerProvider");
            if (aiRule is not null) options.Rules.Remove(aiRule);
        });
    })
    .Build();

// --- §768 DocLibrary configuration check ------------------------------------
// 🔒 Especially here: this host runs the sweeps, and it is the one whose settings were found missing
// six of the eighteen legacy keys. A named error at boot beats a quarter-hourly job that logs a
// confident zero.
CommunityHub.Core.Integrations.DocLibrary.DocLibraryStartupCheck.Run(
    host.Services.GetRequiredService<
        CommunityHub.Core.Integrations.DocLibrary.IDocLibraryPathResolver>(),
    host.Services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()
        .CreateLogger("DocLibrary"));

// --- §303 per-integration field maps (layer 2) ------------------------------
// Same fail-soft load as the web host: zoho-backstage.fieldmap.json overrides
// the ZohoFieldMap code defaults; a missing/invalid file logs and falls back.
{
    var mapLog = host.Services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()
        .CreateLogger("IntegrationFieldMap");
    CommunityHub.Core.Integrations.ZohoFieldMap.ApplyMapFile(
        CommunityHub.Core.Integrations.IntegrationFieldMap.LoadMapFile(
            CommunityHub.Core.Integrations.ZohoFieldMap.MapFilePath,
            err => Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(
                mapLog, "IntegrationFieldMap: {Error}", err)));
    // §323: the INBOUND Sessionize map — the importer (which runs IN THIS HOST) routes
    // category groups by the file's keywords; code defaults are the fallback.
    CommunityHub.Core.Integrations.SessionizeFieldMap.ApplyMapFile(
        CommunityHub.Core.Integrations.IntegrationFieldMap.LoadMapFile(
            CommunityHub.Core.Integrations.SessionizeFieldMap.MapFilePath,
            err => Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(
                mapLog, "IntegrationFieldMap: {Error}", err)));
}

host.Run();
