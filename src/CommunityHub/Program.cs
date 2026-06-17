using CommunityHub.Core.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Reminders;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
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
//  Credential-free template (from the Bicep). In Azure we authenticate to SQL
//  with the app's system-assigned managed identity (passwordless). For local
//  dev a SQL login+password can be supplied via Sql:AdminPassword as a
//  fallback. The presence of Sql:AdminPassword decides which path is used so
//  the same code works both in Azure (MI) and on a developer box (SQL auth).
var sqlTemplate = builder.Configuration["Sql:ConnectionStringTemplate"]
                  ?? throw new InvalidOperationException(
                      "Sql:ConnectionStringTemplate is not configured.");
var sqlPassword = builder.Configuration["Sql:AdminPassword"];
string connectionString;
if (!string.IsNullOrWhiteSpace(sqlPassword))
{
    // Local-dev fallback: SQL login + password.
    var sqlUser = builder.Configuration["Sql:AdminUser"] ?? "communityhubadmin";
    connectionString = $"{sqlTemplate}User ID={sqlUser};Password={sqlPassword};";
}
else
{
    // Azure: passwordless via the app's system-assigned managed identity.
    connectionString = $"{sqlTemplate}Authentication=Active Directory Managed Identity;";
}

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
// Central audit-log path (10a-3): every send flows through LoggingEmailSender,
// which records an EmailLog row then delegates to the real Brevo sender. The
// ambient EmailContext lets callers tag a send (category/edition/participant).
builder.Services.AddSingleton<BrevoEmailSender>();
builder.Services.AddSingleton<IEmailContextAccessor, EmailContextAccessor>();
builder.Services.AddSingleton<IEmailSender>(sp => new LoggingEmailSender(
    sp.GetRequiredService<BrevoEmailSender>(),
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<IEmailContextAccessor>(),
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<EmailOptions>>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetService<Microsoft.Extensions.Logging.ILogger<LoggingEmailSender>>()));

// --- Email system services (10a) -------------------------------------------
builder.Services.AddScoped<ParticipantEmailService>();
builder.Services.AddScoped<OnboardingEmailService>();
builder.Services.AddScoped<OnboardingStepResetEmailService>();
builder.Services.AddScoped<CalendarInviteEmailService>();
builder.Services.AddScoped<SpeakerQuestionDigestService>();
builder.Services.AddScoped<CommunityHub.Core.Organizer.ParticipantActivationService>();

// --- PIN authentication ----------------------------------------------------
builder.Services.AddScoped<PinService>();
builder.Services.AddScoped<PinLoginService>();
// The IIdentityProvider seam. PinIdentityProvider is the only implementation
// today; a verified-SSO provider would be registered the same way later.
builder.Services.AddScoped<IIdentityProvider, PinIdentityProvider>();

// --- Email templates + welcome email ---------------------------------------
builder.Services.Configure<EmailTemplateOptions>(
    builder.Configuration.GetSection(EmailTemplateOptions.SectionName));

// Public "become a sponsor" CTA (REQUIREMENTS §21) — a public sponsorship-contact
// address/URL the prospective-sponsor button points at. Not a secret, but the
// shipped config carries only a placeholder so a real address never lands in the
// public mirror; blank ⇒ the CTA is hidden (no dead link).
builder.Services.Configure<CommunityHub.Core.Sponsors.BecomeSponsorOptions>(
    builder.Configuration.GetSection(CommunityHub.Core.Sponsors.BecomeSponsorOptions.SectionName));
builder.Services.AddSingleton<EmailTemplateProvider>();
builder.Services.AddScoped<WelcomeEmailService>();

// Welcome email for all roles with one-click auto-login (DEV-only, re-sendable).
// IEnvironmentInfo backs the Core service's DEV-only hard guard; the magic-link
// token factory mints the same auto-login token the invitation flow uses.
builder.Services.AddSingleton<CommunityHub.Core.Email.IEnvironmentInfo,
    CommunityHub.Email.HostEnvironmentInfo>();
builder.Services.AddSingleton<CommunityHub.Core.Auth.IMagicLinkTokenFactory>(sp =>
    sp.GetRequiredService<CommunityHub.Auth.MagicLinkService>());
builder.Services.AddScoped<CommunityHub.Core.Reminders.WelcomeWithLoginEmailService>();

// --- Sessionize speaker import (organizer uploads an Excel export) ---------
builder.Services.AddSingleton<SessionizeExcelParser>();
builder.Services.AddScoped<SessionizeImportService>();

// --- Sessionize speaker import via the v2 view API (JSON pull) -------------
// Same upsert semantics as the Excel path; the endpoint id is plain operator
// config (NOT a secret), bound from the Sessionize section of
// integrations.<edition>.json / the gitignored sessionize.<edition>.custom.json.
var sessionizeApiOptions = new CommunityHub.Core.Integrations.SessionizeApiOptions();
builder.Configuration
    .GetSection(CommunityHub.Core.Integrations.SessionizeApiOptions.SectionName)
    .Bind(sessionizeApiOptions);
builder.Services.AddSingleton(sessionizeApiOptions);
builder.Services.AddHttpClient<CommunityHub.Core.Integrations.SessionizeApiClient>();
// Sessions are pulled from the same v2 view API and linked to the speakers.
builder.Services.AddScoped<SessionImportService>();
builder.Services.AddScoped<SessionizeApiImportService>();
// Import DRY-RUN / preview: reads the same source + applies the same merge rules
// as the real import but never writes, so the organizer sees created/updated/skipped
// + exactly which speaker bios would be overwritten before confirming (REQUIREMENTS §21).
builder.Services.AddScoped<SessionizeImportPreviewService>();
// Organizer endpoint admin settings + endpoint-change handling (Replace/Merge).
// Persists the per-edition endpoint id + chosen change mode; updates the in-process
// SessionizeApiOptions so the live client uses a newly-saved id without a restart.
builder.Services.AddScoped<
    CommunityHub.Core.Integrations.SessionizeEndpointSettingsService>();

// --- Session management (hub-only sessions, type/length, room, QR, eval) ---
// Organizers add hub-only sessions (e.g. sponsor sessions) alongside imports,
// set Type/Length/Room, drive per-room QR provisioning and email evaluation
// results to speakers. The QR storage seam defaults to the no-op Null provider
// (CanProvision=false): the real SharePoint site/drive/SPN are operator config
// (Key Vault) not in this repo, so no SharePoint call is faked (◻ pending).
// Swap in a live IRoomQrProvider — no caller changes — once wired.
builder.Services.AddSingleton<
    CommunityHub.Core.Integrations.IRoomQrProvider,
    CommunityHub.Core.Integrations.NullRoomQrProvider>();
builder.Services.AddScoped<SessionManagementService>();
builder.Services.AddScoped<SessionEvaluationMailService>();
builder.Services.AddScoped<CommunityHub.Core.Reminders.PublicSessionsService>();
builder.Services.AddScoped<CommunityHub.Core.Reminders.PublicSpeakersService>();
builder.Services.AddScoped<CommunityHub.Core.Reminders.PublicSponsorsService>();
builder.Services.AddScoped<CommunityHub.Core.Reminders.PublicLandingService>();
builder.Services.AddScoped<CommunityHub.Core.Domain.SessionQuestionService>();
// Per-session attendee EVALUATION (HappyOrNot-style 1–5 rating + comment): a public,
// no-login submit page reached via the room QR (reusing the same Session.PublicToken as
// the ask page), one rating per attendee/session (cookie-soft), and an organizer
// results dashboard with per-session + per-room aggregates. Future ◻: own-devices-via-API
// ingestion would populate the same SessionEvaluation rows — no caller changes.
builder.Services.AddScoped<CommunityHub.Core.Domain.SessionEvaluationService>();

// --- Master-class master-class features (REQUIREMENTS § 6c) ----------------
// 1) Public logistics page: per-master-class no-auth page where an involved
//    speaker OR an organizer publishes/edits setup instructions; minted slug.
// 2) Zoho Booking 1-way participant sync (Booking -> hub), per-master-class
//    endpoint mapped by organizers in master-class management. The fetch seam
//    defaults to the no-op Null fetcher (CanFetch=false): the real Booking
//    endpoint URI is per-master-class operator config and the creds are Key
//    Vault, so no Booking call is faked (🟡 pending). Swap in a live
//    IMasterClassBookingFetcher here once wired — no caller changes.
builder.Services.AddScoped<CommunityHub.Core.Reminders.MasterClassLogisticsService>();
builder.Services.AddSingleton<
    CommunityHub.Core.Integrations.IMasterClassBookingFetcher,
    CommunityHub.Core.Integrations.NullMasterClassBookingFetcher>();
builder.Services.AddScoped<CommunityHub.Core.Integrations.MasterClassBookingSyncService>();

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
        // Not "/Login": an authorized-but-forbidden hit would 302 to /Login, which now
        // redirects an already-signed-in user back to the protected page -> redirect loop.
        // Send them to the hub instead.
        options.AccessDeniedPath = "/";
    });

// Stable DataProtection application name. The cookie (and the magic-link tokens) are
// encrypted with the DataProtection key ring; by default the ring is ISOLATED by an
// app discriminator derived from the content-root path, which changes between deploys
// on App Service — so every deploy silently invalidated existing cookies and forced a
// re-login even when the user chose "stay signed in until I sign out". Pinning the
// application name keeps the key ring stable across deploys/restarts, so persistent
// sessions actually persist. (Keys themselves persist to the App Service %HOME% store.)
builder.Services.AddDataProtection().SetApplicationName("CommunityHub-EventHub");

builder.Services.AddAuthorization();

// --- Localization (i18n: English default + Danish) -------------------------
// Resources live in CommunityHub.Core under /Resources (SharedResource.resx =
// en/invariant fallback, SharedResource.da-DK.resx = Danish satellite). The
// marker type CommunityHub.Core.Resources.SharedResource sits in the matching
// namespace, so its full type name already equals the embedded resource base
// name — ResourcesPath stays EMPTY (a non-empty path would be inserted a second
// time and the lookup would miss). Views resolve strings via the
// IStringLocalizer<SharedResource> injected as `Localizer` in _ViewImports.
// First slice externalizes the high-traffic participant pages; deeper pages stay
// English-only for now (tracked ◻ in REQUIREMENTS). No schema/DB involvement.
builder.Services.AddLocalization();

// Supported cultures. en is the default + invariant fallback; da-DK is the
// Danish satellite. Adding a culture = adding a SharedResource.<culture>.resx
// plus an entry here (and a switcher option in _Layout).
var supportedCultures = new[]
{
    new CultureInfo("en"),
    new CultureInfo("da-DK"),
};
var localizationOptions = new RequestLocalizationOptions
{
    DefaultRequestCulture = new RequestCulture("en"),
    SupportedCultures     = supportedCultures,
    SupportedUICultures   = supportedCultures,
};
// Negotiation order: explicit cookie (set by the language switcher) wins, then
// the browser's Accept-Language header, then the en default. The query-string
// provider is dropped so a stray ?culture= can't override a user's choice.
localizationOptions.RequestCultureProviders =
    localizationOptions.RequestCultureProviders
        .Where(p => p is not Microsoft.AspNetCore.Localization
            .QueryStringRequestCultureProvider)
        .ToList();

builder.Services.AddRazorPages()
    .AddViewLocalization();

builder.Services.AddSingleton<CommunityHub.Branding.ActiveEventNameProvider>();
builder.Services.AddSingleton<CommunityHub.Auth.MagicLinkService>();
builder.Services.AddScoped<CommunityHub.Core.Reminders.OrganizerActionItemService>();
builder.Services.AddScoped<CommunityHub.Core.Reminders.FormChangeRequestService>();
builder.Services.AddScoped<CommunityHub.Core.Organizer.ParticipantBulkOperationService>();
builder.Services.AddScoped<CommunityHub.Core.Organizer.ParticipantDeletionService>();
builder.Services.AddScoped<CommunityHub.Core.Organizer.ParticipantSearchService>();
builder.Services.AddScoped<CommunityHub.Core.Organizer.SessionDeletionService>();
builder.Services.AddScoped<CommunityHub.Core.Organizer.SessionBulkOperationService>();
builder.Services.AddScoped<CommunityHub.Core.Organizer.SpeakerDeletionService>();
builder.Services.AddScoped<CommunityHub.Core.Organizer.SponsorInfoDeletionService>();
builder.Services.AddScoped<CommunityHub.Core.Organizer.VolunteerTaskBulkOperationService>();
builder.Services.AddScoped<CommunityHub.Core.Organizer.OrganizerOverviewService>();
builder.Services.AddScoped<CommunityHub.Core.Organizer.DataFreshnessService>();
builder.Services.AddScoped<CommunityHub.Core.Organizer.PreselectionQueueService>();
builder.Services.AddScoped<CommunityHub.Core.Organizer.OnboardingService>();
builder.Services.AddScoped<CommunityHub.Core.Organizer.CommandCenterService>();
builder.Services.AddScoped<CommunityHub.Core.Organizer.CommsCockpitService>();
builder.Services.AddScoped<CommunityHub.Core.Organizer.OrganizerExportsService>();
builder.Services.AddScoped<CommunityHub.Core.Organizer.SecretaryTokenService>();
builder.Services.AddScoped<CommunityHub.Core.Organizer.ImpersonationAuditService>();
builder.Services.AddScoped<CommunityHub.Core.Organizer.ModifyOnBehalfService>();
builder.Services.AddScoped<CommunityHub.Core.Domain.VolunteerStructureService>();
builder.Services.AddScoped<CommunityHub.Core.Volunteers.VolunteerScheduleBuilder>();
builder.Services.AddScoped<CommunityHub.Core.Volunteers.VolunteerShiftService>();
builder.Services.AddScoped<CommunityHub.Core.Email.VolunteerHelpNotificationService>();
builder.Services.AddScoped<CommunityHub.Notify.HotelCalendarInviter>();
builder.Services.AddScoped<CommunityHub.Core.Organizer.HotelManagementService>();
builder.Services.AddScoped<CommunityHub.Core.Organizer.HotelBulkOperationService>();

// --- Volunteer Buckets: plan import, gap detection, draft->commit allocation ---
builder.Services.AddScoped<CommunityHub.Core.Volunteers.VolunteerAllocationService>();
builder.Services.AddScoped<CommunityHub.Core.Volunteers.VolunteerPlanImportService>();
builder.Services.AddSingleton<CommunityHub.Core.Volunteers.VolunteerPlanParser>();

// AI task-guidance seam: heuristic fallback always available; the real LLM
// provider activates only when an API key is configured (secret via the existing
// config mechanism, NEVER committed). The bound key gates which generator is used.
var guidanceOptions = new CommunityHub.Core.Volunteers.TaskGuidanceOptions();
builder.Configuration
    .GetSection(CommunityHub.Core.Volunteers.TaskGuidanceOptions.SectionName)
    .Bind(guidanceOptions);
builder.Services.AddSingleton(guidanceOptions);
builder.Services.AddSingleton<CommunityHub.Core.Volunteers.HeuristicTaskGuidanceGenerator>();
if (guidanceOptions.IsConfigured)
{
    builder.Services.AddHttpClient<CommunityHub.Core.Volunteers.LlmTaskGuidanceGenerator>(
        c => c.BaseAddress = new Uri(guidanceOptions.BaseUrl));
    builder.Services.AddScoped<CommunityHub.Core.Volunteers.ITaskGuidanceGenerator>(sp =>
        sp.GetRequiredService<CommunityHub.Core.Volunteers.LlmTaskGuidanceGenerator>());
}
else
{
    // No key ⇒ the heuristic IS the generator (no network, no secret).
    builder.Services.AddScoped<CommunityHub.Core.Volunteers.ITaskGuidanceGenerator>(sp =>
        sp.GetRequiredService<CommunityHub.Core.Volunteers.HeuristicTaskGuidanceGenerator>());
}

// --- Calendar sync (per-user subscribable iCal feed) -----------------------
// The token service mints/resolves the per-participant feed token; the builder
// renders the participant's deadlines / shifts / tasks as an RFC 5545
// VCALENDAR. Both are scoped (per-request DbContext). The feed itself is served
// by Api/CalendarController at GET /calendar/{token}.ics (no session).
builder.Services.AddScoped<CommunityHub.Core.Reminders.CalendarFeedTokenService>();
builder.Services.AddScoped<CommunityHub.Core.Reminders.ParticipantCalendarBuilder>();
builder.Services.AddScoped<CommunityHub.Core.Participants.ParticipantChecklistBuilder>();
// Sponsor portal — single self-service home aggregate (REQUIREMENTS §20 Sponsor).
// Read-only over existing seams (checklist builder, company-name chain, ERP links).
builder.Services.AddScoped<CommunityHub.Core.Integrations.Sponsors.SponsorPortalService>();

// Speaker contact-email override -> Zoho Backstage propagation. The hub's own
// mail/calendar already use the override; this keeps the external event system
// in step. ◻ Live Backstage speaker-email wiring is pending: there is no
// documented/implemented Backstage speaker contact-email endpoint, so the
// default IBackstageSpeakerEmailApi is the no-op null writer (CanWrite=false)
// and the propagation service records the desired address in the
// SpeakerBackstageEmailSync queue for a future drainer. Swap in a live writer
// here when an endpoint is wired -- no caller changes.
builder.Services.AddSingleton<
    CommunityHub.Core.Integrations.IBackstageSpeakerEmailApi,
    CommunityHub.Core.Integrations.NullBackstageSpeakerEmailApi>();
builder.Services.AddScoped<CommunityHub.Core.Integrations.SpeakerEmailPropagationService>();

// OUTBOUND speaker-BIO sync to Zoho Backstage (hub -> Backstage). INACTIVE by
// default and never publishes an unselected speaker:
//  * Backstage:SpeakerBioSync:Enabled defaults FALSE -- no automatic/scheduled
//    run; a manual opt-in trigger (organizer action / CLI) is the only way to run.
//  * HARD GATE: a speaker is pushed PUBLIC only when SpeakerProfile.SelectedForPublish
//    is explicitly true (defaults false for everyone -- the lineup is not selected
//    yet); otherwise the bio goes out DRAFT/hidden only.
//  * Live wiring is (square) pending: the real Backstage portal/event ids + OAuth
//    creds are operator config (gitignored config/ or Key Vault), not in this repo,
//    so the default IBackstageSpeakerBioApi is the no-op Null writer (CanWrite=false)
//    -- the gated request is built (for dry-run) but no Zoho call is faked. Swap in
//    a live writer here once an endpoint + creds are wired AND the lineup is selected.
builder.Services.Configure<CommunityHub.Core.Integrations.BackstageSpeakerBioSyncOptions>(
    builder.Configuration.GetSection(
        CommunityHub.Core.Integrations.BackstageSpeakerBioSyncOptions.SectionName));
builder.Services.AddSingleton<
    CommunityHub.Core.Integrations.IBackstageSpeakerBioApi,
    CommunityHub.Core.Integrations.NullBackstageSpeakerBioApi>();
builder.Services.AddScoped<CommunityHub.Core.Integrations.SpeakerBioBackstageSyncService>();

// --- SoMe graphics & SharePoint asset store (REQUIREMENTS §18) -------------
// The compositing engine is pure (ImageSharp); the picture fetcher hits real
// Sessionize picture URLs at import time. The SharePoint file store is GATED:
// the Graph-backed store is selected ONLY when Graphics:SharePoint is configured
// (site URL present) AND the upload client has SPN creds; otherwise the null
// store (CanStore=false) is the default and nothing is faked. Per-user social
// OAuth is not wired — the draft-only share gateway builds drafts, never posts.
builder.Services.AddSingleton<CommunityHub.Core.Integrations.Graphics.GraphicCompositor>();
builder.Services.AddHttpClient<
    CommunityHub.Core.Integrations.Graphics.ISpeakerPictureFetcher,
    CommunityHub.Core.Integrations.Graphics.HttpSpeakerPictureFetcher>();
builder.Services.AddSingleton<
    CommunityHub.Core.Integrations.Graphics.ISocialShareGateway,
    CommunityHub.Core.Integrations.Graphics.DraftOnlySocialShareGateway>();

builder.Services.Configure<CommunityHub.Core.Integrations.Graphics.GraphicsSharePointOptions>(
    builder.Configuration.GetSection(
        CommunityHub.Core.Integrations.Graphics.GraphicsSharePointOptions.SectionName));
var graphicsSpOptions = new CommunityHub.Core.Integrations.Graphics.GraphicsSharePointOptions();
builder.Configuration
    .GetSection(CommunityHub.Core.Integrations.Graphics.GraphicsSharePointOptions.SectionName)
    .Bind(graphicsSpOptions);
if (graphicsSpOptions.IsConfigured)
{
    // Live Graph store: needs the SharePoint upload client (SPN creds, deployment
    // scoped, Key Vault) + the per-edition site/drive/root (operator config).
    var spUploadOptions = new CommunityHub.Core.Integrations.SharePointUploadOptions();
    builder.Configuration
        .GetSection(CommunityHub.Core.Integrations.SharePointUploadOptions.SectionName)
        .Bind(spUploadOptions);
    builder.Services.AddSingleton(spUploadOptions);
    builder.Services.AddHttpClient<CommunityHub.Core.Integrations.SharePointUploadClient>();
    builder.Services.AddScoped<
        CommunityHub.Core.Integrations.Graphics.ISharePointFileStore,
        CommunityHub.Core.Integrations.Graphics.GraphSharePointFileStore>();
}
else
{
    // ◻ Not wired: null store (CanStore=false) — engine computes the stable
    // path/intended URL but stores nothing live and fakes no call.
    builder.Services.AddSingleton<
        CommunityHub.Core.Integrations.Graphics.ISharePointFileStore,
        CommunityHub.Core.Integrations.Graphics.NullSharePointFileStore>();
}

builder.Services.AddScoped<CommunityHub.Core.Integrations.Graphics.GraphicsService>();
builder.Services.AddScoped<CommunityHub.Core.Integrations.Graphics.AssetLocationService>();
// Read-only contract that EXPOSES publishable branding graphics to a downstream
// consumer (the §19 SoMe queue, or any other) — the release/visibility gate is
// already applied so a consumer only sees what is safe to publish. Graphics never
// references a SoMe type; the consumer calls this. (REQUIREMENTS §18.)
builder.Services.AddScoped<
    CommunityHub.Core.Integrations.Graphics.IBrandingGraphicsProvider,
    CommunityHub.Core.Integrations.Graphics.BrandingGraphicsProvider>();

// --- LinkedIn company-page SoMe scheduling queue (REQUIREMENTS §19) ---------
// An organizer-curated, scheduled queue that posts to the event's LinkedIn
// COMPANY PAGE on a timer (distinct from the per-user draft-only share gateway
// above). The publisher is GATED: the default is the no-op Null publisher
// (CanPublish=false) — NOTHING posts until a live publisher is wired AND posting
// is enabled with a company page. The company-page URL/id is operator config
// (NOT a secret); the LinkedIn OAuth access token IS a secret (read from Key
// Vault by the live publisher — secret name only in committed files). Swap in a
// live ILinkedInPostPublisher here once wired — no caller changes.
builder.Services.AddSingleton<
    CommunityHub.Core.Integrations.ILinkedInPostPublisher,
    CommunityHub.Core.Integrations.NullLinkedInPostPublisher>();
builder.Services.AddScoped<CommunityHub.Core.Integrations.SoMeSettingsService>();
builder.Services.AddScoped<CommunityHub.Core.Integrations.SoMeQueueService>();
builder.Services.AddScoped<CommunityHub.Core.Integrations.SoMeDispatchService>();

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
builder.Services.AddScoped<CommunityHub.Core.Integrations.Sponsors.SponsorLeadCaptureService>();

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

// SpeakerMilestoneService: the read-model behind the /Speaker hub progress
// tracker. Reads the speaker's seeded deadline tasks (speakerdl: SourceKey)
// and derives countdown + status; also flips a speaker's own milestone
// done/open. Scoped (per-request DbContext).
builder.Services.AddScoped<CommunityHub.Core.Reminders.SpeakerMilestoneService>();

// SpeakerSessionsService: the read-model behind the Speaker hub "My sessions"
// card (room/time, master-class, attendee-questions links). Own-row scoped --
// only the signed-in speaker's own linked sessions. Scoped (per-request DbContext).
builder.Services.AddScoped<CommunityHub.Core.Reminders.SpeakerSessionsService>();

// SpeakerEvaluationsService: the read-model behind the Speaker self-service
// "My session ratings" page (per-session attendee evaluation count / average /
// anonymous comments). Own-row scoped -- only the signed-in speaker's own linked
// sessions. Read-only. Scoped (per-request DbContext).
builder.Services.AddScoped<CommunityHub.Core.Reminders.SpeakerEvaluationsService>();

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

// --- Database schema (apply EF Core migrations at startup) ------------------
//  The web app's managed identity holds db_ddladmin, so it can create/upgrade
//  the schema on boot. This replaces the manual `dotnet ef database update`
//  step and guarantees an empty (e.g. freshly provisioned prod) database gets
//  its schema before the first request. Idempotent: applies only pending
//  migrations.
//
//  Resilient retry: Migrate() opens its own connection that is NOT covered by
//  the DbContext's EnableRetryOnFailure execution strategy, so a serverless
//  cold-start (40613) OR a transient Entra token-principal resolution blip
//  ("Login failed for user '<token-identified principal>'") right after the MI
//  DB user is created would otherwise throw and crash the container on boot
//  (Linux App Service then never passes the warm-up probe -> crash-loop).
//  We retry with backoff so first-boot timing does not take the app down.
{
    var migrateLog = app.Services.GetRequiredService<ILoggerFactory>()
        .CreateLogger("Startup.Migrate");
    const int maxAttempts = 10;
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CommunityHubDbContext>();
            db.Database.Migrate();
            migrateLog.LogInformation(
                "Database migrations applied (attempt {Attempt}).", attempt);
            break;
        }
        catch (Exception ex) when (attempt < maxAttempts)
        {
            var delay = TimeSpan.FromSeconds(Math.Min(30, attempt * 5));
            migrateLog.LogWarning(ex,
                "Startup migration attempt {Attempt}/{Max} failed; retrying in {Delay}s.",
                attempt, maxAttempts, delay.TotalSeconds);
            Thread.Sleep(delay);
        }
    }
}

// --- Pipeline --------------------------------------------------------------
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

// Request localization: negotiate the per-request culture from the cookie set
// by the in-layout language switcher, then Accept-Language, then the en default
// (see localizationOptions above). CurrentUICulture drives resource lookup
// (resx) and the dynamic <html lang>; CurrentCulture drives date/number
// formatting. The date *picker* stays Danish-formatted via flatpickr regardless
// (dd/mm/yyyy, Monday-first) — the value posted to the server is ISO either way.
app.UseRequestLocalization(localizationOptions);

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

        // Every HTML page renders the shared layout, whose nav/menu varies by the
        // signed-in participant (and even by acting-as state). Such auth-varying
        // HTML must NEVER be cached by the browser, the back/forward cache, or any
        // shared proxy — otherwise one user's rendered menu can be shown to the
        // next visitor or after sign-out (e.g. the Login page briefly showing the
        // previous user's role menu). `no-store` disables HTTP cache AND bfcache,
        // so the page is always re-rendered for the current principal. Static files
        // are served earlier by UseStaticFiles and never reach this middleware, so
        // their long-lived caching is unaffected.
        var contentType = context.Response.ContentType;
        if (!string.IsNullOrEmpty(contentType) &&
            contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
            context.Response.Headers["Pragma"] = "no-cache";
            context.Response.Headers["Expires"] = "0";
        }
        return Task.CompletedTask;
    });
    await next();
});

app.MapHealthChecks("/health");

// Language switcher target. The in-layout switch posts here with the chosen
// culture; we persist it in the standard ASP.NET Core culture cookie (read back
// by CookieRequestCultureProvider on the next request) and redirect to where the
// user was. POST + antiforgery-free (no auth/state change beyond the cookie) so
// it works on anonymous pages (Login) too. `returnUrl` is treated as local-only.
app.MapPost("/set-language", (HttpContext http, string culture, string? returnUrl) =>
{
    var safe = supportedCultures.Any(c =>
        string.Equals(c.Name, culture, StringComparison.OrdinalIgnoreCase))
        ? culture
        : "en";
    http.Response.Cookies.Append(
        CookieRequestCultureProvider.DefaultCookieName,
        CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(safe)),
        new CookieOptions
        {
            Expires  = DateTimeOffset.UtcNow.AddYears(1),
            IsEssential = true,
            SameSite = SameSiteMode.None,   // survive the Backstage iframe (CONTEXT.md 5a)
            Secure   = true,
            Path     = "/",
        });
    var target = (!string.IsNullOrEmpty(returnUrl) && Uri.IsWellFormedUriString(returnUrl, UriKind.Relative))
        ? returnUrl
        : "/";
    return Results.LocalRedirect(target);
});

// Back-compat alias: the "Secure link" page was previously "/Organizer/SecretaryLink".
// Redirect old bookmarks/shared links to the new path, preserving the query string
// (e.g. ?participantId=123) so they don't 404.
app.MapGet("/Organizer/SecretaryLink", (HttpContext http) =>
{
    var qs = http.Request.QueryString.HasValue ? http.Request.QueryString.Value : string.Empty;
    return Results.LocalRedirect($"/Organizer/SecureLink{qs}");
});

app.MapRazorPages();
app.MapControllers();

app.Run();
