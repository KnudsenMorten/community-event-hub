using System.Text;
using QRCoder;

namespace CommunityHub.Core.Evaluation;

/// <summary>
/// §748 C5 — generates the per-session QR code. **One code per session** (operator 2026-07-31:
/// *"1 per session"*, *"qr must be gerated by you"*).
/// </summary>
/// <remarks>
/// <para>🔒 <b>The image is produced HERE, never fetched from a QR service.</b> A third-party
/// renderer would mean a session's feedback token leaving our infrastructure to be drawn, an
/// external dependency that has to stay alive at print time, and a URL in printed material we do not
/// control. None of those is recoverable once 40 signs are on walls.</para>
///
/// <para>🔒 <b><see cref="PngByteQRCode"/>, NOT <c>QRCode</c>.</b> QRCoder's `QRCode` renderer draws
/// through <c>System.Drawing.Common</c>, which is <b>Windows-only from .NET 7 onward</b> — it would
/// work on this host today and throw the moment anything runs on Linux, which is the worst kind of
/// latent break. `PngByteQRCode` writes PNG bytes in pure managed code with no imaging backend at
/// all. The SVG path is likewise pure string output.</para>
///
/// <para>🔑 <b>What the code encodes is the SHORT route</b> — <c>/f/{token}</c>. Every character
/// encoded raises the module density of a symbol that has to scan from a seat at the back of a room,
/// and the brief exempts this one route from the <c>/evaluation/v1</c> convention for exactly that
/// reason. Do not "tidy" it into the namespace.</para>
/// </remarks>
public sealed class SessionQrCodeService
{
    /// <summary>
    /// Error-correction level. 🔑 <b>Q (~25% recoverable), deliberately, not L.</b> These codes are
    /// printed and live on a wall or a lectern for a day: they get creased, smudged, partly covered
    /// by a hand, and photographed at an angle in poor light. L keeps the symbol sparse but has
    /// almost no tolerance for damage; Q costs a modest density increase and is what makes a scuffed
    /// sign still scan. H would be tougher still, but the density cost starts to fight the
    /// scan-from-the-back-row requirement.
    /// </summary>
    public const QRCodeGenerator.ECCLevel ErrorCorrection = QRCodeGenerator.ECCLevel.Q;

    /// <summary>
    /// Pixels per QR module in the PNG. 20 gives a symbol that stays crisp when scaled onto an A5
    /// sign; the PNG is lossless, so this is about giving the printer enough pixels, not file size.
    /// </summary>
    public const int PixelsPerModule = 20;

    /// <summary>
    /// The URL a scan opens. Kept as a method rather than inlined so the page, the download and the
    /// tests cannot disagree about what was encoded.
    /// </summary>
    /// <remarks>
    /// 🔒 The base URL must be the PUBLIC host, not an internal one — this string is printed and
    /// cannot be corrected afterwards.
    /// </remarks>
    public static string FeedbackUrl(string baseUrl, string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return $"{baseUrl.TrimEnd('/')}/f/{token}";
    }

    /// <summary>
    /// PNG bytes for one session's code. 🔑 <b>PNG is the only format</b> (operator 2026-07-31:
    /// *"png is enough"*). It is lossless, so it survives being dropped into a document and scaled
    /// by whoever lays out the sign — which is the one thing that must not soften the module edges a
    /// scanner reads.
    /// </summary>
    public byte[] RenderPng(string baseUrl, string token, int pixelsPerModule = PixelsPerModule)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(
            FeedbackUrl(baseUrl, token), ErrorCorrection);
        return new PngByteQRCode(data).GetGraphic(pixelsPerModule);
    }

    /// <summary>
    /// A filename a human can match to a session on a table full of printouts.
    /// </summary>
    /// <remarks>
    /// 🔑 The session TITLE is in the name on purpose. A folder of <c>qr-17.png</c> is unusable at
    /// print time — somebody has to open each one and cross-reference an id — and that is precisely
    /// when a code gets taped to the wrong door.
    /// </remarks>
    public static string FileName(int sessionId, string? title, string extension)
    {
        var safe = new StringBuilder();
        foreach (var c in (title ?? string.Empty).Trim())
        {
            if (char.IsLetterOrDigit(c)) safe.Append(char.ToLowerInvariant(c));
            else if (c is ' ' or '-' or '_' && safe.Length > 0 && safe[^1] != '-') safe.Append('-');
        }

        var slug = safe.ToString().Trim('-');
        if (slug.Length > 60) slug = slug[..60].TrimEnd('-');

        return slug.Length == 0
            ? $"session-{sessionId}-qr.{extension}"
            : $"session-{sessionId}-{slug}-qr.{extension}";
    }
}
