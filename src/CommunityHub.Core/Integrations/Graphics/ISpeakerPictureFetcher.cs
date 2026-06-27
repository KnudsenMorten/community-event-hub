using System.Net;
using System.Net.Sockets;

namespace CommunityHub.Core.Integrations.Graphics;

/// <summary>One fetched image: its bytes + the content type the source served.</summary>
public sealed record FetchedImage(byte[] Content, string ContentType);

/// <summary>
/// The Sessionize picture DOWNLOAD seam (REQUIREMENTS §18 step 2). The Sessionize
/// import provides a picture URL; this fetches the image bytes DOWN so they can be
/// stored on SharePoint rather than the hub keeping only a foreign URL.
///
/// A clean seam (cf. the repo pattern) so the fetch+store flow is unit-tested with
/// a fake source — the real download hits the live Sessionize picture URLs at
/// import time. The default <see cref="HttpSpeakerPictureFetcher"/> performs a real
/// HTTP GET; tests inject a fake.
/// </summary>
public interface ISpeakerPictureFetcher
{
    /// <summary>
    /// Download the image at <paramref name="pictureUrl"/>. Returns null when the
    /// URL is blank or the fetch fails (caller tolerates a missing picture rather
    /// than crashing the import).
    /// </summary>
    Task<FetchedImage?> FetchAsync(string? pictureUrl, CancellationToken ct = default);
}

/// <summary>
/// Default HTTP fetcher: GETs the picture URL and returns the bytes + content type.
/// Tolerant — returns null on a blank URL or any HTTP/network failure so a single
/// bad picture URL never fails the whole import.
/// </summary>
public sealed class HttpSpeakerPictureFetcher : ISpeakerPictureFetcher
{
    private readonly HttpClient _http;

    public HttpSpeakerPictureFetcher(HttpClient http) => _http = http;

    public async Task<FetchedImage?> FetchAsync(string? pictureUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pictureUrl)
            || !Uri.TryCreate(pictureUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        // SSRF guard (security hardening): the picture URL is Sessionize-supplied —
        // i.e. externally controlled — so a scheme check alone is not enough. Resolve
        // the host and REJECT any address in a private/loopback/link-local range so a
        // crafted URL can't reach internal services or the cloud metadata endpoint
        // (169.254.169.254). Blind-SSRF mitigation: fail CLOSED (return null) on a
        // blocked target or an unresolvable host. (Best-effort: there is a small
        // resolve-then-connect TOCTOU window — a fuller fix would pin the socket to the
        // validated IP — but this blocks IP literals and ordinary DNS-to-private names.)
        if (!await IsAllowedTargetAsync(uri, ct))
        {
            return null;
        }

        try
        {
            using var resp = await _http.GetAsync(uri, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length == 0) return null;

            var contentType = resp.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
            return new FetchedImage(bytes, contentType);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            // Tolerate a bad/slow picture URL — the import continues without it.
            return null;
        }
    }

    /// <summary>
    /// Resolve the URL's host and return true only when EVERY resolved address is a
    /// routable public address. An IP literal is checked directly; a name is resolved
    /// via DNS. Returns false (block) on an unresolvable host, a resolve error, or any
    /// address in a blocked range.
    /// </summary>
    private static async Task<bool> IsAllowedTargetAsync(Uri uri, CancellationToken ct)
    {
        IPAddress[] addresses;
        try
        {
            if (IPAddress.TryParse(uri.Host, out var literal))
            {
                addresses = [literal];
            }
            else
            {
                addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, ct);
            }
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return false;
        }

        if (addresses.Length == 0) return false;
        foreach (var address in addresses)
        {
            if (IsBlockedAddress(address)) return false;
        }
        return true;
    }

    /// <summary>
    /// True when <paramref name="address"/> is loopback, private, link-local, unique-local,
    /// or otherwise non-routable and must be rejected to prevent SSRF. Covers
    /// 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, 127.0.0.0/8, 169.254.0.0/16, 0.0.0.0/8,
    /// ::1, ::, fc00::/7 and fe80::/10. IPv4-mapped IPv6 is normalised first so a mapped
    /// 169.254.169.254 is also caught. Unknown address families are rejected (fail closed).
    /// </summary>
    public static bool IsBlockedAddress(IPAddress address)
    {
        if (address is null) return true;

        // Normalise IPv4-mapped IPv6 (e.g. ::ffff:169.254.169.254) down to IPv4 so the
        // IPv4 range checks below catch it.
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address)) return true; // 127.0.0.0/8, ::1

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            if (b[0] == 10) return true;                          // 10.0.0.0/8
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true; // 172.16.0.0/12
            if (b[0] == 192 && b[1] == 168) return true;          // 192.168.0.0/16
            if (b[0] == 169 && b[1] == 254) return true;          // 169.254.0.0/16 (incl. metadata)
            if (b[0] == 0) return true;                           // 0.0.0.0/8 (unspecified/"this network")
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal) return true;             // fe80::/10
            if (address.IsIPv6UniqueLocal) return true;           // fc00::/7
            if (address.Equals(IPAddress.IPv6Any)) return true;   // ::
            var b = address.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return true;               // fc00::/7 (explicit backstop)
            return false;
        }

        // Unknown address family — reject to be safe.
        return true;
    }
}
