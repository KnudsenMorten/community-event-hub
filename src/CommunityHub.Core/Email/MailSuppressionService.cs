using System.Security.Cryptography;
using System.Text;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Email;

/// <summary>
/// §1080 stage 2 — WHO MUST NOT BE WRITTEN TO, and the link that lets a person say so.
/// </summary>
/// <remarks>
/// <para>🔴 <b>This exists before the first campaign can send, deliberately.</b> The operator's
/// legal basis for the imported list is the existing-customer relationship (his decision,
/// 2026-08-12) — and that basis REQUIRES a working unsubscribe in every mail. An unsubscribe link
/// that arrives in the second campaign is the one that gets a sending IP listed.</para>
///
/// <para>🔒 <b>The check happens at SEND time, never at audience time.</b> A campaign scheduled on
/// Monday must respect an unsubscribe that arrives on Tuesday; resolving a recipient list once and
/// mailing it later is exactly how somebody who opted out receives the next mailing anyway.</para>
/// </remarks>
public sealed class MailSuppressionService
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;
    private readonly string _secret;

    /// <param name="secret">
    /// 🔴 The key the unsubscribe links are signed with. <b>Blank ⇒ no links can be made, and
    /// campaigns must refuse to send</b> — see <see cref="CanMakeLinks"/>. Failing closed is the
    /// only safe answer: a mass mailing with a dead unsubscribe link is the one that costs the
    /// sending reputation and the legal basis at the same time.
    /// </param>
    public MailSuppressionService(
        CommunityHubDbContext db, string? secret = null, TimeProvider? clock = null)
    {
        _db = db;
        _secret = (secret ?? string.Empty).Trim();
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>True when unsubscribe links can be signed — i.e. a secret is configured.</summary>
    public bool CanMakeLinks => _secret.Length > 0;

    // ---------------------------------------------------------------- the list

    /// <summary>Is this address suppressed for this edition?</summary>
    public async Task<bool> IsSuppressedAsync(
        int eventId, string? email, CancellationToken ct = default)
    {
        var key = MailSuppression.Normalise(email);
        if (key.Length == 0) return true;   // ⚠️ no address ⇒ nothing to send to; treat as suppressed

        return await _db.MailSuppressions
            .AnyAsync(s => s.EventId == eventId && s.Email == key, ct);
    }

    /// <summary>
    /// The suppressed set for an edition, as one query.
    /// </summary>
    /// <remarks>
    /// 🔑 A batch send asks ONCE and filters in memory rather than asking per recipient: fifty
    /// round-trips per batch is how a send job becomes the slowest thing in the system.
    /// </remarks>
    public async Task<HashSet<string>> SuppressedSetAsync(
        int eventId, CancellationToken ct = default) =>
        (await _db.MailSuppressions
            .Where(s => s.EventId == eventId)
            .Select(s => s.Email)
            .ToListAsync(ct))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Suppress an address. Idempotent — a second unsubscribe is not an error, and the FIRST reason
    /// is kept because "they asked to stop" is a stronger fact than a later bounce.
    /// </summary>
    public async Task<bool> SuppressAsync(
        int eventId, string? email, MailSuppressionReason reason, string? detail = null,
        CancellationToken ct = default)
    {
        var key = MailSuppression.Normalise(email);
        if (key.Length == 0 || !key.Contains('@')) return false;

        var existing = await _db.MailSuppressions
            .FirstOrDefaultAsync(s => s.EventId == eventId && s.Email == key, ct);
        if (existing is not null) return false;

        _db.MailSuppressions.Add(new MailSuppression
        {
            EventId = eventId, Email = key, Reason = reason, Detail = detail,
            CreatedAt = _clock.GetUtcNow(),
        });
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Remove a suppression — an organizer un-ticking the box, or a bounce that turned out to be a
    /// full mailbox rather than a dead address.
    /// </summary>
    /// <remarks>
    /// ⚠️ Deliberately available for every reason including <c>Unsubscribed</c>: people do ask to be
    /// put back on, and refusing would mean the only way back is a database edit. The organizer page
    /// says plainly what it is undoing.
    /// </remarks>
    public async Task<bool> UnsuppressAsync(
        int eventId, string? email, CancellationToken ct = default)
    {
        var key = MailSuppression.Normalise(email);
        var row = await _db.MailSuppressions
            .FirstOrDefaultAsync(s => s.EventId == eventId && s.Email == key, ct);
        if (row is null) return false;

        _db.MailSuppressions.Remove(row);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    // ------------------------------------------------------------- the link

    /// <summary>
    /// The signature that proves a link was issued by us for THIS address and THIS edition.
    /// </summary>
    /// <remarks>
    /// <para>🔑 <b>Signed, not stored.</b> An HMAC over "eventId:email" needs no table, no cleanup
    /// and no lookup — and it cannot be enumerated: changing the address in the URL invalidates the
    /// signature, so nobody can unsubscribe somebody else by guessing.</para>
    ///
    /// <para>⚠️ URL-safe base64, truncated to 32 characters: still 192 bits of tag, and short enough
    /// that the link does not wrap in a mail client and break on the fold.</para>
    /// </remarks>
    public string TokenFor(int eventId, string? email)
    {
        if (!CanMakeLinks) return string.Empty;

        var payload = $"{eventId}:{MailSuppression.Normalise(email)}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_secret));
        var tag = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToBase64String(tag)
            .Replace("+", "-").Replace("/", "_").TrimEnd('=')[..32];
    }

    /// <summary>
    /// Verify a link. 🔒 Constant-time comparison — a token check that returns early on the first
    /// wrong character is a token check that can be measured.
    /// </summary>
    public bool VerifyToken(int eventId, string? email, string? token)
    {
        if (!CanMakeLinks || string.IsNullOrWhiteSpace(token)) return false;

        var expected = TokenFor(eventId, email);
        if (expected.Length == 0 || expected.Length != token.Length) return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(token));
    }

    /// <summary>The full unsubscribe URL for one recipient, or empty when links cannot be signed.</summary>
    public string LinkFor(string hubUrl, int eventId, string? email)
    {
        var token = TokenFor(eventId, email);
        if (token.Length == 0) return string.Empty;

        var root = (hubUrl ?? string.Empty).TrimEnd('/');
        var address = Uri.EscapeDataString(MailSuppression.Normalise(email));
        return $"{root}/unsubscribe?e={address}&t={token}";
    }

    /// <summary>
    /// The footer every campaign mail carries.
    /// </summary>
    /// <remarks>
    /// 🔴 It says WHY they are receiving it (the existing-customer relationship is the basis, and a
    /// basis nobody is told about is a basis that reads as spam) and gives the one-click way out.
    /// </remarks>
    public string FooterHtml(string hubUrl, int eventId, string? email, string communityName)
    {
        var link = LinkFor(hubUrl, eventId, email);
        if (link.Length == 0) return string.Empty;

        return "<hr style=\"border:0;border-top:1px solid #e5e7eb;margin:24px 0 12px;\" />"
             + "<p style=\"color:#6b7280;font-size:12px;line-height:1.5;\">"
             + $"You are receiving this because you have taken part in a {System.Net.WebUtility.HtmlEncode(communityName)} "
             + "event before. "
             + $"<a href=\"{System.Net.WebUtility.HtmlEncode(link)}\">Unsubscribe from event mails</a>"
             + "</p>";
    }
}
