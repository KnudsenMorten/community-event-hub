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

    private readonly CommunityHubDbContext _db;
    private readonly IEmailSender _sender;
    private readonly IEmailContextAccessor _context;
    private readonly MasterClassSignupService _signups;

    public MasterClassPromotionEmailService(
        CommunityHubDbContext db, IEmailSender sender,
        IEmailContextAccessor context, MasterClassSignupService signups)
    {
        _db = db;
        _sender = sender;
        _context = context;
        _signups = signups;
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
        var encName = System.Net.WebUtility.HtmlEncode(firstName);
        var encTitle = System.Net.WebUtility.HtmlEncode(s.Session.Title);
        var encUrl = System.Net.WebUtility.HtmlEncode(url);

        string subject, htmlBody;
        if (s.Status == MasterClassSignupStatus.Offered)
        {
            var when = s.OfferExpiresAt is { } exp ? $" by {exp:dddd HH:mm} UTC" : "";
            subject = $"A Master Class seat is held for you: {s.Session.Title}";
            htmlBody =
                $"<p>Hi {encName},</p>" +
                $"<p>A seat has opened in <strong>{encTitle}</strong> and we're holding it for you{when}. " +
                "Since you can hold only one confirmed Master Class, taking this one means giving up your current class.</p>" +
                $"<p><a href=\"{encUrl}\">Choose here</a> — switch to the new class or keep your current one. " +
                "If you don't decide in time, we'll switch you automatically.</p>" +
                "<p>The team</p>";
        }
        else
        {
            subject = $"A seat opened up — you're in: {s.Session.Title}";
            htmlBody =
                $"<p>Hi {encName},</p>" +
                $"<p>Good news — a seat has opened up and <strong>you now have a confirmed place</strong> " +
                $"in the Master Class <strong>{encTitle}</strong>. 🎉</p>" +
                $"<p>You don't need to do anything to keep it. If your plans change, please " +
                $"<a href=\"{encUrl}\">manage or give up your place here</a> so someone on the waitlist can take it.</p>" +
                "<p>See you there,<br/>The team</p>";
        }

        using (_context.Set(new EmailContext(Category, s.EventId, null, $"{s.Attendee.FirstName} {s.Attendee.LastName}".Trim())))
        {
            await _sender.SendAsync(s.Attendee.Email, subject, htmlBody, ct);
        }

        await _signups.MarkPromotionNotifiedAsync(s.Id, ct);
        return true;
    }
}
