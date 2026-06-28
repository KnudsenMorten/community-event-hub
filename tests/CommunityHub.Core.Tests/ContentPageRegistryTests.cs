using System.Linq;
using CommunityHub.Core.Content;
using CommunityHub.Core.Domain;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Tests for <see cref="ContentPageRegistry"/> (REQUIREMENTS §104–§123) — the
/// single source of truth the generic <c>/Info/{slug}</c> page + the nav use for
/// content-page titles and §123 role-scoping. Proving the slug set, the
/// all-roles vs speakers-only gating, the organizer-sees-everything rule, and
/// the unknown-slug 404 behaviour here covers the gate; the thin Razor page that
/// consumes it just maps the verdict to NotFound / RedirectToPage.
/// </summary>
public sealed class ContentPageRegistryTests
{
    // §123 audience buckets.
    private static readonly string[] AllRoleSlugs =
        { "wayfinding", "good-to-know", "addresses", "last-event-videos" };

    private static readonly string[] SpeakerSlugs =
    {
        "speaker-template", "session-guidelines", "av-stage-timer",
        "session-preview-final", "session-feedback", "session-evaluations",
        "help-promote",
    };

    private static readonly ParticipantRole[] NonOrganizerNonSpeakerRoles =
    {
        ParticipantRole.Volunteer, ParticipantRole.Sponsor,
        ParticipantRole.Attendee, ParticipantRole.Media,
        ParticipantRole.EventPartner,
    };

    [Fact]
    public void Registry_contains_exactly_the_expected_slugs()
    {
        var expected = AllRoleSlugs.Concat(SpeakerSlugs).OrderBy(s => s);
        var actual = ContentPageRegistry.All.Select(p => p.Slug).OrderBy(s => s);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Every_page_has_a_title_and_a_menu_section()
    {
        foreach (var page in ContentPageRegistry.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(page.Title), $"{page.Slug} title");
            Assert.False(string.IsNullOrWhiteSpace(page.MenuSection), $"{page.Slug} section");
        }
    }

    [Fact]
    public void Get_is_case_insensitive_and_trims()
    {
        Assert.NotNull(ContentPageRegistry.Get("Wayfinding"));
        Assert.NotNull(ContentPageRegistry.Get("  wayfinding  "));
        Assert.Equal("Wayfinding – conference venue", ContentPageRegistry.Get("wayfinding")!.Title);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("does-not-exist")]
    public void Unknown_slug_is_not_found_and_never_accessible(string? slug)
    {
        Assert.Null(ContentPageRegistry.Get(slug));
        Assert.False(ContentPageRegistry.Exists(slug));
        // Even an organizer cannot access an unregistered slug.
        Assert.False(ContentPageRegistry.CanAccess(slug, ParticipantRole.Organizer));
    }

    [Theory]
    [MemberData(nameof(AllRoleSlugData))]
    public void All_role_pages_are_visible_to_every_role(string slug)
    {
        foreach (ParticipantRole role in System.Enum.GetValues<ParticipantRole>())
        {
            Assert.True(ContentPageRegistry.CanAccess(slug, role),
                $"{slug} should be visible to {role}");
        }
    }

    [Theory]
    [MemberData(nameof(SpeakerSlugData))]
    public void Speaker_pages_are_visible_to_speakers_and_organizers(string slug)
    {
        Assert.True(ContentPageRegistry.CanAccess(slug, ParticipantRole.Speaker));
        Assert.True(ContentPageRegistry.CanAccess(slug, ParticipantRole.Organizer));
    }

    [Theory]
    [MemberData(nameof(SpeakerSlugData))]
    public void Speaker_pages_are_hidden_from_non_speaker_roles(string slug)
    {
        foreach (var role in NonOrganizerNonSpeakerRoles)
        {
            Assert.False(ContentPageRegistry.CanAccess(slug, role),
                $"{slug} must be blocked for {role}");
        }
    }

    [Fact]
    public void ForRole_attendee_sees_only_all_role_pages()
    {
        var slugs = ContentPageRegistry.ForRole(ParticipantRole.Attendee)
            .Select(p => p.Slug).OrderBy(s => s);
        Assert.Equal(AllRoleSlugs.OrderBy(s => s), slugs);
    }

    [Fact]
    public void ForRole_speaker_sees_all_role_and_speaker_pages()
    {
        var slugs = ContentPageRegistry.ForRole(ParticipantRole.Speaker)
            .Select(p => p.Slug).OrderBy(s => s);
        Assert.Equal(AllRoleSlugs.Concat(SpeakerSlugs).OrderBy(s => s), slugs);
    }

    [Fact]
    public void ForRole_organizer_sees_every_page()
    {
        Assert.Equal(
            ContentPageRegistry.All.Count,
            ContentPageRegistry.ForRole(ParticipantRole.Organizer).Count);
    }

    [Theory]
    // The SPEAKER-only `{slug}-speaker.md` supplements (e.g. the speaker hotel on the Addresses
    // page) must NOT be registered as standalone pages — otherwise /Info/{slug}-speaker would be
    // directly browsable. They render ONLY as a role-gated supplement inside their parent page.
    [InlineData("addresses-speaker")]
    public void Speaker_supplement_slugs_are_not_directly_browsable_pages(string supplementSlug)
    {
        Assert.False(ContentPageRegistry.Exists(supplementSlug));
        Assert.Null(ContentPageRegistry.Get(supplementSlug));
        foreach (ParticipantRole role in System.Enum.GetValues<ParticipantRole>())
            Assert.False(ContentPageRegistry.CanAccess(supplementSlug, role),
                $"{role} must not be able to open the supplement '{supplementSlug}' as a page.");
    }

    public static TheoryData<string> AllRoleSlugData()
    {
        var data = new TheoryData<string>();
        foreach (var s in AllRoleSlugs) data.Add(s);
        return data;
    }

    public static TheoryData<string> SpeakerSlugData()
    {
        var data = new TheoryData<string>();
        foreach (var s in SpeakerSlugs) data.Add(s);
        return data;
    }
}
