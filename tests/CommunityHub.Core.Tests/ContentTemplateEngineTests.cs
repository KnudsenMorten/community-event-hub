using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §31 content template engine — the pure generator that turns hub data into a
/// WordPress draft body + a LinkedIn short text. These prove it folds the data
/// into copy, escapes HTML, lists speakers + abstracts, never invents a publish
/// path, and degrades gracefully on thin data.
/// </summary>
public sealed class ContentTemplateEngineTests
{
    private static readonly ContentContext Ctx = new(
        EventName: "Experts Live Denmark 2027",
        DatesText: "9–10 June 2027",
        VenueText: "Bella Center, Copenhagen",
        TicketsUrl: "https://expertslive.dk/tickets/",
        AgendaUrl: "https://expertslive.dk/agenda/");

    private static AttendeeTelemetry Telemetry(int total, int pct2Day, params (string Country, int N)[] countries)
    {
        var slices = countries.Select(c => new TelemetrySlice(c.Country, c.N)).ToList();
        var tables = new List<TelemetryTable> { new("Where attendees are from", slices) };
        return new AttendeeTelemetry("all", "All attendees", total, total, 100, pct2Day,
            pct2Day, 0, 0,
            Array.Empty<TelemetryDay>(), tables, DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public void Ticket_sales_folds_count_dates_and_link_into_copy()
    {
        var t = Telemetry(420, 35, ("Denmark", 300), ("Sweden", 120));
        var c = new ContentTemplateEngine().BuildTicketSales(t, Ctx);

        Assert.Equal(ContentKind.TicketSales, c.Kind);
        Assert.Contains("420", c.BodyHtml);
        Assert.Contains("9–10 June 2027", c.Title);
        Assert.Contains("2 countries", c.BodyHtml);
        Assert.Contains("35%", c.BodyHtml);                       // two-day share
        Assert.Contains("expertslive.dk/tickets", c.BodyHtml);    // CTA link
        Assert.Contains("Denmark", c.BodyHtml);                   // top-country list
        // LinkedIn short text carries the headline number + link, no HTML.
        Assert.Contains("420", c.ShortText);
        Assert.Contains("expertslive.dk/tickets", c.ShortText);
        Assert.DoesNotContain("<p>", c.ShortText);
    }

    [Fact]
    public void Master_classes_list_each_class_with_speakers_and_abstract()
    {
        var items = new List<MasterClassContentItem>
        {
            new("Zero Trust in Practice", "A full-day hands-on lab.", "Security",
                new[] { new MasterClassSpeaker("Jane Doe", "Principal Architect", "Contoso") }, null),
            new("AI for Ops", "Bringing copilots to the SOC.", "AI",
                new[] { new MasterClassSpeaker("John Roe", null, "Fabrikam"),
                        new MasterClassSpeaker("Mary Q", "Lead Engineer", null) }, "https://expertslive.dk/mc"),
        };

        var c = new ContentTemplateEngine().BuildMasterClasses(items, Ctx);

        Assert.Equal(ContentKind.MasterClasses, c.Kind);
        Assert.Contains("Zero Trust in Practice", c.BodyHtml);
        Assert.Contains("A full-day hands-on lab.", c.BodyHtml);
        Assert.Contains("Jane Doe", c.BodyHtml);
        Assert.Contains("Principal Architect", c.BodyHtml);       // tagline preferred as role
        // Two speakers joined human-readably.
        Assert.Contains("John Roe", c.BodyHtml);
        Assert.Contains("Mary Q", c.BodyHtml);
        Assert.Contains(" and ", c.BodyHtml);
        Assert.Contains("expertslive.dk/agenda", c.BodyHtml);     // agenda CTA
        Assert.Contains("2", c.ShortText.Split('\n')[2]);        // count line
    }

    [Fact]
    public void Html_is_escaped_to_prevent_injection()
    {
        var items = new List<MasterClassContentItem>
        {
            new("<script>alert(1)</script>", "abstract & more", null,
                new[] { new MasterClassSpeaker("A&B <x>", null, null) }, null),
        };
        var c = new ContentTemplateEngine().BuildMasterClasses(items, Ctx);

        Assert.DoesNotContain("<script>alert(1)</script>", c.BodyHtml);
        Assert.Contains("&lt;script&gt;", c.BodyHtml);
        Assert.Contains("abstract &amp; more", c.BodyHtml);
    }

    [Fact]
    public void Ticket_sales_degrades_gracefully_without_optional_data()
    {
        // No countries, no two-day share — still a valid post, no crash, no link line.
        var bare = new ContentContext("Event X", "Soon", null, null, null);
        var t = Telemetry(5, 0);
        var c = new ContentTemplateEngine().BuildTicketSales(t, bare);

        Assert.Contains("5", c.BodyHtml);
        Assert.DoesNotContain("secure your ticket here", c.BodyHtml);  // no tickets URL ⇒ no CTA
        Assert.DoesNotContain("countries", c.BodyHtml);               // single/zero country
    }
}
