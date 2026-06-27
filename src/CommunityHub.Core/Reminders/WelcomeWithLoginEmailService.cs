using CommunityHub.Core.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Reminders;

/// <summary>
/// The outcome of a welcome-with-login send.
/// </summary>
public sealed record WelcomeWithLoginResult(bool Sent, string Reason)
{
    public static WelcomeWithLoginResult Ok() => new(true, "Sent.");
    public static WelcomeWithLoginResult RefusedNotDev(string env) =>
        new(false, $"Refused: the welcome-with-login email is DEV-only and the environment is '{env}'.");
    public static WelcomeWithLoginResult NoSuchParticipant() =>
        new(false, "Refused: participant not found, inactive, or in a different edition.");
}

/// <summary>
/// Sends the <b>welcome email for all roles</b> with a one-click
/// <b>auto-login</b> link (a real magic-link — the token authenticates the
/// recipient and lands them in their role hub, not a forgeable <c>?email=</c>
/// prefill). It introduces participants to the Event Hub, explains how it fits
/// alongside Zoho Backstage, and carries a single CTA. The copy is role-aware
/// (one role-specific line per recipient) and brand-new-this-year framed.
///
/// <para><b>DEV-ONLY hard guard.</b> The send is <i>refused</i> unless the host
/// environment is Development (<see cref="IEnvironmentInfo.IsDevelopment"/>).
/// This is a code-level gate that does not depend on the email-redirect config:
/// even if <c>Email:RedirectAllTo</c> were misconfigured, this email cannot go
/// out from a non-DEV host. In DEV, the existing
/// <see cref="IEmailSender"/> redirect (all mail → the DEV test address) still
/// applies, so test sends never reach real people.</para>
///
/// <para><b>Re-sendable + recorded.</b> Unlike the legacy once-ever welcome
/// (<see cref="WelcomeEmailService"/>, idempotent via <c>SentReminder</c>), this
/// send is intentionally repeatable so it can be tested again and again. Each
/// send stamps <see cref="Participant.WelcomeWithLoginSentAt"/> with the current
/// time, so the database records who was sent and when (the audit), without
/// gating a re-send.</para>
/// </summary>
public sealed class WelcomeWithLoginEmailService
{
    /// <summary>
    /// Auto-login token lifetime for the welcome email. Short-lived (the welcome
    /// is a one-tap onboarding nudge, not a standing credential); the default
    /// lives on <see cref="WelcomeAutoLoginTokenService.DefaultTtl"/> so the mint
    /// and the docs agree. A lapsed link falls back to email + a one-time code.
    /// </summary>
    public static readonly TimeSpan TokenTtl = WelcomeAutoLoginTokenService.DefaultTtl;

    private readonly CommunityHubDbContext _db;
    private readonly EmailTemplateProvider _templates;
    private readonly IEmailSender _emailSender;
    private readonly IWelcomeAutoLoginTokenService _autoLogin;
    private readonly IEnvironmentInfo _env;
    private readonly TimeProvider _clock;
    private readonly IEmailContextAccessor? _context;
    private readonly WelcomeEmailOptions _options;

    public WelcomeWithLoginEmailService(
        CommunityHubDbContext db,
        EmailTemplateProvider templates,
        IEmailSender emailSender,
        IWelcomeAutoLoginTokenService autoLogin,
        IEnvironmentInfo env,
        TimeProvider clock,
        IEmailContextAccessor? context = null,
        WelcomeEmailOptions? options = null)
    {
        _db = db;
        _templates = templates;
        _emailSender = emailSender;
        _autoLogin = autoLogin;
        _env = env;
        _clock = clock;
        _context = context;
        // Default: auto-login DISABLED (operator "disable welcome mail with login").
        _options = options ?? new WelcomeEmailOptions();
    }

    /// <summary>Whether the auto-login magic-link is minted (else the plain hub URL is used).</summary>
    public bool AutoLoginEnabled => _options.AutoLoginEnabled;

    /// <summary>
    /// Send the welcome-with-login email to one active participant. The
    /// <paramref name="baseUrl"/> is the per-environment public origin
    /// (e.g. <c>https://dev.eldk27.eventhub.expertslive.dk</c>); the caller
    /// derives it from the request host so dev and prod each emit their own URL.
    /// Returns whether it sent and why (refused-not-dev / no-such-participant).
    /// </summary>
    public async Task<WelcomeWithLoginResult> SendAsync(
        int participantId, string baseUrl, CancellationToken ct = default)
    {
        // DEV-ONLY HARD GUARD — refuse before touching the DB or the mailer.
        if (!_env.IsDevelopment)
        {
            return WelcomeWithLoginResult.RefusedNotDev(_env.EnvironmentName);
        }

        return await SendCoreAsync(participantId, baseUrl, onlyIfUnsent: false, ct);
    }

    /// <summary>
    /// Send the welcome-with-login email as part of ATTENDEE AUTO-PROVISIONING
    /// (the 2-day-ticket sync). Unlike <see cref="SendAsync"/> this has NO DEV-only
    /// guard — it is meant to run from the production sync job — but it is gated
    /// upstream by the <c>attendee-welcome</c> feature flag (default OFF) and the
    /// central email kill-switch / ring gate, and here it is IDEMPOTENT: it sends
    /// only when the participant has never been welcomed
    /// (<see cref="Participant.WelcomeWithLoginSentAt"/> is null), so a re-run never
    /// re-emails an attendee.
    /// </summary>
    public Task<WelcomeWithLoginResult> SendForAttendeeProvisioningAsync(
        int participantId, string baseUrl, CancellationToken ct = default)
        => SendCoreAsync(participantId, baseUrl, onlyIfUnsent: true, ct);

    private async Task<WelcomeWithLoginResult> SendCoreAsync(
        int participantId, string baseUrl, bool onlyIfUnsent, CancellationToken ct)
    {
        var participant = await _db.Participants
            .Include(p => p.Event)
            .FirstOrDefaultAsync(p => p.Id == participantId, ct);
        if (participant is null || !participant.IsActive)
        {
            return WelcomeWithLoginResult.NoSuchParticipant();
        }

        // Idempotency for the provisioning path: never re-welcome someone already
        // welcomed (so a re-run of the sync job does not re-email attendees).
        if (onlyIfUnsent && participant.WelcomeWithLoginSentAt is not null)
        {
            return new WelcomeWithLoginResult(false, "Already welcomed.");
        }

        // Roles with NO welcome (Organizer, Attendee — operator 2026-06-22) are not
        // sent a welcome from this platform.
        var roleTemplateKey = WelcomeVariants.TemplateKeyFor(participant.Role);
        if (string.IsNullOrEmpty(roleTemplateKey))
        {
            return new WelcomeWithLoginResult(false, $"No welcome for the {participant.Role} role.");
        }

        // AUTO-LOGIN GATE (operator "disable welcome mail with login"): when enabled
        // we mint a SINGLE-USE, short-lived, revocable, audited auto-login token (its
        // own MagicLinkGrant row) — NOT the reusable invitation magic-link. When
        // DISABLED (the default) we mint NOTHING and the CTA points at the plain hub
        // URL, so the recipient signs in normally with email + a one-time code.
        string loginUrl;
        if (_options.AutoLoginEnabled)
        {
            var token = await _autoLogin.CreateAsync(participant.Id, TokenTtl, ct);
            loginUrl = BuildLoginUrl(baseUrl, token);
        }
        else
        {
            loginUrl = (baseUrl ?? string.Empty).TrimEnd('/');
        }

        var rendered = RenderForUrl(participant, loginUrl);
        // Ring-governed by the welcome-email feature (operator 2026-06-22).
        using (_context?.Set(new EmailContext(
            roleTemplateKey,
            participant.EventId, participant.Id, participant.FullName,
            FeatureKey: "welcome-email")))
        {
            await _emailSender.SendAsync(
                participant.Email, rendered.Subject, rendered.HtmlBody, rendered.TextBody, ct);
        }

        // Record the send. Each send mints a FRESH single-use grant, so an earlier
        // link is left to expire / be superseded — a re-send does not resurrect a
        // used link.
        participant.WelcomeWithLoginSentAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);

        return WelcomeWithLoginResult.Ok();
    }

    /// <summary>
    /// Render the welcome email for a participant against an already-built sign-in
    /// URL (an auto-login magic-link when auto-login is enabled, otherwise the plain
    /// hub URL). Renders the per-role VARIANT template (by the participant's PRIMARY
    /// role via <see cref="WelcomeVariants"/>). Public + pure (no DB / no send / no
    /// token mint / no guard) so the per-role copy can be unit-tested directly. Also
    /// exposes the plain-text alternative on the returned record.
    /// </summary>
    public RenderedWelcomeEmail RenderForUrl(Participant participant, string loginUrl)
    {
        var firstName = string.IsNullOrWhiteSpace(participant.FullName)
            ? "there"
            : participant.FullName.Split(' ')[0];

        var templateKey = WelcomeVariants.TemplateKeyFor(participant.Role)
            ?? throw new InvalidOperationException(
                $"No welcome template for role {participant.Role} — the caller must guard before rendering.");

        var tokens = _templates.NewTokenSet();
        tokens["firstName"] = firstName;
        tokens["communityName"] = participant.Event.CommunityName;
        tokens["eventDisplayName"] = participant.Event.DisplayName;
        tokens["eventCode"] = participant.Event.Code;
        tokens["roleName"] = FriendlyRoleName(participant.Role);
        tokens["roleLine"] = RoleLine(participant.Role);
        // Sponsor welcome: the "you are receiving this in your role as …" line is
        // DYNAMIC — built from the recipient's actual sponsor flags (event
        // coordinator / signer / booth member), not a static value.
        tokens["sponsorRole"] = WelcomeVariants.SponsorRoleLabel(
            participant.IsEventCoordinator, participant.IsSigner, participant.IsBoothMember);
        // Both loginUrl and hubUrl resolve to the same sign-in URL the caller built:
        // the magic-link when auto-login is on, the plain hub URL when off. Templates
        // may reference either token. (NewTokenSet already seeds the branding hubUrl;
        // override it so the variant always points at the same place as loginUrl.)
        tokens["loginUrl"] = loginUrl;
        tokens["hubUrl"] = loginUrl;

        var html = _templates.Render(templateKey, tokens);
        var text = BuildPlainText(
            firstName,
            participant.Event.CommunityName,
            participant.Event.DisplayName,
            FriendlyRoleName(participant.Role),
            RoleLine(participant.Role),
            loginUrl);

        return new RenderedWelcomeEmail(html.Subject, html.HtmlBody, text);
    }

    /// <summary>
    /// Compose the magic-link auto-login URL from the per-environment base URL
    /// and an opaque token. The path matches the shipped magic-link handler
    /// (<c>/Login/Magic?token=...</c>), so following it authenticates and lands
    /// the recipient in their role hub (the handler redirects to <c>/</c>).
    /// </summary>
    public static string BuildLoginUrl(string baseUrl, string token)
    {
        var origin = (baseUrl ?? string.Empty).TrimEnd('/');
        return $"{origin}/Login/Magic?token={Uri.EscapeDataString(token)}";
    }

    /// <summary>Friendly role noun used in the greeting line. Covers every role.</summary>
    public static string FriendlyRoleName(ParticipantRole role) => role switch
    {
        ParticipantRole.Organizer => "organizer",
        ParticipantRole.Speaker => "speaker",
        ParticipantRole.Volunteer => "volunteer",
        ParticipantRole.Sponsor => "sponsor contact",
        ParticipantRole.Attendee => "attendee",
        ParticipantRole.Media => "media crew member",
        ParticipantRole.EventPartner => "event partner",
        _ => "crew member",
    };

    /// <summary>
    /// The single role-specific line in the welcome email — one short sentence
    /// telling each role what their part of the Hub is for. Every role in
    /// <see cref="ParticipantRole"/> has an entry (the requirement: all roles
    /// get a role-specific line).
    /// </summary>
    public static string RoleLine(ParticipantRole role) => role switch
    {
        ParticipantRole.Organizer =>
            "As an organizer you get the full picture: participants, sponsors, attendee reconciliation and the live dashboards that run the whole event from one place.",
        ParticipantRole.Speaker =>
            "As a speaker, start with the \"Get Started\" flow in the hub — it walks you through everything you need to set up. Afterwards you can change anything under Event Logistics if needed.",
        ParticipantRole.Volunteer =>
            "As a volunteer, start with the \"Get Started\" flow in the hub — it walks you through everything you need to set up. Afterwards you can change anything under Event Logistics if needed.",
        ParticipantRole.Sponsor =>
            "As a sponsor contact you get your company's onboarding tasks and deadlines, and you can capture the leads you meet at your booth — straight from your phone.",
        ParticipantRole.Attendee =>
            "As an attendee you get your own \"My Event\" page: a countdown, your Master Class status, the practical info, and a one-tap check-in on the day.",
        ParticipantRole.Media =>
            "As part of the press / media crew, start with the \"Get Started\" flow in the hub — it walks you through everything you need to set up. Afterwards you can change anything under Event Logistics if needed.",
        ParticipantRole.EventPartner =>
            "As an event partner, start with the \"Get Started\" flow in the hub — it walks you through everything you need to set up. Afterwards you can change anything under Event Logistics if needed.",
        _ =>
            "Sign in to see exactly the part of the event that is relevant to you.",
    };

    /// <summary>
    /// The plain-text alternative body, mirroring the HTML copy. Kept in C#
    /// (not a template file) because the renderer + layout are HTML-only; this
    /// is short, single-CTA, and mobile-friendly by being plain.
    /// </summary>
    private static string BuildPlainText(
        string firstName,
        string communityName,
        string eventDisplayName,
        string roleName,
        string roleLine,
        string loginUrl) =>
$@"Hi {firstName},

Welcome to {communityName} — you have been added to the Event Hub for {eventDisplayName} as a {roleName}.

The Event Hub is the one place for everyone's part in the event: your forms, your tasks, your deadlines — all tailored to your role.

{roleLine}

Open the Event Hub:
{loginUrl}

Sign in with your email address — no password to remember.

If anything looks off or breaks, just reply to this email and tell us — your feedback genuinely helps.

See you at {eventDisplayName}.
The team";
}

/// <summary>
/// A rendered welcome-with-login email: subject, HTML body, and the plain-text
/// alternative. (The shared <c>RenderedEmail</c> carries only subject + HTML;
/// this adds the plain-text part the requirement asks for.)
/// </summary>
public sealed record RenderedWelcomeEmail(string Subject, string HtmlBody, string TextBody);
