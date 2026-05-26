using CommunityHub.Core.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Reminders;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

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

app.Run();
