using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Auth;

/// <summary>
/// The outcome of a "send me a PIN" request.
/// </summary>
public sealed record PinRequestResult(bool Accepted, string Message)
{
    public static PinRequestResult Ok() =>
        new(true, "If that email is registered, a code has been sent.");

    public static PinRequestResult RateLimited() =>
        new(false, "Too many code requests. Please wait a while and try again.");
}

/// <summary>
/// Handles the "request a login PIN" half of PIN auth (CONTEXT.md section 5):
/// generate a PIN, store its hash with a 15-minute expiry, email the
/// plaintext, and rate-limit requests per email.
///
/// Verification is handled by <see cref="PinIdentityProvider"/>.
///
/// Privacy: the result is deliberately the SAME whether or not the email is a
/// known participant, so this endpoint cannot be used to enumerate who is
/// registered. A PIN is only generated/sent for a real, active participant.
/// </summary>
public sealed class PinLoginService
{
    /// <summary>A PIN is valid for this long after it is issued.</summary>
    public static readonly TimeSpan PinLifetime = TimeSpan.FromMinutes(15);

    /// <summary>Max PIN requests allowed per email within the rate window.</summary>
    private const int MaxRequestsPerWindow = 5;

    /// <summary>The rate-limit window.</summary>
    private static readonly TimeSpan RateWindow = TimeSpan.FromHours(1);

    private readonly CommunityHubDbContext _db;
    private readonly PinService _pinService;
    private readonly IEmailSender _emailSender;
    private readonly TimeProvider _clock;
    private readonly IEmailContextAccessor? _context;

    // Optional template provider for the sign-in PIN mail (the generic shipped
    // default is the fallback). Null in older test constructions → inline HTML.
    private readonly EmailTemplateProvider? _templates;

    public PinLoginService(
        CommunityHubDbContext db,
        PinService pinService,
        IEmailSender emailSender,
        TimeProvider clock,
        IEmailContextAccessor? context = null,
        EmailTemplateProvider? templates = null)
    {
        _db = db;
        _pinService = pinService;
        _emailSender = emailSender;
        _clock = clock;
        _context = context;
        _templates = templates;
    }

    /// <summary>
    /// Request a login PIN for an email within the active event.
    /// </summary>
    public async Task<PinRequestResult> RequestPinAsync(
        int eventId,
        string email,
        CancellationToken cancellationToken = default)
    {
        var normalizedEmail = NormalizeEmail(email);
        var now = _clock.GetUtcNow();

        var participant = await _db.Participants
            .Include(p => p.LoginPins)
            .FirstOrDefaultAsync(
                p => p.EventId == eventId
                     && p.Email == normalizedEmail
                     && p.IsActive
                     // Onboarding gate: a not-yet-activated queue entry cannot
                     // sign in (login requires IsActive AND lifecycle Active).
                     && p.LifecycleState == ParticipantLifecycleState.Active,
                cancellationToken);

        // Unknown / inactive email: return the same "ok" message so the
        // endpoint reveals nothing. No email is sent.
        if (participant is null)
        {
            return PinRequestResult.Ok();
        }

        // Rate limit: count PINs issued to this participant in the window.
        var windowStart = now - RateWindow;
        var recentCount = participant.LoginPins
            .Count(pin => pin.CreatedAt >= windowStart);
        if (recentCount >= MaxRequestsPerWindow)
        {
            return PinRequestResult.RateLimited();
        }

        // Generate the PIN, store only its hash.
        var plainPin = _pinService.GeneratePin();
        var loginPin = new LoginPin
        {
            ParticipantId = participant.Id,
            PinHash = _pinService.HashPin(plainPin),
            CreatedAt = now,
            ExpiresAt = now + PinLifetime,
        };
        _db.LoginPins.Add(loginPin);
        await _db.SaveChangesAsync(cancellationToken);

        // Email the plaintext PIN. It is never logged or persisted.
        // Prefix subject with the active event's code (e.g. "ELDK27") so the
        // recipient's mail client groups + filters the hub's mail consistently.
        var eventCode = await _db.Events
            .Where(e => e.Id == eventId)
            .Select(e => e.Code)
            .FirstOrDefaultAsync(cancellationToken);
        var subjectPrefix = string.IsNullOrWhiteSpace(eventCode)
            ? "Event Hub"
            : $"{eventCode} Event Hub";

        var firstName = FirstNameOf(participant.FullName);
        // SIGN-IN EXEMPTION (operator 2026-06-22): the on-demand PIN must reach a user
        // at ANY ring — mark this send ring-exempt so the gate never drops it. (The
        // global kill switch still applies.) `using (null)` is a safe no-op in test wiring.
        using (_context?.Set(new EmailContext(
            "pin-signin", eventId, participant.Id, participant.FullName, RingExempt: true)))
        {
            if (_templates is not null)
            {
                var tokens = _templates.NewTokenSet();
                tokens["firstName"] = firstName;
                tokens["pin"] = plainPin;
                tokens["expiryMinutes"] = ((int)PinLifetime.TotalMinutes).ToString();
                tokens["subjectPrefix"] = subjectPrefix;
                var rendered = _templates.Render("pin-signin", tokens);
                await _emailSender.SendAsync(
                    participant.Email, rendered.Subject, rendered.HtmlBody, cancellationToken);
            }
            else
            {
                // Fallback: legacy inline HTML (older test constructions with no provider).
                var subject = $"{subjectPrefix} - Your sign-in code";
                var body = BuildPinEmail(participant.FullName, plainPin);
                await _emailSender.SendAsync(
                    participant.Email, subject, body, cancellationToken);
            }
        }

        return PinRequestResult.Ok();
    }

    /// <summary>Lower-case + trim, so lookups match how emails are stored.</summary>
    public static string NormalizeEmail(string email) =>
        (email ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>First word of a full name, or "there" when blank.</summary>
    private static string FirstNameOf(string name) =>
        string.IsNullOrWhiteSpace(name) ? "there" : name.Split(' ')[0];

    private static string BuildPinEmail(string name, string pin)
    {
        var firstName = FirstNameOf(name);

        // Minimal inline-styled HTML; the full branded template comes from the
        // email-template system in later stages.
        return $@"<p>Hi {WebEncode(firstName)},</p>
<p>Your sign-in code is:</p>
<p style=""font-size:28px;font-weight:bold;letter-spacing:4px;"">{pin}</p>
<p>This code expires in 15 minutes and can be used once.</p>
<p>If you did not request this, you can ignore this email.</p>";
    }

    private static string WebEncode(string s) =>
        System.Net.WebUtility.HtmlEncode(s);
}
