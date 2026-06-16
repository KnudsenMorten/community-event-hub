using CommunityHub.Core.Config;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Offline tests for the <c>resources</c> block of
/// <see cref="EventEditionConfigLoader"/> — the config-only content source
/// behind the shared <c>/Resources</c> page (REQUIREMENTS §1). There is no
/// schema or database here, so the whole feature's data path is a JSON parse:
/// these tests pin the parse, the defensive sanitization, and the empty-state
/// signal the page relies on to avoid rendering dead/blank links.
/// </summary>
public sealed class EventResourcesConfigTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"ceh-resources-{Guid.NewGuid():N}.json");

    private ResourcesConfig Load(string json)
    {
        File.WriteAllText(_path, json);
        return new EventEditionConfigLoader().Load(_path).Resources;
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void Missing_resources_block_yields_an_empty_but_non_null_config()
    {
        var res = Load("""{ "edition": { "code": "X" } }""");

        Assert.NotNull(res);
        Assert.True(res.IsEmpty);
        Assert.Empty(res.Sections);
    }

    [Fact]
    public void Missing_file_yields_an_empty_config()
    {
        // No file written at all.
        var res = new EventEditionConfigLoader()
            .Load(Path.Combine(Path.GetTempPath(), $"ceh-none-{Guid.NewGuid():N}.json"))
            .Resources;

        Assert.True(res.IsEmpty);
    }

    [Fact]
    public void Intro_and_sections_parse_in_order()
    {
        var res = Load("""
        {
          "resources": {
            "intro": "Welcome crew.",
            "sections": [
              { "title": "At the venue", "description": "Find your way.",
                "links": [
                  { "label": "Floor plan", "url": "https://example.test/plan", "note": "Hall map." },
                  { "label": "Guide", "url": "https://example.test/guide.pdf", "isDownload": true }
                ] },
              { "title": "Help", "links": [
                  { "label": "Email us", "url": "mailto:info@example.test" } ] }
            ]
          }
        }
        """);

        Assert.False(res.IsEmpty);
        Assert.Equal("Welcome crew.", res.Intro);
        Assert.Equal(2, res.Sections.Count);

        var venue = res.Sections[0];
        Assert.Equal("At the venue", venue.Title);
        Assert.Equal("Find your way.", venue.Description);
        Assert.Equal(2, venue.Links.Count);
        Assert.Equal("Floor plan", venue.Links[0].Label);
        Assert.Equal("https://example.test/plan", venue.Links[0].Url);
        Assert.Equal("Hall map.", venue.Links[0].Note);
        Assert.False(venue.Links[0].IsDownload);
        Assert.True(venue.Links[1].IsDownload);

        Assert.Equal("Help", res.Sections[1].Title);
    }

    [Fact]
    public void Links_missing_a_label_or_url_are_dropped()
    {
        var res = Load("""
        {
          "resources": {
            "sections": [
              { "title": "Links", "links": [
                { "label": "Good", "url": "https://example.test/ok" },
                { "label": "No url", "url": "" },
                { "label": "", "url": "https://example.test/no-label" },
                { "url": "https://example.test/no-label-key" }
              ] }
            ]
          }
        }
        """);

        var links = Assert.Single(res.Sections).Links;
        Assert.Single(links);
        Assert.Equal("Good", links[0].Label);
    }

    [Fact]
    public void A_section_with_no_title_and_no_links_is_dropped()
    {
        var res = Load("""
        {
          "resources": {
            "sections": [
              { "title": "", "links": [] },
              { "title": "Kept", "links": [
                { "label": "L", "url": "https://example.test/l" } ] }
            ]
          }
        }
        """);

        var kept = Assert.Single(res.Sections);
        Assert.Equal("Kept", kept.Title);
    }

    [Fact]
    public void A_resources_block_with_only_empty_links_reports_empty()
    {
        var res = Load("""
        {
          "resources": {
            "intro": "   ",
            "sections": [ { "title": "", "links": [ { "label": "x", "url": "" } ] } ]
          }
        }
        """);

        Assert.True(res.IsEmpty);
    }
}
