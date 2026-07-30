# UX flows — detailed, per role (§251)

> Operator report, traced claim-by-claim from the code on `feat/jul07-roleflow-corrections` (2026-07-07).
> Every fact cites its source file inline. Companion to `docs/ROLE-FLOWS.md` (§240); this doc adds email
> subjects, the wizard-vs-unique-task split, the FULL sponsor task catalog, and §248/§250 as built.

## 0. Shared plumbing (read once)

- **🔒 RING STATUS — nothing reaches real people yet.** Every email feature defaults to **Ring 1** (`src/CommunityHub.Core/Settings/FeatureCatalog.cs`); `BrevoEmailSender` drops any recipient whose effective ring is above the feature's released ring and logs `RING-DROP` (`src/CommunityHub.Core/Email/BrevoEmailSender.cs`). Attendee welcomes additionally sit under the `Email:AttendeeWelcomeMaxReleaseRing` ceiling, default Ring 1 (same file). **Until you raise rings, only Ring-0/Ring-1 test accounts get mail.** Every subject gets an ` [ELDK27]` postfix at send (`BrevoEmailSender.NormalizeSubject`).
- **§248 two-button welcome, /Welcome retired.** Every welcome email has a PRIMARY button **"Open the Event Hub — Get Started"** → the role's wizard, and a SECONDARY **"Browse the Event Hub"** → hub home; both are auto-sign-in magic links (`templates/emails/welcome-*.html`, `src/CommunityHub.Core/Reminders/WelcomeWithLoginEmailService.cs`). The `/Welcome` first-sign-in interstitial is retired — magic links land DIRECTLY on the target page, `/Index` no longer redirects (`src/CommunityHub/Pages/Welcome.cshtml.cs`, `src/CommunityHub/Pages/Go.cshtml.cs`).
- **§232 engine.** `ReminderJob` runs **daily 08:00 UTC** (`[TimerTrigger("0 0 8 * * *")]`, `src/CommunityHub.Jobs/ReminderJob.cs`); it seeds speaker-deadline/party/master-class tasks, then runs the builders below. Dedup = `SentReminder` ledger; the ledger row is written **only on real delivery**, so ring-dropped mail auto-retries when rings widen.

| Reminder track | Builder | Cadence | Stops when |
|---|---|---|---|
| Task due-date | `TaskReminderBuilder` (`src/CommunityHub.Core/Reminders/TaskReminderBuilder.cs`) | **ONE mail, on the due day** (occasion `task:{id}:due`) | Task done (or the day passes) |
| Party RSVP | `AttendeePartyReminderBuilder` (`src/CommunityHub.Core/Reminders/`) | Biweekly (14-day windows) from task creation | RSVP answered (Yes OR No) |
| Master-Class selection | `AttendeeMasterClassReminderBuilder` (same dir) | Biweekly from task creation | CONFIRMED signup |
| **§250 Get-Started digest** | `GetStartedDigestBuilder` (`src/CommunityHub.Core/Reminders/GetStartedDigestBuilder.cs`) | First send ≥14 days after the welcome (`WelcomeWithLoginSentAt ?? CreatedAt` anchor), then biweekly | 100% wizard complete (`openKeys.Count == 0`) |
| Speaker Q&A digest | `SpeakerQuestionDigestService` (`src/CommunityHub.Core/Email/`) | **Weekly** max (`MinIntervalDays = 7`, §246) | No open questions |

- **§250 digest details** (subject *"{{eventDisplayName}} — a few Get Started steps are still waiting for you"*, `templates/emails/getstarted-digest.html`): lists the person's OPEN wizard steps (titles from the same `SharedResource.resx` the wizard renders) + ONE magic-link button into their wizard route. **Skip rule:** if the only open steps are `party`/`masterclass` it is skipped — those ride their own biweekly track, no double-nag (`SelfNaggingStepKeys`, `GetStartedDigestBuilder.cs`). It enumerates the WIZARD SERVICES, never the task table — so speaker deck deadlines and travel are structurally excluded (§250 scope guard). Sponsor audience = event-coordinator contacts only (§7c check in the same file).
- **Wizard ⇄ tasks sync.** Every wizard step is mirrored as a task (`src/CommunityHub/Forms/WizardStepTaskSeeder.cs`) and `FormTaskReconciler` syncs both ways on page load: form submitted → task Done; data removed → task reopens (`src/CommunityHub.Core/Participants/FormTaskReconciler.cs`).
- **Dates:** setup day 2027-02-08, Master-Class/pre-day **2027-02-09**, main day 2027-02-10 (`config/event.eldk27.json`). Party = **9 Feb 2027, 16:00–18:30**, expo/food area Bella Center (`src/CommunityHub/Pages/Party.cshtml.cs`).

---

## 1. SPONSOR (priority 1)

### 1.1 Email timeline

| When (from webshop order) | Subject | Trigger | Stops / notes |
|---|---|---|---|
| Within ~15 min | *"Welcome to {{eventDisplayName}} — info for sponsors"* (`templates/emails/welcome-sponsor.html`) | `SponsorWelcomeReconcileJob`, every 15 min (`src/CommunityHub.Jobs/SponsorWelcomeReconcileJob.cs`) | **Event-coordinator contacts ONLY** (signer-only excluded). Once per coordinator (`SentReminder`). **Blocks for booth companies until SharePoint upload folders exist.** Two buttons: primary → `/Sponsor/GetStarted`, secondary → hub home. |
| Day 14+, then biweekly | *"…a few Get Started steps are still waiting for you"* | §250 `GetStartedDigestBuilder` via daily `ReminderJob` | Coordinators only; lists open wizard steps; CTA → `/Sponsor/GetStarted`; stops at 100 %; skipped if only Party remains. |
| Day 14+, then biweekly (while Party unanswered) | *"{{eventDisplayName}} task {{state}}: …Party…"* | `AttendeePartyReminderBuilder` | **Stops for the WHOLE company once ANY contact answers** (§228 group RSVP, `PartyRsvpService.SubmitGroupAsync`). |
| On each task's due day (see catalog §1.3) | *"{{eventDisplayName}} task {{state}}: {{taskTitle}}"* (`templates/emails/task-deadline-reminder.html`) | `TaskReminderBuilder`, daily 08:00 | ONE mail per task, sent to **all event coordinators** of the company, never the assignee/signer (`SponsorRecipientResolver`, occasion `task:{id}:due:{email}`). |
| Event-driven (organizer-triggered) | *"…please remember the app game gift — {{companyName}}"* / *"…your group photo session — {{companyName}}"* | Manual send from `/Organizer/AppGame` + `/Organizer/GroupPhotos` (`src/CommunityHub/Pages/Organizer/AppGame.cshtml.cs`, `GroupPhotos.cshtml.cs`) | One-off, at your discretion. |
| During/after event (feature `sponsor-leads`, default OFF) | *"{{eventDisplayName}}: {{leadCount}} new lead(s) for {{sponsorCompany}}"* | `SponsorLeadsJob`, hourly at :15 (`src/CommunityHub.Jobs/SponsorLeadsJob.cs`) | Delta-cursored; daily-cadence prefs fire 06:15 UTC. |
| On wall-design upload | *"[ELDK27] Sponsor uploaded/updated file by {{companyName}}"* → **to organizers** (sb@/mok@) | `SponsorUploadWatchJob`, every 15 min, feature `sponsor-upload-watch` default OFF (`src/CommunityHub.Jobs/SponsorUploadWatchJob.cs`) | Organizer notification, not sponsor mail. |

### 1.2 What they see

- **First click** (welcome primary button): signed in via magic link, lands on **`/Sponsor/GetStarted`** — the wizard. Secondary button → hub home `/` with the **Sponsor pipeline card + task checklist** (`src/CommunityHub/Pages/Index.cshtml.cs`).
- **Nav** (`src/CommunityHub.Core/Navigation/NavBuilder.cs`): Sponsor Get Started · Sponsor Webshop fold-out (Buy Services → external webshop, Orders & Linked Contacts → `/Sponsor`) · Company Details · Attendee Telemetry · *(booth companies only)* **Exhibitor & Booth fold-out** (Exhibitor Profile / Booth Members / Materials / Promo Banner → Zoho dashboard, Our Booth → `/Sponsor/Booth`, Leads/Inquiries (Zoho) + Capture Lead failover → `/Sponsor/CaptureLead`, gated by `sponsor-leads`) · My Tasks (`/Sponsor/Tasks`) · Booth Run-of-Show (`/Sponsor/Logistics`) · Party Signup.
- **`/Sponsor` home**: company card, linked contacts, orders grid, booth number, and read-only **App-Game participation + Group-Photo registration** status blocks (§234, `src/CommunityHub/Pages/Sponsor/Index.cshtml.cs`).

### 1.3 Get-Started WIZARD vs UNIQUE standalone tasks

**Wizard — `SponsorWizardService` (`src/CommunityHub.Core/Forms/SponsorWizardService.cs`, lines 95–137). Company-scoped: any contact's answer completes the step for everyone. Steps deep-link into `/Sponsor/CompanyDetails` anchors. Nagged by the §250 biweekly digest (Party by its own biweekly track).**

| # | Step | Done when | Exhibitor-only |
|---|---|---|---|
| 1 | Company Details | Website OR description saved in `SponsorInfo` | – |
| 2 | Event Coordinator | `EventCoordinatorEmail` set | – |
| 3 | Your contacts | ≥1 e-conomic contact linked (fail-soft if ERP down) | – |
| 4 | Logos & artwork | Raster OR vector logo path saved | – |
| 5 | Booth members | ≥1 `SponsorBoothMember` row | ✓ |
| 6 | Booth materials | ≥1 `SponsorBoothMaterial` row | ✓ |
| 7 | **Booth check-in slot (§229 — NEW)** | `BoothCheckInSlot` set (incl. opt-out) | ✓ |
| 8 | **Party (§228 — group answer)** | Any contact's `PartyRsvp` (Yes/No) for the company | – |

> **§229 booth check-in** (`src/CommunityHub.Core/Domain/SponsorInfo.cs`, `BoothCheckInSlots`): when will the booth team arrive on pre-day 9 Feb 2027 — **7:30–9:00 · 9:00–10:30 · 10:30–12:00 · 12:00–15:00 · "We don't expect to participate on pre-day"** (opt-out). ONE answer per company; **audited**: `BoothCheckInSetAt` + `BoothCheckInSetByEmail` stamp who answered (`src/CommunityHub/Pages/Sponsor/CompanyDetails.cshtml.cs`), plus the global mutating-POST audit trail (`src/CommunityHub/Audit/AuditPageFilter.cs`).
> **§228 party group reservation** (`src/CommunityHub.Core/Reminders/PartyRsvpService.cs`, `SubmitGroupAsync`): ONE `PartyRsvp` row per company — explicit Yes/No (no default) **+ head count for the team** (`HeadCount`, `src/CommunityHub/Pages/Party.cshtml.cs`); any linked contact can update it; answering silences the party reminder for the whole company.

**UNIQUE standalone tasks — the full catalog, seeded from `config/sponsor.eldk27.json` by `SponsorTaskExpander` (`src/CommunityHub.Core/Integrations/SponsorTaskExpander.cs`) on the webshop pull; organizer can add/edit per company at `/Organizer/SponsorAdmin/Tasks` (`src/CommunityHub/Pages/Organizer/SponsorAdmin/Tasks.cshtml.cs`). Company-scoped, visible on `/Sponsor/Tasks`; reminder = ONE due-day mail to all coordinators (`TaskReminderBuilder`).**

| Task (title as shown) | Set | Due (rule → approx.) | Mandatory |
|---|---|---|---|
| Initial onboarding of sponsor | all sponsors | first-order date +14d (`contractPlus`) | ✓ |
| Brochures, competition flyer or swags for attendee bags | all sponsors | event −30d → ~10 Jan 2027 | – |
| **Event App Game — Extra Exposure Opportunity** (donate a prize, logo exposure) | all sponsors | event −55d → ~16 Dec 2026 | – |
| Submit session description | session sponsors | event −135d → ~27 Sep 2026 | ✓ |
| **Upload sponsor wall design in vector format** (versioned SharePoint upload, organizers notified) | booth | event −71d → ~30 Nov 2026 | ✓ |
| Choose your booth layout (table, chairs — tier coupon code) | booth | event −71d → ~30 Nov 2026 | ✓ |
| Register booth members (enables lead scanning) | booth | event −40d → ~31 Dec 2026 | ✓ |
| **Pre-Event Shipment of packages to stand in Expo** (DSV/venue freight, customs notes) | booth | event −7d → ~2 Feb 2027 | – |
| Validate booth members have lead scan app + exhibitor guide | booth | event −7d → ~2 Feb 2027 | ✓ |
| TV Rental for booth-presentations | booth | event −70d → ~1 Dec 2026 | – |
| **Download leads and inquiries** (Zoho export for sales follow-up) | booth | **event +1d → ~10 Feb 2027 — POST-event task** (`downloadLeads.days: -1`) | ✓ |

(`brandedFeature` and `preday` sets are currently empty in the config.) Group photo + app game **status** also surface read-only on the sponsor home (§234); their emails are organizer-triggered (§1.1).

---

## 2. SPEAKER (priority 2)

### 2.1 Email timeline (entry: Sessionize import → you activate in `/Organizer/PreselectionQueue`; §245 speakers go Inactive → Active in ONE step, `PreselectionQueueService`)

| When (from activation) | Subject | Trigger | Stops / notes |
|---|---|---|---|
| Within ~10 min | *"Welcome to {{eventDisplayName}}"* (`templates/emails/welcome-speaker.html`) | `WelcomeReconcileJob`, every 10 min | Once ever (`welcome:{id}`); primary button → `/Forms/SpeakerWizard`, secondary → hub home. |
| Day 14+, then biweekly | *"…a few Get Started steps are still waiting for you"* | §250 digest via `ReminderJob` | Lists open wizard steps; CTA → `/Forms/SpeakerWizard`; stops at 100 %; skipped if only Party remains. |
| Day 14+, then biweekly (Party unanswered) | *"{{eventDisplayName}} task {{state}}: …Party…"* | `AttendeePartyReminderBuilder` | Stops on Yes/No RSVP. |
| On each deadline's due day (§2.3) | *"{{eventDisplayName}} task {{state}}: {{taskTitle}}"* | `TaskReminderBuilder` | ONE mail per task (hotel/dinner/swag 1 Oct 2026; lunch + travel 10 Jan 2027; preview deck 20 Jan; final deck 3 Feb). |
| **Weekly max**, when questions are open | *"{{eventDisplayName}}: {{openCount}} new audience {{openCountNoun}} for your sessions"* | `SpeakerQuestionDigestService` via daily job — §246 `MinIntervalDays = 7`; questions arriving inside the quiet week coalesce into ONE digest | Gate `digest-emails`; stops when no open questions. |
| Event-driven | *"Your session schedule changed: {{sessionTitle}}"* · *"…your promo graphics are ready"* · *"Your travel reimbursement has been paid"* · *"Your session evaluation — {{sessionTitle}}"* · *"Action needed: {{stepLabel}}…"* (step reset) | Respective services/organizer actions (`templates/emails/`) | Each behind its own feature gate. |

### 2.2 What they see

- **First click**: lands on **`/Forms/SpeakerWizard`**. Hub home cards: Hotel, Dinner, Speaker deadlines, My sessions, checklist (`src/CommunityHub/Pages/Index.cshtml.cs`).
- **Nav** (`NavBuilder.cs`): Speaker Onboarding wizard · Speaker Details · My Tasks · My Sessions · My Evaluations · Master-Class Q&A *(only if they run a master class)* · Help Promote/Graphics · Event-Logistics fold-out (Hotel, Dinner, Lunch, Speaker Gift, Travel) · Party. **§247: no "Am I ready?" entry** — the readiness rollup tops Speaker My Tasks; `/Speaker/Readiness` stays routable.
- **Wizard — `SpeakerWizardService` (lines 64–171), entitlement-gated** (`OrderEntitlements.cs`: ELDK-supported = polo, swag, award, hotel, travel, dinner, +pre-day lunch if speaking pre-day, +main-day lunch if speaking main day; **sponsor-self-funded = dinner + main-day lunch ONLY**):

| # | Step | Done when |
|---|---|---|
| 1 | Calendar email (optional) | `CalendarEmailSetAt` stamped |
| 2 | Speaker details | **The speaker edits the bio** (`BioLastEditedBySpeakerAt` — import never counts) |
| 3–6 | Hotel · Dinner · Swag · Lunch *(entitled only)* | Booking/signup/preference row exists |
| 7 | Help promote your sessions | Manual mark-done (`promote:` task) |
| 8 | Join Signal groups *(if in scope)* | Manual mark-done (`signal:` task) |
| 9 | Party sign-up | `PartyRsvp` row (Yes or No) |
| 10 | CoC & Privacy (always last) | `ParticipantPolicyAcceptance` row |

### 2.3 Wizard vs UNIQUE tasks

**Unique deadline tasks — `config/speaker-deadlines.eldk27.json`, seeded per speaker by `SpeakerDeadlineSeeder` (`src/CommunityHub.Core/Config/SpeakerDeadlineSeeder.cs`), keys `speakerdl:{id}:{slug}`. Reminder = ONE due-day mail; NEVER in the §250 digest (scope guard).**

| Task | Due | Gating |
|---|---|---|
| Hotel · Appreciation Dinner · Swag/Speaker gift | 2026-10-01 | Entitlement (supported speakers) — these double as the wizard steps' due dates |
| Pre-day lunch | 2027-01-10 | Master-class (pre-day) speakers only |
| **Submit travel reimbursement** | 2027-01-10 | Non-Denmark speakers only (§143) |
| **Upload preview presentation** | 2027-01-20 | All speakers |
| **Upload final presentation** | 2027-02-03 | All speakers |

---

## 3. VOLUNTEER (priority 3)

### 3.1 Email timeline (entry: public `/volunteer/signup`, 3 steps — about you + photo → per-day availability → collaboration agreement — creates a PENDING participant; **nothing emails the volunteer until you approve** them Inactive → Preselected → Active in `/Organizer/PreselectionQueue`; `src/CommunityHub/Pages/Volunteer/Signup.cshtml.cs`)

| When (from approval) | Subject | Trigger | Stops / notes |
|---|---|---|---|
| Signup itself | notification to the volunteer LEAD only | signup handler | Volunteer gets nothing yet. |
| Within ~10 min of Active | *"Welcome to {{eventDisplayName}} — info for volunteers"* (`templates/emails/welcome-volunteer.html`) | `WelcomeReconcileJob` (10 min) | Once ever; primary → `/Forms/GetStarted`, secondary → hub. |
| Day 14+, then biweekly | §250 Get-Started digest | `GetStartedDigestBuilder` | CTA → `/Forms/GetStarted`; stops at 100 %; skipped if only Party open. |
| Day 14+, then biweekly | Party reminder (`task-deadline-reminder` template) | `AttendeePartyReminderBuilder` | Stops on RSVP. |
| Due days: availability + hotel −30d; dinner/lunch/swag −21d before event start | *"{{eventDisplayName}} task {{state}}: {{taskTitle}}"* | `TaskReminderBuilder` | Offsets from `src/CommunityHub/Forms/Steps/{Hotel,Dinner,Lunch,Swag}FormService.cs` + `VolunteerWizard.cshtml.cs` (−30d availability). ONE mail each. |

### 3.2 What they see

- **First click** → `/Forms/GetStarted`. Home cards: Hotel, Dinner, Volunteer-work card (+supervisor dashboard if supervisor), checklist. **Nav**: Get Started · Profile · My Onboarding Tasks · My Availability · My Assignments · Supervisor dashboard *(supervisors)* · Logistics fold-out (Hotel, Dinner, Lunch, Volunteer Gift) · Party (`NavBuilder.cs`).
- **Wizard — `RoleWizardService` (lines 83–181)**: ① Profile (done = phone filled) → ② **Day availability** (≥1 `VolunteerDayAvailability` row — volunteer-only step) → ③–⑥ Hotel → Dinner → Lunch → Swag (entitled: polo, swag, hotel, dinner, main-day lunch — `OrderEntitlements.cs`) → ⑦ Signal groups (manual) → ⑧ Party (RSVP row) → ⑨ CoC & Privacy last (acceptance row).
- **Unique tasks**: none beyond the wizard mirrors — later shift assignments arrive via `/Organizer/VolunteerStructure` allocation, not the task catalog.

---

## 4. ATTENDEE — 2-day / Master Class (priority 4)

### 4.1 Email timeline (entry: buys 2-day ticket in Zoho Backstage; `AttendeeBackstageSyncJob` every 10 min + `ZohoWebhookDrainJob` every 1 min auto-provision an Active participant)

| When | Subject | Trigger | Stops / notes |
|---|---|---|---|
| Within ~10 min (webhook: ~1 min) | *"Action required: Choose your Master Class for {{eventDisplayName}}"* (`templates/emails/masterclass-selection-invite.html`) — **the de-facto 2-day welcome (§215/§241)** | Both sync jobs auto-send to every eligible-not-invited active 2-day attendee (`src/CommunityHub.Jobs/AttendeeBackstageSyncJob.cs`, `ZohoWebhookDrainJob.cs`) | Once per attendee — `MasterClassInviteSentAt` stamped **only on real delivery**, ring-drops retry when rings widen. Buttons: "Get started in the hub" → `/Forms/GetStarted`, "Browse the Event Hub" → `/`, plus "Choose my Master Class →" deep link. Capped by `Email:AttendeeWelcomeMaxReleaseRing` (§217). |
| Next sync, if still unselected | *"{{eventDisplayName}}: select your Master Class"* | sync job chaser | **Once ever** (`pendingmc:{email}`). |
| Immediately on choosing | *"You're confirmed: {{masterClassTitle}} — see the calendar invite…"* (with .ics) / *"You're on the waitlist: {{masterClassTitle}}"* / promotion: *"You've been moved into {{masterClassTitle}}"* | selection flow / automatic waitlist promotion | 15-min "offer" flow is RESERVED/UNUSED — promotion is direct. |
| Day 14+, then biweekly | Master-Class reminder + Party reminder (`task-deadline-reminder` template) | `AttendeeMasterClassReminderBuilder` + `AttendeePartyReminderBuilder` | Each stops on answer (confirmed seat / RSVP). §250 digest is effectively silent for attendees — their only wizard steps ARE party+masterclass (skip rule). |
| Ticket reassigned | *"Action required: Validate your Master Class for {{eventDisplayName}} (ticket re-assignment)"* | sync (§234), crash-safe, retried | — |
| Ticket cancelled | *"Your ticket for {{eventDisplayName}} was cancelled"* (`templates/emails/masterclass-cancelled-ticket.html`, **§243**) | `MasterClassEmailService.SendPendingTicketCancellationsAsync`, called by both sync jobs; 7-day sweep window | Once per cancellation (occasion keys on `CancelledAt`); seat released + waitlist promoted + login locked (§216); a reappearing ticket restores, a LATER cancellation mails again. |

### 4.2 What they see

- **First click** → `/Forms/GetStarted`. Home: Attendee area card (Master-Class booking status), checklist, Games card. **Nav**: Home · Get Started · Master Class chooser (`/Attendee` — in-hub, shows capacity/confirmed/waitlist) · Waitlist · Master-Class Q&A · Games · Party · (public read-only board at `/MasterClasses`). No logistics entries — the ticket covers food (`OrderEntitlements.cs`: Attendee = nothing).
- **Wizard — `AttendeeWizardService` (2 steps)**: ① Master-Class selection (done = **CONFIRMED** signup; waitlist does NOT count) → ② Party signup (explicit Yes/No, either completes). **No unique standalone tasks.**

---

## 5. MEDIA

- **Entry**: manual add (`/Organizer/Participants`, role Media). **Welcome** *"Welcome to {{eventDisplayName}}"* (`welcome-media.html`) via `WelcomeReconcileJob` (10 min), once ever; primary → `/Forms/GetStarted`. Then: §250 digest (day 14+, biweekly), party biweekly, due-day task reminders (hotel −30d, dinner/lunch/swag −21d).
- **Sees**: Get Started · Profile · Resources · Hotel/Dinner/Lunch/Swag · Party. Home: Hotel/Dinner/Lunch/Swag cards + crew-intro card + checklist (`Index.cshtml.cs`).
- **Wizard (`RoleWizardService`)**: Profile → Hotel → Dinner → Lunch (entitled: polo, hotel, dinner, BOTH lunches — **no swag gift**, `OrderEntitlements.cs` lines 118–125) → Signal (**broadcast-only**) → Party (§206) → CoC & Privacy. **No unique tasks.**

## 6. EVENT PARTNER

Identical machinery to Media: manual add → *"Welcome to {{eventDisplayName}}"* (`welcome-eventpartner.html`) → same digest/party/due-day cadences. Differences: Signal scope = **chat + broadcast**; entitlements = polo, hotel, dinner, both lunches (`OrderEntitlements.cs`, same case as Media). Wizard: Profile → Hotel → Dinner → Lunch → Signal → Party → CoC. **No unique tasks.**

## 7. ORGANIZER

- **No welcome email, no digest audience concern** — `WelcomeVariants` returns null for Organizer (`src/CommunityHub.Core/Email/WelcomeVariants.cs`); they receive only operational mail + due-day reminders on their own tasks.
- **Sees**: full participant surface + management group: Command Center, Dashboard, Telemetry, People/Content/Comms/SoMe/SponsorAdmin/Volunteers/Logistics/Setup, Quizzes, Impersonation Log (`NavBuilder.cs`).
- **They DO have a wizard** (`RoleWizardService.cs` line 80 handles Organizer): Profile → entitled logistics (polo, swag, hotel, dinner, both lunches — `OrderEntitlements.cs`) → Party → CoC; Signal step out of scope for organizers.

## 8. ATTENDEE — 1-day — SUSPENDED (§242)

Flag `attendee-1day-access` is **OFF by default**: 1-day tickets are still mirrored by the sync (data/telemetry complete), but **no provisioning, no `welcome-attendee-1day`, no sign-in, no party task, no reminders**; `ReconcileOneDayAccessAsync` deactivates any pre-existing 1-day-only login each run, and the party seeder/reminder builder skip inactive participants (`src/CommunityHub.Core/Reminders/AttendeeWelcomeProvisioningService.cs`, `PartyTaskSeeder.cs`). Fully reversible: flipping the flag ON restores locked-out logins that still hold an active ticket (never undoing a §216 cancellation lockout) and resumes welcome + party for new holders.

---

## 9. How to verify quickly (1–2 clicks per role)

| Role | Do this |
|---|---|
| **Sponsor** | Open the welcome mail of a Ring-1 coordinator test account → click **"Open the Event Hub — Get Started"** → confirm you land signed-in on `/Sponsor/GetStarted` with 8 steps (booth co.) incl. **Booth check-in** + **Party head-count**; then open `/Sponsor/Tasks` and check the catalog rows incl. *"Download leads and inquiries"* dated **after** the event. |
| **Speaker** | Click the welcome's primary button → `/Forms/SpeakerWizard` (10 steps, CoC last); then `/Speaker/Tasks` → preview deck 20 Jan + final deck 3 Feb + (non-DK) travel rows exist. |
| **Volunteer** | Approve a test signup in `/Organizer/PreselectionQueue` → welcome arrives ≤10 min → primary button lands on `/Forms/GetStarted` with the **Day availability** step second. |
| **2-day attendee** | Add a Ring-1 test ticket in Backstage → *"Action required: Choose your Master Class…"* arrives ≤10 min → "Choose my Master Class →" lands on `/Attendee` chooser; pick a class → confirmed mail with .ics. |
| **Media / Event partner** | Add manually → welcome ≤10 min → wizard shows Hotel/Dinner/Lunch but **no Swag** step. |
| **Organizer** | Add manually → confirm NO welcome arrives; hub shows the management nav group. |
| **1-day (suspended)** | Add a 1-day test ticket → confirm NO mail, and the mirrored row appears in telemetry only. |
| **§250 digest** | Back-date a test account's `WelcomeWithLoginSentAt` 15 days with an open non-party wizard step → run `ReminderJob` → digest arrives; complete all steps → next run sends nothing. |

*Traced 2026-07-07 on `feat/jul07-roleflow-corrections`. Key sources: `templates/emails/*.html`, `src/CommunityHub.Core/{Reminders,Email,Forms,Entitlements,Navigation,Settings,Integrations}/`, `src/CommunityHub.Jobs/`, `config/{sponsor,speaker-deadlines,event}.eldk27.json`.*
