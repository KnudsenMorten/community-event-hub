using System.Security.Cryptography;
using System.Text;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Email;

/// <summary>
/// §1080 stage 5 — BOUNCES AND COMPLAINTS, learned from the provider's webhook.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-12: <i>"ability to detect mails that bounch to learn to exclude for future
/// mails"</i>. His decision was to build the webhook rather than infer bounces from send errors —
/// and the difference is real: SMTP accepts a message and rejects it minutes later, so a send that
/// "succeeded" is exactly where a dead address hides.</para>
///
/// <para>🔴 <b>Repeated sending to dead addresses is what gets an IP listed.</b> That makes this the
/// feature that protects every OTHER mail the hub sends, not just campaigns.</para>
///
/// <para>🔒 <b>A HARD bounce suppresses; a SOFT one does not.</b> A full mailbox or an out-of-office
/// greylist is temporary, and suppressing on it would quietly lose a real attendee for the rest of
/// the event.</para>
/// </remarks>
public sealed class MailBounceIngestService
{
    private readonly CommunityHubDbContext _db;
    private readonly MailSuppressionService _suppression;
    private readonly string _secret;

    /// <param name="secret">
    /// 🔒 The shared secret the provider must present. Blank ⇒ <see cref="IsConfigured"/> is false
    /// and the endpoint refuses everything: an open webhook lets anybody suppress any address.
    /// </param>
    public MailBounceIngestService(
        CommunityHubDbContext db, MailSuppressionService suppression, string? secret = null)
    {
        _db = db;
        _suppression = suppression;
        _secret = (secret ?? string.Empty).Trim();
    }

    public bool IsConfigured => _secret.Length > 0;

    /// <summary>
    /// Constant-time comparison of the presented secret.
    /// </summary>
    /// <remarks>
    /// ⚠️ Not <c>==</c>: a comparison that returns on the first wrong character can be measured, and
    /// this one guards the ability to silence any address we hold.
    /// </remarks>
    public bool VerifySecret(string? presented)
    {
        if (!IsConfigured || string.IsNullOrEmpty(presented)) return false;

        var a = Encoding.UTF8.GetBytes(_secret);
        var b = Encoding.UTF8.GetBytes(presented);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>The provider's event names we act on, and what they mean to us.</summary>
    public static MailSuppressionReason? ReasonFor(string? providerEvent) =>
        (providerEvent ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            // Brevo's vocabulary for "this address is gone".
            "hard_bounce" or "hardbounce" or "invalid_email" or "blocked" or "unsubscribed"
                => MailSuppressionReason.HardBounce,
            "spam" or "complaint" => MailSuppressionReason.Complaint,
            // 🔒 Everything else — soft_bounce, deferred, opened, delivered — is deliberately NOT a
            // suppression. A full mailbox today is a real attendee tomorrow.
            _ => null,
        };

    /// <summary>
    /// Record one webhook event. Returns true when it resulted in a suppression.
    /// </summary>
    public async Task<bool> IngestAsync(
        string? providerEvent, string? email, string? detail, CancellationToken ct = default)
    {
        var reason = ReasonFor(providerEvent);
        if (reason is null) return false;

        var eventId = await _db.Events
            .Where(e => e.IsActive)
            .Select(e => (int?)e.Id)
            .FirstOrDefaultAsync(ct);
        if (eventId is null) return false;

        return await _suppression.SuppressAsync(
            eventId.Value, email, reason.Value,
            $"{providerEvent}: {detail}".Trim().TrimEnd(':'), ct);
    }
}
