using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityHub.Core.Assistant;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace CommunityHub.Core.Integrations.Webshop;

/// <summary>One booth found on the expo layout picture, in pixels of that picture.</summary>
public sealed record DetectedBooth(string BoothId, int X1, int Y1, int X2, int Y2, bool Snapped);

/// <summary>What a detection run produced — the booths, and the ones asked for but not found.</summary>
public sealed record BoothDetection(int Width, int Height, IReadOnlyList<DetectedBooth> Booths, IReadOnlyList<string> NotFound, string? Error = null);

/// <summary>
/// §1305 item 11 — finds the booths on an expo layout picture with AI and marks them on the booth map.
/// </summary>
/// <remarks>
/// <para>Operator 2026-09-26: <i>"consider to use ai to detect the booths on the expo map and mark them. you have their
/// number fx E-17 from the product name … include ai in the mapping if needed"</i>.</para>
///
/// <para>🔑 <b>Two steps, because a vision model reads well but measures loosely.</b> (1) The model is given the picture
/// and the booth numbers from the product names and returns a rectangle per booth. (2) Each rectangle is SNAPPED to the
/// dashed outline actually drawn in the picture: every edge moves to the nearby pixel row/column with the most dark
/// pixels along it. A booth whose edges find no outline keeps the model's rectangle, marked not snapped, so the
/// organizer knows which ones to check.</para>
///
/// <para>Uses the hub's Azure OpenAI deployment (<see cref="OpenAiOptions"/>); off ⇒ a clear message, nothing saved.
/// Reads only: nothing is written anywhere but CEH's own booth spots.</para>
/// </remarks>
public sealed class ShopBoothDetector
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly OpenAiOptions _options;
    private readonly ILogger<ShopBoothDetector>? _log;

    public ShopBoothDetector(HttpClient http, OpenAiOptions options, ILogger<ShopBoothDetector>? log = null)
    {
        _http = http;
        _options = options;
        _log = log;
    }

    public bool IsConfigured => _options.IsConfigured;

    public async Task<BoothDetection> DetectAsync(byte[] picture, IReadOnlyCollection<string> boothIds, CancellationToken ct = default)
    {
        using var image = Image.Load<Rgba32>(picture);
        int w = image.Width, h = image.Height;
        if (!_options.IsConfigured)
            return new(w, h, Array.Empty<DetectedBooth>(), boothIds.ToList(), "The AI service is not configured on this host.");
        if (boothIds.Count == 0)
            return new(w, h, Array.Empty<DetectedBooth>(), Array.Empty<string>(), "No products carry a booth number yet.");

        string? answer;
        try
        {
            answer = await AskAsync(picture, w, h, boothIds, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _log?.LogWarning(ex, "§1305 booth detection: the AI call failed.");
            return new(w, h, Array.Empty<DetectedBooth>(), boothIds.ToList(), "The AI service did not answer: " + ex.Message);
        }
        if (answer is null)
            return new(w, h, Array.Empty<DetectedBooth>(), boothIds.ToList(), "The AI service returned an error.");

        var rough = ParseAnswer(answer, boothIds, w, h);
        var dark = DarkMap(image);
        var booths = rough.Select(b => Snap(b, dark, w, h)).ToList();
        var notFound = boothIds.Where(id => !booths.Any(b => string.Equals(b.BoothId, id, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
        return new(w, h, booths, notFound);
    }

    private async Task<string?> AskAsync(byte[] picture, int w, int h, IReadOnlyCollection<string> boothIds, CancellationToken ct)
    {
        var prompt =
            $"This is an expo floor plan, {w.ToString(CultureInfo.InvariantCulture)} x {h.ToString(CultureInfo.InvariantCulture)} pixels. "
            + "Each sponsor booth is a dashed rectangle with its booth number written inside near the top-left corner "
            + "(for example \"E-17\"), usually with a tier word such as Gold, Diamond, Platinum or Feature. "
            + "Find these booth numbers: " + string.Join(", ", boothIds) + ". "
            + "A number ending in A or B (for example E-20A and E-20B) is half of the rectangle labelled without the letter: "
            + "A is the left half, B the right half. "
            + "For each booth you find, give the rectangle in pixels of this picture: x1,y1 = top-left corner, x2,y2 = bottom-right. "
            + "Leave out any number you cannot see. Answer with JSON only: "
            + "{\"booths\":[{\"id\":\"E-1\",\"x1\":0,\"y1\":0,\"x2\":0,\"y2\":0}]}";

        var payload = new
        {
            messages = new object[]
            {
                new { role = "system", content = "You locate labelled rectangles on floor plans precisely and answer only with JSON." },
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = prompt },
                        new { type = "image_url", image_url = new { url = "data:image/png;base64," + Convert.ToBase64String(ToPng(picture)), detail = "high" } },
                    },
                },
            },
            temperature = 0,
            max_tokens = 4000,
            response_format = new { type = "json_object" },
        };

        var url = $"{_options.Endpoint!.TrimEnd('/')}/openai/deployments/{_options.Deployment}/chat/completions?api-version={_options.ApiVersion}";
        using var msg = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(payload) };
        // Secret header — from config / Key Vault, never logged.
        msg.Headers.TryAddWithoutValidation("api-key", _options.ApiKey);
        using var resp = await _http.SendAsync(msg, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _log?.LogWarning("§1305 booth detection: the AI service answered HTTP {Status}.", (int)resp.StatusCode);
            return null;
        }
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), default, ct);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
    }

    private static byte[] ToPng(byte[] picture)
    {
        if (picture.Length > 8 && picture[0] == 0x89 && picture[1] == 0x50) return picture;
        using var img = Image.Load(picture);
        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// The model's JSON → rectangles, kept only for booth numbers that were asked for, ordered and clamped to the
    /// picture. Tolerates a fenced answer and swapped corners.
    /// </summary>
    public static IReadOnlyList<DetectedBooth> ParseAnswer(string answer, IReadOnlyCollection<string> boothIds, int width, int height)
    {
        var text = answer.Trim();
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return Array.Empty<DetectedBooth>();
        text = text[start..(end + 1)];

        var wanted = new HashSet<string>(boothIds, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, DetectedBooth>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("booths", out var list) || list.ValueKind != JsonValueKind.Array)
                return Array.Empty<DetectedBooth>();
            foreach (var b in list.EnumerateArray())
            {
                var id = b.TryGetProperty("id", out var idEl) ? idEl.GetString()?.Trim().ToUpperInvariant() : null;
                if (id is null || !wanted.Contains(id) || result.ContainsKey(id)) continue;
                if (!Num(b, "x1", out var x1) || !Num(b, "y1", out var y1) || !Num(b, "x2", out var x2) || !Num(b, "y2", out var y2)) continue;
                var (l, r) = (Math.Clamp(Math.Min(x1, x2), 0, width - 1), Math.Clamp(Math.Max(x1, x2), 0, width - 1));
                var (t, bo) = (Math.Clamp(Math.Min(y1, y2), 0, height - 1), Math.Clamp(Math.Max(y1, y2), 0, height - 1));
                if (r - l < 4 || bo - t < 4) continue;
                result[id] = new DetectedBooth(id, l, t, r, bo, Snapped: false);
            }
        }
        catch (JsonException)
        {
            return Array.Empty<DetectedBooth>();
        }
        return result.Values.OrderBy(b => b.BoothId, StringComparer.OrdinalIgnoreCase).ToList();

        static bool Num(JsonElement e, string name, out int value)
        {
            value = 0;
            if (!e.TryGetProperty(name, out var p)) return false;
            if (p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var d)) { value = (int)Math.Round(d); return true; }
            return p.ValueKind == JsonValueKind.String && int.TryParse(p.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }
    }

    /// <summary>True per pixel where the picture is dark (lines and text).</summary>
    public static bool[,] DarkMap(Image<Rgba32> image)
    {
        var dark = new bool[image.Width, image.Height];
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var p = row[x];
                    dark[x, y] = p.A > 128 && (0.299 * p.R + 0.587 * p.G + 0.114 * p.B) < 110;
                }
            }
        });
        return dark;
    }

    /// <summary>
    /// Move each edge of a rough rectangle onto the outline drawn in the picture: the row/column within reach whose
    /// middle stretch has the most dark pixels. An edge with no line near it stays where the model put it.
    /// </summary>
    public static DetectedBooth Snap(DetectedBooth b, bool[,] dark, int width, int height)
    {
        int bw = b.X2 - b.X1, bh = b.Y2 - b.Y1;
        int reachX = Math.Max(8, bw / 4), reachY = Math.Max(8, bh / 4);
        const double MinCoverage = 0.25;       // a dashed line covers roughly half its length

        double Row(int y, int from, int to)
        {
            if (y < 0 || y >= height) return 0;
            int n = 0, total = 0;
            for (var x = Math.Max(0, from); x <= Math.Min(width - 1, to); x++) { total++; if (dark[x, y]) n++; }
            return total == 0 ? 0 : (double)n / total;
        }
        double Col(int x, int from, int to)
        {
            if (x < 0 || x >= width) return 0;
            int n = 0, total = 0;
            for (var y = Math.Max(0, from); y <= Math.Min(height - 1, to); y++) { total++; if (dark[x, y]) n++; }
            return total == 0 ? 0 : (double)n / total;
        }
        int Best(int around, int reach, Func<int, double> score, out bool found)
        {
            var best = around; var bestScore = 0.0;
            for (var d = -reach; d <= reach; d++)
            {
                var s = score(around + d);
                // prefer the nearer line on a tie
                if (s > bestScore + 1e-9 || (Math.Abs(s - bestScore) < 1e-9 && Math.Abs(d) < Math.Abs(best - around))) { bestScore = s; best = around + d; }
            }
            found = bestScore >= MinCoverage;
            return found ? best : around;
        }

        int innerX1 = b.X1 + bw / 5, innerX2 = b.X2 - bw / 5, innerY1 = b.Y1 + bh / 5, innerY2 = b.Y2 - bh / 5;
        var top = Best(b.Y1, reachY, y => Row(y, innerX1, innerX2), out var fTop);
        var bottom = Best(b.Y2, reachY, y => Row(y, innerX1, innerX2), out var fBottom);
        var left = Best(b.X1, reachX, x => Col(x, innerY1, innerY2), out var fLeft);
        var right = Best(b.X2, reachX, x => Col(x, innerY1, innerY2), out var fRight);
        if (right - left < 4 || bottom - top < 4) return b;
        return new DetectedBooth(b.BoothId, left, top, right, bottom, Snapped: fTop && fBottom && fLeft && fRight);
    }
}
