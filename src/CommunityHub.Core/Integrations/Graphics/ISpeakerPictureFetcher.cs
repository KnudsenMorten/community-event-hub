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
}
