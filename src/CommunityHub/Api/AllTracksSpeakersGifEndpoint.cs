using CommunityHub.Core.Integrations.Graphics;
using Microsoft.Extensions.Caching.Memory;

namespace CommunityHub.Api;

/// <summary>
/// §1384 — <c>GET /graphics/track-speakers.gif</c>: the PUBLIC, stable URL of the all-tracks overview
/// GIF (one frame per track, every speaker in it), for mails, WordPress posts and SoMe.
/// </summary>
/// <remarks>
/// <para>🔒 <b>No rendering in the request path.</b> The §767 sweep renders and stores the GIF whenever
/// the line-up changes; this streams the CURRENT stored file (released, active edition). The URL
/// never changes, so every mail and post that embeds it shows the line-up as it is today.</para>
///
/// <para><c>?w=600</c> serves the mail width (the stored 1200 px file scaled down); any other value
/// is the native 1200. The bytes are cached in memory per stored version for 10 minutes, and the
/// response tells clients to cache for an hour — new speakers show up within about an hour.</para>
///
/// <para>ANONYMOUS on purpose: a mail client or a WordPress page has no session, and every face on
/// it is already on the public programme. Un-releasing the asset on /Organizer/Graphics takes it
/// off this URL.</para>
/// </remarks>
public static class AllTracksSpeakersGifEndpoint
{
    public const string Route = "/graphics/track-speakers.gif";
    public const string CacheControl = "public, max-age=3600";

    /// <summary>The width served for a requested <c>w</c>: 600 or 1200, nothing else.</summary>
    public static int WidthFor(int? w) =>
        w is { } v && SoMeGraphicRenderer.OverviewWidths.Contains(v) ? v : SoMeGraphicRenderer.Width;

    public static async Task<IResult> HandleAsync(
        int? w, GraphicsService graphics, IMemoryCache cache, HttpContext http, CancellationToken ct)
    {
        var asset = await graphics.FindPublicAllTracksSpeakersAsync(ct);
        if (asset is null) return Results.NotFound();

        var width = WidthFor(w);
        // Keyed on the stored VERSION: a re-render writes a new item or a new UpdatedAt, so a rebuilt
        // overview is served on the next request instead of after the cache expires.
        var key = $"all-tracks-gif:{asset.Id}:{asset.StorageItemId}:{asset.UpdatedAt?.UtcTicks}:{width}";
        if (!cache.TryGetValue(key, out byte[]? bytes) || bytes is null)
        {
            bytes = await graphics.DownloadAllTracksSpeakersAsync(asset, width, ct);
            if (bytes is null) return Results.NotFound();
            cache.Set(key, bytes, TimeSpan.FromMinutes(10));
        }

        http.Response.Headers["Cache-Control"] = CacheControl;
        return Results.File(bytes, "image/gif");
    }
}
