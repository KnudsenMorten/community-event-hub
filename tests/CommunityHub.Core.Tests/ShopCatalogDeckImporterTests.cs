using System.IO.Compression;
using System.Text;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Webshop;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1312 — operator 2026-09-27: "import the powerpoint into the catalog including pictures". Built from the real ELDK27 deck's
/// shapes: chapters from "Title" layouts, "(1/3)" parts as one page, photo slides after a video on their own page, and never
/// the slides or columns that name who bought what, nor the deck's static prices.
/// </summary>
public sealed class ShopCatalogDeckImporterTests
{
    private const string Ns = "xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";

    private static string Title(string t) =>
        $"<p:sp><p:nvSpPr><p:cNvPr id=\"1\" name=\"t\"/><p:cNvSpPr/><p:nvPr><p:ph type=\"title\"/></p:nvPr></p:nvSpPr><p:txBody><a:p>{Runs(t)}</a:p></p:txBody></p:sp>";
    private static string Runs(string t) => string.Concat(t.Split('¦').Select(r => $"<a:r><a:t xml:space=\"preserve\">{System.Security.SecurityElement.Escape(r)}</a:t></a:r>"));
    private static string Body(params string[] paras) =>
        "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"b\"/><p:cNvSpPr/><p:nvPr><p:ph idx=\"1\"/></p:nvPr></p:nvSpPr><p:txBody>"
        + string.Concat(paras.Select(p => $"<a:p>{Runs(p)}</a:p>")) + "</p:txBody></p:sp>";
    private static string Pic => "<p:pic><p:blipFill><a:blip r:embed=\"rIdImg\"/></p:blipFill></p:pic>";
    private static string Table(params string[][] rows) =>
        "<p:graphicFrame><a:graphic><a:graphicData><a:tbl>"
        + string.Concat(rows.Select(r => "<a:tr>" + string.Concat(r.Select(c => $"<a:tc><a:txBody><a:p>{Runs(c)}</a:p></a:txBody></a:tc>")) + "</a:tr>"))
        + "</a:tbl></a:graphicData></a:graphic></p:graphicFrame>";

    /// <summary>A tiny .pptx: each slide = (layout name, hidden, shapes).</summary>
    private static ZipArchive Deck(params (string Layout, bool Hidden, string Shapes)[] slides)
    {
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Put(string name, string content) { using var w = new StreamWriter(z.CreateEntry(name).Open(), Encoding.UTF8); w.Write(content); }
            Put("ppt/presentation.xml", $"<p:presentation {Ns}><p:sldIdLst>" + string.Concat(slides.Select((_, i) => $"<p:sldId id=\"{256 + i}\" r:id=\"rId{i + 1}\"/>")) + "</p:sldIdLst></p:presentation>");
            Put("ppt/_rels/presentation.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + string.Concat(slides.Select((_, i) => $"<Relationship Id=\"rId{i + 1}\" Type=\"slide\" Target=\"slides/slide{i + 1}.xml\"/>")) + "</Relationships>");
            Put("ppt/slideLayouts/title.xml", $"<p:sldLayout {Ns}><p:cSld name=\"Title\"/></p:sldLayout>");
            Put("ppt/slideLayouts/basic.xml", $"<p:sldLayout {Ns}><p:cSld name=\"Basic\"/></p:sldLayout>");
            Put("ppt/media/image1.png", "not-a-real-picture");
            for (var i = 0; i < slides.Length; i++)
            {
                var (layout, hidden, shapes) = slides[i];
                Put($"ppt/slides/slide{i + 1}.xml", $"<p:sld {Ns}{(hidden ? " show=\"0\"" : "")}><p:cSld><p:spTree>{shapes}</p:spTree></p:cSld></p:sld>");
                Put($"ppt/slides/_rels/slide{i + 1}.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                    + $"<Relationship Id=\"rIdL\" Type=\"layout\" Target=\"../slideLayouts/{layout}.xml\"/>"
                    + "<Relationship Id=\"rIdImg\" Type=\"image\" Target=\"../media/image1.png\"/></Relationships>");
            }
        }
        ms.Position = 0;
        return new ZipArchive(ms, ZipArchiveMode.Read);
    }

    private static ShopCatalogDeckImporter.DeckParse ParseSample() => ShopCatalogDeckImporter.Parse(Deck(
        ("title", false, Title("ELDK27 Sponsor Information") + Body("The largest conference")),
        ("basic", false, Title("Video - Highlights (1 min) - https://highlights.example.test") + Pic),
        ("basic", false, Pic),                                                               // event photos after a video
        ("basic", false, Pic + Body("Community Lounge")),
        ("title", false, Title("ELDK27 Sponsor Opportunities") + Body("Prices excl. VAT")),
        ("basic", false, Title("Sponsor ¦Long¦ ¦6¦0 min Session (1/2)") + Body("Sponsor Opportunity", "60 min session", "EURO 8000")),
        ("basic", false, Pic),                                                               // example photo joins the page
        ("basic", false, Title("Platinum Booths") + Pic),                                   // inside the (1/2)…(2/2) series
        ("basic", false, Title("Sponsor Long 60 min Session (2/2)") + Body("Leads included")),
        ("basic", true, Title("Hidden draft") + Body("secret")),
        ("basic", false, Title("Sponsor Sessions Actual Reservations ELDK27") + Body("Acme bought slot 2")),
        ("basic", false, Pic + Body("Acme logo")),                                           // follows the skipped slide
        ("basic", false, Title("Pre-day Track Sponsor") + Table(
            new[] { "Track", "Est. Attendees", "Price", "Sold To" },
            new[] { "Security | Master Class", "~200", "15000", "Acme A/S" }))),
        new[] { "Actual Reservations" });

    [Fact]
    public void Slides_become_pages_under_their_chapters()
    {
        var r = ParseSample();
        var titles = r.Pages.Select(p => $"{p.Chapter} › {p.Title}").ToList();

        Assert.Equal(new[]
        {
            "About the event › ELDK27 Sponsor Information",
            "About the event › Video - Highlights (1 min)",
            "About the event › Impressions",
            "ELDK27 Sponsor Opportunities › ELDK27 Sponsor Opportunities",
            "ELDK27 Sponsor Opportunities › Sponsor Long 60 min Session",          // runs joined, " " kept, (1/2)+(2/2) one page
            "Availability & booked › Availability",                                 // ONE live page where the reservation slides were
            "ELDK27 Sponsor Opportunities › Pre-day Track Sponsor",
        }, titles);
        var session = r.Pages.Single(p => p.Title == "Sponsor Long 60 min Session");
        // Operator 2026-09-27: the booth pictures were missing from the tier page — a titled slide inside an open
        // "(1/2) … (2/2)" series belongs to it, under its own heading.
        Assert.Equal(new[] { 6, 7, 8, 9 }, session.Slides);
        Assert.Equal(2, session.Pictures.Count);
        Assert.Contains("### Platinum Booths", session.Body.ToString());
        // The video is the page's player, not a link in the text.
        Assert.Equal("https://highlights.example.test", r.Pages[1].VideoUrl);
        Assert.DoesNotContain("Watch the video", r.Pages[1].Body.ToString());
        // Operator 2026-09-28: "so one Availability page that includes everything" — the table and the booth map together.
        var availability = r.Pages.Single(p => p.Title == ShopCatalogDeckImporter.SummaryTitle);
        Assert.Equal(ShopCatalogAvailabilityView.Summary, availability.AvailabilityView);
        Assert.True(availability.ShowBoothMap);
        Assert.Equal(2, r.Pages.Single(p => p.Title == "Impressions").Pictures.Count);
    }

    [Fact]
    public void Who_bought_what_hidden_slides_and_static_prices_are_never_imported()
    {
        var r = ParseSample();
        var all = string.Join("\n", r.Pages.Select(p => p.Title + "\n" + p.Body));

        Assert.DoesNotContain("Acme", all);                 // the reservations slide, its follow-up, the "Sold To" column
        Assert.DoesNotContain("secret", all);               // hidden slide
        Assert.DoesNotContain("EURO 8000", all);            // static price line
        Assert.DoesNotContain("15000", all);                // "Price" column
        Assert.Contains("| Track | Est. Attendees |", all);
        Assert.Contains("Security \\| Master Class", all);  // a pipe in a cell cannot break the table
        Assert.Contains(r.Skipped, s => s.Contains("Actual Reservations"));
        Assert.Contains(r.Skipped, s => s.Contains("hidden"));
    }

    private static string Xfrm(long x, long y, long w, long h) =>
        $"<p:spPr><a:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{w}\" cy=\"{h}\"/></a:xfrm></p:spPr>";
    private static string PicAt(long x, long y, long w, long h) =>
        $"<p:pic><p:blipFill><a:blip r:embed=\"rIdImg\"/></p:blipFill>{Xfrm(x, y, w, h)}</p:pic>";
    private static string TextAt(string text, long x, long y, long w, long h) =>
        $"<p:sp><p:nvSpPr><p:cNvPr id=\"3\" name=\"l\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>{Xfrm(x, y, w, h)}<p:txBody><a:p>{Runs(text)}</a:p></p:txBody></p:sp>";

    [Fact]
    public void Pictures_sit_side_by_side_with_the_label_under_or_beside_them_as_caption()
    {
        // Deck slide 54: photos in a row with a label under each; slide 38: "Example Platinum booth" left of the photo.
        var r = ShopCatalogDeckImporter.Parse(Deck(
            ("basic", false, Title("Options") + PicAt(0, 2_000_000, 2_000_000, 1_500_000) + PicAt(3_000_000, 2_000_000, 2_000_000, 1_500_000)
                             + TextAt("Staff Tickets", 200_000, 3_600_000, 1_500_000, 230_000)
                             + TextAt("TV Booth", 3_300_000, 3_600_000, 1_200_000, 230_000)
                             + TextAt("A long paragraph about the options that is far away from every picture on the slide.", 0, 5_000_000, 8_000_000, 400_000)),
            ("basic", false, PicAt(2_000_000, 200_000, 7_000_000, 4_600_000) + TextAt("Example booth", 200_000, 1_800_000, 1_500_000, 1_400_000))),
            Array.Empty<string>());

        var page = r.Pages.Single();
        Assert.Equal(new[] { "Staff Tickets", "TV Booth", "Example booth" }, page.Pictures.Select(p => p.Caption));
        var body = ShopCatalogDeckImporter.TidyBody(page.Body.ToString());
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(body, "::: catalog-gallery").Count);   // both slides: ONE gallery
        Assert.Contains("^^^ Staff Tickets", body);
        Assert.Contains("A long paragraph", body);                          // not a caption: stays text
        Assert.DoesNotContain("\nStaff Tickets\n", body);                   // a caption is not repeated as text
    }

    private static System.Xml.Linq.XDocument SmartArt(string[] nodes, (int Src, int Dest, int Ord)[] links)
    {
        const string ns = "xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"";
        var pts = "<dgm:pt modelId=\"0\" type=\"doc\"/>" + string.Concat(nodes.Select((t, i) =>
            $"<dgm:pt modelId=\"{i + 1}\"><dgm:t><a:p>{string.Concat(t.Split('¦').Select(r => $"<a:r><a:t>{r}</a:t></a:r>"))}</a:p></dgm:t></dgm:pt>"))
            + "<dgm:pt modelId=\"99\" type=\"pres\"><dgm:t><a:p><a:r><a:t>layout noise</a:t></a:r></a:p></dgm:t></dgm:pt>";
        var cxns = string.Concat(links.Select((l, i) => $"<dgm:cxn modelId=\"c{i}\" srcId=\"{l.Src}\" destId=\"{l.Dest}\" srcOrd=\"{l.Ord}\"/>"))
            + "<dgm:cxn modelId=\"p1\" type=\"presOf\" srcId=\"1\" destId=\"99\"/>";
        return System.Xml.Linq.XDocument.Parse($"<dgm:dataModel {ns}><dgm:ptLst>{pts}</dgm:ptLst><dgm:cxnLst>{cxns}</dgm:cxnLst></dgm:dataModel>");
    }

    [Fact]
    public void A_SmartArt_timeline_becomes_a_readable_table()
    {
        // Deck slide 3 "Numbers" (operator 2026-09-27: "impossible to read … compare with ppt"): years with their numbers.
        var md = ShopCatalogDeckImporter.SmartArtMarkdown(SmartArt(
            new[] { "2026", "2025", "1430 participants¦ (91%)", "113 sessions", "750 participants" },
            new[] { (0, 1, 0), (0, 2, 1), (1, 3, 0), (1, 4, 1), (2, 5, 0) }));

        Assert.Equal("| 2026 | 2025 |\n| --- | --- |\n| 1430 participants (91%) | 750 participants |\n| 113 sessions |  |\n\n", md);
    }

    [Fact]
    public void A_deeper_SmartArt_becomes_a_nested_list_and_layout_points_are_ignored()
    {
        var md = ShopCatalogDeckImporter.SmartArtMarkdown(SmartArt(
            new[] { "Packages", "Booths", "Platinum" },
            new[] { (0, 1, 0), (1, 2, 0), (2, 3, 0) }));

        Assert.Equal("- **Packages**\n  - Booths\n    - Platinum\n\n", md);
        Assert.DoesNotContain("layout noise", md);
    }

    [Fact]
    public void A_page_the_organizers_do_not_want_is_left_out()
    {
        // Operator 2026-09-27: "remove this page" — the deck's cover (catalogDeck.skipPagesTitled), so a re-import does not bring it back.
        var r = ShopCatalogDeckImporter.Parse(Deck(
            ("title", false, Title("ELDK27 Sponsor Information") + Body("Cover text")),
            ("basic", false, Title("Numbers") + Body("1430 participants"))),
            Array.Empty<string>(), skipPagesTitled: new[] { "ELDK27 Sponsor Information" });

        Assert.Equal(new[] { "Numbers" }, r.Pages.Select(p => p.Title));
        Assert.Contains(r.Skipped, s => s.Contains("not wanted"));
    }

    [Fact]
    public void An_orphaned_chapter_is_left_out_whole()
    {
        // Operator 2026-09-27: "drop the last pages about logistics as it is orphaned".
        var r = ShopCatalogDeckImporter.Parse(Deck(
            ("basic", false, Title("Coffee Sponsor") + Body("Your logo on every cup")),
            ("title", false, Title("ELDK27 Logistics") + Body("Four steps")),
            ("basic", false, Title("Step 1: Agreement") + Body("Sign the agreement"))),
            Array.Empty<string>(), skipChaptersTitled: new[] { "ELDK27 Logistics" });

        Assert.Equal(new[] { "Coffee Sponsor" }, r.Pages.Select(p => p.Title));
    }

    [Fact]
    public void How_to_order_explains_the_new_way_from_registration_to_payment()
    {
        // Operator 2026-09-27: "make How to order instead based on new method, including how to register, etc".
        var cfg = new CommunityHub.Core.Config.ShopConfig();
        cfg.LateSurcharge.Percent = 50;
        cfg.LateSurcharge.DaysBeforeEvent = 30;
        cfg.LateSurcharge.Groups.AddRange(new[] { "Furniture", "BoothOptions", "PackageHandling" });

        var md = ShopCatalogDeckImporter.HowToOrderMarkdown(cfg);

        Assert.Contains("(/sponsor-registration)", md);
        Assert.Contains("(/Login)", md);
        Assert.Contains("(/Sponsor/Shop)", md);
        Assert.Contains($"(/sponsor-catalog/{ShopCatalogDeckImporter.Slug(ShopCatalogDeckImporter.SummaryTitle)})", md);
        Assert.Contains("Nothing is ordered until the contract is signed", md);
        Assert.Contains("less than 30 days before the event, furniture, booth options and package handling cost 50 % more", md);
        Assert.DoesNotContain("mok@", md);                                  // the old "reserve by mailing a person" way is gone
    }

    [Fact]
    public void A_picture_that_could_not_be_converted_leaves_no_empty_frame()
    {
        var body = $"Text\n\n::: catalog-gallery\n^^^\n{ShopCatalogDeckImporter.NoPicture}\n^^^ Caption\n\n:::\n\nMore";
        Assert.Equal("Text\n\nMore", ShopCatalogDeckImporter.TidyBody(body));
    }

    [Fact]
    public void Two_cards_in_a_row_stay_two_cards()
    {
        // The gap between two figures ("^^^", blank, "^^^") is not an empty frame — it merged the team into one card.
        var body = "::: catalog-gallery\n^^^\n![A](/a.jpg)\n\n**A**\n^^^\n\n^^^\n![B](/b.jpg)\n\n**B**\n^^^\n\n:::";
        Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(ShopCatalogDeckImporter.TidyBody(body), @"\^\^\^").Count);
    }

    [Fact]
    public void A_team_SmartArt_becomes_a_card_per_person_with_the_photo()
    {
        // Operator 2026-09-28: the team page "looks wrong" — each person was one run-on bullet line.
        var md = ShopCatalogDeckImporter.SmartArtMarkdown(SmartArt(
            new[] { "MORTEN KNUDSEN", "KENT AGERLUND" }, new[] { (0, 1, 0), (0, 2, 1) }),
            new Dictionary<string, string> { ["1"] = "{{picture:0}}", ["2"] = "{{picture:1}}" });

        Assert.Equal("::: catalog-gallery\n^^^\n{{picture:0}}\n\n**MORTEN KNUDSEN**\n^^^\n\n^^^\n{{picture:1}}\n\n**KENT AGERLUND**\n^^^\n\n:::\n\n", md);
    }

    [Fact]
    public void The_front_page_names_the_dates_and_the_venue()
    {
        // Operator 2026-09-28: "make a front page With Experts Live Denmark 2027 and the dates and location - as page 1".
        var cfg = new CommunityHub.Core.Config.ShopConfig();
        cfg.Event.DatesText.AddRange(new[] { "February 9, 2027 – Pre-day", "February 10, 2027 – Main Event Day" });
        cfg.Event.Venue.AddRange(new[] { "Bella Center", "2300 Copenhagen" });
        cfg.Event.Website = "https://event.example.test";

        var md = ShopCatalogDeckImporter.FrontPageMarkdown(cfg);

        Assert.Contains("- February 9, 2027 – Pre-day\n- February 10, 2027 – Main Event Day", md);
        Assert.Contains("Bella Center  \n2300 Copenhagen", md);
        Assert.Contains("[event.example.test](https://event.example.test/)", md);
        Assert.Contains($"(/sponsor-catalog/{ShopCatalogDeckImporter.Slug(ShopCatalogDeckImporter.HowToOrderTitle)})", md);
    }

    [Fact]
    public void A_video_slide_shows_the_video_not_its_still_picture()
    {
        var video = "<p:pic><p:nvPicPr><p:cNvPr id=\"4\" name=\"v\"/><p:cNvPicPr/><p:nvPr><a:videoFile r:link=\"rIdV\"/></p:nvPr></p:nvPicPr>"
                    + "<p:blipFill><a:blip r:embed=\"rIdImg\"/></p:blipFill></p:pic>";
        var r = ShopCatalogDeckImporter.Parse(Deck(("basic", false, Title("Video - Highlights - https://v.example.test") + video)), Array.Empty<string>());

        Assert.Equal("https://v.example.test", r.Pages.Single().VideoUrl);
        Assert.Empty(r.Pages.Single().Pictures);
        Assert.True(ShopCatalogDeckImporter.IsPlayerLink("https://youtu.be/DT086UssZu4"));
        Assert.False(ShopCatalogDeckImporter.IsPlayerLink("https://highlights.eldk26.video.example.test"));
    }

    [Fact]
    public async Task A_short_video_link_is_followed_to_YouTube_even_when_the_host_refuses_clients_without_a_user_agent()
    {
        // Measured on PROD 2026-09-27: the deck's short-link host answers 403 without a User-Agent and 301 youtu.be with one.
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var listener = new System.Net.HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        var serve = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            if (string.IsNullOrEmpty(ctx.Request.UserAgent)) ctx.Response.StatusCode = 403;
            else { ctx.Response.StatusCode = 301; ctx.Response.RedirectLocation = "https://youtu.be/DT086UssZu4"; }
            ctx.Response.Close();
        });

        var resolved = await ShopCatalogDeckImporter.ResolveVideoLinkAsync($"http://localhost:{port}/", CancellationToken.None);
        await serve;

        Assert.Equal("https://youtu.be/DT086UssZu4", resolved);
    }

    [Fact]
    public void A_picture_is_scaled_to_web_size_as_jpeg()
    {
        using var big = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(3200, 1000);
        using var src = new MemoryStream();
        SixLabors.ImageSharp.ImageExtensions.SaveAsPng(big, src);
        src.Position = 0;

        var jpg = ShopCatalogDeckImporter.ToWebJpegAsync(src).GetAwaiter().GetResult();
        using var img = SixLabors.ImageSharp.Image.Load(jpg);
        Assert.Equal(1600, img.Width);
        Assert.Equal(500, img.Height);
        Assert.Equal(0xFF, jpg[0]);                          // JPEG
    }
}
