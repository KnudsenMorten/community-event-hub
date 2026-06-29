using System.Collections.Generic;
using CommunityHub.Core.Content;
using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Navigation;

/// <summary>
/// Pure, server-side nav composition: given a signed-in participant's role,
/// returns the nav split into two clear, role-gated groups —
///   1. the participant / "My event" group (Home, profile, tasks, resources +
///      the self-service forms each role is entitled to), always visible;
///   2. the organizer / "Organizer area" management group, returned ONLY for
///      <see cref="ParticipantRole.Organizer"/>. A non-organizer never receives
///      the management items, so the gate is genuinely server-side (not CSS).
///
/// Routes are never renamed or removed here — this only regroups + relocates the
/// existing links and adds the organizer pages that already exist but were not
/// wired into the nav. Labels are resx KEYS (or a literal fallback when no key
/// exists yet) so the view layer keeps ownership of i18n.
/// </summary>
public static class NavBuilder
{
    /// <param name="isVolunteerSupervisor">
    /// True when the signed-in VOLUNTEER actually supervises at least one bucket.
    /// The volunteer "Supervisor" dashboard item is shown ONLY to real supervisors
    /// (a non-supervisor volunteer would otherwise hit a dead-end empty page). The
    /// caller (the layout) computes this for volunteers only; it is ignored for
    /// every other role.
    /// </param>
    /// <param name="isExhibitor">
    /// True when the signed-in SPONSOR has a physical booth (SponsorInfo.HasBooth).
    /// The exhibitor/booth + leads menu blocks are shown ONLY to real exhibitors —
    /// a digital-only sponsor has no booth, so those Zoho booth/lead items would be
    /// dead links for them. The caller (the layout) computes this for sponsors only;
    /// it is ignored for every other role.
    /// </param>
    /// <param name="speakerHasMasterClass">
    /// True when the signed-in SPEAKER is linked (via <see cref="SessionSpeaker"/>) to at
    /// least one <see cref="SessionType.MasterClass"/> session in the active edition (§138).
    /// The "Master Class Q&amp;A" menu item is shown ONLY then — a speaker who presents no
    /// master class has no Group Q&amp;A board, so the item would land on an empty page. The
    /// page itself stays reachable by direct URL (it shows a friendly empty state). The
    /// caller (the layout) computes this for speakers only; it is ignored for every other role.
    /// </param>
    public static NavModel Build(ParticipantRole role, bool isVolunteerSupervisor = false, bool isExhibitor = false, bool speakerHasMasterClass = false)
    {
        var groups = new List<NavGroup> { BuildParticipantGroup(role, isVolunteerSupervisor, isExhibitor, speakerHasMasterClass) };

        // Server-side management gate: only an organizer ever gets these items.
        if (role == ParticipantRole.Organizer)
        {
            groups.Add(BuildManagementGroup());
        }

        return new NavModel(groups);
    }

    /// <summary>
    /// The always-visible participant / "My event" group. Mirrors the prior
    /// per-role visibility exactly (no route added or dropped) — Home, My tasks,
    /// My profile, Resources, then the role-specific hubs + self-service forms.
    /// </summary>
    private static NavGroup BuildParticipantGroup(ParticipantRole role, bool isVolunteerSupervisor, bool isExhibitor, bool speakerHasMasterClass)
    {
        var items = new List<NavItem>
        {
            new("/", "Nav.Home", ExactMatch: true),
            // §164: the Party RSVP — visible to EVERY role + every ticket type so anyone can sign up
            // and edit their answer. Placed right after Home so it's easy to find.
            new("/Party", "Nav.Party"),
        };

        // §43: generic "Get started" guided wizard for the roles WITHOUT a bespoke
        // wizard — Organizer, Media, EventPartner (Volunteer gets it too, but placed
        // inside its own §47-ordered block below; Speaker §28 + Sponsor §32 keep
        // their own dedicated entries). Added right after Home so it's the obvious
        // first action.
        if (role is ParticipantRole.Organizer
            or ParticipantRole.Media or ParticipantRole.EventPartner)
        {
            items.Add(new("/Forms/GetStarted", "Nav.GetStarted"));
        }

        // Sponsors use the company-shared /Sponsor/Tasks entry, not the generic
        // assigned-to-me /Tasks list. Attendees get a deliberately MINIMAL menu
        // (Home + Master Class + Waitlist only — operator 2026-06-21), so they are
        // excluded from My tasks / My profile / Resources / Sessions here.
        // Speakers excluded too (operator 2026-06-23 menu: Home, My Hub Profile,
        // Bio, My sessions, Event Logistics, Contact — no top-level My tasks).
        // Volunteers excluded here too (§47): they still get the /Tasks list, but
        // relocated to AFTER "My Hub Profile" and relabelled "My Onboarding Tasks"
        // inside the volunteer block below — so it does not appear before profile.
        if (role != ParticipantRole.Sponsor && role != ParticipantRole.Attendee
            && role != ParticipantRole.Speaker && role != ParticipantRole.Volunteer)
        {
            items.Add(new("/Tasks", "Nav.MyTasks"));
        }

        if (role != ParticipantRole.Attendee)
        {
            // Evergreen entries — every role EXCEPT the minimal attendee menu.
            items.Add(new("/Profile", "Nav.MyProfile"));
            // Resources removed for attendees + speakers + sponsors + volunteers
            // (operator 2026-06-21); organizer + media crew keep it.
            if (role is not ParticipantRole.Speaker
                     and not ParticipantRole.Sponsor and not ParticipantRole.Volunteer)
                items.Add(new("/Resources", "Nav.Resources"));
            // The public Sessions catalogue moved into the shared "Event logistics" fold-out for
            // EVERY role (operator 2026-06-28 §153) — see just before the content pages below.
        }

        // Hotel + Dinner: organizer + media crew. Speakers AND volunteers get these
        // inside their own "Event logistics" fold-out below (operator 2026-06-21).
        if (role is ParticipantRole.Organizer
            or ParticipantRole.Media
            or ParticipantRole.EventPartner)
        {
            items.Add(new("/Forms/Hotel", "Nav.Hotel"));
            items.Add(new("/Forms/Dinner", "Nav.Dinner"));
        }

        // Lunch + Swag: organizer + media crew + event partners — all entitled per
        // OrderEntitlements (mirrors the Hotel/Dinner crew block above). Speakers +
        // volunteers get these in their Event logistics fold-out below.
        if (role is ParticipantRole.Organizer
            or ParticipantRole.Media
            or ParticipantRole.EventPartner)
        {
            items.Add(new("/Forms/Lunch", "Nav.Lunch"));
            items.Add(new("/Forms/Swag", "Nav.Swag"));
        }

        // Speaker hub + speaker self-service. The speaker nav was a long flat
        // list; group the speaker-SPECIFIC items under a "Speaker" section
        // heading (mirrors the organizer SectionKey grouping pattern). Added
        // LAST for speaker roles so the section's items are contiguous in the
        // flat list — NavGroup.Sections() then preserves order exactly
        // (loss-free). The crew-shared forms (Hotel/Dinner/Lunch/Swag) above
        // stay ungrouped — they are not speaker-only. Pure IA: no route renamed
        // or dropped.
        // Speaker menu (operator 2026-06-21): the old flat "Speaker" hub is dissolved.
        // My Sessions (the per-speaker sessions hub — also the route to attendee
        // questions / evaluations / SoMe graphics from each session), Bio (the speaker
        // form), a per-role Calendar, an Event-logistics fold-out, and Contact
        // Organizers. No route dropped — Questions/Evaluations/Graphics remain reached
        // from the My Sessions hub.
        if (role is ParticipantRole.Speaker)
        {
            // operator 2026-06-24 (§26c): "Bio" replaced by the consolidated
            // "Speaker Details" page; "Help Promote" added (SoMe graphics). Then My
            // sessions, the Event-logistics fold-out, Contact (last).
            // §28: guided onboarding wizard — the single entry point that chains the
            // speaker's initial tasks (Speaker Details, Hotel, Dinner, …) with progress.
            // (Calendar entries removed: the user-facing calendar UI is retired.)
            items.Add(new("/Forms/SpeakerWizard", "Nav.SpeakerOnboarding"));
            items.Add(new("/Speaker/Details", "Nav.SpeakerDetails"));
            items.Add(new("/Speaker/Tasks", "Nav.MyTasks"));   // operator 2026-06-24: right after Speaker Details
            // §138 (operator 2026-06-27): the standalone "Am I ready?" speaker nav entry is
            // removed — the readiness rollup (score + what's missing) now lives at the TOP of
            // the My Tasks page (so the journey reads details → tasks-with-readiness). The
            // /Speaker/Readiness page + the /Organizer/SpeakerReadiness roster stay intact;
            // only this speaker-facing menu item is dropped.
            items.Add(new("/Speaker", "Nav.MySessions"));
            // §86 (operator 2026-06-26): the speaker Master Class Q&A area — see the
            // audience questions on your session(s), reply (shared with co-speakers), and
            // edit each Master Class attendee landing page. §138 (operator 2026-06-27): shown
            // ONLY when the speaker presents at least one master class (speakerHasMasterClass);
            // a speaker with zero master classes has no Group Q&A board, so the item would land
            // on an empty page. The page stays reachable by direct URL (friendly empty state).
            if (speakerHasMasterClass)
                items.Add(new("/Speaker/Questions", "Nav.MasterClassQa"));
            items.Add(new("/Speaker/Graphics", "Nav.HelpPromote"));

            const string EventLogistics = "Nav.SectionEventLogistics";
            items.Add(new("/Forms/Hotel", "Nav.Hotel", SectionKey: EventLogistics));
            items.Add(new("/Forms/Dinner", "Nav.Dinner", SectionKey: EventLogistics));
            items.Add(new("/Forms/Lunch", "Nav.Lunch", SectionKey: EventLogistics));
            items.Add(new("/Forms/Swag", "Nav.SpeakerGift", SectionKey: EventLogistics));
            items.Add(new("/Forms/Travel", "Nav.Travel", SectionKey: EventLogistics));
            // (Contact Organizers is appended LAST for every role — see end of method.)
        }

        // Volunteer shift wizard — organizer only. For volunteers the sign-up is being
        // moved to an anonymous survey (operator 2026-06-21), so it is NOT in the
        // volunteer menu anymore.
        if (role is ParticipantRole.Organizer)
        {
            items.Add(new("/Forms/VolunteerWizard", "Nav.VolunteerShifts"));
        }

        // Volunteer menu (operator 2026-06-21): My schedule (now the single home for
        // the volunteer's assigned shifts AND tasks — the separate "My shifts" /
        // "My volunteer tasks" tabs are merged here), My Availability (per-day
        // available/blocked the coordinators schedule against), the supervisor
        // dashboard, and an Event-logistics fold-out. Resources + Sessions are
        // removed (above); the shift-signup wizard is gone (becomes an anonymous
        // survey).
        if (role == ParticipantRole.Volunteer)
        {
            // §43: guided "Get started" wizard — the single entry point that walks a
            // volunteer through Profile → Availability → entitlement logistics with
            // progress. Placed FIRST in the volunteer block (just after "My Hub
            // Profile", which is added in the evergreen block above) so it leads, and
            // the §47 order that follows — My Onboarding Tasks, My Availability, My
            // Assignments — is preserved.
            items.Add(new("/Forms/GetStarted", "Nav.GetStarted"));
            // §47: "My Onboarding Tasks" (the generic /Tasks list, volunteer-only
            // label) is placed here so it renders AFTER "My Hub Profile" (added
            // above) instead of before it. The shared "Nav.MyTasks" key is left
            // untouched (organizer/media/speaker still read it); volunteers use the
            // dedicated "Nav.MyOnboardingTasks" label so other roles are unaffected.
            items.Add(new("/Tasks", "Nav.MyOnboardingTasks"));
            // My Availability first (operator 2026-06-23) — volunteers set it before
            // they have a schedule to look at.
            items.Add(new("/volunteer/availability", "Nav.MyAvailability"));
            // §47: relabelled "My Assignments" (was "My schedule"); volunteer-only key.
            items.Add(new("/volunteer/myschedule", "Nav.MyAssignments"));
            // Supervisor dashboard: shown ONLY to volunteers who actually supervise a
            // bucket (operator 2026-06-21). A non-supervisor volunteer would otherwise
            // see a menu item that lands on a "you are not a supervisor" dead end.
            if (isVolunteerSupervisor)
                items.Add(new("/volunteer/supervisor", "Nav.VolunteerSupervisor"));

            const string EventLogistics = "Nav.SectionEventLogistics";
            items.Add(new("/Forms/Hotel", "Nav.Hotel", SectionKey: EventLogistics));
            items.Add(new("/Forms/Dinner", "Nav.Dinner", SectionKey: EventLogistics));
            items.Add(new("/Forms/Lunch", "Nav.Lunch", SectionKey: EventLogistics));
            items.Add(new("/Forms/Swag", "Nav.VolunteerGift", SectionKey: EventLogistics));
            // (Calendar "Important dates" entry removed: the user-facing calendar UI is retired.)
        }

        // Attendee area — MINIMAL menu (operator 2026-06-21): Home (added at top) +
        // Master Class + Waitlist only. The in-hub Master Class chooser lives at
        // /Attendee (replaces the old Zoho-Bookings deep-link); the waitlist view at
        // /Attendee/Waitlist. My Event / My Plan / Sessions / Tasks / Profile /
        // Resources are intentionally not shown to attendees.
        if (role == ParticipantRole.Attendee)
        {
            items.Add(new("/Attendee", "Nav.MasterClass"));
            // My plan removed (operator 2026-06-23) — handled in Zoho Backstage, not
            // the hub.
            items.Add(new("/Attendee/Waitlist", "Nav.Waitlist"));
            // Master Class Q&A shortcut (operator 2026-06-24): redirects to the
            // attendee's confirmed Master Class page, which hosts the shared Q&A board.
            items.Add(new("/Attendee/MasterClassQa", "Nav.MasterClassQa"));
            // §171: the attendee "fun IT games" — three timed learning quizzes (AI /
            // Intune / Security) with a leaderboard. Player-facing entry, default ON for
            // attendees (ungated — a fun core surface, like the Party RSVP).
            items.Add(new("/Games", "Nav.Games"));
        }

        // Sponsor menu (operator 2026-06-21). The redundant Sponsor Portal + the
        // standalone Capture-lead tab are removed; engagement details become the
        // "Sponsor Webshop" fold-out; two new fold-outs surface the Zoho exhibitor
        // dashboard (Exhibitor & Booth Details, Leads). Resources + Sessions removed
        // above. NOTE: the eldk27.expertslive.dk Zoho URLs are this edition's
        // exhibitor dashboard; for the community mirror they should move to config.
        if (role == ParticipantRole.Sponsor)
        {
            const string Zoho = "https://eldk27.expertslive.dk/#/exhibitor-dashboard/";

            // §32: guided "Get started" wizard — the single entry point that walks a
            // sponsor through the Company Details sections with progress. First item.
            items.Add(new("/Sponsor/GetStarted", "Nav.SponsorGetStarted"));

            // Sponsor Webshop (renamed from "Engagement details"): the external
            // webshop buy-flow + the internal hub sections for orders / linked contacts.
            const string Webshop = "Nav.SectionSponsorWebshop";
            items.Add(new("https://expertslive.dk/sponsor", "Nav.SponsorBuyServices", SectionKey: Webshop, External: true));
            // Orders + Linked-contacts are sections of the SAME /Sponsor page, so
            // they're one menu entry (operator 2026-06-23).
            items.Add(new("/Sponsor", "Nav.SponsorOrders", SectionKey: Webshop));

            // Company Details — the in-hub self-service page where a sponsor/exhibitor
            // maintains vital company info (top-level so it's easy to find).
            items.Add(new("/Sponsor/CompanyDetails", "Nav.SponsorCompanyDetails"));

            // §135 (operator 2026-06-27): the standalone "Deliverables" nav entry is removed —
            // the deliverables rollup (X of N done, % + the still-missing/overdue items with
            // deep links) now lives at the TOP of Sponsor My Tasks (mirrors the speaker §138
            // readiness move). The /Sponsor/Deliverables page + the /Organizer/SponsorDeliverables
            // board + SponsorDeliverablesService stay intact; only this sponsor-facing menu item
            // is dropped.

            // Attendee telemetry — the "who's coming" stats. §55: link to the
            // AUTHENTICATED in-area page (same ranked tables/filters as the public
            // page, reached without leaving the hub), NOT the external public link.
            items.Add(new("/Sponsor/Telemetry", "Nav.AttendeeTelemetry"));

            // Exhibitor & Booth Details (Zoho, external) — booth-only. A digital-only
            // sponsor (no physical booth) gets none of these, so they are gated behind
            // isExhibitor (SponsorInfo.HasBooth, computed by the caller for sponsors).
            // §162 (operator 2026-06-28): "Exhibitor & Booth Details" is now the SINGLE booth
            // fold-out — the booth profile/members/materials/banner, "Your Booth" (booth number +
            // expo map), AND the whole Leads group (Leads / Inquiries / Capture-leads) all live
            // here, instead of Leads being a separate fold-out and "Your Booth" sitting under Event
            // logistics. All booth-only (isExhibitor); kept contiguous so they group as one section.
            if (isExhibitor)
            {
                const string Booth = "Nav.SectionExhibitorBooth";
                items.Add(new($"{Zoho}booth-info", "Nav.ExhibitorProfile", SectionKey: Booth, External: true));
                items.Add(new($"{Zoho}booth-members", "Nav.BoothMembers", SectionKey: Booth, External: true));
                items.Add(new($"{Zoho}booth-materials", "Nav.ExhibitorMaterials", SectionKey: Booth, External: true));
                items.Add(new($"{Zoho}expo-promo-banner", "Nav.PromotionalBanner", SectionKey: Booth, External: true));
                // "Your Booth" — booth number + expo map (moved here from Event logistics).
                items.Add(new("/Sponsor/Booth", "Nav.OurBooth", SectionKey: Booth));
                // Leads group (moved here from its own "Leads" fold-out): the Zoho lead/inquiry
                // lists + the in-hub Capture-lead failover for when Zoho is down. §163: the whole
                // group is gated behind the "sponsor-leads" feature (DEFAULT OFF) — the Zoho leads
                // API doesn't exist yet, so the operator turns this ON only to expose the failover.
                items.Add(new($"{Zoho}lead-list", "Nav.LeadsZoho", SectionKey: Booth, External: true, FeatureKey: "sponsor-leads"));
                items.Add(new($"{Zoho}inquiry-list", "Nav.InquiriesZoho", SectionKey: Booth, External: true, FeatureKey: "sponsor-leads"));
                items.Add(new("/Sponsor/CaptureLead", "Nav.CaptureLeadFailover", SectionKey: Booth, FeatureKey: "sponsor-leads"));
            }

            items.Add(new("/Sponsor/Tasks", "Nav.SponsorTasks"));

            // §135 (operator 2026-06-27): the booth run-of-show (key dates & times) is now a
            // LEAF inside the SHARED "Event logistics" fold-out (Nav.SectionEventLogistics) —
            // it is NO LONGER a second standalone "Event logistics" entry that read identically
            // to the content-hub Event-Logistics fold-out below. Added here (before the §104–§123
            // content pages join the same section) so it LEADS the fold-out; the order then reads
            // Booth run-of-show, Wayfinding, Good to know, Addresses, Check out last event.
            items.Add(new("/Sponsor/Logistics", "Nav.SponsorBoothRunOfShow", SectionKey: "Nav.SectionEventLogistics"));
            // ("Your Booth" moved up into the Exhibitor & Booth Details fold-out — §162.)
            // (Contact Organizers is appended LAST for every role — see end of method.)
        }

        // §104–§123: operator-authored CONTENT pages, wired in entirely from the
        // ContentPageRegistry (the single source of truth) so a new .md + registry row
        // surfaces in the nav with no further wiring. They render under the SAME
        // "Event logistics" fold-out used by the entitlement forms above (so speakers/
        // volunteers see one combined fold-out), and are ROLE-SCOPED per §123:
        // ForRole() returns only the pages this role may see — the all-roles pages
        // (Wayfinding, Good to know, Addresses, Check out our last event) for everyone,
        // plus the speaker-only pages (Speaker template, Session Guidelines, A/V &
        // Stage-timer, Session Preview/Final, Session feedback / evaluations, Help
        // Promote) for speakers; organizers see every page. Each links to the generic
        // /Info/{slug} content page and uses the page Title as its label (no resx key —
        // operator-authored copy lives in the Markdown, not the resx).
        // (A different SectionKey local name avoids colliding with the inner-scope
        // "EventLogistics" consts declared in the speaker/volunteer blocks above.)
        const string ContentLogisticsSection = "Nav.SectionEventLogistics";
        // §153 (operator 2026-06-28): the public Sessions catalogue is a LEAF inside the shared
        // "Event logistics" fold-out for EVERY role (was a top-level item, hidden for sponsors/
        // volunteers/speakers). Added at the head of the content-logistics cluster so it sits with
        // the other Event-logistics leaves regardless of role.
        items.Add(new("/Sessions", "Nav.Sessions", SectionKey: ContentLogisticsSection));
        foreach (var page in ContentPageRegistry.ForRole(role))
        {
            items.Add(new($"/Info/{page.Slug}", LabelKey: null,
                FallbackLabel: page.Title, SectionKey: ContentLogisticsSection));
        }

        // Policies — a foldout menu (Privacy Policy + Code of Conduct) for EVERY role,
        // just before Contact Organizers (operator 2026-06-25). External links.
        const string Policies = "Nav.SectionPolicies";
        items.Add(new("https://expertslive.dk/privacy-policy/", "Nav.PrivacyPolicy", SectionKey: Policies, External: true));
        items.Add(new("https://expertslive.dk/code-of-conduct/", "Nav.CodeOfConduct", SectionKey: Policies, External: true));

        // Contact Organizers — ALWAYS the furthest-right (last) menu item, and the SAME
        // shared /Contact page for EVERY role (operator 2026-06-28: one consistent contact
        // page — generic email + phone, then the organizer-team cards). Sponsors no longer
        // get a separate page.
        items.Add(new("/Contact", "Nav.ContactOrganizers"));

        // No heading on the primary group — it IS the primary nav.
        return new NavGroup(HeadingKey: null, Items: items, IsManagement: false);
    }

    /// <summary>
    /// The organizer / management group, consolidated (phase-1 of the nav/IA
    /// restructure) from ~35 flat links into ~8 grouped <em>hub</em> entries plus
    /// the prominent Dashboard and the standalone Audit log. Each hub is a thin
    /// card-link landing page (under <c>/Organizer/*</c>) that fans out to the
    /// individual feature pages — so the top-level menu stays short while every
    /// existing feature page is still reachable in two clicks. No route is
    /// renamed or removed: the deep-links (e.g. <c>/Organizer/Participants</c>)
    /// all still resolve; they are simply reached via their hub now.
    ///
    /// Hubs and the feature pages they front:
    ///   • People       → Participants, PreselectionQueue, Onboarding, Attendees, ActionQueue
    ///   • Comms        → EmailCenter, EmailLog, Broadcast, SendInvitations, SendWelcomeLogin, SpeakerReminders
    ///   • Sessions     → Speakers, Sessions, SessionQuestions, SessionEvaluations, SessionizeImport, SessionizeEndpointSettings
    ///   • Sponsors     → the existing /Organizer/SponsorAdmin hub (Sponsors directory, Tasks, Leads, Dashboard, AppGame)
    ///   • Volunteers   → VolunteerStructure, BucketAllocation
    ///   • Logistics    → Hotels, HotelAssignments, Swag, TravelReimbursements, Lunch, GroupPhotos
    ///   • Marketing    → Graphics, SoMeQueue, SoMeSettings, AssetLocations
    ///   • Setup        → CalendarSettings + cross-cutting config
    ///
    /// Organizer-only — see <see cref="Build"/>.
    ///
    /// REQUIREMENTS §21 "Group the organizer nav": the consolidated hubs are now
    /// additionally tagged with a <see cref="NavItem.SectionKey"/> so the menu
    /// renders as a small set of named, collapsible sub-groups
    /// (People / Sessions / Comms / Sponsors / Volunteers / Logistics) instead of
    /// one flat list — pure information architecture, no route renamed/removed and
    /// every entry still present. The prominent overview entries (Organizer home,
    /// Command center, Dashboard) stay ungrouped at the top (null section).
    /// </summary>
    private static NavGroup BuildManagementGroup()
    {
        // Flat, hub-level menu — the feature pages live on the hub button-grids
        // (the _HubGrid partial on each hub landing page), NOT in this menu.
        var items = new List<NavItem>
        {
            new("/Organizer", "Nav.OrgArea"),
            new("/Organizer/CommandCenter", "Nav.OrgCommandCenter"),
            new("/Organizer/Dashboard", "Nav.OrgDashboard"),
            // §55: "who's coming" attendee telemetry — the AUTHENTICATED in-area
            // organizer page (same ranked tables/filters as the public page), not the
            // external public link.
            new("/Organizer/Telemetry", "Nav.AttendeeTelemetry"),
            // §167: "Find a person" dropped from the nav — the Participants page already searches by
            // name/email. The page stays reachable by URL; it's no longer a redundant menu entry.

            new("/Organizer/People", "Nav.OrgPeople"),
            new("/Organizer/Content", "Nav.OrgSessionsHub"),
            new("/Organizer/Comms", "Nav.OrgComms"),
            new("/Organizer/SoMe", "Nav.OrgSoMe"),
            new("/Organizer/SponsorAdmin/Index", "Nav.OrgSponsorsHub"),
            new("/Organizer/Volunteers", "Nav.OrgVolunteers"),
            new("/Organizer/Logistics", "Nav.OrgLogistics"),
            new("/Organizer/Setup", "Nav.OrgSetup"),
            // §171: authoring for the attendee "fun IT games" quizzes — view/add/edit/
            // disable quizzes + questions + see the leaderboards. A direct organizer entry
            // (its own page, not fronted by a hub); flat (no SectionKey) like the others.
            new("/Organizer/Quizzes", "Nav.OrgQuizzes"),

            new("/Organizer/ImpersonationLog", "Nav.OrgImpersonationLog"),
        };

        return new NavGroup(HeadingKey: "Nav.OrgArea", Items: items, IsManagement: true);
    }
}
