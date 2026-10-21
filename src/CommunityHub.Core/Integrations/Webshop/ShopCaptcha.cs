using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;

namespace CommunityHub.Core.Integrations.Webshop;

/// <summary>Captcha settings. Bound from <c>Shop:Captcha</c>.</summary>
public sealed class ShopCaptchaOptions
{
    public const string SectionName = "Shop:Captcha";

    /// <summary>Cloudflare Turnstile site key. Empty ⇒ the built-in challenge is used.</summary>
    public string TurnstileSiteKey { get; set; } = string.Empty;

    /// <summary>Cloudflare Turnstile secret (Key Vault). Empty ⇒ the built-in challenge is used.</summary>
    public string TurnstileSecret { get; set; } = string.Empty;

    public bool UsesTurnstile => !string.IsNullOrWhiteSpace(TurnstileSiteKey) && !string.IsNullOrWhiteSpace(TurnstileSecret);
}

/// <summary>A challenge to show: the question and the sealed token to post back.</summary>
public sealed record ShopCaptchaChallenge(string Question, string Token);

/// <summary>
/// §1305 — the registration captcha. Operator: <i>"secure login with pin code so we have an authenticated
/// registration, also with capcha"</i>.
/// </summary>
/// <remarks>
/// <para>CEH had no captcha anywhere (only honeypot fields). Two modes:</para>
/// <list type="bullet">
///   <item><b>Turnstile</b> (Cloudflare) when <see cref="ShopCaptchaOptions"/> carries a site key + secret.</item>
///   <item><b>Built-in</b> otherwise: a small arithmetic question whose answer and issue time are sealed with
///   ASP.NET Data Protection, so it cannot be forged, and expires after 20 minutes. Combined with the honeypot
///   field and a minimum fill time on the page, it stops scripted sign-ups without any external account.</item>
/// </list>
/// <para>The e-mail code that follows is the real proof of a human with a mailbox; the captcha only keeps bots
/// from making CEH send codes to strangers.</para>
/// </remarks>
public sealed class ShopCaptcha
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(20);

    /// <summary>A form submitted faster than this after the challenge was issued is treated as a bot.</summary>
    public static readonly TimeSpan MinimumFillTime = TimeSpan.FromSeconds(3);

    private readonly ShopCaptchaOptions _options;
    private readonly IDataProtector _protector;
    private readonly HttpClient? _http;
    private readonly TimeProvider _clock;

    public ShopCaptcha(ShopCaptchaOptions options, IDataProtectionProvider protection, TimeProvider clock, HttpClient? http = null)
    {
        _options = options;
        _protector = protection.CreateProtector("CommunityHub.Shop.Captcha.v1");
        _clock = clock;
        _http = http;
    }

    public bool UsesTurnstile => _options.UsesTurnstile;
    public string TurnstileSiteKey => _options.TurnstileSiteKey;

    /// <summary>A new built-in challenge.</summary>
    public ShopCaptchaChallenge NewChallenge()
    {
        var a = RandomNumberGenerator.GetInt32(2, 10);
        var b = RandomNumberGenerator.GetInt32(2, 10);
        var issued = _clock.GetUtcNow().ToUnixTimeSeconds();
        var token = _protector.Protect($"{a + b}|{issued}");
        return new ShopCaptchaChallenge($"What is {a} + {b}?", token);
    }

    /// <summary>Verify a built-in answer (or a Turnstile token when Turnstile is configured).</summary>
    public async Task<bool> VerifyAsync(string? token, string? answer, string? remoteIp, CancellationToken ct = default)
    {
        if (_options.UsesTurnstile) return await VerifyTurnstileAsync(answer, remoteIp, ct);
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(answer)) return false;
        try
        {
            var parts = _protector.Unprotect(token).Split('|');
            var expected = parts[0];
            var issued = DateTimeOffset.FromUnixTimeSeconds(long.Parse(parts[1], CultureInfo.InvariantCulture));
            var age = _clock.GetUtcNow() - issued;
            if (age < MinimumFillTime || age > Lifetime) return false;
            return string.Equals(answer.Trim(), expected, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private sealed record TurnstileResponse([property: JsonPropertyName("success")] bool Success);

    private async Task<bool> VerifyTurnstileAsync(string? responseToken, string? remoteIp, CancellationToken ct)
    {
        if (_http is null || string.IsNullOrWhiteSpace(responseToken)) return false;
        var form = new Dictionary<string, string>
        {
            ["secret"] = _options.TurnstileSecret,
            ["response"] = responseToken,
        };
        if (!string.IsNullOrWhiteSpace(remoteIp)) form["remoteip"] = remoteIp;
        try
        {
            using var resp = await _http.PostAsync(
                "https://challenges.cloudflare.com/turnstile/v0/siteverify", new FormUrlEncodedContent(form), ct);
            if (!resp.IsSuccessStatusCode) return false;
            var r = await resp.Content.ReadFromJsonAsync<TurnstileResponse>(cancellationToken: ct);
            return r?.Success == true;
        }
        catch
        {
            return false;
        }
    }
}
