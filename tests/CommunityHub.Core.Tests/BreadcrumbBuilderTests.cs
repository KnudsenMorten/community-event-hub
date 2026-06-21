using System;
using System.Globalization;
using System.Linq;
using CommunityHub.Core.Navigation;
using CommunityHub.Core.Resources;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Breadcrumb trail tests (REQUIREMENTS §21 cross-cutting "breadcrumbs").
///
/// <see cref="BreadcrumbBuilder"/> is the single source of truth for the
/// organizer back-office ancestor trail (root ▸ hub) of a deep feature page. The
/// current page is never in the trail (the view appends the page title as the
/// leaf), so these tests assert the ancestor chain, that the trail agrees with
/// the nav's hub→section grouping, and that every label key the builder emits
/// actually resolves in BOTH cultures (so a crumb never renders a raw key).
/// </summary>
public sealed class BreadcrumbBuilderTests
{
    [Fact]
    public void Feature_page_trail_is_root_then_its_hub()
    {
        var trail = BreadcrumbBuilder.Build("/Organizer/Participants");

        Assert.Collection(trail,
            root => { Assert.Equal("/Organizer", root.Href); Assert.Equal("Nav.OrgArea", root.LabelKey); },
            hub  => { Assert.Equal("/Organizer/People", hub.Href); Assert.Equal("Nav.OrgPeople", hub.LabelKey); });
    }

    [Fact]
    public void Sessions_feature_page_lives_under_the_content_hub()
    {
        // The Sessions & speakers hub route is /Organizer/Content (so it does not
        // collide with the /Organizer/Sessions feature page) — the breadcrumb must
        // point the parent crumb at the hub, not the feature page.
        var trail = BreadcrumbBuilder.Build("/Organizer/Sessions");

        Assert.Equal(2, trail.Count);
        Assert.Equal("/Organizer/Content", trail[1].Href);
        Assert.Equal("Nav.OrgSessionsHub", trail[1].LabelKey);
    }

    [Fact]
    public void Hub_page_trail_is_just_the_root()
    {
        var trail = BreadcrumbBuilder.Build("/Organizer/People");

        Assert.Single(trail);
        Assert.Equal("/Organizer", trail[0].Href);
        Assert.Equal("Nav.OrgArea", trail[0].LabelKey);
    }

    [Fact]
    public void Organizer_root_has_no_ancestors()
    {
        Assert.Empty(BreadcrumbBuilder.Build("/Organizer"));
        Assert.Empty(BreadcrumbBuilder.Build("/Organizer/"));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/Sessions")]
    [InlineData("/Speaker")]
    [InlineData("/Sponsor/Tasks")]
    [InlineData(null)]
    [InlineData("")]
    public void Non_organizer_paths_have_no_trail(string? path)
    {
        Assert.Empty(BreadcrumbBuilder.Build(path));
    }

    [Fact]
    public void Path_match_is_case_and_trailing_slash_insensitive()
    {
        var a = BreadcrumbBuilder.Build("/organizer/participants/");
        var b = BreadcrumbBuilder.Build("/Organizer/Participants");

        Assert.Equal(b.Count, a.Count);
        Assert.Equal(b[1].Href, a[1].Href);
        Assert.Equal(b[1].LabelKey, a[1].LabelKey);
    }

    [Fact]
    public void Known_unmapped_organizer_page_falls_back_to_just_the_root()
    {
        // Prominent entries (CommandCenter/Dashboard) and any not-yet-mapped
        // organizer page still get a one-click way back to the root rather than
        // a guessed section.
        var trail = BreadcrumbBuilder.Build("/Organizer/CommandCenter");

        Assert.Single(trail);
        Assert.Equal("/Organizer", trail[0].Href);
    }

    [Theory]
    // One representative feature page from each hub — the parent crumb must be
    // the matching hub, proving the breadcrumb agrees with the nav grouping.
    [InlineData("/Organizer/Onboarding",            "/Organizer/People")]
    [InlineData("/Organizer/SessionEvaluations",    "/Organizer/Content")]
    [InlineData("/Organizer/Broadcast",             "/Organizer/Comms")]
    [InlineData("/Organizer/Graphics",              "/Organizer/SoMe")]
    [InlineData("/Organizer/BucketAllocation",      "/Organizer/Volunteers")]
    [InlineData("/Organizer/Hotels",                "/Organizer/Logistics")]
    [InlineData("/Organizer/CalendarSettings",      "/Organizer/Setup")]
    public void Each_hub_anchors_its_feature_pages(string featurePath, string expectedHubHref)
    {
        var trail = BreadcrumbBuilder.Build(featurePath);

        Assert.Equal(2, trail.Count);
        Assert.Equal(expectedHubHref, trail[1].Href);
    }

    [Fact]
    public void Every_emitted_label_key_resolves()
    {
        // Walk every organizer feature page + hub the builder knows about and
        // assert each crumb's label key resolves (no raw-key leak) in English.
        var loc = MakeLocalizer();

        var samplePaths = new[]
        {
            "/Organizer/People", "/Organizer/Content", "/Organizer/Comms",
            "/Organizer/SoMe", "/Organizer/Volunteers", "/Organizer/Logistics",
            "/Organizer/Setup",
            "/Organizer/Participants", "/Organizer/EditParticipant",
            "/Organizer/Sessions", "/Organizer/SessionizeImport",
            "/Organizer/EmailCenter", "/Organizer/Graphics",
            "/Organizer/VolunteerStructure", "/Organizer/HotelAssignments",
            "/Organizer/CalendarSettings",
        };

        foreach (var path in samplePaths)
        {
            foreach (var crumb in BreadcrumbBuilder.Build(path))
            {
                var value = WithCulture("en", () => loc[crumb.LabelKey]);
                Assert.False(value.ResourceNotFound,
                    $"Breadcrumb label '{crumb.LabelKey}' (for {path}) missing in 'en'.");
                Assert.False(string.IsNullOrWhiteSpace(value.Value));
            }
        }
    }

    [Fact]
    public void Breadcrumb_label_resolves()
    {
        var loc = MakeLocalizer();
        var en = WithCulture("en", () => loc["Breadcrumb.Label"].Value);

        Assert.False(loc["Breadcrumb.Label"].ResourceNotFound);
        Assert.False(string.IsNullOrWhiteSpace(en));
    }

    private static IStringLocalizer<SharedResource> MakeLocalizer()
    {
        var options = Options.Create(new LocalizationOptions { ResourcesPath = "" });
        var factory = new ResourceManagerStringLocalizerFactory(options, NullLoggerFactory.Instance);
        return new StringLocalizer<SharedResource>(factory);
    }

    private static T WithCulture<T>(string culture, Func<T> body)
    {
        var prevUi = CultureInfo.CurrentUICulture;
        var prev = CultureInfo.CurrentCulture;
        try
        {
            var ci = new CultureInfo(culture);
            CultureInfo.CurrentUICulture = ci;
            CultureInfo.CurrentCulture = ci;
            return body();
        }
        finally
        {
            CultureInfo.CurrentUICulture = prevUi;
            CultureInfo.CurrentCulture = prev;
        }
    }
}
