using CommunityHub.Core.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Reminders;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

// ===========================================================================
//  CommunityHub - web app entry point.
//  Stage 2: DbContext + /health. Stage 3: PIN authentication - the PIN
//  services, the IIdentityProvider seam, and a signed session cookie issued
//  SameSite=None so the hub works inside the Backstage iframe (CONTEXT.md 5a).
// ===========================================================================

var builder = WebApplication.CreateBuilder(args);

// --- Configuration: SQL connection -----------------------------------------
//  Credential-free template (from the Bicep) + password (from Key Vault),
//  composed here so no credential sits in a config file or the repo.
var sqlTemplate = builder.Configuration["Sql:ConnectionStringTemplate"]
                  ?? throw new InvalidOperationException(
                      "Sql:ConnectionStringTemplate is not configured.");
var sqlPassword = builder.Configuration["Sql:AdminPassword"]
                  ?? throw new InvalidOperationException(
                      "Sql:AdminPassword is not configured.");
var sqlUser = builder.Configuration["Sql:AdminUser"] ?? "communityhubadmin";
var connectionString =
    $"{sqlTemplate}User ID={sqlUser};Password={sqlPassword};";

// --- Services --------------------------------------------------------------
// EnableRetryOnFailure: hides Azure SQL Serverless cold-start (error 40613)
// from end users. On a paused DB the first request takes ~30-60s instead of
// erroring; storage is always-on so no data loss either way.
builder.Services.AddDbContext<CommunityHubDbContext>(options =>
    options.UseSqlServer(connectionString, sql =>
        sql.EnableRetryOnFailure(
            maxRetryCount:  6,
            maxRetryDelay:  TimeSpan.FromSeconds(30),
            errorNumbersToAdd: null)));

builder.Services.AddHealthChecks()
    .AddDbContextCheck<CommunityHubDbContext>("database");

// Clock abstraction - lets the PIN expiry logic be tested deterministically.
builder.Services.AddSingleton(TimeProvider.System);

// --- Email (Brevo SMTP) ----------------------------------------------------
//  SmtpUsername / SmtpKey are bound from Key Vault-backed config; the rest
//  from appsettings. See EmailOptions.
builder.Services.Configure<EmailOptions>(
    builder.Configuration.GetSection(EmailOptions.SectionName));
builder.Services.AddSingleton<IEmailSender, BrevoEmailSender>();

// --- PIN authentication ----------------------------------------------------
builder.Services.AddScoped<PinService>();
builder.Services.AddScoped<PinLoginService>();
// The IIdentityProvider seam. PinIdentityProvider is the only implementation
// today; a verified-SSO provider would be registered the same way later.
builder.Services.AddScoped<IIdentityProvider, PinIdentityProvider>();

// --- Email templates + welcome email ---------------------------------------
builder.Services.Configure<EmailTemplateOptions>(
    builder.Configuration.GetSection(EmailTemplateOptions.SectionName));
builder.Services.AddSingleton<EmailTemplateProvider>();
builder.Services.AddScoped<WelcomeEmailService>();

// --- Sessionize speaker import (organizer uploads an Excel export) ---------
builder.Services.AddSingleton<SessionizeExcelParser>();
builder.Services.AddScoped<SessionizeImportService>();

// --- Reporting / dashboard -------------------------------------------------
builder.Services.AddScoped<CommunityHub.Core.Reporting.ReportingService>();

// --- Surveys (definitions in JSON under App_Data/Surveys/) ----------------
// Definitions are read from disk on first request per slug then cached for
// the app lifetime. Restart picks up edits. Responses persist to the DB
// (SurveyResponse + SurveyResponsePick).
builder.Services.AddSingleton<CommunityHub.Surveys.SurveyDefinitionProvider>();

// --- Session cookie --------------------------------------------------------
//  SameSite=None + Secure so the cookie survives inside the cross-site
//  Backstage iframe (CONTEXT.md 5a). Without this the browser drops it and
//  the participant appears logged out on every navigation.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "communityhub.session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.None;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.LoginPath = "/Login";
        options.LogoutPath = "/Logout";
        options.AccessDeniedPath = "/Login";
    });

builder.Services.AddAuthorization();
builder.Services.AddRazorPages();

builder.Services.AddSingleton<CommunityHub.Branding.ActiveEventNameProvider>();
builder.Services.AddSingleton<CommunityHub.Auth.MagicLinkService>();
builder.Services.AddScoped<CommunityHub.Core.Reminders.OrganizerActionItemService>();
builder.Services.AddScoped<CommunityHub.Notify.HotelCalendarInviter>();

// Sponsor leads API auth: deterministic per-sponsor token derived from
// (EventId, SponsorCompanyId, TokenVersion, GlobalSecret). Durable since
// v1.2.6: token-version bumps (= revocations) persist in
// DbSet<SponsorTokenVersion> and survive restarts / slot swaps. Scoped
// because the implementation needs the per-request DbContext.
builder.Services.AddScoped<
    CommunityHub.Core.Integrations.Sponsors.IDeterministicSponsorTokenService,
    CommunityHub.Core.Integrations.Sponsors.DbDeterministicSponsorTokenService>();

// Legacy issued-key path -- kept registered so existing tokens still
// validate during the transition; durable since v1.2.6
// (DbSet<SponsorApiKey>; the in-memory scaffold lost keys on restart).
builder.Services.AddScoped<
    CommunityHub.Core.Integrations.Sponsors.ISponsorApiKeyService,
    CommunityHub.Core.Integrations.Sponsors.DbSponsorApiKeyService>();

// --- Zoho + sponsor leads pipeline (web side) ------------------------------
// The Leads admin page's "Sync now" fires the same CRM pull the nightly job
// runs. Gated by Zoho__Enabled + Zoho__CrmEnabled (both default false), so
// registering it is inert until the CRM integration is switched on.
var zohoWebOptions = new CommunityHub.Core.Integrations.ZohoOptions();
builder.Configuration.GetSection(CommunityHub.Core.Integrations.ZohoOptions.SectionName).Bind(zohoWebOptions);
builder.Services.AddSingleton(zohoWebOptions);
builder.Services.AddHttpClient<CommunityHub.Core.Integrations.ZohoClient>();
builder.Services.AddSingleton<CommunityHub.Core.Integrations.Sponsors.SponsorLeadScreeningService>();
builder.Services.AddScoped<CommunityHub.Core.Integrations.Sponsors.SponsorLeadSyncService>();

// API routing (the SponsorLeadsController under /api/v1/sponsors/{id}/...).
// AddControllers is additive on top of AddRazorPages, so the existing
// page surface keeps working.
builder.Services.AddControllers();

// SpeakerDeadlineSeeder: web-side invocation so a speaker's deadline tasks
// appear on their first hub visit, not only when the Functions ReminderJob
// runs. The seeder is idempotent (SourceKey-keyed), so calling it from
// /Index on every speaker page-load is safe -- duplicates are impossible.
var speakerDeadlineOptions = new CommunityHub.Core.Config.SpeakerDeadlineOptions();
builder.Configuration
    .GetSection(CommunityHub.Core.Config.SpeakerDeadlineOptions.SectionName)
    .Bind(speakerDeadlineOptions);
builder.Services.AddSingleton(speakerDeadlineOptions);
builder.Services.AddScoped<CommunityHub.Core.Config.SpeakerDeadlineSeeder>();

// --- Company Manager (sponsor-side company + contacts source of truth) ----
// Used by /Sponsor/Index to render a read-only "Sponsor details" card
// pulled from the webshop (company name, website, LinkedIn, X) with a
// button back to the configurator for updates.
var cmOptions = new CommunityHub.Core.Integrations.CompanyManagerOptions();
builder.Configuration.GetSection(CommunityHub.Core.Integrations.CompanyManagerOptions.SectionName).Bind(cmOptions);
builder.Services.AddSingleton(cmOptions);
builder.Services.AddHttpClient<CommunityHub.Core.Integrations.CompanyManagerClient>();

// --- WooCommerce (sponsor orders, displayed on /Sponsor/Index) ------------
// Web-side WooCommerce client renders the "Sponsor orders" section on
// /Sponsor/Index. Same shape as the Jobs / OneShot wiring.
var wooOptions = new CommunityHub.Core.Integrations.WooCommerceOptions();
builder.Configuration.GetSection(CommunityHub.Core.Integrations.WooCommerceOptions.SectionName).Bind(wooOptions);
builder.Services.AddSingleton(wooOptions);
builder.Services.AddHttpClient<CommunityHub.Core.Integrations.WooCommerceClient>();
// In-memory cache for sponsor-orders rendering: WooCommerce + products
// enrichment is ~2-3 seconds and the same sponsor will refresh the page
// repeatedly. 5-minute TTL keyed on company id.
builder.Services.AddMemoryCache();

// Event-edition facts + cross-cutting placeholders ({{configuratorUrl}}
// etc.) for rendering the "update this data in the webshop" link.
var eventConfigOptions = new CommunityHub.Core.Config.EventConfigOptions();
builder.Configuration.GetSection(CommunityHub.Core.Config.EventConfigOptions.SectionName).Bind(eventConfigOptions);
builder.Services.AddSingleton(eventConfigOptions);
builder.Services.AddSingleton<CommunityHub.Core.Config.EventEditionConfigLoader>();

// --- Current-participant accessor (Stage 4) --------------------------------
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<
    CommunityHub.Auth.ICurrentParticipantAccessor,
    CommunityHub.Auth.HttpCurrentParticipantAccessor>();

var app = builder.Build();

// --- Pipeline --------------------------------------------------------------
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

// Force Danish locale on every request so all date/number rendering is
// dd-MM-yyyy (Monday-first week, comma decimal separator). Both
// CurrentCulture (number/date formatting) and CurrentUICulture (resource
// lookup) are pinned -- the app is single-language Danish.
var daDk = new CultureInfo("da-DK");
CultureInfo.DefaultThreadCurrentCulture   = daDk;
CultureInfo.DefaultThreadCurrentUICulture = daDk;
app.UseRequestLocalization(new RequestLocalizationOptions
{
    DefaultRequestCulture = new RequestCulture(daDk),
    SupportedCultures     = new[] { daDk },
    SupportedUICultures   = new[] { daDk },
});

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

// frame-ancestors CSP: allow the hub to be embedded ONLY by the configured
// Backstage origin (CONTEXT.md 5a). Empty config => no embedding allowed.
// Razor Pages' antiforgery default also emits `X-Frame-Options: SAMEORIGIN`,
// which modern browsers prioritise over CSP frame-ancestors -- so we strip
// it here, otherwise the Backstage iframe is blocked even when CSP allows it.
var backstageOrigin = app.Configuration["Embedding:BackstageOrigin"];
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var frameAncestors = string.IsNullOrWhiteSpace(backstageOrigin)
            ? "'none'"
            : backstageOrigin;
        context.Response.Headers["Content-Security-Policy"] =
            $"frame-ancestors {frameAncestors};";
        context.Response.Headers.Remove("X-Frame-Options");
        return Task.CompletedTask;
    });
    await next();
});

app.MapHealthChecks("/health");
app.MapRazorPages();
app.MapControllers();

app.Run();
