using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.DocLibrary;
using CommunityHub.Core.Integrations.Graphics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace CommunityHub.Core.Integrations.Webshop;

/// <summary>
/// §1312 — import the sponsor PowerPoint into the public sponsor catalogue, pictures included (operator 2026-09-27: "import
/// the powerpoint into the catalog including pictures").
/// </summary>
/// <remarks>
/// <para><b>What becomes what.</b> A titled slide starts a page; an untitled slide (an example photo, a second picture)
/// joins the page before it, and "Platinum Exhibitor (1/3)… (3/3)" become ONE page. A section-header / title slide names the
/// chapter of the pages after it. Text becomes paragraphs and bullet lists, tables become Markdown tables, pictures are
/// scaled to web size (≤ 1600 px) and stored in the shop's SharePoint products folder as <c>catalog-*.jpg</c>, served by
/// <c>/shop-catalog-image/{name}</c>. Charts and video files are not carried; a video slide keeps its link.</para>
/// <para><b>Safety.</b> Hidden slides are skipped, and so is every slide whose title contains one of
/// <c>catalogDeck.skipTitlesContaining</c> — the "actual reservations" slides name who bought what, and the catalogue
/// never shows company names. Every imported page is created UNPUBLISHED; a re-import replaces only the pages an earlier
/// import made (slug <c>deck-…</c>), never a page an organizer wrote.</para>
/// </remarks>
public sealed class ShopCatalogDeckImporter
{
    public const string SlugPrefix = "deck-";
    public const string PicturePrefix = "catalog-";
    private const int MaxPictureWidth = 1600;

    private readonly CommunityHubDbContext _db;
    private readonly ShopSettings _settings;
    private readonly ISharePointFileStore _store;
    private readonly IDocLibraryPathResolver _paths;
    private readonly TimeProvider _clock;
    private readonly ILogger<ShopCatalogDeckImporter>? _log;
    private readonly Func<string, CancellationToken, Task<string>> _resolveVideo;

    public ShopCatalogDeckImporter(CommunityHubDbContext db, ShopSettings settings, ISharePointFileStore store,
        IDocLibraryPathResolver paths, TimeProvider clock, ILogger<ShopCatalogDeckImporter>? log = null,
        Func<string, CancellationToken, Task<string>>? resolveVideo = null)
    {
        _db = db;
        _settings = settings;
        _store = store;
        _paths = paths;
        _clock = clock;
        _log = log;
        _resolveVideo = resolveVideo ?? ResolveVideoLinkAsync;
    }

    /// <remarks>
    /// 🔴 It MUST send a User-Agent: the deck's short-link host answers 403 to a request without one (HttpClient sends
    /// none), which left every imported video a plain link on PROD (2026-09-27, measured: no UA ⇒ 403, a UA ⇒ 301 youtu.be).
    /// </remarks>
    private static readonly HttpClient NoRedirects = CreateNoRedirectClient();

    private static HttpClient CreateNoRedirectClient()
    {
        var c = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; CommunityHub-CatalogImport/1.0)");
        return c;
    }

    /// <summary>A known player (YouTube, Vimeo) can be shown IN the page.</summary>
    public static bool IsPlayerLink(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
        && (u.Host.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase) || u.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase)
            || u.Host.EndsWith("vimeo.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The deck's video links are short names that redirect to YouTube ("highlights.eldk26.video…" → youtu.be/…): follow the
    /// redirects so the page can embed the player. Anything else — or no answer — keeps the link as it was.
    /// </summary>
    public static async Task<string> ResolveVideoLinkAsync(string url, CancellationToken ct)
    {
        var current = url;
        try
        {
            for (var hop = 0; hop < 4 && !IsPlayerLink(current); hop++)
            {
                using var res = await NoRedirects.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, ct);
                if (res.Headers.Location is not { } next) break;
                current = next.IsAbsoluteUri ? next.ToString() : new Uri(new Uri(current), next).ToString();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            return url;
        }
        return IsPlayerLink(current) ? current : url;
    }

    public sealed record DeckPicture(string MediaPath, string Token, string? Caption = null);
    public sealed class DeckPage
    {
        public required string Title { get; init; }
        public string? Chapter { get; init; }
        public StringBuilder Body { get; } = new();
        public List<DeckPicture> Pictures { get; } = new();
        public List<int> Slides { get; } = new();
        /// <summary>A video page: untitled photo slides after it start their own page.</summary>
        public bool ClosedForPhotos { get; init; }
        /// <summary>The slide's video link — shown as a player at the top of the page, not as its still picture.</summary>
        public string? VideoUrl { get; set; }
        /// <summary>A live availability page put where the deck's hand-kept "Actual Reservations" slides were.</summary>
        public ShopCatalogAvailabilityView AvailabilityView { get; init; }
        public bool ShowBoothMap { get; init; }
        /// <summary>The page's packages when not its title (config <c>productMatches</c>, or a page made from a product).</summary>
        public string? Match { get; set; }
    }

    /// <summary>
    /// The live page that replaces the deck's reservation slides, in its own chapter (operator: "many sponsors ask: what do
    /// you have remaining" · "one Availability page that includes everything"). Earlier imports made three pages (summary,
    /// available, reserved &amp; confirmed); the Available / Taken views stay selectable in the page editor.
    /// </summary>
    public const string AvailabilityChapter = "Availability & booked";
    public const string SummaryTitle = "Availability";

    private const string FirstChapter = "About the event";
    private const string PhotosTitle = "Impressions";

    /// <summary>
    /// Table columns never imported: who bought it (company names — never public) and the deck's static prices (the live shop
    /// shows the real, current price and discount).
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultSkipColumns = new[] { "Sold To", "Bought by", "Customer", "Price" };
    public sealed record DeckParse(IReadOnlyList<DeckPage> Pages, IReadOnlyList<string> Skipped);
    public sealed record ImportResult(bool Ok, string Message, IReadOnlyList<string> Lines);

    // ------------------------------------------------------------------------------------------------------------------
    // Parse (pure)
    // ------------------------------------------------------------------------------------------------------------------

    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>Read the deck into catalogue pages. Pictures are referenced by their path inside the package.</summary>
    public static DeckParse Parse(ZipArchive zip, IReadOnlyCollection<string> skipTitlesContaining,
        IReadOnlyCollection<string>? skipColumnsNamed = null, IReadOnlyCollection<string>? skipPagesTitled = null,
        IReadOnlyCollection<string>? skipChaptersTitled = null)
    {
        skipColumnsNamed ??= DefaultSkipColumns;
        var pres = Load(zip, "ppt/presentation.xml") ?? throw new InvalidDataException("Not a PowerPoint file (no presentation.xml).");
        var presRels = Rels(zip, "ppt/presentation.xml");
        var order = pres.Descendants(P + "sldId").Select(e => (string?)e.Attribute(R + "id"))
            .Where(id => id is not null && presRels.ContainsKey(id)).Select(id => Resolve("ppt/presentation.xml", presRels[id!])).ToList();

        var pages = new List<DeckPage>();
        var skipped = new List<string>();
        string? chapter = null;
        DeckPage? current = null;
        DeckPage? series = null;           // an open "X (1/3) … (3/3)" page: titled slides in between belong to it
        var availabilityAdded = false;
        var skipping = false;
        var number = 0;
        foreach (var path in order)
        {
            number++;
            var slide = Load(zip, path);
            if (slide is null) continue;
            if ((string?)slide.Root?.Attribute("show") == "0") { skipped.Add($"slide {number}: hidden"); continue; }

            var rels = Rels(zip, path);
            var title = Clean(string.Join(" ", slide.Descendants(P + "sp").Where(IsTitle).SelectMany(Paragraphs)));
            if (title.Length > 0 && skipTitlesContaining.Any(s => !string.IsNullOrWhiteSpace(s) && title.Contains(s, StringComparison.OrdinalIgnoreCase)))
            {
                skipped.Add($"slide {number}: \"{title}\" (names who bought what)");
                skipping = true;           // its untitled follow-up slides go with it
                series = null;
                if (!availabilityAdded)
                {
                    // Where the deck kept its hand-made reservation lists, the catalogue shows the LIVE ones — no names.
                    availabilityAdded = true;
                    const string ch = AvailabilityChapter;
                    // ONE page with everything (operator 2026-09-28: "so one Availability page that includes everything"): the
                    // booth map, then every package with its booths / slots coloured free, reserved + who, sold + who.
                    var availability = new DeckPage
                    {
                        Title = SummaryTitle, Chapter = ch, AvailabilityView = ShopCatalogAvailabilityView.Summary, ShowBoothMap = true,
                    };
                    availability.Body.Append("Every package at a glance — the price, how many are left, and who has reserved or bought each "
                                             + "booth and slot. Live: it changes the moment something is reserved or signed. Click a package to "
                                             + "see what is included.\n\n");
                    pages.Add(availability);
                }
                continue;
            }
            if (title.Length == 0 && skipping) { skipped.Add($"slide {number}: follows a skipped slide"); continue; }
            skipping = false;

            var chapterSlide = number > 1 && title.Length > 0 && IsChapterSlide(zip, path, rels);
            if (chapterSlide) { chapter = title; series = null; }

            string? link = null;
            if (title.Length > 0)
            {
                // "Video - ELDK26 Highlights (1 min) - https://…": the link leaves the title and becomes a link on the page.
                var m = Regex.Match(title, @"\s+-\s+(https?://\S+)\s*$");
                if (m.Success) { link = m.Groups[1].Value; title = title[..m.Index].Trim(); }
                var part = Regex.Match(title, @"\s*\((\d+)\s*/\s*(\d+)\)\s*$");
                var baseTitle = part.Success ? title[..part.Index].Trim() : title;
                if (!part.Success && link is null && !chapterSlide && series is not null)
                {
                    // "Platinum Booths" between "Platinum Exhibitor (1/3)" and "(2/3)": the booth photos are part of the
                    // Platinum page (operator: the pictures were missing from it), under their own heading.
                    current = series;
                    current.Body.Append("### ").Append(Escape(baseTitle)).Append("\n\n");
                }
                else
                {
                    // "Platinum Exhibitor (1/3) … (3/3)" is one page.
                    current = pages.FirstOrDefault(p => p.Chapter == (chapter ?? FirstChapter)
                                                        && string.Equals(p.Title, baseTitle, StringComparison.OrdinalIgnoreCase));
                    if (current is null)
                    {
                        current = new DeckPage { Title = baseTitle, Chapter = chapter ?? FirstChapter, ClosedForPhotos = link is not null };
                        pages.Add(current);
                    }
                    series = part.Success && int.Parse(part.Groups[1].Value) < int.Parse(part.Groups[2].Value) ? current : null;
                }
            }
            else if (current is null || current.ClosedForPhotos)
            {
                // Untitled slides after a video (the event photos) get a page of their own instead of joining the video.
                current = pages.LastOrDefault(p => p.Title == PhotosTitle && p.Chapter == (chapter ?? FirstChapter))
                          ?? new DeckPage { Title = PhotosTitle, Chapter = chapter ?? FirstChapter };
                if (!pages.Contains(current)) pages.Add(current);
            }
            current.Slides.Add(number);
            if (link is not null) current.VideoUrl ??= link;
            AppendSlide(zip, current, slide, rels, path, skipColumnsNamed);
        }

        // A page with nothing but its title (a divider, a chart-only slide) is not worth a page.
        var kept = pages.Where(p => p.Body.ToString().Trim().Length > 0 || p.Pictures.Count > 0 || p.VideoUrl is not null
                                    || p.AvailabilityView != ShopCatalogAvailabilityView.None).ToList();
        skipped.AddRange(pages.Except(kept).Select(p => $"\"{p.Title}\": no text, table or picture to import (chart / divider)"));
        // Pages the organizers do not want in the catalogue (operator 2026-09-27: "remove this page" — the cover slide).
        if (skipPagesTitled is { Count: > 0 } || skipChaptersTitled is { Count: > 0 })
        {
            static bool In(IReadOnlyCollection<string>? list, string? value) =>
                list is not null && value is not null && list.Any(t => string.Equals(t?.Trim(), value, StringComparison.OrdinalIgnoreCase));
            var unwanted = kept.Where(p => In(skipPagesTitled, p.Title) || In(skipChaptersTitled, p.Chapter)).ToList();
            kept = kept.Except(unwanted).ToList();
            skipped.AddRange(unwanted.Select(p => $"\"{p.Title}\": not wanted in the catalogue (catalogDeck.skipPagesTitled / skipChaptersTitled)"));
        }
        return new DeckParse(kept, skipped);
    }

    private static void AppendSlide(ZipArchive zip, DeckPage page, XDocument slide, IReadOnlyDictionary<string, string> rels, string slidePath,
        IReadOnlyCollection<string> skipColumnsNamed)
    {
        var tree = slide.Descendants(P + "spTree").FirstOrDefault();
        if (tree is null) return;
        var captions = Captions(tree);
        var gallery = new List<DeckPicture>();
        foreach (var el in tree.Descendants())
        {
            if (el.Name == P + "sp" && !IsTitle(el) && captions.Values.Contains(el)) continue;   // shown under its picture
            if (el.Name == P + "sp" && !IsTitle(el))
            {
                var paras = el.Descendants(A + "p").Select(p => (Text: Clean(ParagraphText(p)), Level: (int?)p.Element(A + "pPr")?.Attribute("lvl") ?? 0))
                    .Where(p => p.Text.Length > 0 && !IsPriceLine(p.Text)).ToList();
                if (paras.Count == 0) continue;
                if (paras.Count == 1) page.Body.Append(Escape(paras[0].Text)).Append("\n\n");
                else
                {
                    foreach (var (text, level) in paras) page.Body.Append(new string(' ', 2 * level)).Append("- ").Append(Escape(text)).Append('\n');
                    page.Body.Append('\n');
                }
            }
            else if (el.Name == Dgm + "relIds")
            {
                // A SmartArt diagram (the "Numbers" timeline): its text lives in the diagram's data part, not the slide.
                var dm = (string?)el.Attribute(R + "dm");
                if (dm is not null && rels.TryGetValue(dm, out var dataTarget))
                {
                    var dataPath = Resolve(slidePath, dataTarget);
                    var data = Load(zip, dataPath);
                    // A picture in a diagram (the team slide's photos) hangs on a layout point that names its node.
                    var photos = new Dictionary<string, string>(StringComparer.Ordinal);
                    if (data is not null)
                    {
                        var dataRels = Rels(zip, dataPath);
                        var nodeText = data.Descendants(Dgm + "pt").Where(p => (string?)p.Attribute("modelId") is not null)
                            .GroupBy(p => (string)p.Attribute("modelId")!).ToDictionary(g => g.Key, g => SmartArtParagraphs(g.First()).FirstOrDefault());
                        foreach (var pt in data.Descendants(Dgm + "pt"))
                        {
                            var node = (string?)pt.Element(Dgm + "prSet")?.Attribute("presAssocID");
                            var blip = (string?)pt.Descendants(A + "blip").FirstOrDefault()?.Attribute(R + "embed");
                            if (node is null || blip is null || photos.ContainsKey(node) || !dataRels.TryGetValue(blip, out var mediaTarget)) continue;
                            var token = $"{{{{picture:{page.Pictures.Count}}}}}";
                            page.Pictures.Add(new DeckPicture(Resolve(dataPath, mediaTarget), token, nodeText.GetValueOrDefault(node)));
                            photos[node] = token;
                        }
                    }
                    page.Body.Append(SmartArtMarkdown(data, photos));
                }
            }
            else if (el.Name == A + "tbl")
            {
                var rows = el.Elements(A + "tr")
                    .Select(tr => tr.Elements(A + "tc").Select(tc => Clean(string.Join(" ", tc.Descendants(A + "p").Select(ParagraphText)))
                        .Replace("|", "\\|")).ToList())
                    .Where(r => r.Any(c => c.Length > 0)).ToList();
                if (rows.Count == 0) continue;
                var drop = rows[0].Select((h, i) => (h, i))
                    .Where(x => skipColumnsNamed.Any(s => x.h.Equals(s, StringComparison.OrdinalIgnoreCase))).Select(x => x.i).ToHashSet();
                rows = rows.Select(r => r.Where((_, i) => !drop.Contains(i)).ToList()).Where(r => r.Count > 0).ToList();
                if (rows.Count == 0) continue;
                var cols = rows.Max(r => r.Count);
                string Row(List<string> r) => "| " + string.Join(" | ", Enumerable.Range(0, cols).Select(i => i < r.Count ? r[i] : "")) + " |";
                page.Body.Append(Row(rows[0])).Append('\n').Append("|").Append(string.Concat(Enumerable.Repeat(" --- |", cols))).Append('\n');
                foreach (var r in rows.Skip(1)) page.Body.Append(Row(r)).Append('\n');
                page.Body.Append('\n');
            }
            else if (el.Name == P + "pic")
            {
                // A video's still frame: the page shows the video itself.
                if (page.VideoUrl is not null && el.Descendants(A + "videoFile").Any()) continue;
                var embed = (string?)el.Descendants(A + "blip").FirstOrDefault()?.Attribute(R + "embed");
                if (embed is null || !rels.TryGetValue(embed, out var target)) continue;
                var media = Resolve(slidePath, target);
                var token = $"{{{{picture:{page.Pictures.Count}}}}}";
                var caption = captions.TryGetValue(el, out var c) ? Clean(string.Join(" ", c.Descendants(A + "p").Select(ParagraphText))) : null;
                var pic = new DeckPicture(media, token, string.IsNullOrEmpty(caption) ? null : caption);
                page.Pictures.Add(pic);
                gallery.Add(pic);
            }
        }

        // The slide's pictures side by side, each with its caption — not one full-width picture after the other.
        if (gallery.Count == 0) return;
        page.Body.Append("::: catalog-gallery\n");
        foreach (var pic in gallery)
            page.Body.Append("^^^\n").Append(pic.Token).Append('\n').Append("^^^")
                .Append(pic.Caption is null ? "" : " " + Escape(pic.Caption)).Append("\n\n");
        page.Body.Append(":::\n\n");
    }

    private static readonly XNamespace Dgm = "http://schemas.openxmlformats.org/drawingml/2006/diagram";

    /// <summary>
    /// §1312 — a SmartArt diagram as text (operator 2026-09-27: the Numbers page was "impossible to read" — only a photo of
    /// the slide on a screen; the numbers themselves were a SmartArt the import did not read). Its nodes form a tree
    /// (<c>dgm:cxn</c> parent → child, in <c>srcOrd</c>). A few columns of plain values (2026 | 2025 | 2024, each with its
    /// numbers) become a TABLE, anything deeper a nested list.
    /// </summary>
    public static string SmartArtMarkdown(XDocument? data, IReadOnlyDictionary<string, string>? photos = null)
    {
        if (data?.Root is null) return string.Empty;
        var points = data.Descendants(Dgm + "pt").Where(p => (string?)p.Attribute("modelId") is not null)
            .GroupBy(p => (string)p.Attribute("modelId")!).Select(g => g.First()).ToList();
        string TypeOf(XElement pt) => (string?)pt.Attribute("type") ?? "node";
        var paragraphs = points.ToDictionary(p => (string)p.Attribute("modelId")!, SmartArtParagraphs);
        var text = paragraphs.ToDictionary(kv => kv.Key, kv => Clean(string.Join(" ", kv.Value)));
        var nodes = points.Where(p => TypeOf(p) is "node" or "doc").ToDictionary(p => (string)p.Attribute("modelId")!, TypeOf);
        var children = data.Descendants(Dgm + "cxn")
            .Where(c => ((string?)c.Attribute("type") ?? "parOf") == "parOf")
            .Select(c => (Src: (string)c.Attribute("srcId")!, Dest: (string)c.Attribute("destId")!, Ord: (int?)c.Attribute("srcOrd") ?? 0))
            .Where(c => nodes.ContainsKey(c.Src) && nodes.ContainsKey(c.Dest))
            .GroupBy(c => c.Src).ToDictionary(g => g.Key, g => g.OrderBy(c => c.Ord).Select(c => c.Dest).ToList());
        List<string> Kids(string id) => children.TryGetValue(id, out var k) ? k : new List<string>();
        string Text(string id) => text.GetValueOrDefault(id) ?? string.Empty;

        var root = nodes.FirstOrDefault(n => n.Value == "doc").Key;
        if (root is null) return string.Empty;
        var top = Kids(root);
        if (top.Count == 0) return string.Empty;

        var sb = new StringBuilder();

        // People (the team slide): a card each — photo, the name in bold, then title, link and role on lines of their own.
        // Operator 2026-09-28: the team page "looks wrong" — each person had become one run-on bullet line.
        var people = top.All(t => Kids(t).Count == 0 && paragraphs.GetValueOrDefault(t)?.Count > 0)
                     && (top.Any(t => photos?.ContainsKey(t) == true) || top.All(t => paragraphs[t].Count >= 2));
        if (people)
        {
            sb.Append("::: catalog-gallery\n");
            foreach (var t in top)
            {
                var lines = paragraphs[t];
                sb.Append("^^^\n");
                if (photos?.TryGetValue(t, out var token) == true) sb.Append(token).Append("\n\n");
                sb.Append("**").Append(Escape(lines[0])).Append("**");
                foreach (var line in lines.Skip(1)) sb.Append("  \n").Append(Escape(line));
                sb.Append("\n^^^\n\n");
            }
            return sb.Append(":::\n\n").ToString();
        }

        var leafColumns = top.Count is >= 2 and <= 6 && top.All(t => Text(t).Length > 0 && Kids(t).Count > 0 && Kids(t).All(k => Kids(k).Count == 0));
        if (leafColumns)
        {
            var cols = top.Select(t => Kids(t).Select(Text).Where(s => s.Length > 0).ToList()).ToList();
            string Cell(string s) => Escape(s).Replace("|", "\\|");
            sb.Append("| ").Append(string.Join(" | ", top.Select(t => Cell(Text(t))))).Append(" |\n");
            sb.Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", top.Count))).Append('\n');
            for (var row = 0; row < cols.Max(c => c.Count); row++)
                sb.Append("| ").Append(string.Join(" | ", cols.Select(c => row < c.Count ? Cell(c[row]) : ""))).Append(" |\n");
            return sb.Append('\n').ToString();
        }

        void List(string id, int depth)
        {
            var t = Text(id);
            var next = depth;
            if (t.Length > 0)
            {
                sb.Append(new string(' ', 2 * depth)).Append("- ").Append(depth == 0 && Kids(id).Count > 0 ? $"**{Escape(t)}**" : Escape(t)).Append('\n');
                next = depth + 1;
            }
            foreach (var k in Kids(id)) List(k, next);
        }
        foreach (var t in top) List(t, 0);
        return sb.Length == 0 ? string.Empty : sb.Append('\n').ToString();
    }

    /// <summary>A diagram point's text, one entry per non-empty paragraph.</summary>
    private static List<string> SmartArtParagraphs(XElement pt) =>
        (pt.Element(Dgm + "t")?.Elements(A + "p") ?? Enumerable.Empty<XElement>())
            .Select(p => Clean(ParagraphText(p))).Where(s => s.Length > 0).ToList();

    private sealed record Box(long X, long Y, long W, long H)
    {
        public long CenterX => X + W / 2;
        public long CenterY => Y + H / 2;
    }

    private static Box? BoxOf(XElement shape)
    {
        var xfrm = shape.Element(P + "spPr")?.Element(A + "xfrm");
        var off = xfrm?.Element(A + "off");
        var ext = xfrm?.Element(A + "ext");
        if (off is null || ext is null) return null;
        return long.TryParse((string?)off.Attribute("x"), out var x) && long.TryParse((string?)off.Attribute("y"), out var y)
               && long.TryParse((string?)ext.Attribute("cx"), out var w) && long.TryParse((string?)ext.Attribute("cy"), out var h)
            ? new Box(x, y, w, h) : null;
    }

    /// <summary>
    /// A short text right on, above or below a picture is its caption ("Extra Exhibitor Staff Tickets" under the photo,
    /// "Example Platinum booth" on it). Each text is used once, by the nearest picture.
    /// </summary>
    private static Dictionary<XElement, XElement> Captions(XElement tree)
    {
        var pics = tree.Elements(P + "pic").Select(e => (El: e, Box: BoxOf(e))).Where(x => x.Box is not null).ToList();
        var texts = tree.Elements(P + "sp").Where(e => !IsTitle(e))
            .Select(e => (El: e, Box: BoxOf(e), Text: Clean(string.Join(" ", e.Descendants(A + "p").Select(ParagraphText)))))
            .Where(t => t.Box is not null && t.Text.Length is > 0 and <= 80 && !IsPriceLine(t.Text))
            .ToList();
        var result = new Dictionary<XElement, XElement>();
        var used = new HashSet<XElement>();
        foreach (var candidate in pics
                     .SelectMany(p => texts.Select(t => (Pic: p.El, PicBox: p.Box!, Text: t.El, TextBox: t.Box!)))
                     .Where(x => (x.TextBox.CenterX >= x.PicBox.X && x.TextBox.CenterX <= x.PicBox.X + x.PicBox.W
                                  && x.TextBox.Y >= x.PicBox.Y - x.PicBox.H * 2 / 5
                                  && x.TextBox.Y + x.TextBox.H <= x.PicBox.Y + x.PicBox.H + x.PicBox.H * 2 / 5)
                                 // …or a label beside it ("Example Platinum booth" left of the photo)
                                 || (x.TextBox.CenterY >= x.PicBox.Y && x.TextBox.CenterY <= x.PicBox.Y + x.PicBox.H
                                     && Math.Min(Math.Abs(x.PicBox.X - (x.TextBox.X + x.TextBox.W)), Math.Abs(x.TextBox.X - (x.PicBox.X + x.PicBox.W)))
                                        <= x.PicBox.W / 4))
                     .OrderBy(x => Math.Abs(x.TextBox.CenterY - x.PicBox.CenterY)))
        {
            if (result.ContainsKey(candidate.Pic) || !used.Add(candidate.Text)) continue;
            result[candidate.Pic] = candidate.Text;
        }
        return result;
    }

    /// <summary>"EURO 13500", "€ 7.500": the deck's static price — the live shop shows the real price and discount.</summary>
    private static bool IsPriceLine(string s) => Regex.IsMatch(s, @"^(EURO|EUR|€)\s*[\d .,]+(,-)?$", RegexOptions.IgnoreCase);

    private static bool IsTitle(XElement sp) =>
        sp.Descendants(P + "ph").Any(ph => (string?)ph.Attribute("type") is "title" or "ctrTitle");

    private static IEnumerable<string> Paragraphs(XElement sp) => sp.Descendants(A + "p").Select(ParagraphText);

    private static string ParagraphText(XElement p) =>
        string.Concat(p.Descendants().Where(e => e.Name == A + "t" || e.Name == A + "br").Select(e => e.Name == A + "br" ? " " : e.Value));

    /// <summary>Collapse whitespace (incl. non-breaking) — titles arrive split over runs ("6 0 min").</summary>
    private static string Clean(string s) => Regex.Replace(s.Replace(' ', ' ').Replace('\v', ' '), @"\s+", " ").Trim();

    /// <summary>Text is text: Markdown characters that would change its meaning are escaped (URLs stay clickable).</summary>
    private static string Escape(string s) => Regex.Replace(s, @"([\\`*_\[\]#<>])", @"\$1");

    /// <summary>
    /// Does this slide open a CHAPTER? Its layout says so by type (title / section header) or — in decks whose layouts carry
    /// no type, like the ELDK27 deck — by name ("Title", "Title Slide", "Section Header").
    /// </summary>
    private static bool IsChapterSlide(ZipArchive zip, string slidePath, IReadOnlyDictionary<string, string> rels)
    {
        var layout = rels.Values.FirstOrDefault(t => t.Contains("slideLayouts/", StringComparison.Ordinal));
        var root = layout is null ? null : Load(zip, Resolve(slidePath, layout))?.Root;
        if (root is null) return false;
        if ((string?)root.Attribute("type") is "title" or "secHead") return true;
        var name = ((string?)root.Element(P + "cSld")?.Attribute("name") ?? string.Empty).Trim();
        return name.Equals("Title", StringComparison.OrdinalIgnoreCase) || name.Equals("Title Slide", StringComparison.OrdinalIgnoreCase)
               || name.Equals("Section Header", StringComparison.OrdinalIgnoreCase);
    }

    private static XDocument? Load(ZipArchive zip, string path)
    {
        var e = zip.GetEntry(path);
        if (e is null) return null;
        using var s = e.Open();
        return XDocument.Load(s, LoadOptions.PreserveWhitespace);   // a run that is only " " is a real space
    }

    private static Dictionary<string, string> Rels(ZipArchive zip, string partPath)
    {
        var dir = Path.GetDirectoryName(partPath)!.Replace('\\', '/');
        var doc = Load(zip, $"{dir}/_rels/{Path.GetFileName(partPath)}.rels");
        return doc is null ? new Dictionary<string, string>()
            : doc.Descendants(Rel + "Relationship")
                .Where(r => (string?)r.Attribute("TargetMode") != "External")
                .ToDictionary(r => (string)r.Attribute("Id")!, r => (string)r.Attribute("Target")!);
    }

    /// <summary>A relationship target relative to its part ("../media/image3.jpg") as a package path.</summary>
    private static string Resolve(string partPath, string target)
    {
        if (target.StartsWith('/')) return target.TrimStart('/');
        var parts = (Path.GetDirectoryName(partPath)!.Replace('\\', '/') + "/" + target).Split('/').ToList();
        var stack = new List<string>();
        foreach (var p in parts)
        {
            if (p == "..") { if (stack.Count > 0) stack.RemoveAt(stack.Count - 1); }
            else if (p.Length > 0 && p != ".") stack.Add(p);
        }
        return string.Join("/", stack);
    }

    public static string Slug(string title)
    {
        var s = Regex.Replace(title.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        return SlugPrefix + (s.Length > 60 ? s[..60].Trim('-') : s);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Import
    // ------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Read the configured deck from SharePoint and (unless <paramref name="dryRun"/>) replace the imported pages. A dry run
    /// downloads and parses only — it writes nothing — and reports the pages and what was skipped.
    /// </summary>
    public async Task<ImportResult> ImportAsync(int eventId, string byEmail, bool dryRun, CancellationToken ct = default)
    {
        if (!_settings.IsConfigured) return new(false, "The shop is not configured for this edition.", Array.Empty<string>());
        var cfg = _settings.Shop().CatalogDeck;
        if (string.IsNullOrWhiteSpace(cfg.Folder) || string.IsNullOrWhiteSpace(cfg.FileName))
            return new(false, "No sponsor deck is configured (shop config → catalogDeck).", Array.Empty<string>());
        if (!_store.CanRead) return new(false, "SharePoint cannot be read right now.", Array.Empty<string>());

        var file = (await _store.ListAsync(cfg.Folder, ct))
            .FirstOrDefault(f => string.Equals(f.Name, cfg.FileName, StringComparison.OrdinalIgnoreCase));
        if (file is null) return new(false, $"'{cfg.FileName}' was not found in '{cfg.Folder}'.", Array.Empty<string>());
        var bytes = await _store.DownloadAsync(file.ItemId, ct);
        if (bytes is null) return new(false, $"'{cfg.FileName}' could not be downloaded.", Array.Empty<string>());

        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var parse = Parse(zip, cfg.SkipTitlesContaining, cfg.SkipColumnsNamed.Count > 0 ? cfg.SkipColumnsNamed : null, cfg.SkipPagesTitled,
            cfg.SkipChaptersTitled);
        // The deck's "How to participate" (reserve by mail) is replaced by how ordering works now (operator 2026-09-27).
        var howTo = new DeckPage { Title = HowToOrderTitle, Chapter = HowToOrderTitle };
        howTo.Body.Append(HowToOrderMarkdown(_settings.Shop()));
        // Page 1: the event, its dates and where (operator 2026-09-28: "make a front page With Experts Live Denmark 2027 and
        // the dates and location - as page 1"). The edition's display name, dates and venue — never the deck's cover text.
        var eventName = await _db.Events.AsNoTracking().Where(e => e.Id == eventId).Select(e => e.DisplayName).FirstOrDefaultAsync(ct);
        var front = new DeckPage { Title = string.IsNullOrWhiteSpace(eventName) ? _settings.Shop().Event.Name : eventName!, Chapter = FirstChapter };
        front.Body.Append(FrontPageMarkdown(_settings.Shop()));
        parse = new DeckParse(parse.Pages.Prepend(front).Append(howTo).ToList(), parse.Skipped);

        // Config: a page whose title does not name its packages (the lounge page and the lounge-with-booth package).
        foreach (var p in parse.Pages)
            if (cfg.ProductMatches.TryGetValue(p.Title, out var m) && !string.IsNullOrWhiteSpace(m)) p.Match = m;

        // A package the deck has no page for (Fruit, Pre-day Sponsor…) gets one from its webshop product, so every line of
        // the availability pages links somewhere (operator 2026-09-28: "some are missing links").
        var extra = await ProductPagesAsync(eventId, parse.Pages, ct);
        if (extra.Count > 0)
        {
            var list = parse.Pages.ToList();
            var at = list.FindIndex(p => p.AvailabilityView != ShopCatalogAvailabilityView.None);
            list.InsertRange(at < 0 ? list.Count - 1 : at, extra);
            parse = new DeckParse(list, parse.Skipped);
        }

        var lines = parse.Pages.Select(p => $"{p.Chapter} › {p.Title} — slides {string.Join(", ", p.Slides)}, {p.Pictures.Count} picture(s)")
            .Concat(parse.Skipped.Select(s => "skipped: " + s)).ToList();
        if (dryRun)
            return new(true, $"Preview: {parse.Pages.Count} page(s) with {parse.Pages.Sum(p => p.Pictures.Count)} picture(s) would be imported "
                             + $"(unpublished); {parse.Skipped.Count} skipped. Nothing was changed.", lines);

        if (!_store.CanStore || !_paths.TryResolve(DocLibraryPaths.SponsorWebshopProducts, out var folder) || string.IsNullOrWhiteSpace(folder))
            return new(false, "Pictures cannot be stored: no SharePoint folder is configured for the shop's products.", lines);

        var now = _clock.GetUtcNow();
        var pages = new List<ShopCatalogPage>();
        var slugs = new HashSet<string>(StringComparer.Ordinal);
        var sort = 1000;
        var pictureCount = 0;
        foreach (var dp in parse.Pages)
        {
            var slug = Slug(dp.Title);
            for (var i = 2; !slugs.Add(slug); i++) slug = Slug(dp.Title) + "-" + i;
            var body = dp.Body.ToString();
            var n = 0;
            foreach (var pic in dp.Pictures)
            {
                var entry = zip.GetEntry(pic.MediaPath);
                string replacement = NoPicture;
                if (entry is not null)
                {
                    try
                    {
                        var name = $"{PicturePrefix}{slug[SlugPrefix.Length..]}-{++n}.jpg";
                        await using var s = entry.Open();
                        var jpg = await ToWebJpegAsync(s, ct);
                        await _store.UploadToFolderAsync(folder, name, jpg, "image/jpeg", ct);
                        replacement = $"![{Escape(pic.Caption ?? dp.Title)}](/shop-catalog-image/{name})";
                        pictureCount++;
                    }
                    catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException or NotSupportedException)
                    {
                        _log?.LogInformation("§1312 deck picture {Media} is not a web picture (skipped): {Error}", pic.MediaPath, ex.Message);
                    }
                }
                body = body.Replace(pic.Token, replacement, StringComparison.Ordinal);
            }
            pages.Add(new ShopCatalogPage
            {
                EventId = eventId, Slug = slug, Title = dp.Title, Chapter = dp.Chapter, SortOrder = sort += 10,
                BodyMarkdown = TidyBody(body), IsPublished = false, UpdatedAt = now, UpdatedByEmail = byEmail,
                MediaUrl = dp.VideoUrl is null ? null : await _resolveVideo(dp.VideoUrl, ct),
                // The page title picks its packages ("Gold Exhibitor" → every Gold booth, live price + availability).
                ProductMatch = dp.AvailabilityView == ShopCatalogAvailabilityView.None ? Truncate(dp.Match ?? dp.Title, 200) : null,
                AvailabilityView = dp.AvailabilityView, ShowBoothMap = dp.ShowBoothMap,
            });
        }

        // Replace ONLY what an earlier import made; pages an organizer wrote are never touched.
        var old = await _db.ShopCatalogPages.Where(p => p.EventId == eventId && p.Slug.StartsWith(SlugPrefix)).ToListAsync(ct);
        _db.ShopCatalogPages.RemoveRange(old);
        await _db.SaveChangesAsync(ct);
        _db.ShopCatalogPages.AddRange(pages);
        await _db.SaveChangesAsync(ct);
        return new(true, $"Imported {pages.Count} page(s) with {pictureCount} picture(s) from '{cfg.FileName}' — all UNPUBLISHED; review and "
                         + $"publish them. {parse.Skipped.Count} slide(s) skipped{(old.Count > 0 ? $"; {old.Count} page(s) from the previous import replaced" : "")}.",
            lines);
    }

    public const string HowToOrderTitle = "How to order";

    /// <summary>
    /// §1312 — a page for every sellable package no deck page covers: its webshop description and first picture; the page
    /// then shows its live price and availability like any other. Placed in the chapter most package pages are in.
    /// </summary>
    private async Task<List<DeckPage>> ProductPagesAsync(int eventId, IReadOnlyList<DeckPage> pages, CancellationToken ct)
    {
        var products = (await _db.ShopProducts.AsNoTracking().Include(p => p.PrimaryCategory).Include(p => p.Images)
                .Where(p => p.EventId == eventId && p.Status == ShopProductStatus.Published && p.PriceEur > 0)
                .ToListAsync(ct))
            .Where(p => p.PrimaryCategory is not null && ShopPackageAvailability.Groups.Contains(p.PrimaryCategory.Group))
            .ToList();
        if (products.Count == 0) return new();
        var rows = ShopPackageAvailability.BuildRows(products, new Dictionary<int, ShopAvailability>(),
            Array.Empty<ShopPackageAvailability.Hold>(), null);
        var matching = pages.Where(p => p.AvailabilityView == ShopCatalogAvailabilityView.None).ToList();
        string MatchOf(DeckPage p) => p.Match ?? p.Title;
        var chapter = matching.Where(p => rows.Any(r => r.Products.Any(x => ShopPackageAvailability.Matches(x, MatchOf(p)))))
            .GroupBy(p => p.Chapter).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? FirstChapter;

        var result = new List<DeckPage>();
        foreach (var row in rows.Where(r => !matching.Any(p => r.Products.Any(x => ShopPackageAvailability.Matches(x, MatchOf(p))))))
        {
            var product = row.Products[0];
            var page = new DeckPage { Title = row.Label, Chapter = chapter, Match = "=" + row.Label };
            var text = ShopSigningService.PlainText(product.DescriptionHtml) ?? ShopSigningService.PlainText(product.ShortDescriptionHtml);
            if (text is not null)
                foreach (var para in text.Split('\n'))
                    page.Body.Append(para.StartsWith("• ", StringComparison.Ordinal) ? "- " + Escape(para[2..]) + "\n" : Escape(para) + "\n\n");
            if (product.Images.OrderBy(i => i.SortOrder).FirstOrDefault() is { } image)
                page.Body.Append($"\n::: catalog-gallery\n^^^\n![{Escape(row.Label)}](/shop-image/{image.Id})\n^^^\n\n:::\n\n");
            if (page.Body.Length == 0) page.Body.Append("See the price and availability below.\n\n");
            result.Add(page);
        }
        return result;
    }

    /// <summary>§1312 — page 1 of the catalogue: sponsor information, the dates, the venue and where to go next.</summary>
    public static string FrontPageMarkdown(Config.ShopConfig cfg)
    {
        var ev = cfg.Event;
        var sb = new StringBuilder("### Sponsor information\n\n");
        if (ev.DatesText.Count > 0)
        {
            sb.Append("**Dates**\n\n");
            foreach (var d in ev.DatesText.Where(d => !string.IsNullOrWhiteSpace(d))) sb.Append("- ").Append(Escape(d.Trim())).Append('\n');
            sb.Append('\n');
        }
        var venue = ev.Venue.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => Escape(v.Trim())).ToList();
        if (venue.Count > 0) sb.Append("**Location**\n\n").Append(string.Join("  \n", venue)).Append("\n\n");
        if (Uri.TryCreate(ev.Website, UriKind.Absolute, out var site))
            sb.Append($"**Website:** [{site.Host}]({site})\n\n");
        sb.Append($"[What is still available →](/sponsor-catalog/{Slug(SummaryTitle)}) · [How to order →](/sponsor-catalog/{Slug(HowToOrderTitle)})\n");
        return sb.ToString();
    }

    /// <summary>
    /// §1312 — how ordering works NOW, replacing the deck's "How to Participate" (reserve by e-mail, order in the old portal).
    /// Operator 2026-09-27: <i>"make How to order instead based on new method, including how to register, etc"</i>. Built
    /// from the edition's shop config (late surcharge), so it stays true for the next edition.
    /// </summary>
    public static string HowToOrderMarkdown(Config.ShopConfig cfg)
    {
        var sb = new StringBuilder();
        sb.Append("Everything happens in the Experts Live sponsor shop — registration, reservation, contract and signing.\n\n");
        sb.Append("### 1. Register your company\n\n");
        sb.Append("New to the sponsor shop? [Register your company](/sponsor-registration) — it takes a few minutes: the company's legal "
                  + "and public name, VAT number, address, and the people who sign, pay and coordinate. We check the VAT number, and if "
                  + "your company is already known (same VAT number or e-mail domain) you join it instead of creating it twice.\n\n");
        sb.Append("Sponsored with us before? Your company is most likely already there — just sign in.\n\n");
        sb.Append("### 2. Sign in\n\n");
        sb.Append("[Sign in](/Login) with your work e-mail. We send you a one-time PIN code — there is no password.\n\n");
        sb.Append("### 3. Choose\n\n");
        sb.Append("Browse the [sponsor shop](/Sponsor/Shop): tier packages with an exhibitor booth, digital-only packages, sponsor "
                  + "sessions, branded feature packages and options — with today's prices and availability. Pick your booth on the "
                  + $"booth map. [{AvailabilityChapter}](/sponsor-catalog/{Slug(SummaryTitle)}) shows what is still left.\n\n");
        sb.Append("### 4. Reserve or order\n\n");
        sb.Append("- **Reservation — a handshake.** Ask us to reserve your cart. When we accept it, the items are held for your company "
                  + "until the agreed deadline — first come, first served. From the reservation mail you can review and sign, ask for "
                  + "more time, or cancel.\n");
        sb.Append("- **Order — a commitment.** Review the contract in the shop and sign it online: draw, type or upload your signature. "
                  + "Nothing is ordered until the contract is signed, and everyone gets the signed contract as a PDF.\n");
        sb.Append("- **Prefer paper?** Sign the contract PDF and send it back to us — we confirm the order for you.\n\n");
        sb.Append("### 5. Pay\n\n");
        sb.Append("You receive an invoice after signing; payment is due as stated in the contract. Prices are excluding VAT; Danish "
                  + "VAT is added for Danish companies only.");
        var late = cfg.LateSurcharge;
        if (late is { Percent: > 0, DaysBeforeEvent: > 0 })
        {
            var groups = late.Groups.Select(g => Regex.Replace(g, "(?<=[a-z])(?=[A-Z])", " ").ToLowerInvariant()).ToList();
            var what = groups.Count switch
            {
                0 => "the order",
                1 => groups[0],
                _ => string.Join(", ", groups.Take(groups.Count - 1)) + " and " + groups[^1],
            };
            sb.Append($" Ordered less than {late.DaysBeforeEvent} days before the event, {what} cost {late.Percent:0.##} % more.");
        }
        sb.Append("\n\n### 6. After you have ordered\n\n");
        sb.Append("Your **Get Started** page in the hub takes you through the next steps — company profile, logos, contacts, booth and "
                  + "deadlines — and reminds you of what is still missing.\n\n");
        sb.Append("Questions? Reply to any of our mails and we will help.\n");
        return sb.ToString();
    }

    /// <summary>A picture that could not be converted leaves no empty frame (nor an empty gallery) behind.</summary>
    public static string TidyBody(string body)
    {
        // 🔴 Only a frame whose picture is MISSING goes — marked explicitly. Matching "^^^, blank, ^^^" also matched the gap
        // BETWEEN two cards and merged the whole team into one card (2026-09-28).
        body = Regex.Replace(body, @"\^\^\^\n" + Regex.Escape(NoPicture) + @"\n\^\^\^[^\n]*\n", "");
        body = body.Replace(NoPicture + "\n\n", "").Replace(NoPicture, "");
        body = Regex.Replace(body, @"::: catalog-gallery\n\n*:::\n", "");
        // Photo slides in a row (15 event photos, three booth examples) are ONE gallery, not a stack of full-width pictures.
        body = Regex.Replace(body, @":::\n\n+::: catalog-gallery\n", "");
        return Regex.Replace(body, @"\n{3,}", "\n\n").Trim();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    /// <summary>What a picture that could not be converted leaves behind, for <see cref="TidyBody"/> to remove.</summary>
    public const string NoPicture = "{{no-picture}}";

    /// <summary>A deck picture as a web JPEG, at most 1600 px wide.</summary>
    public static async Task<byte[]> ToWebJpegAsync(Stream source, CancellationToken ct = default)
    {
        using var img = await Image.LoadAsync(source, ct);
        if (img.Width > MaxPictureWidth)
            img.Mutate(x => x.Resize(new ResizeOptions { Mode = ResizeMode.Max, Size = new Size(MaxPictureWidth, 0) }));
        // A transparent PNG goes on white, not black.
        img.Mutate(x => x.BackgroundColor(Color.White));
        using var ms = new MemoryStream();
        await img.SaveAsJpegAsync(ms, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = 82 }, ct);
        return ms.ToArray();
    }
}
