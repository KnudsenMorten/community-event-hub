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
    /// <summary>§183: the attendee topic/level survey results (external, opens in a new tab).
    /// One constant — speakers link it under Speaker Info, every other role under Event Info.</summary>
    private const string AttendeeSurveyResultsUrl =
        "https://eldk27.eventhub.expertslive.dk/survey/eldk27-topics/results";

    /// <summary>§326f-c (operator 2026-07-25: "have everything run on the app reg to make
    /// it consistent"): the logo-pack zip (Experts Live DK + ELDK27 event logos) is
    /// SERVER-PROXIED — the in-hub route streams it from SharePoint with the app
    /// registration's credentials (LogoPackService; no share link, no sharing-scope
    /// surprises). Event Info leaf for SPEAKERS + SPONSORS; the browser downloads the
    /// file in place (Content-Disposition attachment), so it is NOT an external item.</summary>
    private const string LogoPackUrl = "/logo-pack/download";

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
    /// <param name="attendeeIsTwoDay">
    /// True when the signed-in ATTENDEE holds the 2-day ticket (<see cref="Attendee.TicketStatus"/>
    /// == TwoDay) that includes a Master Class seat (§234 UX). The three Master-Class menu
    /// entries (chooser / waitlist / Q&amp;A) are shown ONLY then — a 1-day holder's ticket
    /// excludes Master Classes, so those items would be dead ends for them. The pages stay
    /// reachable by direct URL (each shows a friendly not-eligible state). The caller (the
    /// layout) computes this for attendees only; it is ignored for every other role.
    /// </param>
    /// <param name="speakerIsSponsorCategory">
    /// §457 (operator 2026-07-27: *"remove these menu-items for the speakercategory sponsor"*) —
    /// true when this speaker's <c>SpeakerProfile.Category</c> is
    /// <see cref="Domain.SpeakerCategory.Sponsor"/>. A sponsor-brought speaker does not run the
    /// ELDK speaker programme: no session guidelines, no preview/final guidance, no attendee
    /// survey results, no Help Promote and no speaker template. What they DO keep is everything
    /// about actually delivering on the day — their sessions, key dates, telemetry, the room A/V
    /// page, evaluation QR codes and their ratings. Pairs with §458 (no Help-Promote TASK) and
    /// §461 (the same actions hidden on the My Sessions cards) so the three surfaces agree.
    /// </param>
    public static NavModel Build(ParticipantRole role, bool isVolunteerSupervisor = false, bool isExhibitor = false, bool speakerHasMasterClass = false, bool attendeeIsTwoDay = false, bool attendeeGamesEnabled = false, bool speakerIsSponsorCategory = false)
    {
        var groups = new List<NavGroup> { BuildParticipantGroup(role, isVolunteerSupervisor, isExhibitor, speakerHasMasterClass, attendeeIsTwoDay, attendeeGamesEnabled, speakerIsSponsorCategory) };

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
    private static NavGroup BuildParticipantGroup(ParticipantRole role, bool isVolunteerSupervisor, bool isExhibitor, bool speakerHasMasterClass, bool attendeeIsTwoDay, bool attendeeGamesEnabled = false, bool speakerIsSponsorCategory = false)
    {
        var items = new List<NavItem>
        {
            new("/", "Nav.Home", ExactMatch: true),
            // §164: the Party RSVP is visible to EVERY role + every ticket type so anyone can sign up
            // and edit their answer. §173c (operator 2026-06-28): it is NO LONGER a top-level item —
            // it is a LEAF inside the shared "Event logistics" fold-out (added with the content-
            // logistics cluster below, for every role that has that fold-out, i.e. all of them).
        };

        // §43: generic "Get started" guided wizard for the roles WITHOUT a bespoke
        // wizard — Organizer, Media, EventPartner (Volunteer gets it too, but placed
        // inside its own §47-ordered block below; Speaker §28 + Sponsor §32 keep
        // their own dedicated entries). Added right after Home so it's the obvious
        // first action.
        if (role is ParticipantRole.Organizer
            or ParticipantRole.Media or ParticipantRole.EventPartner)
        {
            items.Add(new("/Forms/Wizard", "Nav.GetStarted"));
        }

        // Sponsors use the company-shared /Sponsor/Tasks entry, not the generic
        // assigned-to-me /Tasks list. Attendees get a deliberately MINIMAL menu
        // (Home + Master Class + Waitlist only — operator 2026-06-21), so they are
        // excluded from My tasks / My profile / Resources / Sessions here.
        // Speakers excluded too (operator 2026-06-23 menu: Home, My Hub Profile,
        // Bio, My sessions, Event Logistics, Contact — no top-level My tasks).
        // ⚰️ §950 — TASKS v1 IS RETIRED for the generic non-organizer roles. Operator 2026-08-07:
        // *"if tasks v1 module still exist for volunteers, media, event partners, then you can retire
        // that, as we now replaced it with get started"*.
        //
        // 🔴 Volunteers went first (§939) because the legacy list scored a FINISHED volunteer at 90%
        // from a task model they no longer use — two answers to "am I done?", and the false one sends
        // somebody looking for work that does not exist. Media and Event Partner ran the same risk;
        // they simply had not been looked at yet. Get Started is now the single answer for all three.
        //
        // ⚠️ ORGANIZERS KEEP IT — he named volunteers, media and event partners, not organizers.
        // Organizers are staff and use the list as an admin surface; removing it is a separate call.
        // ⚠️ The PAGE and the task DEFINITIONS stay: `ParticipantTaskDefinitions.GenericRoles` still
        // seeds these rows for all four generic roles, and `FormTaskReconciler` opens/closes them from
        // the same saved form data Get Started reads (§173e). Retiring the MENU is reversible;
        // deleting the model is not, and reminders/digests/organizer views also read those rows.
        if (role == ParticipantRole.Organizer)
        {
            // §301c (operator 2026-07-24, "i want it to be consistent"): every "(Register)" form
            // lives under the "Register" fold-out for EVERY role (the §288/§288b rule that moved
            // Party there) — so "My Tasks" is a PLAIN link to the tasks page again, not a fold-out.
            items.Add(new("/Tasks", "Nav.MyTasks"));
        }

        if (role != ParticipantRole.Attendee)
        {
            // §283 (operator 2026-07-10): "My Hub Profile" is NO LONGER a main-menu item — it moved
            // into the dropdown under the signed-in person's NAME in the header (see _Layout), to
            // free up room on the main menu. /Profile stays reachable from that dropdown.
            // §290 (operator 2026-07-10): "Attendee telemetry" in the MAIN menu for crew — the same
            // limited "who's coming" view sponsors have (sponsors add it in their own branch below);
            // organizers link to their fuller /Organizer/Telemetry.
            // §294 (operator 2026-07-11): for SPEAKERS, attendee telemetry moves OUT of the top
            // level and INTO the "Speaker Info" fold-out (added with the other Speaker Info items
            // below, so the section stays contiguous). Other crew keep it at the top level.
            // §939 (operator 2026-08-07): for VOLUNTEERS it moves into the "Event Info" fold-out —
            // the same shape §294 gave speakers. It is context about the event, not a thing a
            // volunteer acts on, so it belongs with the other reference material rather than
            // competing with their own tasks at the top level.
            // 🔑 §944 — the volunteer's copy is added LATER, inside the volunteer block, and that
            // position is load-bearing: a section takes its place in the bar from where its FIRST
            // item appears (NavModel.Sections), so adding telemetry here would pin "Event Info"
            // ahead of "Register/Update". See the §944 note in the volunteer block below.
            // ⚠️ Media and EventPartner deliberately KEEP it at the top level: he was reviewing the
            // volunteer menu, and moving theirs was not asked for.
            if (role is ParticipantRole.Media or ParticipantRole.EventPartner)
                items.Add(new("/Sponsor/Telemetry", "Nav.AttendeeTelemetry"));
            else if (role == ParticipantRole.Organizer)
                items.Add(new("/Organizer/Telemetry", "Nav.AttendeeTelemetry"));
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
            // §267/§301c: the register/claim self-service forms group under the "Register" menu
            // (same fold-out Party lives in — consistent for every role).
            items.Add(new("/Forms/Hotel", "Nav.Hotel", SectionKey: "Nav.SectionRegister"));
            items.Add(new("/Forms/Dinner", "Nav.Dinner", SectionKey: "Nav.SectionRegister"));
        }

        // §1078 (operator 2026-08-11: "i would like to have menu addition for media participants in
        // the hub. Add 'Media Management' in main menu with 2 menu-items") — the two libraries the
        // press/photo crew manages IN THE HUB.
        // 🔑 Media AND Organizer. He named the media team; organizers are staff and manage the same
        // material, and every other document surface in the hub is reachable by them.
        // 🔒 The items point at HUB pages, not at the two SharePoint URLs he pasted: a SharePoint
        // link opens in the visitor's OWN session, which is the one thing he ruled out ("it should
        // not run in their user context, but through the app context").
        if (role is ParticipantRole.Media or ParticipantRole.Organizer)
        {
            items.Add(new("/Media/Pictures", "Nav.MediaPictures", SectionKey: "Nav.SectionMedia"));
            items.Add(new("/Media/Videos", "Nav.MediaVideos", SectionKey: "Nav.SectionMedia"));
        }

        // Lunch + Swag: organizer + media crew + event partners — all entitled per
        // OrderEntitlements (mirrors the Hotel/Dinner crew block above). Speakers +
        // volunteers get these in their Event logistics fold-out below.
        if (role is ParticipantRole.Organizer
            or ParticipantRole.Media
            or ParticipantRole.EventPartner)
        {
            items.Add(new("/Forms/Lunch", "Nav.Lunch", SectionKey: "Nav.SectionRegister")); // §301c: registers lunch
            items.Add(new("/Forms/Swag", "Nav.Swag", SectionKey: "Nav.SectionRegister")); // §267/§301c
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
            // §285: "Get Started" now opens the INLINE wizard (/Forms/Wizard) — the forms are
            // replicated inline step-by-step (Prev/Next/Finish), not dead links out to pages.
            items.Add(new("/Forms/Wizard", "Nav.SpeakerOnboarding"));
            // 🔒 §707.56 — "Speaker Details" REMOVED from the nav. Operator 2026-07-30: *"in my
            // opinion, it has now been replaced by get started menu"*, and the audit agrees with a
            // clarity the sponsor case did not have: `/Speaker/Details` and
            // `/Forms/Wizard?step=details` are not two implementations, they are ONE with two hosts.
            // Both bind `SpeakerDetailsFormModel`, both call `SpeakerDetailsFormService`, and both
            // render the same `_DetailsFields.cshtml` partial — whose own header says the host owns
            // only the chrome. The page has exactly ONE save path, and the wizard step uses it.
            // ⇒ ZERO capability gap, unlike `/Sponsor/CompanyDetails` (§707.44), which still owns
            // three things nothing else does.
            // items.Add(new("/Speaker/Details", "Nav.SpeakerDetails"));
            items.Add(new("/Speaker/Tasks", "Nav.MyTasks"));   // §301c: plain link — register forms moved to "Register"
            // §247 (operator 2026-07-07: "DROP THIS speaker readiness!"): the §234
            // "Am I ready?" menu entry is REMOVED again — the readiness rollup at the
            // top of Speaker My Tasks covers the need. /Speaker/Readiness stays
            // routable (organizer roster + testing use the same calculator), it just
            // has no speaker menu entry.
            // §317 (operator 2026-07-24): the "Speaker Info" menu is RESTRUCTURED to the
            // operator's exact order — two direct leaves (My Sessions, Master Class Q&A),
            // then three NESTED sub-fold-outs (rendered auto-OPEN by the layout):
            //   Preparing My Session for ELDK27 → Session Guidelines, Deadlines (preview/
            //     final), Attendee Telemetry, Attendee Survey Results, Help Promote,
            //     Speaker template (unlisted by the operator; kept — it is prep material)
            //   Session Room Info → A/V, Comfort Screen, HDMI Switches, Stage-timer
            //   Session Evaluation → My Session Ratings, How We Do Session Evaluations?
            // The speaker-only CONTENT pages are added explicitly here (the generic
            // content-page loop below skips them for speakers) so the order is exact.
            const string SpeakerInfo = "Nav.SectionSpeakerInfo";
            const string Prep = "Nav.SubPrepareSession";
            const string Room = "Nav.SubSessionRoom";
            const string Eval = "Nav.SubSessionEvaluation";
            void AddSpeakerContent(string slug, string subSection)
            {
                var page = ContentPageRegistry.Get(slug);
                if (page is null) return;   // registry drift — never emit a dead link
                items.Add(new($"/Info/{page.Slug}", LabelKey: null,
                    FallbackLabel: page.Title, SectionKey: SpeakerInfo, SubSectionKey: subSection));
            }

            items.Add(new("/Speaker", "Nav.MySessions", SectionKey: SpeakerInfo)); // §267 Speaker info
            // §86/§138: Master Class Q&A only when the speaker presents a master class —
            // otherwise it lands on an empty page (still reachable by direct URL).
            if (speakerHasMasterClass)
                items.Add(new("/Speaker/Questions", "Nav.MasterClassQaSpeaker", SectionKey: SpeakerInfo));
            // §326e (operator 2026-07-25): "Key Dates & Times" — a DIRECT Speaker Info
            // leaf (during-event + before-event tables + the agenda overview).
            {
                var keyDates = ContentPageRegistry.Get("key-dates-times");
                if (keyDates is not null)
                    items.Add(new($"/Info/{keyDates.Slug}", LabelKey: null,
                        FallbackLabel: keyDates.Title, SectionKey: SpeakerInfo));
            }

            // §457: the ELDK speaker-programme items — hidden for a SPONSOR-category speaker.
            // Telemetry stays (not arrowed by the operator): knowing who bought a ticket is useful
            // to a sponsor speaker too, and it is the same page their company already sees.
            if (!speakerIsSponsorCategory)
            {
                AddSpeakerContent("session-guidelines", Prep);
                AddSpeakerContent("session-preview-final", Prep);
            }
            items.Add(new("/Sponsor/Telemetry", "Nav.AttendeeTelemetrySpeaker", SectionKey: SpeakerInfo, SubSectionKey: Prep));
            if (!speakerIsSponsorCategory)
            {
                // §183/§290: the attendee topic/level survey results — speaker placement (other
                // roles get it under Event Info, added after the content loop below).
                items.Add(new(AttendeeSurveyResultsUrl, "Nav.AttendeeSurveyResults", SectionKey: SpeakerInfo, External: true, SubSectionKey: Prep));
                items.Add(new("/Speaker/Graphics", "Nav.HelpPromote", SectionKey: SpeakerInfo, SubSectionKey: Prep)); // §267
                // §838 — what CEH will post about their sessions, and when. Sits beside "Help
                // promote" because it answers the same question from the other side: promote WHAT,
                // and WHEN does the official post go out.
                items.Add(new("/Speaker/Announcements", "Nav.SpeakerAnnouncements", SectionKey: SpeakerInfo, SubSectionKey: Prep));
                AddSpeakerContent("speaker-template", Prep);
            }
            // 🙈 §748.5 (operator 2026-07-31: "hide the page until C10 exists") — the §320 entry
            // (2026-07-24: direct entry to the per-ROOM evaluation-QR downloads) is REMOVED, not
            // forgotten. §748 replaced room QRs with one QR per SESSION on his instruction, so this
            // link pointed at the superseded mechanism. ⚠️ When C10 restores the page, point the
            // link at the per-SESSION QR — do not re-add the room one.
            // §322k: the public slides catalogue — a speaker checks OTHER sessions' decks
            // while preparing their own. (Also under Event Info for every role.)
            // §326n: the slides catalogue is STANDALONE (no hub chrome, §322j) — open in
            // a new tab so the speaker can view and come back.
            items.Add(new("/Sessions/Slides", "Nav.SlidesForSpeakers", SectionKey: SpeakerInfo, External: true, SubSectionKey: Prep));

            AddSpeakerContent("av-stage-timer", Room);

            // §752 C10 — the four-point results page, replacing the retired 1–5 one. This is what
            // the report-ready mail links to, so a speaker who gets the mail can also find it in
            // the menu afterwards.
            items.Add(new("/Speaker/Results", "Nav.SpeakerEvaluations", SectionKey: SpeakerInfo, SubSectionKey: Eval));
            // 🙈 §748.5 — the §234/§267 entry to /Speaker/Evaluations is REMOVED while the 1–5 scale
            // is retired and C10 (the speaker view of the four-point model) does not exist yet.
            // Left in the menu, it offered a speaker an empty ratings list, and an empty list reads
            // as "nobody rated me" rather than as "not built yet". The page still EXISTS and
            // redirects to /Speaker; C10 re-adds this line.
            AddSpeakerContent("session-feedback", Eval);

            // §267/§301c: EVERY register/claim form — Lunch included (operator 2026-07-24: "it
            // is used to trigger the lunch registration") — groups under the "Register"
            // fold-out, the same menu Party lives in. Consistent for every role.
            const string Register = "Nav.SectionRegister";
            items.Add(new("/Forms/Hotel", "Nav.Hotel", SectionKey: Register));
            items.Add(new("/Forms/Dinner", "Nav.Dinner", SectionKey: Register));
            items.Add(new("/Forms/Lunch", "Nav.Lunch", SectionKey: Register));
            items.Add(new("/Forms/Swag", "Nav.SpeakerGift", SectionKey: Register));
            items.Add(new("/Forms/Travel", "Nav.Travel", SectionKey: Register));
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
            items.Add(new("/Forms/Wizard", "Nav.GetStarted"));
            // ⚰️ §939 — "My Tasks" REMOVED for volunteers. Operator 2026-08-07: *"My tasks is
            // pointing towards the old tasks model and shows a 90% completion, which is wrong …
            // volunteers dont have any extra tasks outside of the get started, so once they complete
            // the get started, they are done (100%). remove My Tasks menu item, as it is legacy."*
            //
            // 🔴 It was not merely redundant, it was WRONG: the legacy /Tasks list computes its
            // percentage from a task model volunteers no longer use, so a volunteer who had finished
            // everything still saw 90%. Two answers to "am I done?", one of them false — and the
            // false one is the one that makes somebody go looking for work that does not exist.
            // ⇒ Get Started is the single source of truth for a volunteer's completion.
            // 🔒 The shared "Nav.MyTasks" key is untouched — organizer/media/speaker still use it.

            // §939 — My Availability now points at the WIZARD STEP, not the retired standalone page.
            // Operator: the old /volunteer/availability *"should be retired"*; the menu must land on
            // the same form the Get Started flow walks him through, so there is one availability
            // form rather than two that can disagree.
            items.Add(new("/Forms/Wizard?step=availability", "Nav.MyAvailability"));
            // §47: relabelled "My Assignments" (was "My schedule"); volunteer-only key.
            items.Add(new("/volunteer/myschedule", "Nav.MyAssignments"));
            // Supervisor dashboard: shown ONLY to volunteers who actually supervise a
            // bucket (operator 2026-06-21). A non-supervisor volunteer would otherwise
            // see a menu item that lands on a "you are not a supervisor" dead end.
            if (isVolunteerSupervisor)
                items.Add(new("/volunteer/supervisor", "Nav.VolunteerSupervisor"));

            // §267/§301c: EVERY register form — Lunch included (operator 2026-07-24) — groups
            // under the "Register" fold-out, the same menu Party lives in.
            const string Register = "Nav.SectionRegister";
            items.Add(new("/Forms/Hotel", "Nav.Hotel", SectionKey: Register));
            items.Add(new("/Forms/Dinner", "Nav.Dinner", SectionKey: Register));
            items.Add(new("/Forms/Lunch", "Nav.Lunch", SectionKey: Register));
            items.Add(new("/Forms/Swag", "Nav.VolunteerGift", SectionKey: Register));

            // §944 (operator 2026-08-07): *"register/update menu item must be moved 1x left so event
            // info is next to contact organizers"* ⇒ Register/Update comes BEFORE Event Info, leaving
            // the two informational fold-outs (Event Info, Contact Organizers) together at the end.
            // 🔑 A section's place in the bar is where its FIRST item appears (NavModel.Sections), so
            // the swap is done by adding the volunteer's Event Info anchor HERE — after Register —
            // rather than by reordering anything. §939 put attendee telemetry in this fold-out; it is
            // simply the earliest Event Info item a volunteer has, and therefore the one that decides
            // the position. The rest of the fold-out (the shared content pages) is added further down
            // and joins this section wherever it already sits.
            items.Add(new("/Sponsor/Telemetry", "Nav.AttendeeTelemetry",
                SectionKey: "Nav.SectionEventLogistics"));
            // (Calendar "Important dates" entry removed: the user-facing calendar UI is retired.)
        }

        // Attendee area — MINIMAL menu (operator 2026-06-21): Home (added at top) +
        // Master Class + Waitlist only. The in-hub Master Class chooser lives at
        // /Attendee (replaces the old Zoho-Bookings deep-link); the waitlist view at
        // /Attendee/Waitlist. My Event / My Plan / Sessions / Tasks / Profile /
        // Resources are intentionally not shown to attendees.
        if (role == ParticipantRole.Attendee)
        {
            // §207/§208: a main-menu "Get Started" stepper for attendees — Master Class +
            // Party for 2-day holders, Party for 1-day holders (ticket-driven, editable).
            items.Add(new("/Forms/Wizard", "Nav.GetStarted"));
            // §234 UX: the three Master-Class entries (chooser / waitlist / Q&A) are shown
            // ONLY to 2-day ticket holders — a 1-day attendee's ticket excludes Master
            // Classes, so all three would be dead ends for them. The pages stay reachable
            // by direct URL (each shows a friendly not-eligible state).
            if (attendeeIsTwoDay)
            {
                // §365 (operator 2026-07-26): "it is still showing the old menu-item to
                // /attendee". Master Class Selection now points at the INLINE wizard step
                // (§352) — the same consolidation the party got in §351-6. /Attendee stays
                // routable for old links, but nothing in the product navigates there.
                items.Add(new("/Forms/Wizard?step=masterclass", "Nav.MasterClass"));
                // My plan removed (operator 2026-06-23) — handled in Zoho Backstage, not
                // the hub.
                items.Add(new("/Attendee/Waitlist", "Nav.Waitlist"));
                // Master Class Q&A shortcut (operator 2026-06-24): redirects to the
                // attendee's confirmed Master Class page, which hosts the shared Q&A board.
                items.Add(new("/Attendee/MasterClassQa", "Nav.MasterClassQa"));
                // §326al (operator 2026-07-25): the same three Master-Class entries ALSO
                // appear under the shared "Register/Update" fold-out — deliberate
                // duplicates, so an attendee finds them where every other role finds
                // their register forms. The top-level entries above stay (§177).
                items.Add(new("/Forms/Wizard?step=masterclass", "Nav.MasterClass", SectionKey: "Nav.SectionRegister"));
                items.Add(new("/Attendee/Waitlist", "Nav.Waitlist", SectionKey: "Nav.SectionRegister"));
                items.Add(new("/Attendee/MasterClassQa", "Nav.MasterClassQa", SectionKey: "Nav.SectionRegister"));
            }
            // §171/§270: the attendee "fun IT games" — three timed learning quizzes. Now an
            // ON/OFF feature (operator 2026-07-10), default OFF: shown only when the edition
            // opts in via event.<edition>.json -> attendeeGamesEnabled: true.
            if (attendeeGamesEnabled)
            {
                items.Add(new("/Games", "Nav.Games"));
            }
        }

        // Sponsor menu (operator 2026-06-21). The redundant Sponsor Portal + the
        // standalone Capture-lead tab are removed; engagement details become the
        // "Sponsor Webshop" fold-out; two new fold-outs surface the Zoho exhibitor
        // dashboard (Exhibitor & Booth Details, Leads). Resources + Sessions removed
        // above. NOTE: the eldk27.expertslive.dk Zoho URLs are this edition's
        // exhibitor dashboard; for the community mirror they should move to config.
        if (role == ParticipantRole.Sponsor)
        {
            // §667 (operator 2026-07-29) — he gave the canonical destinations verbatim:
            //   .../ELDK27-ExpertsLiveDenmark2027#/exhibitor-dashboard/lead-list
            //   .../ELDK27-ExpertsLiveDenmark2027#/exhibitor-dashboard/inquiry-list
            // The ROUTE NAMES here were already right; the EVENT-SLUG path segment before the
            // '#' was missing. Without it the Backstage SPA loads the portal root and chooses
            // its own event — which looks like the link worked, so nobody noticed. Compare the
            // whole string, fragment included.
            //
            // ⚠ Still hard-coded here while config/event.<edition>.json holds the same values
            // (leadsListUrl / inquiriesListUrl). NavBuilder.Build is static with no config
            // access, so this is a knowing duplicate — pinned by NavZohoUrlsMatchConfigTests
            // so the two copies cannot drift again.
            const string Zoho = "https://eldk27.expertslive.dk/ELDK27-ExpertsLiveDenmark2027#/exhibitor-dashboard/";

            // §32: guided "Get started" wizard — the single entry point that walks a
            // sponsor through the Company Details sections with progress. First item.
            // §285: sponsors now open the shared INLINE wizard (/Forms/Wizard drives their plan
            // from SponsorWizardService) instead of the card-stepper that deep-linked out.
            items.Add(new("/Forms/Wizard", "Nav.SponsorGetStarted"));

            // Sponsor Webshop (renamed from "Engagement details"): the external
            // webshop buy-flow + the internal hub sections for orders / linked contacts.
            const string Webshop = "Nav.SectionSponsorWebshop";
            // §176: the EXTERNAL webshop item confirms before leaving — the buyer logs in to
            // the external webshop with their own email + password (Redirect=Webshop renders
            // the "lost your password" hint and a confirm() before navigating).
            items.Add(new("https://expertslive.dk/sponsor", "Nav.SponsorBuyServices", SectionKey: Webshop, External: true, Redirect: ExternalRedirectKind.Webshop));
            // Orders + Linked-contacts are sections of the SAME /Sponsor page, so
            // they're one menu entry (operator 2026-06-23). This one is IN-HUB — no
            // Redirect marker, or every click would warn about an external login that
            // never happens.
            items.Add(new("/Sponsor", "Nav.SponsorOrders", SectionKey: Webshop));

            // 🔒 §707.44 — "Company Details" is REMOVED FROM THE NAV. Operator 2026-07-30:
            // *"yesterday we decided to retire the company details view … but it is still shown in
            // the Main menu, why"* / *"we have migrated everything to either getstarted and tasks so
            // it was redundant/legacy"*.
            //
            // §689.1 decided the retirement — ONE editor per dataset — and §689.2's reference sweep
            // set the order of work. Step 1 (the embedded booth-members editor with delete + sync
            // errors) SHIPPED 2026-07-29; steps 2–4 did not, so the page kept its front door and went
            // on offering a second place to edit the same data. That is the duplication the decision
            // exists to remove, and the nav is where a sponsor meets it.
            //
            // 🔑 The PAGE deliberately still exists and still resolves. §689.2 found it is the
            // DEEP-LINK TARGET of the Get Started wizard plus three other surfaces, and its URL is in
            // welcome mails, task bodies and his own bookmarks. Removing the entrance is reversible
            // and breaks nothing; deleting the page before those links are re-pointed (§689.1 steps
            // 2–4) would 404 every one of them.
            // items.Add(new("/Sponsor/CompanyDetails", "Nav.SponsorCompanyDetails"));

            // §135 (operator 2026-06-27): the standalone "Deliverables" nav entry is removed —
            // the deliverables rollup (X of N done, % + the still-missing/overdue items with
            // deep links) now lives at the TOP of Sponsor My Tasks (mirrors the speaker §138
            // readiness move). The /Sponsor/Deliverables page + the /Organizer/SponsorDeliverables
            // board + SponsorDeliverablesService stay intact; only this sponsor-facing menu item
            // is dropped.

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

                // §483 (operator 2026-07-27) — the fold-out is ordered HUB-FIRST: the pages an
                // exhibitor actually works in lead, and everything that bounces them out to Zoho is
                // gathered into ONE nested "Zoho Event System" sub-fold-out. Previously the four
                // Zoho links OPENED the menu, so the first thing an exhibitor saw was a list of
                // places that leave the hub.
                //
                // Leaf order here is insertion order, and the layout renders all direct leaves
                // BEFORE any sub-fold-out — so this reads: Key Dates & Times, Your Booth, Party
                // Preday (added later in this method, still a Booth leaf), then the Zoho group.

                // §483: "Key Dates & Times" moves UP from the Event-logistics fold-out to lead this
                // one. Non-exhibitor sponsors keep it under Event logistics (added further below),
                // so nobody loses the page — it is RELOCATED for exhibitors, not duplicated.
                items.Add(new("/Sponsor/Logistics", "Nav.SponsorBoothRunOfShow", SectionKey: Booth));
                // "Your Booth" — booth number + expo map (moved here from Event logistics).
                items.Add(new("/Sponsor/Booth", "Nav.OurBooth", SectionKey: Booth));

                // §483: the nested Zoho group. §317 renders sub-fold-outs OPEN by default, which is
                // exactly the operator's "folds out automatically" — the caret still collapses it.
                const string ZohoSystem = "Nav.SectionZohoEventSystem";
                // §175: every Zoho exhibitor-dashboard link confirms before leaving — the
                // exhibitor logs in to Zoho with their email + a one-time password
                // (Redirect=Zoho renders that message and a confirm() before navigating).
                //
                // §483: Leads FIRST, Inquiries SECOND (his explicit order) — those are used DURING
                // the event; profile/materials/banner are pre-event setup, so they follow.
                // §163: both lead links stay gated behind "sponsor-leads" (DEFAULT OFF).
                items.Add(new($"{Zoho}lead-list", "Nav.LeadsZoho", SectionKey: Booth, External: true, FeatureKey: "sponsor-leads", Redirect: ExternalRedirectKind.Zoho, SubSectionKey: ZohoSystem));
                items.Add(new($"{Zoho}inquiry-list", "Nav.InquiriesZoho", SectionKey: Booth, External: true, FeatureKey: "sponsor-leads", Redirect: ExternalRedirectKind.Zoho, SubSectionKey: ZohoSystem));

                // §488 (operator 2026-07-27): the four Zoho SETUP links — Exhibitor Profile, Booth
                // Members, Exhibitor Materials, Promotional Banner — are REMOVED. Everything they
                // reached is now owned by the hub (company details, booth members, booth materials,
                // logos & artwork), so sending an exhibitor to Zoho to edit the same data invited
                // two divergent copies. Leads + Inquiries stay: those genuinely have no API yet,
                // which is what the §484 interstitial explains.

                // §483: "Capture Leads (Failover)" REMOVED from the menu at his request. The
                // /Sponsor/CaptureLead PAGE is deliberately left routable — only the nav entry
                // goes — so existing links keep working and the failover can be re-exposed with one
                // line if the Zoho leads API is ever down during the event.
            }

            // §297 (operator 2026-07-11): Attendee telemetry moved to AFTER "Exhibitor & Booth
            // Details". §55: the AUTHENTICATED in-area page (ranked tables/filters without leaving
            // the hub), NOT the external public link.
            //
            // §489 (operator 2026-07-27): "remove the attendee telemetry from the sponsor main menu
            // - and add this under Exhibitor & Booth Info". For an EXHIBITOR it becomes a leaf of
            // that fold-out (added below, so it follows Party Preday and keeps the §483 order
            // intact). A digital-only sponsor has NO booth fold-out, so for them it stays a
            // top-level item — otherwise removing it from the main menu would hide the page from
            // them entirely.
            if (!isExhibitor)
            {
                items.Add(new("/Sponsor/Telemetry", "Nav.AttendeeTelemetry"));
            }

            items.Add(new("/Sponsor/Tasks", "Nav.SponsorTasks"));
            // §837 — the posts CEH will publish about this sponsor: their company, their tier and
            // their sessions, each with the preview and the date it runs on LinkedIn.
            //
            // 🔴 §1027 (operator 2026-08-10: *"wrong placement of SoMe announcement - should go into
            // exhibibitor & booth details"*) — for an EXHIBITOR this moves INTO that fold-out
            // (added with the other booth leaves below). It is exactly the §489 shape, which did
            // the same for Attendee Telemetry, INCLUDING the reason it is conditional: a
            // digital-only sponsor has NO booth fold-out, so for them it must stay top-level or
            // the page disappears from the menu entirely.
            if (!isExhibitor)
            {
                items.Add(new("/Sponsor/Announcements", "Nav.SponsorAnnouncements"));
            }

            // §135 (operator 2026-06-27): the booth run-of-show (key dates & times) is now a
            // LEAF inside the SHARED "Event logistics" fold-out (Nav.SectionEventLogistics) —
            // it is NO LONGER a second standalone "Event logistics" entry that read identically
            // to the content-hub Event-Logistics fold-out below. Added here (before the §104–§123
            // content pages join the same section) so it LEADS the fold-out; the order then reads
            // Booth run-of-show, Wayfinding, Good to know, Addresses, Check out last event.
            //
            // §483: for an EXHIBITOR this entry has moved to the top of "Exhibitor & Booth
            // Details" (see above), so it is skipped here — otherwise the same page would appear
            // twice in the menu. A non-exhibitor sponsor has no booth fold-out at all, so for them
            // this stays exactly where it was.
            if (!isExhibitor)
            {
                items.Add(new("/Sponsor/Logistics", "Nav.SponsorBoothRunOfShow", SectionKey: "Nav.SectionEventLogistics"));
            }
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
        // §206 (operator 2026-06-30): "Party Signup" lives under the shared "Event logistics"
        // fold-out for EVERY role (standardized label Nav.PartySignup = "Party Signup").
        // ATTENDEES additionally keep their prominent §177 MAIN-nav "Party Signup" entry (their
        // one tracked action, with its own task + 2-week reminder cadence). Insertion order here
        // is not the render order — the view alphabetizes the fold-out by resolved label (§173d).
        // §267: Party sign-up groups under the "Register" menu (with the other register forms).
        // §288 (all roles): Party (register) lives under the "Register" menu, NOT the "My tasks"
        // fold-out (operator 2026-07-10 — it duplicated Sponsor Tasks / the task list).
        // §297 (operator 2026-07-11): Party (register). ATTENDEES get ONE prominent top-level entry
        // (§177) — NOT a duplicate under Register too. An EXHIBITOR sponsor gets it under "Exhibitor
        // & Booth Details" (Sections() merges by key regardless of position); every other role gets
        // it under "Register".
        if (role == ParticipantRole.Attendee)
        {
            // §326am (operator 2026-07-25: "why does the party sign-up switch to a separate
            // page — is it not possible to make it more embedded like the get started
            // experience"): it CAN be, and now is. The wizard already owns an inline party
            // step (PartyStepHandler), and §326ak gives attendees that wizard — so the
            // attendee's Party entry deep-links INTO the wizard step (hub chrome, breadcrumb,
            // Prev/Next) instead of opening the standalone /Party page in a new tab.
            // /Party stays alive for e-mail/reminder links and the other roles.
            // §353 (operator 2026-07-26): "rename the 'Party register' to 'Party Preday' on the
            // main menu for attendees. Add also the Party under the Register/Update menu."
            // This REVERSES the §297 decision that attendees get exactly ONE entry and no
            // Register duplicate — the operator now wants both, so the duplicate is INTENTIONAL.
            // Its own label key (Nav.PartyPreday) keeps the rename off the other roles' entry.
            items.Add(new("/Forms/Wizard?step=party", "Nav.PartyPreday"));
            items.Add(new("/Forms/Wizard?step=party", "Nav.PartySignup",
                SectionKey: "Nav.SectionRegister"));
        }
        else
        {
            // §351-6 (operator 2026-07-26): "i see 2 party pages (very confusing) … i prefer to
            // have /Forms/Wizard?step=party only." CONSOLIDATED — every role now goes to the
            // wizard step, exactly like the attendee entry above. It keeps hub chrome and a way
            // back, so the §326n "open the chrome-less page in a new tab" workaround is no longer
            // needed and External is dropped. /Party itself stays routable for old e-mail links
            // and bookmarks; nothing in the product points at it any more.
            items.Add(new("/Forms/Wizard?step=party", "Nav.PartySignup",
                SectionKey: (role == ParticipantRole.Sponsor && isExhibitor) ? "Nav.SectionExhibitorBooth" : "Nav.SectionRegister"));

            // §489: an EXHIBITOR's Attendee Telemetry joins the booth fold-out. Added HERE, after
            // the party leaf, so the §483 order the operator set (Key Dates → Your Booth → Party
            // Preday) is preserved and telemetry simply follows it, ahead of the Zoho group.
            if (role == ParticipantRole.Sponsor && isExhibitor)
            {
                items.Add(new("/Sponsor/Telemetry", "Nav.AttendeeTelemetry",
                    SectionKey: "Nav.SectionExhibitorBooth"));
                // §1027 — and the social announcements, immediately after telemetry. Both are
                // "what the event is doing FOR you", which is why they belong together and why
                // neither reads as a top-level action the sponsor has to take.
                items.Add(new("/Sponsor/Announcements", "Nav.SponsorAnnouncements",
                    SectionKey: "Nav.SectionExhibitorBooth"));
            }
        }
        // §317: Event Info renders in INSERTION order (the §173d alphabetize is retired) —
        // the operator's exact order: Check Out Last Event → Good To Know → Address →
        // Wayfinding (registry declaration order), then the Sessions catalogue + (non-
        // speaker) survey results, then the nested Policies sub-fold-out.
        foreach (var page in ContentPageRegistry.ForRole(role))
        {
            // §317: a SPEAKER's speaker-only content pages are added EXPLICITLY inside the
            // speaker block above (Speaker Info sub-fold-outs) — skip them here so they
            // don't duplicate. All-roles pages (empty Roles) stay under Event Info; for
            // every other role (incl. organizers) the speaker pages stay under Event Info.
            if (role == ParticipantRole.Speaker
                && page.Roles.Count > 0
                && page.Roles.Contains(ParticipantRole.Speaker))
            {
                continue;
            }
            // §326br: role-aware label (speakers get the "& Speaker Hotel" variant).
            items.Add(new($"/Info/{page.Slug}", LabelKey: null,
                FallbackLabel: page.TitleFor(role), SectionKey: ContentLogisticsSection));
        }
        // §153 (operator 2026-06-28): the public Sessions catalogue is a LEAF inside the shared
        // "Event Info" fold-out for EVERY role (§317: after the four content leaves).
        // §326n: the public Sessions catalogue renders OUTSIDE the hub chrome — new tab.
        items.Add(new("/Sessions", "Nav.Sessions", SectionKey: ContentLogisticsSection, External: true));
        // §322k: the anonymous slides catalogue for EVERY role (speakers additionally get it
        // under Speaker Info → Preparing My Session — a deliberate duplicate).
        // §326n: standalone slides catalogue (no hub chrome, §322j) — new tab.
        items.Add(new("/Sessions/Slides", "Nav.SlidesPublic", SectionKey: ContentLogisticsSection, External: true));
        // §326f (operator 2026-07-25): the logo-pack zip download — SPEAKERS + SPONSORS
        // only, under Event Info for both. §326f-c: an IN-HUB proxied download (the
        // browser saves the file in place — no navigation, so no new tab needed).
        if (role is ParticipantRole.Speaker or ParticipantRole.Sponsor)
        {
            items.Add(new(LogoPackUrl, "Nav.DownloadLogos",
                SectionKey: ContentLogisticsSection));
        }
        // §183 (operator 2026-06-29): the attendee topic/level survey RESULTS — one external leaf
        // in Event Info for every NON-SPEAKER role (speakers get it under Speaker Info →
        // Preparing My Session, added in the speaker block above).
        // §351 (operator 2026-07-26): "change for all roles under Event Info - remove the
        // menu-item 'Attendee Survey Results: Topics and Level'". REMOVED from Event Info for
        // every non-speaker role. The SPEAKER entry under Speaker Info → Preparing My Session
        // (added in the speaker block above) is deliberately KEPT — the operator scoped this to
        // Event Info, and for a speaker the survey results are session-prep material.

        // Policies — a foldout menu (Privacy Policy + Code of Conduct) for EVERY role,
        // just before Contact Organizers (operator 2026-06-25). External links.
        // §290 (operator 2026-07-10): Policies is its OWN "Policies" submenu again (Code of Conduct +
        // Privacy Policy under it), not folded into Event Info.
        // §297 (operator 2026-07-11, all roles): Policies is a NESTED sub-fold-out INSIDE "Event
        // Info" — Event Info → Policies → (Code of Conduct + Privacy Policy). Two-level menu via
        // SubSectionKey (NOT its own top-level fold-out, and NOT the two links flattened into Event
        // Info). The layout renders SubSectionKey items as a nested <details>.
        // §317: Code of Conduct BEFORE Privacy Policy (the operator's order).
        const string Policies = "Nav.SectionPolicies";
        items.Add(new("https://expertslive.dk/code-of-conduct/", "Nav.CodeOfConduct", SectionKey: ContentLogisticsSection, External: true, SubSectionKey: Policies));
        items.Add(new("https://expertslive.dk/privacy-policy/", "Nav.PrivacyPolicy", SectionKey: ContentLogisticsSection, External: true, SubSectionKey: Policies));

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
    ///   • Comms        → EmailCenter, EmailLog, SendWelcomeLogin, SpeakerReminders  (§705.12: Broadcast + SendInvitations deleted)
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
            // 🔴 §1086 — A DIRECT ENTRY, because a hub tile was not findable. The operator, the day
            // after it shipped: *"i cannot find the status dashboard with role filter option where
            // i can see completion fx of all get started + tasks for all sponsors, where is it"*.
            // It was two clicks in, on the People hub. This is the §646 precedent exactly (Platform
            // Health + Jobs promoted to the menu because they are what he opens when asking "is
            // anything broken?") — "who still owes me something?" is asked just as often.
            new("/Organizer/ParticipantStatus", "Nav.OrgParticipantStatus"),
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
            // §652 (operator 2026-07-29: "remove the fun it games from organizer menu - we have
            // disabled this functionality for now"). The PAGE stays routable — same treatment as
            // §167's "Find a person" — so nothing is lost if it comes back; it is just not a menu
            // entry while the feature is off.
            // new("/Organizer/Quizzes", "Nav.OrgQuizzes"),

            // §646 (operator 2026-07-29: "organizer menus - add Platform Health + Jobs to main menu
            // for quick access"). Both were reachable only by URL or from another page — and they
            // are the two he opens when asking "is anything broken?", which §637 showed is the
            // question the hub answered worst. Kept adjacent: Platform Health is the OUTSIDE view
            // (calls, failures, timings); Jobs is the INSIDE one (what ran, and Run now).
            new("/Organizer/PlatformHealth", "Nav.OrgPlatformHealth"),
            new("/Organizer/Jobs", "Nav.OrgJobs"),

            // 🔴 §1120 — COUPON INVOICING GETS ITS OWN ENTRY (operator 2026-08-21: *"add coupon
            // invoicing to the main menu of the organizer"*).
            //
            // 🔑 Same argument as §1086 and §646 before it: the menu is hub-level by design, and the
            // exception is a page opened OFTEN and by URL. This one is opened every time a partner
            // buys, extends, asks for a cap, or queries an invoice — and it was two clicks in under
            // Setup, which is where things go that you configure once. Coupon invoicing is not
            // configured once; it is worked.
            //
            // ⚠️ The page stays where it is (§167/§652: a menu entry is added, nothing is moved), so
            // every existing link and every mail that says *"Organizer → Setup → Coupon invoicing"*
            // still lands correctly.
            new("/Organizer/CouponInvoicing", "Nav.OrgCouponInvoicing"),

            // §741 (operator 2026-07-31: "replace this with audit log instead on the main menu for
            // organizer"). The slot held "Switch User Log", which is ONE Category inside the audit
            // trail; the trail is the whole record (every user action + backend event), so the menu
            // now points at the superset.
            //
            // 🔑 The impersonation page STAYS ROUTABLE — the §652/§167 treatment: losing a menu
            // entry must not lose the page. /Organizer/Participants still links to it directly
            // ("Acting-as audit log"), which is where someone is actually standing when they want it.
            new("/Organizer/AuditTrail", "Nav.OrgAuditTrail"),
        };

        return new NavGroup(HeadingKey: "Nav.OrgArea", Items: items, IsManagement: true);
    }
}

