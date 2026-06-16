using System.Collections.Generic;
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
    public static NavModel Build(ParticipantRole role)
    {
        var groups = new List<NavGroup> { BuildParticipantGroup(role) };

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
    private static NavGroup BuildParticipantGroup(ParticipantRole role)
    {
        var items = new List<NavItem>
        {
            new("/", "Nav.Home", ExactMatch: true),
        };

        // Sponsors use the company-shared /Sponsor/Tasks entry, not the generic
        // assigned-to-me /Tasks list.
        if (role != ParticipantRole.Sponsor)
        {
            items.Add(new("/Tasks", "Nav.MyTasks"));
        }

        // Evergreen, every-role entries.
        items.Add(new("/Profile", "Nav.MyProfile"));
        items.Add(new("/Resources", "Nav.Resources"));
        // Public sessions listing — visible to every signed-in role.
        items.Add(new("/Sessions", "Nav.Sessions"));

        // Hotel + Dinner: crew/speaker/organizer.
        if (role is ParticipantRole.Organizer
            or ParticipantRole.Speaker
            or ParticipantRole.MasterclassSpeaker
            or ParticipantRole.Volunteer
            or ParticipantRole.Video
            or ParticipantRole.Camera)
        {
            items.Add(new("/Forms/Hotel", "Nav.Hotel"));
            items.Add(new("/Forms/Dinner", "Nav.Dinner"));
        }

        // Speaker hub + speaker self-service.
        if (role is ParticipantRole.Speaker or ParticipantRole.MasterclassSpeaker)
        {
            items.Add(new("/Speaker", "Nav.SpeakerHub"));
            items.Add(new("/Speaker/Questions", "Nav.SpeakerQuestions"));
            items.Add(new("/Forms/Speaker", "Nav.Speaker"));
            items.Add(new("/Speaker/Graphics", "Nav.ShareGraphics"));
            items.Add(new("/Forms/Travel", "Nav.Travel"));
        }

        // Lunch + Swag: volunteer/speaker/organizer.
        if (role is ParticipantRole.Volunteer
            or ParticipantRole.Speaker
            or ParticipantRole.MasterclassSpeaker
            or ParticipantRole.Organizer)
        {
            items.Add(new("/Forms/Lunch", "Nav.Lunch"));
            items.Add(new("/Forms/Swag", "Nav.Swag"));
        }

        // Volunteer shift wizard.
        if (role is ParticipantRole.Volunteer or ParticipantRole.Organizer)
        {
            items.Add(new("/Forms/VolunteerWizard", "Nav.VolunteerShifts"));
        }

        // Volunteer unified "My schedule" (all assigned shifts/tasks, time-ordered,
        // with the go-to people + a personal calendar). Volunteers only — an
        // organizer manages the schedule via the Volunteers hub, not this view.
        if (role == ParticipantRole.Volunteer)
        {
            items.Add(new("/volunteer/myschedule", "Nav.MySchedule"));
            // Self-service shift management: confirm / decline / request a swap.
            items.Add(new("/volunteer/myshifts", "Nav.MyShifts"));
        }

        // Attendee area.
        if (role == ParticipantRole.Attendee)
        {
            items.Add(new("/Attendee", "Nav.MyAttendance"));
        }

        // Sponsor area. The portal is the single self-service home (REQUIREMENTS §20
        // Sponsor) and leads the group; the existing pages are reached from it + here.
        if (role == ParticipantRole.Sponsor)
        {
            items.Add(new("/Sponsor/Portal", "Nav.SponsorPortal"));
            items.Add(new("/Sponsor", "Nav.SponsorEngagement"));
            items.Add(new("/Sponsor/Tasks", "Nav.SponsorTasks"));
            items.Add(new("/Sponsor/Logistics", "Nav.Logistics"));
            items.Add(new("/Sponsor/Contact", "Nav.Contact"));
        }

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
        // Section heading resx keys (REQUIREMENTS §21 organizer-nav grouping).
        // null section = the leading "prominent" bucket (no heading).
        const string People      = "Nav.OrgSectionPeople";
        const string Sessions    = "Nav.OrgSectionSessions";
        const string Comms       = "Nav.OrgSectionComms";
        const string Sponsors    = "Nav.OrgSectionSponsors";
        const string Volunteers  = "Nav.OrgSectionVolunteers";
        const string Logistics   = "Nav.OrgSectionLogistics";

        var items = new List<NavItem>
        {
            // --- Prominent / ungrouped (render at the top, no section heading) ---
            // Organizer home — landing root that links out to every hub below.
            new("/Organizer", "Nav.OrgArea"),

            // Command center (the "what needs my attention" overview, REQUIREMENTS
            // §20) and the Dashboard are both kept prominent as their own
            // top-level entries. The older cross-role Overview is folded into the
            // dashboard/home as a section, so it is no longer a separate top-level
            // item — its route /Organizer/Overview is unchanged and still works.
            new("/Organizer/CommandCenter", "Nav.OrgCommandCenter"),
            new("/Organizer/Dashboard", "Nav.OrgDashboard"),

            // Global "find a person fast" search — kept prominent (no section)
            // because it is the single most frequent organizer action: jump from
            // a name/email fragment straight to the right person (REQUIREMENTS §20).
            new("/Organizer/FindPerson", "Nav.OrgFindPerson"),

            // --- People ---
            new("/Organizer/People", "Nav.OrgPeople", SectionKey: People),

            // --- Sessions & speakers ---
            // Hub route is /Organizer/Content so it does not collide with the
            // existing /Organizer/Sessions feature page (which it links to).
            new("/Organizer/Content", "Nav.OrgSessionsHub", SectionKey: Sessions),

            // --- Comms (incl. marketing / SoMe) ---
            new("/Organizer/Comms", "Nav.OrgComms", SectionKey: Comms),
            new("/Organizer/SoMe", "Nav.OrgSoMe", SectionKey: Comms),

            // --- Sponsors (points at the EXISTING SponsorAdmin hub, no new page) ---
            new("/Organizer/SponsorAdmin/Index", "Nav.OrgSponsorsHub", SectionKey: Sponsors),

            // --- Volunteers ---
            new("/Organizer/Volunteers", "Nav.OrgVolunteers", SectionKey: Volunteers),

            // --- Logistics (event ops + setup + audit) ---
            // The Logistics hub page links to the on-site Exports & run-sheets
            // page (REQUIREMENTS §20 Organizer) — kept off the top-level nav so
            // the consolidated menu stays short.
            new("/Organizer/Logistics", "Nav.OrgLogistics", SectionKey: Logistics),
            new("/Organizer/Setup", "Nav.OrgSetup", SectionKey: Logistics),
            // Audit lives under Logistics/ops rather than as a stray flat link.
            new("/Organizer/ImpersonationLog", "Nav.OrgImpersonationLog", SectionKey: Logistics),
        };

        return new NavGroup(HeadingKey: "Nav.OrgArea", Items: items, IsManagement: true);
    }
}
