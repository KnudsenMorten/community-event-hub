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

    /// <summary>
    /// §361 — the address belongs to a NON-2-day attendee, who has no hub access, so no PIN was
    /// sent and none ever will be. Operator 2026-07-26: <i>"i expected to get a message … 'Access
    /// to Event Hub is only relevant to 2-day ticket holders …'. instead i got [the neutral
    /// message] but of course i never get the pin"</i>.
    ///
    /// <para>⚠️ <b>This deliberately breaks the anti-enumeration property</b> that the neutral
    /// <see cref="Ok"/> exists to protect: answering differently confirms the address IS a known
    /// 1-day attendee. The operator was shown the trade-off and chose this — a person waiting
    /// forever for a code that is never coming is the worse outcome. The disclosure is narrow
    /// (the ticket class of an address the asker already typed) and reveals no PIN, name or
    /// other personal detail.</para>
    /// </summary>
    public static PinRequestResult NoHubAccessForTicket() =>
        new(false,
            "Access to the Event Hub is only relevant for 2-day ticket holders, because it is "
            + "used for the Master Class functionality. Your sign-in request was therefore not "
            + "sent. If you think this is wrong, please contact info@expertslive.dk.");

    /// <summary>
    /// §444 — the address belongs to a participant whose account is NOT ACTIVE (deactivated, or
    /// still in the onboarding queue). Sign-in requires <c>IsActive</c> AND
    /// <c>LifecycleState == Active</c>, so the lookup misses, no PIN is generated, and without
    /// this they are shown the code box and wait forever for a mail that will never arrive.
    /// Operator 2026-07-27: <i>"similar to the experience for 1-day ticket holder … you should add
    /// a similar message for anyone trying to sign-in with inactive status. they should not try to
    /// get a sign-in code"</i>.
    ///
    /// <para>⚠️ <b>Same deliberate anti-enumeration trade as <see cref="NoHubAccessForTicket"/></b>,
    /// and taken for the same reason: answering differently confirms the address is registered.
    /// The project already accepted that trade for the 1-day gate; a person stuck retrying a code
    /// that can never come is the worse outcome. The disclosure is narrow — account STATE for an
    /// address the asker already typed — and reveals no PIN, name or other detail. A stranger
    /// probing an address we do not hold still gets the neutral <see cref="Ok"/>.</para>
    /// </summary>
    public static PinRequestResult AccountNotActive() =>
        new(false,
            "This account is not active, so no sign-in code was sent and requesting another one "
            + "will not help. If you think this is wrong, please contact info@expertslive.dk.");
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

    /// <summary>
    /// Max PIN requests allowed per email within the rate window.
    ///
    /// <para><b>Operator decision 2026-07-27 — raised 5 → 1000/hour, i.e. effectively off.</b> The
    /// old cap was scoped wrong for this product. It was never a THROUGHPUT limit: it counts per
    /// ACCOUNT, so 2,000 users always had 2,000 × 5 between them and the platform was never capped
    /// at 5 logins/hour. What it actually did was stop one person requesting more than 5 PINs an
    /// hour — invisible in normal use, and squarely in the way of repeatedly testing one account.</para>
    ///
    /// <para><b>What is given up, plainly:</b> the only thing it protected against was a mail-bomb —
    /// somebody pasting a real address into the login form over and over to flood that person's
    /// inbox with PIN e-mails. That is now possible up to 1000/hour, bounded only by the e-mail
    /// kill switch and the per-hour send ceiling (§326av).</para>
    ///
    /// <para><b>What is NOT given up:</b> brute-force protection is a SEPARATE control and is
    /// untouched — <c>PinIdentityProvider.MaxFailedAttempts = 5</c> locks an individual PIN after
    /// five wrong guesses, PINs expire after 15 minutes, and only the hash is ever stored. Guessing
    /// a PIN is exactly as hard as it was.</para>
    /// </summary>
    private const int MaxRequestsPerWindow = 1000;

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

        // Match the PRIMARY email or the optional ALTERNATE login email (§26d).
        var participant = await _db.Participants
            .Include(p => p.LoginPins)
            .FirstOrDefaultAsync(
                p => p.EventId == eventId
                     && (p.Email == normalizedEmail || p.AlternateEmail == normalizedEmail)
                     && p.IsActive
                     // Onboarding gate: a not-yet-activated queue entry cannot
                     // sign in (login requires IsActive AND lifecycle Active).
                     && p.LifecycleState == ParticipantLifecycleState.Active,
                cancellationToken);

        // Unknown / inactive email: return the same "ok" message so the
        // endpoint reveals nothing. No email is sent.
        if (participant is null)
        {
            // §361 (operator 2026-07-26) — ONE named exception to the neutral reply. A NON-2-day
            // attendee is provisioned but left IsActive = false, so the lookup above misses and
            // they would wait forever for a code that is never generated. Tell them why.
            //
            // Scoped as narrowly as possible: it fires only when the address matches an Attendee
            // row in THIS edition holding a KNOWN non-2-day ticket (TicketStatus.Other). An
            // address with no attendee row — i.e. a stranger probing the endpoint — still gets
            // the neutral reply, so the enumeration surface is limited to ticket class, not
            // existence in general. TicketStatus.None is deliberately EXCLUDED: it means the
            // Zoho sync found no matching order line, which is a data gap, not a 1-day ticket —
            // telling such a person "you're not a 2-day holder" could be flatly wrong.
            var nonTwoDayTicket = await _db.Attendees
                .AnyAsync(
                    a => a.EventId == eventId
                         && a.Email == normalizedEmail
                         && a.TicketStatus == TicketStatus.Other,
                    cancellationToken);
            if (nonTwoDayTicket) return PinRequestResult.NoHubAccessForTicket();

            // §444 (operator 2026-07-27) — the SECOND named exception, same shape and same
            // reasoning as §361 above. The address may belong to a participant who exists but
            // cannot sign in: deactivated (IsActive = false) or not yet activated out of the
            // onboarding queue (LifecycleState != Active). The lookup at the top requires BOTH,
            // so such a person silently falls to the neutral reply, sees the 6-digit code box,
            // and waits for a mail that is never generated.
            //
            // Deliberately checked ONLY after the participant lookup missed, and matched on the
            // same primary-or-alternate address rule, so this can never change the answer for an
            // address we do not hold — a stranger probing the endpoint still gets Ok().
            var inactiveAccount = await _db.Participants
                .AnyAsync(
                    p => p.EventId == eventId
                         && (p.Email == normalizedEmail || p.AlternateEmail == normalizedEmail),
                    cancellationToken);
            if (inactiveAccount) return PinRequestResult.AccountNotActive();

            return PinRequestResult.Ok();
        }

        // Rate limit: count PINs issued to this participant in the window. The cap
        // still holds (no PIN is generated or emailed past it), but the RESPONSE is
        // the same neutral Ok as for an unknown email (§234 4c): a distinguishable
        // "too many requests" error fired only for REAL accounts, so 6 rapid requests
        // turned this endpoint into an account-enumeration oracle. Now known and
        // unknown emails observably behave identically at any request rate.
        var windowStart = now - RateWindow;
        var recentCount = participant.LoginPins
            .Count(pin => pin.CreatedAt >= windowStart);
        if (recentCount >= MaxRequestsPerWindow)
        {
            return PinRequestResult.Ok();
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

        // Email the plaintext PIN. It is never logged or persisted. The edition
        // tag is appended centrally as a POSTFIX by EmailTemplateRenderer
        // (REQUIREMENTS §103) — the subject here is content-only, no edition prefix.
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
                var rendered = _templates.Render("pin-signin", tokens);
                // Send to the address the user typed (primary OR alternate) so they
                // receive the PIN at the inbox they signed in with (§26d).
                await _emailSender.SendAsync(
                    normalizedEmail, rendered.Subject, rendered.HtmlBody, cancellationToken);
            }
            else
            {
                // Fallback: legacy inline HTML (older test constructions with no provider).
                var subject = $"Your sign-in code {EmailTemplateRenderer.SubjectTag}";
                var body = BuildPinEmail(participant.FullName, plainPin);
                await _emailSender.SendAsync(
                    normalizedEmail, subject, body, cancellationToken);
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
