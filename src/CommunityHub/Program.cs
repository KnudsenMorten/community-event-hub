using CommunityHub.Core.Diagnostics;
using CommunityHub.Core.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Reminders;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
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

// 🔒 §783.10 — FAIL AT STARTUP, NOT ON ONE PAGE.
//
// /Organizer/Logistics returned HTTP 500 in production because `ExpoLogisticsProducer` was
// registered while its `ISponsorPurchaseSummary` dependency was not. Nothing could catch it: a
// missing DI registration compiles cleanly, every unit test passes (they construct services
// directly), and the post-deploy check only proved the ANONYMOUS auth gate answered — the failure
// lives past the login, in the page body.
//
// `ValidateOnBuild` resolves every registered service at container build time, so this exact defect
// becomes a STARTUP failure instead. That is strictly better here because PROD is deployed through
// the staging slot: a broken container fails the slot's warm-up and the deploy stops BEFORE the
// swap, so the fault never reaches a signed-in user. A page that 500s for one organizer is far
// harder to notice than a deploy that refuses to go green.
//
// `ValidateScopes` catches the sibling mistake — a singleton capturing a scoped DbContext, which
// corrupts state under concurrency rather than throwing anywhere near the cause.
builder.Host.UseDefaultServiceProvider((_, options) =>
{
    options.ValidateOnBuild = true;
    options.ValidateScopes = true;
});

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

// §391 — APPLICATION INSIGHTS FOR THE WEB TIER (operator 2026-07-26: "i experience many times with
// the master class add/cancel/move up it hangs … can we trace something to understand what is
// happening").
//
// The answer was NO, and this is why: APPLICATIONINSIGHTS_CONNECTION_STRING has been set on the web
// app all along, but the SDK was never registered — so the connection string went nowhere and the
// web tier emitted NOTHING. Confirmed against the live resource: 4h of telemetry contained 4,613
// dependencies / 102 requests, every one of them from the Functions app, and zero from the web role.
// So every hang he has reported was, by construction, invisible.
//
// With this in place we get per-request duration AND the SQL dependency underneath it, which is what
// separates the three candidate causes: seat-claim lock contention (a slow SQL dependency inside a
// fast-arriving request), a slot swap (a gap in requests + a cold start), and Zoho/Brevo latency (a
// slow outbound dependency). Guarded on the setting, so a local run with no connection string is
// unchanged — DEV in Azure DOES have one (operator 2026-07-26: "did you also add the telemetry to
// the dev env"), verified set on eldk27hub-web-devz237e against eldk27hub-ai-dev, so DEV emits too.
if (!string.IsNullOrWhiteSpace(
        builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
{
    builder.Services.AddApplicationInsightsTelemetry();
}

// §392 — the organizer telemetry dashboard READS that same App Insights resource. Registered
// unconditionally: the service reports "not configured" on the page rather than disappearing, so a
// missing setting is visible instead of silently removing a feature.
builder.Services.AddSingleton<CommunityHub.Telemetry.PlatformTelemetryService>();

// Clock abstraction - lets the PIN expiry logic be tested deterministically.
builder.Services.AddSingleton(TimeProvider.System);

// Generic CONTENT-HUB markdown renderer for the /Info/{slug} pages
// (REQUIREMENTS §104-§123). Stateless + thread-safe Markdig pipeline, so a
// singleton is fine.
builder.Services.AddSingleton<CommunityHub.Content.ContentMarkdownRenderer>();

// §680 — the per-role WELCOME copy for the Get-Started wizard's step 1
// (config/welcome/<edition>/<role>.md). SINGLETON: it caches one file read per role per
// process, and the four wizard services ask it on every wizard build. Registered here rather
// than picked up by the handler-discovery loop below because it lives in Core (the wizard
// services need it to decide whether to OFFER the step at all).
builder.Services.AddSingleton<CommunityHub.Core.Content.WelcomeCopyStore>();

// --- Email (Brevo SMTP) ----------------------------------------------------
//  SmtpUsername / SmtpKey are bound from Key Vault-backed config; the rest
//  from appsettings. See EmailOptions.
builder.Services.Configure<EmailOptions>(
    builder.Configuration.GetSection(EmailOptions.SectionName));
// Central audit-log path (10a-3): every send flows through LoggingEmailSender,
// which records an EmailLog row then delegates to the real Brevo sender. The
// ambient EmailContext lets callers tag a send (category/edition/participant).
builder.Services.AddSingleton<IEmailContextAccessor, EmailContextAccessor>();
// §219 (Risk-4): paces bulk email loops (reminder batch, attendee welcome loops)
// under Brevo's per-second rate limit. Reads Email:BulkSendDelayMs (default 150ms).
builder.Services.AddSingleton<IBulkSendPacer>(sp => new BulkSendPacer(
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<EmailOptions>>(),
    sp.GetService<TimeProvider>()));
// §234: delivered-vs-dropped seam. The SCOPED instance (injected into ledger
// callers: welcome services, ReminderEngine) installs a per-request/-flow outcome
// holder; the SINGLETON senders write into it via Detached() instances, so a
// ring-dropped send is never stamped/ledgered/audited as sent.
builder.Services.AddScoped<IEmailDeliveryOutcome, EmailDeliveryOutcome>();
// The Brevo sender ring-gates every send (REQUIREMENTS §23): it opens a scope per
// send for RingResolver + FeatureGateService (both scoped, registered below) and
// reads the active edition from the ambient EmailContext. Audience control is
// rings-only (no allowlist): ring gate + DEV RedirectAllTo + global KillSwitch.
builder.Services.AddSingleton<BrevoEmailSender>(sp => new BrevoEmailSender(
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<EmailOptions>>(),
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<IEmailContextAccessor>(),
    sp.GetService<Microsoft.Extensions.Logging.ILogger<BrevoEmailSender>>(),
    EmailDeliveryOutcome.Detached()));
builder.Services.AddSingleton<IEmailSender>(sp => new LoggingEmailSender(
    sp.GetRequiredService<BrevoEmailSender>(),
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<IEmailContextAccessor>(),
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<EmailOptions>>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetService<Microsoft.Extensions.Logging.ILogger<LoggingEmailSender>>(),
    EmailDeliveryOutcome.Detached()));

// --- Email system services (10a) -------------------------------------------
builder.Services.AddScoped<ParticipantEmailService>();
builder.Services.AddScoped<EmailResendService>();
builder.Services.AddScoped<OnboardingEmailService>();
builder.Services.AddScoped<OnboardingStepResetEmailService>();
builder.Services.AddScoped<CalendarInviteEmailService>();
builder.Services.AddScoped<SpeakerQuestionDigestService>();
builder.Services.AddScoped<CommunityHub.Core.Organizer.ParticipantActivationService>();
// Pure planner for the organizer test-send-to-an-arbitrary-address feature: it
// reads EmailOptions so the preview matches the sender's redirect/allowlist gate.
builder.Services.AddSingleton<EmailTestSendPlanner>();

// --- PIN authentication ----------------------------------------------------
builder.Services.AddScoped<PinService>();
builder.Services.AddScoped<PinLoginService>();
// §299 7.1: 1-day ticket-holder sign-in gate (attendee-1day-access ring rule) —
// consulted by PIN login, magic-link, /go AND the cookie revalidation backstop.
builder.Services.AddScoped<CommunityHub.Core.Auth.OneDayAccessGate>();
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

// Welcome-email options: auto-login link DISABLED by default (operator "disable
// welcome mail with login"); bound from the WelcomeEmail config section so it can
// be re-enabled per environment without a code change.
builder.Services.Configure<CommunityHub.Core.Reminders.WelcomeEmailOptions>(
    builder.Configuration.GetSection(CommunityHub.Core.Reminders.WelcomeEmailOptions.SectionName));
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CommunityHub.Core.Reminders.WelcomeEmailOptions>>().Value);

// Universal sponsor-email audience rule (REQUIREMENTS §7c): the single authority
// for "which contacts at a sponsor company receive this email" — coordinators
// only (signer-only excluded, both-roles included). The coordinator audience is
// resolved READ-ONLY from e-conomic ERP Role-2 data (ISponsorErpCoordinatorSource,
// registered below near Company Manager), with the manual IsEventCoordinator flag
// as an additive override and a fail-soft fallback to the CM default. Explicit
// factory so the ERP-source ctor is always chosen. The allowlist still gates sends.
builder.Services.AddScoped<SponsorRecipientResolver>(sp => new SponsorRecipientResolver(
    sp.GetRequiredService<CommunityHub.Core.Data.CommunityHubDbContext>(),
    sp.GetService<CommunityHub.Core.Email.ISponsorErpCoordinatorSource>()));
builder.Services.AddScoped<CommunityHub.Core.Reminders.SponsorWelcomeEmailService>();

// Welcome email for all roles with one-click auto-login (DEV-only, re-sendable).
// IEnvironmentInfo backs the Core service's DEV-only hard guard. The welcome's
// auto-login link is a SINGLE-USE, short-lived, revocable, audited token
// (WelcomeAutoLoginTokenService + a MagicLinkGrant row), distinct from the
// reusable invitation magic-link (IMagicLinkTokenFactory).
builder.Services.AddSingleton<CommunityHub.Core.Email.IEnvironmentInfo,
    CommunityHub.Email.HostEnvironmentInfo>();
builder.Services.AddSingleton<CommunityHub.Core.Auth.IMagicLinkTokenFactory>(sp =>
    sp.GetRequiredService<CommunityHub.Auth.MagicLinkService>());
builder.Services.AddScoped<CommunityHub.Core.Auth.IWelcomeAutoLoginTokenService,
    CommunityHub.Core.Auth.WelcomeAutoLoginTokenService>();
// Organizer admin (list / revoke) + housekeeping (prune) for welcome grants.
builder.Services.AddScoped<CommunityHub.Core.Auth.WelcomeGrantAdminService>();
// §169 personal email magic-link: the long-lived (365-day), REUSABLE, per-
// participant auto-login link that every in-hub email CTA routes through (via the
// EmailTemplateProvider hubUrl seam) and that the /go page redeems. Distinct
// DataProtection purpose + grant Purpose ("email") from the single-use welcome
// grants above. Resolved per-send (scoped) by the singleton EmailTemplateProvider.
builder.Services.AddScoped<CommunityHub.Core.Auth.EmailMagicLinkService>();
builder.Services.AddScoped<CommunityHub.Core.Auth.IEmailMagicLinkService>(sp =>
    sp.GetRequiredService<CommunityHub.Core.Auth.EmailMagicLinkService>());
// Anonymous Party RSVP (public /Party form + organizer headcount).
builder.Services.AddScoped<CommunityHub.Core.Reminders.PartyRsvpService>();
// In-hub Master Class signup + waitlist (CEH-owned; replaces Zoho Bookings).
builder.Services.AddScoped<CommunityHub.Core.Reminders.MasterClassSignupService>();
// Ring-gated "you got a seat" promotion email (waitlist -> confirmed).
builder.Services.AddScoped<CommunityHub.Core.Email.MasterClassPromotionEmailService>();
// MC lifecycle emails (confirmed / waitlist-with-terms / cancellation) + .ics.
builder.Services.AddScoped<CommunityHub.Core.Email.MasterClassEmailService>();
builder.Services.AddScoped<CommunityHub.Core.Reminders.WelcomeWithLoginEmailService>();
// §236: attendee welcome resend from the organizer participant editor (reset-welcome).
builder.Services.AddScoped<CommunityHub.Core.Reminders.AttendeeOneDayWelcomeEmailService>();

// --- Sessionize speaker import (shared upsert core) ------------------------
builder.Services.AddScoped<SessionizeImportService>();

// --- Sessionize speaker import via the v2 view API (JSON pull) -------------
// The only import source (§82 — Excel upload removed); the endpoint id is plain operator
// config (NOT a secret), bound from the Sessionize section of
// integrations.<edition>.json / the gitignored sessionize.<edition>.custom.json.
var sessionizeApiOptions = new CommunityHub.Core.Integrations.SessionizeApiOptions();
builder.Configuration
    .GetSection(CommunityHub.Core.Integrations.SessionizeApiOptions.SectionName)
    .Bind(sessionizeApiOptions);
builder.Services.AddSingleton(sessionizeApiOptions);
builder.Services.AddHttpClient<CommunityHub.Core.Integrations.SessionizeApiClient>()
    .AddCredentialFailureAlert("Sessionize");
// Sessions are pulled from the same v2 view API and linked to the speakers.
builder.Services.AddScoped<SessionImportService>();
// Pluggable SESSION source (default Sessionize; Zoho Backstage when enabled) —
// REQUIREMENTS §6. The resolver picks the active source per edition from settings.
builder.Services.AddScoped<CommunityHub.Core.Integrations.Sessions.ISessionSource,
    CommunityHub.Core.Integrations.Sessions.SessionizeSessionSource>();
builder.Services.AddScoped<CommunityHub.Core.Integrations.Sessions.ISessionSource,
    CommunityHub.Core.Integrations.Sessions.BackstageSessionSource>();
builder.Services.AddScoped<CommunityHub.Core.Integrations.Sessions.SessionSourceSettingsService>();
builder.Services.AddScoped<CommunityHub.Core.Integrations.Sessions.SessionSourceResolver>();
// §58 NEVER-AUTO-DELETE: alert the operator about speakers/sessions that disappeared from
// Sessionize after an import (the import never deletes them). Delivery uses the ring-exempt
// EngineAlertSender (the ops mailbox is not a ring-gated participant), registered here so the
// web-triggered import path can deliver the alert too.
// §702 — which environment this process is, for the [DEV]/[PROD] alert-subject tag and the §703
// Settings badge. 🔒 NOT IHostEnvironment: ASPNETCORE_ENVIRONMENT is "Production" on DEV too
// (verified 2026-07-29), so that would label every DEV alert [PROD]. See HubEnvironment.
builder.Services.AddSingleton(sp => new CommunityHub.Core.Diagnostics.HubEnvironment(
    sp.GetRequiredService<IConfiguration>()["Hub:EnvironmentLabel"],
    Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME")));
builder.Services.AddSingleton<CommunityHub.Core.Email.EngineAlertSender>();
// RULE (operator 2026-07-23): every CEH-made Zoho Backstage write notifies
// info@expertslive.dk (publish/delete is manual in Backstage). Rides the ring-exempt
// EngineAlertSender above; batched per run, unthrottled. Constructor-injected as
// OPTIONAL into every Zoho-writing service (push/provision/sync/profile engines).
builder.Services.AddSingleton<CommunityHub.Core.Email.ZohoChangeNotifier>();
builder.Services.AddScoped<CommunityHub.Core.Reminders.SessionizeDisappearanceDetector>();
builder.Services.AddScoped<SessionizeApiImportService>();
// §198: the organizer "trigger import now" page depends on the import seam; map it to
// the concrete service so the on-demand button runs the SAME import as the timer job.
builder.Services.AddScoped<CommunityHub.Core.Reminders.ISessionizeApiImportService>(
    sp => sp.GetRequiredService<SessionizeApiImportService>());
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
builder.Services.AddScoped<CommunityHub.Core.Reminders.PublicAgendaService>();
builder.Services.AddScoped<CommunityHub.Core.Reminders.PublicSpeakersService>();
builder.Services.AddScoped<CommunityHub.Core.Reminders.PublicSponsorsService>();
builder.Services.AddScoped<CommunityHub.Core.Reminders.ScheduleService>();
builder.Services.AddScoped<CommunityHub.Core.Domain.SessionQuestionService>();
// 🗑 §748.1 — SessionEvaluationService (the 1–5 rating) is GONE. The live four-point channel is
// EvaluationQrService + the /f/{token} page. Its one still-used member, EnsurePublicTokenAsync,
// was a DUPLICATE of the identical method on SessionQuestionService above, which now serves both
// callers: the ask page and the feedback link mint the SAME Session.PublicToken, so nothing
// already shared breaks. Two copies of one token rule was the drift risk; there is now one.

// --- Master-class features (REQUIREMENTS §6) -------------------------------
// Public logistics page: per-master-class no-auth page where an involved
// speaker OR an organizer publishes/edits setup instructions; minted slug.
// (The legacy Zoho Booking 1-way participant sync was retired — CEH now OWNS
// master-class seats + waitlist via MasterClassSignup; see REQUIREMENTS §6.)
builder.Services.AddScoped<CommunityHub.Core.Reminders.MasterClassLogisticsService>();
// Master Class attendee LANDING PAGE (FEATURE 2): prep content + Q&A comments + 1:1
// private questions — the single server-side authority for that page's access model.
builder.Services.AddScoped<CommunityHub.Core.Reminders.MasterClassPrepService>();

// §383 — Master Class landing-page notifications + the per-person opt-outs behind the two toggles.
builder.Services.AddScoped<CommunityHub.Core.Reminders.MasterClassNotificationService>();

// --- Reporting / dashboard -------------------------------------------------
builder.Services.AddScoped<CommunityHub.Core.Reporting.ReportingService>();

// --- Surveys (definitions in JSON under App_Data/Surveys/) ----------------
// Definitions are read from disk on first request per slug then cached for
// the app lifetime. Restart picks up edits. Responses persist to the DB
// (SurveyResponse + SurveyResponsePick).
builder.Services.AddSingleton<CommunityHub.Surveys.SurveyDefinitionProvider>();
// 🔒 §6.5 — the SAME provider instance, seen through the Core seam. Core needs a definition for one
// thing only: the derived close date (event end + N months). Registering a SECOND instance here
// would give the app two definition caches that could disagree after an edit.
builder.Services.AddSingleton<CommunityHub.Core.Surveys.ISurveyDefinitionSource>(
    sp => sp.GetRequiredService<CommunityHub.Surveys.SurveyDefinitionProvider>());
// Survey response aggregation + organizer admin state (open/close, reset).
// Scoped: it uses the per-request DbContext.
builder.Services.AddScoped<CommunityHub.Core.Domain.SurveySummaryService>();
builder.Services.AddScoped<CommunityHub.Core.Surveys.SurveyQuestionSummaryService>();

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

        // §234 (4b): PER-SESSION IsActive/lifecycle REVALIDATION. The "remember me" /
        // magic-link cookie lives 365 days, so deactivating a participant (organizer
        // lock-out, ticket cancellation §216, offboarding) must not leave an already-
        // issued cookie usable for a year. On each request we re-check the participant
        // row behind the cookie — but at most once every 5 minutes per session: the
        // last-validated instant is stamped into the ticket's AuthenticationProperties
        // and the ticket is renewed (ShouldRenew) when the stamp goes stale, so the
        // steady-state cost is one indexed PK lookup per session per 5 minutes.
        // A missing, IsActive=false, or non-Active-lifecycle participant is rejected
        // AND signed out (cookie deleted) — the request then hits the fail-closed
        // FallbackPolicy and lands on /Login.
        options.Events = new CookieAuthenticationEvents
        {
            OnValidatePrincipal = async context =>
            {
                const string stampKey = ".communityhub.lastValidated";
                var now = DateTimeOffset.UtcNow;
                if (context.Properties.Items.TryGetValue(stampKey, out var stamp)
                    && DateTimeOffset.TryParse(
                        stamp, CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var last)
                    && now - last < TimeSpan.FromMinutes(5)
                    && last <= now)
                {
                    return;     // validated recently — skip the DB hit
                }

                // NameIdentifier = the participant id (see ParticipantSessionSignIn);
                // acting-as / secretary sessions carry the TARGET's id, so a
                // deactivated target also kills those sessions.
                var idClaim = context.Principal?.FindFirst(
                    System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                var stillValid = false;
                if (int.TryParse(idClaim, out var participantId))
                {
                    var db = context.HttpContext.RequestServices
                        .GetRequiredService<CommunityHubDbContext>();
                    stillValid = await db.Participants.AnyAsync(p =>
                        p.Id == participantId
                        && p.IsActive
                        && p.LifecycleState == CommunityHub.Core.Domain
                            .ParticipantLifecycleState.Active);
                }

                // §299 7.1 server-side backstop: a 1-day ticket holder outside the
                // one-day-hub-access ring is rejected even on an ALREADY-ISSUED
                // 365-day cookie — hiding the pages is not enforcement; without this,
                // a signed-in 1-day holder reaching a master-class or party endpoint
                // directly still lands in the seat + catering counts.
                if (stillValid)
                {
                    var oneDayGate = context.HttpContext.RequestServices
                        .GetService<CommunityHub.Core.Auth.OneDayAccessGate>();
                    if (oneDayGate is not null
                        && await oneDayGate.IsBlockedAsync(participantId))
                    {
                        stillValid = false;
                    }
                }

                if (!stillValid)
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync(
                        CookieAuthenticationDefaults.AuthenticationScheme);
                    return;
                }

                context.Properties.Items[stampKey] = now.ToString("O", CultureInfo.InvariantCulture);
                context.ShouldRenew = true;     // persist the fresh stamp into the ticket
            },
        };
    });

// Stable DataProtection application name. The cookie (and the magic-link tokens) are
// encrypted with the DataProtection key ring; by default the ring is ISOLATED by an
// app discriminator derived from the content-root path, which changes between deploys
// on App Service — so every deploy silently invalidated existing cookies and forced a
// re-login even when the user chose "stay signed in until I sign out". Pinning the
// application name keeps the key ring stable across deploys/restarts, so persistent
// sessions actually persist. (Keys themselves persist to the App Service %HOME% store.)
// Persist the key ring to the SHARED SQL DB (PersistKeysToDbContext) so auth cookies
// survive slot-swap deploys + restarts. Previously the keys lived in per-slot %HOME%,
// so every swap brought a worker with a DIFFERENT key ring and all existing cookies
// became undecryptable — users were logged out on every deploy. Both slots use the
// same SQL connection, so the key ring is now genuinely shared. The pinned application
// name keeps the discriminator stable on top of that.
builder.Services.AddDataProtection()
    .PersistKeysToDbContext<CommunityHubDbContext>()
    .SetApplicationName("CommunityHub-EventHub");

// FAIL-CLOSED authorization backstop: every endpoint that does NOT carry its own
// authorization metadata ([Authorize]/[AllowAnonymous]/RequireAuthorization) falls
// back to "must be an authenticated user". This guarantees a future page added
// without an explicit attribute is private by default rather than silently public.
// The public surfaces (Index/Agenda/Sessions/Speakers/Sponsors/MasterClass/Party/
// Volunteer-Signup/Survey/AttendeeTelemetry/Login/Login.Magic), the token-secured
// API controllers (PublicSessionCalendar/Calendar/Secretary/SponsorLeads) and the
// /health + /set-language endpoints are explicitly opted out with [AllowAnonymous]
// / .AllowAnonymous() so they keep working without a cookie.
builder.Services.AddAuthorization(o =>
    o.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build());

// --- Localization (i18n: English only) -------------------------------------
// Resources live in CommunityHub.Core under /Resources (SharedResource.resx =
// the en/invariant resource). The marker type
// CommunityHub.Core.Resources.SharedResource sits in the matching namespace, so
// its full type name already equals the embedded resource base name —
// ResourcesPath stays EMPTY (a non-empty path would be inserted a second time
// and the lookup would miss). Views resolve strings via the
// IStringLocalizer<SharedResource> injected as `Localizer` in _ViewImports.
// No schema/DB involvement.
builder.Services.AddLocalization();

// Supported cultures. ENGLISH-ONLY (operator directive 2026-06-18: "we only
// support english language, dont publish anything on danish"). The hub is never
// served in any other language; RequestLocalization always resolves to en
// regardless of the browser's Accept-Language. To introduce a language, add its
// CultureInfo here + a matching SharedResource.<culture>.resx satellite.
var supportedCultures = new[]
{
    new CultureInfo("en"),
};
var localizationOptions = new RequestLocalizationOptions
{
    DefaultRequestCulture = new RequestCulture("en"),
    SupportedCultures     = supportedCultures,
    SupportedUICultures   = supportedCultures,
};
// Negotiation order: explicit culture cookie wins, then the browser's
// Accept-Language header, then the en default. The query-string provider is
// dropped so a stray ?culture= can't override a user's choice. With en as the
// only supported culture, every request resolves to English regardless.
localizationOptions.RequestCultureProviders =
    localizationOptions.RequestCultureProviders
        .Where(p => p is not Microsoft.AspNetCore.Localization
            .QueryStringRequestCultureProvider)
        .ToList();

builder.Services.AddRazorPages()
    .AddViewLocalization()
    // Unified AUDIT TRAIL (REQUIREMENTS §24): a global page filter auto-captures every
    // mutating user action. Type-activated per request so its scoped deps resolve.
    .AddMvcOptions(o => o.Filters.Add<CommunityHub.Audit.AuditPageFilter>());

// Audit trail writer (append-only, edition-scoped). Scoped — holds the DbContext.
builder.Services.AddScoped<CommunityHub.Core.Audit.IAuditTrail, CommunityHub.Core.Audit.AuditTrailService>();

builder.Services.AddSingleton<CommunityHub.Branding.ActiveEventNameProvider>();
builder.Services.AddSingleton<CommunityHub.Auth.MagicLinkService>();
// --- Feature customization & controlled rollout (REQUIREMENTS §23) ---------
builder.Services.AddScoped<CommunityHub.Core.Settings.FeatureGateService>();
builder.Services.AddScoped<CommunityHub.Core.Settings.FeatureSettingsService>();
// Effective-ring resolver (controlled rollout §23) — the SAME resolver the GUI,
// engines and schedulers/jobs all use to decide a resource's release ring.
builder.Services.AddScoped<CommunityHub.Core.Settings.RingResolver>();
// Admin surface to assign rings per resource (company / contact / speaker / volunteer).
builder.Services.AddScoped<CommunityHub.Core.Settings.ResourceRingService>();
builder.Services.AddScoped<CommunityHub.Core.Reminders.OrganizerActionItemService>();
builder.Services.AddScoped<CommunityHub.Core.Reminders.FormChangeRequestService>();
// §253 G1: the ONE deactivation cascade every organizer entry point funnels
// through (grid toggle, soft-delete, bulk ops, data-grid row save + the G8b
// sponsor-company withdrawal).
builder.Services.AddScoped<CommunityHub.Core.Organizer.ParticipantDeactivationService>();
builder.Services.AddScoped<CommunityHub.Core.Organizer.ParticipantBulkOperationService>();
builder.Services.AddScoped<CommunityHub.Core.Organizer.ParticipantDeletionService>();
builder.Services.AddScoped<CommunityHub.Core.Organizer.AttendeeOnboardingResetService>();   // §355
builder.Services.AddScoped<CommunityHub.Core.Organizer.ParticipantOnboardingResetService>();// §355 speaker half
builder.Services.AddScoped<CommunityHub.Core.Organizer.SponsorOnboardingResetService>();    // §366
builder.Services.AddScoped<CommunityHub.Core.Organizer.ParticipantSearchService>();
builder.Services.AddScoped<CommunityHub.Core.Organizer.SessionDeletionService>();
builder.Services.AddScoped<CommunityHub.Core.Organizer.SessionBulkOperationService>();
builder.Services.AddScoped<CommunityHub.Core.Organizer.SpeakerDeletionService>();
builder.Services.AddScoped<CommunityHub.Core.Organizer.SponsorInfoDeletionService>();
builder.Services.AddScoped<CommunityHub.Core.Organizer.TestDataCleanupService>();
// §327: the organizer background-jobs page (inventory + per-job throttle).
builder.Services.AddScoped<CommunityHub.Core.Settings.JobScheduleService>();
// §635 — "is outbound e-mail itself down?" for the Jobs page banner. Read-only over EmailLogs;
// it exists because a dead mail relay is the one fault that cannot mail you about itself.
builder.Services.AddScoped<CommunityHub.Core.Diagnostics.EmailTransportHealth>();
builder.Services.AddScoped<CommunityHub.Core.Organizer.VolunteerTaskBulkOperationService>();
builder.Services.AddScoped<CommunityHub.Core.Organizer.OrganizerOverviewService>();
builder.Services.AddScoped<CommunityHub.Core.Entitlements.OrderCountService>();
// §6.10 / §768.10 D5 — the travel-claim freeze + the organizer reopen.
// 🔒 MUST be registered: TravelFormService takes it as an OPTIONAL parameter (so its existing
// constructions keep working), which means an unregistered lock does not throw — the claim would
// simply never freeze, silently. That is the §768.15 failure shape, and the reason this line
// carries a comment instead of sitting anonymously in the list.
// §6.10 — the freeze is OFF until TravelClaim:FreezeEnabled=true. It ships to production without
// changing any speaker's experience, and is switched on after the operator has validated it.
builder.Services.AddSingleton(sp =>
{
    var o = new CommunityHub.Core.Entitlements.TravelClaimLockOptions();
    builder.Configuration
        .GetSection(CommunityHub.Core.Entitlements.TravelClaimLockOptions.SectionName)
        .Bind(o);
    return o;
});
builder.Services.AddScoped<CommunityHub.Core.Entitlements.TravelClaimLock>();

// §6.9 — photo cleanup on deactivation. 🔒 THE ONLY SERVICE IN CEH THAT DELETES A DOCUMENT-LIBRARY
// FILE, so it ships DRY-RUN and stays that way until PhotoCleanup:DryRun is set to false. Bound from
// configuration so switching it on is an operator act with a diff, not a redeploy.
builder.Services.AddSingleton(sp =>
{
    var o = new CommunityHub.Core.Integrations.Graphics.PhotoCleanupOptions();
    builder.Configuration
        .GetSection(CommunityHub.Core.Integrations.Graphics.PhotoCleanupOptions.SectionName)
        .Bind(o);
    return o;
});
builder.Services.AddScoped<CommunityHub.Core.Integrations.Graphics.ParticipantPhotoCleanupService>();
builder.Services.AddScoped<CommunityHub.Core.Organizer.DataFreshnessService>();
builder.Services.AddScoped<CommunityHub.Core.Organizer.SyncHealthService>();
builder.Services.AddScoped<CommunityHub.Core.Organizer.PreselectionQueueService>();
// §59: delta-approval queue — the /Organizer/SyncQueue page approves/rejects detected
// sync changes (audited; approve applies + emails; never auto-applied). On approve, a
// CehToZoho Update PUSHES to Zoho via the push services (lazily resolved — they capture the
// queue lazily, so there is no construction cycle). The push services are registered below.
builder.Services.AddScoped(sp => new CommunityHub.Core.Integrations.Sessions.SyncDeltaQueueService(
    sp.GetRequiredService<CommunityHub.Core.Data.CommunityHubDbContext>(),
    clock: sp.GetService<TimeProvider>(),
    audit: sp.GetService<CommunityHub.Core.Audit.IAuditTrail>(),
    alerts: sp.GetService<CommunityHub.Core.Email.EngineAlertSender>(),
    sender: sp.GetService<CommunityHub.Core.Email.IEmailSender>(),
    context: sp.GetService<CommunityHub.Core.Email.IEmailContextAccessor>(),
    templates: sp.GetService<CommunityHub.Core.Email.EmailTemplateProvider>(),
    sessionPush: sp.GetService<CommunityHub.Core.Integrations.Sessions.SessionBackstagePushService>(),
    speakerPush: sp.GetService<CommunityHub.Core.Integrations.Sessions.SpeakerBackstagePushService>()));
// §57/§58 stage-2 push services — needed so an approved CehToZoho Update on the SyncQueue
// page can push to Zoho. They enqueue updates of linked records via a LAZY queue factory.
builder.Services.AddScoped(sp => new CommunityHub.Core.Integrations.Sessions.SessionBackstagePushService(
    sp.GetRequiredService<CommunityHub.Core.Data.CommunityHubDbContext>(),
    sp.GetRequiredService<CommunityHub.Core.Integrations.ZohoClient>(),
    sp.GetRequiredService<CommunityHub.Core.Integrations.ZohoOptions>(),
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
builder.Services.AddScoped(sp => new CommunityHub.Core.Integrations.Sessions.SpeakerBackstagePushService(
    sp.GetRequiredService<CommunityHub.Core.Data.CommunityHubDbContext>(),
    sp.GetRequiredService<CommunityHub.Core.Integrations.ZohoClient>(),
    sp.GetRequiredService<CommunityHub.Core.Integrations.ZohoOptions>(),
    tokenOverride: null,
    queueFactory: () => sp.GetRequiredService<CommunityHub.Core.Integrations.Sessions.SyncDeltaQueueService>(),
    // Operator 2026-07-23: every successful Zoho write notifies info@expertslive.dk.
    zohoChanges: sp.GetService<CommunityHub.Core.Email.ZohoChangeNotifier>(),
    // Stage-2 go-live: only APPROVED speakers inside the backstage-speaker-sync
    // released ring (Ring1 today) are pushed; everyone else is held.
    gate: sp.GetRequiredService<CommunityHub.Core.Settings.FeatureGateService>()));
builder.Services.AddScoped<CommunityHub.Core.Organizer.OnboardingService>();
builder.Services.AddScoped<CommunityHub.Core.Organizer.CommandCenterService>();
builder.Services.AddScoped<CommunityHub.Core.Organizer.CommsCockpitService>();
builder.Services.AddScoped<CommunityHub.Core.Organizer.OrganizerExportsService>();
builder.Services.AddScoped<CommunityHub.Core.Organizer.SecretaryTokenService>();
builder.Services.AddScoped<CommunityHub.Core.Organizer.ImpersonationAuditService>();
// §303b: ModifyOnBehalfService DELETED — "Switch to user" is the ONE act-as feature.
// §304: pending-speaker approval (category + ring; Save activates → flows to Zoho).
builder.Services.AddScoped<CommunityHub.Core.Organizer.SpeakerApprovalService>();
builder.Services.AddScoped<CommunityHub.Core.Domain.VolunteerStructureService>();
builder.Services.AddScoped<CommunityHub.Core.Volunteers.VolunteerScheduleBuilder>();
builder.Services.AddScoped<CommunityHub.Core.Volunteers.VolunteerShiftService>();
builder.Services.AddScoped<CommunityHub.Core.Email.VolunteerHelpNotificationService>();
builder.Services.AddScoped<CommunityHub.Notify.HotelCalendarInviter>();
builder.Services.AddScoped<CommunityHub.Core.Organizer.HotelManagementService>();
builder.Services.AddScoped<CommunityHub.Core.Organizer.HotelBulkOperationService>();
builder.Services.AddScoped<CommunityHub.Core.Organizer.HotelRoomBlockService>();
// §326bs: per-night allotment vs demand + the contract release deadlines.
builder.Services.AddScoped<CommunityHub.Core.Organizer.HotelAllotmentService>();

// --- Volunteer Buckets: plan import, gap detection, draft->commit allocation ---
builder.Services.AddScoped<CommunityHub.Core.Volunteers.VolunteerAllocationService>();
builder.Services.AddScoped<CommunityHub.Core.Volunteers.VolunteerPlanImportService>();
builder.Services.AddSingleton<CommunityHub.Core.Volunteers.VolunteerPlanParser>();

// --- §150/§151 task allocation pipeline: organizer-side queue (mirrors the volunteer
// one over the organizer target role), the availability auto-assign engine + team
// router (step 2), the batched commit notifier (commit-only mail), and the Excel
// task round-trip. This is the single composition root for all of the new services.
// (ResponsibleTeamRouter is a static helper — no DI registration needed.)
builder.Services.AddScoped<CommunityHub.Core.Volunteers.OrganizerAllocationService>();
// Generic stage→simulate→commit allocation scenarios (§129) — bulk reshuffles staged + proven
// safe before they touch the live assignment tables; auto-seeds a backfill on a drop-out.
builder.Services.AddScoped<CommunityHub.Core.Volunteers.AllocationScenarioService>();
builder.Services.AddScoped<CommunityHub.Core.Volunteers.AvailabilityAutoAssignEngine>();
builder.Services.AddScoped<CommunityHub.Core.Email.CommitNotificationService>();
builder.Services.AddScoped<CommunityHub.Core.Email.ICommitNotificationService>(
    sp => sp.GetRequiredService<CommunityHub.Core.Email.CommitNotificationService>());
builder.Services.AddScoped<CommunityHub.Core.Volunteers.VolunteerTaskExcelService>();

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

// --- AI Community Helper (code-named AiHelper; REQUIREMENTS §129) -----------
// Display name is operator-configurable via OpenAI:AssistantName (default "Community Helper").
// Azure OpenAI is wired via the "OpenAI" config section (ApiKey is a KV ref). The
// assistant is GATED on OpenAiOptions.IsConfigured (Enabled + endpoint + deployment +
// key) and no-ops gracefully when off. The grounding builder enforces authorization-at-
// retrieval: role-scoped content (ContentPageRegistry.ForRole) + the participant's
// OWN rows only — the assistant gets no raw DB access, just assembled context.
var openAiOptions = new CommunityHub.Core.Assistant.OpenAiOptions();
builder.Configuration
    .GetSection(CommunityHub.Core.Assistant.OpenAiOptions.SectionName)
    .Bind(openAiOptions);
builder.Services.AddSingleton(openAiOptions);
builder.Services.Configure<CommunityHub.Core.Assistant.OpenAiOptions>(
    builder.Configuration.GetSection(CommunityHub.Core.Assistant.OpenAiOptions.SectionName));
// Grounding sources (web): role-scoped content from disk + the participant's own rows.
builder.Services.AddScoped<CommunityHub.Core.Assistant.IAiHelperContentProvider,
    CommunityHub.Assistant.WebAiHelperContentProvider>();
builder.Services.AddScoped<CommunityHub.Core.Assistant.IAiHelperOwnDataProvider,
    CommunityHub.Assistant.WebAiHelperOwnDataProvider>();
// §133 ORGANIZER OPS MODE: curated, read-only ops aggregates (speaker readiness / missing
// slides, sponsor missing deliverables, master-class non-selections, participation/task/
// attendee counts) reused from the existing §11/§134/§135/§6 aggregators. The grounding
// builder injects these ONLY for a SERVER-resolved Organizer (the gate lives in the builder,
// never the prompt) — a non-organizer never even calls this provider.
builder.Services.AddScoped<CommunityHub.Core.Assistant.IAiHelperOrganizerOpsProvider,
    CommunityHub.Assistant.WebAiHelperOrganizerOpsProvider>();
// §149 PUBLIC INFO for EVERY role: published speakers + their skills + sessions, the session
// programme, and the event schedule / key times (doors, lunch, party, dinner). Added for ALL
// roles by the grounding builder (no role gate) — the provider itself enforces the speaker
// publish HARD GATE, so no unselected speaker leaks. Anyone can ask "who speaks about X?" /
// "when is lunch?".
builder.Services.AddScoped<CommunityHub.Core.Assistant.IAiHelperPublicInfoProvider,
    CommunityHub.Assistant.WebAiHelperPublicInfoProvider>();
// §152 SHAREPOINT GROUNDING for EVERY role: operator-dropped md/txt/docx/pdf/xlsx files in
// the configured SharePoint grounding folder (Graphics:SharePoint:GroundingFolderPath),
// extracted to text and grounded for ALL roles (no role gate) via the existing
// ISharePointFileStore read seam. MUST be registered (not just defaulted) because the
// grounding builder is a concrete type whose ctor the DI container fills from registrations —
// the C# default param is only for tests. Depends on ISharePointFileStore (already registered
// gated/null), GraphicsSharePointOptions (already Configure'd) + IMemoryCache (already in the
// container); inert via CanRead when the null store is active or the folder path is blank.
builder.Services.AddScoped<CommunityHub.Core.Assistant.IAiHelperSharePointGroundingProvider,
    CommunityHub.Core.Assistant.SharePointGroundingProvider>();
builder.Services.AddScoped<CommunityHub.Core.Assistant.IAiHelperGroundingBuilder,
    CommunityHub.Core.Assistant.AiHelperGroundingBuilder>();
// Per-participant in-memory rate limit on the endpoint.
builder.Services.AddSingleton<CommunityHub.Assistant.AiHelperRateLimiter>();
// The assistant: a typed HttpClient (absolute Azure OpenAI URL, no BaseAddress).
builder.Services.AddHttpClient<CommunityHub.Core.Assistant.AiHelperAssistant>();
builder.Services.AddScoped<CommunityHub.Core.Assistant.IAiHelperAssistant>(sp =>
    sp.GetRequiredService<CommunityHub.Core.Assistant.AiHelperAssistant>());

// §137 AiHelper INTAKE: detect bug/feature reports in a user's message + forward explicit
// "contact the organizers" messages — capture a FeedbackItem ("CEH feed") AND email the
// right mailbox (bug/feature → dev mailbox, question → organizers). Keyword set + both
// addresses are config-overridable via the "Feedback" section (defaults: ELDK27 wiring).
var feedbackOptions = new CommunityHub.Core.Assistant.FeedbackIntakeOptions();
builder.Configuration
    .GetSection(CommunityHub.Core.Assistant.FeedbackIntakeOptions.SectionName)
    .Bind(feedbackOptions);
builder.Services.AddSingleton(feedbackOptions);
builder.Services.AddSingleton(
    new CommunityHub.Core.Assistant.FeedbackIntakeDetector(feedbackOptions));
builder.Services.AddScoped<CommunityHub.Core.Assistant.FeedbackIntakeService>();

// --- Calendar sync (per-user subscribable iCal feed) -----------------------
// §193: the per-participant calendar FEED + single-item .ics builders were removed.
// Calendar entries are now e-mailed as invitations via CalendarInviteEmailService.
builder.Services.AddScoped<CommunityHub.Core.Participants.ParticipantChecklistBuilder>();
// §134 Speaker Readiness: read-only aggregator that rolls up a speaker's "am I ready?"
// signals (details/headshot/hotel/dinner/uploads/master-class/tasks) from existing data.
builder.Services.AddScoped<CommunityHub.Core.Participants.SpeakerReadinessService>();
// §135 Sponsor Deliverables Tracker: read-only aggregator that rolls up a sponsor company's
// lifecycle stages (onboarding/logo/booth-materials/booth-members/tasks) with done + overdue
// vs deadline, from existing SponsorInfo / uploads / booth members / ParticipantTask data.
builder.Services.AddScoped<CommunityHub.Core.Sponsors.SponsorDeliverablesService>();
// §603 — the ONE sponsor artefact uploader (validate → versioned name → stream → record → notify),
// so a TASK can receive its file in place instead of sending the sponsor to Company Details, and
// without adding a fifth copy of the upload rules (§494b — two copies is what produced §482/§494d).
builder.Services.AddScoped<CommunityHub.Uploads.SponsorArtefactUploader>();
// §598 — the artefact existence verifier (also run daily by the Jobs host).
builder.Services.AddScoped<CommunityHub.Uploads.SponsorArtefactVerifier>();
// Reconciles per-form data signals (Hotel/Dinner/Lunch/Swag/Volunteer-day/Travel)
// onto their OPEN form-owned + speaker-deadline tasks so a saved submission marks
// the matching task(s) Done even when it was saved before the auto-task wiring.
builder.Services.AddScoped<CommunityHub.Core.Participants.FormTaskReconciler>();
// (Sponsor Portal retired 2026-06-21 — the dead /Sponsor/Portal page + its service
// were removed; the sponsor self-service home is /Sponsor, the company landing card.)

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
//  * LIVE writer wired 2026-06-25: LiveBackstageSpeakerBioApi (wraps ZohoClient).
//    CanWrite is true only when Zoho is Enabled + portal/event configured, so it stays
//    a no-op otherwise. The Backstage speakers API is CREATE-ONLY (no update endpoint),
//    so it creates a new speaker or — if one already exists — blocks + emails info@
//    (never a duplicate). The whole sync is still INACTIVE by default
//    (Backstage:SpeakerBioSync:Enabled=false) and ring-gated per speaker.
builder.Services.Configure<CommunityHub.Core.Integrations.BackstageSpeakerBioSyncOptions>(
    builder.Configuration.GetSection(
        CommunityHub.Core.Integrations.BackstageSpeakerBioSyncOptions.SectionName));
builder.Services.AddScoped<
    CommunityHub.Core.Integrations.IBackstageSpeakerBioApi,
    CommunityHub.Core.Integrations.LiveBackstageSpeakerBioApi>();
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

// §768 Phase 2 — the DocLibrary registry + the ONE resolver allowed to build a library path.
// Registered ALONGSIDE the legacy graphics options while call sites migrate. 🔒 The legacy options
// are DELETED once nothing reads them — not left behind as a second source of truth, which is
// precisely the defect this replaces.
builder.Services.Configure<CommunityHub.Core.Integrations.DocLibrary.DocLibraryOptions>(
    builder.Configuration.GetSection(
        CommunityHub.Core.Integrations.DocLibrary.DocLibraryOptions.SectionName));
builder.Services.AddSingleton<
    CommunityHub.Core.Integrations.DocLibrary.IDocLibraryPathResolver,
    CommunityHub.Core.Integrations.DocLibrary.DocLibraryPathResolver>();

// §769 — the operator's SAVED path edits. The cache is a singleton because the resolver is, and
// because resolving a path is synchronous on hot paths; the store is scoped (it writes). 🔒 The
// cache must be registered in BOTH hosts: the jobs host resolves the same keys, and an edit that
// reached only the web host would move a folder for the pages and not for the sweeps.
builder.Services.AddSingleton<CommunityHub.Core.Integrations.DocLibrary.DocLibraryOverrideCache>();
builder.Services.AddScoped<CommunityHub.Core.Integrations.DocLibrary.DocLibraryOverrideStore>();
builder.Services.AddScoped<CommunityHub.Core.Integrations.DocLibrary.DocLibraryPathTester>();

// --- §6.3 — the logistics chain, in the WEB host too -------------------------
// 🔒 The jobs host already registers all of this for LogisticsFilesJob. /Organizer/Logistics now
// LISTS the generated files and offers "Generate now", so the web host needs the same chain — and
// it needs the WHOLE chain: a page that injects LogisticsRunService with one producer missing fails
// at ACTIVATION, which is a 500 on a real page that no build and no unit test can catch.
// ⚠️ The recipients object is registered here as well even though this host never mails on a
// schedule: LogisticsArtifactsService reads ApprovedForRealRecipients to tell the organizer that
// mails are held, and a default-constructed one would silently claim they are not.
builder.Services.AddSingleton(sp =>
{
    var o = new CommunityHub.Core.Integrations.DocLibrary.LogisticsRecipients();
    builder.Configuration
        .GetSection(CommunityHub.Core.Integrations.DocLibrary.LogisticsRecipients.SectionName)
        .Bind(o);
    return o;
});
builder.Services.AddScoped<CommunityHub.Core.Integrations.DocLibrary.DocLibraryFilePublisher>();
builder.Services.AddScoped<CommunityHub.Core.Integrations.DocLibrary.SwagLogisticsProducer>();
builder.Services.AddScoped<CommunityHub.Core.Integrations.DocLibrary.FoodLogisticsProducer>();
builder.Services.AddScoped<CommunityHub.Core.Integrations.DocLibrary.LunchLogisticsProducer>();
builder.Services.AddScoped<CommunityHub.Core.Integrations.DocLibrary.ExpoLogisticsProducer>();
builder.Services.AddScoped<CommunityHub.Core.Integrations.DocLibrary.HotelLogisticsProducer>();
builder.Services.AddScoped<CommunityHub.Core.Integrations.DocLibrary.LogisticsRunService>();
builder.Services.AddScoped<CommunityHub.Core.Integrations.DocLibrary.LogisticsArtifactsService>();

// SharePoint upload client (SPN creds, deployment-scoped, Key Vault) — registered
// UNCONDITIONALLY so any web page (volunteer photo upload, etc.) can inject it. It
// no-ops when the "SharePoint" section is not configured (IsConfigured=false), so
// this is safe even without SPN creds present. (Previously this lived inside the
// graphics-store `if`, so injecting it elsewhere 500'd page activation in prod.)
var spUploadOptions = new CommunityHub.Core.Integrations.SharePointUploadOptions();
builder.Configuration
    .GetSection(CommunityHub.Core.Integrations.SharePointUploadOptions.SectionName)
    .Bind(spUploadOptions);
builder.Services.AddSingleton(spUploadOptions);
// §102: raise the default 100s HttpClient timeout — SharePoint folder
// provisioning (Graph folder walk + createLink) can exceed it on a slow site.
builder.Services.AddHttpClient<CommunityHub.Core.Integrations.SharePointUploadClient>(c =>
    c.Timeout = TimeSpan.FromMinutes(5))
    .AddCredentialFailureAlert("SharePoint");

var graphicsSpOptions = new CommunityHub.Core.Integrations.Graphics.GraphicsSharePointOptions();
builder.Configuration
    .GetSection(CommunityHub.Core.Integrations.Graphics.GraphicsSharePointOptions.SectionName)
    .Bind(graphicsSpOptions);
if (graphicsSpOptions.IsConfigured)
{
    // Live Graph store: uses the SharePoint upload client registered above.
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
// §436: releasing a graphic on /Organizer/Graphics mails the speaker the Help Promote
// link straight away (the daily sweep in the Functions host stays as the safety net).
// Both need the reminder ledger, which is why the engine is registered in the web host too.
builder.Services.AddScoped<CommunityHub.Core.Reminders.ReminderEngine>();
builder.Services.AddScoped<CommunityHub.Core.Integrations.Graphics.SpeakerGraphicsReadyNotifier>();
// §124: per-room session-evaluation QR codes — reads/uploads via the same
// SharePoint file-store seam; inert until the QR folder path is configured.
builder.Services.AddScoped<CommunityHub.Core.Integrations.Graphics.SessionEvalsQrService>();
// §146: reusable, server-proxied, LIVE venue images — reads an allowlisted Venue
// SUBFOLDER via the same SharePoint file-store seam (app creds), caches bytes ~15 min,
// and never exposes a SharePoint link. Inert until Graphics:SharePoint:VenueRootFolderPath
// is set (then the committed wwwroot images are the fallback via VenueImageProvider).
builder.Services.AddScoped<CommunityHub.Core.Integrations.Graphics.VenueImageService>();
builder.Services.AddScoped<CommunityHub.Venue.VenueImageProvider>();
// §153: server-proxied DIRECT download of the speaker presentation template (.potx) from the
// configured SharePoint folder — speakers get the file, never a SharePoint-site link. Inert until
// Graphics:SharePoint:SpeakerTemplateFolderPath is set (the page then falls back to the URL).
builder.Services.AddScoped<CommunityHub.Core.Integrations.Graphics.SpeakerTemplateService>();
// §665: server-proxied speaker photo (see the /speaker-photo route) — the sponsor-uploaded photo
// is fetched with the app's creds instead of handing the browser a SharePoint URL it cannot fetch.
builder.Services.AddScoped<CommunityHub.Core.Integrations.Graphics.SpeakerPhotoService>();
// §326f-c: server-proxied logo-pack zip download (app-registration creds — "everything
// runs on the app reg"); inert until Graphics:SharePoint:LogoPackFolderPath is set.
builder.Services.AddScoped<CommunityHub.Core.Integrations.Graphics.LogoPackService>();
// §166: final per-session evaluation PDFs — organizer upload + speaker download via the same
// SharePoint file-store seam (app creds); inert until Graphics:SharePoint:SessionEvalPdfFolderPath
// is set. Speakers get the file through the /session-eval/{id}/download proxy, never a SharePoint URL.
builder.Services.AddScoped<CommunityHub.Core.Integrations.Graphics.SessionEvalPdfService>();
// §322: speaker preview/final presentation uploads IN THE HUB — the app registration writes
// (and overwrites) the deck on SharePoint; speakers never get a SharePoint link/login. Inert
// until Graphics:SharePoint:PresentationPreviewFolderPath / PresentationFinalFolderPath are set.
builder.Services.AddScoped<CommunityHub.Core.Integrations.Graphics.SpeakerPresentationService>();
// ⚰️ §768 — the §165 external-designer pipeline is RETIRED. It prepared per-session build folders
// and an Excel brief for a human designer to work from; §767's graphics service now produces that
// artwork automatically. Operator 2026-08-02: "some graphics service builds now. it replaces human
// build process". Service, page, naming helper and brief workbook all deleted.
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
// LinkedIn live publisher (§19/§31) — wired but INERT by default: registered only
// when LinkedIn is enabled AND credentialed; and even then LinkedIn:DryRun (default
// true) holds every post (logs intent, posts nothing). Unconfigured ⇒ Null no-op.
var liOptions = new CommunityHub.Core.Integrations.LinkedInOptions();
builder.Configuration.GetSection(CommunityHub.Core.Integrations.LinkedInOptions.SectionName).Bind(liOptions);
builder.Services.AddSingleton(liOptions);
// §324: the org posting token can now be MINTED in-hub (/Organizer/LinkedInConnect)
// and stored in the DB (LinkedInTokenStore) — so the LIVE publisher registers
// whenever LinkedIn is Enabled (token resolved at publish time: DB → static →
// refresh triplet). DryRun (default true) still holds every post.
builder.Services.AddHttpClient<CommunityHub.Core.Integrations.LinkedInTokenStore>();
if (liOptions.Enabled)
{
    builder.Services.AddHttpClient<
        CommunityHub.Core.Integrations.ILinkedInPostPublisher,
        CommunityHub.Core.Integrations.LiveLinkedInPostPublisher>()
        .AddCredentialFailureAlert("LinkedIn");
}
else
{
    builder.Services.AddSingleton<
        CommunityHub.Core.Integrations.ILinkedInPostPublisher,
        CommunityHub.Core.Integrations.NullLinkedInPostPublisher>();
}
builder.Services.AddScoped<CommunityHub.Core.Integrations.SoMeSettingsService>();
builder.Services.AddScoped<CommunityHub.Core.Integrations.SoMeQueueService>();
// §824.2C/§824.16 — the post-template editor and the variable resolver behind its preview.
// 🔒 WEB-ONLY is deliberate for now: the editor is a page, and nothing in the Functions host
// composes posts yet. The moment the scheduler (§824.2E) does, BOTH hosts need these —
// [[ceh-di-two-hosts]]: a service registered in one host and not the other deploys green and
// then fails on the first tick.
builder.Services.AddScoped<CommunityHub.Core.Integrations.SoMeTemplateService>();
builder.Services.AddScoped<CommunityHub.Core.Integrations.SoMeVariableResolver>();
// §864 — resolves a post's {tokens} at PUBLISH time and drives the coloured editor preview.
// 🔒 Registered in BOTH hosts (see JobsServiceRegistration) — a service in one host only deploys
// green and then fails on the first tick ([[ceh-di-two-hosts]]).
builder.Services.AddScoped<CommunityHub.Core.Integrations.SoMePostComposer>();
// §828 — the event-post deck importer (his markdown drop-box → the Type 5 post repo).
// 🔒 WEB-ONLY, and for a stronger reason than the two above: the import is an OPERATOR ACTION
// (he clicks it after landing a new deck), never a timer. §828.1's overwrite tick is a per-post
// decision he makes in the UI, so an unattended job importing on a schedule would be the very
// thing that rule forbids. If a job is ever added, the Functions host must register this too —
// [[ceh-di-two-hosts]].
builder.Services.AddScoped<CommunityHub.Core.Integrations.EventSoMePostImportService>();
// §824.2D — the AI intro writer, on the same Azure OpenAI options the AiHelper binds above.
builder.Services.AddHttpClient<CommunityHub.Core.Integrations.SoMeIntroGenerator>();
builder.Services.AddScoped<CommunityHub.Core.Integrations.SoMeDispatchService>();
// §833 — the scheduler was JOBS-ONLY, which is why it had no page and he could not find it. The
// planner page reads its readiness and can run it on demand, so it is registered here too.
// 🔒 Safe to run from a page: every post it creates is IsActive = false (§824.21a), so an on-demand
// run can never publish anything — it only fills the queue he then approves.
builder.Services.AddScoped<CommunityHub.Core.Integrations.SoMeScheduleService>();
// §834 — the setup wizard, and §835–§838's shared "which posts mention this subject" query.
// Both are read-only projections over data the engine already owns; neither stores anything.
builder.Services.AddScoped<CommunityHub.Core.Integrations.SoMeWizardService>();
builder.Services.AddScoped<CommunityHub.Core.Integrations.SoMeAnnouncementQuery>();
// §841 — the picture library behind the post editor's preview + picker.
builder.Services.AddScoped<CommunityHub.Core.Integrations.SoMeGraphicLibrary>();
// §842.2 — per-type announcement frequency (and the §842.5 sponsor guard).
builder.Services.AddScoped<CommunityHub.Core.Integrations.SoMeCadenceService>();
// §850 — the approval blocker. Registered in BOTH hosts: the queue/editor approve through it, and
// the DISPATCHER re-checks eligibility at send time. [[ceh-di-two-hosts]].
builder.Services.AddScoped<CommunityHub.Core.Integrations.SoMeApprovalGate>();
// Speaker self-service LinkedIn publish (REQUIREMENTS §52): a speaker pushes their
// RELEASED announcement graphic + generated text to the event's LinkedIn page
// through the SAME gated queue/publisher path above — so the credential gate and
// idempotency are identical and there is one publish code path. Gated by the
// linkedin-queue feature flag + the SoMe settings; when LinkedIn creds aren't wired
// the announcement is QUEUED (nothing posted live, nothing faked).
builder.Services.AddScoped<CommunityHub.Core.Integrations.SpeakerLinkedInPublishService>();
// §326p (operator 2026-07-25): the §324c "way 2" one-click own-profile post was
// REMOVED — it never got past LinkedIn's redirect-URI gate live; the share-intent +
// clipboard flow on /Speaker/Graphics is the speaker path. The company-page publish
// above is untouched.

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
// §543 — "Run now" on the Jobs page: the web app starts a real timer function through the
// Functions admin endpoint. Blank config ⇒ the button is hidden and says why, so a missing key
// degrades to today's behaviour rather than to a broken button.
var jobTriggerOptions = new CommunityHub.Core.Settings.JobTriggerOptions();
builder.Configuration.GetSection(CommunityHub.Core.Settings.JobTriggerOptions.SectionName)
    .Bind(jobTriggerOptions);
builder.Services.AddSingleton(jobTriggerOptions);
builder.Services.AddHttpClient<CommunityHub.Core.Settings.JobTriggerService>();
// §525 — SINGLETON, deliberately: ZohoClient is created per-resolution by AddHttpClient, so the
// shared access token must live here. Without it every one of the 27 call sites minted its own
// token and tripped Zoho's refresh-grant rate limit, taking the whole Zoho integration down.
builder.Services.AddSingleton<CommunityHub.Core.Integrations.ZohoAccessTokenCache>();
builder.Services.AddHttpClient<CommunityHub.Core.Integrations.ZohoClient>();
// Anonymous attendee telemetry (public "who's coming" page) — aggregate Zoho stats, cached.
builder.Services.AddScoped<CommunityHub.Core.Integrations.AttendeeTelemetryService>();
// Pushes a sponsor's company overview/short-description into the Backstage
// exhibitor profile when they save it in the hub (fail-soft, gated by Zoho config).
builder.Services.AddScoped<CommunityHub.Core.Integrations.BackstageExhibitorProfileSync>();
// Company Details "Save & Sync to Zoho": pushes the sponsor + exhibitor fields to
// Backstage (resolves+caches the Zoho ids by company name, then targets by id).
builder.Services.AddScoped<CommunityHub.Core.Integrations.SponsorZohoSyncService>();
// §482b — also registered in the WEB host so a sponsor's contact edit can pull the change back
// immediately (ERP → Company Manager → hub). Without this the edit was correct but invisible until
// the scheduled sync ran, which reads as "my change did nothing".
builder.Services.AddScoped<CommunityHub.Core.Integrations.SponsorContactSyncService>();
// Stage 4b: create/link Zoho sponsor + exhibitor records from webshop data. The
// exhibitor-create seam (exhibitor_requests) — Live; CanCreate guards it when the
// booth-category id isn't configured (then only sponsor create happens).
// --- §340-H EXTERNAL WRITES (env default + per-edition organizer override) ---
// Registered in BOTH hosts: the web app also reaches Zoho (SponsorZohoProvisionService,
// the organizer push buttons), so a guard only in the Jobs host would leave the GUI able
// to write from an environment that is supposed to be write-blocked. SCOPED — the guard
// reads the organizer's Settings override from the DB and caches it per request.
var externalWriteWebOptions = new CommunityHub.Core.Integrations.ExternalWriteOptions();
builder.Configuration.GetSection(CommunityHub.Core.Integrations.ExternalWriteOptions.SectionName)
    .Bind(externalWriteWebOptions);
builder.Services.AddSingleton(externalWriteWebOptions);
builder.Services.AddScoped<CommunityHub.Core.Integrations.IExternalWriteGuard,
    CommunityHub.Core.Integrations.ExternalWriteGuard>();

var backstageExhibitorWebOptions = new CommunityHub.Core.Integrations.BackstageExhibitorOptions();
builder.Configuration.GetSection(CommunityHub.Core.Integrations.BackstageExhibitorOptions.SectionName)
    .Bind(backstageExhibitorWebOptions);
builder.Services.AddSingleton(backstageExhibitorWebOptions);
builder.Services.AddHttpClient<CommunityHub.Core.Integrations.IBackstageExhibitorApi,
    CommunityHub.Core.Integrations.LiveBackstageExhibitorApi>();
builder.Services.AddScoped<CommunityHub.Core.Integrations.SponsorZohoProvisionService>();
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

// PartyTaskSeeder (§164): ensures the per-participant "party sign-up" task exists for
// the staff roles (Sponsor/Speaker/Volunteer/EventPartner/Organizer) so it surfaces in
// My Tasks + Get Started and the reminder job nags until answered. Idempotent
// (SourceKey-keyed), so it is safe to call from /Index on every hub page-load.
builder.Services.AddScoped<CommunityHub.Core.Config.PartyTaskSeeder>();

// --- Attendee "fun IT games" quizzes (REQUIREMENTS §171) -------------------
// The server-authoritative play engine (draw/score/timing), the per-topic
// leaderboard reader, organizer authoring, and the starter-pool seeder. The
// seeder is idempotent (slug-keyed), so it is safe to call on every Games/
// authoring page-load to guarantee a fresh DB has playable quizzes.
builder.Services.AddScoped<CommunityHub.Core.Quizzes.QuizPlayService>();
builder.Services.AddScoped<CommunityHub.Core.Quizzes.QuizLeaderboardService>();
builder.Services.AddScoped<CommunityHub.Core.Quizzes.QuizAuthoringService>();
builder.Services.AddScoped<CommunityHub.Core.Config.QuizSeeder>();

// SignalGroupsProvider (§109): resolves the role-appropriate Signal chat + broadcast
// invite links from config/signal-groups.<edition>.json for the "Join Signal groups"
// Get-Started step + task. Operational invite links (not secrets); a missing config
// just hides the step. Singleton — the file is read once + cached.
var signalGroupsOptions = new CommunityHub.Core.Config.SignalGroupsOptions();
builder.Configuration
    .GetSection(CommunityHub.Core.Config.SignalGroupsOptions.SectionName)
    .Bind(signalGroupsOptions);
builder.Services.AddSingleton(signalGroupsOptions);
builder.Services.AddSingleton<CommunityHub.Core.Config.SignalGroupsProvider>();

// SpeakerMilestoneService: the read-model behind the /Speaker hub progress
// tracker. Reads the speaker's seeded deadline tasks (speakerdl: SourceKey)
// and derives countdown + status; also flips a speaker's own milestone
// done/open. Scoped (per-request DbContext).
builder.Services.AddScoped<CommunityHub.Core.Reminders.SpeakerMilestoneService>();

// SpeakerSessionsService: the read-model behind the Speaker hub "My sessions"
// card (room/time, master-class, attendee-questions links). Own-row scoped --
// only the signed-in speaker's own linked sessions. Scoped (per-request DbContext).
builder.Services.AddScoped<CommunityHub.Core.Reminders.SpeakerSessionsService>();

// 🗑 §748.1 — SpeakerEvaluationsService is GONE with the 1–5 model it read. The speaker's view of
// the four-point results is C10, not yet built; until it exists /Speaker/Evaluations redirects
// (§748.5) rather than showing an empty page that would read as "nobody rated me".

// AttendeePlanService: the read+write model behind the attendee self-service
// "My plan" page and the "Save to my plan" toggle on the public sessions list.
// Own-row scoped -- a participant only ever sees/changes their OWN saved
// sessions; a personal bookmark list (never books a seat). Scoped (per-request
// DbContext); uses the ambient TimeProvider for the saved-at stamp.
builder.Services.AddScoped<CommunityHub.Core.Attendees.AttendeePlanService>();

// --- Company Manager (sponsor-side company + contacts source of truth) ----
// Used by /Sponsor/Index to render a read-only "Sponsor details" card
// pulled from the webshop (company name, website, LinkedIn, X) with a
// button back to the configurator for updates.
var cmOptions = new CommunityHub.Core.Integrations.CompanyManagerOptions();
builder.Configuration.GetSection(CommunityHub.Core.Integrations.CompanyManagerOptions.SectionName).Bind(cmOptions);
builder.Services.AddSingleton(cmOptions);
// Bounded, jittered transient-fault retry (5xx/408/429/timeout) so a momentary
// upstream blip from Company Manager doesn't surface as a hard failure (2026-06-27 incident).
builder.Services.AddHttpClient<CommunityHub.Core.Integrations.CompanyManagerClient>()
    .AddHttpMessageHandler(() => new CommunityHub.Core.Integrations.TransientFaultRetryHandler())
    .AddCredentialFailureAlert("Company Manager");

// --- Content Studio: WordPress + LinkedIn content connector & template engine (§31) --
// DRAFT-ONLY: the WordPress connector always posts status=draft (operator validates
// in wp-admin); LinkedIn output is the short text held for validation. The connector
// self-gates on CanWrite (URL + creds) so an unconfigured deploy is inert; the
// 'content-studio' feature (default OFF) gates draft creation.
var wpOptions = new CommunityHub.Core.Integrations.WordPressOptions();
builder.Configuration.GetSection(CommunityHub.Core.Integrations.WordPressOptions.SectionName).Bind(wpOptions);
builder.Services.AddSingleton(wpOptions);
builder.Services.AddHttpClient<CommunityHub.Core.Integrations.IWordPressPublisher,
    CommunityHub.Core.Integrations.LiveWordPressPublisher>();
var contentStudioOptions = new CommunityHub.Core.Integrations.ContentStudioOptions();
builder.Configuration.GetSection(CommunityHub.Core.Integrations.ContentStudioOptions.SectionName).Bind(contentStudioOptions);
builder.Services.AddSingleton(contentStudioOptions);
builder.Services.AddSingleton<CommunityHub.Core.Integrations.ContentTemplateEngine>();
builder.Services.AddScoped<CommunityHub.Core.Integrations.ContentStudioService>();

// --- Speaker onboarding wizard (§28, design A): guided shell over the existing
// speaker forms (Speaker Details → Hotel → Dinner → Lunch → Swag → Travel) with
// entitlement + progress; reuses the forms untouched.
builder.Services.AddScoped<CommunityHub.Forms.SpeakerWizardService>();
// Sponsor "Get started" wizard (§32): guided shell over the Company Details sections.
builder.Services.AddScoped<CommunityHub.Forms.SponsorWizardService>();
// Generic "Get started" wizard (§43): guided shell for the remaining roles
// (Volunteer / Organizer / Media / EventPartner); same design-A shell + entitlement
// gating as the speaker wizard, reusing the existing pages untouched.
builder.Services.AddScoped<CommunityHub.Forms.RoleWizardService>();
// §207/§208: the ATTENDEE Get-Started stepper (Master Class + Party for 2-day; Party for 1-day).
builder.Services.AddScoped<CommunityHub.Forms.AttendeeWizardService>();
// §173e: ensures EVERY Get-Started step a per-participant role has is mirrored by a
// matching task (idempotent), so My-Tasks lists exactly the role's steps + deadline tasks.
// Driven off the wizard services above; its done-state is synced by FormTaskReconciler.
builder.Services.AddScoped<CommunityHub.Forms.WizardStepTaskSeeder>();
// §720 - the "someone completed Get Started" notice to the operator (Settings on/off).
builder.Services.AddScoped<CommunityHub.Core.Reminders.GetStartedCompletionNotifier>();
// §253 G11: on an organizer ROLE change, prunes the old role's auto-seeded tasks
// (wizard mirrors + dated speakerdl: deadlines) and seeds/reconciles the new role's.
builder.Services.AddScoped<CommunityHub.Forms.RoleChangeTaskReconciler>();

// In-wizard STEPPER (§148): auto-discover every step handler + shared form-service in the
// web assembly so a new onboarding step = one HandlerClass + one XxxFormService with ZERO
// edits here. Each IWizardStepHandler is registered against the interface (the host injects
// IEnumerable<IWizardStepHandler> and keys them by Handler.Key); each IWizardFormService
// marker type self-registers by its concrete type (injected by the handler + the standalone page).
foreach (var t in typeof(Program).Assembly.DefinedTypes.Where(t => t is { IsAbstract: false, IsInterface: false }))
{
    if (typeof(CommunityHub.Forms.IWizardStepHandler).IsAssignableFrom(t))
        builder.Services.AddScoped(typeof(CommunityHub.Forms.IWizardStepHandler), t);
    if (typeof(CommunityHub.Forms.IWizardFormService).IsAssignableFrom(t))
        builder.Services.AddScoped(t);
}

// --- Read-only e-conomic ROLE source for the sponsor-email audience (§7c) --
// The sponsor-email coordinator audience is resolved READ-ONLY from e-conomic
// contact role data (Role 2 = event coordinator), because Company Manager
// cannot be extended to hold per-user roles. Opt-in (EconomicRoles:Enabled,
// default false) + fail-soft: when disabled/unreachable/empty the resolver
// falls back to the manual IsEventCoordinator flags (seeded from the Company
// Manager single default coordinator). GETs only — never an ERP write.
var economicErpOptionsWeb = new CommunityHub.Core.Integrations.Erp.EconomicErpOptions();
builder.Configuration.GetSection(CommunityHub.Core.Integrations.Erp.EconomicErpOptions.SectionName)
    .Bind(economicErpOptionsWeb);
builder.Services.AddSingleton(economicErpOptionsWeb);
// Organizer e-conomic contact-CRUD GUI (REQUIREMENTS §6): live REST client +
// the orchestrating service (live name/email/phone + hub-side role/notes). The
// client self-gates on CanWrite (tokens/base URL present), so it is safe to
// register unconditionally; the page shows "not configured" until wired.
builder.Services.AddHttpClient<CommunityHub.Core.Integrations.Erp.IEconomicContactAdminClient,
    CommunityHub.Core.Integrations.Erp.LiveEconomicContactAdminClient>()
    .AddCredentialFailureAlert("e-conomic");
builder.Services.AddScoped<CommunityHub.Core.Integrations.Erp.EconomicContactAdminService>();
// ERP→webshop reconcile (create missing webshop users + set defaults from ERP
// contact roles + alert on missing roles).
builder.Services.AddScoped<CommunityHub.Core.Integrations.Erp.ErpWebshopContactSyncService>();

// 🔴 §786.6 — THE FX PROVIDER, AND WHY IT IS REGISTERED HERE.
//
// WebshopDraftInvoiceService needs IFxRateProvider. The JOBS host has always registered it; the web
// host never had to, because nothing here had ever needed it. Adding the service below without this
// broke the PROD deploy at the staging warm-up: `ValidateOnBuild` (§783.10) resolves every
// registration when the container is built, so the app did not start, the slot never answered 200,
// and deploy-app.ps1 correctly REFUSED TO SWAP. Prod was never touched.
//
// 🔒 That is the §687.9 / §783.10 / §784.15 defect one more time — a service registered in one host
// and not the other — and it is the third distinct host-pair it has bitten. The guard did its job
// here; what it cost was a deploy cycle, because the web host's check only runs when the app starts.
var fxRateOptionsWeb = new CommunityHub.Core.Integrations.Erp.FxRateOptions();
builder.Configuration.GetSection(CommunityHub.Core.Integrations.Erp.FxRateOptions.SectionName)
    .Bind(fxRateOptionsWeb);
builder.Services.AddSingleton(fxRateOptionsWeb);
builder.Services.AddHttpClient<CommunityHub.Core.Integrations.Erp.IFxRateProvider,
    CommunityHub.Core.Integrations.Erp.FxRateProvider>();

// §786 — webshop order → e-conomic DRAFT invoice. The JOB that uses this runs in the Functions host,
// but 🔒 CLAUDE.md's rule applies: a Core service registered in one host only is half live, and
// /Organizer/Jobs can trigger this job, which activates the chain HERE. The client self-gates on
// CanWrite, so registering it unconditionally cannot reach e-conomic before it is configured —
// and the FEATURE switch (webshop-erp-invoicing, off) is what decides whether it ever runs.
builder.Services.AddHttpClient<CommunityHub.Core.Integrations.Erp.IEconomicInvoiceClient,
    CommunityHub.Core.Integrations.Erp.LiveEconomicInvoiceClient>()
    .AddCredentialFailureAlert("e-conomic");

// §788 — the shared DRY-RUN switch for BOTH invoice modules. 🔒 An absent section leaves DryRun at
// its safe default of TRUE. Registered in BOTH hosts because WebshopDraftInvoiceService resolves
// here too — the §786.6 lesson applied rather than relearned.
// 🔒 FromConfiguration, NOT .Bind(): Bind THROWS on a blank or unparseable bool, and this host runs
// ValidateOnBuild — so a typo'd Invoicing:DryRun app setting would fail the container, leave the
// staging slot answering nothing and abandon the swap (§786.6, again). The reader keeps dry run ON.
builder.Services.AddSingleton(
    CommunityHub.Core.Integrations.Erp.InvoicingOptions.FromConfiguration(builder.Configuration));
builder.Services.AddScoped<CommunityHub.Core.Integrations.Erp.WebshopDraftInvoiceService>();
// §787 — the coupon half. Registered in BOTH hosts for the same §786.6 reason as the line above:
// the web host resolves it for /Organizer/CouponInvoicing, the jobs host for CouponInvoiceJob, and
// a service present in one and missing in the other deploys green and then fails on the first tick.
builder.Services.AddScoped<CommunityHub.Core.Integrations.Erp.CouponDraftInvoiceService>();
builder.Services.AddScoped<CommunityHub.Core.Integrations.Erp.CouponMappingAlertService>();
// §795.2/§795.3 — same rule, same reason: /Organizer/Jobs can trigger CouponInvoiceJob from HERE,
// which resolves both of these. Registered in one host only, that is a green deploy and a 500 on
// the first trigger.
builder.Services.AddScoped<CommunityHub.Core.Integrations.Erp.CouponDiscoveryService>();
builder.Services.AddScoped<CommunityHub.Core.Integrations.Erp.CouponPrepaidBillingReminderService>();
builder.Services.AddScoped<CommunityHub.Core.Integrations.Erp.CouponPrepaidLowBalanceAlertService>();
builder.Services.AddScoped<CommunityHub.Core.Integrations.Erp.DraftInvoiceCreatedNotifier>();
builder.Services.AddScoped<CommunityHub.Core.Integrations.Erp.InvoiceProblemNotifier>();

var economicRolesOptionsWeb = new CommunityHub.Core.Integrations.Erp.EconomicRolesOptions();
builder.Configuration.GetSection(CommunityHub.Core.Integrations.Erp.EconomicRolesOptions.SectionName)
    .Bind(economicRolesOptionsWeb);
builder.Services.AddSingleton(economicRolesOptionsWeb);
if (economicRolesOptionsWeb.Enabled)
{
    builder.Services.AddHttpClient<CommunityHub.Core.Integrations.Erp.IEconomicRoleClient,
        CommunityHub.Core.Integrations.Erp.EconomicRoleClient>();
    builder.Services.AddScoped<CommunityHub.Core.Email.ISponsorErpCoordinatorSource,
        CommunityHub.Core.Email.SponsorErpCoordinatorSource>();
}
else
{
    builder.Services.AddSingleton<CommunityHub.Core.Integrations.Erp.IEconomicRoleClient,
        CommunityHub.Core.Integrations.Erp.NullEconomicRoleClient>();
    builder.Services.AddSingleton<CommunityHub.Core.Email.ISponsorErpCoordinatorSource,
        CommunityHub.Core.Email.NullSponsorErpCoordinatorSource>();
}

// --- WooCommerce (sponsor orders, displayed on /Sponsor/Index) ------------
// Web-side WooCommerce client renders the "Sponsor orders" section on
// /Sponsor/Index. Same shape as the Jobs / OneShot wiring.
var wooOptions = new CommunityHub.Core.Integrations.WooCommerceOptions();
builder.Configuration.GetSection(CommunityHub.Core.Integrations.WooCommerceOptions.SectionName).Bind(wooOptions);
builder.Services.AddSingleton(wooOptions);
builder.Services.AddHttpClient<CommunityHub.Core.Integrations.WooCommerceClient>()
    // §649 — retry a WooCommerce 429 instead of failing the caller. Same reasoning as the Jobs
    // host: read-only pulls, and the handler already classifies 429 as transient.
    .AddHttpMessageHandler(() => new CommunityHub.Core.Integrations.TransientFaultRetryHandler())
    .AddCredentialFailureAlert("WooCommerce");
// In-memory cache for sponsor-orders rendering: WooCommerce + products
// enrichment is ~2-3 seconds and the same sponsor will refresh the page
// repeatedly. 5-minute TTL keyed on company id.
builder.Services.AddMemoryCache();

// --- REQUIREMENTS §684 — the task-body model (definitions in code, bodies in Markdown) ----
// Registered alongside the legacy path, not in place of it (§684.16 step 1): TaskBodyService
// renders a task from the registry when the row is migrated and returns null when it is not, so an
// unmigrated task keeps its existing rendering byte for byte.
//
// 🔒 SINGLETON registry + body store on purpose: the registry is a static list and the store caches
// parsed bodies, so one parse per process rather than one per page view. The placeholder builder is
// SCOPED — it reads per-company rows through the DbContext.
builder.Services.AddSingleton(CommunityHub.Core.Tasks.Definitions.TaskDefinitionRegistry.Shipped);
builder.Services.AddSingleton<CommunityHub.Core.Tasks.Definitions.TaskBodyStore>();
// §666 — the first :::data provider. Registered against the interface so TaskDataResolver picks it
// up from IEnumerable<ITaskDataProvider>; adding the next provider is one more line here.
builder.Services.AddScoped<CommunityHub.Core.Integrations.SponsorPurchaseSummaryService>();
// 🔴 §783.10 — the INTERFACE mapping, which was missing here and 500'd /Organizer/Logistics live.
// `ExpoLogisticsProducer` takes `ISponsorPurchaseSummary`, not the concrete class, and only the
// CONCRETE one was registered in this host — so the page died at ACTIVATION with "Unable to resolve
// service for type 'ISponsorPurchaseSummary'".
//
// 🔒 It is worth being precise about why this survived review: the jobs host registers both, and its
// comment there states "It is registered in the WEB host already". That sentence was WRONG, and it
// is the kind of wrong that reads as a completed check — the concrete registration on the line above
// makes the file LOOK like it covers this. A grep for the service name finds it; only a grep for the
// INTERFACE finds the hole.
//
// ⚠️ A missing DI registration cannot fail a build and cannot fail any test that does not construct
// the real web container — which is why the deploy check passed. See §783.10.
builder.Services.AddScoped<CommunityHub.Core.Integrations.ISponsorPurchaseSummary>(sp =>
    sp.GetRequiredService<CommunityHub.Core.Integrations.SponsorPurchaseSummaryService>());
builder.Services.AddScoped<CommunityHub.Core.Tasks.Data.ITaskDataProvider,
    CommunityHub.Core.Tasks.Data.TvPurchaseTaskDataProvider>();
// §687.1 — which shipment services this sponsor already bought. Same summary service as the TV
// provider and (once built) the organizer's Logistics panel, so the three cannot disagree.
builder.Services.AddScoped<CommunityHub.Core.Tasks.Data.ITaskDataProvider,
    CommunityHub.Core.Tasks.Data.ShipmentPurchasesTaskDataProvider>();
// §687.3 — which booth furniture this sponsor has actually ORDERED. The list is the visible part;
// the point is that ordering (not ticking) is what gets furniture delivered.
builder.Services.AddScoped<CommunityHub.Core.Tasks.Data.ITaskDataProvider,
    CommunityHub.Core.Tasks.Data.BoothFurniturePurchasesTaskDataProvider>();
// §687.5 — the attendee-bag PACKAGING service. The §676 decision records intent; this records
// whether the service is actually paid for, and they are not the same fact.
builder.Services.AddScoped<CommunityHub.Core.Tasks.Data.ITaskDataProvider,
    CommunityHub.Core.Tasks.Data.AttendeeBagPackagingTaskDataProvider>();
builder.Services.AddScoped<CommunityHub.Core.Tasks.Data.ITaskDataProvider,
    CommunityHub.Core.Tasks.Data.ExtraStaffTicketsTaskDataProvider>();
builder.Services.AddScoped<CommunityHub.Core.Tasks.Data.TaskDataResolver>();
builder.Services.AddScoped<CommunityHub.Core.Tasks.TaskBodyService>();
builder.Services.AddScoped<CommunityHub.Core.Tasks.SponsorTaskPlaceholderBuilder>();
// §708 — the speaker twin. Much smaller (no company/tier/coupon); its real job is publishing the
// §708.2a canonical FORM ROUTES to the bodies, so no body carries a URL of its own.
builder.Services.AddScoped<CommunityHub.Core.Tasks.SpeakerTaskPlaceholderBuilder>();
// §707.57d — "the task said DONE and the file did not exist". Derives the two presentation tasks
// from the decks that actually exist, and NEVER reopens on a lookup it could not perform.
builder.Services.AddScoped<CommunityHub.Core.Tasks.SpeakerPresentationTaskReconciler>();
// §687.8 — "a furniture order should auto-close that task". Derives Done from the real order.
builder.Services.AddScoped<CommunityHub.Core.Tasks.PurchaseTaskReconciler>();

// Event-edition facts + cross-cutting placeholders ({{configuratorUrl}}
// etc.) for rendering the "update this data in the webshop" link.
var eventConfigOptions = new CommunityHub.Core.Config.EventConfigOptions();
builder.Configuration.GetSection(CommunityHub.Core.Config.EventConfigOptions.SectionName).Bind(eventConfigOptions);
builder.Services.AddSingleton(eventConfigOptions);
builder.Services.AddSingleton<CommunityHub.Core.Config.EventEditionConfigLoader>();
// §299.8/b7 + §299.6/b5: per-edition session length/level OPTIONS and the room
// REGISTRY — both pure config from event.<edition>.json (no DB; public pages stay
// fast). Scoped so an edited config file is re-read per request scope.
builder.Services.AddScoped<CommunityHub.Core.Config.SessionOptionsService>();
builder.Services.AddScoped<CommunityHub.Core.Config.RoomRegistryService>();

// Sponsor config file location — needed by the Phase 2 config editor so it can
// read the shipped sponsor defaults to enumerate that section's scalar fields.
var sponsorConfigOptions = new CommunityHub.Core.Config.SponsorConfigOptions();
builder.Configuration.GetSection(CommunityHub.Core.Config.SponsorConfigOptions.SectionName)
    .Bind(sponsorConfigOptions);
builder.Services.AddSingleton(sponsorConfigOptions);
// 🔴 PROD INCIDENT 2026-07-29 (§687.9): the WEB host never registered this, only the Jobs host did.
// It went unnoticed because nothing in the web app had ever needed it — until §687's
// SponsorTaskPlaceholderBuilder took a dependency on it, at which point EVERY authenticated load of
// /Sponsor/Tasks threw "Unable to resolve service for type SponsorConfigLoader" and returned 500.
// 🔒 The post-deploy smoke passed anyway, because every probe was ANONYMOUS and anonymous requests
// bounce to /Login before the page model is constructed. Health checks that never build the page
// cannot see a DI failure in it.
builder.Services.AddSingleton<CommunityHub.Core.Config.SponsorConfigLoader>();

// --- Admin-editable config overrides (HYBRID config model, Phase 1) --------
// The shipped JSON files (event/sponsor/integrations) remain the default that
// travels with each release; SQL ConfigOverride rows carry per-edition partial
// overrides the loaders deep-merge on top at runtime. No row ⇒ shipped default
// unchanged (additive). No UI yet (Phase 2); no ring-gate yet (Phase 4).
var integrationsConfigOptions = new CommunityHub.Core.Config.IntegrationsConfigOptions();
builder.Configuration.GetSection(CommunityHub.Core.Config.IntegrationsConfigOptions.SectionName)
    .Bind(integrationsConfigOptions);
builder.Services.AddSingleton(integrationsConfigOptions);
builder.Services.AddSingleton<CommunityHub.Core.Config.IntegrationsConfigLoader>();
builder.Services.AddScoped<CommunityHub.Core.Config.ConfigOverrideStore>();
// Per-edition editable email templates (REQUIREMENTS §25h): the override store the editor
// writes + the EmailTemplateProvider reads at send/preview time.
builder.Services.AddScoped<CommunityHub.Core.Email.EmailTemplateOverrideStore>();
// §515 — per-template release rings (speaker welcome at a different ring to sponsor). Resolved by
// the transport on every tagged send, so it must be registered in BOTH hosts.
builder.Services.AddScoped<CommunityHub.Core.Email.EmailTemplateRingService>();

// §707.11 — the per-mail repeat interval for recurring reminders. The WEB host needs it for the
// Settings control; the JOBS host needs it because that is where the builders run.
builder.Services.AddScoped<CommunityHub.Core.Email.EmailReminderCadenceService>();

// §743 C4a — the device-facing ingest path (Session Evaluation). Web-only for now: the two
// endpoints the feedback devices call are served by this host, and nothing in the jobs host
// touches them yet.
builder.Services.AddScoped<CommunityHub.Core.Evaluation.EvaluationIngestService>();
// §753 — device self-service onboarding + the health-telemetry directive. Registered once and used
// by BOTH the provisioning endpoint and the heartbeat, so the two cannot return different answers.
builder.Services.AddScoped<CommunityHub.Core.Evaluation.EvaluationProvisioningService>();
// §743 C3 — mirror CEH's sessions + rooms into the evaluation model, and backfill attribution.
builder.Services.AddScoped<CommunityHub.Core.Evaluation.EvaluationSessionSyncService>();
// §743 C7 — derive the satisfaction figures from the raw responses (nothing is stored).
builder.Services.AddScoped<CommunityHub.Core.Evaluation.EvaluationScoreService>();
// §784.7(C) — the merged per-session view (/Organizer/SessionFeedback). It owns the CEH-session ↔
// evaluation-session join that the four merged pages each did differently.
builder.Services.AddScoped<CommunityHub.Core.Evaluation.SessionFeedbackOverviewService>();
// §743 C7 — the per-session PDF, rendered on request (PdfSharpCore, MIT).
builder.Services.AddScoped<CommunityHub.Core.Evaluation.EvaluationReportService>();
// §747 C8 — ONE assembly site for the report, shared by the organiser download and the outbound
// pull, plus the version identifier that tells a consumer a recomputation superseded its copy.
builder.Services.AddScoped<CommunityHub.Core.Evaluation.EvaluationReportBuilder>();
// §747 C8 — the service credentials external systems present to pull reports.
builder.Services.AddScoped<CommunityHub.Core.Evaluation.EvaluationApiClientService>();
// §748 C5/C6 — the QR channel: resolve a printed token, and record the scan as an ordinary
// EvaluationResponse (same table, same weights as a device press).
builder.Services.AddScoped<CommunityHub.Core.Evaluation.EvaluationQrService>();
// §748 C5 — we generate the QR images ourselves (operator: "qr must be gerated by you"); QRCoder,
// MIT, via PngByteQRCode so nothing touches the Windows-only System.Drawing.
builder.Services.AddSingleton<CommunityHub.Core.Evaluation.SessionQrCodeService>();
// §749.1/§749.2 — push what we GENERATE into SharePoint (operator: "dont forget to download pdf
// files intonsharepoint source with correct naming ... solution exist in cost with paths", plus
// "both qr code link download"). Reuses the EXISTING stores and path settings — it adds no second
// SharePoint integration, and every file name comes from the service that already owns it.
builder.Services.AddScoped<CommunityHub.Core.Evaluation.EvaluationArtifactPublishService>();
// §750 C7 — the report-ready notification (a LINK, never the PDF: it carries verbatim attendee
// comments, and an attachment would put uncontrolled copies outside the retention schedule), and
// the 30-minute debounce that decides WHEN a report is rebuilt, published and notified.
builder.Services.AddScoped<CommunityHub.Core.Reminders.EvaluationReportReadyMailService>();
builder.Services.AddScoped<CommunityHub.Core.Evaluation.EvaluationReportDebounceService>();
// §751 — organizer LOG views show event-local time, not UTC. Scoped so the timezone is resolved
// once per request rather than once per rendered row.
builder.Services.AddScoped<CommunityHub.Services.OrganizerLogClock>();

// --- Current-participant accessor (Stage 4) --------------------------------
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<
    CommunityHub.Auth.ICurrentParticipantAccessor,
    CommunityHub.Auth.HttpCurrentParticipantAccessor>();

// Self-warm the authenticated hot path (login + hub EF queries) on every boot so
// the first real sign-in after a restart is not slow (cold query-plan compilation
// on small SKUs cost ~30s). Background + best-effort; never blocks readiness.
builder.Services.AddHostedService<CommunityHub.Startup.StartupWarmupService>();

var app = builder.Build();

// --- §768 DocLibrary configuration check ------------------------------------
// Says at boot which path key is wrong, by name. A misconfigured folder does NOT throw at runtime —
// the store returns an empty listing and every caller reads that as "nothing to do", which is how a
// sweep ran four production cycles reporting success while matching nothing. Logs; does not kill the
// host, because one unset folder must not take down sign-in and the agenda with it.
CommunityHub.Core.Integrations.DocLibrary.DocLibraryStartupCheck.Run(
    app.Services.GetRequiredService<
        CommunityHub.Core.Integrations.DocLibrary.IDocLibraryPathResolver>(),
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DocLibrary"));

// --- §303 per-integration field maps (layer 2) ------------------------------
// Apply the shipped zoho-backstage.fieldmap.json over the ZohoFieldMap code
// defaults. FAIL-SOFT: a missing/invalid file logs a warning and the code rows
// stay in force — a bad config file must never take the host down.
CommunityHub.Core.Integrations.ZohoFieldMap.ApplyMapFile(
    CommunityHub.Core.Integrations.IntegrationFieldMap.LoadMapFile(
        CommunityHub.Core.Integrations.ZohoFieldMap.MapFilePath,
        err => app.Logger.LogWarning("IntegrationFieldMap: {Error}", err)));
// §323: the INBOUND Sessionize map — the importer's category-group routing keywords
// (format/track/level/tag) come from the file; code defaults are the fallback.
CommunityHub.Core.Integrations.SessionizeFieldMap.ApplyMapFile(
    CommunityHub.Core.Integrations.IntegrationFieldMap.LoadMapFile(
        CommunityHub.Core.Integrations.SessionizeFieldMap.MapFilePath,
        err => app.Logger.LogWarning("IntegrationFieldMap: {Error}", err)));

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

// Request localization: negotiate the per-request culture from the culture
// cookie, then Accept-Language, then the en default (see localizationOptions
// above). With en the only supported culture, this always resolves to English.
// CurrentUICulture drives resource lookup (resx) and the dynamic <html lang>;
// CurrentCulture drives date/number formatting. The date *picker* stays
// dd/mm/yyyy (Monday-first) via flatpickr regardless — the value posted to the
// server is ISO either way.
app.UseRequestLocalization(localizationOptions);

// Honour the reverse-proxy's X-Forwarded-* headers BEFORE HTTPS redirection so
// the app sees the original client scheme/IP (the proxy terminates TLS and
// forwards over HTTP). KnownNetworks/KnownProxies are cleared because the proxy
// hop is not on a known private range here; without this the forwarded headers
// would be ignored and HTTPS redirection / scheme-sensitive URLs would break.
var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
};
forwardedHeadersOptions.KnownNetworks.Clear();
forwardedHeadersOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeadersOptions);

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

// Anonymous on purpose: the deploy script probes / and /health to confirm the site
// is up. Under the fail-closed FallbackPolicy this endpoint would otherwise require
// auth and fail every deploy health probe.
app.MapHealthChecks("/health").AllowAnonymous();

// Culture cookie endpoint. Persists a chosen culture in the standard ASP.NET
// Core culture cookie (read back by CookieRequestCultureProvider on the next
// §494 — DIRECT-TO-STORAGE upload endpoints (begin / complete). The file itself goes
// browser → SharePoint and never touches this app; these two small JSON calls only bracket it.
CommunityHub.Uploads.DirectUploadEndpoints.MapDirectUploadEndpoints(app);

// request) and redirects to where the user was. The value is clamped to a
// supported culture, so today it always resolves to en (English-only). POST +
// antiforgery-free (no auth/state change beyond the cookie) so it works on
// anonymous pages (Login) too. `returnUrl` is treated as local-only.
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
// Anonymous: the language switcher is used on the Login page before sign-in, so it
// must opt out of the fail-closed FallbackPolicy (it only sets a culture cookie).
}).AllowAnonymous();

// Back-compat alias: the "Secure link" page was previously "/Organizer/SecretaryLink".
// Redirect old bookmarks/shared links to the new path, preserving the query string
// (e.g. ?participantId=123) so they don't 404.
app.MapGet("/Organizer/SecretaryLink", (HttpContext http) =>
{
    var qs = http.Request.QueryString.HasValue ? http.Request.QueryString.Value : string.Empty;
    return Results.LocalRedirect($"/Organizer/SecureLink{qs}");
});

// §193: the per-schedule-entry and per-session ".ics" download routes were removed.
// Calendar entries now arrive as e-mailed INVITATIONS to signed-in participants (the
// speaker "Email me a calendar invite" action, the task "Add Reminder" action, etc.),
// not as downloadable files.

// §146: SERVER-PROXIED venue image. Streams an allowlisted Venue SUBFOLDER image
// (wayfinding / good-to-know / evaluations / expo) fetched with the app's OWN SharePoint
// credentials, with a committed-wwwroot fallback — end users have no SharePoint access and
// never see a SharePoint URL. The {folder}/{file} route segments can't carry a slash, the
// folder is allowlisted, and the file name is sanitized (no traversal) in the provider.
// Authenticated (these pages are behind login). Bytes are memory-cached ~15 min in the
// service so replacing a file on SharePoint propagates within the window.
app.MapGet("/venue-image/{folder}/{file}", async (
        string folder, string file,
        CommunityHub.Venue.VenueImageProvider venue,
        CancellationToken ct) =>
{
    var img = await venue.GetImageAsync(folder, file, ct);
    return img is null
        ? Results.NotFound()
        : Results.File(img.Content, img.ContentType);
}).RequireAuthorization();

// §665: SERVER-PROXIED speaker photo. A sponsor-uploaded photo lives in the SharePoint
// speaker-photo folder; this streams it with the app's OWN credentials. Before this the page
// rendered the raw SharePoint document URL, which a browser cannot fetch (no SharePoint creds ⇒
// sign-in page ⇒ broken image), and re-uploading could never fix it.
// ANONYMOUS on purpose: speaker headshots are published content (the programme and the public
// speaker catalogue render them), so requiring auth here would break those pages. The proxy stays
// bounded — only the configured folder, only image extensions, and the name is reduced to a leaf
// and matched against the folder listing, so it cannot read anything else on the drive.
app.MapGet("/speaker-photo/{file}", async (
        string file,
        CommunityHub.Core.Integrations.Graphics.SpeakerPhotoService photos,
        CancellationToken ct) =>
{
    var photo = await photos.GetPhotoAsync(file, ct);
    return photo is null
        ? Results.NotFound()
        : Results.File(photo.Content, photo.ContentType);
}).AllowAnonymous();

// §153: DIRECT download of the speaker presentation template — streamed from SharePoint with the
// app's own creds (no SharePoint-site link). 404s when the proxy isn't configured/available; the
// speaker page only links here when the service reports IsAvailable, else it uses the fallback URL.
app.MapGet("/speaker-template/download", async (
        CommunityHub.Core.Integrations.Graphics.SpeakerTemplateService templates,
        CancellationToken ct) =>
{
    var tpl = await templates.GetTemplateAsync(ct);
    return tpl is null
        ? Results.NotFound()
        : Results.File(tpl.Content, tpl.ContentType, fileDownloadName: tpl.FileName);
}).RequireAuthorization();

// §326f-c: DIRECT download of the logo pack (Experts Live DK + ELDK27 event logos) —
// streamed from SharePoint with the app registration's creds ("everything runs on the
// app reg", operator 2026-07-25) instead of a share link. 404s until the folder is
// configured (Graphics:SharePoint:LogoPackFolderPath).
app.MapGet("/logo-pack/download", async (
        CommunityHub.Core.Integrations.Graphics.LogoPackService logos,
        CancellationToken ct) =>
{
    var pack = await logos.GetAsync(ct);
    return pack is null
        ? Results.NotFound()
        : Results.File(pack.Content, pack.ContentType, fileDownloadName: pack.FileName);
}).RequireAuthorization();

// §160: server-proxied download of ONE of the signed-in speaker's OWN released graphics — streamed
// from SharePoint with the app's creds so the speaker (who has no SharePoint permission) actually
// gets the file. Ownership + release gate enforced in the service; 404 otherwise.
app.MapGet("/speaker-graphic/{id:int}", async (
        int id,
        CommunityHub.Core.Integrations.Graphics.GraphicsService graphics,
        CommunityHub.Auth.ICurrentParticipantAccessor participant,
        CancellationToken ct) =>
{
    var me = participant.Current;
    if (me is null) return Results.Unauthorized();
    var f = await graphics.GetSpeakerGraphicFileAsync(me.EventId, me.ParticipantId, id, ct);
    return f is null
        ? Results.NotFound()
        : Results.File(f.Content, f.ContentType, fileDownloadName: f.FileName);
}).RequireAuthorization();

// §841: ORGANIZER-ONLY preview of an event SoMe graphic, by FILE NAME — what SoMePost.ImageRef
// holds (§828.7). Streamed from SharePoint with the app's creds because an organizer's browser has
// no SharePoint permission of its own, mirroring the speaker-graphic proxy above.
// 🔒 Organizer-gated, not merely authenticated: these are unpublished campaign assets.
// 🔒 The service refuses a name containing a path separator — ImageRef is operator-editable, so it
// is untrusted input and must never be able to walk out of the folder.
app.MapGet("/organizer/some-graphic", async (
        string? name,
        int? kind,
        CommunityHub.Core.Domain.SoMePostMediaKind? media,
        CommunityHub.Core.Integrations.SoMeGraphicLibrary library,
        CommunityHub.Auth.ICurrentParticipantAccessor participant,
        HttpContext http,
        CancellationToken ct) =>
{
    var me = participant.Current;
    if (me is null) return Results.Unauthorized();
    if (me.Role != CommunityHub.Core.Domain.ParticipantRole.Organizer) return Results.Forbid();

    // §844.3 — the post TYPE and MEDIUM decide which library folder the name is resolved from.
    // Both default to the event-graphics folder, which is what the §841 callers relied on.
    var f = await library.GetAsync(
        name,
        kind is null ? CommunityHub.Core.Integrations.SoMeTemplateKind.EventPost
                     : (CommunityHub.Core.Integrations.SoMeTemplateKind)kind.Value,
        media ?? CommunityHub.Core.Domain.SoMePostMediaKind.Graphic,
        ct);

    if (f is null) return Results.NotFound();

    // The gallery loads dozens of these per page; they are immutable per name.
    http.Response.Headers["Cache-Control"] = "private, max-age=3600";
    return Results.File(f.Content, f.ContentType);
}).RequireAuthorization();

// §172: PUBLIC (no-auth) OpenGraph image for a session's public detail page. Streams the
// session's RELEASED SoMe session-graphic (lowest id, active edition) from SharePoint with the
// app's creds, so social crawlers (LinkedIn/X) can fetch it for the shared link's preview card —
// the auth proxy above is unreachable to a crawler. Released promo graphics are public BY DESIGN;
// a draft/unreleased, sponsor, non-graphic or unknown session ⇒ 404 (enforced in the service).
// AllowAnonymous opts out of the fail-closed FallbackPolicy; cached so crawlers don't hammer the store.
app.MapGet("/og/session-graphic/{sessionId:int}", async (
        int sessionId,
        CommunityHub.Core.Integrations.Graphics.GraphicsService graphics,
        HttpContext http,
        CancellationToken ct) =>
{
    var f = await graphics.GetPublicSessionOgGraphicAsync(sessionId, ct);
    if (f is null) return Results.NotFound();
    http.Response.Headers["Cache-Control"] = "public, max-age=3600";
    return Results.File(f.Content, f.ContentType);
}).AllowAnonymous();

// §192 (reworking §166): server-proxied download of a session's FINAL evaluation PDF of a
// KIND (score|feedback) — streamed from SharePoint with the app's creds so a speaker (no
// SharePoint permission) actually gets the file. Access gate (organizer in the edition OR a
// speaker on this session) is enforced in the service; 404 on unknown kind / not allowed / no file.
app.MapGet("/session-eval/{id:int}/{kind}/download", async (
        int id, string kind,
        CommunityHub.Core.Integrations.Graphics.SessionEvalPdfService evalPdfs,
        CommunityHub.Auth.ICurrentParticipantAccessor participant,
        CancellationToken ct) =>
{
    var me = participant.Current;
    if (me is null) return Results.Unauthorized();
    var parsed = CommunityHub.Core.Integrations.Graphics.SessionEvalPdfService.ParseKind(kind);
    if (parsed is null) return Results.NotFound();
    var f = await evalPdfs.GetPdfForParticipantAsync(me.EventId, me.ParticipantId, me.Role, id, parsed.Value, ct);
    return f is null
        ? Results.NotFound()
        : Results.File(f.Content, "application/pdf", fileDownloadName: f.FileName);
}).RequireAuthorization();

// §322c: PUBLIC (anonymous) proxy download of a session's LATEST deck (preview|final) —
// streamed from SharePoint with the app's creds so attendees need no login and never see a
// SharePoint URL. Session slides are public BY DESIGN (the /Sessions/Slides catalogue);
// unknown session / kind / no deck ⇒ 404.
app.MapGet("/session-slides/{id:int}/{kind}/download", async (
        int id, string kind,
        CommunityHub.Core.Integrations.Graphics.SpeakerPresentationService presentations,
        CancellationToken ct) =>
{
    var parsed = ParseSlidesKind(kind);
    if (parsed is null) return Results.NotFound();
    var f = await presentations.GetDeckAsync(id, parsed.Value, ct);
    if (f is null) return Results.NotFound();
    // §322h: one hit per download (also covers the Office-embed fetch for PPTX views —
    // the viewer page itself does not count, so nothing double-counts).
    await presentations.RecordHitAsync(id, ct);
    // §322i: users get the CLEAN name (no "{sessionId} - " storage prefix).
    return Results.File(f.Content, f.ContentType,
        fileDownloadName: CommunityHub.Core.Integrations.Graphics.SpeakerPresentationService.DisplayName(f.FileName));
}).AllowAnonymous();

// §322c: the VIEW route — a PDF streams inline in the browser tab; a PPTX redirects to the
// Office web viewer pointed at the public download URL (which is anonymous, so the viewer
// can fetch it); anything else falls back to the download.
app.MapGet("/session-slides/{id:int}/{kind}/view", async (
        int id, string kind, HttpContext http,
        CommunityHub.Core.Integrations.Graphics.SpeakerPresentationService presentations,
        CancellationToken ct) =>
{
    var parsed = ParseSlidesKind(kind);
    if (parsed is null) return Results.NotFound();
    var f = await presentations.GetDeckAsync(id, parsed.Value, ct);
    if (f is null) return Results.NotFound();

    if (f.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
    {
        // §322h: one hit per embedded PDF render (the PPTX path counts via the
        // Office-embed's /download fetch instead — never both).
        await presentations.RecordHitAsync(id, ct);
        return Results.File(f.Content, f.ContentType);   // no download name ⇒ renders inline
    }
    if (f.FileName.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase))
    {
        var absolute = $"{http.Request.Scheme}://{http.Request.Host}/session-slides/{id}/{kind}/download";
        return Results.Redirect(
            "https://view.officeapps.live.com/op/view.aspx?src=" + Uri.EscapeDataString(absolute));
    }
    return Results.Redirect($"/session-slides/{id}/{kind}/download");   // zip → download
}).AllowAnonymous();

// §322f: PUBLIC batch download — one ZIP holding the EFFECTIVE deck (§322d: final wins,
// else preview) of every selected session (?ids=1&ids=2…). Streams the archive and pulls
// one deck at a time, so memory stays bounded to a single deck. Sessions without a deck
// are skipped; nothing at all ⇒ 404. GET on purpose (anonymous, no antiforgery, linkable
// — e.g. "all Azure-track slides" from the filtered multi-select).
app.MapGet("/session-slides/batch-download", async (
        HttpContext http,
        CommunityHub.Core.Integrations.Graphics.SpeakerPresentationService presentations,
        CancellationToken ct) =>
{
    var ids = http.Request.Query["ids"]
        .Select(v => int.TryParse(v, out var i) ? i : (int?)null)
        .Where(i => i is not null)
        .Select(i => i!.Value)
        .Distinct()
        .ToList();
    if (ids.Count == 0) return Results.NotFound();

    var rows = (await presentations.ListPublicAsync(ct))
        .Where(s => ids.Contains(s.SessionId) && s.EffectiveKind is not null)
        .ToList();
    if (rows.Count == 0) return Results.NotFound();

    // §659 — tell the PAGE that the download has started, so it can drop its "Preparing your
    // download…" state at the right moment. A download response never fires a page event (the
    // page it was requested from is not navigated), so the only signal available to JS is a
    // cookie echoed back on the download response itself. NOT HttpOnly, deliberately: the whole
    // point is that the page's script reads it. The value is the caller's own opaque token, so
    // nothing about the request or the user is disclosed by it.
    var dlToken = http.Request.Query["dl"].ToString();
    if (!string.IsNullOrWhiteSpace(dlToken) && dlToken.Length <= 64)
    {
        http.Response.Cookies.Append("slides-dl", dlToken, new CookieOptions
        {
            Path = "/",
            HttpOnly = false,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            Secure = http.Request.IsHttps,
            MaxAge = TimeSpan.FromMinutes(5),
        });
    }

    return Results.Stream(async stream =>
    {
        using var zip = new System.IO.Compression.ZipArchive(
            stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var deck = await presentations.GetDeckAsync(row.SessionId, row.EffectiveKind!.Value, ct);
            if (deck is null) continue;   // vanished between listing and pull — tolerate
            // §322i: clean entry names; fall back to the stored (id-prefixed) name when
            // two sessions share a title so entries never collide.
            var name = CommunityHub.Core.Integrations.Graphics.SpeakerPresentationService.DisplayName(deck.FileName);
            if (!usedNames.Add(name)) name = deck.FileName;
            var entry = zip.CreateEntry(name, System.IO.Compression.CompressionLevel.Fastest);
            await using (var es = entry.Open())
            {
                await es.WriteAsync(deck.Content, ct);
            }
            await presentations.RecordHitAsync(row.SessionId, ct);   // §322h: one hit per included session
        }
    }, "application/zip", fileDownloadName: "eldk27-session-slides.zip");
}).AllowAnonymous();

static CommunityHub.Core.Integrations.Graphics.PresentationKind? ParseSlidesKind(string? kind) =>
    (kind ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "preview" => CommunityHub.Core.Integrations.Graphics.PresentationKind.Preview,
        "final" => CommunityHub.Core.Integrations.Graphics.PresentationKind.Final,
        _ => null,
    };

app.MapRazorPages();
app.MapControllers();

app.Run();

