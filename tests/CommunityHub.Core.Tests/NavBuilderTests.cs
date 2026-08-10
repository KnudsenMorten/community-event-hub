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
        // §741 (operator 2026-07-31) — the standalone Audit slot holds the AUDIT TRAIL, not the
        // acting-as log. Acting-as is one Category inside the trail, so the menu pointed at a
        // subset of the page next to it. ImpersonationLog stays routable, linked from Participants.
        "/Organizer/AuditTrail",         // standalone Audit
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
        // §705.12: Broadcast + SendInvitations deleted (verified unused in PROD).
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
        // §303b: /Organizer/EditOnBehalf DELETED — "Switch to user" is the one act-as feature.
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
        //
        // §646 — raised 14 → 16 for Platform Health + Background Jobs, on his explicit request
        // ("organizer menus - add Platform Health + Jobs to main menu for quick access"). The
        // 2026-06-21 principle is NOT weakened: it bars per-FEATURE pages, and these two are OPS
        // pages — the ones he opens to ask "is anything broken?", which §637 showed the hub answered
        // worst of all.
        // 🔒 The limit still exists and is still tight. If it needs raising again, that is a
        // conversation about what the menu is FOR, not a number to nudge.
        Assert.True(mgmt.Items.Count <= 16,
            $"Lean organizer menu should stay ~13-16 hub-level entries; got {mgmt.Items.Count}.");
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
        Assert.Contains("/Organizer/AuditTrail", hrefs);            // §741
        Assert.DoesNotContain("/Organizer/ImpersonationLog", hrefs); // §741 — replaced, not kept

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
        Assert.Contains("/Tasks", hrefs);
        Assert.Contains("/Resources", hrefs);
        Assert.Contains("/Sessions", hrefs);
        // §283 (operator 2026-07-10): "My Hub Profile" is NO LONGER a main-menu item —
        // it moved into the dropdown under the signed-in person's name in the header.
        Assert.DoesNotContain("/Profile", hrefs);
    }

    [Fact]
    public void Speaker_menu_matches_redesign()
    {
        // §138: the Master Class Q&A item is gated on speakerHasMasterClass; build a
        // speaker WITH a master class so the rest of the menu (and that item) is present.
        var g = NavBuilder.Build(ParticipantRole.Speaker, speakerHasMasterClass: true).Groups[0];
        var hrefs = g.Items.Select(i => i.Href).ToList();

        // Current speaker IA (§267/§267b/§285/§290/§294): Home, Get Started (inline wizard),
        // Speaker Details top-level; a "My Tasks" fold-out with the register forms; a
        // "Speaker Info" fold-out with the speaker-specific items + speaker content pages;
        // Event Info with Lunch + the shared leaves; Party under "Register"; Contact last.
        // §283 (operator 2026-07-10): "My Hub Profile" is no longer a main-menu item — it moved
        // into the dropdown under the signed-in person's name in the header.
        Assert.DoesNotContain("/Profile", hrefs);
        // §285: "Get Started" opens the INLINE wizard at /Forms/Wizard (speaker onboarding §28).
        var getStarted = g.Items.Single(i => i.Href == "/Forms/Wizard");
        Assert.Equal("Nav.SpeakerOnboarding", getStarted.LabelKey);
        Assert.Null(getStarted.SectionKey);
        // §707.56 — the standalone "Speaker Details" NAV ENTRY IS REMOVED. It was a second door to
        // the form the Get-Started wizard already hosts at ?step=details — one model, one service,
        // one partial, two hosts (§708.1) — so the menu entry was a duplicate ENTRANCE, which is
        // precisely what §708.2 is dismantling. The page itself still resolves for anyone holding a
        // link; only the menu item went. This assertion outlived the change and was red on main.
        Assert.DoesNotContain("/Speaker/Details", hrefs);
        Assert.DoesNotContain("/Forms/Speaker", hrefs);   // Bio replaced by Speaker Details
        Assert.Contains("/Contact", hrefs);

        // The calendar UI is retired: /Calendar no longer appears in the speaker menu.
        Assert.DoesNotContain("/Calendar", hrefs);
        // §247 (operator 2026-07-07: "DROP THIS speaker readiness!"): the §234 "Am I
        // ready?" entry is removed again — the readiness rollup on Speaker My Tasks
        // covers it. The page stays routable; it just has no menu entry.
        Assert.DoesNotContain("/Speaker/Readiness", hrefs);
        // Removed for speakers (operator 2026-06-23): the generic /Tasks list (speakers use
        // /Speaker/Tasks) and Resources.
        Assert.DoesNotContain("/Tasks", hrefs);
        Assert.DoesNotContain("/Resources", hrefs);

        // §301c (operator 2026-07-24 "i want it to be consistent"): "My Tasks" is a PLAIN
        // link again — every register/claim form moved to the "Register" fold-out.
        var myTasks = g.Items.Single(i => i.Href == "/Speaker/Tasks");
        Assert.Equal("Nav.MyTasks", myTasks.LabelKey);
        Assert.Null(myTasks.SectionKey);

        // §317 (operator 2026-07-24): the "Speaker Info" fold-out in the operator's EXACT
        // order — two direct leaves (My Sessions, Master Class Q&A), then the three nested
        // sub-fold-outs: Preparing My Session for ELDK27 (guidelines, deadlines, telemetry,
        // survey results, Help Promote, template), Session Room Info (A/V), Session
        // Evaluation (ratings, how-we-evaluate).
        var speakerInfo = g.Sections().Single(s => s.HeadingKey == "Nav.SectionSpeakerInfo");
        Assert.Equal(new[]
        {
            // §326e: "Key Dates & Times" is a DIRECT leaf right after the Q&A entry.
            "/Speaker", "/Speaker/Questions", "/Info/key-dates-times",
            "/Info/session-guidelines", "/Info/session-preview-final", "/Sponsor/Telemetry",
            "https://eldk27.eventhub.expertslive.dk/survey/eldk27-topics/results",
            // 🙈 §748.5 — BOTH /Speaker/Evaluations entries are gone (operator 2026-07-31: "hide the
            // page until C10 exists"): the §320 "#eval-qr" direct entry and the §234 evaluation
            // entry. C10 restores them — pointing at the per-SESSION QR, not the retired room one.
            // §838 — "Social media announcements": what CEH will post about their sessions, and
            // when. Right after "Help Promote" because it answers the same question from the other
            // side — promote WHAT, and WHEN does the official post go out.
            "/Speaker/Graphics", "/Speaker/Announcements", "/Info/speaker-template",
            "/Sessions/Slides",
            "/Info/av-stage-timer",
            // §752 C10 — the four-point results page, replacing the retired 1–5 one that §748.5 hid.
            "/Speaker/Results", "/Info/session-feedback",
        }, speakerInfo.Items.Select(i => i.Href).ToList());
        // Leaves carry no SubSectionKey; every other item sits in its named sub-fold-out.
        string? Sub(string href) => speakerInfo.Items.Single(i => i.Href == href).SubSectionKey;
        Assert.Null(Sub("/Speaker"));
        Assert.Null(Sub("/Speaker/Questions"));
        Assert.Null(Sub("/Info/key-dates-times"));   // §326e direct leaf
        Assert.Equal("Nav.SubPrepareSession", Sub("/Info/session-guidelines"));
        Assert.Equal("Nav.SubPrepareSession", Sub("/Info/session-preview-final"));
        Assert.Equal("Nav.SubPrepareSession", Sub("/Sponsor/Telemetry"));
        Assert.Equal("Nav.SubPrepareSession", Sub("/Speaker/Graphics"));
        Assert.Equal("Nav.SubPrepareSession", Sub("/Speaker/Announcements"));   // §838
        Assert.Equal("Nav.SubPrepareSession", Sub("/Info/speaker-template"));
        Assert.Equal("Nav.SubPrepareSession", Sub("/Sessions/Slides"));               // §322k
        Assert.Equal("Nav.SubSessionRoom", Sub("/Info/av-stage-timer"));
        Assert.Equal("Nav.SubSessionEvaluation", Sub("/Speaker/Results"));
        Assert.Equal("Nav.SubSessionEvaluation", Sub("/Info/session-feedback"));
        // 🙈 §748.5 — the hiding asserted directly, not merely implied by the list above: a speaker
        // must not be able to REACH the retired 1–5 page from the menu by either route.
        Assert.DoesNotContain("/Speaker/Evaluations", hrefs);
        Assert.DoesNotContain("/Speaker/Evaluations#eval-qr", hrefs);
        // Speaker-specific labels (§317): the fuller Q&A + telemetry wordings.
        Assert.Equal("Nav.MySessions", speakerInfo.Items.Single(i => i.Href == "/Speaker").LabelKey);
        Assert.Equal("Nav.MasterClassQaSpeaker", speakerInfo.Items.Single(i => i.Href == "/Speaker/Questions").LabelKey);
        Assert.Equal("Nav.HelpPromote", speakerInfo.Items.Single(i => i.Href == "/Speaker/Graphics").LabelKey);
        Assert.Equal("Nav.AttendeeTelemetrySpeaker", speakerInfo.Items.Single(i => i.Href == "/Sponsor/Telemetry").LabelKey);
        // §289: the "Social Media Guidelines" (help-promote) content page is REMOVED — its
        // copy lives on /Speaker/Graphics now.
        Assert.DoesNotContain("/Info/help-promote", hrefs);

        // §317: the Event Info fold-out in the operator's order — Check Out Last Event →
        // Good To Know → Address → Wayfinding, then the Sessions catalogue, then the nested
        // Policies sub-fold-out (Code of Conduct before Privacy Policy). No survey-results
        // leaf here for speakers (it lives under Speaker Info), no Lunch (§301c: Register).
        var logistics = g.Sections().Single(s => s.HeadingKey == "Nav.SectionEventLogistics");
        var lh = logistics.Items.Select(i => i.Href).ToList();
        Assert.DoesNotContain("/Forms/Lunch", lh);
        Assert.Equal(new[]
        {
            "/Info/last-event-videos", "/Info/good-to-know", "/Info/addresses", "/Info/wayfinding",
            "/Info/ceh-introduction",   // §326ag
            "/Sessions", "/Sessions/Slides",
            // §326f: the logo-pack zip download (speakers + sponsors only).
            "/logo-pack/download",
            "https://expertslive.dk/code-of-conduct/", "https://expertslive.dk/privacy-policy/",
        }, lh);
        Assert.DoesNotContain("/Info/session-guidelines", lh);  // speaker-only pages moved to Speaker Info

        // §288/§297/§301c: the "Register" menu holds EVERY register/claim form — Hotel,
        // Dinner, Lunch, Swag, Travel — plus Party (register). Not My Tasks, not Event Info.
        var register = g.Sections().Single(s => s.HeadingKey == "Nav.SectionRegister");
        Assert.Equal(
            new[] { "/Forms/Hotel", "/Forms/Dinner", "/Forms/Lunch", "/Forms/Swag", "/Forms/Travel", "/Forms/Wizard?step=party" },
            register.Items.Select(i => i.Href).ToList());
        Assert.Equal("Nav.SpeakerGift", register.Items.Single(i => i.Href == "/Forms/Swag").LabelKey);
        Assert.Equal("Nav.PartySignup", register.Items.Single(i => i.Href == "/Forms/Wizard?step=party").LabelKey);
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
    public void Neither_readiness_nor_evaluations_is_in_the_speaker_menu()
    {
        // §234 UX surfaced both orphaned pages; §247 (operator 2026-07-07) then DROPPED
        // the "Am I ready?" (/Speaker/Readiness) entry again — the readiness rollup at
        // the top of Speaker My Tasks covers it (the page stays routable, no menu entry).
        //
        // 🙈 §748.5 (operator 2026-07-31: "hide the page until C10 exists") — /Speaker/Evaluations
        // joined it, for the same reason and with the same treatment: still routable (it redirects
        // to /Speaker), no menu entry. It read the retired 1–5 scale, so it could only ever show a
        // speaker an empty list of their OWN ratings — which reads as "nobody rated me".
        //
        // 🔑 Asserted across BOTH master-class states because the gate is what decides which speaker
        // branch runs; hiding it on one branch only would leave it reachable for half the speakers.
        foreach (var hasMc in new[] { false, true })
        {
            var hrefs = NavBuilder.Build(ParticipantRole.Speaker, speakerHasMasterClass: hasMc)
                .AllItems.Select(i => i.Href).ToList();
            Assert.DoesNotContain("/Speaker/Readiness", hrefs);
            Assert.DoesNotContain("/Speaker/Evaluations", hrefs);
            Assert.DoesNotContain("/Speaker/Evaluations#eval-qr", hrefs);
        }
    }

    [Fact]
    public void Attendee_menu_is_minimal_home_masterclass_waitlist()
    {
        // §234 UX: the three Master-Class entries are gated on the 2-day ticket, so this
        // FULL menu is the 2-day holder's; the 1-day menu is asserted in the next test.
        var attendee = NavBuilder.Build(ParticipantRole.Attendee, attendeeIsTwoDay: true).Groups[0];
        var hrefs = attendee.Items.Select(i => i.Href).ToList();

        // Home + Master Class + Waitlist + Q&A + Contact Organizers (the latter appended
        // last for every role, operator 2026-06-21). My plan removed (operator 2026-06-23 —
        // Zoho Backstage); Master Class Q&A shortcut added (operator 2026-06-24).
        // §104–§123: the all-roles content pages (Event-logistics fold-out) appear for
        // attendees too, between the attendee leaves and the Policies/Contact tail.
        // §297: Policies (Privacy Policy + Code of Conduct) is a nested sub-fold-out inside
        // Event Info, still just before Contact in the flat list.
        // §153 (operator 2026-06-28): the public Sessions catalogue leads the shared
        // Event-logistics fold-out for every role (attendees included).
        // §270 (operator 2026-07-10): the "fun IT games" entry is DEFAULT OFF — no /Games
        // here (see Games_entry_is_flag_gated_for_attendees_and_authoring_for_organizers).
        Assert.Equal(new[]
        {
            // §285: the attendee Get-Started stepper (now the inline /Forms/Wizard) leads.
            "/", "/Forms/Wizard", "/Forms/Wizard?step=masterclass", "/Attendee/Waitlist", "/Attendee/MasterClassQa",
            // §326al (operator 2026-07-25): the three Master-Class entries repeat under the
            // "Register/Update" fold-out — deliberate duplicates of the main-nav leaves above.
            "/Forms/Wizard?step=masterclass", "/Attendee/Waitlist", "/Attendee/MasterClassQa",
            // §353 (operator 2026-07-26) REVERSES §297: the prominent main-nav entry
            // ("Party Preday") PLUS a deliberate duplicate under Register/Update. §326am: both
            // open the wizard's INLINE party step instead of the standalone /Party page.
            "/Forms/Wizard?step=party", "/Forms/Wizard?step=party",
            // §317: Event Info in the operator's order — the four content leaves, then the
            // Sessions catalogue + the survey-results leaf, then the nested Policies
            // sub-fold-out (Code of Conduct before Privacy Policy).
            "/Info/last-event-videos", "/Info/good-to-know", "/Info/addresses", "/Info/wayfinding",
            "/Info/ceh-introduction",   // §326ag
            "/Sessions", "/Sessions/Slides",
            "https://expertslive.dk/code-of-conduct/", "https://expertslive.dk/privacy-policy/", "/Contact",
        }, hrefs);
        // §326al: each Master-Class href now resolves twice (main nav + Register fold-out) —
        // assert the main-nav leaf, then that the Register duplicate carries the same label.
        Assert.Equal("Nav.MasterClass", attendee.Items.Single(i => i.Href == "/Forms/Wizard?step=masterclass" && i.SectionKey is null).LabelKey);
        Assert.Equal("Nav.Waitlist", attendee.Items.Single(i => i.Href == "/Attendee/Waitlist" && i.SectionKey is null).LabelKey);
        Assert.Equal("Nav.MasterClassQa", attendee.Items.Single(i => i.Href == "/Attendee/MasterClassQa" && i.SectionKey is null).LabelKey);
        foreach (var (href, label) in new[]
                 {
                     ("/Forms/Wizard?step=masterclass", "Nav.MasterClass"),
                     ("/Attendee/Waitlist", "Nav.Waitlist"),
                     ("/Attendee/MasterClassQa", "Nav.MasterClassQa"),
                 })
        {
            var dup = attendee.Items.Single(i => i.Href == href && i.SectionKey == "Nav.SectionRegister");
            Assert.Equal(label, dup.LabelKey);
        }
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
    public void One_day_attendee_menu_hides_the_three_master_class_entries()
    {
        // §234 UX: a 1-DAY ticket excludes Master Classes — the chooser, waitlist and Q&A
        // entries would all be dead ends, so they are hidden (attendeeIsTwoDay defaults to
        // false). The pages stay reachable by direct URL (friendly not-eligible states);
        // everything else in the attendee menu is unchanged.
        var oneDay = NavBuilder.Build(ParticipantRole.Attendee).AllItems.Select(i => i.Href).ToList();
        Assert.DoesNotContain("/Forms/Wizard?step=masterclass", oneDay);
        Assert.DoesNotContain("/Attendee/Waitlist", oneDay);
        Assert.DoesNotContain("/Attendee/MasterClassQa", oneDay);
        Assert.Contains("/Forms/Wizard", oneDay);   // §285: the inline Get-Started wizard
        // §270: games are DEFAULT OFF — absent unless the edition opts in.
        Assert.DoesNotContain("/Games", oneDay);
        Assert.Contains("/Forms/Wizard?step=party", oneDay);   // §326am: inline party step

        var twoDay = NavBuilder.Build(ParticipantRole.Attendee, attendeeIsTwoDay: true)
            .AllItems.Select(i => i.Href).ToList();
        Assert.Contains("/Forms/Wizard?step=masterclass", twoDay);
        Assert.Contains("/Attendee/Waitlist", twoDay);
        Assert.Contains("/Attendee/MasterClassQa", twoDay);
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
        // §707.56 — "/Speaker/Details" left the MENU (a duplicate entrance to ?step=details), so
        // the spot-check no longer asserts it. The constraint this test guards — relocate only,
        // never silently drop a route — still holds: the page resolves, and the wizard step hosts
        // the same form. Red on main since §707.56 shipped.
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
        // §483: "Capture Leads (Failover)" is no longer in the MENU (operator request). The page
        // itself stays routable, so this asserts the nav entry is gone, not that the page is.
        Assert.DoesNotContain("/Sponsor/CaptureLead", sponsor);
        // A sponsor uses the company-shared tasks entry, not the generic /Tasks.
        Assert.DoesNotContain("/Tasks", sponsor);

        // A digital-only (non-exhibitor) sponsor must NOT see the booth/lead items.
        var digitalSponsor = NavBuilder.Build(ParticipantRole.Sponsor, isExhibitor: false).AllItems.Select(i => i.Href).ToList();
        Assert.DoesNotContain("/Sponsor/CaptureLead", digitalSponsor);
        Assert.Contains("/Sponsor/Tasks", digitalSponsor); // non-gated entries still present

        // §234 UX: the Master-Class routes are 2-day-gated, so build a 2-day holder here.
        var attendee = NavBuilder.Build(ParticipantRole.Attendee, attendeeIsTwoDay: true).AllItems.Select(i => i.Href).ToList();
        Assert.Contains("/Forms/Wizard?step=masterclass", attendee);          // in-hub Master Class chooser
        Assert.Contains("/Attendee/Waitlist", attendee); // waitlist view

        var vol = NavBuilder.Build(ParticipantRole.Volunteer, isVolunteerSupervisor: true).AllItems.Select(i => i.Href).ToList();
        Assert.Contains("/volunteer/myschedule", vol);   // merged shifts + tasks home
        // §939 — the standalone availability page is retired; the wizard step replaces it.
        Assert.Contains("/Forms/Wizard?step=availability", vol); // per-day available/blocked
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

        // §483/§488/§489: the fold-out carries FOUR in-hub leaves (Key Dates & Times, Your Booth,
        // Party Preday, Attendee Telemetry) plus the nested "Zoho Event System" group — just Leads
        // + Inquiries after §488 removed the four Zoho setup links ⇒ 6 items.
        // §1027: Social Media Announcements joins them as a FIFTH leaf ⇒ 7.
        var booth = g.Sections().Single(s => s.HeadingKey == "Nav.SectionExhibitorBooth");
        var boothHrefs = booth.Items.Select(i => i.Href).ToList();
        Assert.Equal(7, booth.Items.Count);
        Assert.Contains("/Sponsor/Booth", boothHrefs);          // Your Booth moved here
        Assert.DoesNotContain("/Sponsor/CaptureLead", boothHrefs);

        // §483 ORDER — the operator's exact sequence. The layout renders direct leaves before any
        // sub-fold-out, so asserting the leaf order here is what pins what he actually sees.
        var boothLeaves = booth.Items.Where(i => i.SubSectionKey is null).Select(i => i.Href).ToList();
        // §1027: announcements follow telemetry — both are "what the event is doing FOR you",
        // which is why they sit together and neither belongs in the top-level action menu.
        Assert.Equal(
            new[] { "/Sponsor/Logistics", "/Sponsor/Booth", "/Forms/Wizard?step=party",
                    "/Sponsor/Telemetry", "/Sponsor/Announcements" },
            boothLeaves);
        // §1027: announcements left the sponsor's TOP-LEVEL menu — for an exhibitor it lives only
        // in the booth fold-out. (A digital-only sponsor has no fold-out, so it stays top-level
        // for them — asserted in the non-exhibitor test.)
        Assert.DoesNotContain("/Sponsor/Announcements",
            g.Sections().Where(s => s.HeadingKey is null).SelectMany(s => s.Items).Select(i => i.Href));
        // §489: telemetry left the sponsor's TOP-LEVEL menu — for an exhibitor it exists only here.
        Assert.DoesNotContain(g.Items, i => i.Href == "/Sponsor/Telemetry" && i.SectionKey is null);

        // §483 — every remaining Zoho link sits in the nested group, Leads FIRST, Inquiries SECOND.
        // §488 — and ONLY those two: the four Zoho setup links are gone, because the hub now owns
        // that data and a second editable copy in Zoho would diverge from it.
        var zoho = booth.Items.Where(i => i.SubSectionKey == "Nav.SectionZohoEventSystem").ToList();
        Assert.Equal(2, zoho.Count);
        Assert.All(zoho, i => Assert.True(i.External));         // the whole group leaves the hub
        Assert.EndsWith("lead-list", zoho[0].Href);
        Assert.EndsWith("inquiry-list", zoho[1].Href);
        // Nothing Zoho may remain a direct leaf — that was the point of grouping them.
        Assert.DoesNotContain(boothLeaves, h => h.StartsWith("https://eldk27.expertslive.dk/"));

        // §297: an EXHIBITOR sponsor gets Party (register) INSIDE this fold-out (not Register).
        Assert.Contains("/Forms/Wizard?step=party", boothHrefs);
        Assert.Equal("Nav.PartySignup", booth.Items.Single(i => i.Href == "/Forms/Wizard?step=party").LabelKey);
        // The Zoho links open in a new tab; the in-hub pages are same-tab.
        Assert.All(booth.Items.Where(i => i.Href.StartsWith("https://eldk27.expertslive.dk/")), i => Assert.True(i.External));
        Assert.False(booth.Items.Single(i => i.Href == "/Sponsor/Booth").External);

        // Operator 2026-06-27 (BUG): there is exactly ONE "Event logistics" entry.
        Assert.Single(g.Sections(), s => s.HeadingKey == "Nav.SectionEventLogistics");
        Assert.DoesNotContain(g.Items, i => i.LabelKey == "Nav.SponsorEventLogistics");
        var eventLogistics = g.Sections().Single(s => s.HeadingKey == "Nav.SectionEventLogistics");
        // §483: for an EXHIBITOR, "Key Dates & Times" has MOVED to the top of Exhibitor & Booth
        // Details, so it must NOT also appear here — the same page listed twice in one menu was
        // the exact failure mode the 2026-06-27 bug above was about.
        Assert.DoesNotContain(eventLogistics.Items, i => i.Href == "/Sponsor/Logistics");
        var elHrefs = eventLogistics.Items.Select(i => i.Href).ToList();
        Assert.Contains("/Info/wayfinding", elHrefs);  // content-hub pages share the SAME fold-out

        // §162: "Your Booth" (/Sponsor/Booth) is NO LONGER under Event logistics — it moved into the
        // Exhibitor & Booth Details fold-out (asserted above), so it must NOT appear here.
        Assert.DoesNotContain("/Sponsor/Booth", elHrefs);

        // Operator 2026-06-27: the standalone "Deliverables" nav entry is removed. §234 UX:
        // after the rollup card was ALSO dropped from Sponsor My Tasks (2026-06-28) the page
        // was orphaned — it is now linked from the Sponsor home card + a callout on Sponsor
        // Tasks (in-page links, still NOT a nav entry).
        Assert.DoesNotContain("/Sponsor/Deliverables", hrefs);

        // §290/§297: "Attendee telemetry" — the authenticated in-hub "who's coming" view.
        // §489: for an EXHIBITOR it is no longer top-level; it lives inside the booth fold-out.
        var telemetry = g.Items.Single(i => i.Href == "/Sponsor/Telemetry");
        Assert.Equal("Nav.AttendeeTelemetry", telemetry.LabelKey);
        Assert.Equal("Nav.SectionExhibitorBooth", telemetry.SectionKey);

        // P7: a digital-only (non-exhibitor) sponsor gets NEITHER the Exhibitor/Booth
        // nor the Leads fold-out (the booth/lead Zoho items would be dead links).
        var digital = NavBuilder.Build(ParticipantRole.Sponsor, isExhibitor: false).Groups[0];
        var digitalSections = digital.Sections().Where(s => s.HeadingKey is not null).Select(s => s.HeadingKey).ToList();
        Assert.DoesNotContain("Nav.SectionExhibitorBooth", digitalSections);
        Assert.DoesNotContain("Nav.SectionLeads", digitalSections);
        // §297: with no booth fold-out, a digital sponsor's Party leaf falls under "Register".
        Assert.Equal("Nav.SectionRegister", digital.Items.Single(i => i.Href == "/Forms/Wizard?step=party").SectionKey);
        // The non-gated Sponsor Webshop fold-out still shows for a digital sponsor.
        Assert.Contains("Nav.SectionSponsorWebshop", digitalSections);
        // §146: "Our Booth" is exhibitor-only — a digital sponsor (no booth) doesn't get it.
        Assert.DoesNotContain("/Sponsor/Booth", digital.Items.Select(i => i.Href));

        // §489 (operator: "we also need the 'Attendee Telemetry' as main menu also so it shows for
        // a silver sponsor also (who are not an exhibitor)"): telemetry moved INTO the booth
        // fold-out for exhibitors, and a non-exhibitor has no such fold-out — so for them it must
        // stay a TOP-LEVEL item (SectionKey null). Without this, a silver sponsor would lose the
        // page entirely rather than merely have it moved.
        var digitalTelemetry = digital.Items.Single(i => i.Href == "/Sponsor/Telemetry");
        Assert.Null(digitalTelemetry.SectionKey);
        Assert.Equal("Nav.AttendeeTelemetry", digitalTelemetry.LabelKey);
    }

    [Fact]
    public void Sponsor_zoho_and_webshop_items_carry_the_right_redirect_confirm_kind()
    {
        // §175/§176: the links that leave the hub for a third-party login carry a Redirect
        // kind so the view shows a confirm() before navigating. Build an EXHIBITOR sponsor so
        // the Zoho booth fold-out appears.
        var items = NavBuilder.Build(ParticipantRole.Sponsor, isExhibitor: true).AllItems.ToList();
        NavItem Item(string labelKey) => items.Single(i => i.LabelKey == labelKey);

        // §175 — the remaining Zoho items confirm with the Zoho message.
        // §488: the four Zoho SETUP links were removed (the hub owns that data now), so Leads and
        // Inquiries are the only ones left — they have no API, which is what §484 explains.
        var zohoLabels = new[] { "Nav.LeadsZoho", "Nav.InquiriesZoho" };
        Assert.DoesNotContain(items, i =>
            i.LabelKey is "Nav.ExhibitorProfile" or "Nav.BoothMembers"
                       or "Nav.ExhibitorMaterials" or "Nav.PromotionalBanner");
        foreach (var label in zohoLabels)
        {
            var it = Item(label);
            Assert.Equal(ExternalRedirectKind.Zoho, it.Redirect);
            Assert.True(it.External, $"{label} stays an external new-tab link.");
        }

        // Every Zoho-targeted href is tagged, and ONLY Zoho hrefs are (the in-hub leaves under the
        // same fold-out are NOT redirects).
        Assert.All(items.Where(i => i.Href.StartsWith("https://eldk27.expertslive.dk/", StringComparison.Ordinal)),
            i => Assert.Equal(ExternalRedirectKind.Zoho, i.Redirect));
        // §483: the Capture-lead failover left the menu, so the in-hub counterpart asserted here is
        // "Your Booth" — still proving a same-fold-out internal leaf carries no redirect kind.
        Assert.Equal(ExternalRedirectKind.None, items.Single(i => i.Href == "/Sponsor/Booth").Redirect);

        // §176 — the EXTERNAL webshop buy-flow confirms with the Webshop message. The
        // in-hub orders page does NOT (fixed 2026-07-07: it warned about an external
        // login that never happens, on every click).
        Assert.Equal(ExternalRedirectKind.Webshop, Item("Nav.SponsorBuyServices").Redirect);
        Assert.Equal(ExternalRedirectKind.None, Item("Nav.SponsorOrders").Redirect);

        // Non-redirect leaves (e.g. the booth run-of-show, Our Booth, Tasks) carry no kind.
        Assert.Equal(ExternalRedirectKind.None, Item("Nav.SponsorBoothRunOfShow").Redirect);
        Assert.Equal(ExternalRedirectKind.None, Item("Nav.OurBooth").Redirect);
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
        Assert.Contains("/volunteer/supervisor", hrefs);

        // 🔴 §939 — My Availability points at the WIZARD STEP; the standalone page is retired.
        // Operator 2026-08-07: one availability form, not two that can disagree.
        Assert.Contains("/Forms/Wizard?step=availability", hrefs);
        Assert.DoesNotContain("/volunteer/availability", hrefs);
        Assert.True(
            hrefs.IndexOf("/Forms/Wizard?step=availability") < hrefs.IndexOf("/volunteer/myschedule"),
            "My availability comes before My schedule (operator 2026-06-23).");

        // ⚰️ §939 — "My Tasks" is GONE for volunteers: the legacy list scored them 90% complete
        // from a task model they no longer use, while Get Started said they were done. Two answers
        // to "am I finished?", and the wrong one sends people looking for work that does not exist.
        Assert.DoesNotContain("/Tasks", hrefs);

        // §285: the guided Get-Started wizard is the inline /Forms/Wizard, top-level.
        var getStarted = g.Items.Single(i => i.Href == "/Forms/Wizard");
        Assert.Equal("Nav.GetStarted", getStarted.LabelKey);
        Assert.Null(getStarted.SectionKey);
        // §290: "Attendee telemetry" is a MAIN-menu item for volunteer crew (the shared
        // limited /Sponsor/Telemetry "who's coming" view).
        // §939 (operator 2026-08-07): for volunteers this moved OUT of the top level and INTO the
        // "Event Info" fold-out — context about the event, not a thing a volunteer acts on.
        var telemetry = g.Items.Single(i => i.Href == "/Sponsor/Telemetry");
        Assert.Equal("Nav.AttendeeTelemetry", telemetry.LabelKey);
        Assert.Equal("Nav.SectionEventLogistics", telemetry.SectionKey);

        // ⚰️ §939 — "My Tasks" is GONE for volunteers (the §301c assertion above is retired with
        // it). The legacy /Tasks list scored a finished volunteer at 90% from a task model they no
        // longer use, while Get Started said they were done. Get Started is now the only answer.
        Assert.DoesNotContain(g.Items, i => i.Href == "/Tasks");

        // §317: the Event Info fold-out in the operator's order — the four content leaves,
        // then the Sessions catalogue + the survey-results leaf, then the nested Policies
        // sub-fold-out. A volunteer (non-speaker) must NOT get the speaker-only content
        // pages (§123). Lunch moved to Register (§301c).
        var logistics = g.Sections().Single(s => s.HeadingKey == "Nav.SectionEventLogistics");
        var lh = logistics.Items.Select(i => i.Href).ToList();
        Assert.DoesNotContain("/Forms/Lunch", lh);
        Assert.Equal(new[]
        {
            // §939 — Attendee telemetry LEADS the fold-out for volunteers, because it is added
            // before the content leaves. It is also the only live, changing item in here, so
            // leading is defensible — but it is a consequence of add-order rather than a decision,
            // and moving it to the end is a one-line change.
            // 🔑 §944 — it is now added in the VOLUNTEER block (after Register) rather than the
            // evergreen block, and that is what puts "Register/Update" left of "Event Info": a
            // section takes its position from its first item. Still first WITHIN the fold-out.
            "/Sponsor/Telemetry",
            "/Info/last-event-videos", "/Info/good-to-know", "/Info/addresses", "/Info/wayfinding",
            "/Info/ceh-introduction",   // §326ag
            "/Sessions", "/Sessions/Slides",
            "https://expertslive.dk/code-of-conduct/", "https://expertslive.dk/privacy-policy/",
        }, lh);
        Assert.DoesNotContain("/Info/session-guidelines", lh);
        Assert.DoesNotContain("/Calendar", hrefs);

        // §288/§297/§301c: the "Register" menu = every register form + Party (register).
        var register = g.Sections().Single(s => s.HeadingKey == "Nav.SectionRegister");
        Assert.Equal(new[] { "/Forms/Hotel", "/Forms/Dinner", "/Forms/Lunch", "/Forms/Swag", "/Forms/Wizard?step=party" },
            register.Items.Select(i => i.Href).ToList());
        Assert.Equal("Nav.VolunteerGift", register.Items.Single(i => i.Href == "/Forms/Swag").LabelKey);
    }

    /// <summary>
    /// §944 (operator 2026-08-07): *"register/update menu item must be moved 1x left so event info is
    /// next to contact organizers"*. The bar ends with the two INFORMATIONAL items together —
    /// Event Info, then Contact Organizers (which <c>_Layout</c> always renders furthest right) — with
    /// the action fold-out before them.
    ///
    /// <para>🔑 Pinned as SECTION ORDER, not as an item index, because that is what he can see. A
    /// section takes its place in the bar from where its FIRST item is added
    /// (<c>NavModel.Sections</c>), so this ordering is an emergent property of add-order in
    /// <c>NavBuilder</c> — exactly the kind that drifts back the next time somebody adds an Event Info
    /// leaf a few lines too early.</para>
    /// </summary>
    [Fact]
    public void Volunteer_register_menu_sits_left_of_event_info()
    {
        var g = NavBuilder.Build(ParticipantRole.Volunteer, isVolunteerSupervisor: true).Groups[0];

        var headings = g.Sections()
            .Where(s => s.HeadingKey is not null)
            .Select(s => s.HeadingKey!)
            .ToList();

        var register = headings.IndexOf("Nav.SectionRegister");
        var eventInfo = headings.IndexOf("Nav.SectionEventLogistics");

        Assert.True(register >= 0, "the Register/Update fold-out must exist for volunteers");
        Assert.True(eventInfo >= 0, "the Event Info fold-out must exist for volunteers");
        Assert.True(register < eventInfo,
            $"Register/Update must render LEFT of Event Info (§944); got register={register}, eventInfo={eventInfo}");

        // …and Event Info is the LAST fold-out, so nothing separates it from Contact Organizers.
        Assert.Equal(eventInfo, headings.Count - 1);
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
    public void Games_entry_is_flag_gated_for_attendees_and_authoring_for_organizers()
    {
        // §171/§270: the player-facing "fun IT games" entry is an ON/OFF feature, DEFAULT
        // OFF (operator 2026-07-10) — attendees see /Games ONLY when the edition opts in
        // (attendeeGamesEnabled); the organizer authoring entry is in the management group.
        var attendeeDefault = NavBuilder.Build(ParticipantRole.Attendee).AllItems.Select(i => i.Href).ToList();
        Assert.DoesNotContain("/Games", attendeeDefault);

        var attendeeGamesOn = NavBuilder.Build(ParticipantRole.Attendee, attendeeGamesEnabled: true).AllItems.ToList();
        Assert.Contains("/Games", attendeeGamesOn.Select(i => i.Href));
        Assert.Equal("Nav.Games", attendeeGamesOn.Single(i => i.Href == "/Games").LabelKey);

        var organizer = NavBuilder.Build(ParticipantRole.Organizer);
        // §652 (operator 2026-07-29: "remove the fun it games from organizer menu - we have
        // disabled this functionality for now"). The authoring entry is GONE from the menu while
        // the feature is off. The page itself stays routable — same treatment as §167's "Find a
        // person" — so re-enabling it is one uncommented line, not a rebuild.
        Assert.DoesNotContain("/Organizer/Quizzes", organizer.ManagementGroup!.Items.Select(i => i.Href));

        // §182 (operator 2026-06-29): the /Games PLAYER surface belongs in the participant
        // menu (attendees), NOT the organizer admin nav. It must not appear anywhere in the
        // organizer's nav — only the authoring page (/Organizer/Quizzes) is admin-side.
        Assert.DoesNotContain("/Games", organizer.AllItems.Select(i => i.Href));
        Assert.DoesNotContain("/Games", organizer.ManagementGroup!.Items.Select(i => i.Href));
    }

    [Fact]
    public void Attendee_survey_results_leaf_is_under_event_logistics_except_speaker_info_for_speakers()
    {
        // §183 (operator 2026-06-29): a single external "Attendee Survey Result: Topics and
        // Level" leaf, opening in a new tab, for EVERY role. §290 (operator 2026-07-10): for
        // SPEAKERS it lives under the "Speaker Info" fold-out; every other role keeps it in
        // the shared Event-logistics ("Event Info") fold-out.
        const string surveyUrl = "https://eldk27.eventhub.expertslive.dk/survey/eldk27-topics/results";
        // §351-1 (operator 2026-07-26): "change for all roles under Event Info - remove the
        // menu-item 'Attendee Survey Results: Topics and Level'". The leaf is now SPEAKER-ONLY
        // (Speaker Info → Preparing My Session, where it is session-prep material) and is GONE
        // from Event Info for every other role. Asserting the ABSENCE rather than deleting the
        // test, so a future edit cannot silently restore it for everyone.
        foreach (var role in Enum.GetValues<ParticipantRole>())
        {
            var group = NavBuilder.Build(role).Groups[0];

            if (role == ParticipantRole.Speaker)
            {
                var section = group.Sections()
                    .SingleOrDefault(s => s.HeadingKey == "Nav.SectionSpeakerInfo");
                Assert.NotNull(section);
                var leaf = section!.Items.SingleOrDefault(i => i.Href == surveyUrl);
                Assert.NotNull(leaf);
                Assert.Equal("Nav.AttendeeSurveyResults", leaf!.LabelKey);
                Assert.True(leaf.External, "survey-results leaf must open in a new tab.");
                Assert.Single(group.Items, i => i.Href == surveyUrl);
            }
            else
            {
                Assert.DoesNotContain(group.Items, i => i.Href == surveyUrl);
            }
        }
    }

    [Fact]
    public void Attendee_telemetry_is_a_main_menu_item_for_crew_roles()
    {
        // §290: "Attendee Details" = the attendee telemetry — a MAIN-menu (top-level) item
        // for the crew roles: Volunteer/Media/EventPartner get the shared limited
        // /Sponsor/Telemetry "who's coming" view; the Organizer links to the fuller
        // /Organizer/Telemetry. Speakers get it under "Speaker Info" instead (§294 —
        // asserted in Speaker_menu_matches_redesign); attendees never see it.
        // §939 (operator 2026-08-07) — VOLUNTEERS now get it inside the "Event Info" fold-out
        // instead, the same shape §294 gave speakers: it is context about the event, not something
        // a volunteer acts on. Media and EventPartner deliberately keep it at the top level.
        foreach (var role in new[] { ParticipantRole.Media, ParticipantRole.EventPartner })
        {
            var item = NavBuilder.Build(role).Groups[0].Items.Single(i => i.Href == "/Sponsor/Telemetry");
            Assert.Equal("Nav.AttendeeTelemetry", item.LabelKey);
            Assert.Null(item.SectionKey);
        }

        var volunteerTelemetry = NavBuilder.Build(ParticipantRole.Volunteer).Groups[0]
            .Items.Single(i => i.Href == "/Sponsor/Telemetry");
        Assert.Equal("Nav.AttendeeTelemetry", volunteerTelemetry.LabelKey);
        Assert.Equal("Nav.SectionEventLogistics", volunteerTelemetry.SectionKey);

        var organizer = NavBuilder.Build(ParticipantRole.Organizer).Groups[0]
            .Items.Single(i => i.Href == "/Organizer/Telemetry");
        Assert.Equal("Nav.AttendeeTelemetry", organizer.LabelKey);
        Assert.Null(organizer.SectionKey);

        Assert.DoesNotContain("/Sponsor/Telemetry",
            NavBuilder.Build(ParticipantRole.Attendee).AllItems.Select(i => i.Href));
    }

    [Fact]
    public void Policies_is_a_nested_sub_foldout_inside_event_info_for_every_role()
    {
        // §297 (supersedes §290's own-submenu form): Event Info → Policies → (Privacy Policy
        // + Code of Conduct) — a two-level nested sub-fold-out via SubSectionKey, NOT its own
        // top-level fold-out and NOT flattened into Event Info. Both are external links.
        foreach (var role in Enum.GetValues<ParticipantRole>())
        {
            var items = NavBuilder.Build(role).Groups[0].Items;
            foreach (var href in new[]
                     {
                         "https://expertslive.dk/privacy-policy/",
                         "https://expertslive.dk/code-of-conduct/",
                     })
            {
                var link = items.Single(i => i.Href == href);
                Assert.Equal("Nav.SectionEventLogistics", link.SectionKey);
                Assert.Equal("Nav.SectionPolicies", link.SubSectionKey);
                Assert.True(link.External, $"{role}: {href} must open in a new tab.");
            }
        }
    }

    [Fact]
    public void Attendee_primary_entry_is_the_master_class_chooser()
    {
        // §234 UX: the Master-Class entries are 2-day-gated — build a 2-day holder.
        var attendee = NavBuilder.Build(ParticipantRole.Attendee, attendeeIsTwoDay: true).Groups[0];
        var hrefs = attendee.Items.Select(i => i.Href).ToList();

        // Operator 2026-06-21: the attendee menu is just Home + Master Class +
        // Waitlist. /Attendee is the in-hub Master Class chooser (replaced the old
        // Zoho-Bookings page) labelled "Master Class"; /Attendee/Waitlist is next.
        // §326al: /Attendee appears twice (main nav + Register fold-out) — the PRIMARY
        // entry is the un-sectioned main-nav leaf.
        var mc = attendee.Items.Single(i => i.Href == "/Forms/Wizard?step=masterclass" && i.SectionKey is null);
        Assert.Equal("Nav.MasterClass", mc.LabelKey);
        Assert.True(hrefs.IndexOf("/Forms/Wizard?step=masterclass") < hrefs.IndexOf("/Attendee/Waitlist"),
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
        "/Info/ceh-introduction",
    };

    // §289: /Info/help-promote ("Social Media Guidelines") was REMOVED from the registry —
    // its copy lives on the Help Promote page (/Speaker/Graphics) now.
    private static readonly string[] SpeakerOnlyContentHrefs =
    {
        "/Info/speaker-template", "/Info/session-guidelines", "/Info/av-stage-timer",
        "/Info/session-preview-final", "/Info/session-feedback",
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
        var group = NavBuilder.Build(role).Groups[0];
        var hrefs = group.Items.Select(i => i.Href).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // §267: for SPEAKERS the speaker-only pages group under "Speaker Info"; organizers
        // (who see every page, §123) keep them under the Event Info fold-out.
        var expectedSection = role == ParticipantRole.Speaker
            ? "Nav.SectionSpeakerInfo"
            : "Nav.SectionEventLogistics";
        foreach (var h in SpeakerOnlyContentHrefs)
        {
            Assert.True(hrefs.Contains(h), $"{role} should see speaker content page {h}.");
            Assert.Equal(expectedSection, group.Items.Single(i => i.Href == h).SectionKey);
        }
        // ...and the all-roles pages too (always under Event Info).
        foreach (var h in AllRoleContentHrefs)
            Assert.True(hrefs.Contains(h), $"{role} should see all-roles content page {h}.");
        // §289: the removed Social-Media-Guidelines page never reappears.
        Assert.DoesNotContain("/Info/help-promote", hrefs);
    }

    // ---- §288/§297: Party is a leaf under the "Register" menu (attendees excepted) ----

    [Fact]
    public void Party_is_a_leaf_under_register_for_every_non_attendee_role_not_top_level()
    {
        // §288b/§297: Party (register) moved OUT of "My tasks"/Event-logistics — for every
        // non-attendee role it is a leaf under the "Register" menu (Nav.SectionRegister),
        // never a top-level (null-section) item. §177 amends this for ATTENDEES (next test);
        // an EXHIBITOR sponsor gets it inside "Exhibitor & Booth Details" instead (§297,
        // asserted at the end + in Sponsor_menu_matches_redesign).
        foreach (var role in Enum.GetValues<ParticipantRole>())
        {
            if (role == ParticipantRole.Attendee) continue;   // §177 — attendees get it in the MAIN nav

            var group = NavBuilder.Build(role, speakerHasMasterClass: true).Groups[0];

            var party = group.Items.SingleOrDefault(i => i.Href == "/Forms/Wizard?step=party");
            Assert.NotNull(party);                                    // still present for every role
            Assert.Equal("Nav.SectionRegister", party!.SectionKey);   // the "Register" fold-out leaf
            Assert.Equal("Nav.PartySignup", party.LabelKey);          // §206: "Party Signup"

            var sections = group.Sections();
            var register = sections.Single(s => s.HeadingKey == "Nav.SectionRegister");
            Assert.Contains("/Forms/Wizard?step=party", register.Items.Select(i => i.Href));
            // Not a top-level (null-heading) leaf, and no longer under Event logistics.
            var topLevel = sections.Single(s => s.HeadingKey is null);
            Assert.DoesNotContain("/Forms/Wizard?step=party", topLevel.Items.Select(i => i.Href));
            var logistics = sections.Single(s => s.HeadingKey == "Nav.SectionEventLogistics");
            Assert.DoesNotContain("/Forms/Wizard?step=party", logistics.Items.Select(i => i.Href));
        }

        // §297: the EXHIBITOR sponsor exception — Party joins the booth fold-out.
        var exhibitor = NavBuilder.Build(ParticipantRole.Sponsor, isExhibitor: true).Groups[0];
        Assert.Equal("Nav.SectionExhibitorBooth",
            exhibitor.Items.Single(i => i.Href == "/Forms/Wizard?step=party").SectionKey);
    }

    [Fact]
    public void Attendee_party_is_a_single_prominent_main_nav_item()
    {
        // §353 (operator 2026-07-26) REVERSES §297: attendees now get the prominent MAIN-nav
        // entry labelled "Party Preday" AND a duplicate under Register/Update. The duplicate is
        // INTENTIONAL — the operator asked for both — so this test now pins TWO entries.
        var group = NavBuilder.Build(ParticipantRole.Attendee).Groups[0];

        // §326am: both deep-link into the wizard's INLINE party step (hub chrome, same tab)
        // rather than the standalone /Party page.
        const string PartyHref = "/Forms/Wizard?step=party";
        var partyItems = group.Items.Where(i => i.Href == PartyHref).ToList();
        Assert.Equal(2, partyItems.Count);

        // The MAIN-nav one carries the new label and no section.
        var main = Assert.Single(partyItems, i => i.SectionKey is null);
        Assert.Equal("Nav.PartyPreday", main.LabelKey);
        Assert.False(main.External);

        // The Register/Update one keeps the shared label.
        var register = Assert.Single(partyItems, i => i.SectionKey == "Nav.SectionRegister");
        Assert.Equal("Nav.PartySignup", register.LabelKey);
        Assert.False(register.External);

        // §351-6: the standalone /Party page is no longer linked from the nav for ANY role —
        // the wizard step is the single canonical party surface.
        Assert.DoesNotContain(group.Items, i => i.Href == "/Party");

        var sections = group.Sections();
        var topLevel = sections.Single(s => s.HeadingKey is null);
        Assert.Contains(PartyHref, topLevel.Items.Select(i => i.Href));
        // Still NOT in the Event-Info fold-out — the §297 de-duplication holds there.
        var logistics = sections.Single(s => s.HeadingKey == "Nav.SectionEventLogistics");
        Assert.DoesNotContain(PartyHref, logistics.Items.Select(i => i.Href));
    }

    [Fact]
    public void Event_info_foldout_renders_in_the_operator_order_not_alphabetized()
    {
        // §317 (operator 2026-07-24): the layout renders fold-outs in NavBuilder INSERTION
        // order — the §173d "alphabetize Event logistics" projection is retired (the
        // operator specifies the exact menu order). The Sections(resolver, keys) overload
        // itself still works (Label_sort_leaves_non_targeted_sections_in_insertion_order).
        var group = NavBuilder.Build(ParticipantRole.Speaker, speakerHasMasterClass: true).Groups[0];

        var insertion = group.Sections().Single(s => s.HeadingKey == "Nav.SectionEventLogistics");
        Assert.Equal(new[]
        {
            "/Info/last-event-videos", "/Info/good-to-know", "/Info/addresses", "/Info/wayfinding",
            "/Info/ceh-introduction",   // §326ag
            "/Sessions", "/Sessions/Slides",
            // §326f: the logo-pack zip (speakers + sponsors only) — an EXTERNAL leaf.
            "/logo-pack/download",
            "https://expertslive.dk/code-of-conduct/", "https://expertslive.dk/privacy-policy/",
        }, insertion.Items.Select(i => i.Href).ToList());
        // §288/§297: Party is NOT in this fold-out anymore — it lives under "Register".
        Assert.DoesNotContain("/Forms/Wizard?step=party", insertion.Items.Select(i => i.Href));
        // The Policies pair stays a NESTED sub-fold-out (rendered auto-open by the layout).
        // §326f/§326n: the OTHER Event-Info externals (logo pack; the chrome-less Sessions +
        // Slides pages, which now open in a new tab) are plain LEAVES with no sub-fold-out.
        foreach (var href in new[]
                 { "https://expertslive.dk/code-of-conduct/", "https://expertslive.dk/privacy-policy/" })
        {
            Assert.Equal("Nav.SectionPolicies", insertion.Items.Single(i => i.Href == href).SubSectionKey);
        }
        Assert.Null(insertion.Items.Single(i => i.LabelKey == "Nav.DownloadLogos").SubSectionKey);
        // §326n: menu items leading OUT of the hub chrome open in a new tab.
        Assert.True(insertion.Items.Single(i => i.Href == "/Sessions").External);
        Assert.True(insertion.Items.Single(i => i.Href == "/Sessions/Slides").External);
    }

    [Fact]
    public void Label_sort_leaves_non_targeted_sections_in_insertion_order()
    {
        // §173d: only the named section(s) are alphabetized; every other section keeps its
        // insertion order. A sponsor's Webshop fold-out (not targeted) must be untouched.
        string Label(NavItem i) => i.FallbackLabel ?? i.LabelKey ?? i.Href;
        var group = NavBuilder.Build(ParticipantRole.Sponsor, isExhibitor: true).Groups[0];

        var viaPlain = group.Sections()
            .Single(s => s.HeadingKey == "Nav.SectionSponsorWebshop").Items.Select(i => i.Href).ToList();
        var viaSorted = group.Sections(Label, "Nav.SectionEventLogistics")
            .Single(s => s.HeadingKey == "Nav.SectionSponsorWebshop").Items.Select(i => i.Href).ToList();

        Assert.Equal(viaPlain, viaSorted);
    }

    // ---- §213/§285: every role has a "Get Started" entry in its MAIN (primary) nav -----
    // §285: every role's Get-Started entry now opens the INLINE wizard at /Forms/Wizard —
    // one route drives the generic wizard (Organizer/Media/EventPartner/Volunteer/Attendee),
    // the speaker onboarding chain (§28) and the sponsor plan (§32, via SponsorWizardService).
    // The item must live in the PRIMARY group (Groups[0]) — not a fold-out — so it is always
    // one tap away. The per-role map stays so a future bespoke route is a one-line change.
    private static readonly Dictionary<ParticipantRole, string> GetStartedRouteByRole = new()
    {
        [ParticipantRole.Organizer]    = "/Forms/Wizard",
        [ParticipantRole.Speaker]      = "/Forms/Wizard",
        [ParticipantRole.Volunteer]    = "/Forms/Wizard",
        [ParticipantRole.Sponsor]      = "/Forms/Wizard",
        [ParticipantRole.Attendee]     = "/Forms/Wizard",
        [ParticipantRole.Media]        = "/Forms/Wizard",
        [ParticipantRole.EventPartner] = "/Forms/Wizard",
    };

    [Fact]
    public void Get_started_route_map_covers_every_role()
    {
        // Guard: if a new ParticipantRole is added, this map (and the per-role assertion
        // below) must be extended — §213 requires EVERY role to have a get-started entry.
        foreach (var role in Enum.GetValues<ParticipantRole>())
            Assert.True(GetStartedRouteByRole.ContainsKey(role),
                $"§213: role {role} has no expected Get-Started route — add it.");
    }

    [Theory]
    [MemberData(nameof(AllRoleData))]
    public void Every_role_has_a_get_started_entry_in_the_main_nav(ParticipantRole role)
    {
        // §213: the Get-Started item is in the PRIMARY/main group (Groups[0]) as a
        // top-level (null-section) leaf, pointing at the role's own get-started wizard.
        var primary = NavBuilder.Build(role, speakerHasMasterClass: true).Groups[0];
        var expected = GetStartedRouteByRole[role];

        var getStarted = primary.Items.FirstOrDefault(i =>
            i.Href == expected && i.SectionKey is null);

        Assert.True(getStarted is not null,
            $"§213: role {role} must have a top-level main-nav Get-Started entry routed to {expected}.");
    }

    public static TheoryData<ParticipantRole> AllRoleData()
    {
        var data = new TheoryData<ParticipantRole>();
        foreach (var r in Enum.GetValues<ParticipantRole>()) data.Add(r);
        return data;
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

