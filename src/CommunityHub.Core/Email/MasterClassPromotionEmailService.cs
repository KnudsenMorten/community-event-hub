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
    public async Task<bool> SendPromotionAsync(int signupId, string baseUrl, CancellationToken ct = default)
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
        var when = s.OfferExpiresAt is { } exp ? $" by {exp:dddd HH:mm} UTC" : "";
        var name = $"{s.Attendee.FirstName} {s.Attendee.LastName}".Trim();

        string subject, htmlBody;
        if (_templates is not null)
        {
            // The service already branches on status: render the matching key.
            // §169: the participant id makes the generic {{hubUrl}} CTA the recipient's
            // personal auto-login magic-link (the selfServiceUrl deep-link is unchanged).
            var pid = await MasterClassEmailService.ResolveAttendeeParticipantIdAsync(
                _db, s.Attendee.Email, s.EventId, ct);
            var tokens = _templates.NewTokenSet(pid);
            tokens["firstName"] = firstName;
            tokens["masterClassTitle"] = s.Session.Title;
            tokens["selfServiceUrl"] = url;
            tokens["offerDeadline"] = when;   // offer variant only; empty otherwise
            using (_context.Set(new EmailContext(Category, s.EventId, null, name, FeatureKey: "masterclass-invites")))
            {
                var rendered = _templates.Render(
                    isOffer ? "masterclass-offer" : "masterclass-promoted", tokens);
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

        using (_context.Set(new EmailContext(Category, s.EventId, null, name, FeatureKey: "masterclass-invites")))
        {
            await _sender.SendAsync(s.Attendee.Email, subject, htmlBody, ct);
        }

        await _signups.MarkPromotionNotifiedAsync(s.Id, ct);
        return true;
    }
}
