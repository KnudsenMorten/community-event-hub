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
        // §167: /Organizer/FindPerson dropped from the nav (Participants already searches).
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
        ["/Organizer/WelcomeLinks"]               = "/Organizer/People",
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
        ["/Organizer/SessionSource"]              = "/Organizer/Content",
        ["/Organizer/MasterClasses"]              = "/Organizer/Content",
        ["/Organizer/Surveys"]                    = "/Organizer/Content",
        // Sponsors hub (the existing SponsorAdmin landing).
        ["/Organizer/Sponsors"]                   = "/Organizer/SponsorAdmin/Index",
        ["/Organizer/SponsorAdmin/Tasks"]         = "/Organizer/SponsorAdmin/Index",
        ["/Organizer/SponsorAdmin/Leads"]         = "/Organizer/SponsorAdmin/Index",
        ["/Organizer/SponsorAdmin/Dashboard"]     = "/Organizer/SponsorAdmin/Index",
        ["/Organizer/AppGame"]                    = "/Organizer/SponsorAdmin/Index",
        ["/Organizer/EconomicContacts"]           = "/Organizer/SponsorAdmin/Index",
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
        ["/Organizer/Schedule"]                   = "/Organizer/Logistics",
        ["/Organizer/PartyRsvps"]                 = "/Organizer/Logistics",
        // Marketing / SoMe hub.
        ["/Organizer/Graphics"]                   = "/Organizer/SoMe",
        ["/Organizer/SoMeQueue"]                  = "/Organizer/SoMe",
        ["/Organizer/SoMeSettings"]               = "/Organizer/SoMe",
        ["/Organizer/AssetLocations"]             = "/Organizer/SoMe",
        // Setup hub (surfaces previously-hidden CalendarSettings).
        ["/Organizer/CalendarSettings"]           = "/Organizer/Setup",
        // Feature settings / controlled-rollout surface (§23) lives under Setup.
        ["/Organizer/Settings"]                   = "/Organizer/Setup",
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
        ParticipantRole.Volunteer,
        ParticipantRole.Sponsor,
        ParticipantRole.Attendee,
        ParticipantRole.Media,
        ParticipantRole.EventPartner,
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
    public void Organizer_receives_the_management_group()
    {
        var nav = NavBuilder.Build(ParticipantRole.Organizer);

        Assert.True(nav.HasManagement);
        var mgmt = nav.ManagementGroup!;
        Assert.Equal("Nav.OrgArea", mgmt.HeadingKey);

        var hrefs = mgmt.Items.Select(i => i.Href).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The prominent entries, every section hub lead, and the audit log are all
        // present (the menu also lists the individual feature pages — see
        // Every_feature_page_is_a_direct_nav_item).
        foreach (var route in ManagementTopLevelRoutes)
        {
            Assert.Contains(route, hrefs);
        }
    }

    [Fact]
    public void Management_menu_is_lean_hub_level()
    {
        var mgmt = NavBuilder.Build(ParticipantRole.Organizer).ManagementGroup!;
        var hrefs = mgmt.Items.Select(i => i.Href).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Operator 2026-06-21: the menu is a SHORT flat list — prominent overview
        // entries + the 8 hub landings + the audit log. Feature pages are NOT in the
        // menu; they live on the hub button-grids (see _HubGrid). So the menu is
        // small and carries no per-feature rows or collapsible sub-folds.
        Assert.True(mgmt.Items.Count <= 14,
            $"Lean organizer menu should be ~13 hub-level entries; got {mgmt.Items.Count}.");
        Assert.All(mgmt.Items, i => Assert.Null(i.SectionKey));   // flat — no sub-folds

        foreach (var hub in new[]
                 {
                     "/Organizer/People", "/Organizer/Content", "/Organizer/Comms",
                     "/Organizer/SoMe", "/Organizer/SponsorAdmin/Index",
                     "/Organizer/Volunteers", "/Organizer/Logistics", "/Organizer/Setup",
                 })
        {
            Assert.Contains(hub, hrefs);
        }
        Assert.Contains("/Organizer", hrefs);
        Assert.Contains("/Organizer/CommandCenter", hrefs);
        Assert.Contains("/Organizer/Dashboard", hrefs);
        Assert.Contains("/Organizer/ImpersonationLog", hrefs);

        // Feature pages are reached via their hub grid, NOT the menu.
        Assert.DoesNotContain("/Organizer/Participants", hrefs);
        Assert.DoesNotContain("/Organizer/Hotels", hrefs);
    }

    [Fact]
    public void Every_feature_pages_hub_is_in_the_menu()
    {
        var hrefs = NavBuilder.Build(ParticipantRole.Organizer)
            .ManagementGroup!.Items
            .Select(i => i.Href)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The menu is hub-level: every feature page's HUB must be a menu entry, so
        // each feature is reachable in two clicks (menu hub -> hub-grid tile).
        foreach (var (feature, hub) in FeaturePageToHub)
        {
            Assert.True(hrefs.Contains(hub),
                $"Feature {feature} is reached via hub {hub}, which must be a menu entry.");
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

        // Every INTERNAL management entry is an /Organizer route — none scattered
        // elsewhere. (External/standalone links like the public attendee-telemetry page
        // open in a new tab and are exempt — operator 2026-06-25.)
        Assert.All(mgmt.Items.Where(i => !i.External),
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
    public void Organizer_keeps_the_evergreen_participant_entries(ParticipantRole role)
    {
        // Only the organizer (and media crew) keep the full evergreen set now —
        // attendee/speaker/sponsor/volunteer menus were trimmed (operator 2026-06-21):
        // attendee is minimal; speaker/sponsor/volunteer drop Resources; sponsor/
        // volunteer also drop the public Sessions list.
        var participant = NavBuilder.Build(role).Groups[0];
        var hrefs = participant.Items.Select(i => i.Href).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("/", hrefs);
        Assert.Contains("/Profile", hrefs);
        Assert.Contains("/Resources", hrefs);
        Assert.Contains("/Sessions", hrefs);
    }

    [Fact]
    public void Speaker_menu_matches_redesign()
    {
        // §138: the Master Class Q&A item is gated on speakerHasMasterClass; build a
        // speaker WITH a master class so the rest of the menu (and that item) is present.
        var g = NavBuilder.Build(ParticipantRole.Speaker, speakerHasMasterClass: true).Groups[0];
        var hrefs = g.Items.Select(i => i.Href).ToList();

        // operator 2026-06-24 (§26c): Home, My Hub Profile, Speaker Details, My sessions,
        // My tasks, Help Promote, Event-logistics fold-out, Contact.
        // (Calendar entries removed: the user-facing calendar UI is retired.)
        Assert.Equal("Nav.MyProfile", g.Items.Single(i => i.Href == "/Profile").LabelKey);
        Assert.Equal("Nav.SpeakerDetails", g.Items.Single(i => i.Href == "/Speaker/Details").LabelKey);
        Assert.Equal("Nav.MySessions", g.Items.Single(i => i.Href == "/Speaker").LabelKey);
        // §86/§138: the speaker Master Class Q&A area is a top-level entry (master-class speaker only).
        Assert.Equal("Nav.MasterClassQa", g.Items.Single(i => i.Href == "/Speaker/Questions").LabelKey);
        Assert.Equal("Nav.HelpPromote", g.Items.Single(i => i.Href == "/Speaker/Graphics").LabelKey);
        Assert.DoesNotContain("/Forms/Speaker", hrefs);   // Bio replaced by Speaker Details
        Assert.Contains("/Contact", hrefs);

        // The calendar UI is retired: /Calendar no longer appears in the speaker menu.
        Assert.DoesNotContain("/Calendar", hrefs);
        // §138: the standalone "Am I ready?" item is removed from the speaker menu (the
        // readiness rollup moved to the top of My Tasks).
        Assert.DoesNotContain("/Speaker/Readiness", hrefs);

        var logistics = g.Sections().SingleOrDefault(s => s.HeadingKey == "Nav.SectionEventLogistics");
        Assert.NotNull(logistics);
        var lh = logistics!.Items.Select(i => i.Href).ToList();
        // The entitlement forms lead the Event-logistics fold-out, in order...
        Assert.Equal(new[] { "/Forms/Hotel", "/Forms/Dinner", "/Forms/Lunch", "/Forms/Swag", "/Forms/Travel" }, lh.Take(5).ToList());
        Assert.Equal("Nav.SpeakerGift", logistics.Items.Single(i => i.Href == "/Forms/Swag").LabelKey);
        // ...then the §104–§123 content pages share the SAME fold-out: speakers see
        // both the all-roles pages and the speaker-only pages (§123).
        Assert.Contains("/Info/wayfinding", lh);        // all-roles
        Assert.Contains("/Info/session-guidelines", lh); // speaker-only
        Assert.Contains("/Info/help-promote", lh);       // speaker-only

        // Removed for speakers (operator 2026-06-23): My tasks, Resources.
        Assert.DoesNotContain("/Tasks", hrefs);
        Assert.DoesNotContain("/Resources", hrefs);
        // §153 (operator 2026-06-28): the public Sessions catalogue now lives in the SHARED
        // Event-logistics fold-out for every role (no longer removed for speakers).
        Assert.Contains("/Sessions", lh);
        // §86/§138: the Master Class Q&A area IS a speaker nav entry (for a master-class speaker).
        Assert.Contains("/Speaker/Questions", hrefs);
        Assert.DoesNotContain("/Speaker/Evaluations", hrefs); // reached from My Sessions hub
        // /Speaker/Graphics is now surfaced as "Help Promote" (§26c) — asserted above.
    }

    [Fact]
    public void Master_class_qa_item_shows_only_for_master_class_speakers()
    {
        // §138: the "Master Class Q&A" item is shown ONLY when the speaker presents at
        // least one master class. A speaker with zero master classes would otherwise land
        // on an empty Group-Q&A page (the page stays reachable by direct URL).
        var without = NavBuilder.Build(ParticipantRole.Speaker, speakerHasMasterClass: false)
            .AllItems.Select(i => i.Href).ToList();
        Assert.DoesNotContain("/Speaker/Questions", without);

        var with = NavBuilder.Build(ParticipantRole.Speaker, speakerHasMasterClass: true)
            .AllItems.Select(i => i.Href).ToList();
        Assert.Contains("/Speaker/Questions", with);
    }

    [Fact]
    public void Speaker_readiness_item_is_removed_from_the_menu()
    {
        // §138: the standalone "Am I ready?" speaker nav item is gone (the rollup now
        // lives at the top of My Tasks). This holds regardless of the master-class gate.
        foreach (var hasMc in new[] { false, true })
        {
            var hrefs = NavBuilder.Build(ParticipantRole.Speaker, speakerHasMasterClass: hasMc)
                .AllItems.Select(i => i.Href).ToList();
            Assert.DoesNotContain("/Speaker/Readiness", hrefs);
        }
    }

    [Fact]
    public void Attendee_menu_is_minimal_home_masterclass_waitlist()
    {
        var attendee = NavBuilder.Build(ParticipantRole.Attendee).Groups[0];
        var hrefs = attendee.Items.Select(i => i.Href).ToList();

        // Home + Master Class + My plan + Waitlist + Contact Organizers (the latter
        // appended last for every role, operator 2026-06-21). My plan was surfaced —
        // a complete page that previously had no menu/link path.
        // My plan removed (operator 2026-06-23 — Zoho Backstage); Master Class Q&A
        // shortcut added (operator 2026-06-24).
        // §104–§123: the all-roles content pages (Event-logistics fold-out) appear for
        // attendees too, between the attendee leaves and the Policies/Contact tail.
        // Policies (Privacy Policy + Code of Conduct) precede Contact for every role (operator 2026-06-25).
        // §153 (operator 2026-06-28): the public Sessions catalogue leads the shared
        // Event-logistics fold-out for every role (attendees included).
        Assert.Equal(new[]
        {
            "/", "/Party", "/Attendee", "/Attendee/Waitlist", "/Attendee/MasterClassQa",
            // §171: the attendee "fun IT games" entry sits in the attendee block.
            "/Games",
            "/Sessions",
            "/Info/wayfinding", "/Info/good-to-know", "/Info/addresses", "/Info/last-event-videos",
            "https://expertslive.dk/privacy-policy/", "https://expertslive.dk/code-of-conduct/", "/Contact",
        }, hrefs);
        Assert.Equal("Nav.MasterClass", attendee.Items.Single(i => i.Href == "/Attendee").LabelKey);
        Assert.Equal("Nav.Waitlist", attendee.Items.Single(i => i.Href == "/Attendee/Waitlist").LabelKey);
        Assert.Equal("Nav.MasterClassQa", attendee.Items.Single(i => i.Href == "/Attendee/MasterClassQa").LabelKey);
        Assert.DoesNotContain("/Attendee/MyPlan", hrefs);

        // Removed for attendees.
        Assert.DoesNotContain("/Tasks", hrefs);
        Assert.DoesNotContain("/Profile", hrefs);
        Assert.DoesNotContain("/Resources", hrefs);
        Assert.DoesNotContain("/Attendee/MyEvent", hrefs);
        // §153 (operator 2026-06-28): Sessions is now in the shared Event-logistics fold-out for
        // EVERY role, attendees included.
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
        Assert.Contains("/Speaker", speaker);          // now "My Sessions"
        Assert.Contains("/Speaker/Details", speaker);  // consolidated Speaker Details (replaced Bio, §26c)
        Assert.Contains("/Forms/Travel", speaker);
        Assert.Contains("/Forms/Lunch", speaker);
        Assert.Contains("/Forms/Swag", speaker);
        Assert.Contains("/Contact", speaker);

        // P7: the booth/lead block (incl. /Sponsor/CaptureLead) is gated behind
        // isExhibitor — an EXHIBITOR sponsor (HasBooth) sees it; a digital-only sponsor
        // does not. Non-gated sponsor routes (Tasks/Logistics/Contact) appear for both.
        var sponsor = NavBuilder.Build(ParticipantRole.Sponsor, isExhibitor: true).AllItems.Select(i => i.Href).ToList();
        Assert.Contains("/Sponsor/Tasks", sponsor);
        Assert.Contains("/Sponsor/Logistics", sponsor);   // Key Dates & times, in the Event-logistics fold-out
        Assert.Contains("/Contact", sponsor);             // "Contact Organizers" — unified shared page (2026-06-28)
        Assert.Contains("/Sponsor/CaptureLead", sponsor); // failover in the Leads fold-out (exhibitor-only)
        // A sponsor uses the company-shared tasks entry, not the generic /Tasks.
        Assert.DoesNotContain("/Tasks", sponsor);

        // A digital-only (non-exhibitor) sponsor must NOT see the booth/lead items.
        var digitalSponsor = NavBuilder.Build(ParticipantRole.Sponsor, isExhibitor: false).AllItems.Select(i => i.Href).ToList();
        Assert.DoesNotContain("/Sponsor/CaptureLead", digitalSponsor);
        Assert.Contains("/Sponsor/Tasks", digitalSponsor); // non-gated entries still present

        var attendee = NavBuilder.Build(ParticipantRole.Attendee).AllItems.Select(i => i.Href).ToList();
        Assert.Contains("/Attendee", attendee);          // in-hub Master Class chooser
        Assert.Contains("/Attendee/Waitlist", attendee); // waitlist view

        var vol = NavBuilder.Build(ParticipantRole.Volunteer, isVolunteerSupervisor: true).AllItems.Select(i => i.Href).ToList();
        Assert.Contains("/volunteer/myschedule", vol);   // merged shifts + tasks home
        Assert.Contains("/volunteer/availability", vol); // per-day available/blocked
        Assert.Contains("/volunteer/supervisor", vol);   // only for actual supervisors
    }

    [Fact]
    public void Sponsor_menu_matches_redesign()
    {
        // P7: the Exhibitor/Booth + Leads fold-outs are exhibitor-only — build an
        // EXHIBITOR sponsor (HasBooth) so they appear; a digital-only sponsor is
        // asserted not to get them at the end of this test.
        var g = NavBuilder.Build(ParticipantRole.Sponsor, isExhibitor: true).Groups[0];
        var hrefs = g.Items.Select(i => i.Href).ToList();

        // Removed (operator 2026-06-21): Sponsor Portal, standalone Capture-lead tab,
        // the plain /Sponsor engagement link, Resources.
        Assert.DoesNotContain("/Sponsor/Portal", hrefs);
        Assert.DoesNotContain("/Resources", hrefs);
        // §153 (operator 2026-06-28): the public Sessions catalogue is now in the shared
        // Event-logistics fold-out for sponsors too.
        Assert.Contains("/Sessions", hrefs);
        // Orders + Linked-contacts merged into ONE Webshop item -> the /Sponsor page
        // (operator 2026-06-23). The old per-anchor entries are gone.
        Assert.Contains("/Sponsor", hrefs);
        Assert.DoesNotContain("/Sponsor#orders", hrefs);
        Assert.DoesNotContain("/Sponsor#linked-contacts", hrefs);

        // Fold-outs present with the right sections. §162: Leads no longer has its own fold-out —
        // the Leads group + "Your Booth" were merged INTO "Exhibitor & Booth Details".
        var sections = g.Sections().Where(s => s.HeadingKey is not null).Select(s => s.HeadingKey).ToList();
        Assert.Contains("Nav.SectionSponsorWebshop", sections);
        Assert.Contains("Nav.SectionExhibitorBooth", sections);
        Assert.DoesNotContain("Nav.SectionLeads", sections);

        // The single "Exhibitor & Booth Details" fold-out now carries: the 4 external Zoho booth
        // links, "Your Booth" (internal), and the Leads group (2 external Zoho + the internal
        // Capture-lead failover) = 8 items.
        var booth = g.Sections().Single(s => s.HeadingKey == "Nav.SectionExhibitorBooth");
        var boothHrefs = booth.Items.Select(i => i.Href).ToList();
        Assert.Equal(8, booth.Items.Count);
        Assert.Contains("/Sponsor/Booth", boothHrefs);          // Your Booth moved here
        Assert.Contains("/Sponsor/CaptureLead", boothHrefs);    // Leads failover moved here
        // The Zoho links open in a new tab; the two in-hub pages are same-tab.
        Assert.All(booth.Items.Where(i => i.Href.StartsWith("https://eldk27.expertslive.dk/")), i => Assert.True(i.External));
        Assert.False(booth.Items.Single(i => i.Href == "/Sponsor/CaptureLead").External);
        Assert.False(booth.Items.Single(i => i.Href == "/Sponsor/Booth").External);

        // Operator 2026-06-27 (BUG): there is exactly ONE "Event logistics" entry. The booth
        // run-of-show (/Sponsor/Logistics) is now a LEAF inside the SHARED
        // Nav.SectionEventLogistics fold-out (not a second standalone "Event logistics" leaf),
        // and it LEADS that fold-out, ahead of the §104–§123 content-hub pages.
        Assert.Single(g.Sections(), s => s.HeadingKey == "Nav.SectionEventLogistics");
        Assert.DoesNotContain(g.Items, i => i.LabelKey == "Nav.SponsorEventLogistics");
        var eventLogistics = g.Sections().Single(s => s.HeadingKey == "Nav.SectionEventLogistics");
        var boothLeaf = eventLogistics.Items.Single(i => i.Href == "/Sponsor/Logistics");
        Assert.Equal("Nav.SponsorBoothRunOfShow", boothLeaf.LabelKey);
        Assert.Equal("/Sponsor/Logistics", eventLogistics.Items[0].Href); // booth run-of-show leads
        var elHrefs = eventLogistics.Items.Select(i => i.Href).ToList();
        Assert.Contains("/Info/wayfinding", elHrefs);  // content-hub pages share the SAME fold-out

        // §162: "Your Booth" (/Sponsor/Booth) is NO LONGER under Event logistics — it moved into the
        // Exhibitor & Booth Details fold-out (asserted above), so it must NOT appear here.
        Assert.DoesNotContain("/Sponsor/Booth", elHrefs);

        // Operator 2026-06-27: the standalone "Deliverables" nav entry is removed (the rollup
        // moved to the top of Sponsor My Tasks). The page route stays reachable by direct URL.
        Assert.DoesNotContain("/Sponsor/Deliverables", hrefs);

        // P7: a digital-only (non-exhibitor) sponsor gets NEITHER the Exhibitor/Booth
        // nor the Leads fold-out (the booth/lead Zoho items would be dead links).
        var digital = NavBuilder.Build(ParticipantRole.Sponsor, isExhibitor: false).Groups[0];
        var digitalSections = digital.Sections().Where(s => s.HeadingKey is not null).Select(s => s.HeadingKey).ToList();
        Assert.DoesNotContain("Nav.SectionExhibitorBooth", digitalSections);
        Assert.DoesNotContain("Nav.SectionLeads", digitalSections);
        // The non-gated Sponsor Webshop fold-out still shows for a digital sponsor.
        Assert.Contains("Nav.SectionSponsorWebshop", digitalSections);
        // §146: "Our Booth" is exhibitor-only — a digital sponsor (no booth) doesn't get it.
        Assert.DoesNotContain("/Sponsor/Booth", digital.Items.Select(i => i.Href));
    }

    [Fact]
    public void Volunteer_menu_matches_redesign()
    {
        var g = NavBuilder.Build(ParticipantRole.Volunteer, isVolunteerSupervisor: true).Groups[0];
        var hrefs = g.Items.Select(i => i.Href).ToList();

        // Removed: Resources, the shift-signup wizard, the separate
        // My-shifts / My-volunteer-tasks tabs (merged into My schedule).
        Assert.DoesNotContain("/Resources", hrefs);
        Assert.DoesNotContain("/Forms/VolunteerWizard", hrefs);
        // §153 (operator 2026-06-28): Sessions is now in the shared Event-logistics fold-out for
        // volunteers too.
        Assert.Contains("/Sessions", hrefs);
        Assert.DoesNotContain("/volunteer/myshifts", hrefs);
        Assert.DoesNotContain("/volunteer/mytasks", hrefs);

        // Kept: My schedule (merged), My availability, supervisor (for supervisors).
        Assert.Contains("/volunteer/myschedule", hrefs);
        Assert.Contains("/volunteer/availability", hrefs);
        Assert.Contains("/volunteer/supervisor", hrefs);
        Assert.True(hrefs.IndexOf("/volunteer/availability") < hrefs.IndexOf("/volunteer/myschedule"),
            "My availability comes before My schedule (operator 2026-06-23).");

        // Event-logistics fold-out with Volunteer Gift (the Important dates / Calendar
        // entry was removed: the user-facing calendar UI is retired).
        var logistics = g.Sections().Single(s => s.HeadingKey == "Nav.SectionEventLogistics");
        var lh = logistics.Items.Select(i => i.Href).ToList();
        // The entitlement forms lead the fold-out, in order...
        Assert.Equal(new[] { "/Forms/Hotel", "/Forms/Dinner", "/Forms/Lunch", "/Forms/Swag" }, lh.Take(4).ToList());
        Assert.Equal("Nav.VolunteerGift", logistics.Items.Single(i => i.Href == "/Forms/Swag").LabelKey);
        // ...then the all-roles content pages join the same fold-out, but a volunteer
        // (non-speaker) must NOT get the speaker-only content pages (§123).
        Assert.Contains("/Info/wayfinding", lh);
        Assert.DoesNotContain("/Info/session-guidelines", lh);
        Assert.DoesNotContain("/Calendar", hrefs);
    }

    [Fact]
    public void Volunteer_supervisor_item_shows_only_for_supervisors()
    {
        // A non-supervisor volunteer must NOT see the Supervisor dashboard item
        // (it would land on a "you are not a supervisor" dead end); a supervisor does.
        var nonSup = NavBuilder.Build(ParticipantRole.Volunteer, isVolunteerSupervisor: false)
            .AllItems.Select(i => i.Href).ToList();
        Assert.DoesNotContain("/volunteer/supervisor", nonSup);

        var sup = NavBuilder.Build(ParticipantRole.Volunteer, isVolunteerSupervisor: true)
            .AllItems.Select(i => i.Href).ToList();
        Assert.Contains("/volunteer/supervisor", sup);
    }

    // ---- Role-nav orphan fixes (per-role UX audit) --------------------------
    // Each of these was a "the best surface for this role isn't in the menu"
    // defect. The fix is information-architecture only (targets / labels /
    // grouping); no route is dropped — the assertions below lock that in.

    [Fact]
    public void Contact_organizers_is_the_furthest_right_item_for_every_role()
    {
        // Operator 2026-06-21: Contact Organizers is always the LAST (rightmost)
        // participant-menu item, in every role. Sponsors keep their own contact page.
        foreach (var role in Enum.GetValues<ParticipantRole>())
        {
            var participant = NavBuilder.Build(role).Groups[0];
            var last = participant.Items[^1];
            Assert.Equal("Nav.ContactOrganizers", last.LabelKey);
            Assert.Equal("/Contact", last.Href);   // unified shared contact page for EVERY role (2026-06-28)
        }
    }

    [Fact]
    public void Games_entry_is_present_for_attendees_and_authoring_for_organizers()
    {
        // §171: the player-facing "fun IT games" entry is in the attendee menu (default ON
        // for attendees, ungated); the organizer authoring entry is in the management group.
        var attendee = NavBuilder.Build(ParticipantRole.Attendee).AllItems.Select(i => i.Href).ToList();
        Assert.Contains("/Games", attendee);
        Assert.Equal("Nav.Games", NavBuilder.Build(ParticipantRole.Attendee)
            .AllItems.Single(i => i.Href == "/Games").LabelKey);

        var organizer = NavBuilder.Build(ParticipantRole.Organizer);
        Assert.Contains("/Organizer/Quizzes", organizer.ManagementGroup!.Items.Select(i => i.Href));
        // The authoring entry is flat (no sub-fold) like every other management item.
        Assert.Null(organizer.ManagementGroup!.Items.Single(i => i.Href == "/Organizer/Quizzes").SectionKey);
    }

    [Fact]
    public void Attendee_primary_entry_is_the_master_class_chooser()
    {
        var attendee = NavBuilder.Build(ParticipantRole.Attendee).Groups[0];
        var hrefs = attendee.Items.Select(i => i.Href).ToList();

        // Operator 2026-06-21: the attendee menu is just Home + Master Class +
        // Waitlist. /Attendee is the in-hub Master Class chooser (replaced the old
        // Zoho-Bookings page) labelled "Master Class"; /Attendee/Waitlist is next.
        var mc = attendee.Items.Single(i => i.Href == "/Attendee");
        Assert.Equal("Nav.MasterClass", mc.LabelKey);
        Assert.True(hrefs.IndexOf("/Attendee") < hrefs.IndexOf("/Attendee/Waitlist"),
            "Master Class must come before Waitlist.");
    }

    // (Removed: Sponsor_capture_lead_is_a_prominent_nav_entry, Volunteer_mytasks_and_
    // supervisor_are_in_the_nav, Speaker_specific_items_are_grouped_under_a_speaker_
    // section, Speaker_grouping_drops_no_route — they asserted the PRE-2026-06-21 nav.
    // The current per-role menus are covered by Speaker_/Sponsor_/Volunteer_menu_matches_redesign.)

    // ---- REQUIREMENTS §21 "Group the organizer nav" -------------------------
    // The flat management menu is now bucketed into named, collapsible sub-groups
    // (People / Sessions / Comms / Sponsors / Volunteers / Logistics) via
    // NavItem.SectionKey + NavGroup.Sections(). These assert the grouping is pure
    // information architecture: every prior link is still present, nothing is
    // dropped or duplicated, and the six named groups exist.

    [Fact]
    public void Grouping_preserves_every_management_link_exactly_once()
    {
        var mgmt = NavBuilder.Build(ParticipantRole.Organizer).ManagementGroup!;

        // The lean menu is a single flat (null-heading) bucket — the flattened
        // section view equals the flat Items list, with no duplicates.
        var flat = mgmt.Items.Select(i => i.Href).ToList();
        var viaSections = mgmt.Sections().SelectMany(s => s.Items).Select(i => i.Href).ToList();

        Assert.Equal(flat, viaSections);
        Assert.Equal(viaSections.Count, viaSections.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    // ---- §104–§123 content pages wired into the nav (role-scoped per §123) -----
    // The operator-authored content pages (Wayfinding, Good to know, Addresses,
    // Check out our last event, + the speaker-only Session Guidelines / A/V /
    // template / preview / feedback / evaluations / Help Promote) are wired into the
    // nav from the ContentPageRegistry under the Event-logistics fold-out. These lock
    // the §123 audience gating: all-roles pages appear for every role; speaker-only
    // pages appear ONLY for speakers (and organizers, who see everything).

    private static readonly string[] AllRoleContentHrefs =
    {
        "/Info/wayfinding", "/Info/good-to-know", "/Info/addresses", "/Info/last-event-videos",
    };

    private static readonly string[] SpeakerOnlyContentHrefs =
    {
        "/Info/speaker-template", "/Info/session-guidelines", "/Info/av-stage-timer",
        "/Info/session-preview-final", "/Info/session-feedback",
        "/Info/help-promote",
    };

    [Fact]
    public void All_role_content_pages_appear_for_every_role_under_event_logistics()
    {
        foreach (var role in Enum.GetValues<ParticipantRole>())
        {
            var group = NavBuilder.Build(role).Groups[0];
            var logistics = group.Sections().SingleOrDefault(s => s.HeadingKey == "Nav.SectionEventLogistics");
            Assert.NotNull(logistics);
            var hrefs = logistics!.Items.Select(i => i.Href).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var h in AllRoleContentHrefs)
                Assert.True(hrefs.Contains(h), $"{role} should see all-roles content page {h}.");
        }
    }

    [Fact]
    public void Content_pages_use_the_title_as_label_and_point_at_the_info_page()
    {
        // Content nav items carry no resx key (operator copy lives in the .md), so the
        // view falls back to the page Title; the href is the generic /Info/{slug} page.
        var item = NavBuilder.Build(ParticipantRole.Attendee).AllItems
            .Single(i => i.Href == "/Info/wayfinding");
        Assert.Null(item.LabelKey);
        Assert.False(string.IsNullOrWhiteSpace(item.FallbackLabel));
        Assert.Equal("Nav.SectionEventLogistics", item.SectionKey);
    }

    [Theory]
    [MemberData(nameof(NonOrganizerNonSpeakerRoleData))]
    public void Speaker_only_content_pages_are_hidden_from_non_speaker_roles(ParticipantRole role)
    {
        var hrefs = NavBuilder.Build(role).AllItems.Select(i => i.Href).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var h in SpeakerOnlyContentHrefs)
            Assert.False(hrefs.Contains(h), $"{role} must NOT see speaker-only content page {h}.");
    }

    [Theory]
    [InlineData(ParticipantRole.Speaker)]
    [InlineData(ParticipantRole.Organizer)] // organizers see every page (§123)
    public void Speaker_only_content_pages_are_visible_to_speakers_and_organizers(ParticipantRole role)
    {
        var hrefs = NavBuilder.Build(role).AllItems.Select(i => i.Href).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var h in SpeakerOnlyContentHrefs)
            Assert.True(hrefs.Contains(h), $"{role} should see speaker content page {h}.");
        // ...and the all-roles pages too.
        foreach (var h in AllRoleContentHrefs)
            Assert.True(hrefs.Contains(h), $"{role} should see all-roles content page {h}.");
    }

    public static TheoryData<ParticipantRole> NonOrganizerNonSpeakerRoleData()
    {
        var data = new TheoryData<ParticipantRole>();
        foreach (var r in new[]
                 {
                     ParticipantRole.Volunteer, ParticipantRole.Sponsor,
                     ParticipantRole.Attendee, ParticipantRole.Media,
                     ParticipantRole.EventPartner,
                 })
            data.Add(r);
        return data;
    }

    public static TheoryData<ParticipantRole> NonOrganizerRoleData()
    {
        var data = new TheoryData<ParticipantRole>();
        foreach (var r in NonOrganizerRoles) data.Add(r);
        return data;
    }
}
