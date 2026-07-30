using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Email;

/// <summary>
/// Notifies an attendee who has just been PROMOTED from a Master Class waitlist to
/// a confirmed seat (REQUIREMENTS §6). The send goes through the standard
/// <see cref="IEmailSender"/> path, so it is **ring-gated** (early rings keep it to
/// ring 0/1; out-of-ring recipients are RING-DROP logged) and **EmailLog-recorded**
/// — so we always know exactly who got mail. Idempotent: only an un-notified
/// confirmed signup is sent, and <see cref="MasterClassSignup.PromotionNotifiedAt"/>
/// is stamped after.
/// </summary>
public sealed class MasterClassPromotionEmailService
{
    private const string Category = "masterclass-promotion";

    private const string DefaultSupportEmail = "info@expertslive.dk";

    private readonly CommunityHubDbContext _db;
    private readonly IEmailSender _sender;
    private readonly IEmailContextAccessor _context;
    private readonly MasterClassSignupService _signups;

    // Optional template provider for the offer/promoted mails (the generic shipped
    // default is the fallback). Null in older test constructions → inline HTML.
    private readonly EmailTemplateProvider? _templates;

    // Optional edition-config source for the support email shown in the contact
    // line. Null in older test constructions → falls back to the default.
    private readonly EventEditionConfigLoader? _eventConfigLoader;
    private readonly EventConfigOptions? _eventConfigOptions;
    private string? _supportEmail;

    public MasterClassPromotionEmailService(
        CommunityHubDbContext db, IEmailSender sender,
        IEmailContextAccessor context, MasterClassSignupService signups,
        EmailTemplateProvider? templates = null,
        EventEditionConfigLoader? eventConfigLoader = null,
        EventConfigOptions? eventConfigOptions = null)
    {
        _db = db;
        _sender = sender;
        _context = context;
        _signups = signups;
        _templates = templates;
        _eventConfigLoader = eventConfigLoader;
        _eventConfigOptions = eventConfigOptions;
    }

    /// <summary>The "Questions? Email …" contact line; support email sourced from edition config.</summary>
    private string ContactLine()
    {
        _supportEmail ??= MasterClassEmailService.ResolveSupportEmail(
            _eventConfigLoader, _eventConfigOptions);
        return $"<p>Questions? Email {System.Net.WebUtility.HtmlEncode(_supportEmail)}.</p>";
    }

    /// <summary>
    /// Send the "you got a seat" email for a just-promoted signup. <paramref name="baseUrl"/>
    /// is the scheme+host for the self-service link (e.g. "https://host"). Returns
    /// false when the signup is unknown, not a confirmed seat, or already notified.
    /// </summary>
    /// <param name="releasedTitle">
    /// §386 — the class whose seat was auto-released to take this one, when the attendee held a seat
    /// elsewhere and consented to the swap. Null when nothing was given up. Without it the mail said
    /// only "you're in X" while a seat quietly vanished, so the attendee found out by noticing.
    /// </param>
    public async Task<bool> SendPromotionAsync(
        int signupId, string baseUrl, CancellationToken ct = default, string? releasedTitle = null)
    {
        var s = await _db.MasterClassSignups
            .Include(x => x.Attendee)
            .Include(x => x.Session)
            .Include(x => x.Event)
            .FirstOrDefaultAsync(x => x.Id == signupId, ct);
        // Notify a freshly CONFIRMED seat or a held OFFER (decision needed).
        if (s is null
            || s.Status is not (MasterClassSignupStatus.Confirmed or MasterClassSignupStatus.Offered)
            || s.PromotionNotifiedAt is not null)
            return false;
        if (string.IsNullOrWhiteSpace(s.Attendee.Email)) return false;

        var token = await _signups.EnsureSelfServiceTokenAsync(s.AttendeeId, ct);
        var url = $"{baseUrl.TrimEnd('/')}/MyMasterClass?t={token}";

        var firstName = string.IsNullOrWhiteSpace(s.Attendee.FirstName) ? "there" : s.Attendee.FirstName;
        var isOffer = s.Status == MasterClassSignupStatus.Offered;
        // §707.2b — the MAIL IDENTITY. This service sends TWO different mails from one method, so the
        // key is derived from the same branch the renderer uses (below) rather than restated, and it is
        // passed as EmailContext.TemplateName by both the templated and the legacy-fallback send.
        // Without it both resolved by the welcome-email FEATURE ring and neither could be tuned per role.
        var mailKey = isOffer ? "masterclass-offer" : "masterclass-promoted";
        var when = s.OfferExpiresAt is { } exp ? $" by {exp:dddd HH:mm} UTC" : "";
        var name = $"{s.Attendee.FirstName} {s.Attendee.LastName}".Trim();

        // §169 + §252 F6: the recipient's provisioned login Participant id — magic-link
        // CTA + participant-keyed ring gate (never fail-closed dropped as "unknown").
        // §252 F5: the promotion rides the SAME welcome-email ring as the rest of the
        // Master Class funnel (one ring, one raise at go-live).
        var pid = await MasterClassEmailService.ResolveAttendeeParticipantIdAsync(
            _db, s.Attendee.Email, s.EventId, ct);

        string subject, htmlBody;
        if (_templates is not null)
        {
            // The service already branches on status: render the matching key.
            var tokens = _templates.NewTokenSet(pid);
            tokens["firstName"] = firstName;
            tokens["masterClassTitle"] = s.Session.Title;
            tokens["selfServiceUrl"] = url;
            tokens["offerDeadline"] = when;   // offer variant only; empty otherwise
            // §386: a whole SENTENCE, not just the title, so the template needs no conditional —
            // empty when nothing was released, which renders as nothing.
            //
            // §386b: the token MUST end in "Block" (or "Html"). The renderer HTML-ENCODES token
            // values at the seam, so a token named `releasedNote` had its <p> and <strong> printed
            // as literal tags in the operator's inbox. That suffix IS the documented opt-out for
            // sender-built fragments — the same convention `waitlistTerms`/`heldMasterClass` use.
            // The TITLE is still encoded by hand, because it is user/event free text.
            tokens["releasedNoteBlock"] = string.IsNullOrWhiteSpace(releasedTitle)
                ? string.Empty
                : "<p style=\"margin:0 0 16px;\">Your previous seat in <strong>"
                  + System.Net.WebUtility.HtmlEncode(releasedTitle)
                  + "</strong> has been released &mdash; that is what you agreed to when you joined "
                  + "the wait list.</p>";
            using (_context.Set(new EmailContext(
                Category, s.EventId, pid, name,
                TemplateName: mailKey, FeatureKey: "welcome-email")))
            {
                var rendered = _templates.Render(mailKey, tokens);
                await _sender.SendAsync(s.Attendee.Email, rendered.Subject, rendered.HtmlBody, ct);
            }
            await _signups.MarkPromotionNotifiedAsync(s.Id, ct);
            return true;
        }

        // Fallback: legacy inline HTML (older test constructions with no provider).
        var encName = System.Net.WebUtility.HtmlEncode(firstName);
        var encTitle = System.Net.WebUtility.HtmlEncode(s.Session.Title);
        var encUrl = System.Net.WebUtility.HtmlEncode(url);
        if (isOffer)
        {
            subject = $"You've been moved into {s.Session.Title}";
            htmlBody =
                $"<p>Hi {encName},</p>" +
                $"<p>A seat opened up and <strong>you've been moved into the Master Class {encTitle}</strong>.</p>" +
                "<p><strong>Your previous Master Class seat has been released.</strong> You don't need to do anything — " +
                $"this is just to let you know you now have one confirmed Master Class. You can review it here: " +
                $"<a href=\"{encUrl}\">Review my Master Class</a>.</p>" +
                ContactLine();
        }
        else
        {
            subject = $"You've been moved into {s.Session.Title}";
            htmlBody =
                $"<p>Hi {encName},</p>" +
                $"<p>A seat opened up and <strong>you've been moved into the Master Class {encTitle}</strong>.</p>" +
                "<p><strong>Your previous Master Class seat has been released.</strong> You don't need to do anything. " +
                $"You can review or manage it here: <a href=\"{encUrl}\">Review my Master Class</a>.</p>" +
                ContactLine();
        }

        using (_context.Set(new EmailContext(
            Category, s.EventId, pid, name,
            TemplateName: mailKey, FeatureKey: "welcome-email")))
        {
            await _sender.SendAsync(s.Attendee.Email, subject, htmlBody, ct);
        }

        await _signups.MarkPromotionNotifiedAsync(s.Id, ct);
        return true;
    }
}
