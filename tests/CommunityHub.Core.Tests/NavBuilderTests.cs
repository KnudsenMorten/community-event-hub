using System;
using System.Collections.Generic;
using System.Linq;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Navigation;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Nav grouping + role-gating tests (REQUIREMENTS §11 nav/IA restructure,
/// phase-1 organizer-menu consolidation: ~35 flat links → ~8 grouped hubs + the
/// prominent Dashboard + the standalone Audit log + the Organizer home root).
///
/// The nav is split into two clear, role-gated groups by
/// <see cref="NavBuilder"/>:
///   1. the participant / "My event" group — always visible;
///   2. the organizer / "Organizer area" management group — composed ONLY for
///      <see cref="ParticipantRole.Organizer"/>, now as a short list of hub
///      entries rather than one flat link per feature page.
///
/// These assert the *server-side* gate (a non-organizer's NavModel never even
/// contains the management items), the consolidation (≈8 hubs, not 35 links),
/// that every existing feature page is reachable from some hub, and that
/// action-only pages stay OUT of the nav (reached via in-page buttons).
/// </summary>
public sealed class NavBuilderTests
{
    /// <summary>
    /// The consolidated top-level management entries (phase-1). This is the WHOLE
    /// management menu — ~8 hubs plus Dashboard, the Organizer home and Audit.
    /// </summary>
    private static readonly string[] ManagementTopLevelRoutes =
    {
        "/Organizer",                    // Organizer home / landing root
        "/Organizer/CommandCenter",      // prominent "what needs my attention" overview
        "/Organizer/Dashboard",          // prominent dashboard (Overview folded in)
        "/Organizer/FindPerson",         // prominent global "find a person" search
        "/Organizer/People",             // hub
        "/Organizer/Comms",              // hub
        "/Organizer/Content",            // Sessions & speakers hub
        "/Organizer/SponsorAdmin/Index", // existing Sponsors hub
        "/Organizer/Volunteers",         // hub
        "/Organizer/Logistics",          // hub
        "/Organizer/SoMe",               // Marketing / SoMe hub
        "/Organizer/Setup",              // hub
        "/Organizer/ImpersonationLog",   // standalone Audit
    };

    /// <summary>
    /// Every organizer FEATURE page (the deep-link routes that used to be flat in
    /// the menu) must still be reachable — now via a hub. Phase-1 fans these out
    /// from the hub card pages, so they are no longer top-level nav entries, but
    /// the routes are preserved and the test guarantees a hub fronts each one.
    /// Maps feature route → the hub that links to it.
    /// </summary>
    private static readonly Dictionary<string, string> FeaturePageToHub = new(StringComparer.OrdinalIgnoreCase)
    {
        // People hub.
        ["/Organizer/Participants"]               = "/Organizer/People",
        ["/Organizer/PreselectionQueue"]          = "/Organizer/People",
        ["/Organizer/Onboarding"]                 = "/Organizer/People",
        ["/Organizer/Attendees"]                  = "/Organizer/People",
        ["/Organizer/ActionQueue"]                = "/Organizer/People",
        // Comms hub.
        ["/Organizer/EmailCenter"]                = "/Organizer/Comms",
        ["/Organizer/EmailLog"]                   = "/Organizer/Comms",
        ["/Organizer/Broadcast"]                  = "/Organizer/Comms",
        ["/Organizer/SendInvitations"]            = "/Organizer/Comms",
        ["/Organizer/SendWelcomeLogin"]           = "/Organizer/Comms",
        ["/Organizer/SpeakerReminders"]           = "/Organizer/Comms",
        // Sessions & speakers hub.
        ["/Organizer/Speakers"]                   = "/Organizer/Content",
        ["/Organizer/Sessions"]                   = "/Organizer/Content",
        ["/Organizer/SessionQuestions"]           = "/Organizer/Content",
        ["/Organizer/SessionEvaluations"]         = "/Organizer/Content",
        ["/Organizer/SessionizeImport"]           = "/Organizer/Content",
        ["/Organizer/SessionizeEndpointSettings"] = "/Organizer/Content",
        // Sponsors hub (the existing SponsorAdmin landing).
        ["/Organizer/Sponsors"]                   = "/Organizer/SponsorAdmin/Index",
        ["/Organizer/SponsorAdmin/Tasks"]         = "/Organizer/SponsorAdmin/Index",
        ["/Organizer/SponsorAdmin/Leads"]         = "/Organizer/SponsorAdmin/Index",
        ["/Organizer/SponsorAdmin/Dashboard"]     = "/Organizer/SponsorAdmin/Index",
        ["/Organizer/AppGame"]                    = "/Organizer/SponsorAdmin/Index",
        // Volunteers hub.
        ["/Organizer/VolunteerStructure"]         = "/Organizer/Volunteers",
        ["/Organizer/BucketAllocation"]           = "/Organizer/Volunteers",
        // Logistics hub.
        ["/Organizer/Hotels"]                     = "/Organizer/Logistics",
        ["/Organizer/HotelAssignments"]           = "/Organizer/Logistics",
        ["/Organizer/Swag"]                       = "/Organizer/Logistics",
        ["/Organizer/TravelReimbursements"]       = "/Organizer/Logistics",
        ["/Organizer/Lunch"]                      = "/Organizer/Logistics",
        ["/Organizer/GroupPhotos"]                = "/Organizer/Logistics",
        // Marketing / SoMe hub.
        ["/Organizer/Graphics"]                   = "/Organizer/SoMe",
        ["/Organizer/SoMeQueue"]                  = "/Organizer/SoMe",
        ["/Organizer/SoMeSettings"]               = "/Organizer/SoMe",
        ["/Organizer/AssetLocations"]             = "/Organizer/SoMe",
        // Setup hub (surfaces previously-hidden CalendarSettings).
        ["/Organizer/CalendarSettings"]           = "/Organizer/Setup",
        // Prominent dashboard + cross-role overview (Overview folded into it).
        ["/Organizer/Overview"]                   = "/Organizer/Dashboard",
    };

    /// <summary>
    /// Action-only pages reachable via in-page buttons — must NEVER be a nav
    /// entry (neither top-level nor surfaced as a hub itself).
    /// </summary>
    private static readonly string[] ActionOnlyRoutes =
    {
        "/Organizer/EditParticipant",
        "/Organizer/EditOnBehalf",
        "/Organizer/SecureLink",
        "/Organizer/ReturnToOrganizer",
        "/Organizer/DataGrid",
        "/Organizer/TasksTable",
    };

    private static readonly ParticipantRole[] NonOrganizerRoles =
    {
        ParticipantRole.Speaker,
        ParticipantRole.MasterclassSpeaker,
        ParticipantRole.Volunteer,
        ParticipantRole.Sponsor,
        ParticipantRole.Attendee,
        ParticipantRole.Video,
        ParticipantRole.Camera,
    };

    [Theory]
    [MemberData(nameof(NonOrganizerRoleData))]
    public void Non_organizer_never_receives_the_management_group(ParticipantRole role)
    {
        var nav = NavBuilder.Build(role);

        Assert.False(nav.HasManagement, $"{role} must not get the management group.");
        Assert.Null(nav.ManagementGroup);
        Assert.DoesNotContain(nav.Groups, g => g.IsManagement);
    }

    [Theory]
    [MemberData(nameof(NonOrganizerRoleData))]
    public void Non_organizer_nav_contains_no_management_route(ParticipantRole role)
    {
        var nav = NavBuilder.Build(role);

        // Belt-and-braces: nothing under the /Organizer area leaks to a
        // non-organizer at all — not a hub, not a feature page.
        Assert.DoesNotContain(nav.AllItems,
            i => i.Href.StartsWith("/Organizer", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Organizer_receives_the_consolidated_management_group()
    {
        var nav = NavBuilder.Build(ParticipantRole.Organizer);

        Assert.True(nav.HasManagement);
        var mgmt = nav.ManagementGroup!;
        Assert.Equal("Nav.OrgArea", mgmt.HeadingKey);

        var hrefs = mgmt.Items.Select(i => i.Href).ToList();

        // It is exactly the consolidated top-level set — same membership, no extras.
        Assert.Equal(
            ManagementTopLevelRoutes.OrderBy(x => x, StringComparer.OrdinalIgnoreCase),
            hrefs.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Consolidation_keeps_the_menu_short()
    {
        var mgmt = NavBuilder.Build(ParticipantRole.Organizer).ManagementGroup!;

        // The whole point of phase-1: the old ~35-link flat menu becomes a short
        // grouped one. Assert it is meaningfully consolidated (well under 15) —
        // ~8 hubs plus the home root, Command center, Dashboard and Audit.
        Assert.True(mgmt.Items.Count <= 13,
            $"Management menu should be consolidated to ~8 hubs (+home/command-center/dashboard/audit); got {mgmt.Items.Count}.");

        // The 8 consolidation hubs are all present.
        var hrefs = mgmt.Items.Select(i => i.Href).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var hub in new[]
                 {
                     "/Organizer/People", "/Organizer/Comms", "/Organizer/Content",
                     "/Organizer/SponsorAdmin/Index", "/Organizer/Volunteers",
                     "/Organizer/Logistics", "/Organizer/SoMe", "/Organizer/Setup",
                 })
        {
            Assert.Contains(hub, hrefs);
        }

        // Command center + Dashboard stay prominent; Audit stays standalone;
        // home root present.
        Assert.Contains("/Organizer", hrefs);
        Assert.Contains("/Organizer/CommandCenter", hrefs);
        Assert.Contains("/Organizer/Dashboard", hrefs);
        Assert.Contains("/Organizer/ImpersonationLog", hrefs);
    }

    [Fact]
    public void Every_feature_page_is_reachable_via_a_hub_in_the_top_level_menu()
    {
        var topLevel = NavBuilder.Build(ParticipantRole.Organizer)
            .ManagementGroup!.Items
            .Select(i => i.Href)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // For each feature page, the hub that fronts it must itself be a
        // top-level menu entry — so every feature is reachable in two clicks.
        foreach (var (feature, hub) in FeaturePageToHub)
        {
            Assert.True(topLevel.Contains(hub),
                $"Feature {feature} is fronted by hub {hub}, which must be a top-level menu entry.");
        }
    }

    [Fact]
    public void Action_only_pages_are_absent_from_the_nav()
    {
        var allHrefs = NavBuilder.Build(ParticipantRole.Organizer)
            .AllItems.Select(i => i.Href).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var route in ActionOnlyRoutes)
        {
            Assert.DoesNotContain(route, allHrefs);
        }
    }

    [Fact]
    public void Organizer_management_items_all_live_under_the_organizer_area()
    {
        var mgmt = NavBuilder.Build(ParticipantRole.Organizer).ManagementGroup!;

        // Every management entry is an /Organizer route — none scattered elsewhere.
        Assert.All(mgmt.Items,
            i => Assert.StartsWith("/Organizer", i.Href, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void First_group_is_the_always_visible_participant_group()
    {
        foreach (var role in Enum.GetValues<ParticipantRole>())
        {
            var nav = NavBuilder.Build(role);
            Assert.NotEmpty(nav.Groups);
            var first = nav.Groups[0];
            Assert.False(first.IsManagement);
            // Home is the universal first entry for every role.
            Assert.Equal("/", first.Items[0].Href);
        }
    }

    [Theory]
    [InlineData(ParticipantRole.Organizer)]
    [InlineData(ParticipantRole.Speaker)]
    [InlineData(ParticipantRole.Volunteer)]
    [InlineData(ParticipantRole.Sponsor)]
    [InlineData(ParticipantRole.Attendee)]
    public void Every_role_keeps_the_evergreen_participant_entries(ParticipantRole role)
    {
        var participant = NavBuilder.Build(role).Groups[0];
        var hrefs = participant.Items.Select(i => i.Href).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("/", hrefs);
        Assert.Contains("/Profile", hrefs);
        Assert.Contains("/Resources", hrefs);
        Assert.Contains("/Sessions", hrefs);
    }

    [Fact]
    public void Participant_form_routes_are_preserved_per_role()
    {
        // Spot-check that the regroup did not drop or rename any participant route
        // (constraint: relocate only, never remove/rename a route).
        var speaker = NavBuilder.Build(ParticipantRole.Speaker).AllItems.Select(i => i.Href).ToList();
        Assert.Contains("/Forms/Hotel", speaker);
        Assert.Contains("/Forms/Dinner", speaker);
        Assert.Contains("/Speaker", speaker);
        Assert.Contains("/Speaker/Questions", speaker);
        Assert.Contains("/Forms/Speaker", speaker);
        Assert.Contains("/Speaker/Graphics", speaker);
        Assert.Contains("/Forms/Travel", speaker);
        Assert.Contains("/Forms/Lunch", speaker);
        Assert.Contains("/Forms/Swag", speaker);

        var volunteer = NavBuilder.Build(ParticipantRole.Volunteer).AllItems.Select(i => i.Href).ToList();
        Assert.Contains("/Forms/VolunteerWizard", volunteer);

        var sponsor = NavBuilder.Build(ParticipantRole.Sponsor).AllItems.Select(i => i.Href).ToList();
        Assert.Contains("/Sponsor/Portal", sponsor);   // single self-service home (REQUIREMENTS §20)
        Assert.Contains("/Sponsor", sponsor);
        Assert.Contains("/Sponsor/Tasks", sponsor);
        Assert.Contains("/Sponsor/Logistics", sponsor);
        Assert.Contains("/Sponsor/Contact", sponsor);
        // A sponsor uses the company-shared tasks entry, not the generic /Tasks.
        Assert.DoesNotContain("/Tasks", sponsor);

        var attendee = NavBuilder.Build(ParticipantRole.Attendee).AllItems.Select(i => i.Href).ToList();
        Assert.Contains("/Attendee", attendee);
    }

    // ---- REQUIREMENTS §21 "Group the organizer nav" -------------------------
    // The flat management menu is now bucketed into named, collapsible sub-groups
    // (People / Sessions / Comms / Sponsors / Volunteers / Logistics) via
    // NavItem.SectionKey + NavGroup.Sections(). These assert the grouping is pure
    // information architecture: every prior link is still present, nothing is
    // dropped or duplicated, and the six named groups exist.

    /// <summary>The six named organizer-nav section heading keys (REQUIREMENTS §21).</summary>
    private static readonly string[] ExpectedSectionKeys =
    {
        "Nav.OrgSectionPeople",
        "Nav.OrgSectionSessions",
        "Nav.OrgSectionComms",
        "Nav.OrgSectionSponsors",
        "Nav.OrgSectionVolunteers",
        "Nav.OrgSectionLogistics",
    };

    [Fact]
    public void Grouping_preserves_every_management_link_exactly_once()
    {
        var mgmt = NavBuilder.Build(ParticipantRole.Organizer).ManagementGroup!;

        // The flattened section view must equal the flat Items list — no link
        // added, dropped, re-ordered away, or duplicated by the grouping.
        var flat = mgmt.Items.Select(i => i.Href).ToList();
        var viaSections = mgmt.Sections().SelectMany(s => s.Items).Select(i => i.Href).ToList();

        Assert.Equal(flat, viaSections);
        // And the consolidated top-level set is still exactly the prior set.
        Assert.Equal(
            ManagementTopLevelRoutes.OrderBy(x => x, StringComparer.OrdinalIgnoreCase),
            viaSections.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Menu_has_the_six_named_groups_plus_a_leading_prominent_bucket()
    {
        var sections = NavBuilder.Build(ParticipantRole.Organizer).ManagementGroup!.Sections();

        // The very first section is the prominent, ungrouped bucket (no heading):
        // Organizer home, Command center, Dashboard.
        var prominent = sections[0];
        Assert.Null(prominent.HeadingKey);
        Assert.Equal(
            new[] { "/Organizer", "/Organizer/CommandCenter", "/Organizer/Dashboard", "/Organizer/FindPerson" },
            prominent.Items.Select(i => i.Href).ToArray());

        // The remaining sections are exactly the six named groups, in order.
        var named = sections.Skip(1).Select(s => s.HeadingKey).ToArray();
        Assert.Equal(ExpectedSectionKeys, named);
    }

    [Fact]
    public void Every_named_group_is_non_empty_and_each_named_item_carries_a_section()
    {
        var mgmt = NavBuilder.Build(ParticipantRole.Organizer).ManagementGroup!;

        foreach (var section in mgmt.Sections().Where(s => s.HeadingKey is not null))
        {
            Assert.NotEmpty(section.Items);
            // Every item in a named section actually declares that section key.
            Assert.All(section.Items, i => Assert.Equal(section.HeadingKey, i.SectionKey));
        }

        // Exactly the three prominent entries are section-less; everything else is grouped.
        var ungrouped = mgmt.Items.Where(i => i.SectionKey is null).Select(i => i.Href).ToArray();
        Assert.Equal(
            new[] { "/Organizer", "/Organizer/CommandCenter", "/Organizer/Dashboard", "/Organizer/FindPerson" },
            ungrouped);
    }

    [Fact]
    public void Each_consolidation_hub_lands_in_the_expected_named_group()
    {
        var mgmt = NavBuilder.Build(ParticipantRole.Organizer).ManagementGroup!;
        var section = mgmt.Items.ToDictionary(i => i.Href, i => i.SectionKey, StringComparer.OrdinalIgnoreCase);

        Assert.Equal("Nav.OrgSectionPeople",     section["/Organizer/People"]);
        Assert.Equal("Nav.OrgSectionSessions",   section["/Organizer/Content"]);
        Assert.Equal("Nav.OrgSectionComms",      section["/Organizer/Comms"]);
        Assert.Equal("Nav.OrgSectionComms",      section["/Organizer/SoMe"]);
        Assert.Equal("Nav.OrgSectionSponsors",   section["/Organizer/SponsorAdmin/Index"]);
        Assert.Equal("Nav.OrgSectionVolunteers", section["/Organizer/Volunteers"]);
        Assert.Equal("Nav.OrgSectionLogistics",  section["/Organizer/Logistics"]);
        Assert.Equal("Nav.OrgSectionLogistics",  section["/Organizer/Setup"]);
        Assert.Equal("Nav.OrgSectionLogistics",  section["/Organizer/ImpersonationLog"]);
    }

    public static TheoryData<ParticipantRole> NonOrganizerRoleData()
    {
        var data = new TheoryData<ParticipantRole>();
        foreach (var r in NonOrganizerRoles) data.Add(r);
        return data;
    }
}
