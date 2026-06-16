using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace CommunityHub.Core.Auth;

/// <summary>
/// The shipped <see cref="IMagicLinkTokenFactory"/>: one-tap sign-in via a
/// signed URL. The token is an ASP.NET DataProtection payload containing
/// { ParticipantId, ExpiresAtUtc, Nonce } — tamper-evident, no secret in the
/// URL, and the key is managed by ASP.NET (rotation-aware).
///
/// Tokens are NOT single-use: anyone with the URL can sign in for the
/// participant within the expiry window. The intent is bulk-mail distribution
/// where reshare exposure is acceptable; if a link leaks, the participant
/// simply uses their email + PIN instead and the organizer regenerates by
/// re-sending. This is the same token contract the invitation flow already
/// relies on (moved into Core so the auto-login welcome email can reuse it).
/// </summary>
public sealed class MagicLinkTokenFactory : IMagicLinkTokenFactory
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromDays(14);
    private const string Purpose = "CommunityHub.MagicLink.v1";

    private readonly IDataProtector _protector;

    public MagicLinkTokenFactory(IDataProtectionProvider dataProtectionProvider)
    {
        _protector = dataProtectionProvider.CreateProtector(Purpose);
    }

    /// <inheritdoc />
    public string CreateToken(int participantId, TimeSpan? ttl = null)
    {
        var payload = new Payload(
            ParticipantId: participantId,
            ExpiresAtUtcTicks: DateTime.UtcNow.Add(ttl ?? DefaultTtl).Ticks,
            Nonce: RandomNumberGenerator.GetHexString(16));
        var json = JsonSerializer.Serialize(payload);
        var protectedBytes = _protector.Protect(Encoding.UTF8.GetBytes(json));
        return Base64Url(protectedBytes);
    }

    /// <inheritdoc />
    public int? ValidateToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        try
        {
            var bytes = FromBase64Url(token);
            var json = Encoding.UTF8.GetString(_protector.Unprotect(bytes));
            var payload = JsonSerializer.Deserialize<Payload>(json);
            if (payload is null) return null;
            if (DateTime.UtcNow.Ticks > payload.ExpiresAtUtcTicks) return null;
            return payload.ParticipantId;
        }
        catch
        {
            return null;
        }
    }

    private sealed record Payload(int ParticipantId, long ExpiresAtUtcTicks, string Nonce);

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }
}
