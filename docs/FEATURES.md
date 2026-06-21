# Community Event Hub — Feature Catalog

*Delivered feature set, as of 2026-06-21.*

Community Event Hub (CEH) is the all-in-one workspace that runs a tech-community
conference end to end: one home for your organizers, speakers, volunteers,
sponsors and attendees. Everyone signs in to a single hub tailored to their role,
fills in the forms that apply to them, sees exactly the tasks they owe, and gets
gentle, well-timed reminders so nothing slips. Organizers get live dashboards,
exports and a one-stop email center; sponsors get their own lead pipeline; speakers
get their deadlines; attendees get a clean status view.

This catalog describes what the product does for each audience today. Features are
grouped by area — Platform, Sign-in, Crew, Self-service Forms, Tasks & Reminders,
Sessions & Surveys, Sponsors, Sponsor Leads, Attendees, Email, Organizer Hub, and
Hosting & Reliability.

---

## 1. Platform — built for every edition

![The public front door — event details, programme and a no-login sign-in, the same generic platform behind every edition](img/public-landing.png)
*The public front door of an edition: event details, programme and a sign-in, with no login required. A new edition is a new configuration row, not a new build.*

- **One hub, every year, every community.** The same platform powers each edition
  and each community event. Launching a new edition is a matter of configuration —
  a new event and its settings — never a rebuild. The year lives only in your web
  address and the event's display name, so nothing has to be re-coded season to
  season.
- **Clean separation of product, code and your community.** The product is
  Community Event Hub; your community keeps its own name and branding throughout.
- **Everything about an edition is configuration.** Event details, sponsors,
  content, hotel, integrations and speaker deadlines are all settings you can edit,
  not code you have to change.
- **A sanitized public template.** A scrubbed template version of the platform is
  published openly, while your real configuration, logos and production settings
  stay private.
- **Everyone has a profile they own.** Every signed-in person — whatever their
  role — gets a "My profile" page to keep their own name and phone up to date.
  Their email (their sign-in) and role (set by the organizers) are shown but kept
  safe from accidental edits, and a person can only ever change their own details.
  *(✅ 2026-06-15)*
- **One shared Resources page for all crew.** A single, always-current place for
  the practical info everyone needs — venue and floor plan, the event site, the
  exhibitor/crew guide, and how to reach the organizers. Organizers maintain it
  entirely as edition settings (no developer needed); links and downloads are
  grouped into tidy sections, and the page shows a friendly "nothing here yet"
  message until content is added. *(✅ 2026-06-15)*
- **Go-live test-data cleanup — clear the rehearsal cast in one click.** ✅ 2026-06-18 —
  while you set the hub up you can seed a synthetic test cast (speakers, sponsors,
  volunteers, attendees) to rehearse every flow. Before the edition goes live, a
  single organizer page lists everyone marked as a test user and clears them out so
  they never skew your real counts, exports or emails. It is safe by design: a clean
  test person is removed outright, but a test person who picked up real-looking data
  during the rehearsal is kept and simply deactivated instead of deleted, so nothing
  can be left dangling — and your genuine registrations are never touched. You see
  exactly who will go (and what happens to each) before you confirm, and the action
  can be re-run any time.

## 2. Sign-in & embedding — frictionless, no new passwords

- **One-time PIN by email — no new account to remember.** Crew sign in with just
  their email: the hub sends a 6-digit PIN that expires in 15 minutes and works
  once. No passwords to create, reset or forget. Sensible safeguards are built in
  (rate limiting, lockout after repeated wrong tries, and neutral messaging that
  never reveals whether an email is registered).
- **"Stay signed in" your way.** At login you choose how long to stay signed in —
  a day, a week (the default), a month, or until you sign out — and your session
  refreshes itself as you keep using the hub.
- **Magic-link login.** Invitation emails can carry a tap-to-sign-in link, so crew
  land straight in their hub without typing a PIN.
- **Never a dead end — gentle sign-in recovery.** ✅ 2026-06-17 — if a tap-to-sign-in
  link has expired (or is otherwise no longer valid), the page doesn't leave anyone
  stuck: it explains what happened and offers a one-tap **"Request a new sign-in
  code"** that takes the person to the email + code sign-in — and, when the expired
  link is recognised, it pre-fills their email and remembers where the link was
  taking them, so they get a fresh code and land on the right page without retyping
  anything. A deactivated account is told to contact the organizers instead. Fully
  bilingual (English / Dansk) and mobile-friendly.
- **Pre-filled login links.** A link of the form `/Login?email=<address>` opens the
  sign-in page with the email already filled in, so a person only has to request their
  code — a small convenience that never bypasses the PIN. It works in every environment
  because each link uses that environment's own web address.
- **Ready for single sign-on.** The sign-in system is designed so a trusted
  identity provider can be added as a drop-in option in future, without disrupting
  the PIN experience.
- **Embeds safely in your event portal.** The hub can appear inside your existing
  conference platform (e.g. a Backstage portal) as a seamless embedded panel, with
  the security controls needed to keep that safe.
- **Anonymous visitors go straight to sign-in.** *(✅ 2026-06-21)* The separate public
  landing page was removed; someone who isn't signed in now lands directly on the
  **sign-in** page. The public, no-login programme pages (Sessions, Speakers, Sponsors,
  Master Classes, Agenda) remain reachable by their own addresses.
- **Sign-in sessions survive a deploy.** *(✅ 2026-06-21)* Sessions now persist across
  releases — a deploy no longer signs everyone out — because the hub uses a **shared
  data-protection key ring** rather than per-instance keys. People stay signed in
  through an update.

## 3. Crew profiles & roles — the right hub for each person

- **A complete crew profile.** Each person has one profile per edition: name,
  contact details, role, accreditation (MVP / Expert / RD / MS Employee), awards,
  clothing sizes, and status flags like verified and packed.
- **Test-data tagging for a clean go-live (2026-06-14).** Any profile can be marked
  as test/dummy data, so when the event goes live the team can remove or deactivate
  all test entries in one step without touching a single real registration.
- **A tailored hub per role.** Every role — Organizer, Speaker, Masterclass
  Speaker, Volunteer, Sponsor, Speaker-Sponsor, Video, Photography, VIP, Attendee —
  sees a hub built around what that person actually needs to do.
- **A friendly one-time welcome.** New crew get a welcome page the first time they
  arrive, once per edition.
- **Activate or deactivate people in a click.** Organizers can filter crew by role
  and status and switch someone active or inactive; deactivated people can no
  longer sign in.
- **Edit any participant from the grid.** Every row has an obvious **Edit / Modify**
  action that opens the full participant editor — name, email, persona/role, active
  state and sponsor-company link — validated and saved with a confirmation. The
  lighter "Modify on behalf" stays for quick hotel/swag tweaks. *(✅ 2026-06-15)*
- **Delete a participant safely, with a confirmation prompt.** Every row has a
  **Delete** that opens a confirmation modal first. People with linked data
  (sessions, volunteer tasks, claims, history) are **deactivated** instead of being
  permanently removed, so nothing important is ever silently lost; a never-engaged
  row is fully removed (its hotel/swag/sign-up/login leftovers cleaned up first).
  Bulk **Deactivate** is also available for ticked rows. Every removal is audited.
  *(✅ 2026-06-15)*
- **Link a sponsor contact to their company.** When editing someone, organizers can
  set or clear the sponsor company a contact belongs to, so that person sees exactly
  their company's sponsor tasks — and unlink them just as easily. *(✅ 2026-06-14)*

### Volunteer work structure — run a big volunteer pool without a bottleneck *(✅ 2026-06-15)*

For events with dozens of volunteers, organizers can break the work into a clear,
three-level structure and hand the day-to-day running of each area to a trusted
volunteer — so the organizing team isn't the single point of contact for everyone.

- **A three-level work tree.** Organizers build **Categories** (broad areas like
  Registration or A/V), each split into **Subcategories**, each holding concrete
  **Tasks**. Volunteers are assigned to tasks, and everything rolls up so a task
  always shows which subcategory and category it belongs to.
- **Two owners per category — oversight and hands-on.** Each category has a
  **lead**, who is an organizer providing oversight, and a **supervisor**, who is a
  volunteer appointed from the pool to actually run that category. Appointing a
  supervisor is a one-click organizer action that gives that volunteer management
  rights for **just that category** — they remain an ordinary volunteer everywhere
  else.
- **Supervisors run their own area.** A supervisor gets a dashboard for the
  categories they run: add subcategories and tasks, assign volunteers, move tasks
  along, and answer help requests — all limited to their own categories.
- **Volunteers see exactly their tasks.** A "My tasks" view groups a volunteer's
  assigned work by category and subcategory, lets them update their own progress,
  and shows any help they've asked for.
- **A built-in help channel.** A volunteer who is stuck on a task can **ask their
  category's supervisor for help** in one tap. The supervisor sees it (and the
  category's organizer lead can too, for oversight) and replies; each request moves
  from open to answered to resolved.
- **The supervisor is emailed when help is needed.** Raising a help request also
  **notifies the category's supervisor by email** (with the organizer lead copied in
  for oversight), so they don't have to be watching the dashboard to know a volunteer
  is waiting — the email shows the task, the category and the volunteer's message. The
  request is still saved and visible in the hub even if mail can't be sent.
- **Mobile-first.** Every screen — the organizer tree, the supervisor dashboard and
  the volunteer "My tasks" — works on a phone at the venue, and the hub home page
  surfaces each volunteer's assigned-task count and a link to their supervisor
  dashboard when they run a category.

### Volunteer "My schedule" — your whole day, in one place *(✅ 2026-06-15)*

Every volunteer gets a single mobile-first page that answers "what am I doing, and
when?" at a glance — built for someone standing at the venue with their phone.

| Volunteer "My schedule" | …on a phone |
|---|---|
| [![Volunteer My schedule with shifts and calendar subscribe](img/volunteer-schedule.png)](img/volunteer-schedule.png) | [![Volunteer My schedule on mobile](img/volunteer-schedule-mobile.png)](img/volunteer-schedule-mobile.png) |

*A volunteer's whole day in one place — shifts time-ordered, who to ask, and one-tap calendar subscribe — and the same view on a phone at the venue.*

- **All your shifts and tasks, time-ordered.** One page lists every task you're
  assigned to across all areas, sorted so dated work comes first (earliest first),
  then by shift window, with undated work last — no hunting through category groups.
- **Where, when and who to ask.** Each entry shows its bucket and subcategory, the
  due date and shift window, and the **go-to people**: the bucket's supervisor(s)
  plus the ELDK lead — so you always know who to turn to.
- **Ask for help in one tap.** Stuck on a task? Ask your supervisor for help right
  from the entry; they're notified by email and you see their reply inline.
- **Add it to your own calendar.** Subscribe your shifts to the calendar you
  already use, or download a single shift as a `.ics` — the same private, always-up-
  to-date personal feed used for deadlines, now carrying your volunteer work too.
- **Update your progress.** Move a task along (Open / In progress / Done) without
  leaving the page.
- **Fully bilingual + accessible.** English and Danish throughout, mobile-first at
  ~360px, with labelled controls for screen readers. *(No self event-check-in —
  that stays in Zoho Backstage.)*

### Volunteer self-service shifts — confirm, decline or swap *(✅ 2026-06-16)*

Volunteers stay in control of their own shifts on one mobile-first page, so the
coordinator is not chased down for every change.

- **Confirm you can take a shift.** For each shift you're assigned, tap "I can take
  this" to confirm your availability — the shift shows a green Confirmed badge.
- **Decline a shift you can't work.** Can't make it? Decline it with an optional
  short reason. Your assignment isn't silently dropped — a coordinator is signalled
  to reassign it, and the shift shows "Declined — needs reassigning".
- **Request a swap.** Want to hand a shift back or to someone else? Request a swap
  (with an optional note); the coordinator picks it up the same way.
- **Read the instructions for each shift.** The per-shift instructions are shown
  right where you decide, so you know exactly what the shift involves.
- **Change your mind any time.** An "undo" puts a declined or swap-requested shift
  back to normal and withdraws the coordinator nudge automatically.
- **Coordinators see it in one place.** Declines and swap requests surface on the
  existing organizer action queue — no new inbox to watch.
- **Yours only, and safe.** You can only act on shifts you're actually assigned to;
  the page never trusts who the browser claims to be. English and Danish, mobile-
  first at ~360px, with labelled controls and a status region for screen readers.
  *(No self event-check-in / live headcount — that stays in Zoho Backstage.)*

### Volunteer self-service, unified — one schedule, your availability, signup *(✅ 2026-06-21)*

The volunteer experience now gathers everything a volunteer manages into one place,
keeps the supervisor tools out of an ordinary volunteer's way, and opens a no-login
way to sign up.

- **One unified "My Schedule".** Assigned **shifts and tasks** now live on a single
  page, each with the actions a volunteer needs: **confirm**, **decline**,
  **request-swap**, **withdraw**, and **ask-for-help** — so a volunteer manages their
  whole commitment in one view instead of two.
- **My Availability — tell organizers when you can work.** A volunteer sets their
  availability **per event day**: **full**, **half** or **blocked** — so planners know
  who is free before they assign work.
- **An anonymous shift-signup survey at `/volunteer/signup`.** A no-login page lets a
  prospective volunteer sign up — capturing their **identity and shift availability** —
  which lands in the normal validation queue, just like other interest-form sign-ups.
- **The Supervisor dashboard shows only to actual supervisors.** The supervisor tools
  are now shown **only to volunteers who actually run a category**, so an ordinary
  volunteer's hub stays focused on their own work.
- **An Event-logistics fold-out and action guidance.** The volunteer hub carries a
  fold-out with the practical **event logistics** plus short action guidance, so a
  volunteer knows what to do and where to find it. Mobile-first, in English and Danish.

### Volunteer Buckets & resource allocation — plan staffing, then commit *(✅ 2026-06-15)*

On top of the work structure, organizers get a planning surface that turns a long
task list into a staffed plan, with a draft-it-then-commit workflow so nothing is
assigned by accident.

- **Buckets group the work, with two clear go-to tiers.** Tasks live in **Buckets**.
  Each Bucket has **one or more supervisors** (the go-to volunteers who run it) and an
  **ELDK lead** (the go-to person for the supervisors) — a simple chain of
  volunteer → supervisor → ELDK lead so everyone knows who to ask.
- **Richer task detail.** Every task can carry a time, status, criticality
  (need-to-have / nice-to-have), the responsible team, an ELDK lead, how many people
  it needs, pre-requisites, expectations and per-task instructions. The **ELDK lead
  can mark a task completed**, signed and time-stamped.
- **Import the plan in one action.** Organizers upload the detailed volunteer plan
  (CSV) and the hub creates the buckets and tasks for them, derives the bucket from
  each task's responsible team, fills in the task detail, and links named helpers to
  the volunteers already in the hub. Re-importing is safe — it updates rather than
  duplicates.
- **AI-assisted guidance.** For any task missing a pre-requisite or an expectation,
  the hub suggests one from the task name — using a real AI model when the event has
  configured one, or a sensible built-in rule of thumb otherwise. Every suggestion is
  fully editable and can be regenerated.
- **See the gaps at a glance.** Each task shows a **red/green** indicator of needed vs
  assigned people — green when it's covered, red (with the shortfall) when it's short
  — so organizers can spot under-staffed tasks across every bucket.
- **Plan as a draft, then commit.** Organizers map people to tasks into a **draft**
  and watch the red/green coverage update live as a *simulation* — but no one is
  actually assigned yet. Only **Commit** turns the draft into real assignments;
  **Discard** throws the draft away. Two organizers can plan at the same time without
  stepping on each other.
- **Volunteers see the full picture.** A volunteer's "My tasks" view shows each task's
  instructions, pre-requisites and expectations, plus their bucket's supervisor(s) and
  ELDK lead, so they know exactly what to do and who to ask.

### Onboarding lifecycle — from sign-up to set-up *(✅ 2026-06-15)*

A clear path from "someone is interested" to "they're ready to go", with the
organizing team in control of who comes on board.

- **A holding queue for new people.** Prospective volunteers, speakers and
  media-team don't go live automatically. They land in a **pre-selection queue** in
  a holding state — both the speaker sync results and the volunteer interest-form
  sign-ups arrive here for review. Each person moves through three stages:
  **inactive → preselected → active**, and only an **active** person can sign in.
- **Validate, then activate — one or many at once.** Organizers review the queue
  (filter by where each person came from), then preselect or activate people one row
  at a time, or tick several rows and activate them together with a single click.
- **Remove duplicates and spam from the queue.** Each queue row has a **Delete**
  button (behind a confirmation dialog) so an organizer can clear an obvious duplicate
  or spam entry. Queue rows are people who haven't gone live yet, so a clean row is
  removed outright; if a row somehow already has linked data it is safely deactivated
  instead of deleted, so nothing is ever orphaned. *(✅ 2026-06-16)*
- **A short onboarding wizard tailored to each persona.** Once activated, each
  person runs a quick, mobile-first wizard covering only the steps that apply to
  them — a speaker verifies a bio + picture and completes hotel, appreciation and
  swag; a volunteer or media-team member skips the public bio; a sponsor or
  organizer just does appreciation + swag. Each step is saved on its own (and the
  moment it was completed is recorded), with a progress strip showing what's done,
  and a persona is "fully onboarded" once it has finished all the steps it needs —
  not a fixed checklist for everyone.
- **An onboarding dashboard for organizers.** A dashboard shows counts and lists by
  **stage** — Pre-selected, Invited, In-progress, Completed — each with a completion
  percentage and **filterable by persona** (speakers / volunteers / media / sponsors
  / organizers), plus per-step progress (counting only people who need that step) and
  a per-person grid of who has and hasn't completed each step — so the team can see at
  a glance who still needs a nudge.
- **Re-open a step to send a reminder.** If something changes — say a speaker decides
  they want a hotel after all — an organizer can re-open just that step for that
  person, which queues a reminder asking them to complete it.

### Multi-hotel management — split the crew across several hotels *(✅ 2026-06-15)*

When the rooms don't all fit in one hotel, organizers can define several hotels and
place each person in the right one, then manage the room block per hotel.

- **Organizer-defined hotels.** A simple management page to add, edit and remove the
  hotels for the edition — each with a name, address and a contact email for the
  reception/booking desk.
- **Tidy several hotels at once.** *(✅ 2026-06-17)* As well as deleting one hotel at a
  time, organizers can tick several and remove them in one go. A clear confirmation
  box names exactly **how many hotels** are about to be deleted before anything
  happens, and anyone currently placed in a removed hotel is **automatically
  un-assigned** (their participant record is kept, just no longer pointing at the
  now-gone hotel) — so a mis-imported or duplicated room block is quick to clean up
  without losing anyone.
- **Assign each person to a hotel.** From the hotel-assignments page, an organizer
  picks which hotel each participant stays in (or leaves them unassigned), in one
  click per person.
- **Everyone grouped by hotel.** The same page lists everyone **grouped by hotel** —
  Hotel 1's list, Hotel 2's list, and a "Not assigned" group — with a per-hotel
  headcount and how many are confirmed, plus each person's room-need flag, so the team
  can manage each hotel's room block at a glance. Empty hotels still show, so it's
  obvious a hotel has no one in it yet.
- **A confirmation number per person.** Once a hotel returns a reservation number,
  the organizer records it against that person.
- **Room-block occupancy at a glance.** *(✅ 2026-06-17)* Each hotel can carry the
  **size of the room block** you reserved there (set it on the hotel's edit form). A
  new **Room block occupancy** page then shows, per hotel, the reserved block against
  the number of people you've placed there — counting only those who actually said
  they need a room — with the rooms still free and a clear **within block / over by N /
  no block set** status. A roll-up across all hotels shows your total reserved rooms,
  how many room-needers are placed, how many rooms are still free, and — importantly —
  **how many people who need a room aren't placed in any hotel yet**, so it's obvious
  whether the room plan still holds. A green banner confirms when everything fits; an
  amber one flags exactly what needs attention. Read-only (it doesn't move anyone),
  mobile-first and available in English and Danish.
- **The hotel details land in the person's email.** When a participant's hotel
  calendar invite/email goes out, it now names their **assigned hotel, its address and
  their confirmation number** (the per-person number takes priority over any legacy
  vendor number), so each person sees exactly where they're staying. In the test
  environment all such mail is still safely redirected to the team inbox.
- **Mobile-first and bilingual.** Both pages work on a phone and are available in
  English and Danish.

## 4. Self-service forms — crew fill in their own details

Each form is short, mobile-friendly, and wired so that completing it does the right
follow-up automatically.

- **Appreciation dinner.** RSVP with a calendar invite, and capture dietary needs
  for the dinner.
- **Hotel.** Book a room and receive a hotel calendar invite; this feeds the
  rooming list and the room-night forecast.
- **Lunch.** Sign up for pre-day and main-day lunch.
- **Speaker info & editable bio (✅ 2026-06-15).** Speakers manage their own
  details and edit their **public bio** in tabbed sections (Bio · Tagline · Links
  & Social · Photo · Sessions). The bio is seeded from Sessionize but owned by the
  speaker — anything they change is kept and the nightly Sessionize sync won't
  overwrite it (see §6). Mobile-first, keyboard- and screen-reader-friendly.
- **Preferred email for calendar & messages (✅ 2026-06-15).** Many speakers don't
  use the email they registered with on Sessionize for their day-to-day calendar
  and mail. A speaker can set a preferred address on their form ("blank = use your
  Sessionize address"); when set, **all** calendar invites and emails — from the hub
  **and** Zoho Backstage — go to that address instead. Their Sessionize/community
  email stays their sign-in and the key the hub matches them on, so login keeps
  working and a Sessionize re-import never disturbs the preference. The field is
  mobile-first, validates the address format, and shows a clear confirmation when it
  changes.
- **Swag.** Choose polo, jacket and award preferences.
- **Travel.** Submit a reimbursement claim, which automatically creates the
  matching invoice/payout task.
- **Volunteer sign-up.** A single guided, multi-step wizard that sets up the right
  tasks for each volunteer (the older one-page form was retired so there is exactly
  one path — ✅ 2026-06-14).
- **Late-change alerts done right.** If someone edits their hotel, dinner or shift
  details after the change deadline, organizers are notified of the late change;
  edits before the deadline stay quiet — no inbox noise.
- **Every form confirms it saved (✅ 2026-06-16).** Submitting any self-service form
  — hotel, dinner, lunch, swag, travel, speaker details, the volunteer wizard and
  the first-run onboarding wizard — now shows the same clear "saved" confirmation
  banner, announced to screen readers and dismissable, so no submit is silent.
- **Clear, in-place guidance when something's missing (✅ 2026-06-16).** Forms now
  flag exactly which field needs attention right next to it, and re-check on the
  server so nothing slips through. Examples: the dinner asks you to pick yes/no/maybe,
  the hotel form requires a room choice and valid check-in/check-out dates
  (check-out after check-in), the swag form needs a polo choice, and the travel form
  requires an amount when you pick "Other".
- **Structured dietary & allergy capture for catering (✅ 2026-06-16).** The dinner
  and speaker forms replace the unusable free-text allergy box with a structured
  picker: a diet choice (vegetarian, vegan, pescatarian, halal, kosher) plus
  tick-boxes for the common allergens (gluten, nuts, shellfish, dairy, egg, fish,
  soy, sesame and more), with a free-text box only for anything not listed. Because
  it's structured, the caterer gets real head-counts ("how many gluten-free, how many
  vegan") instead of a pile of notes. Day-catering (collected on the speaker form)
  and the Appreciation Dinner are tracked separately. Mobile-first and accessible
  (the allergen group is a labelled fieldset).
- **Ask for a change after the deadline (✅ 2026-06-17).** Once the change deadline
  passes, the forms become read-only — but that's no longer a dead end. A locked
  form now shows a **"Request a change"** link that opens a short page where you
  describe what needs updating (for example "my arrival moved to the 8th, please
  change my check-in"). Your request goes straight to the organizers' to-do list, and
  the page shows you which of your requests are still pending so you don't have to ask
  twice. No more emailing around to fix a detail after the cut-off. Mobile-first,
  available in English and Danish, and accessible.

## 5. Tasks & reminders — nothing slips, no inbox spam

- **A personal to-do list for every person.** Each crew member sees only their own
  tasks, can tick them off, and the list fills itself from the forms they complete
  and the role they hold.
- **One consistent "what do I still owe" checklist, everywhere.** The same unified
  checklist — what's pending, what's done, and a clear **overdue** badge with the
  number of days late — now renders identically on the hub home, the Tasks page and
  the attendee My-event page, instead of competing landing surfaces each showing a
  different list. Pending items deep-link straight to the form that completes them,
  and a sponsor contact's company-scoped tasks are included, so the checklist never
  says "all done" while sponsor work is still outstanding. *(✅ 2026-06-15)*
- **Speaker deadlines, scheduled for you.** Each speaker automatically gets a dated
  task for every key milestone, so the path from accepted to on-stage is laid out
  for them. The milestones now carry confirmed, fixed calendar dates (2026-06-14):
  verify bio + photo in the hub (1 Oct 2026), upload a draft preview deck (20 Jan
  2027) and upload the final slide deck (3 Feb 2027) for all speakers, plus a
  Master-Class-only "submit title and abstract" (20 Jun 2026). Reminders count down
  (14 / 7 / 3 / 1 days) to each fixed date.
- **A speaker hub that shows the whole journey at a glance.** Speakers (and
  master-class speakers) get their own page that turns those milestones into one
  cohesive, mobile-first tracker: a progress bar (X of N done), a per-milestone
  card with a live countdown ("12 days to go" / "due today" / "overdue 3 days"),
  a one-tap "Mark done" / "Reopen", a clear "next up" summary, and a quick link to
  finish their speaker details and travel claim — so a speaker always knows exactly
  where they stand without digging through the generic task list. *(✅ 2026-06-14)*

  | Speaker hub | …on a phone |
  |---|---|
  | [![Speaker hub milestone tracker](img/speaker-hub.png)](img/speaker-hub.png) | [![Speaker hub on mobile](img/speaker-hub-mobile.png)](img/speaker-hub-mobile.png) |

  *The speaker hub turns deadlines into a progress tracker with live countdowns, "My sessions" and a public-profile preview — mobile-first, the same on a phone.*
- **"My sessions", right on the speaker hub.** The hub now shows each session a
  speaker is presenting — title, day and time, room (or a clear "to be scheduled" /
  "room to be assigned" when the grid isn't published yet), any co-speakers, a
  "master class" badge where it applies, a one-tap jump to the attendee questions
  for that session (with an open-question count), and — once they're announced — a
  link to their public session page. Speakers see exactly how and where they
  present without asking an organizer. *(✅ 2026-06-16)*
- **A My Sessions hub — every session as a card with its own actions.** *(✅ 2026-06-21)*
  The speaker's sessions are now laid out as a **per-session grid**, each card carrying
  the actions for that talk: **SoMe Promote** (build a share post), **Calendar Sync**
  (download the session `.ics`), an **Evaluations** link once results are published, and
  a **Session Template**. A small **per-session action legend** explains what each action
  does, so a speaker manages each talk in place. Mobile-first, in English and Danish.
- **Bio is now a dedicated bio-only page.** *(✅ 2026-06-21)* A speaker's public bio lives
  on its own focused page, separate from their personal details, so editing the bio is a
  single clear task. The other **speaker details — preferred email, accreditation,
  country, gender and "first-time speaker"** — have moved to **My Profile**, keeping each
  page about one thing.
- **Speaking days are derived automatically.** *(✅ 2026-06-21)* A speaker's **speaking
  days** are now worked out from their accepted sessions rather than asked for separately,
  and are shown and managed on the organizer **Speakers** page — so the days a speaker is
  needed always match the sessions on the agenda.
- **A Calendar + Event-logistics fold-out for speakers.** *(✅ 2026-06-21)* The speaker
  hub now carries a fold-out covering the practical event logistics a speaker needs —
  **hotel, dinner, lunch, speaker gift and travel** — plus a one-tap **Contact
  Organizers**, so everything a speaker needs for the day is gathered in one place.
  Mobile-first, in English and Danish.
- **See how your sessions were rated.** A speaker now has a self-service "My session
  ratings" page: for each of their own sessions it shows how attendees rated it
  (the quick 1–5 smiley score they leave via the room QR code), the average and
  number of ratings, and every anonymous written comment — newest first — plus an
  overall score across all their sessions. Speakers no longer have to wait for an
  organizer to forward the results; they can check the feedback for their own talks
  whenever they like. The comments are anonymous and a speaker only ever sees their
  own sessions. *(✅ 2026-06-17)*
- **Preview your public profile.** From the hub a speaker can preview their public
  speaker page exactly as attendees will see it — the moment the organizers select
  them for the line-up. Until then the hub explains the preview unlocks at
  announcement and points them to polish their bio and photo first (their edits are
  always kept). *(✅ 2026-06-16)*
- **Speaker deadlines in your own calendar.** Speakers can subscribe their milestone
  deadlines to their personal calendar with one tap (copy link or subscribe), so new
  and moved dates stay in sync automatically — the same trusted per-person calendar
  feed volunteers already use. *(✅ 2026-06-16)*
- **A reminder engine that's gentle and reliable.** Reminders go out on a sensible
  cadence per type — speaker milestones counting down (and a nudge if overdue), a
  weekly digest of what's still pending, weekly sponsor and form chasers, and a
  short series for general tasks. It never double-sends and quietly catches up if a
  day is missed.
- **Tuned entirely through settings.** Whether each reminder type is on, how often
  it goes, exactly what it says, and who receives it (including CC, BCC and
  escalation) are all configurable — no code changes.
- **Built to minimize email.** The guiding principle is to nudge only when
  something is actually overdue.
- **Sync your reminders to your own calendar.** Every speaker, volunteer and
  organizer can subscribe their hub deadlines and shifts to the calendar they
  already use — Outlook, Google or Apple. Add the link once and it keeps itself
  up to date: new reminders, moved deadlines and completed items flow through
  automatically, with a gentle pop-up reminder a week and a day before each due
  date. Each person sees only their own items. The hub shows a one-click
  "Subscribe" link with a copy button and short per-app instructions, plus a
  "Download .ics" button to drop a single item straight into a calendar. The
  subscribe link is private to each person and an organizer can reset it at any
  time. *(✅ 2026-06-15)* The subscribe link now uses a short, friendly address.
- **The event lands in your calendar the moment you're activated.** When an
  organizer activates a person, their activation email carries a calendar invite
  for the event itself — one tap and the dates are in their calendar, alongside the
  link to subscribe to their personal deadlines. *(✅ 2026-06-15)*
- **Organizers can turn calendar sync on or off.** A simple switch (on by default)
  controls calendar sync for the whole edition — when off, the personal feed and
  the activation invite are disabled and the "Add to my calendar" card is hidden.
  *(✅ 2026-06-15)*
- **Preview your own calendar feed before you share it.** Right on the calendar
  settings page, an organizer now sees a read-only preview of exactly what their
  personal calendar feed contains — each item with its date and where it happens —
  so they can confirm the feed looks right before passing the subscribe link along.
  *(✅ 2026-06-18)*
- **Key dates, tailored to each role — and synced to your calendar.** Everyone now
  sees a short table of the dates that actually apply to them: move-in, logistics,
  packing, setup, pre-day/master-class and main day, plus the timed moments on the
  pre-day (Party 16:00, Group photo 17:30, Appreciation Dinner 18:00). A speaker
  never sees move-in; sponsors don't see the group photo — each entry is shown only
  to the roles it's tagged for (**Media** covers both the video and photo crew).
  Every row carries a one-tap **"+ calendar"** download, and the whole list is part
  of the personal calendar feed, so dates land in your own calendar and stay current.
  Organizers manage it all at **Schedule / key dates** (reached from the Logistics
  hub) — add/remove entries, mark them all-day or timed, and tick which roles see
  each one; sensible defaults are seeded automatically. *(✅ 2026-06-20)*
- **Anyone can RSVP to the Party — no login.** A public page at **`/Party`** asks for
  name + email and an opt-in to attend the Party (16:00–18:00 on the pre-day). It's
  spam-hardened (honeypot + IP hash), upserts by email (re-submit to change your
  mind), and offers an **"Add to my calendar"** download on confirmation — no email
  invite (so no inbox needed, no spam risk). Organizers get the **attending
  headcount** + a CSV at **Party RSVPs** (Logistics hub) — the figure for the Bella
  Center food order. Signing up grants no hub access. *(✅ 2026-06-21)*

## 6. Sessions & surveys — from call-for-speakers to the schedule

| Public speaker line-up | Public session detail |
|---|---|
| [![Public speakers page](img/public-speakers.png)](img/public-speakers.png) | [![Public session detail](img/public-session-detail.png)](img/public-session-detail.png) |

*The public, no-login programme: only published speakers appear (each links to their sessions), and every talk has a shareable detail page with "Add to my calendar" and "ask the speaker".*

- **Pull your speakers straight from Sessionize.** Connect a Sessionize API
  endpoint and the hub pulls the **accepted**-speaker list automatically — nightly,
  or on demand from an organizer button — creating or updating speakers (matched on
  email, never overwriting roles) and reporting anyone it skipped. No file shuffling.
  - *Setup (one time):* in Sessionize open your event → **API/Embed** → create a
    new API endpoint → name it → choose **JSON** → include all built-in fields →
    enable the **speaker emails** advanced field (required, or every speaker is
    skipped — email is the match key) → configure the **accepted-speakers** view →
    **save** → copy the endpoint id. The URL looks like
    `https://sessionize.com/api/v2/<your-event-id>/view/All`.
    The endpoint id is ordinary operator configuration (not a secret): put it in the
    hub's per-edition config (your private `integrations.<edition>.json` or gitignored
    custom config). Keep the real id out of the public mirror, but it is plain config —
    not a Key Vault secret.
- **Set or switch your Sessionize endpoint from the hub — with a safe change prompt.**
  An organizer **endpoint settings page** lets you set or edit the Sessionize endpoint
  id (and view) for the edition right in the hub, on top of the deployment default —
  no redeploy. The endpoint that's currently in effect is shown, and the live importer
  picks up a newly-saved id immediately. **When you change the endpoint** (the typical
  case is switching from your call-for-speakers event to the accepted-lineup event),
  the hub asks how to treat the speakers you've already imported:
  - **Replace** — replace the existing data and re-import the accepted speakers from
    the **new** endpoint. This is the **normal production path**; it runs as a **Full
    import** (every bio field re-seeded from the new endpoint).
  - **Merge** — merge with what's already there (**for testing only**, e.g. checking a
    new endpoint's data against existing rows). It runs as a **delta sync** and
    **never flushes a speaker's own edits**.
  Your choice and the endpoint are saved per edition; the page records the choice and
  points you at the matching import button — it never starts an import on its own, so
  switching the endpoint is always a deliberate two-step action. *(✅ 2026-06-15)*
- **Sessions come across too — linked to their speakers.** The same Sessionize
  pull also imports your **sessions** (the Sessions/All view), creating a session
  record for each talk and **linking it to its speaker(s)** — a session can have
  several co-speakers and a speaker several sessions. Each speaker's linked
  session(s) show up on the organizer **speaker overview** (with a total session
  count), so you can see who is presenting what at a glance. Sessions upsert by
  their Sessionize id on every pull (nightly or on-demand) — new and changed
  sessions are refreshed in place, nothing is duplicated and nothing is deleted.
  Sessions stay **inside the hub** (they are not pushed to speakers' public profiles
  or to Backstage). *(✅ 2026-06-15)*
- **Add your own sessions in the hub.** Not everything comes from Sessionize — add a
  **sponsor session** (or any other session) directly in the hub, alongside the
  imported ones. Hub-added sessions are clearly marked and are **safe across
  re-imports**: a Sessionize pull never touches or removes them. *(✅ 2026-06-15)*
- **Preview before you import — see exactly what would change.** Before committing a
  Sessionize import (delta or full, API or spreadsheet) you can run a **dry-run
  preview** that changes nothing and shows **how many speakers would be created,
  updated or left unchanged** — and, crucially, **which speaker bios would be
  overwritten**, with the ones a speaker has personally curated in the hub clearly
  flagged. A full import is the only one that overwrites, so the preview spells out
  the cost up front; a delta preview confirms it would overwrite nothing. The real
  import is a separate, explicit click behind a clear confirmation, so a blind import
  can never silently clobber curated speaker content. *(✅ 2026-06-16)*
- **Delete a bad or duplicate session — safely.** Organizers can **delete a session**
  from the sessions admin, behind a confirmation dialog. The delete is safe by design:
  a session that has collected **attendee questions, evaluations or master-class
  bookings is protected** and won't be deleted (you're told what to clear first), so
  attendee input is never silently lost; a clean session is removed along with its
  speaker links (nothing is left orphaned). If the session came from Sessionize you're
  reminded that a future import will bring it back unless it's removed there too.
  *(✅ 2026-06-16)*
- **Remove someone from the speaker roster — safely, without deleting the person.**
  On the speakers admin, each speaker has a **Remove from speakers** action (single,
  behind a confirmation dialog, or in bulk for a ticked selection). It removes the
  **speaker profile** — bio, photo, accreditation, publish flag — so the person is no
  longer a speaker, while **keeping them as a participant** (their login, hotel,
  travel and everything else are untouched). It is safe by design: a speaker who is
  **still linked to a session on the agenda is protected** and won't be removed — you
  unlink the session(s) first, so the running order is never silently orphaned. The
  bulk action reports honestly how many were removed and how many were kept because
  they're still on the agenda. *(✅ 2026-06-17)*
- **Type and length on every session.** Each session carries a **type** (Community
  Master Class, Community Tech Session, or Sponsor Session) and a **length** (full
  day, 20, 50 or 60 minutes). Imported sessions get a sensible default (length from
  the scheduled times, with a full-day session treated as a master class); you set
  them yourself on hub-added sessions, and can adjust any session. The session list
  **filters by type and length** so you can find exactly the set you want.
  *(✅ 2026-06-15)*
- **A public sessions overview anyone can browse.** A clean, no-login page at
  **`/Sessions`** lists the live edition's sessions with their **speaker(s), type,
  length, room and scheduled time**. Visitors **filter by type and length** (and by
  room), and **search** by title, speaker or room — so an attendee can quickly find the
  talks that interest them. Each **session title links to its own detail page**, and each
  **published speaker's name links to that speaker** — so you can hop from a talk to the
  person and back. It is read-only and mobile-first, with a friendly empty state when
  nothing is published yet. *(✅ 2026-06-15)*
- **A shareable page for every session.** Each talk has its own clean, no-login page at
  **`/Sessions/{id}`** with its **title, abstract, type, length, room and time**, the
  **speaker(s)** (cross-linked to their speaker page) and one-click links to the
  session's **master-class info & setup page** (master classes) and **"ask the speaker a
  question"** page — so a single talk can be deep-linked, shared and indexed. A scheduled
  talk also offers a one-click **"Add to my calendar"** download (`.ics`) so an attendee
  can drop the session straight into Outlook / Google / Apple Calendar from the public page
  — no login, and re-downloading updates the same entry instead of duplicating it.
  Read-only and mobile-first. *(✅ 2026-06-15)*
- **A public speaker lineup anyone can browse.** A clean, no-login page at
  **`/Speakers`** introduces this year's speakers — each with their **photo, tagline and
  the session(s) they're presenting** (each session linked to its detail page). Only
  speakers the organizers have **chosen to publish** appear, so the lineup is never shown
  before it's ready: until speakers are selected the page shows a friendly **"speaker
  lineup coming soon"** message, and each speaker appears automatically the moment
  they're selected. Every published speaker also has their **own page at `/Speakers/{id}`**
  (bio + their sessions) — and that same publish gate means an unselected speaker is never
  linked or reachable. Read-only and mobile-first. *(✅ 2026-06-15)*
- **A public front door at `/`.** Anyone landing on the site — not signed in — gets a
  welcoming page with the **event name, dates and venue**, a **Visit-event / Sign-in**
  call to action, and clear cards into the public **Sessions, Speakers, Sponsors and
  Master Classes** pages — so the public programme is shareable and discoverable without
  a login. Signed-in crew still go straight to their personal hub. Mobile-first,
  accessible, and bilingual (English / Danish). *(✅ 2026-06-15)* *(Superseded
  2026-06-21: the `/` landing page was retired so an anonymous visitor now goes straight
  to sign-in; the public programme pages stay reachable by their own addresses — see §2.)*
- **A day-by-day agenda anyone can scan.** A public, no-login page at **`/Agenda`** lays
  the programme out as a **running order, grouped by day** and shown in start time order —
  each talk with its **time, room, type and speaker(s)**, deep-linking to the full session
  page. It's the "what's happening, and when" view that sits alongside the searchable
  `/Sessions` list (the list is for finding and filtering; the agenda is for planning your
  day). Talks that don't have a time yet are kept off the timetable (and pointed to the
  list), and the page shows a friendly "running order coming soon" message until the
  schedule is published. Linked from the public front door, mobile-first, accessible, and
  bilingual (English / Danish). *(✅ 2026-06-17)*
- **Every public section is one tap away.** The public navigation (shown to anyone who
  isn't signed in) now reaches the whole public site — **Agenda, Sessions, Master classes,
  Speakers, Sponsors and Contributors** — so no public page is an orphan. "Master classes"
  jumps straight to the programme pre-filtered to the master-class talks, and that filter
  (like every Sessions filter) lives in the page address, so a filtered view is **shareable
  and bookmarkable** — send someone the link and they see exactly what you saw. Mobile-first
  and bilingual. *(✅ 2026-06-18)*
- **Times shown in the event's local time, not UTC.** Public timestamps now read in the
  venue's wall-clock time: the **master-class logistics** "last updated" stamp and the
  **survey-results "latest response"** time are shown in the event's timezone with a clear
  zone label (e.g. `UTC+01:00`) instead of raw UTC — so a reader never has to do the maths.
  *(✅ 2026-06-18)*
- **A clearer "no responses yet" survey dashboard.** When a topic-interest survey has no
  responses, the live-results page now leads with a friendly **headline** (not just a small
  line of text), so a freshly-shared dashboard reads as "ready and waiting" rather than
  looking broken. Bilingual (English / Danish). *(✅ 2026-06-18)*
- **Manage your surveys from one organizer page.** A new **Surveys** page (in the
  *Sessions & speakers* area) lists every survey with its **response count** and whether it's
  **open or closed**, and for each one you can:
  - **Grab the shareable links** — the public survey link and its results link, each with a
    one-tap **Copy** button (and shown as plain, selectable text so it works even with
    JavaScript off);
  - **Open or close submissions** — flip a survey **closed** when you're done collecting and
    the public page shows a friendly "this survey is closed" message while the **results stay
    viewable**; **activate** it again any time. (Surveys are open by default.)
  - **View the results right there** — the same demand breakdown the public dashboard shows,
    embedded in the organizer page, plus a link to open the full public dashboard;
  - **Reset responses before a fresh send-out** — a safe, **type-to-confirm** action that
    deletes every response for that one survey (and nothing else), so you can start clean
    before sending it out. Organizer-only, and every reset is recorded in the logs.
  Mobile-first and bilingual (English / Danish). *(✅ 2026-06-18)*
- **A Contributors page that credits people properly.** The public **Contributors** page now
  shows each person with an **avatar** (their photo when provided, otherwise their initials),
  their **name** (linking to their profile when available) and their **role** — a polished,
  mobile-first credit roll for the organizer team and everyone who helps build the hub.
  *(✅ 2026-06-18)*
- **A QR code for every room — on the screen and in the slides.** Give each physical
  room a single QR code linked to that room. The hub generates it, stores the image on
  your **SharePoint**, and attaches its link to every session in the room — so each
  speaker gets a **"Download QR"** button to drop the code straight into their
  PowerPoint. *(SharePoint storage is set up once by your operators; until then the hub
  tells you it isn't wired rather than inventing a link.)* *(✅ 2026-06-15)*
- **Collect session feedback, deliver it to the speaker.** Two evaluation paths: a
  physical **HappyOrNot** smiley box at the room (results read off its report and, with
  one click, **emailed to the session's speaker(s)** after the talk), and a **QR-code
  evaluation** form you can attach per session. Both reach the speaker's preferred
  inbox. *(A future option to collect feedback on attendees' own devices via an API is
  designed as a drop-in.)* *(✅ 2026-06-15)*
- **Quick attendee evaluation page (HappyOrNot-style) + results dashboard.** Every
  session has a **public, no-login rating page** (`/sessions/<token>/evaluate`,
  addressed by the same unguessable per-session token as the ask page) where an
  attendee taps a **1–5 smiley** and, if they like, leaves a short comment — built for
  phones, takes a second, fully anonymous. **Point the room QR at it** (the organizer
  session view shows the exact link to encode). **Light anti-abuse, no login:** one
  rating per attendee/session (a re-visit on the same device updates their rating
  rather than stacking duplicates), plus a honeypot and a soft rate-limit — the same
  spam-resistance the public forms use. Organizers get a **results dashboard**
  (*Session evaluations*) with **per-session and per-room** averages, counts and the
  anonymous comments, **filterable by session type and room**. *(A future option to
  collect the same ratings on attendees' own devices via an API would feed the same
  dashboard with no other changes.)* *(✅ 2026-06-15)*
- **Let attendees ask questions for a session — before the event.** Every session
  has a **public, no-login link** (`/sessions/<token>/ask`, addressed by an
  unguessable per-session token so it can't be guessed) that lands on a clean
  mobile-first page showing the session and its speaker(s) with a single "ask a
  question" form. Great for masterclass logistics or topics attendees want covered.
  - **Questions stay inside the hub — never posted publicly.** A submitted question
    goes only to the organizers and the session's speakers. Name and email are
    optional (ask anonymously if you like); only the question text is required.
  - **Organizers see everything; speakers see their own.** Organizers get an
    edition-wide view of all questions (grouped by session, with each session's
    shareable ask link). A speaker sees and answers the questions for the session(s)
    they're on — and a reply is **visible to the co-speakers on the same session**
    too, so a masterclass team can coordinate what to prepare.
  - **Spam-resistant by design.** The public form carries a honeypot and a soft
    per-IP rate-limit (same approach as the public survey), so bots and floods are
    quietly dropped without bothering real attendees. *(✅ 2026-06-15)*
  - **Speakers are told when new questions arrive — no need to keep checking.**
    When attendees send in new questions for a speaker's session(s), the hub emails
    that speaker a short digest ("you have N open questions across M of your
    sessions") with one button straight to the page where they read and answer them.
    It is sent on the hub's normal daily schedule, only counts **open** (unanswered)
    questions, and is **smart about repeats**: a digest only goes out again once a
    genuinely new question arrives — answering or closing questions never triggers a
    re-send, so a speaker is never spammed about the same questions twice. Their own
    Questions page also shows a live "N open questions awaiting your reply" line and a
    note that the digest will reach them. Co-speakers on a shared session each get
    their own digest. *(✅ 2026-06-17)*
- **Or import from a spreadsheet.** Prefer files? Upload your Sessionize Excel export
  instead and the hub reads the columns in any order, with the same create/update
  rules and skip reporting. No network dependency — just the file. *(Speakers only;
  the API pull is the path that also brings sessions.)*
- **Imported speakers get welcomed automatically.** New speakers receive their
  welcome email on import, once — sent to their preferred address if they set one.
- **Re-imports respect a speaker's preferred email.** A speaker's preferred
  contact email (see §4) is a hub-collected preference: a Sessionize re-import
  matches on the original community email and refreshes bio/social links, but
  never overwrites the preferred address. *(✅ 2026-06-15)*
- **Push approved speaker bios to your public site — safely.** When your lineup is
  set, the hub can mirror each speaker's bio (tagline, biography, blog, LinkedIn, X)
  out to your Zoho Backstage speaker page. It is built to be safe by default:
  - **No one goes public by accident.** A speaker is only ever made *visible* on
    Backstage when you explicitly approve them (a per-speaker "selected for publish"
    switch that starts **off** for everyone). Until then their bio is only ever
    written as a hidden **draft** — an unselected speaker is never exposed.
  - **Off until you turn it on.** There is no automatic or scheduled push; it runs
    only when an organizer chooses to run it. *(◻ live activation pending — needs your
    Backstage credentials and a selected lineup; built + tested, off by default.)*
- **Speakers own their bio — Sessionize just seeds it.** Each speaker's public
  profile (bio, tagline, LinkedIn / X / blog links, photo) is seeded from
  Sessionize but **belongs to the speaker** once they touch it. *(✅ 2026-06-15)*
  - **Edit it yourself, in tabs.** On their own page a speaker edits their bio in
    tidy tabbed sections — **Bio · Tagline · Links & Social · Photo · Sessions** —
    mobile-first and keyboard/screen-reader friendly. One Save stores everything.
  - **The nightly sync never flushes your edits.** The scheduled Sessionize pull
    runs in **delta** mode: it adds **new** speakers and fills only fields that are
    still **empty and untouched**. Any field a speaker has edited in the hub is
    left exactly as they wrote it — re-imports can't overwrite it.
  - **One-click full refresh when you want Sessionize to win.** An organizer can
    run **"Full import from Sessionize"** to pull **all** accepted speakers and
    **all** fields and force-refresh every bio from Sessionize (clearing speaker
    edits) — the deliberate complete re-seed, with a created / updated / skipped
    summary. Neither path ever changes a speaker's role, deletes anyone, or sends
    email.
- **Welcome people you add by hand, too.** When an organizer creates a participant
  manually, they can send the welcome email right then — or send it later from the
  edit screen — using the same branded template. It is sent only once per person, so
  there is no risk of a duplicate welcome. *(✅ 2026-06-14)*
- **Public, no-login surveys.** Run a beautiful 3-step survey at its own web
  address — pick a track, rank topics, set your level — with a live results
  dashboard anyone can view. Spam protection is built in and no sign-in is
  required.
- **Call-for-speakers demand survey.** Gather what your audience actually wants:
  weighted topic rankings, per-track breakdowns and a level distribution, all on a
  shareable results page that helps you shape the agenda.
- **Mobile-first and polished.** Surveys feature a clean hero, per-step imagery,
  friendly skill levels, clear ranking, and per-track deep links — designed for
  phones first.
- **Public master-class logistics page.** Every master class gets its own clean,
  no-login web page where the speaker(s) and organizers publish setup instructions
  for attendees — "bring your laptop charged", what to install beforehand, how to
  prepare the environment. Share the link with booked attendees or drop it into the
  Backstage session description. Only an involved speaker or an organizer can edit
  the text (they simply sign in); everyone else just reads it. A "show public link"
  button on the organizer session view and on the speaker hub gives you the URL in
  one click. Mobile-first and accessible. *(✅ 2026-06-15)*
- **A Master Class communication page at `/MasterClass/{slug}`.** *(✅ 2026-06-21)*
  Every master class's public page now opens with a standard, plain-language **"What is
  a Master Class?"** explainer and a short **laptop / charging FAQ** above the per-class
  logistics, so a first-timer understands what to expect — bring a charged laptop, what
  the format is — before reading the class-specific setup notes. The same page carries
  the speaker- and organizer-edited logistics underneath. Mobile-first, accessible and
  bilingual (English / Danish).
- **Master-class participant sync from Zoho Booking.** Pull the people who booked a
  master class straight into the hub, linked to the right class — one-way from Zoho
  Booking, so Booking stays the source of truth. Each master class has its own
  Booking endpoint that organizers set in master-class management, and a "sync now"
  button. Re-running the sync never creates duplicates, and newly-booked people land
  in the normal validation queue (they can't sign in until an organizer activates
  them). Until your Booking endpoint and credentials are connected it honestly
  reports "not configured" rather than inventing participants. *(✅ 2026-06-15)*
- **Speaker emails pulled from Sessionize's secured side-view.** Sessionize keeps
  speaker emails off the public speaker view (privacy) and serves them only through
  a separate, token-protected view. The hub now reads that secured view and matches
  it to each speaker, so the import gets the email it needs to create the account —
  without it speakers were silently skipped. The token is stored as a secret, never
  in the repo. *(✅ 2026-06-20)*
- **Sessionize sync runs every hour.** The speaker + session pull now refreshes
  hourly instead of once a day, so new acceptances and schedule tweaks show up the
  same hour. It's still additive and safe — new people and empty fields only, never
  deletes, never emails, and a missed hour is simply caught the next run. *(✅ 2026-06-20)*
- **Your own talk lands in your calendar.** Each speaker's accepted session(s) —
  title, room and time — are added to their personal calendar feed automatically, so
  the talk they're giving sits alongside the key dates they need to be there for.
  *(✅ 2026-06-20)*

## 7. Sponsors — managed as companies, with the right tasks

| Public sponsors page | Sponsor portal |
|---|---|
| [![Public sponsors page](img/public-sponsors.png)](img/public-sponsors.png) | [![Sponsor portal](img/sponsor-portal.png)](img/sponsor-portal.png) |

*Sponsors grouped by tier on the public page (with an initials badge when no logo is uploaded), and the signed-in sponsor portal: profile, booth, deliverables checklist, leads and order status — one company's view.*

- **A sponsor is a company, not a single contact.** Every contact at a sponsor
  company sees that company's shared tasks, so nothing depends on one person.
- **Your company directory stays the source of truth.** Company and contact details,
  including who signs and who coordinates the event, come from your central company
  directory; the hub reads from it and never duplicates it.
- **Always the right public company name.** Sponsor-facing text shows the company's
  chosen public name, with a sensible fallback chain so a name always appears.
- **A public sponsors page that thanks your supporters.** A clean, no-login page at
  **`/Sponsors`** lists your sponsor companies **grouped by tier** (Platinum, Diamond,
  Gold, Feature, and other supporters), each with their **logo**, their **public
  company name**, and an optional **link to their website**. When a company hasn't
  uploaded a logo yet, a tidy initials badge stands in. Read-only and mobile-first,
  with a friendly empty state before sponsors are announced. *(✅ 2026-06-15)*
- **Sponsor tiers fill in automatically from their booth order.** ✅ 2026-06-18 — the
  public sponsors page groups companies by tier, and now a company lands in the right
  tier group on its own: when its booth order is processed, the hub reads the booth
  tier from the order and stamps it onto the company, so a sponsor appears under
  Platinum / Diamond / Gold / Feature without an organizer setting it by hand. A
  company that orders several booth products is filed under its **highest** tier. It is
  also safe to override: the automatic stamp only ever fills a blank tier or raises a
  lower one, so a tier you've deliberately bumped (for example, a comped upgrade) is
  never quietly downgraded on the next order sync.
- **A real "become a sponsor" call-to-action.** *(✅ 2026-06-17)* The public sponsors
  page now offers a prospective sponsor a clear way to reach out — a prominent
  **"Contact us about sponsoring"** button that opens either a pre-filled email (with
  the event name already in the subject) or your hosted sponsorship page/form,
  whichever you've set. It shows in **both** the "sponsors coming soon" state and the
  populated page, and if you haven't set a sponsorship contact it simply doesn't
  appear (no dead button). Mobile-first and available in English and Danish.
- **Clean up stale sponsor entries — safely.** Sometimes a booth order is processed
  under a wrong or later-changed company id, leaving an **orphaned company card**
  (logo, description, website, tier) on the public sponsors page even though no one
  from that company is in the edition any more. The sponsors admin now lists these
  **stale company facts** (only the rows whose company has **no active contact**) and
  lets an organizer delete them behind a confirmation dialog, so they stop showing
  publicly. It is safe by design: the facts of a **live** sponsor — one that still has
  an active contact — are protected and can't be deleted from here (handle the
  contacts first). *(✅ 2026-06-17)*
- **Booth tasks generated from what each sponsor bought.** Each booth product is
  recognized automatically and turned into the right set of tasks — shared booth
  basics plus the extras that come with each tier (Platinum / Diamond / Gold) — so
  new booth products need no setup.
- **A baseline checklist for every sponsor.** Logo upload, onboarding, company
  description, attendee-bag insert and shipment, and the app-game are set up for all
  sponsors.
- **Deadlines that make sense.** Most sponsor deadlines are anchored to the event
  date; logo and description deadlines are anchored to the first order — all
  configurable per edition.
- **No duplicate tasks, ever.** Tasks are de-duplicated across orders, so a company
  never sees the same item twice.
- **Clear, friendly task wording.** The sponsor task list is hand-curated for
  clarity, with narrative guidance rather than raw order data.
- **Automatic work stays invisible.** Things the platform handles behind the scenes
  (webshop-to-portal sync, ERP sync, currency checks, masterclass reconciliation)
  are never shown to sponsors as to-dos.
- **Reminders reach the right people.** Assigned tasks nudge the responsible contact
  with event coordinators copied; unassigned tasks go to the coordinators; signers
  are never bothered with task reminders.
- **Tidy buttons instead of long links.** Task instructions render link text as
  clean buttons, hiding long underlying URLs.
- **Per-task upload folders with change alerts.** Each task can have its own upload
  folder with a simple edit link, and the hub notices when files change — and now
  reliably sees every file in a folder, however many there are. *(✅ 2026-06-14)*
- **Sponsor details and sponsor tasks, side by side.** Sponsors get a details card
  (company info, linked contacts, who manages them) alongside their task list.
- **Full sponsor management for organizers.** Organizers can add, link or remove
  coordinators, set the default signer and coordinator, and create or edit tasks
  targeted at all exhibitors, all sponsors, or a specific tier.
- **Accounting that keeps itself in step.** *(🟡 2026-06-15)* New sponsors flow into
  your accounting system as customers, their contacts are created with the right
  roles (who signs, who coordinates), and webshop orders become accounting orders —
  all using the sponsor's chosen public company name. Each step is idempotent, so
  re-running never creates duplicates. *(Optional + off until your accounting/webshop
  credentials are configured; until then it shows exactly what it would do and never
  touches a live system.)*
- **Tax-ID checked before a sponsor is created.** *(🟡 2026-06-15)* A new sponsor's
  company tax-id is validated automatically, catching typos and invalid numbers up
  front rather than at invoicing time.
- **Right currency, right rate.** *(🟡 2026-06-15)* Orders in a foreign currency get
  a currency check at creation time, with today's exchange rate when a rate source is
  configured — so cross-currency orders are handled correctly instead of silently
  mis-booked.
- **A single sponsor portal — everything you owe and are owed, in one place.**
  *(✅ 2026-06-16)* Signed-in sponsors get a self-service home at **`/Sponsor`** that
  pulls together everything about their sponsorship: their **company profile and
  logo** (with the chosen public name, or a tidy initials badge when no logo is
  uploaded) and **booth tier**; **booth & logistics** quick-links (floor plan,
  exhibitor guide, full logistics); their **deliverables checklist** — the same
  pending/completed view used across the hub, so what's still needed and what's done
  look identical everywhere; a read view of their **leads** (recent + total, with
  links to capture and download); and **order & invoice status** drawn straight from
  the records the hub holds. Where invoicing isn't configured yet, the portal says so
  plainly and shows orders as *pending* rather than inventing an invoice. Each
  sponsor sees only their own company's data, mobile-first and in English or Danish.
- **A sponsor landing card and orientation guidance.** *(✅ 2026-06-21)* The sponsor
  portal now opens with a landing card showing the company's **logo, a short
  description and an overview** of their sponsorship, and the sponsor-tasks page carries
  short **orientation guidance** so a contact knows what's expected of them. **Key dates**
  have moved under **Event logistics**, keeping the home view focused.
- **A Sponsor Webshop fold-out.** *(✅ 2026-06-21)* Sponsors get a **webshop** fold-out
  to **buy extra services**, see their **orders**, and view the **linked contacts** for
  their company — all inside their own portal. Mobile-first, in English and Danish.
- **Exhibitor & Booth Details and Leads fold-outs.** *(✅ 2026-06-21)* Two further
  fold-outs round out the portal: **Exhibitor & Booth Details** surfaces the **Zoho
  dashboard** plus an **in-hub capture-lead failover** so booth staff can still log a lead
  if the Zoho path is unavailable, and a **Leads** fold-out gives a **leads export page**.
  Mobile-first, in English and Danish.

## 8. Sponsor leads — capture, screen and route booth leads

- **Capture leads at the booth, right in the hub.** *(✅ 2026-06-14)* Booth staff
  can type in the people they meet at their stand — name, email or phone,
  company, job title, and what they were interested in — straight from any phone,
  with no app install and no scanner setup. Each captured lead lands immediately
  in the company's leads pipeline and download feed, is screened for junk on the
  way in (the same 0–100 quality check as synced leads), and shows in a
  "recently captured at your booth" list so staff can see what they have logged.
  At least an email or a phone is required so every lead is followable-up, and an
  obviously invalid email is caught before saving. This works alongside the Zoho
  Backstage scanner — a booth can use either or both.
- **A leads API for sponsors.** Sponsors can pull their own leads as JSON or CSV via
  a simple, secured endpoint, with ready-made script samples and a browser-friendly
  "Your Leads API" page.
- **Secure, revocable access per sponsor.** Each sponsor gets its own access key and
  token; keys are shown once and stored only as a secure hash, and access can be
  revoked instantly.
- **A real lead pipeline.** Leads live in a proper pipeline with a live grid to
  Reply, mark Processed, set Interest, or flag Ignore/Junk — nothing is ever hard-
  deleted.
- **Timely notifications.** Sponsors can opt into a daily digest or near-real-time
  alerts of new leads, with junk skipped and recipients defaulting to all the
  company's contacts.
- **Smart junk screening.** Each lead gets a 0–100 quality score and label, and only
  unmistakable test entries are auto-flagged as junk — operators stay in control.
- **See *why* a lead scored the way it did.** *(✅ 2026-06-17)* The quality-score badge
  on the leads grid now expands to a plain-language "why this score?" breakdown — the
  starting baseline plus each factor that moved the number up or down (has a usable
  email, has a full name, company filled in, has a phone, unreachable, or looks like a
  test/junk entry) and the final total. The same logic that scores a lead produces the
  explanation, so the badge and the breakdown always agree. No more guessing why a lead
  was flagged. Available in English and Danish, phone-first.
- **Contact your leads with one tap — no API key needed.** *(✅ 2026-06-18)* Your "Your
  leads" page now lists every lead, inquiry and booth meeting captured for your company
  right in the hub, with a one-tap **Email** and **Call** button on each — opening your
  own mail or phone app with the person already addressed. You don't need to set up the
  download API or run a script to follow up; the API and CSV feed stay there for anyone
  who wants to automate. Junk and ignored leads are hidden, the newest are first, and a
  lead with no email or phone is clearly marked. Phone-first, English and Danish.

## 9. Attendees & masterclass reconciliation — one clear picture

| Attendee "My Event" | …on a phone |
|---|---|
| [![Attendee My Event hub](img/attendee-my-event.png)](img/attendee-my-event.png) | [![Attendee My Event on mobile](img/attendee-my-event-mobile.png)](img/attendee-my-event-mobile.png) |

*The attendee hub: a live countdown, ticket and Master Class status, a personal agenda and self check-in — built phone-first for the person walking up to the venue.*

- **Tickets and masterclass seats reconciled automatically.** The hub compares
  two-day tickets against masterclass bookings and surfaces the mismatches — no
  booking, no ticket, or duplicate bookings — with branded chaser emails to sort
  them out.
- **Attendee status in the hub, bookings stay at the source.** Attendees are synced
  in for visibility, with deep links back to the booking system; the hub never tries
  to re-do seat reservations, capacity or waitlists.
- **People decide identity, not algorithms.** "Same person, two emails" cases are
  resolved by a human or by the attendee via a chaser — never auto-merged.
- **An attendee browser for organizers.** A clean, read-only view with tiles,
  search, filters and CSV export (correctly handling accented names). Corrections
  happen at the source system, keeping data trustworthy.
- **A "My Event" dashboard for attendees (2026-06-14).** Every attendee gets one
  mobile-first home that pulls together what they need: a live countdown to the
  event (or a "Happening now" badge during it), their Master Class status at a
  glance (reserved / not booked / double-booked, with a deep-link out to manage
  the booking), and the practical info — pre-day and conference dates, plus the
  venue as a one-tap map link.
- **Self check-in — "I'm here" (2026-06-14).** On the event days, a ticket-holding
  attendee can tap one button on the My Event dashboard to check themselves in;
  the time is recorded and shown back to them. It is self-service and idempotent
  (tapping again does nothing), opens only for ticket holders during the event
  window, and never re-implements turnstiles or badge scanning — it is a
  lightweight presence signal the attendee owns.
- **A personal agenda on "My Event" (2026-06-16).** The same My Event home now also
  pulls together each attendee's programme: a **My sessions** card showing the
  session they reserved (their Master Class), the **full agenda** for the edition
  with their own session clearly highlighted, and one-tap **quick links** to their
  hotel, swag and lunch forms plus the public agenda. Every session lists its time,
  room and speaker(s) and links straight to its details, to **ask the speaker a
  question** before the event, and to **rate the session** afterwards. It is a
  read-only, mobile-first view in English and Danish — booking still happens at the
  source, the hub just gives attendees one tidy place to see their whole day.
- **Build your own "My plan" of talks to attend (✅ 2026-06-17).** A new **My plan**
  page lets each attendee curate their own running order: browse the programme and tap
  **Save** to add the talks they want to attend, or **Remove** to take one out. Saved
  talks appear together at the top — the scheduled ones in time order, with any
  not-yet-timed talks listed after so nothing is forgotten — each showing its time,
  room and speaker(s) and linking straight to the session details. It is a private,
  personal bookmark list that is **only ever visible to that attendee** and **never
  books a seat** (booking stays in the booking system) — it simply remembers the
  sessions they care about. Saving the same talk twice does nothing, and a talk that
  gets removed from the programme quietly drops off the plan. Phone-first, in English
  and Danish.
- **Add your whole plan to your calendar (✅ 2026-06-17).** From the My plan page an
  attendee can **download their saved talks as a calendar file (.ics)** and open it in
  Outlook, Google or Apple Calendar — one entry per saved talk that has a confirmed
  time, with the room, the speaker(s) and a link back to the session details, plus a
  reminder shortly before each talk starts. Re-download any time: your calendar
  **updates in place** rather than creating duplicates, and a talk you remove from your
  plan drops off your calendar on the next download. The file is **only ever your own**
  plan, and when none of your saved talks has a confirmed time yet the page simply tells
  you there's nothing to add to a calendar instead of offering an empty file.
  Phone-first, in English and Danish.
- **A focused attendee menu.** *(✅ 2026-06-21)* An attendee now sees a minimal,
  uncluttered menu built around just what they need — **Home, Master Class, My plan**
  and **Waitlist** — instead of a long mixed list, so the things an attendee actually
  uses are one tap away. Mobile-first, English and Danish.
- **Choose your Master Class right in the hub.** *(✅ 2026-06-21)* The external booking
  deep-link is replaced by an **in-hub Master Class chooser**: an attendee can **sign
  up** for a master class, **give up a seat** they no longer want, download the session
  **`.ics`**, and toggle a **reminder** — all without leaving the hub. The hub card and
  the waitlist also carry short **orientation guidance** so a first-timer knows what a
  Master Class is and how seats work. Phone-first, in English and Danish.
- **Master Class waitlist with auto-email on a seat opening.** *(✅ 2026-06-21)* When a
  master class is full, an attendee can join its **waitlist**; the moment a seat frees up
  (someone gives up their place), the next person on the list is **emailed automatically**
  so they can claim it. Mobile-first, in English and Danish.

## 10. Email & notifications — on-brand, controllable, safe

- **Reliable, branded email delivery.** All mail is sent through a professional
  relay from your event sender address.
- **One branded template engine.** Every email uses a shared branded shell with
  per-type content and simple token substitution; templates are built to render
  correctly across email clients, including Outlook.
- **A library of ready templates.** Welcome notes, reminders, chasers, app-game and
  broadcast messages all come as branded templates.
- **Every email is on-brand — including invitations, task nudges and payout
  confirmations.** Sign-in invitations, manual task reminders and travel-reimbursement
  confirmations now use the same branded shell as the rest, so all mail looks
  consistent and adapts automatically to each community's branding. *(✅ 2026-06-14)*
- **A one-tap welcome email for every role.** *(✅ 2026-06-15)* Send everyone a warm,
  mobile-first welcome that introduces the Event Hub as the one place for their part
  in the event, with a single button — **"Open my Event Hub — signs you in
  automatically"** — that signs the person in and drops them straight into their own
  role hub, no password and no code to type. Each role (organizer, speaker, Master
  Class speaker, volunteer, sponsor, attendee, video and photography crew) gets its
  own line about what their hub is for, the email explains how the Hub sits alongside
  the public Zoho Backstage site (Backstage = the public site, schedule and tickets;
  the Hub = its behind-the-scenes self-service companion), and it sets the tone that
  the Hub is brand-new this year so a friendly "reply if anything breaks" invites
  feedback. It ships as both a designed HTML email and a plain-text version. The
  sign-in link is a genuine secure auto-login link (not just a pre-filled address),
  and the welcome is **available in the development environment only** while the Hub
  is being shaken out, with every test send safely redirected to the team's test
  inbox; it can be re-sent as often as needed and the team can see who has received
  it.
- **An Email Center for organizers.** Preview any template safely, send a one-click
  test to yourself, and watch a delivery pulse with a filterable history of what's
  been sent.
- **Send a test copy to any address.** *(✅ 2026-06-17)* Beyond the "send to me" test, an
  organizer can type **any mailbox** — a colleague's, a role test account — and send the
  selected template there to confirm it really arrives. The hub is honest about the outcome
  before it tries: if the address isn't on the safety allowlist it tells you it would be
  **dropped** (so you're never left waiting for mail that will never come), and in the test
  environment it tells you when the copy is **redirected** to the shared test inbox. A typo or
  malformed address is caught up front. Mobile-first and bilingual (English / Dansk).
- **Broadcast to exactly the people you mean.** *(✅ 2026-06-15)* Send one personalized
  message individually (branded layout, personal "Hi {FirstName}") to a precisely chosen
  audience. **Filter the audience** by role group (speaker / sponsor / volunteer / organizer
  / attendee and the crew roles), by **status** (active only, inactive only, or both), and
  with a one-tick **"exclude test users"** safeguard (on by default) so a real broadcast
  never reaches the synthetic go-live test cast; attendees reconciled from Zoho can be
  included too. Before anything is sent you see the **recipient count and the actual filtered
  list** (email, first name, group) — what you preview is exactly what is sent. **Start from
  a reusable template** — *blank*, *generic announcement*, *friendly reminder*, or
  *welcome / introduction* — then edit it freely; simple **{FirstName} / {EventName} tokens**
  are filled in per recipient. An inline **"variables you can use" legend** spells out each token
  with a plain-language meaning and an example, and a tap drops the token straight into your subject
  or message — so you never have to remember the exact spelling *(✅ 2026-06-18)*. Sending stays
  resilient (a single bad address never stops the batch) and resume-safe (re-sending the same
  subject only reaches people who have not yet received it). Mobile-first and screen-reader friendly.
- **Per-persona onboarding emails that send themselves.** *(✅ 2026-06-15)* Each crew group
  (volunteer / speaker / media-team / sponsor / organizer) has its own short **set of getting-started
  emails**. The moment an organizer **activates** someone, that person automatically receives their
  group's onboarding emails — **no approval, no extra click**. It is safe to activate the same people
  twice: nobody is ever emailed the same onboarding message a second time.
- **Re-send any email to one person, on demand.** *(✅ 2026-06-15)* From the Email Center, pick a person
  and a template and send it again — useful when someone says "I never got it". You can set that person's
  **secondary email** at the same time so the copy lands where they want.
- **A complete email log.** *(✅ 2026-06-15)* Every email the hub sends — welcome, sign-in codes,
  reminders, broadcasts, onboarding and manual re-sends — is recorded. Organizers get a log view that
  shows **all** emails and **per person**, **filterable by name or email**, with the subject, category,
  the address it went to (and any CC), and whether it succeeded. Nothing is sent off the books.
- **Re-send a failed email straight from the log.** *(✅ 2026-06-18)* When a logged email
  shows **Failed**, organizers get a one-click **Re-send** button right on that row — it
  retries the exact same template to the exact same person, with their secondary-email CC
  and the safety allowlist applied just like the original. The retry is itself logged, so
  you can see whether the second attempt went through. Only failed emails that targeted a
  known person are re-sendable; a broadcast or a sign-in code (which has no template/person
  on record) shows no button and is honestly explained, never a fake success. English and
  Danish.
- **A secondary email per person (optional CC).** *(✅ 2026-06-15)* Anyone can add an **extra address**
  that gets **copied on every email** to them — a colleague's inbox, a shared team alias or a personal
  backup. It is purely additive: the main message still goes to the person's primary (or, for speakers,
  their preferred) address, with the secondary CC'd on top. Add it during onboarding (organizer) or later
  in your own hub profile; mobile-first and validated.
- **Scheduled task reminders, per group.** *(✅ 2026-06-15)* The daily deadline reminders now carry the
  person's group, so reminders to volunteers, speakers, media crew, sponsors and organizers are tracked
  per persona in the log. As before, reminders fire at 14 / 7 / 3 / 1 days before a deadline and are never
  sent twice.
- **"Complete this step" emails when a step is re-opened.** *(✅ 2026-06-15)* When an organizer re-opens
  someone's onboarding step, the hub emails that person a friendly note pointing them straight at the
  wizard to finish it — automatically on the nightly run, or instantly via a **"send now"** button on the
  Action Queue. Each re-opened step is chased exactly once.
- **Names and titles can never break a branded email.** *(✅ 2026-06-18)* A person, company or task name
  that happens to contain characters with special meaning in web pages — an ampersand, an angle bracket or
  a quote (e.g. *"Tom & Jerry"*, *"R&D <invoice>"*) — is now always rendered as plain readable text inside
  every branded email, never as broken or unsafe markup. Previously some emails handled this and others did
  not, so the same name could look fine in one mail and garbled in another. There is now **one consistent
  rule applied in a single place**, so every current and future email is safe by default. The email's
  subject line still reads exactly as typed. This is a behind-the-scenes correctness and safety fix; nothing
  changes in how organizers compose or send mail.

## 11. Organizer hub — run the whole event from one place

| Command center | Live dashboard |
|---|---|
| [![Organizer command center](img/organizer-command-center.png)](img/organizer-command-center.png) | [![Organizer dashboard](img/organizer-dashboard.png)](img/organizer-dashboard.png) |

*"Is the event on track, what do I do next?" — the command center triages the whole event with every number a link into the matching list; the live dashboard shows form completion, participants by role, tasks, sponsor and volunteer coverage at a glance.*

- **A clearer menu that fits your role.** *(✅ 2026-06-15)* The top menu is now two
  clearly separated groups instead of one long mixed list. Everyone sees a tidy
  **"My event"** bar — Home, My profile, My tasks, Resources, and just the forms that
  apply to them (hotel, dinner, lunch, swag, travel, volunteer shifts, the speaker
  area). Organizers additionally get a single **"Organizer area"** dropdown that
  gathers every management tool in one place — dashboards, participants, the
  pre-selection queue, onboarding, the action queue, email/broadcast, speakers and
  sessions, Sessionize, sponsors, social graphics, volunteer structure and the
  acting-as log. A regular attendee, speaker, volunteer or sponsor never sees the
  management tools at all. The dropdown collapses to one tap on a phone and is fully
  keyboard- and screen-reader-friendly.
- **The organizer menu is grouped, not a flat wall of links.** *(✅ 2026-06-16)*
  Inside the "Organizer area" dropdown the management tools are now arranged into a
  few clearly-labelled, collapsible sections so you can scan straight to what you
  need: the three things you reach for most — **Organizer home, Command center and
  Dashboard** — sit at the top, and everything else is folded under **People**,
  **Sessions**, **Comms**, **Sponsors**, **Volunteers** and **Logistics**. Open a
  section to reveal its tools; the section that contains the page you're on opens
  automatically. Nothing was moved out of reach — every tool that was in the menu is
  still there, just sorted into its group. Works the same on a phone, in English and
  Danish, with full keyboard and screen-reader support and the current page clearly
  marked.
- **A lean hub-level menu with button-grid hub pages.** *(✅ 2026-06-21)* The top menu
  is now leaner, pointing at a small set of **hubs**; each hub page lays its tools out
  as a **button grid**, so **every admin page is reachable as a tile** on its hub
  rather than buried in a long dropdown. Mobile-first, in English and Danish.
- **Retired the dead Sponsor Portal page.** *(✅ 2026-06-21)* The unused organizer
  Sponsor Portal page was removed, so the back-office no longer carries a dead link.
- **A cross-role event overview.** One read-only page that answers "where does
  the whole event stand?" in a glance: participation counts by role
  (organizer / speaker / sponsor / volunteer / attendee), task completion split
  per role and per category, speaker milestone-deadline progress (how many
  speakers cleared each deadline), volunteer coverage (assigned vs. still-open
  tasks across the volunteer work tree), sponsor task and lead totals, and
  attendee check-in numbers — topped by "needs attention" tiles for overdue
  tasks, unassigned volunteer tasks, open help requests and pending volunteer
  applications. Pure aggregation over existing data: it changes nothing.
  Mobile-first and screen-reader friendly. *(✅ 2026-06-15)* The "needs attention"
  tiles are now **clickable** — each opens the matching list already filtered to
  exactly the rows it counts (e.g. the overdue-tasks tile lands on the open tasks
  sorted by due date, pending volunteers on the inactive volunteers), so a number
  that needs action is one tap from the work. *(✅ 2026-06-18)*
- **Download "who hasn't onboarded yet" as a chase-list.** *(✅ 2026-06-17)* The
  onboarding dashboard now has a one-click **CSV export** of everyone still working
  through onboarding — anyone who hasn't finished every step their role needs. Each
  row carries the person's name and email, where they are (pre-selected / invited /
  in-progress), how far along they are, and — most usefully — **exactly which steps
  they're still missing** (bio, photo, hotel, appreciation, swag), so you can follow
  up with the right ask. The export honours the dashboard's **persona filter** (export
  just the speakers, just the volunteers, …) and opens cleanly in Excel, with Danish
  names intact. Read-only — it never changes anyone's data. Mobile-first, in English
  and Danish.
- **Re-open one onboarding step for a whole group at once.** *(✅ 2026-06-17)* When
  something changes for everyone — a hotel-booking deadline moves, the swag order
  re-opens — you no longer have to re-open that step person by person. Filter the
  onboarding dashboard to a group (speakers, volunteers, …), pick the step, and
  **re-open it for everyone in that group who had already completed it** in a single
  action. Each affected person is automatically queued a reminder to do that step
  again, exactly like the per-person re-open. The action only touches people who
  actually finished the step and whose role needs it (nobody else is disturbed), it
  tells you honestly how many were re-opened (or that nobody had it done), and you
  confirm before it runs. Mobile-first, in English and Danish.
- **A command-center landing — "is the event on track, what do I do next?"**
  The prominent **Command center** in your menu opens one screen that triages the
  whole event for you: how many people registered (and how many are active),
  attendee numbers, **onboarding completion %** overall and per group (speakers,
  volunteers, sponsors, organizers — most-behind first), **hotel / swag / lunch /
  dinner headcounts**, how many **sessions** are scheduled vs. still need a slot,
  and **sponsor** status. At the top sits a prioritized **"what needs my
  attention"** call-out — overdue tasks, things due today, unassigned volunteer
  shifts, open help requests, people still waiting to be approved, attendee
  reconciliation mismatches, open action items and unscheduled sessions. Every
  number is a button that drops you straight into the matching list already
  filtered, so nothing is a dead end; when there is genuinely nothing to act on it
  says so ("all clear") instead of inventing a red badge. It also shows a literal
  **today's / overdue tasks** list. Read-only — it changes nothing — mobile-first,
  bilingual (English / Danish) and screen-reader friendly. *(✅ 2026-06-16)*
- **A comms cockpit — schedule, send and track all outreach in one place.**
  *(✅ 2026-06-16)* The **Comms** page is now a single cockpit for everything you
  send. One **timeline** brings together your email and your scheduled social posts
  — what already went out and what is going out next, newest first with the upcoming
  posts floated to the top. A **"who got what"** table shows, per person, the *real*
  outcome from the actual send log — **delivered**, **dropped** (held back by the
  safety allowlist) or **failed** — never an optimistic "we tried" tally; a
  matching **by-campaign** view rolls the same honest outcome up per kind of message
  (welcome, onboarding, reminders, broadcast…). When a message didn't reach
  someone, the **resend** panel lists exactly those people and lets you send it
  again in one click (using the same trustworthy per-person send the Email Center
  uses — and the resend then shows up on the very same cockpit). It also surfaces
  the **next things going out**. All of it is a read-only view over data you already
  have; the cockpit still links out to the full Email Center, Email Log, Broadcast,
  invitations, welcome, reminders and the social-post queue — it brings them
  together rather than replacing them. Mobile-first, English / Danish, screen-reader
  friendly (live status regions, captioned tables).
- **Exports & printable run-sheets — on-site operations on paper.** *(✅ 2026-06-16)*
  The day of the event still runs on offline artifacts, so an **"Exports & run-sheets"**
  page (under Logistics) gives you both **downloadable CSV files** and **print-friendly
  run-sheets** for the five lists you carry to the floor: the **attendee list**, the
  **lunch headcount** (per pre-conference day, plus who eats which day), **room &
  session sheets** (the running order per room, with each session's room-QR link and
  speakers), the **volunteer rota** (who works which task, where and when, from the
  volunteer plan), and **badge data** (each active person's name, role and company —
  the fields a badge printer or mail-merge needs). Hit **Download CSV** on any list, or
  use your browser's **Print** to save a tidy PDF — the page switches to a clean,
  ink-friendly layout (buttons and menus hidden) when printed. Everything is a
  read-only view of data you already have inside your own edition; it never changes
  anything and is **not** an event check-in tool (that lives in your ticketing system).
  Mobile-first, English / Danish, and screen-reader friendly (captioned tables,
  labelled download buttons).
- **A live dashboard.** See form completion, participants by role, tasks and
  overdues, sponsor completion, attendee mismatches and volunteer coverage at a
  glance, with live pipeline cards for leads and event prep.
- **Practical data grids.** Work through participants and hotel bookings (with inline
  active and check-in/out toggles and filters) and tasks (inline edit), each with
  CSV export.
- **Find a person fast — search, filter and sort everyone.** *(✅ 2026-06-16)* The
  most frequent organizer action made instant. A dedicated **"Find a person"** box
  (prominent in your menu) searches every participant in the edition by **name or
  email** and lists the matches with a one-tap link straight to that person — handy
  when someone walks up at the desk and you just need to pull them up. The full
  **Participants** grid carries the same power for working through a list: free-text
  search on name + email, filter by **status** (active / inactive / everyone — where
  "active" means exactly who can sign in), by **persona/role** and by **sponsor
  company**, and **sort** by name, email, persona or status (click a column to
  reverse). Everything runs on the server inside your own event, so a long
  participant list stays fast and the results always match who can actually log in.
  Mobile-first, English / Danish, and screen-reader friendly (labelled controls, a
  live result count, and a captioned results table).
- **Bulk participant operations.** Tick several people on the Participants grid and
  deactivate, reactivate, or change their role in one action instead of one row at a
  time. Every bulk action stays inside your own event, is safe to re-run (already-in-state
  rows are skipped, not double-applied), and reports exactly how many actually changed.
- **Bulk session clean-up.** *(✅ 2026-06-16)* Tick several sessions on the Sessions
  grid and **delete them in one action** instead of one row at a time — ideal for
  clearing duplicates after an import. The same safety as the single-row delete applies
  to every row: a session that has **attendee data** (questions, evaluations or
  master-class bookings) is **protected** — it can't even be ticked, and the result
  banner tells you how many were kept. Clean sessions are removed together with their
  speaker links; if any deleted session came from Sessionize you're reminded a
  re-import will bring it back. A confirm dialog shows the live count before anything
  is deleted, and the whole batch commits together. Mobile-first, English / Danish,
  screen-reader friendly.
- **Bulk volunteer-task actions.** *(✅ 2026-06-16)* When building the volunteer work
  structure (Category → Subcategory → Task), tick **any tasks across the whole tree**
  and either **set their status** (Open / In progress / Done / Cancelled) or **delete**
  them in one action — so an organizer planning a rota of dozens of tasks doesn't edit
  one row at a time. Status changes are safe to re-run (a task already in that status is
  skipped) and report exactly how many changed; a bulk delete removes the clean tasks
  with their volunteer assignments, while any task that already has **help-request
  history** is **kept** (so coordination history is never lost) and counted in the
  result. A confirm dialog shows the live count, and the batch commits together.
  Mobile-first, English / Danish, accessible.
- **Act as a participant — see and do their part.** *(✅ 2026-06-15)* From the
  Participants grid an organizer can **"Switch to user"** to **switch INTO** that
  person — they land on the user's **own hub home** and navigate the **whole app**
  exactly as that user sees it (their tasks, forms and every page), and act on their
  behalf (complete a task, submit a form). It is full impersonation, not a limited
  form: the switch lands on the user's My-Event view, **never** the small
  "Modify on behalf" quick-edit page. It is unmistakably an organizer-acting-as
  session: a banner across the top names who is being helped, and **"Return to
  organizer"** drops the organizer straight back to their own session. Only real
  organizers can start it, an acting-as session can never start another one, and
  every switch, return and on-behalf change is written to an **acting-as audit log**
  the team can review.
- **Filter the grid by persona and sponsor company.** *(✅ 2026-06-15)* Alongside the
  active/inactive filter, narrow the grid by **persona** (organizer, speaker, sponsor,
  volunteer, attendee, crew) and, for sponsors, by **sponsor company** (shown with the
  company's public name), so finding the right group in a large cast is quick.
- **Active by default, with a show-inactive toggle.** *(✅ 2026-06-15)* The grid shows
  **active** participants by default — meaning people who can actually sign in (activated
  and not withdrawn) — with a toggle to show inactive or everyone. The status badge
  reflects that same combined rule, so "Active" on the grid always means "can sign in".
- **Modify a person's logistics on their behalf.** *(✅ 2026-06-15)* A lighter,
  explicit alternative to switching into the user: from the grid the **"Modify on
  behalf"** quick-edit lets an organizer change a couple of practical things **for**
  someone — whether they need a hotel room, their polo size — **without** leaving the
  organizer seat. The change is written to the very same record the person sees, so it
  **shows up on their own view** immediately, and a late change still raises the usual
  action-queue item so nothing slips past the team. (For anything beyond these fields,
  use **Switch to user** to act as them across the whole app.)
- **Cancel a participant.** *(✅ 2026-06-15)* Remove someone from the event like a
  cancellation: they become inactive, drop out of the active views, and can no longer
  sign in — reversible at any time.
- **Secure links — let an assistant fill things in.** *(✅ 2026-06-15)* For a VP or
  speaker who has someone handle their admin, an organizer can issue a **secure
  link**. The link signs the assistant in **scoped to just that one person**,
  to fill in their onboarding and tasks on their behalf — nothing else. Each link is
  **time-bound** (you choose how many days), **revocable** in one click, and limited to
  the single participant it was issued for; the link holder can never reach organizer areas
  or act as anyone else. Every use is recorded in the acting-as audit log.
- **Hotel management.** Export the rooming list, import confirmation IDs, send
  updated calendar invites, and track it all on a dashboard.
- **Group photos.** Register a company and contact, schedule a slot, and send
  calendar invites that update cleanly rather than duplicating.
- **App game.** Register a sponsor's gift and send the gift reminder.
- **Travel reimbursements.** See all claims, register payouts, and send confirmation
  emails.
- **Swag ordering.** Produce a multi-sheet vendor spreadsheet for polo, award and
  jacket orders.
- **Lunch and dinner overviews.** See pre- and main-day lunch numbers and the
  appreciation-dinner list with allergies.
- **A sponsor admin area.** Manage the sponsor task catalog, run the leads pipeline
  (issue, rotate or revoke keys, set notification preferences, action leads), and
  watch a sponsor status dashboard sorted overdue-first.
- **An action queue for late changes.** When a participant edits an
  already-submitted hotel booking or dinner RSVP close to the lock date — exactly
  the changes that may contradict what was already sent to the hotel or caterer —
  the hub surfaces it as an action item for organizers. The queue groups items by
  type with live open counts, lets you mark each resolved with a note (or re-open
  it), and exports the open list to CSV. Early edits stay quiet, so the queue
  shows only what genuinely needs a human to re-confirm. The organizer dashboard
  card carries the open-item badge. *(✅ 2026-06-14)*
- **Built-in safety on file handling.** Organizer tools that read files are guarded
  against path-traversal.
- **Find anyone fast on the big grids — search, sort, pages and safe bulk.** *(✅ 2026-06-15)*
  The high-traffic organizer grids — **Participants, Speakers and Attendees** — now carry
  free-text **search** (by name / email), clickable **column sorting** (with a clear ▲/▼
  direction and screen-reader `aria-sort`), and **pagination** so a long list (Attendees can
  run to many pages) loads one page at a time instead of everything at once. All of it runs
  **server-side** — the database does the filtering, ordering and paging, so the page stays
  fast no matter how many people are registered. Where a grid already supports bulk actions
  (deactivate / reactivate / change persona on Participants; set pre-day / main-day on
  Speakers) those now go through a **confirmation dialog that states how many rows you
  selected** before anything is applied — no accidental mass change on a stray click. Mobile-first,
  keyboard-accessible, and fully English / Danish. Attendees stays read-only by design (it is
  reconciled from the source systems), so it gets search/sort/paging but no bulk writes.
- **Same fast search, sort and pages on the Sponsors, Leads and Sessions grids — plus a one-click
  sponsor drill-down.** *(✅ 2026-06-16)*
  The remaining back-office grids now match the rest: **Sessions** (search by title, room or
  speaker; sort by title / type / length / room), **Sponsor leads** (search by name, email or
  sponsor; sort by captured date, sponsor, name or status; defaults to newest first; the
  show-ignored/junk toggle is preserved across every action), and the **Sponsors** company roster
  (search by company or contact; sort by company, contacts, open / done / overdue / total tasks or
  next due) — all filtered, ordered and paged **server-side** so they stay fast as the event grows,
  with a clear "showing X–Y of Z" count and ▲/▼ + `aria-sort` headers. Each Sponsors company row is
  now a **link straight to that company's people** on the Participants grid (its sponsor contacts,
  pre-filtered), so you can jump from "who is this sponsor" to "show me their contacts" in one click.
  Mobile-first, keyboard-accessible, English / Danish.
- **A data-freshness panel — see at a glance whether every sync is still running.** *(✅ 2026-06-17)*
  A new **Data freshness** page (under the Logistics tools) answers a question that is otherwise easy
  to miss until it bites: *is each data source still being fed, or has a sync quietly stopped?* It
  lists each major source — outbound email, attendee sync, master-class bookings, sponsor leads,
  speaker and session imports, attendee questions, session ratings and social-media posts — and for
  each shows **when it last produced data**, **how long ago that was**, and a clear state: **up to
  date**, **looks stale** (it has gone quieter than expected for that source, so it is worth a check),
  or **no data yet** (a brand-new edition does not light up red). A summary banner at the top tells you
  in one line whether everything is current or how many sources look stale. It is read-only — it
  reports what already happened and never sends or changes anything. Mobile-first, keyboard-accessible,
  English / Danish.
- **Breadcrumbs — always know where you are in the back-office.** *(✅ 2026-06-18)* The organizer area
  is a deep set of hubs and feature pages reached through one compact menu, which made it easy to lose
  your bearings on a deep page. Every back-office page now shows a small **breadcrumb trail** at the top
  — for example **Organizer area / People / Participants** — where each step before the current page is
  a one-click link back to its hub and to the organizer home. It appears automatically on every
  organizer page (and stays out of the way everywhere else), reads correctly to screen readers (a
  navigation landmark with the current page marked as such), wraps cleanly on a phone, and is shown in
  English and Danish.
- **Unsaved-changes guard on the participant editor.** *(✅ 2026-06-18)* If you start editing a
  participant and then try to leave the page without saving, the browser now asks you to confirm — so a
  stray click or back-button never silently loses your edits. Saving (or pressing Cancel) leaves
  normally with no prompt, and the **Save** button shows a busy state and locks while the save is in
  flight so a double-click can't post twice.
- **Loading skeletons on the big grids — the portal never looks frozen.** *(✅ 2026-06-18)* The
  high-volume organizer grids (**Participants, Speakers, Attendees, Sessions, Sponsors**) re-load the
  whole page when you search, change a filter, sort a column or page through results, which left a brief
  moment where nothing seemed to happen. Now, the instant you trigger one of those, the grid dims its
  current rows and shows an animated **loading skeleton** over them while the next page comes back, and
  screen-reader users hear a polite "Loading…" announcement. It clears by itself when the new page
  arrives (and when you press the browser Back button). It honours the "reduce motion" accessibility
  setting (you still get the dimmed "busy" look, just without the animation), works down to a narrow
  phone screen, and is shown in English and Danish. A CSV download link is deliberately excluded so a
  file download doesn't flash the skeleton.

## 12. Hosting & reliability — production-grade by design

- **Defined entirely as code.** The full environment (database, web app, scheduled
  jobs, storage, secret vault, logging and monitoring) is described as code for
  consistent, repeatable deployments.
- **Scheduled jobs that just work.** Background jobs handle reminders, order pulls,
  attendee reconciliation, portal sync, sponsor-lead delivery and upload-change
  watching on their own schedules, each individually switchable.
- **Scripted, safe deploys with rollback.** Releases build a versioned artifact,
  deploy, and health-check; a one-command rollback is always available.
- **Zero-downtime production releases.** Production deploys go to a staging slot,
  warm up, then swap in — so visitors never see downtime.
- **Separate environments, shared upstreams.** Development and production are fully
  separated, while shared upstream services are reused across both.
- **Resilient on a budget.** The platform absorbs database cold-starts gracefully
  and runs happily on cost-efficient, auto-pausing infrastructure.
- **Schema managed as code, kept in sync.** Database structure is versioned and
  applied in a controlled way, and every release keeps the development and production
  schemas in step (the same versioned changes apply to both, with a per-release note).
- **Dev mirrors prod.** Development holds the same data as production so it is a true
  rehearsal — the one deliberate difference is that all email in development is safely
  redirected to a single test address.
- **Test users stay separate from real ones.** Synthetic test accounts are tagged so
  they can live alongside real participants without ever skewing real counts, exports
  or dashboards.
- **A safe public-mirror workflow.** Publishing to the public template runs through a
  controlled, allow-listed process with a dry-run pre-flight, so only intended
  content is ever made public.
- **Strong delivery governance.** Protected branches, required reviews, automated
  checks that scan for secrets, and a consistent commit convention keep the codebase
  clean and safe.
- **Custom domains per environment.** Each environment binds its own verified custom
  domain with a managed certificate.

## 13. Accessibility — usable by keyboard and screen reader *(✅ 2026-06-15)*

The participant-facing pages had their first accessibility pass, targeting **WCAG 2.1
AA**. Nothing about the look changed for mouse users; the work is markup, ARIA and CSS
only (no database or data-model change).

- **Correct page language.** The page now declares its real language (English) so a
  screen reader pronounces the copy correctly. The Danish-formatted date picker
  (dd/mm/yyyy, Monday-first) is unchanged.
- **Skip to main content.** A keyboard user can jump straight past the header and
  navigation to the page body with the first Tab, instead of tabbing through every
  menu item on every page.
- **Visible keyboard focus everywhere.** Every link, button and field shows a clear
  focus ring when reached by keyboard, so it is always obvious where you are — without
  drawing a ring on ordinary mouse clicks.
- **A real navigation landmark with "you are here".** The primary menu is announced as
  navigation, and the current page is marked so assistive tech (and sighted users) can
  tell which section they are in.
- **Forms that announce themselves properly.** Grouped choices (RSVP, speaking days,
  lunch days, swag extras, shifts, "do you need a hotel/travel") are now proper
  fieldsets with a group label; every read-only field has an associated label; and
  success / error messages are announced the moment they appear.
- **An accessible survey wizard.** The public 3-step topic survey announces its current
  step, its "pick one/two/three more" counter updates live, and the rank (1st / 2nd /
  3rd) buttons report their pressed state — all keyboard operable.
- **Links that open elsewhere say so.** Links that open a new tab (event site, GitHub,
  template downloads) now tell screen-reader users they will leave the page.
- **Checked by automated tooling.** A new axe-core accessibility test suite scans the
  login, survey and per-role hub pages for WCAG A/AA violations (see TESTS.md), so
  regressions are caught.
- **Consistent, accessible feedback after every action.** A shared set of UX building
  blocks now gives the whole hub the same dependable behaviour:
  - **Clear "saved" / error messages.** After you submit a form, a tidy banner confirms
    the result ("✓ Saved") or explains an error. Success banners fade away on their own;
    errors stay until you have dealt with them, and both are announced to screen readers
    the moment they appear. There is always a Dismiss button.
  - **Helpful, to-the-point form errors.** When something is missing or wrong, the message
    appears **right next to the field** that needs fixing, plus a short summary at the top of
    the form — so you are never left guessing. As a first beneficiary, the **travel
    reimbursement** form now catches the case where you pick "Other" but forget to enter the
    amount (it used to save a blank claim silently); it now asks you to fill it in.
  - **A clear "are you sure?" step before big or irreversible actions.** Before something
    like sending a broadcast email, a confirmation dialog shows exactly **how many people**
    will be affected and what will happen, and is fully keyboard- and screen-reader-friendly
    (close with Esc, the backdrop, or Cancel). Sending a broadcast now shows the recipient
    count up front.
  - **An honest "did it actually work?" confirmation after every send and QR provisioning**
    *(✅ 2026-06-16)*. After an organizer sends a broadcast, emails session-evaluation results
    to speakers, or provisions a room QR code, the hub now tells them **truthfully** what
    happened — never an optimistic "done" when it wasn't. A real send confirms **"sent at
    &lt;time&gt; — N recipient(s)"**; a successful QR provisioning confirms **"done at
    &lt;time&gt;" plus the stored link**. If a send reached **nobody** (for example everyone was
    filtered out by the recipient-safety allowlist, or had already received it) or a QR couldn't
    be stored, that is shown as a distinct, clearly-not-a-success notice **explaining why** — and
    a real failure shows the reason. The confirmation is colour- and icon-coded (green success,
    blue "nothing happened", red error), announced to screen readers, and available in English
    and Danish.
  - **Small interaction cues that prevent confusion and accidental double-submits** *(✅ 2026-06-18)*.
    Four lightweight, accessible touches now work the same everywhere, with no setup:
    - **"Copied!" feedback.** The *Copy link* button on the subscribable-calendar cards (the hub
      home, the Speaker hub and the Volunteer schedule) now copies the link and briefly confirms
      **"Copied!"** on the button — announced to screen readers — instead of silently doing nothing.
      (This unifies three slightly different copy buttons onto one shared behaviour.)
    - **Live character counters.** Long free-text fields show a live **"used / limit"** counter as
      you type — turning amber as you approach the cap — so you are never surprised by a hard limit.
      It is on the public **ask-a-question** and **rate-this-session** boxes, the **volunteer
      application** note, the **speaker bio**, and the sponsor **lead-capture** notes.
    - **Submit feedback + double-click guard.** When you submit one of those forms, the button shows
      a busy **"Saving…/Sending…"** state and is locked while the post is in flight, so an
      impatient double-tap can never send your question, rating or application twice.
    - **Unsaved-changes guard.** If you start filling one of those forms and then try to leave the
      page without submitting, the browser asks you to confirm — so you don't lose what you typed.
    All four are progressive enhancements (everything still works with JavaScript off), mobile-first
    (~360px), and bilingual (English + Danish).

## 14. Bilingual UI — English and Danish *(✅ 2026-06-15)*

The participant-facing pages can now be shown in **English (default) or Danish**.
The work is markup + resource files only — **no database or data-model change**.

- **Pick your language anywhere.** A small **English / Dansk** switcher sits in the top
  bar of every page (including the anonymous sign-in page). Your choice is remembered in
  a cookie, so it sticks across pages and visits — and works inside the embedded event
  view too.
- **Follows your browser by default.** If you have never picked a language, the page
  honours your browser's preferred language (Danish browsers see Danish), otherwise it
  falls back to English.
- **The page language is now dynamic.** The `lang` the page declares to a screen reader
  switches with the chosen language (English → `en`, Danish → `da-DK`), so the
  accessibility work keeps announcing copy with the correct pronunciation. The
  Danish-formatted date picker (dd/mm/yyyy, Monday-first) is unchanged regardless.
- **Every participant page is now bilingual.** The first slice covered the highest-traffic
  journeys (**sign-in**, the shared **layout**, the **role hub** home page, **My tasks**,
  **My Event** check-in, the **Speaker hub**). The completion slice translated the rest of the
  participant surface so nothing is half-English anymore:
  - **First-run onboarding:** the one-time **Welcome** landing page and the mandatory
    **onboarding wizard** (verify bio, bio picture, hotel, appreciation, swag) — every step,
    label, and button is bilingual.
  - **Self-service forms:** Hotel preference, Appreciation Dinner RSVP, Lunch logistics, Swag
    preferences, Travel reimbursement, Speaker details, and the 3-step Volunteer sign-up wizard.
  - **Sponsor pages:** the booth **lead-capture** form (and its recent-leads list) and the
    **Sponsor tasks** page including the per-task action row (mark complete / reopen / add to
    calendar, the company-info upload form, and due-date badges).
  - **Attendee detail:** the Master Class ticket / booking page.
  - **Survey:** the full 3-step survey **wizard** (track → top-3 topics → level, including the
    dynamic in-page status messages and level pickers) and the live **Results** dashboard.
  - **Organizer navigation:** the organizer-area links in the top bar.
  - **Hub status cards:** the role sub-cards on the home page now report their "on file / not yet
    submitted" status in the chosen language.
  - Factual content that isn't UI chrome — fixed deadline dates, the organizer-team credits, and
    external/social links — stays as data in both languages.
- **Easy to extend.** Strings live in one shared resource file per language
  (`SharedResource.resx` for English, `SharedResource.da-DK.resx` for Danish); adding a
  language or translating more pages is a resource-file edit, not a code rewrite.

### Public Sessions list + Master Class logistics pages now bilingual *(✅ 2026-06-18)*

The last two **public, no-login** pages that still showed only English are now bilingual,
so an anonymous Danish visitor sees a fully Danish page (no schema or data change):

- **The Sessions list (`/Sessions`).** The heading, the filter bar (Type / Length / Room / Search
  labels, the placeholder and the "All types / Any length / All rooms" options), the "Showing N of M"
  count, the friendly empty states ("nothing published yet" vs "no match — clear the filters"), and
  every session card's **type / length / room tags**, the "With <speakers>" label and the "Master
  class info & setup" / "Ask the speaker a question" links all follow the chosen language.
- **The Master Class logistics page (`/MasterClass/{slug}`).** The not-found message, the hero
  "Master Class logistics & setup" / "Presented by …" meta, the "Before you arrive" section and its
  "nothing published yet" empty state, the "last updated" stamp, and the involved-speaker/organizer
  **edit logistics** affordance (heading, field label, placeholder, help note and Save button) are
  all bilingual.

### Signed-in Hub status badges now bilingual + accessible *(✅ 2026-06-18)*

The **Done / Pending status chips** on the signed-in Hub home (the role sub-cards for
hotel, dinner, volunteer shifts and volunteer work) were the last hardcoded-English
fragments on that page — the badge text ("Submitted" / "Pending") was baked into the
markup and never followed the chosen language. They now read from the shared
`Status.Done` / `Status.Pending` resource keys, so a Danish participant sees **Færdig /
Mangler**. The leading status glyph (✓ / ⚠) is marked decorative (`aria-hidden`) so a
screen reader announces only the localized word, and the chip styling moved from inline
styles into mobile-first `.hub-badge` CSS classes (the chip stays on one line and wraps
with its caption at ~360px). No schema or data change.

## 15. Social-media graphics & shared file store *(✅ 2026-06-15)*

The hub now produces **ready-to-share social graphics** for speakers and sponsors, keeps every
graphic and speaker picture in **one shared file store (SharePoint)**, and lets speakers share
their graphics in their own words — all with an organizer review step so nothing goes out before
it's approved.

- **Speaker graphics, generated for you.** From a template background plus the speaker's photo
  and name, the hub composes a polished **PNG** graphic. The image engine is cross-platform and
  needs no special server setup.
- **Approval before anything is visible.** A generated speaker (or per-session) graphic is **not
  shown to the speaker until an organizer reviews and releases it**. Organizers get a simple
  review queue: release to the speaker, or pull it back.
- **Organizers can swap in their own design.** An organizer can **replace** any generated graphic
  with their own artwork. The link to the graphic **stays exactly the same**, so anything already
  pointing at it keeps working — only the picture behind the link changes.
- **Sponsor graphics for your own posts.** The hub also builds sponsor graphics (template +
  sponsor logo) **for the organizers' own social-media use**. These are **internal only** and are
  **never shown in the sponsor's view**.
- **Speakers share in their own context.** On a **"My share graphics"** page, speakers see their
  released graphics and can **download the PNG** or open a **ready-to-edit draft on LinkedIn or
  X** — they review and post it themselves; the hub never posts on anyone's behalf.
- **"I'm speaking at &lt;event&gt;" button.** One click builds a **LinkedIn draft** with the event
  dates, the ticket link (the edition's public event URL) and the speaker's session — the speaker
  finalizes the wording and posts when ready.
- **Pictures are kept, not just linked.** When a speaker picture comes in (e.g. from the speaker
  import), the hub **downloads the image and stores it** in the shared file store, rather than
  relying on a link that might disappear.
- **One place to point each group.** An organizer **settings page** lets you configure, **per
  group** (volunteers, speakers, media, organizers), the **SharePoint location** where that
  group's details and files live.
- **Ready for your social-media calendar.** Approved graphics are exposed to the social-media post
  scheduler as ready-to-use branding — the right image plus a prefilled draft caption — so a scheduled
  post can pick up the correct, approved artwork automatically. Only **released** speaker/session graphics
  are offered (the approval step still applies), and sponsor graphics stay for your own internal posts.

*External connections (the SharePoint tenant/site and per-user LinkedIn/X posting) are set up by
the operator with their own credentials; until configured, the hub still generates graphics and
builds share drafts — it simply doesn't push anything to an external system.*

### LinkedIn company-page post scheduler *(✅ 2026-06-15)*

A **social-media calendar** for your event's LinkedIn **company page**: organizers schedule posts
for speakers and sponsors (and one-off announcements), and the hub publishes them automatically at
the right time — with a review-and-fine-tune step so every post is exactly right before it goes out.

- **A queue you curate.** Each post has a type (**Speaker**, **Sponsor**, or **Ad-hoc**), a
  scheduled date and time, post text, an optional branding image, and a tag set. See the whole
  calendar at a glance.
- **Auto-written, your-edit-wins.** The hub can **auto-write** a sensible post from the linked
  speaker or sponsor — or you turn auto off and write it yourself. Either way, **your manual edit
  always wins**, and a later refresh never overwrites your wording.
- **The right approved graphic, attached automatically.** An auto post pulls in the **approved
  branding graphic** for its speaker, session or sponsor (a session post prefers the per-session
  graphic). It only ever uses a graphic that has passed the **organizer release/approval step** — a
  graphic still awaiting approval is **not** attached; the post shows a clear **"awaiting approved
  graphic"** note and publishes as text-only until the graphic is released, so a post never carries a
  broken image. The moment the graphic is approved, a refresh attaches it. A branding image you set
  yourself is always kept, and turning auto off keeps your own static image.
- **Preview exactly what will publish.** A **Preview** button renders the post precisely as it will
  go out, including whether it will actually fire on its schedule.
- **Turn a post off without deleting it.** Each post has an **Active/Inactive** toggle — if a
  speaker drops out, flip the post inactive and it simply won't publish.
- **Ad-hoc posts.** Compose a one-off post (image + text + schedule) straight into the same queue.
- **Smart, compliant tagging.** Sponsor posts tag the **signer**, the **event coordinator** and the
  **sponsor company**. Speaker posts tag **organizers only** — LinkedIn doesn't allow tagging an
  external speaker from a company page, so instead…
- **5-minute speaker heads-up.** Five minutes before a speaker post publishes, a **designated
  organizer gets an email** so they can manually add the speaker's real LinkedIn handle.
- **Publish notifications.** Choose a list of organizers to be emailed whenever a post publishes —
  with a simple on/off toggle. Nothing is ever double-posted, and any failure is recorded, not lost.

*The connection to LinkedIn (which company page, and the access token) is set up by the operator
with their own credentials; until it's connected, the whole calendar — scheduling, tagging,
previews, pre-alerts and notifications — works in a safe, no-post mode that never sends anything to
LinkedIn.*

## 16. Feature settings — turn capabilities on when you're ready *(✅ 2026-06-17)*

The hub is built so that **nothing optional "just happens"**. The core experience — signing in,
searching, viewing and editing, and the public pages — is always on. Every optional integration and
piece of automation is **off until an organizer turns it on**, so you can switch things on one at a
time, test each, and never have a new release spring a surprise on a live event.

- **One Feature settings page.** A single organizer page (under **Setup → Feature settings**) lists
  every optional capability, grouped into clear chapters: **Email**, **Speakers and sessions**,
  **Sponsors and exhibitors**, **Social media**, **Surveys**, **Reminders and digests**, and
  **Attendees**. Each capability has a plain-language name and description and a simple
  **Enable / Disable** button.
- **Off by default, opt-in.** New optional capabilities start **switched off**. You decide when each
  one goes live for your edition — so a deploy that ships a new integration never starts running it
  automatically.
- **Release rings, with the ring shown.** *(✅ 2026-06-21)* Feature flags now default to
  **Ring 1**, and each flag shows its **release ring**, so an organizer can see at a glance
  which stage of rollout a capability is in.
- **Attendee welcome & auto sign-in (off by default).** *(✅ 2026-06-21)* A new flag,
  **"Attendee welcome & auto sign-in"**, enables **attendee auto-provisioning** plus a
  **one-click magic-link welcome** when a 2-day ticket is synced — so a new attendee can be
  set up and signed in from a single link. It ships **switched off** and only runs once an
  organizer turns it on.
- **Disabled features stay visible.** A turned-off capability is never hidden — it stays on the page,
  **dimmed with a small "Disabled" label**, so you always know it exists and can turn it on later.
- **Switches that really take effect everywhere.** Turning a capability off makes it genuinely inert:
  the background jobs and sync services check the switch and **do nothing** when it's off — no
  imports, no posts, no digests, no sends. It's not just hidden in the screen; the work actually
  stops. This now covers **every** optional sync — Sessionize speaker/session import, the sponsor
  order pull, the sponsor leads pipeline, the sponsor upload watcher, the Backstage/Zoho exhibitor
  sync, the social-media scheduler, and the attendee reconciliation — **on both the scheduled run AND
  the matching "do it now" button** in the screen, so the toggle you see always equals what actually
  happens. A turned-off "do it now" button responds with a clear *"turned off — enable it in
  Settings"* message instead of silently doing nothing.
- **Prerequisite hints.** Some capabilities build on another (for example, digest emails need the
  reminder job). The page shows what each one needs and **warns** if you've enabled something whose
  prerequisite is still off, so you're never left wondering why nothing happens.
- **Email controls, front and centre.** A global **email master switch** turns **all** outbound email
  off instantly — sign-in codes, welcomes, reminders and digests — across the whole hub. Alongside it
  the page shows the **safe-recipient allowlist** and any **test redirect** in force, so it's always
  clear who can and can't receive mail. The hub never mails anyone outside the allowlist, and with the
  master switch off it sends to nobody at all.
- **Per-edition.** Every switch is set per edition, so a new edition starts clean and you tailor it to
  that event.
- **Bilingual & mobile-first.** The whole page is available in English and Danish and works on a phone.
