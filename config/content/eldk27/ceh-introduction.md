# Introduction to the Community Event Hub solution

<!-- §326ag/§326aj (operator 2026-07-25): an all-roles Event Info page. The operator's
     audience is "Azure nerds", so the architecture, security and engineering sections are
     deliberately technical, with inline SVG diagrams (no JS, renders anywhere).
     PUBLIC-SAFE: technologies and patterns only — no resource names, tenant/subscription
     ids, hostnames, key-vault names, app ids or anything that helps an attacker. -->

The **Community Event Hub (CEH)** is the app you are using right now. It is built and run by
the community itself, on Azure, and shared as open source so other communities can run their
event on it. This page covers what it is, **how it is architected**, **how it is secured**,
and **what each role gets**.

<!-- DEFAULT CONTENT shipped with the public template. It describes the product, not an event:
     adapt it (or keep it) for your community. -->

<p style="margin:18px 0;">
  <a href="https://github.com/KnudsenMorten/community-event-hub"
     target="_blank" rel="noopener noreferrer"
     style="display:inline-block;background:#1b2a63;color:#fff;padding:12px 22px;border-radius:8px;
            text-decoration:none;font-size:16px;font-weight:600;line-height:1.3;">
    📖 Read the complete documentation on GitHub →
  </a>
</p>

*This page is the summary. The full documentation — every feature, the architecture, and how to run
CEH for your own community — lives in the open-source repository, and opens in a new tab.*

## Quick links

**Just want to get on with something?** These are the pages people actually come here for.

| I want to… | Go to |
| --- | --- |
| **See what the hub does for me** | [3. Features](#3-features--what-the-hub-actually-does) |
| **Sign in, or work out how sign-in works** | [4. How it works for you](#4-how-it-works-for-you) |
| **Find my open tasks and deadlines** | [Tasks and reminders](#3-features--what-the-hub-actually-does) |
| **Understand how it is built** | [5. Solution architecture](#5-solution-architecture) |
<!-- internal-only:start -->
| **Check how my data is protected** | [7. Security](#7-security) |
<!-- internal-only:end -->
| **Run this for my own community** | [9. Who built it](#9-who-built-it) |

## Contents

*Click any section to jump straight to it.*


**Start here**

1. [What is CEH?](#1-what-is-ceh) — the idea, and the problems it removes
2. [CEH by the numbers](#2-ceh-by-the-numbers) — code, tests, services, jobs, features and roles, counted from the source
3. [**Features — what the hub actually does**](#3-features--what-the-hub-actually-does) — the full catalogue, with screenshots, grouped by who it is for
   - [Shared by every role](#shared-by-every-role)
   - [Speaker](#speaker) · [Sponsor and exhibitor](#sponsor-and-exhibitor) · [Volunteer](#volunteer)
   - [Attendee](#attendee) · [Media and event partner](#media-and-event-partner) · [Organizer](#organizer)
4. [How it works for you](#4-how-it-works-for-you) — sign-in, your hub, tasks, reminders

**Architecture & Security**

5. [Solution architecture](#5-solution-architecture) — Azure components, runtime shape, data model
6. [Integration layer and field mappers](#6-integration-layer-and-field-mappers) — the generic mapper engine and change management
<!-- internal-only:start -->
7. [Security](#7-security) — identity, authorization, secrets, data protection, e-mail safety, audit
<!-- internal-only:end -->
8. [How it is built and released](#8-how-it-is-built-and-released) — evergreen design, tests, zero-downtime deploys, rollout rings
9. [Who built it](#9-who-built-it) — credits and the open-source project


> **How to read this page.** Sections 1–4 are for **everyone**. Sections 5–8 are deliberately
> technical — this community's audience asks how things are built, so the architecture, integration
> and security sections answer that properly rather than hand-waving. **Everything is shown; nothing
> is hidden behind a control** — use the quick links above, or the contents below, to jump.


---

<a id="1-what-is-ceh"></a>
## 1. What is CEH?

Running a community conference used to mean spreadsheets, static forms and long mail threads:
who needs a hotel room, who is coming to the dinner, which speaker uploaded slides, which
sponsor sent a logo. CEH replaces that with **one place** where every person signs in and
self-services their own part of the event.

- **One hub, every role** — speakers, volunteers, sponsors, attendees, media and event
  partners share one app; each sees only what applies to them.
- **You own your data** — you update your own profile and bookings; nobody re-types anything.
- **Nothing slips** — every obligation is a task with a deadline and a reminder cadence.
- **Evergreen** — a new edition is new configuration, not new code.

<a id="2-ceh-by-the-numbers"></a>
## 2. CEH by the numbers

Everything below is counted from the shipping source code, not estimated.

### Code and tests

| | |
|---|---|
| **Application code** | **184,885** lines of C# (958 files) |
| **User interface** | **38,944** lines of Razor views (247 pages) |
| **Database migrations** | **13,726** lines of schema steps (221 migrations) |
| **Production total** | **237,555 lines** |
| **Automated tests** | **127,968** lines — 121,528 C# (580 files) plus 6,440 of PowerShell and browser tests |
| **Test count** | **5,538 automated tests**, all run before every release |
| **Total** | **365,523 lines** |

**One line of test for every two lines of production code** — and counting C# against C#, closer to
**1 : 1.5**. That ratio is why changing a shared route or a shared e-mail layout shows up as a handful
of failing assertions in seconds, instead of as a broken link somebody notices weeks later.

> The repository also holds ~1,221,000 lines of *generated* database-migration snapshots. They are
> excluded above: the tooling rewrites a complete model snapshot on every schema change, so counting
> them would more than quadruple the total while describing none of the work.

### What the platform is made of

| | |
|---|---|
| **Services** | **210** application services — one responsibility each |
| **Background jobs** | **36** — scheduled timers plus a webhook receiver, each with its own cadence you set yourself |
| **Feature switches** | **44**, each independently controllable per edition |
| **Roles** | **7** — organizer, speaker, sponsor, volunteer, attendee, media, event partner |
| **E-mail types** | **73** registered, each with its own audience control |
| **Data model** | **117** entity sets on one evergreen schema |
| **Integration code** | **190** files covering ticketing, the webshop, the finance system, SharePoint and LinkedIn |

### Reading these numbers

- **208 services and 36 background jobs** is what "nothing slips" actually costs. Every reminder, sync
  and deadline chase is a named, separately testable piece of work rather than a manual routine
  somebody has to remember to run — and you can change how often each one runs from a settings page,
  without a developer.
- **41 feature switches across 7 roles** are the control surface: any capability can be switched off
  for one edition, or released to a small test group first and widened once it is proven.
- **73 e-mail types** is the honest number, and each one is named, has a stated audience, can be held
  back on its own, and can have its reminder interval set **per role** — because a speaker and an
  attendee do not deserve the same nagging rhythm, and a wrong mail to the wrong list cannot be
  taken back.

<a id="3-features--what-the-hub-actually-does"></a>
## 3. Features — what the hub actually does

The full list, grouped. Everyone gets the **shared basics** first; after that each role sees
only its own sections. Nothing here is a mock-up — every item below is a page or a flow that
exists in the hub today.

> **If you run a community event, read this as a menu rather than a manual.** Most of what follows
> replaces something an organizing team currently does by hand — a spreadsheet, a mail-merge, a
> reminder somebody has to remember to send, a folder of logos, a "who still owes us their bio"
> conversation. Every one of these can be switched **off** for your edition if you do not need it,
> so a smaller event is not forced to run a bigger event's machinery.

**Every screenshot on this page is the real hub**, captured automatically from a running system.
The names, e-mail addresses, companies and photographs in them are **synthetic** — the capture
replaces every one before the picture is taken, so nobody's details are on display here.

### At a glance

| For | What they mainly use it for | Jump to |
| --- | --- | --- |
| **Everyone** | Signing in, Get Started, tasks and reminders, event info, asking the AI helper | [Shared basics](#shared-by-every-role) |
| **Speakers** | Sessions, bio and photo, slide uploads, travel and hotel, promotion graphics | [Speaker](#speaker) |
| **Sponsors** | Booth details, deliverables and logos, lead capture, their own announcements | [Sponsor and exhibitor](#sponsor-and-exhibitor) |
| **Volunteers** | Availability, shifts, the tasks assigned to them on the day | [Volunteer](#volunteer) |
| **Attendees** | Master Class selection, party sign-up, agenda, session evaluation | [Attendee](#attendee) |
| **Media / partners** | Accreditation, assets, what they are allowed to publish | [Media and event partner](#media-and-event-partner) |
| **Organizers** | Everything above, plus the command centre, e-mail, jobs and the campaign | [Organizer](#organizer) |

### The hub the moment you sign in

![The signed-in hub home page](/content/eldk27/img/hub-home.png)

Every role lands on a page shaped like this one: **what you still owe**, when it is due, and a
direct route to the pages that apply to you. Nothing else is shown — a speaker never sees a
sponsor's deliverables, and an attendee never sees either.

![Your open tasks with deadlines and a progress rollup](/content/eldk27/img/unified-task-checklist.png)

**Every obligation in the hub is a task with a deadline and its own reminder cadence**, and the
reminders stop the moment you answer. That single mechanism is what replaces the chasing e-mails:
nobody has to remember who still owes a bio, because the hub already knows and has already asked.

### Shared by every role


#### Everything every role gets, in full



| Group | What you get |
| --- | --- |
| **Getting in** | Passwordless sign-in; one-tap sign-in from any hub e-mail; stay signed in; sign in with a one-time code if you'd rather type it |
| **Get Started** | One resumable guided flow per role — leave halfway, come back, pick up where you stopped; every step editable afterwards, any time |
<!-- covers: 83 — an unfinished step names the field that would finish it. -->
| **It tells you what is missing** | An unfinished step names the field that would finish it — *"To finish this step, add: your phone number"* — right on the step, before you open anything. It always agrees with the tick: a step is unfinished only if it can name something outstanding, and a finished step says nothing at all, so you are never sent to fill in something already filled in |
| **Tasks and reminders** | Your open items with deadlines, a progress rollup, and reminders that stop the moment you answer — never a reminder for something you already did |
| **Register / update** | Party sign-up with head count; the logistics your role is entitled to (hotel, dinner, lunch, swag) |
| **Calendar** | E-mail yourself a calendar invitation for your hotel stay, the dinner, the party or a task deadline |
| **Event Info** | Wayfinding, addresses, good-to-know, last year's event, the public session catalogue, the public slides catalogue, and the Code of Conduct + Privacy Policy |
| **Ask anything** | AI Community Helper — answers grounded in this event's own content, and only ever from what you are allowed to see |
| **Getting help** | Contact Organizers, on every page, reaching the team directly |
| **Community feed** | Event announcements and updates in the hub, so news is not only in an inbox |
| **Games** | Timed learning games with a leaderboard — a light way to keep people engaged between sessions |
| **Session evaluation** | Scan the evaluation QR code in the room and submit your session evaluation — no app, no login wall |
| **Your privacy** | A plain-language privacy policy and Code of Conduct, and a hub that shows you only your own data |
| **Everywhere** | Mobile-first layout, keyboard and screen-reader accessible, and your data visible only to you |



### Speaker


A speaker signs in and sees **their own sessions** — times, rooms, track, level — and, next to them, the handful of things still outstanding: the bio, the photo, the preview slides, the travel claim. Each is a dated task with its own reminder rhythm, and a **readiness rollup** turns "have I done everything?" into a question with an actual answer.

The part speakers notice most is the **promotion pack**: the hub builds their announcement graphic for them, so sharing the session is a download rather than a design job.

#### Everything a speaker gets, in full



| Group | What you get |
| --- | --- |
| **Your sessions** | Personal hub with your sessions, times, rooms, track, level and length |
| **Your details** | Speaker Details in one page — profile, bio, photo, company, country, accreditation; changes flow out to the public event site automatically |
| **Deadlines** | Preview slide upload, final slide upload, travel reimbursement, promotion — each a dated task with its own reminder cadence, plus a readiness rollup that tells you what is still missing |
| **Slides and materials** | Upload per session, auto-versioned (the newest file is always the one served); browse other sessions' decks; speaker template and the event logo pack |
| **Help Promote** | Your personal session graphic and ready-made post text; publish to your own LinkedIn profile in one click; share a single session with its graphic attached |
| **Artwork composed for you** | The hub builds the event's promotion graphic itself — the event photograph, your photo in the speaker badge, your name and session title, in one look across the whole line-up. One speaker is a still image; two or more becomes an animation with a frame each, and a master class shows everyone teaching it |
| **What we post about you** | The announcements the organizers will publish about your sessions and your track on the event's LinkedIn page — the real text, the date and time, and whether each is still planned or already scheduled. Listed under **Social Media Announcements**, kept separate from the material you publish yourself |
| **Know your audience** | Attendee telemetry — who is coming, which topics and levels they picked — plus the full attendee survey results |
| **Master Class** | Group Q&A board for the class you teach (shown only if you teach one) |
| **After your session** | Your session ratings and open feedback, delivered to you; per-room evaluation QR codes to download and show |
| **Preparing** | Session guidelines, key dates and times, preview/final deadlines explained, how we do session evaluations |
| **Questions from the room** | Session Q&A — attendees ask, you answer, in the hub rather than in a chat nobody keeps |
| **Your category** | Community, sponsor or guest speaker — each with its own entitlements, so nobody is promised a hotel they were never offered |
| **The room** | A/V, comfort screen, HDMI switches, stage timer |
| **Your stay** | Hotel and speaker hotel info, dinner, lunch, speaker gift, travel reimbursement, party |
| **Your data, once** | Details you give the hub flow out to the public site and the schedule automatically — you are never asked for the same bio twice |

<!-- covers: 104 — the travel claim is only offered, and only chased, where it can be claimed. -->

**Travel reimbursement is only offered where it can be claimed.** It is for community speakers
travelling from outside the host country, and the claim form refuses anyone else. The
**reminder** now waits until you have told us which country you travel from — the task still appears
on your list in the meantime, so nobody quietly loses a reimbursement they are entitled to, but
nothing is chased on a guess.

#### 🖼 Screenshots — Speaker (3)


#### A speaker’s own hub — sessions, times, rooms and what is still outstanding


![A speaker’s own hub — sessions, times, rooms and what is still outstanding](/content/eldk27/img/speaker-hub.png)


#### The readiness rollup: what is still missing before the speaker is “done”


![The readiness rollup: what is still missing before the speaker is “done”](/content/eldk27/img/speaker-readiness.png)


#### Ready-made promotion graphics the speaker can download and share


![Ready-made promotion graphics the speaker can download and share](/content/eldk27/img/speaker-graphics.png)



### Sponsor and exhibitor


Sponsorship is where the manual chasing usually lives — logos, booth details, who is staffing the stand, which talk they get, whether the artwork arrived in the right format. The hub turns each of those into a **deliverable with a deadline and an owner**, visible to the sponsor themselves.

They also get the commercial side: **lead capture** at the booth, their own announcement posts, and a page that says plainly what their package includes and what is still missing.

#### Everything a sponsor or exhibitor gets, in full



| Group | What you get |
| --- | --- |
| **A shared company hub** | Every contact at your company sees the same hub — any colleague can continue where another stopped |
| **Company details** | Company profile, event coordinator, contacts, logos and artwork, kept by you rather than mailed back and forth |
| **Orders** | Your purchased services and packages, your linked contacts, and a link straight to the sponsor webshop |
| **Deliverables and tasks** | Every deliverable as a dated task, with a rollup at the top showing what is done, what is missing and what is overdue — tasks are driven by what you actually ordered |
| **Booth and exhibitor** *(booth holders)* | Booth profile, booth members, booth materials, promotional banner, your booth number and the expo map, and the booth run-of-show with your check-in slot |
[feature:sponsor-leads]| **Leads** *(booth holders)* | Lead and inquiry lists, plus an in-hub lead capture that keeps working if the lead system is unavailable |
| **Know your audience** | Attendee telemetry — the same "who is coming" view, with ranked topics and levels |
| **Promotion artwork** | The hub composes your sponsor graphic from your logo and the event photograph — the white panel takes the shape of your logo rather than stretching it — for the event's own social promotion, individually and grouped by category |
| **What we post about you** | Every announcement the organizers will publish about your company, your sponsor category and your own speakers on the event's LinkedIn page — **the picture, the real text** (your name and website already filled in, not placeholders), the date and time, and its state. Only posts that are approved and have everything they need are listed. Under **Social Media Announcements** |

<!-- covers: 99 — the preview shows the actual picture and the resolved words, on the sponsor and
     speaker pages the two rows above already describe. -->

| **Your team's evening** | One group party registration for the whole team, with a head count |
| **Your ticket allocation** | The tickets and discount codes included in your package — what has been used, what is left, and a warning before a prepaid pool runs dry |
| **Exhibiting** | Booth data kept in step with the ticketing system automatically, so your booth staff list does not need re-typing anywhere |
| **Engagement** | The sponsor app game — a light way to draw traffic to your booth, with its own leaderboard |
| **Materials** | Event logo pack download; all shared event info |



#### 🖼 Screenshots — Sponsor and exhibitor (3)


#### The sponsor’s self-service portal


![The sponsor’s self-service portal](/content/eldk27/img/sponsor-portal.png)


#### Booth details, stand number and what is still owed


![Booth details, stand number and what is still owed](/content/eldk27/img/sponsor-our-booth.png)


#### Deliverables tracked per item, with deadlines


![Deliverables tracked per item, with deadlines](/content/eldk27/img/sponsor-deliverables.png)



### Volunteer


Volunteers say **when they are available**, and the hub turns that into an actual schedule — shifts, tasks and a personal timetable for the day, rather than a shared spreadsheet nobody is sure is current.

#### Everything a volunteer gets, in full



| Group | What you get |
| --- | --- |
| **Joining** | Public sign-up form; access to the hub once a coordinator approves you |
| **Getting set up** | Profile, per-day availability, hotel, dinner, lunch, volunteer gift, party |
| **Your work** | My Availability (the days you can and cannot work) and My Assignments — your shifts and your tasks in one place |
| **Your shifts** | Confirm, decline or ask to swap a shift; declining frees it for someone else instead of leaving a silent gap |
| **Supervising** *(supervisors only)* | A dashboard for the areas you run — who is assigned, what is still uncovered |
| **Knowing what the job is** | Every shift carries its area, sub-area and the actual task description, so nobody arrives asking what they are meant to do |
| **Context** | Attendee telemetry, all shared event info |



#### 🖼 Screenshots — Volunteer (2)


#### Volunteer sign-up — availability, in their own words


![Volunteer sign-up — availability, in their own words](/content/eldk27/img/volunteer-signup.png)


#### The resulting personal shift schedule


![The resulting personal shift schedule](/content/eldk27/img/volunteer-schedule.png)



### Attendee


#### My Event — the attendee’s own agenda, bookings and choices


![My Event — the attendee’s own agenda, bookings and choices](/content/eldk27/img/attendee-my-event.png)


Attendees get **their** event: the Master Class they chose, the party they signed up for, their agenda, and a way to evaluate a session by scanning the code in the room — no app to install and no login wall in front of the feedback.

#### Everything an attendee gets, in full



| Group | What you get |
| --- | --- |
| **Master Class** *(2-day tickets)* | Choose your class with live seat counts; join a waitlist and get promoted automatically when a seat frees up; a group Q&A board with your teacher |
| **The party** | Sign up for the networking party on the pre-day, with the time, venue and what to expect — and change your answer at any time |
| **Sessions** | The public session catalogue and the slides catalogue, filterable by track, level and speaker |
| **Your ticket** | Ticket changes, upgrades and cancellations picked up from the ticketing system on their own — no "please re-register" mails |
| **Have your say** | Submit a session evaluation from the QR code in the room; answer the attendee survey that shapes next year's programme |
| **In your inbox** | A welcome that points at your first step, calendar invitations, and reminders only for things you have not answered |

<!-- covers: 101 — the volume-package monitor: one link per block buyer, own company only. -->

**If your company bought a block of tickets**, whoever paid can be sent a single link that shows who
has claimed one — name, company, e-mail, order id and the date, plus an Excel export — without a
login and without seeing anybody else's company. The count the organizers see is the same query the
link runs, so they can check the list is right before sending it. Cancelled tickets, other editions
and look-alike domains are left out by design.

### Media and event partner


Accredited media and event partners get a narrow, deliberate slice: what they may publish, the assets they are allowed to use, and the practical details — without access to anybody else’s personal data.

#### Everything media and event partners get, in full



| Group | What you get |
| --- | --- |
| **Getting set up** | Profile, hotel, dinner, lunch, swag, party — the logistics your accreditation entitles you to |
| **Your obligations** | Task list with deadlines and reminders |
| **Context** | Crew resources, attendee telemetry, all shared event info |



### Organizer


#### The command centre — what needs attention right now


![The command centre — what needs attention right now](/content/eldk27/img/organizer-command-center.png)


#### Every participant, filterable by role, status and ring


![Every participant, filterable by role, status and ring](/content/eldk27/img/organizer-participants.png)


#### The e-mail centre — preview exactly what will be sent


![The e-mail centre — preview exactly what will be sent](/content/eldk27/img/organizer-email-center.png)


#### The social-media campaign: planned, held, and approved by a human


![The social-media campaign: planned, held, and approved by a human](/content/eldk27/img/some-content-studio.png)


#### Background jobs, their cadence, and whether they are healthy


![Background jobs, their cadence, and whether they are healthy](/content/eldk27/img/organizer-jobs.png)


The organizer view is the reason the rest of it stays calm. It answers **"what needs me today?"** in one place: who has not finished onboarding, which sponsor has not sent artwork, which speaker is missing a bio, which job stopped running.

It also holds the two things that are easiest to get wrong at a community event. **E-mail** is previewable before it sends and gated so a test can never reach a real audience by accident. And the **social-media campaign** is planned by the hub but never published without a human approving it — posts are scheduled, held, and each one says what it is still waiting for.

#### Everything an organizer gets, in full



| Group | What you get |
| --- | --- |
| **Overview** | Command centre, dashboard and event overview — registrations, head counts, task completion, readiness and anything needing attention, at a glance |
| **People** | Participant directory and grid editing; pending-speaker approval; pre-selection queue; onboarding status; attendees; action queue; welcome and permanent sign-in links; act-as-user with its own separate log |
<!-- covers: 82, 80, 81 — pre-staging, the test-data mark, and test accounts out of the totals.
     Markers are HTML comments so they never render; IntroCurrencyTests reads them. -->
| **Adding someone the sync has not heard of** | People arrive before the systems know about them — a volunteer says yes in a corridor, a booth contact is named before the paperwork. A **New participant** button takes first name, last name, e-mail, active or not, and a role, and creates a real participant you can assign work to straight away. E-mail identifies the person, so a later import merges rather than duplicates, and **nobody is e-mailed unless you tick the box** |
| **Test accounts are marked, whatever you called them** | A rehearsal account is excluded because it is *marked as test data* — a deliberate property of the account, not a guess about which ones look synthetic. Accounts you created before release rings existed carry the mark too, and moving somebody to a different ring to trial a feature never quietly adds them to the lunch order |
| **Test accounts stop ordering lunch** | Rehearsal accounts fill in forms — that is the point of them — but those answers used to land in the numbers you order against. Anything marked as test data now drops out of **every** operational total: hotel rooms and the rooming list, lunch, swag and polo sizes, the party head count, the appreciation dinner, the kitchen's allergy roll-up and the head-count tiles. The lists and the numbers changed together, so a total and the names printed beside it always still agree |
| **Sessions and speakers** | Speaker roster and readiness; sessions; session Q&A; session evaluations; call-for-speakers import; session source and sync direction; sync queue; Master Classes |
[feature:surveys]| **Surveys** | Attendee surveys — build them, and read the topic/level results that feed the speaker and sponsor "know your audience" views |
| **Sponsors** | Sponsor directory; sponsor tasks and deliverables; status dashboard; sponsor welcome; app game; ERP contacts; webshop company management |
[feature:sponsor-leads]| **Sponsor leads** | Lead, inquiry and booth-meeting lists pulled from the CRM, per sponsor, downloadable as CSV |
| **Volunteers** | Work structure (areas, sub-areas, tasks); bucket allocation; allocation scenarios; the allocation queue; bulk task import and export via Excel; task definition editing |
<!-- covers: 84 — bulk move of imported volunteer tasks into the right category. -->
| **Getting the imported plan into your structure** | A spreadsheet import lands everything in one bucket while the categories you set up — lead and supervisor already appointed — sit empty. Tick a batch of tasks, choose where they belong, and **move** them; it is reversible. Finding the right ones is the slow part, so there is a search across the task text **including the description the list does not show**, and a **Select all matching** button — and searches add up, so "check-in" then "registration desk" move together. **Volunteers keep their assignments**: moving a task changes where it sits, not who is doing it. Long categories fold away so a small one is not pushed off screen by one holding a hundred |
| **Logistics** | Hotels, hotel assignments, room blocks, and room **allotments** compared against real demand with cut-off reminders; swag; travel reimbursements; lunch head counts; group photos; speaker and order review; the schedule; evaluation QR codes; party RSVPs; exports; data freshness and sync health |
| **Communication** | E-mail centre, delivery log, templates, broadcasts, invitations, welcome sign-in mails, speaker reminders, community feed — every message logged per recipient, and every mail findable under **every role it reaches** |
| **Reminder rhythm** | Set how often each chase goes out — and set it **separately per role**, because a speaker owing slides and an attendee owing a party answer do not need the same cadence. "Once only" is a valid answer |
[feature:webshop-erp-invoicing]| **Money and tickets** | Webshop orders; sponsor packages turned into deliverables automatically; draft invoices raised in the finance system; partner **coupon pools** with what is left and who has been billed; a warning **before** a prepaid pool runs out; new coupon codes that announce themselves |
| **Marketing and social** | Session and speaker graphics composed by the hub itself; the content studio; and the LinkedIn campaign below |
| **The LinkedIn campaign** | The hub plans the whole announcement campaign itself — every track, session, sponsor tier and sponsor gets its posts, dated across the run-up so the types interleave rather than arriving in clumps. Each post is **held for approval**; nothing reaches the company page until someone says so |
| **Written by you, varied by the hub** | You supply a **catalog of wordings** per post type and the hub draws one per post, so forty announcements do not open with the same sentence. The choice is stable — the same post keeps its wording, so the queue stops moving under you while you review it |
| **Live values, not frozen text** | A post stores the wording with its `{variables}` intact and fills them in **when it publishes** — so a speaker added next month, a sponsor's late text edit, or a rebuilt graphic all reach a post planned long before. The picture is attached for you from the subject's own artwork; you never pick one from a list |
| **An assistant writes the teaser** | For session and sponsor-tier posts, an AI assistant drafts the opening paragraph from the session's own title and abstract. It is written once, stored, and shown to you for approval like everything else |
| **It refuses when something is missing** | A post whose text still has an empty value — a sponsor who has not sent their social text, a teaser not yet written — **will not publish**, and says what it is waiting for. The same sentence is shown to the sponsor or speaker on their own page, so they can see what they owe |
| **Approval, optionally automatic** | Approve each post yourself, or let the hub approve the template-built ones once they are far enough ahead. Anything you wrote or edited always waits for you, and the lead time doubles as a review window — an auto-approved post cannot publish for days |
<!-- covers: 74, 75, 76, 77, 78, 79 — the social-media campaign chapters: unsaved-changes marker,
     speaker mentions, sponsor-contact mentions, resolve-once, master-class run, track readiness. -->
| **Speakers are tagged, not just named** | A `{Speakers}` variable turns the speakers on a session or track into real LinkedIn **mentions**, so the speaker is notified and the post reaches their network too — through LinkedIn's own official lookup, no third-party service and no scraping. LinkedIn only lets a company page mention people who **follow that page**, so anyone who does not is shown by their full name exactly as before, and the hub **tells you who it could not tag** and links to their profile so you can do it by hand |
| **The sponsor's own people, on their announcement** | A sponsor's announcement can tag the sponsor's signer and event coordinator alongside the company, so the people who actually bought the sponsorship see it — with the same follow rule and the same honest "could not tag this person" report |
| **Everyone mentionable is looked up once** | LinkedIn lookups are slow and rate-limited and a person's id never changes, so each is resolved once and remembered, with a daily job filling in anyone new. It covers speakers and sponsor contacts alike, it **never guesses** between two people with the same name, and it **never treats a rate-limited failure as an answer** — one clears by itself, the other does not |
| **Announce your master classes together** | The scheduler spreads by default so no week is crowded. Master classes want the opposite: they are confirmed early and deserve to land as a **moment**. Name a start date and they all go out together from that day, filling forward at your normal posts-per-day — and it **moves posts you have already approved**, in both directions, because a date that only steered future posts would have changed nothing you could see. Only the date changes; the wording, picture and approval stay as you left them |
| **Nothing typed is lost** | The post editor marks **unsaved changes** the moment your text differs from what loaded, and anything that would discard it — Next, Previous, approving, deleting, rescheduling — asks first. Saving never asks, because saving is the thing being protected |
| **Track announcements wait for the line-up, not for a date** | A post naming a track's speakers can never pick anyone up once it has published, so the hub works out readiness from the data rather than from a date somebody guessed months earlier. A track whose newest session arrived a week ago has stopped growing — but that alone could not tell a batch that had **ended** from one that had never **begun**, so readiness is the latest of this track's newest session, the newest session anywhere in the edition, and the day your **Call for Speakers closes**. After that the data takes over again: if your intake lands a fortnight late, every track announcement moves a fortnight with it, with nobody editing anything |
[feature:signage-agenda-sync]| **Screens and signage** | Agenda and signage kept in step with the schedule, so a room change reaches the screens without anyone re-exporting anything |
| **Feeding other systems** | Session and evaluation reports other systems can collect for themselves, and an upload path for evaluation devices — your data does not get trapped in the hub |
| **Setup and governance** | Feature settings with release rings; calendar settings; rooms, session lengths and levels; integration endpoints; the audit trail; background jobs with a pause switch and **cadences you set yourself**; test-data cleanup |
| **Knowing it is healthy** | Data freshness and sync health at a glance; a job that fails twice e-mails you; a held speaker or a volunteer waiting for review is chased to the team inbox rather than sitting unnoticed |
| **Safety rails** | Destructive actions are blocked when something still depends on the record, guarded behind confirmation, and written to the audit trail with what they changed |

<!-- covers: 102, 103 — one place to compose a post, and the picture picked from the library. -->
<!-- covers: 105 — the public who's-coming page: aggregates only, and readable per topic. -->

**Writing a post yourself** starts at the top of the campaign page and opens the same **post editor**
every other post uses, so a new post gets the live preview, the variables, the picture picker and the
schedule — rather than a second, slightly different compose form. Any post in the list can also be
**duplicated** as a starting point; the copy is held for approval and its text is copied as *words*,
with the variables filled in as they read today, because a copy belongs to no session or sponsor.
The **picture is chosen from a list** of what is actually in the artwork library instead of typed
from memory — a mistyped file name used to publish a post with no picture and look exactly like the
artwork having failed.

**Who is coming** is published as aggregates — ticket type, job roles, where people travel from, how
they heard about the event — with **no company names and nothing that identifies an individual**, and
sponsors see the same figures as everyone else. Organizers get one extra breakdown, the companies
their attendees come from, behind their own login; it is never built for the public or sponsor pages
rather than merely hidden on them.



<a id="4-how-it-works-for-you"></a>
## 4. How it works for you

- **Passwordless sign-in.** Every hub link in our e-mails signs you in automatically; manual
  sign-in is a one-time code by e-mail. There is no password to forget or leak.
- **A hub per role**, with your tasks, sessions or shifts and the practical info you need.
- **Get Started** — a resumable guided flow that collects everything we need from you.
- **Tasks and reminders** — open items carry deadlines; reminders stop the moment you answer.
- **Calendar invitations** for your hotel stay, dinner, party and task deadlines.

<a id="5-solution-architecture"></a>
## 5. Solution architecture

CEH is a compact .NET solution on Azure PaaS, built deliberately on proven, conventional
components rather than novel ones: three deployables sharing one domain library and one database.

**Why .NET and server-rendered pages, not an app.** Almost everyone opens the hub on a phone,
between sessions, on conference wifi. A server-rendered .NET site sends a finished page instead
of a JavaScript bundle that must download, parse and then fetch its data — so the first screen
appears fast on a weak connection and on an older handset, and there is nothing to install or
update. Every page is built mobile-first and checked at ~360px wide before it ships; the same
URL works on a laptop. It also means no separate app store release, no app to keep signed in,
and one codebase to secure rather than a web app plus two native ones.

<svg viewBox="0 0 940 470" role="img" aria-label="CEH architecture: browser and crawlers reach the App Service web app; a Functions job host runs timers; both share a Core library, Azure SQL, Key Vault, App Insights, Microsoft Graph and external systems" style="width:100%;height:auto;max-width:940px;font-family:Segoe UI,Arial,sans-serif;">
  <defs>
    <marker id="ar" markerWidth="9" markerHeight="9" refX="8" refY="3" orient="auto"><path d="M0,0 L0,6 L9,3 z" fill="#64748b"/></marker>
  </defs>
  <rect x="8" y="8" width="924" height="454" rx="10" fill="#f8fafc" stroke="#e2e8f0"/>
  <text x="24" y="32" font-size="13" fill="#64748b">Clients</text>
  <rect x="24" y="42" width="150" height="52" rx="8" fill="#fff" stroke="#cbd5e1"/><text x="99" y="66" font-size="13" text-anchor="middle" fill="#1f2937">Participant browser</text><text x="99" y="82" font-size="11" text-anchor="middle" fill="#6b7280">mobile-first</text>
  <rect x="24" y="104" width="150" height="46" rx="8" fill="#fff" stroke="#cbd5e1"/><text x="99" y="126" font-size="12" text-anchor="middle" fill="#1f2937">Social crawlers</text><text x="99" y="141" font-size="11" text-anchor="middle" fill="#6b7280">OpenGraph cards</text>
  <rect x="24" y="160" width="150" height="46" rx="8" fill="#fff" stroke="#cbd5e1"/><text x="99" y="182" font-size="12" text-anchor="middle" fill="#1f2937">Event-site iframe</text><text x="99" y="197" font-size="11" text-anchor="middle" fill="#6b7280">CSP frame-ancestors</text>
  <text x="214" y="32" font-size="13" fill="#64748b">Azure App Platform</text>
  <rect x="214" y="42" width="240" height="118" rx="10" fill="#e8f1fc" stroke="#1565c0"/>
  <text x="334" y="62" font-size="14" font-weight="bold" text-anchor="middle" fill="#0b3c78">App Service (Linux containers)</text>
  <!-- §658 — prose, not the raw Azure runtime-stack identifier. That deployment token reads as a
       broken string on a customer-facing page (operator 2026-07-29: "picture looks wrong
       formatted") and is internal infra detail besides. The token itself is deliberately NOT
       repeated here: this file is rendered markdown-with-raw-HTML, and a stray token inside a
       comment has escaped into the output before on this platform. -->
  <text x="334" y="80" font-size="12" text-anchor="middle" fill="#1f2937">ASP.NET Core Razor Pages · .NET 10</text>
  <!-- §471 — the redundancy: the platform load balancer in front of TWO container instances,
       so one recycling does not take the hub down. -->
  <text x="334" y="98" font-size="11" text-anchor="middle" fill="#0b3c78">▼ platform load balancer ▼</text>
  <rect x="226" y="104" width="102" height="30" rx="6" fill="#fff" stroke="#1565c0"/>
  <text x="277" y="123" font-size="11" text-anchor="middle" fill="#0b3c78">instance 1</text>
  <rect x="340" y="104" width="102" height="30" rx="6" fill="#fff" stroke="#1565c0"/>
  <text x="391" y="123" font-size="11" text-anchor="middle" fill="#0b3c78">instance 2</text>
  <!-- §707.48 (operator 2026-07-30: "format issue") — SPLIT ACROSS TWO LINES. As one line at
       font-size 11 this string is ~310px wide inside a 240px box (x=214..454), so it spilled out of
       both sides of the App Service card. SVG does not wrap text: a <text> element runs to whatever
       width it needs and silently overflows its container, which is why this looks fine in source
       and wrong on screen. Two shorter lines at font-size 10 fit inside the box with the baselines
       clear of the border at y=160. -->
  <text x="334" y="146" font-size="10" text-anchor="middle" fill="#475569">production + staging slot · swap deploy</text>
  <text x="334" y="157" font-size="10" text-anchor="middle" fill="#475569">instant rollback</text>
  <rect x="214" y="176" width="240" height="104" rx="10" fill="#eef7ee" stroke="#2e7d32"/>
  <text x="334" y="200" font-size="14" font-weight="bold" text-anchor="middle" fill="#1b5e20">Azure Functions (isolated)</text>
  <!-- §1056 — COUNTED, not carried forward. 36 = the [Function] attributes in CommunityHub.Jobs
       (37) minus the single HttpTrigger, and it cross-checks against JobCatalog's 36 descriptors.
       It read "25" until 2026-08-10, which was true when it was written. ⚠️ A hand-written count in
       a diagram goes stale silently — re-derive it from the two sources above, do not update it by
       memory. -->
  <text x="334" y="220" font-size="12" text-anchor="middle" fill="#1f2937">36 timer jobs + 1 webhook</text>
  <text x="334" y="238" font-size="11" text-anchor="middle" fill="#475569">syncs · reminders · digests</text>
  <text x="334" y="256" font-size="11" text-anchor="middle" fill="#475569">pause switch · failure alerts</text>
  <rect x="214" y="296" width="240" height="60" rx="10" fill="#fff7ed" stroke="#c2860a"/>
  <text x="334" y="318" font-size="13" font-weight="bold" text-anchor="middle" fill="#7c4a03">Shared domain library</text>
  <text x="334" y="337" font-size="11" text-anchor="middle" fill="#475569">EF Core · services · integrations</text>
  <text x="494" y="32" font-size="13" fill="#64748b">Platform services</text>
  <rect x="494" y="42" width="196" height="56" rx="8" fill="#fff" stroke="#cbd5e1"/><text x="592" y="64" font-size="13" text-anchor="middle" fill="#1f2937">Azure SQL Database</text><text x="592" y="82" font-size="11" text-anchor="middle" fill="#6b7280">EF Core · RCSI · PITR</text>
  <rect x="494" y="108" width="196" height="56" rx="8" fill="#fff" stroke="#cbd5e1"/><text x="592" y="130" font-size="13" text-anchor="middle" fill="#1f2937">Key Vault</text><text x="592" y="148" font-size="11" text-anchor="middle" fill="#6b7280">secrets · certificates</text>
  <rect x="494" y="174" width="196" height="56" rx="8" fill="#fff" stroke="#cbd5e1"/><text x="592" y="196" font-size="13" text-anchor="middle" fill="#1f2937">Microsoft Entra ID</text><text x="592" y="214" font-size="11" text-anchor="middle" fill="#6b7280">app identities · SQL auth</text>
  <rect x="494" y="240" width="196" height="56" rx="8" fill="#fff" stroke="#cbd5e1"/><text x="592" y="262" font-size="13" text-anchor="middle" fill="#1f2937">Application Insights</text><text x="592" y="280" font-size="11" text-anchor="middle" fill="#6b7280">traces · failures</text>
  <rect x="494" y="306" width="196" height="56" rx="8" fill="#fff" stroke="#cbd5e1"/><text x="592" y="328" font-size="13" text-anchor="middle" fill="#1f2937">Microsoft Graph</text><text x="592" y="346" font-size="11" text-anchor="middle" fill="#6b7280">SharePoint documents</text>
  <text x="730" y="32" font-size="13" fill="#64748b">External systems</text>
  <!-- §678 — each external system now NAMES its product, and Webshop / ERP are two separate rows
       because they are two separate systems (operator 2026-07-29). Two lines per entry: the role in
       purple, the product beneath it in grey, so the longer names fit the fixed-width box. -->
  <rect x="730" y="42" width="186" height="336" rx="10" fill="#faf5ff" stroke="#7e57c2"/>
  <text x="823" y="62" font-size="11.5" text-anchor="middle" fill="#4527a0">Call for speakers</text>
  <text x="823" y="75" font-size="10" text-anchor="middle" fill="#7e57c2">Sessionize</text>
  <text x="823" y="97" font-size="11.5" text-anchor="middle" fill="#4527a0">Event / ticket system</text>
  <text x="823" y="110" font-size="10" text-anchor="middle" fill="#7e57c2">Zoho Backstage</text>
  <text x="823" y="132" font-size="11.5" text-anchor="middle" fill="#4527a0">Webshop</text>
  <text x="823" y="145" font-size="10" text-anchor="middle" fill="#7e57c2">WooCommerce</text>
  <text x="823" y="167" font-size="11.5" text-anchor="middle" fill="#4527a0">ERP</text>
  <text x="823" y="180" font-size="10" text-anchor="middle" fill="#7e57c2">e-conomic</text>
  <text x="823" y="202" font-size="11.5" text-anchor="middle" fill="#4527a0">Mail relay (SMTP)</text>
  <text x="823" y="215" font-size="10" text-anchor="middle" fill="#7e57c2">Brevo</text>
  <text x="823" y="237" font-size="11.5" text-anchor="middle" fill="#4527a0">Social publishing</text>
  <text x="823" y="250" font-size="10" text-anchor="middle" fill="#7e57c2">LinkedIn &amp; X/Twitter</text>
  <text x="823" y="272" font-size="11.5" text-anchor="middle" fill="#4527a0">Session evaluation</text>
  <text x="823" y="285" font-size="10" text-anchor="middle" fill="#7e57c2">built into the hub</text>
  <text x="823" y="307" font-size="11.5" text-anchor="middle" fill="#4527a0">Signage</text>
  <text x="823" y="320" font-size="10" text-anchor="middle" fill="#7e57c2">OptiSigns</text>
  <!-- §1056 — THREE LINES, NOT TWO. The second line was "adapters + field maps (see the
       integration section)": ~50 characters at font-size 10 is ~250px, centred at x=823 inside a
       panel that spans 730–916 (186px). It therefore overflowed BOTH edges of the box and ran past
       the 940 viewBox itself. ⚠️ SVG text does not wrap — there is no width to reflow against, so a
       caption that fits in the editor is only ever a coincidence. Measure: at font-size 10, ~186px
       holds roughly 36 characters; keep every line under that. -->
  <text x="823" y="336" font-size="10" text-anchor="middle" fill="#6b7280">reached only through typed</text>
  <text x="823" y="349" font-size="10" text-anchor="middle" fill="#6b7280">adapters + field maps</text>
  <text x="823" y="362" font-size="10" text-anchor="middle" fill="#6b7280">(see the integration section)</text>
  <line x1="174" y1="68" x2="212" y2="90" stroke="#64748b" marker-end="url(#ar)"/>
  <line x1="174" y1="127" x2="212" y2="110" stroke="#64748b" marker-end="url(#ar)"/>
  <line x1="174" y1="183" x2="212" y2="130" stroke="#64748b" marker-end="url(#ar)"/>
  <line x1="334" y1="160" x2="334" y2="174" stroke="#64748b" marker-end="url(#ar)"/>
  <line x1="334" y1="280" x2="334" y2="294" stroke="#64748b" marker-end="url(#ar)"/>
  <line x1="454" y1="100" x2="492" y2="70" stroke="#64748b" marker-end="url(#ar)"/>
  <line x1="454" y1="118" x2="492" y2="136" stroke="#64748b" marker-end="url(#ar)"/>
  <line x1="454" y1="228" x2="492" y2="202" stroke="#64748b" marker-end="url(#ar)"/>
  <line x1="454" y1="240" x2="492" y2="268" stroke="#64748b" marker-end="url(#ar)"/>
  <line x1="454" y1="326" x2="492" y2="334" stroke="#64748b" marker-end="url(#ar)"/>
  <line x1="690" y1="200" x2="728" y2="200" stroke="#64748b" marker-end="url(#ar)"/>
  <text x="24" y="404" font-size="12" fill="#475569">Everything below the browser is PaaS — no VMs and no state on disk. The web app runs as a managed Linux container (.NET 10), patched by Azure.</text>
  <text x="24" y="426" font-size="12" fill="#475569">Infrastructure is declared in Bicep; the same templates build dev and production.</text>
  <text x="24" y="448" font-size="12" fill="#475569">Serverless SQL and a small App Service plan keep the running cost low.</text>
</svg>

### Components

| Component | What runs there | Notes for the curious |
| --- | --- | --- |
| **Azure App Service (Linux)** | ASP.NET Core (.NET 10) Razor Pages + a few minimal-API endpoints (health, file proxies, leads API) | Production has a **staging slot**; every release is deployed to the slot, warmed, then **swapped** — zero downtime, and rollback is a swap back |
| **Azure Functions, isolated worker** | 25 timer-triggered jobs + one HTTP webhook receiver | Cadences from 1 minute (webhook drain) to daily (reminders 08:00 UTC). A master **pause switch** and a middleware that alerts on the *second* consecutive failure |
| **Azure SQL Database** | One evergreen schema, ~90 entity sets, all edition-scoped | EF Core with retrying execution strategy; RCSI on, so seat allocation uses an explicit `UPDLOCK, HOLDLOCK` claim to stay oversell-proof; point-in-time restore + long-term retention |
| **Key Vault** | Integration secrets and certificates | Nothing sensitive lives in source or in the repo; apps read what they need at runtime |
| **Microsoft Entra ID** | Workload identities for deployment, management and database access | SQL is **Entra-only** — there are no SQL logins to steal |
| **Application Insights** | Traces, dependencies, failures | Engine failures also raise an operator e-mail, so problems surface without watching dashboards |
| **Microsoft Graph** | SharePoint document access (templates, logos, slides, graphics, QR codes, evaluation PDFs) | Scoped to specific sites; participants never touch SharePoint — the hub proxies every file |
| **Bicep** | The whole environment as code | A new environment is a parameter file, not a click-path |

### Runtime shape

- **One shared domain library.** Web and jobs are thin hosts over the same services, so a rule
  (entitlements, ring gating, reminder cadence) exists exactly once and cannot drift between
  the UI and the background engine.
- **Edition-scoped everything.** Every table hangs off an `Event` row. A new year is a new row
  plus JSON config — same code, same database, history preserved, and a returning speaker is
  the same person across editions.
- **Configuration as data.** Dates, venue rooms, hotels, deadlines, task catalogues, e-mail
  templates, content pages (including this one) and integration maps are JSON or database
  rows the organizers edit — not code.
- **Stateless web tier.** No session state on disk; the data-protection key ring is shared via
  the database so a slot swap never signs anyone out.
- **Containers, not VMs.** The web app and the Functions host each run as a managed **Linux
  container** on App Service (.NET 10). Azure builds and patches the image; we supply
  the application. A restart is a container start/stop — which is exactly why a cold start is
  visible in the logs as one.
- **Two instances behind the platform load balancer.** The production plan runs **2 workers**, so
  a container recycle on one no longer takes the site down with it: the load balancer keeps
  serving from the other. This works only because the tier is stateless and the key ring is
  shared — otherwise the second instance would sign people out at random.
- **Health-gated swaps.** `WEBSITE_SWAP_WARMUP_PING_PATH=/health` and a health-check path mean the
  platform will not route traffic to an instance that has not reported healthy.
- **Run from package.** The app is mounted from an immutable package (`WEBSITE_RUN_FROM_PACKAGE`),
  so the file system is read-only at runtime — nothing can write to `wwwroot`, and every instance
  runs byte-identical code.

<a id="6-integration-layer-and-field-mappers"></a>
## 6. Integration layer and field mappers

Six external systems feed or receive data. Rather than hand-rolling per-system sync code, CEH
has a small **integration engine** plus **declarative field maps** — the part most worth
stealing if you build something similar.

The diagram below names each system by its **role**, not its brand: that is the generic-first
rule this hub is built on, and it is what lets another community swap a vendor without touching the
engine. In the upstream instance those roles are filled by:

| Role in the diagram | System we use |
|---|---|
| Call for speakers | **Sessionize** — speaker submissions, acceptance, and the session catalogue |
| Ticketing / attendees | **Zoho Backstage** — ticket sales, orders, attendee records |
| Webshop | **WooCommerce** — sponsor and exhibitor packages |
| ERP | **e-conomic** — invoicing and customer records |
| Document library | **Microsoft SharePoint** (via Microsoft Graph) — sponsor uploads, speaker material, room QR codes |
| Public event system | **Zoho Backstage** — the public agenda and speaker pages |
| Mail relay | **Brevo** — every outbound email, logged per recipient |
| Social publishing | **LinkedIn** — scheduled company-page posts for speakers and sponsors |
| Hosting and identity | **Microsoft Azure** + **Microsoft Entra ID** — app, jobs, SQL, Key Vault |

Where the same brand appears twice, the two directions are genuinely different integrations:
tickets are pulled *in* from Zoho Backstage, while the agenda is pushed *out* to it.

<svg viewBox="0 0 940 372" role="img" aria-label="CEH sits in the middle: inbound from call-for-speakers, ticketing and webshop or ERP; outbound to the event system, mail, social and signage; all traffic passes the field-map engine" style="width:100%;height:auto;max-width:940px;font-family:Segoe UI,Arial,sans-serif;">
  <defs><marker id="ar2" markerWidth="9" markerHeight="9" refX="8" refY="3" orient="auto"><path d="M0,0 L0,6 L9,3 z" fill="#64748b"/></marker></defs>
  <rect x="8" y="8" width="924" height="314" rx="10" fill="#f8fafc" stroke="#e2e8f0"/>
  <!-- §678 — product names under each role, same convention as the External systems box. -->
  <rect x="30" y="52" width="190" height="42" rx="8" fill="#fff" stroke="#cbd5e1"/><text x="125" y="72" font-size="12" text-anchor="middle" fill="#1f2937">Call for speakers</text><text x="125" y="86" font-size="10" text-anchor="middle" fill="#6b7280">Sessionize</text>
  <rect x="30" y="106" width="190" height="42" rx="8" fill="#fff" stroke="#cbd5e1"/><text x="125" y="126" font-size="12" text-anchor="middle" fill="#1f2937">Ticketing / attendees</text><text x="125" y="140" font-size="10" text-anchor="middle" fill="#6b7280">Zoho Backstage</text>
  <rect x="30" y="160" width="190" height="42" rx="8" fill="#fff" stroke="#cbd5e1"/><text x="125" y="180" font-size="12" text-anchor="middle" fill="#1f2937">Webshop</text><text x="125" y="194" font-size="10" text-anchor="middle" fill="#6b7280">WooCommerce</text>
  <rect x="30" y="214" width="190" height="42" rx="8" fill="#fff" stroke="#cbd5e1"/><text x="125" y="234" font-size="12" text-anchor="middle" fill="#1f2937">ERP</text><text x="125" y="248" font-size="10" text-anchor="middle" fill="#6b7280">e-conomic</text>
  <rect x="30" y="268" width="190" height="42" rx="8" fill="#fff" stroke="#cbd5e1"/><text x="125" y="288" font-size="12" text-anchor="middle" fill="#1f2937">Document library</text><text x="125" y="302" font-size="10" text-anchor="middle" fill="#6b7280">SharePoint</text>
  <text x="125" y="36" font-size="12" text-anchor="middle" fill="#64748b">Inbound</text>
  <rect x="300" y="60" width="330" height="190" rx="12" fill="#e8f1fc" stroke="#1565c0"/>
  <text x="465" y="86" font-size="14" font-weight="bold" text-anchor="middle" fill="#0b3c78">Field-map engine</text>
  <text x="465" y="110" font-size="12" text-anchor="middle" fill="#1f2937">FieldKind: string · multiline · integer</text>
  <text x="465" y="128" font-size="12" text-anchor="middle" fill="#1f2937">object · array</text>
  <text x="465" y="150" font-size="12" text-anchor="middle" fill="#1f2937">Capability: create-and-update ·</text>
  <text x="465" y="168" font-size="12" text-anchor="middle" fill="#1f2937">create-only · GUI-only</text>
  <text x="465" y="190" font-size="12" text-anchor="middle" fill="#1f2937">ReadSupport: list · detail · not-readable</text>
  <text x="465" y="212" font-size="12" text-anchor="middle" fill="#1f2937">Compare → up-to-date / needs-update /</text>
  <text x="465" y="230" font-size="12" text-anchor="middle" fill="#1f2937">unverifiable</text>
  <rect x="300" y="264" width="330" height="42" rx="8" fill="#fff7ed" stroke="#c2860a"/>
  <text x="465" y="290" font-size="12.5" text-anchor="middle" fill="#7c4a03">Per-integration JSON maps (schema v2)</text>
  <rect x="710" y="52" width="200" height="42" rx="8" fill="#fff" stroke="#cbd5e1"/><text x="810" y="72" font-size="12" text-anchor="middle" fill="#1f2937">Public event system</text><text x="810" y="86" font-size="10" text-anchor="middle" fill="#6b7280">Zoho Backstage</text>
  <rect x="710" y="106" width="200" height="42" rx="8" fill="#fff" stroke="#cbd5e1"/><text x="810" y="126" font-size="12" text-anchor="middle" fill="#1f2937">Mail relay</text><text x="810" y="140" font-size="10" text-anchor="middle" fill="#6b7280">Brevo</text>
  <rect x="710" y="160" width="200" height="42" rx="8" fill="#fff" stroke="#cbd5e1"/><text x="810" y="180" font-size="12" text-anchor="middle" fill="#1f2937">Social publishing</text><text x="810" y="194" font-size="10" text-anchor="middle" fill="#6b7280">LinkedIn &amp; X/Twitter</text>
  <!-- 352 (operator 2026-07-26): "these are 2 systems - make it as 2 bullets - and call it
       Evaluation / Surveys". Signage and evaluation are separate outbound integrations and were
       misleadingly drawn as one box. -->
  <rect x="710" y="214" width="200" height="42" rx="8" fill="#fff" stroke="#cbd5e1"/><text x="810" y="234" font-size="12" text-anchor="middle" fill="#1f2937">Signage</text><text x="810" y="248" font-size="10" text-anchor="middle" fill="#6b7280">OptiSigns</text>
  <rect x="710" y="268" width="200" height="42" rx="8" fill="#fff" stroke="#cbd5e1"/><text x="810" y="288" font-size="12" text-anchor="middle" fill="#1f2937">Evaluation / Surveys</text><text x="810" y="302" font-size="10" text-anchor="middle" fill="#6b7280">built into the hub</text>
  <text x="810" y="36" font-size="12" text-anchor="middle" fill="#64748b">Outbound (CEH is master)</text>
  <line x1="220" y1="73" x2="298" y2="120" stroke="#64748b" marker-end="url(#ar2)"/>
  <line x1="220" y1="127" x2="298" y2="140" stroke="#64748b" marker-end="url(#ar2)"/>
  <line x1="220" y1="181" x2="298" y2="160" stroke="#64748b" marker-end="url(#ar2)"/>
  <line x1="220" y1="235" x2="298" y2="180" stroke="#64748b" marker-end="url(#ar2)"/>
  <line x1="632" y1="120" x2="708" y2="73" stroke="#64748b" marker-end="url(#ar2)"/>
  <line x1="632" y1="140" x2="708" y2="127" stroke="#64748b" marker-end="url(#ar2)"/>
  <line x1="632" y1="160" x2="708" y2="181" stroke="#64748b" marker-end="url(#ar2)"/>
  <line x1="632" y1="180" x2="708" y2="235" stroke="#64748b" marker-end="url(#ar2)"/>
  <!-- 352: arrow for the new Evaluation / Surveys box. -->
  <line x1="632" y1="190" x2="708" y2="289" stroke="#64748b" marker-end="url(#ar2)"/>
</svg>

**The engine** (integration-agnostic, unit-tested, permanent):

- **`FieldKind`** — `String`, `MultiLine`, `Integer`, `Object`, `Array`. The kind decides how
  "changed" is judged: a trimmed ordinal compare for strings; **existence only** for
  multiline (rich-text targets reformat what you send, so a character compare would flag
  every record forever) and for merged objects; set-inclusion for arrays.
- **`Capability`** — `CreateAndUpdate` (push changes automatically), `CreateOnly` (the target
  API accepts the field on create but has no update — a later change becomes an *action mail*
  to the organizers naming the exact GUI fields to fix), `GuiOnly` (the API refuses the field
  entirely — action mail from the very first push).
- **`ReadSupport`** — `List`, `DetailOnly`, `NotReadable`. Some targets accept a value but
  never echo it back; those get a **local push-hash stamp** so we can tell "already sent"
  from "needs sending" without an infinite re-push loop.
- **`Direction`** — inbound or outbound per field. For the public event system it is uniformly
  outbound: **CEH is the master**, and a value edited directly in that system's UI shows up as
  a drift report, not as a silent overwrite of CEH.
- **Named derivations** — computed values (a skills CSV merged from checkbox groups, a tag set
  built from level + language + submitted labels) are functions registered by key, so a map
  can reference them without embedding logic in the data.

**The maps** are JSON files, one per integration, in a versioned schema where every property
sits under an explicit `source` or `target` block: record names, **API endpoints**, field
names, the **GUI label** the organizer sees, kind, capability, read support, and free-text
**limitations** per field. Adding an integration is a new JSON file plus any new derivation
functions — no engine change. Vendor names never leak into the domain model; they live in
adapters and configuration.

**Why this matters operationally:** it turns "did the sync work?" into a deterministic
verdict per field, and it makes the awkward reality of third-party APIs (create-only records,
fields that never echo, fields only settable in a GUI) explicit and testable instead of tribal
knowledge.

<!-- internal-only:start -->
<a id="7-security"></a>
## 7. Security

Security here is layered, and every layer is server-enforced — nothing depends on the browser
behaving.

<svg viewBox="0 0 940 300" role="img" aria-label="Request path: TLS, then authentication, then the fail-closed authorization backstop, then per-request revalidation, then own-row scoping, with audit and secret management alongside" style="width:100%;height:auto;max-width:940px;font-family:Segoe UI,Arial,sans-serif;">
  <defs><marker id="ar3" markerWidth="9" markerHeight="9" refX="8" refY="3" orient="auto"><path d="M0,0 L0,6 L9,3 z" fill="#64748b"/></marker></defs>
  <rect x="8" y="8" width="924" height="284" rx="10" fill="#f8fafc" stroke="#e2e8f0"/>
  <text x="24" y="34" font-size="13" fill="#64748b">Request path</text>
  <rect x="24" y="46" width="150" height="66" rx="8" fill="#fff" stroke="#cbd5e1"/><text x="99" y="72" font-size="12.5" text-anchor="middle" fill="#1f2937">HTTPS + HSTS</text><text x="99" y="92" font-size="11" text-anchor="middle" fill="#6b7280">CSP frame-ancestors</text>
  <rect x="196" y="46" width="150" height="66" rx="8" fill="#fff" stroke="#cbd5e1"/><text x="271" y="72" font-size="12.5" text-anchor="middle" fill="#1f2937">Authentication</text><text x="271" y="92" font-size="11" text-anchor="middle" fill="#6b7280">PIN · magic link</text>
  <rect x="368" y="46" width="164" height="66" rx="8" fill="#fdecec" stroke="#c62828"/><text x="450" y="72" font-size="12.5" text-anchor="middle" fill="#7f1d1d">Fail-closed authz</text><text x="450" y="92" font-size="11" text-anchor="middle" fill="#7f1d1d">deny unless opted out</text>
  <rect x="554" y="46" width="164" height="66" rx="8" fill="#fff" stroke="#cbd5e1"/><text x="636" y="72" font-size="12.5" text-anchor="middle" fill="#1f2937">Live revalidation</text><text x="636" y="92" font-size="11" text-anchor="middle" fill="#6b7280">account still active?</text>
  <rect x="740" y="46" width="176" height="66" rx="8" fill="#eef7ee" stroke="#2e7d32"/><text x="828" y="72" font-size="12.5" text-anchor="middle" fill="#1b5e20">Own-row scoping</text><text x="828" y="92" font-size="11" text-anchor="middle" fill="#1b5e20">your data only</text>
  <line x1="174" y1="79" x2="194" y2="79" stroke="#64748b" marker-end="url(#ar3)"/>
  <line x1="346" y1="79" x2="366" y2="79" stroke="#64748b" marker-end="url(#ar3)"/>
  <line x1="532" y1="79" x2="552" y2="79" stroke="#64748b" marker-end="url(#ar3)"/>
  <line x1="718" y1="79" x2="738" y2="79" stroke="#64748b" marker-end="url(#ar3)"/>
  <rect x="24" y="140" width="286" height="130" rx="10" fill="#fff" stroke="#cbd5e1"/>
  <text x="167" y="164" font-size="13" font-weight="bold" text-anchor="middle" fill="#1D3380">Identity</text>
  <text x="40" y="188" font-size="11.5" fill="#374151">• one-time PIN: hashed, short TTL, lockout</text>
  <text x="40" y="208" font-size="11.5" fill="#374151">• non-enumerable: unknown = same answer</text>
  <text x="40" y="228" font-size="11.5" fill="#374151">• magic links hashed at rest, revocable</text>
  <text x="40" y="248" font-size="11.5" fill="#374151">• links scoped to one person + edition</text>
  <rect x="326" y="140" width="286" height="130" rx="10" fill="#fff" stroke="#cbd5e1"/>
  <text x="469" y="164" font-size="13" font-weight="bold" text-anchor="middle" fill="#1D3380">Secrets and access</text>
  <text x="342" y="188" font-size="11.5" fill="#374151">• no secrets in source or config files</text>
  <text x="342" y="208" font-size="11.5" fill="#374151">• Key Vault + workload identities</text>
  <text x="342" y="228" font-size="11.5" fill="#374151">• database is Entra-only (no SQL logins)</text>
  <text x="342" y="248" font-size="11.5" fill="#374151">• document access scoped to one site</text>
  <rect x="628" y="140" width="288" height="130" rx="10" fill="#fff" stroke="#cbd5e1"/>
  <text x="772" y="164" font-size="13" font-weight="bold" text-anchor="middle" fill="#1D3380">Accountability</text>
  <text x="644" y="188" font-size="11.5" fill="#374151">• every mutating action audited</text>
  <text x="644" y="208" font-size="11.5" fill="#374151">• act-as-user is logged separately</text>
  <text x="644" y="228" font-size="11.5" fill="#374151">• outbound mail logged per recipient</text>
  <text x="644" y="248" font-size="11.5" fill="#374151">• engine failures alert the operators</text>
</svg>

**Sign-in.** There are no passwords. A one-time code is hashed before storage, expires
quickly, locks out after repeated wrong attempts, and the request path is deliberately
**non-enumerable** — an unknown address gets exactly the same response as a known one, so the
form cannot be used to discover who is registered. The auto-login links in our mails are
random tokens **stored only as a hash**, bound to one person and one edition, revocable and
rotatable by the organizers, and their use is recorded.

**Sessions.** Cookies are `HttpOnly`, `Secure` and `SameSite=None` (the hub can run inside the
event site's iframe, which the CSP `frame-ancestors` rule restricts to that one origin).
Authenticated pages are sent `no-store` so a shared or bfcached browser cannot show them
again. Crucially, a live session is **re-validated against the database** on a short cycle:
deactivate someone and their open session stops working, rather than surviving until the
cookie expires.

**Authorization is fail-closed.** The app's default policy denies anything not explicitly
marked public, so a newly added page is private by mistake, never public by mistake. On top of
that, role checks run **server-side**, and every read is **own-row scoped** — a speaker's
queries are constrained to sessions they present, a sponsor's to their company. When an
organizer uses *act as user*, the session carries markers that block nested impersonation and
block write handlers that require a genuine organizer, and the whole episode is logged.

**Secrets and workload identity.** No secret is in source control. Integration credentials
live in Key Vault; deployment and document management use **certificate-based workload
identities** with the smallest scope that works, and database access is **Entra-only**, so
there is no SQL password anywhere to leak. Documents are read and written by the application's
own identity against a specific SharePoint site — participants get files through hub proxy
endpoints and never receive SharePoint permissions.

**Outbound mail is gated at one chokepoint.** Every message — from a welcome to a reminder to
an ops alert — passes one sender that enforces: the recipient's **release ring**, a global
**kill switch**, and (in non-production) a redirect of all mail to a single test inbox. Ledger
entries that prevent duplicate sends are written **only when a message is really delivered**,
so a suppressed mail is retried later instead of being silently marked done. Unknown
recipients are dropped rather than mailed.

**Data protection and privacy.** Aggregated telemetry (who is coming, topics, levels) is shown
as counts, never as a list of individuals. Deactivating a participant runs a single cascade
that releases their bookings, shifts, RSVPs and open tasks so catering and hotel numbers stay
truthful. Backups cover the non-redeployable state (database, secrets, uploads); the code and
infrastructure are simply redeployed from source.

<!-- internal-only:end -->
<a id="8-how-it-is-built-and-released"></a>
## 8. How it is built and released

- **Generic-first.** The product name in code is neutral; vendor-specific behaviour lives in
  adapters and JSON. That is what makes the platform reusable by other communities rather
  than a one-conference fork.
- **Tests are the release gate.** 5,538 unit and integration tests (domain rules, entitlement
  matrices, ring gating, reminder cadences, seat-allocation races), a Pester suite that asserts
  one contract per shipped feature, and Playwright browser suites per role that check every
  route renders, has no console errors, keeps a mobile layout and enforces the role gates. The
  full suite must be green before anything is pushed.

### The platform by numbers

The full count — code, tests, services, jobs, feature switches and roles — is in
[2. CEH by the numbers](#2-ceh-by-the-numbers). It is kept in ONE place on purpose: two tables of the
same figures drift apart, and the stale one is always the one being read.

### Knowing when something is wrong

The platform reports on itself, so problems are found by looking rather than by waiting for someone
to complain:

- **Full request tracing.** Every page request and every database call is timed and recorded, so a
  slow page can be separated into its parts — is the database busy, is an external system slow, or
  is the app itself the bottleneck? Errors are captured with the exact operation that produced them.
- **A dashboard for the organizers**, not just for engineers: traffic over time, how many people are
  using the hub concurrently, the slowest operations, failed requests and the health of every
  outbound integration — all in the admin area, no cloud console needed.
- **Engine alerts.** A background job that fails twice in a row e-mails the organizers, so a broken
  sync surfaces on its own instead of being discovered when the data looks wrong.
- **Every outbound e-mail is logged per recipient**, with its outcome, so "did they get it?" is a
  question with an answer.
- **Concurrency is proven, not assumed.** Master-class seat allocation was load-tested against
  the real production database at **400 and 1000 concurrent users with zero oversell** — the
  guarded claim uses explicit locking because snapshot isolation would otherwise permit a
  double sell. Details below.

### What the stress testing actually showed

Correctness under load was measured on the **real production Azure SQL database**, not a
developer machine — because the bug we were hunting only exists on the real engine:

- **Master Class seats, 400 concurrent** sign-ups, waitlists and cancellation-driven
  promotions: **confirmed seats exactly equalled capacity, oversell 0, zero exceptions**, in
  about 6 seconds, with each freed seat promoted exactly once.
- **Deliberate worst case, 1000 concurrent** with 500 people racing for a *single* 8-seat
  class: **oversell still 0** — correctness held — though ~2% of those requests timed out
  queueing for the seat lock. That is not a realistic shape (the real ~1500 people spread
  across ~8 classes and across weeks), and the answer is to scale the database tier up for
  the event window.
- **Email, 500 messages** through the mail relay: 500 accepted, 0 failures, ~37/second, no
  throttling.
- **Sign-in, 400 concurrent** users authenticating and using the app on production:
  400/400 succeeded, half of all responses under 0.2 s, 95% under 1.8 s.

The finding that justified the whole exercise: production SQL runs with **snapshot isolation
on**, under which the original "count the seats, then insert" guard read a stale count and
**did** oversell. A local SQL Express test could never have shown it — snapshot isolation is
off there. The fix takes an explicit lock over just that one class's seats, so two people
booking the *same* class serialize while different classes stay independent.
- **Zero-downtime releases.** Build → deploy to the staging slot → warm up → swap → health
  check → log validation. Rollback is a swap back, in seconds.
- **Progressive rollout with rings.** Every feature and every mail carries a released ring;
  people carry a ring. Ring 0/1 is the operator's own accounts, ring 2 a handful of real
  testers, ring 3 everyone. New behaviour is proven on a small audience before it reaches
  1500 inboxes.
- **Documentation is part of the definition of done** — a living backlog, a customer-facing
  feature catalogue, a design document and a test document, all updated in the same change as
  the code.

### The toolchain

CEH is built with **AI-assisted development**, and is open about it:

- **Claude** (Anthropic), driven through **Claude Code**, is the primary development partner —
  used to write and review code, audit whole subsystems for defects, and keep the four
  canonical documents in step with every change. It works to the same rule a human contributor
  does: the full test suite is the gate, and nothing ships red.
- **Visual Studio Code** is the editor, with **Git and GitHub** for source control, change
  history and the public open-source mirror.
- **GitHub Actions** builds and publishes; **Microsoft Azure** hosts the web app, the Functions
  jobs, the SQL database and Key Vault.
- **A human stays accountable.** Every release is reviewed and triggered by a person,
  destructive actions are guarded, and the release rings above mean new behaviour reaches a
  handful of real people before it reaches 1500 inboxes. AI makes the work faster and lets far
  more of the codebase be audited at once — it does not decide what ships.

### How a release reaches production

Every deploy is a **slot swap**, never an in-place overwrite. The new build is published to a
**staging slot**, warmed there, and only then swapped into production — so the version that goes
live is one that has already started successfully.

<svg viewBox="0 0 940 300" role="img" aria-label="Deployment flow: build and test, publish to the staging slot, warm it, swap into production behind the load balancer across two instances, verify health, with rollback by swapping back" style="width:100%;height:auto;max-width:940px;font-family:Segoe UI,Arial,sans-serif;">
  <defs>
    <marker id="dep-ar" markerWidth="10" markerHeight="10" refX="9" refY="3" orient="auto"><path d="M0,0 L0,6 L9,3 z" fill="#64748b"/></marker>
  </defs>
  <rect x="8" y="8" width="924" height="284" rx="10" fill="#f8fafc" stroke="#e2e8f0"/>

  <rect x="24" y="60" width="150" height="66" rx="8" fill="#e0f2fe" stroke="#7dd3fc"/>
  <text x="99" y="86" font-size="13" font-weight="600" text-anchor="middle" fill="#0c4a6e">1. Build</text>
  <text x="99" y="106" font-size="11" text-anchor="middle" fill="#0c4a6e">full test suite green</text>

  <rect x="204" y="60" width="150" height="66" rx="8" fill="#e0f2fe" stroke="#7dd3fc"/>
  <text x="279" y="86" font-size="13" font-weight="600" text-anchor="middle" fill="#0c4a6e">2. Staging slot</text>
  <text x="279" y="106" font-size="11" text-anchor="middle" fill="#0c4a6e">package deployed</text>

  <rect x="384" y="60" width="150" height="66" rx="8" fill="#fef9c3" stroke="#fde047"/>
  <text x="459" y="86" font-size="13" font-weight="600" text-anchor="middle" fill="#713f12">3. Warm up</text>
  <text x="459" y="106" font-size="11" text-anchor="middle" fill="#713f12">/health must answer</text>

  <rect x="564" y="60" width="150" height="66" rx="8" fill="#dcfce7" stroke="#86efac"/>
  <text x="639" y="86" font-size="13" font-weight="600" text-anchor="middle" fill="#14532d">4. Swap</text>
  <text x="639" y="106" font-size="11" text-anchor="middle" fill="#14532d">staging ⇄ production</text>

  <rect x="744" y="60" width="172" height="66" rx="8" fill="#dcfce7" stroke="#86efac"/>
  <text x="830" y="86" font-size="13" font-weight="600" text-anchor="middle" fill="#14532d">5. Verify</text>
  <text x="830" y="106" font-size="11" text-anchor="middle" fill="#14532d">health + boot log check</text>

  <line x1="174" y1="93" x2="200" y2="93" stroke="#64748b" marker-end="url(#dep-ar)"/>
  <line x1="354" y1="93" x2="380" y2="93" stroke="#64748b" marker-end="url(#dep-ar)"/>
  <line x1="534" y1="93" x2="560" y2="93" stroke="#64748b" marker-end="url(#dep-ar)"/>
  <line x1="714" y1="93" x2="740" y2="93" stroke="#64748b" marker-end="url(#dep-ar)"/>

  <rect x="384" y="176" width="330" height="72" rx="8" fill="#ede9fe" stroke="#c4b5fd"/>
  <text x="549" y="200" font-size="12" font-weight="600" text-anchor="middle" fill="#4c1d95">Production: load balancer → 2 container instances</text>
  <text x="549" y="222" font-size="11" text-anchor="middle" fill="#4c1d95">one can recycle while the other keeps serving</text>
  <line x1="639" y1="126" x2="639" y2="172" stroke="#64748b" marker-end="url(#dep-ar)"/>

  <text x="24" y="200" font-size="12" fill="#b91c1c" font-weight="600">Rollback</text>
  <text x="24" y="220" font-size="11" fill="#475569">Swap back — the previous build is</text>
  <text x="24" y="236" font-size="11" fill="#475569">still sitting in the staging slot.</text>

  <text x="24" y="274" font-size="11" fill="#475569">If the app is slow to start, the swap is refused rather than serving a half-started site — the failure mode is a failed deploy, never a broken production.</text>
</svg>

### Monitoring and alerting

<svg viewBox="0 0 940 260" role="img" aria-label="Monitoring: health endpoint polled by the platform, Application Insights collecting traces and exceptions, deployment log validation after each release, and job run state in the database" style="width:100%;height:auto;max-width:940px;font-family:Segoe UI,Arial,sans-serif;">
  <rect x="8" y="8" width="924" height="244" rx="10" fill="#f8fafc" stroke="#e2e8f0"/>

  <rect x="24" y="48" width="204" height="92" rx="8" fill="#e0f2fe" stroke="#7dd3fc"/>
  <text x="126" y="76" font-size="13" font-weight="600" text-anchor="middle" fill="#0c4a6e">Health probe</text>
  <text x="126" y="98" font-size="11" text-anchor="middle" fill="#0c4a6e">/health, polled by App Service</text>
  <text x="126" y="116" font-size="11" text-anchor="middle" fill="#0c4a6e">gates traffic and swaps</text>

  <rect x="252" y="48" width="204" height="92" rx="8" fill="#e0f2fe" stroke="#7dd3fc"/>
  <text x="354" y="76" font-size="13" font-weight="600" text-anchor="middle" fill="#0c4a6e">Application Insights</text>
  <text x="354" y="98" font-size="11" text-anchor="middle" fill="#0c4a6e">requests, traces, exceptions</text>
  <text x="354" y="116" font-size="11" text-anchor="middle" fill="#0c4a6e">queryable per deploy window</text>

  <rect x="480" y="48" width="204" height="92" rx="8" fill="#e0f2fe" stroke="#7dd3fc"/>
  <text x="582" y="76" font-size="13" font-weight="600" text-anchor="middle" fill="#0c4a6e">Post-deploy log check</text>
  <text x="582" y="98" font-size="11" text-anchor="middle" fill="#0c4a6e">boot window scanned for</text>
  <text x="582" y="116" font-size="11" text-anchor="middle" fill="#0c4a6e">errors before it is called done</text>

  <rect x="708" y="48" width="208" height="92" rx="8" fill="#e0f2fe" stroke="#7dd3fc"/>
  <text x="812" y="76" font-size="13" font-weight="600" text-anchor="middle" fill="#0c4a6e">Job run state</text>
  <text x="812" y="98" font-size="11" text-anchor="middle" fill="#0c4a6e">every scheduled job records</text>
  <text x="812" y="116" font-size="11" text-anchor="middle" fill="#0c4a6e">its last run and outcome</text>

  <!-- §727: SVG <text> DOES NOT WRAP — it renders on one line and the viewBox clips the tail, so a
       long sentence silently loses its end ("…still read from the Jobs pa"). Split across explicit
       lines, and the rect grew to hold them. Never put a full sentence in one <text> here. -->
  <rect x="24" y="164" width="892" height="86" rx="8" fill="#ecfdf5" stroke="#a7f3d0"/>
  <text x="40" y="190" font-size="12" font-weight="600" fill="#065f46">Automated alerting — five Azure Monitor rules, e-mailed to the organizers</text>
  <text x="40" y="211" font-size="11" fill="#065f46">Sev 1: health check failing on any instance · Sev 2: more than 10 server errors in 5 minutes</text>
  <text x="40" y="228" font-size="11" fill="#065f46">Sev 3: average response time over 5s for 15 minutes</text>
  <text x="40" y="245" font-size="11" fill="#065f46">Job failures are still read from the Jobs page rather than alerted.</text>
</svg>

<a id="9-who-built-it"></a>
## 9. Who built it

CEH was built for a community conference and released as **open source** so any community can run
their event on it:
[github.com/KnudsenMorten/community-event-hub](https://github.com/KnudsenMorten/community-event-hub).

Found a bug in the software or have an idea? Raise it on GitHub at
[github.com/KnudsenMorten/community-event-hub/issues](https://github.com/KnudsenMorten/community-event-hub/issues).
Questions about this event go to the organizers — see **Contact Organizers**.

