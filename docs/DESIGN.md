# Community Event Hub (CEH / ELDK27) — Design

This is the **single design document** for the Community Event Hub: how the built system
works — its architecture, data model, integrations, jobs, email, build, infra, deploy, and
operational runbook. It absorbs the former CONCEPT / RUNBOOK / build / design-notes / parity
material into one place. For *what we still want to build* (backlog + status with ◻/🟡/✅
markers) see [`REQUIREMENTS.md`](REQUIREMENTS.md). For test procedures (smoke walkthrough,
Playwright suites, Pester) see [`TESTS.md`](TESTS.md). For the documentation model and the
rules about which doc owns what, see [`../CLAUDE.md`](../CLAUDE.md).

---

## Table of contents

1. [System overview](#1-system-overview)
2. [Solution shape](#2-solution-shape)
3. [Data model & storage](#3-data-model--storage)
4. [Auth, identity & embedding](#4-auth-identity--embedding)
5. [Jobs (scheduled timers)](#5-jobs-scheduled-timers)
6. [Integrations](#6-integrations)
7. [Email system](#7-email-system)
8. [Feature surface (hubs & organizer areas)](#8-feature-surface-hubs--organizer-areas)
9. [Cross-cutting decisions](#9-cross-cutting-decisions)
10. [Build & local dev](#10-build--local-dev)
11. [Infrastructure (Bicep) & environments](#11-infrastructure-bicep--environments)
12. [Deploy, rollback & zero-downtime](#12-deploy-rollback--zero-downtime)
13. [Governance, branching & publishing](#13-governance-branching--publishing)
14. [Dev → Prod parity](#14-dev--prod-parity)
15. [Operational runbook](#15-operational-runbook)
16. [Testing strategy](#16-testing-strategy)
17. [Configuration & Key Vault reference](#17-configuration--key-vault-reference)

---

## 1. System overview

CEH is an evergreen, multi-community **ASP.NET Core 8 (Razor Pages)** participant portal plus an
**Azure Functions** job host, sharing one domain library and one Azure SQL database. Every
participant logs in with an emailed PIN, lands on a role-personalized hub, self-services their
pre-event obligations; organizers run the event from an admin hub; sponsors get a company-scoped
portal; timer jobs pull upstream systems and send reminders.

It is a **role-personalized crew-management web app** for a Microsoft-community conference
(first edition: Experts Live Denmark 2027 / **ELDK27**, 9–10 February 2027 at Bella Center
Copenhagen, pre-day 8 Feb). It replaces a previous spreadsheet + PowerShell + Microsoft Planner
workflow with a single database-backed self-service hub: speakers/volunteers/sponsors see and
manage their own submissions, organizers get one overview instead of five, and data syncs to
subsystems (Backstage / ERP / webshop) rather than living in email threads.

**Evergreen, not per-year.** Application code, repo, Azure resources and namespaces are all
`CommunityHub` / `community-hub` — never `eldk27-hub`. A new edition or community is a **new
`Event` row + JSON config**, never a fork. Three distinct names that must not be conflated:

- **Code / infra name = `CommunityHub`** — namespaces, projects, `CommunityHubDbContext`, the
  Bicep base name. Fixed; not renamed per community. (`EventHub` was rejected — it collides with
  the Azure Event Hubs service.)
- **Product display name = "Community Event Hub (CEH)"** — shown in the layout chrome only.
- **Community name = `Event.CommunityName` data field** (e.g. "Experts Live Denmark") — per-edition
  data, never hard-coded.

The year appears in exactly two places: user-facing labels (the active event's display name) and
the frontend hostname. Benefits: ELDK28 is a new *row*, not a new deployment; past data stays
queryable; a returning speaker is the same person across editions; one codebase forever.

**Project history (why Azure).** Zoho Backstage (event-marketing only, can't host crew logic) and
Zoho Creator (low-code, prototyped then dropped) were both rejected; the user wanted to own 100%
of the code in VS Code, true IaC, simple PIN auth, and reusability by other communities. A
WordPress-plugin-on-the-live-webshop option was rejected because a plugin bug could take down
payments. Azure isolates the app, gives first-class Bicep IaC, and is existing home turf. There is
**no fallback system** — this Azure app is the only solution.

**Estimated cost:** ~€25/month per instance (~€50/month dev + prod combined).

The private repo (`eldk-community-event-hub`) carries the real config; the public mirror
(`community-event-hub`) is the sanitized template (§13).

---

## 2. Solution shape

| Project | Type | Role |
|---|---|---|
| `src/CommunityHub.Core` | class library | EF Core `CommunityHubDbContext`, domain entities, services, integrations, email, auth — shared by web + jobs |
| `src/CommunityHub` | ASP.NET Core Razor Pages (+1 MVC API controller) | the web hub (participant / sponsor / organizer pages, Leads API, `/health`) |
| `src/CommunityHub.Jobs` | Azure Functions v4 isolated worker (timer triggers) | scheduled pulls, reconciliation, reminders, watchers |
| `tools/CommunityHub.OneShot` | console CLI | run one job once locally against the same services |

`CommunityHub.Core` is organized into `Config/` (typed config loader + schema validation),
`Domain/`, `Data/` (DbContext + migrations), `Auth/` (PIN generation/verification/session + the
`IIdentityProvider` seam), `Email/` (Brevo SMTP sender + template engine), `Reminders/` (stateless
reminder evaluation), and `Integrations/` (WooCommerce, Company Manager, Zoho, Sessionize clients).

Entry points:
- `src/CommunityHub/Program.cs` — SQL connection composed from a Bicep template + KV password;
  cookie auth `SameSite=None; Secure` for the Backstage iframe; `da-DK` culture; CSP
  `frame-ancestors`; an X-Frame-Options stripping middleware; `ActiveEventNameProvider` DI.
- `src/CommunityHub.Jobs/Program.cs` — same wiring; the Backstage exhibitor API is DI-swapped by
  `TestMode`.

The staged build (8 stages + post-stage features) is **all built and statically reviewed**; the
historical staging plan lives in the source CONTEXT material and is not repeated here as a backlog
(see `REQUIREMENTS.md` for outstanding work).

---

## 3. Data model & storage

- **Azure SQL via EF Core 8**, single `CommunityHubDbContext` (~26 DbSets). Every entity FKs to
  `Event` by `EventId` → multi-edition with no schema change. Participant identity = unique
  `(EventId, Email)`.
- **Migrations are the DB-as-code** (~18 EF migrations), applied **out-of-band**, not at startup.
- **Idempotency guards:** unique `SentReminder(EventId,Recipient,Type,Occasion)`; filtered-unique
  `SponsorLead(EventId,ZohoRecordId)`. FKs use `NoAction`/`Restrict` to avoid multi-cascade-path
  errors.

### Core entities
- **`Event`** — one row per edition (the multi-event design). Carries community identity, dates,
  venue, hotel, form deadlines, the lock date, role list, sponsor rules.
- **`Participant`** — one row per person per edition. Fields cover: Name, Email (login key),
  Gender, Mobile, Role, IsActive, MS accreditation (MVP/Expert/RD/MS Employee), award, polo
  request/size, jacket size, verification, packed status, plus `SponsorCompanyId` (external
  Company Manager id, for sponsor scoping). 8 roles, single-role-per-person: Organizer, Speaker,
  Volunteer, Sponsor, Speaker-Sponsor, Video, Photography, VIP — plus **Attendee** added later
  (§6 Zoho) as a sixth participant audience.
- **`LoginPin`** — PIN-hash storage, 15-min expiry, single-use.
- **`HotelBooking`** — modeled as a **check-in/check-out date range** (not per-night columns).
  Nightly room occupancy is *computed* from the range for the hotel room-night forecast; a double
  room shared with a partner counts as **one room**. Carries occupancy (single/double), partner,
  confirmation number (organizer-editable, crew read-only).
- **`DinnerSignup`**, **`VolunteerAvailability`** — the two other self-service form entities.
- **`ParticipantTask`** — rich task model (title, T-day, date, start/end, criticality, responsible
  team, assignee, owner, resources, shift-or-deadline, due date, priority, status, notes, **Link**,
  `AssignedContactId`, `SponsorCompanyId`). Tasks show in the assignee's hub sorted by due date.
- **`SentReminder`** — the reminder idempotency ledger (recipient + type + occasion).
- **`Attendee`** — Zoho-synced master-class reconciliation rows + booking status.
- **`SponsorOrder`**, **`SponsorLead`** — WooCommerce orders and Zoho CRM leads.
- **`SurveyResponse` / `SurveyResponsePick`** — public survey persistence (FK + cascade).
- Note: the hub has **no company entity** — `SponsorCompanyId` is just the external Company Manager
  id carried for scoping; company facts are read from Company Manager (§6).

### Other stores
- **Survey definitions** as JSON (`src/CommunityHub/App_Data/Surveys/*.json`, loaded + cached by
  `SurveyDefinitionProvider`); editing them needs no code change or migration.
- **Per-edition config** `config/*.<edition>.json` (§17).
- **Runtime uploads + hosted assets in Azure Blob Storage**; SQL holds structured data + URL
  references only, **never binaries**. Three file classes, three homes:
  1. Settings & templates → in the repo, version-controlled (`config/*.json`,
     `templates/emails/`, `templates/assets/`).
  2. Runtime-uploaded files (speaker manual, volunteer handbook, booth artwork, venue map) →
     Blob Storage (private container for crew uploads, public container for hot-linked assets like
     the logo); DB stores only the blob URL/key.
  3. Generated exports (room-night forecast, organizer CSVs) → transient, streamed on demand.

### Seed data
Per-edition seed reads the JSON config (one `Event` row + role list + deadlines). Dev is seeded
with the ELDK27 event + a small set of per-role test participants; **prod gets the real Event row
and real participants via the import paths (Sessionize / WooCommerce / Zoho), never the dev test
participants**.

---

## 4. Auth, identity & embedding

**PIN by email.** Crew never get Azure AD or WordPress accounts.
- User enters email → if it matches an **active** `Participant`, the app generates a
  cryptographically random **6-digit PIN**, stores a **PBKDF2-SHA256 salted hash** with a
  **15-min expiry**, emails the plaintext.
- PIN requests are **rate-limited (5/hour per email)**; the request returns a neutral message so
  the endpoint cannot enumerate registered emails.
- Verification is constant-time; PINs are single-use, expiry-enforced, and **locked after 5 wrong
  tries**. PINs are never logged in plaintext.
- A **magic-link / PIN auto-login URL** is valid 7 days. *(Known defect: the magic-link path omits
  the `EventId` claim — see `REQUIREMENTS.md`.)*
- Session = a signed ASP.NET Core auth cookie carrying identity + role + an **`EventId` claim**.

**Request/identity flow:** cookie principal → `CurrentParticipant.Current` → every authed page does
the `me is null → /Login` guard and scopes queries by `EventId`. Organizer pages add an in-handler
`Role == Organizer` gate. Sponsor pages scope to the contact's `SponsorCompanyId`.

**`IIdentityProvider` seam.** Identity establishment is isolated behind one abstraction so a future
verified-SSO provider slots in without a rewrite. `PinIdentityProvider` is the shipped
implementation.

**Embedding in Zoho Backstage.** The hub runs inside a Backstage *Embed Widget* (`<iframe>`) on a
participants-only Backstage page (so Backstage already forces a login). Two ways to avoid a second
login:
- **Option A — verified SSO handoff** (designed as a drop-in, not built): only acceptable if the
  identity claim is cryptographically verifiable (signed token/JWT or a Backstage callback). A
  plain `?email=` is forgeable and must never be trusted.
- **Option B — one-tap PIN** (built, the default): inside the embed the participant clicks one
  "Send my code" button; the hub's security never depends on trusting the embed.

Embedding mechanics:
- `Content-Security-Policy: frame-ancestors` allows the Backstage origins (the `zohobackstage.*` /
  `zoho.*` / `zohopublic.*` / `zohoexternal.*` domains) — **not** `X-Frame-Options: DENY`, and
  never `*`. The exact origin list is the `Embedding__BackstageOrigin` app setting (§14).
- The session cookie is `SameSite=None; Secure` so it survives the cross-site iframe.

---

## 5. Jobs (scheduled timers)

`CommunityHub.Jobs` hosts timer-triggered Azure Functions (NCRONTAB, UTC). Cron expressions and
on/off switches live in `integrations.<edition>.json → scheduledJobs`; each is `Enabled`-gated,
logs, and returns cleanly. **A web app does not run on a timer** — this Functions app is the
scheduler; consumption plan (a daily job costs almost nothing). WebJobs (ties scheduler to the web
app) and Logic Apps (a separate no-code moving part) were considered and rejected.

| Function | Schedule (UTC) | What it does | Gate |
|---|---|---|---|
| `WooCommercePullJob` | 03:00 (06:00 in some configs) | Pull completed shop orders, expand into per-product sponsor tasks | `WooCommerce:Enabled` |
| `BackstageSyncJob` | 06:30 | Derive sponsors/exhibitors from completed orders, create missing Backstage exhibitor requests, email coordinator | Zoho/TestMode |
| `AttendeeReconcileJob` | 07:00 | Reconcile Zoho tickets vs Bookings, upsert `Attendee`, send the 3 chasers | `Zoho:Enabled` |
| `ReminderJob` | 08:00 | Seed speaker-deadline tasks (idempotent), then send all due reminders | always |
| SponsorLeads | hourly :15 (CRM pull 05:15, digest 06:15) | Zoho CRM lead pull + digest | gated off by default |
| SponsorUploadWatch | every 15 min | Watch per-sponsor SharePoint upload folders | SharePoint |

**Reminders are NOT daily.** The timer *wakes* daily so weekly/milestone rules can be checked, but
whether anything *sends* follows the per-type cadence in `content.<edition>.json → reminders`:
- Speaker milestone deadlines — fire relative to each of the 4 deadline dates (7/1/0 days before +
  overdue).
- Speaker pending-tasks digest — weekly, configured weekday, only if open tasks exist.
- Sponsor overdue chase — weekly, configured weekday.
- General task deadlines — milestone (3/1/0 days), then a **weekly** chase once overdue (not daily).
- Incomplete-form chaser — weekly, from a configured number of days before the form deadline.

**Stateless & idempotent.** The job does not pre-queue emails. Each run it re-evaluates "is today a
send day, and who is due?", sends, and records what it sent in `SentReminder` keyed by
recipient + type + occasion (the deadline mark, or ISO week for weekly). A missed run self-heals on
the next run; nothing sends twice.

`TestMode` makes all upstream integrations **read-only** in DEV (no writes to Zoho / Woo / Company
Manager / Backstage / Bookings).

---

## 6. Integrations

All integrations are **optional + config-gated** (an `enabled` toggle in
`integrations.<edition>.json`) and secret-name-only (values in Key Vault). A community without a
given integration runs fine on its fallback path.

### WooCommerce (sponsor orders)
- Read-only REST pull from `https://your-wordpress-site.example/wp-json/wc/v3/orders` (status=completed,
  paginated 100/page). Upsert into `SponsorOrder` keyed on order number + product id.
- Each order carries **`_cm_company_id`** order-meta → links the order to its Company Manager
  company; this replaces the old fragile approver-meta correlation.
- **Category-driven product classification** (`sponsor.<edition>.json → productClassification`),
  not fixed product IDs — ELDK27 made each booth its own product (~30 booth products). A product is
  classified by its WooCommerce `Categories` string (name as fallback) into: **booth** (`Tier
  Packages With Exhibitor Booth`, or a `Booth E-NN` name code), **session** (`Sessions`),
  **brandedFeature** (Fruit/Popcorn/Coffee/Name Badge/Attendee Bag/Keynote Video/etc.), **preday**
  (`Pre-day`), or **addon** (Booth Options/Package Handling/etc. — logistics, **no tasks**). Silver
  (`Tier Packages Without Booth (Digital Only)`) matches no booth/session rule → baseline set only.
  A product may match several types; each adds its task set. New booths next year need no config
  change.
- **Task expansion** (`SponsorTaskExpander` + `taskSets` + `deadlineRules`): every sponsor gets the
  baseline `allSponsors` set; booths get a shared `booth` base **plus** a tier set
  (`boothPlatinum`/`boothDiamond`/`boothGold`). Tier read from the category suffix (no suffix →
  Gold). Real per-tier differences: wall size (Platinum 6m / Diamond 5m / Gold 4m / Feature 3m) and
  pre-keynote video (Platinum 3 images, Diamond 1, Gold none). Per-tier wall-spec PDF URL + coupon
  resolved by tier (`boothWallSpecs.tiers`). Deadlines: mostly event-date − N; logo/description are
  order(contract)-date + N with a now+N fallback.
- **Idempotent:** task `SourceKey` = `woo:{orderId}:{productId}:{taskTitle-slug}`.
- Tasks are **per-company**, not per-contact — all contacts of a company see all that company's
  tasks. Never embed an order id, product name, or contact email in a task description.

### Company Manager (companies / contacts / roles)
- A custom WordPress plugin on your-domain.example that **owns** sponsor company/contact/role data; the
  hub reads it as source-of-truth, never re-implements it.
- REST base `https://your-wordpress-site.example/wp-json/company-manager/v1`, HTTP Basic (a WP application
  password in Key Vault). Endpoints in use: `GET/POST /companies`, `GET/PUT /companies/{id}`,
  `POST /companies/{id}/users`, `GET/PUT /users`.
- Company fields include `erp_customer_number`, `corporate_identification_number` (CVR), currency,
  VAT zone, billing block, `default_signer_id`, `event_coordination_default_contact_id`.
- **Roles:** Role 1 = Signer (`default_signer_id`), Role 2 = Event Coordinator
  (`event_coordination_default_contact_id`). The hub's "reminders go to Event Coordinators, never
  the Signer" rule maps directly to these.
- **Public company name** resolves via the fallback chain `company_name_public → legal → billing →
  "Company {id}"` for every sponsor-facing reference.
- Editing of contacts/roles stays in the WordPress UI; the hub reads via API (open question on
  whether to surface editing in the hub).

### SharePoint (Graph)
Per-sponsor upload folders + a watcher job (SponsorUploadWatch) that detects new sponsor
uploads. The folder listing (`SharePointUploadClient.ListFolderFilesAsync`) follows
`@odata.nextLink` — it pages through every child via the absolute next-page URL Graph
returns, so folders larger than one Graph page are listed completely (the watcher no
longer misses files past the first page).

### Zoho — Backstage / Bookings / CRM
- **Backstage** — ticket orders (per portal/event, paged); exhibitor create via the verified v3
  endpoint `POST /backstage/v3/portals/{portal}/events/{event}/exhibitor_requests` (OAuth scope
  `zohobackstage.exhibitor.CREATE`; needs `backstageExhibitor.defaultBoothCategoryId`). Creates an
  exhibitor *request* pending organizer approval. Known limitation: Backstage has no documented
  find-by-company lookup, so `ExistsAsync` returns false (treats every exhibitor as missing) — the
  coordinator email is the duplicate safeguard. The live exhibitor API is DI-swapped by `TestMode`.
- **Bookings** — master-class appointment reservations; `fetchappointment` is a quirky multipart
  POST, paged 100/run, filtered by a time window around the master-class date.
- **Attendee reconciliation** — two Zoho systems hold two halves of one fact: Backstage orders
  (who bought a `2-day` ticket) and Bookings appointments (who reserved a `master class` seat,
  status ≠ cancelled). `AttendeeReconciler` surfaces and chases **three** mismatches: (1) bought
  ticket, no master-class booking; (2) booked master class, no ticket (commonly an email-mismatch);
  (3) booked >1 master class (`BookingStatus = MultipleBookings`, listing all bookings with cancel
  links). Records are **synced into the hub DB** (`Attendee` table, refreshed each run) so the
  attendee hub is fast and `SentReminder` can dedup; the hub **deep-links** to Zoho Bookings to act
  but never re-implements seat reservation/capacity/waitlists. No auto-merge of identities — a human
  resolves "same person, two emails".
- **CRM** — lead pull (gated off by default) into `SponsorLead`.
- Zoho is the **EU** data centre — token endpoint `accounts.zoho.eu`, API `zohoapis.eu`, OAuth
  refresh-token based. Config: a `zoho` block with the EU API domain, Backstage portal id + event
  id, the Bookings service-name regex and 2-day ticket-class regex, and KV secret names.

### Sessionize (speaker import)
**File-based**, not an API — no network dependency, nothing to configure. The organizer exports the
accepted-speaker list to `.xlsx` and uploads it at `/Organizer/SessionizeImport` (max 5 MB).
`SessionizeExcelParser` (ClosedXML) locates columns by header name (Email / First Name / Last Name /
Tag Line), any order; `SessionizeImportService` upserts `Participant` rows as Speakers (match on
email, update name, never delete, never change role) and sends the welcome email to new speakers.
Rows with no email are skipped + reported. Imported speakers are all role Speaker; reclassifying a
MasterclassSpeaker is a manual organizer action.

### Brevo (email transport)
SMTP relay `smtp-relay.brevo.com:587` STARTTLS, sender `info@your-domain.example`. The SMTP **username is
a Brevo-issued login (e.g. `8xxxxxx@smtp-brevo.com`), not the account email**; credentials in Key
Vault. See §7.

---

## 7. Email system

All email renders through a small **template engine** (`EmailTemplateRenderer` +
`EmailTemplateProvider` + `BrevoEmailSender` behind the `IEmailSender` seam). An email = a branded
`_layout.html` table-based shell + a per-type content template dropped into it. The renderer splits
the `Subject:` first line and substitutes `{{token}}` placeholders (e.g. `{{firstName}}`,
`{{taskTitle}}`, `{{dueDate}}`, `{{taskListHtml}}`, `{{roleGuidance}}`, `{{boothWallSpecUrl}}`); an
unknown token is left blank and logged, never a crash. Templates live in `templates/emails/`
(version-controlled config, not code — see `templates/emails/README.md` for the token list and
Outlook-safe rules). Email images must be served from a **public Blob URL** (email can't embed repo
files); `event.<edition>.json → community.logoUrl` points there.

**Reminders are fully settings-controlled across four layers** (on/off, cadence, wording,
recipients) with zero code changes:
- **On/off** — each reminder type in `content.<edition>.json → reminders` has an `enabled` flag.
- **Cadence** — weekly vs milestone, weekday, day-offsets, overdue behaviour (same JSON block).
- **Wording** — each reminder names a `template`; editing the `.html` edits what it says.
- **Recipients** — a `recipients` block per reminder: `primary` (keyword `subject` = the person
  the reminder is about); `cc`/`bcc` arrays of literal addresses or keywords (`organizers`,
  `owner`); `replyTo`; and `escalateTo` + an escalate trigger (`escalateAfterDaysOverdue` /
  `escalateAfterFormDeadline`). Keywords resolve at send time; an unresolvable recipient is skipped
  and logged. Sponsor reminders expand to **every** Event Coordinator (Company Manager role 2);
  Signers never get task reminders; booth staff only for tasks assigned to them.

**Built-and-wired emails today:** PIN sign-in code (inline), task-deadline reminder
(`task-deadline-reminder.html`), the three attendee chasers (missing-booking / missing-ticket /
duplicate-booking), the welcome email (`welcome.html`, role-aware `{{roleGuidance}}`, idempotent
via `SentReminder`, triggered on Sessionize import of new speakers **and on the manual
participant-create / send-welcome path** — see §8), the magic-link **invitation** (`invitation.html`,
sent by `/Organizer/SendInvitations`), the manual **task reminder** (`task-manual-reminder.html`,
sent by `/Organizer/SpeakerReminders`), and the **travel-reimbursement-paid** confirmation
(`travel-reimbursement-paid.html`, sent by `/Organizer/TravelReimbursements`). These last three were
the former hand-rolled-HTML holdouts; they now render through the shared `EmailTemplateProvider` so
branding is consistent and multi-tenant-clean (no hard-coded team sign-off or brand colour in C#).
Template files present but not yet wired to every sender: `incomplete-form-chaser.html`,
`speaker-deadline-reminder.html`, `speaker-pending-tasks.html`, `sponsor-overdue.html` (these route
through the same engine + `SentReminder` dedup once wired).

**Change notifications:** editing a hotel/dinner/shift record **after** the event lock date emails
the organizers flagged `[LATE CHANGE]`; edits before the lock date send nothing.

---

## 8. Feature surface (hubs & organizer areas)

**Welcome** — a one-time per-participant landing page per edition.

**Role hubs** (each personalized by `ParticipantRole`):
- **Speaker** — tasks + overdue-only reminders; collect/maintain hotel (with .ics), appreciation
  dinner (with .ics), swag/polo, travel-reimbursement claim, lunch participation, speaker info
  (accreditation, country, first-time). 4 generated milestone deadline tasks (validate Backstage
  profile, update Sessionize, first PPT draft, final presentation) feed the reminder engine.
- **Volunteer** — interest sign-up (unconfirmed); a confirmed-only view (congrats mail, hotel,
  dinner, swag, lunch, assigned tasks with sync-to-calendar). The in-hub shift form is the single
  3-step `/Forms/VolunteerWizard` (shifts → role/hours → review), which writes `VolunteerAvailability`
  and wires the follow-up tasks; the older single-page `/Forms/Volunteer` was retired (2026-06-14) so
  there is one canonical path. The public, login-free `/volunteer/signup` page remains the external
  recruitment entry point.
- **Sponsor** — `/Sponsor/Index`: a contact marks tasks complete/reopen, scoped to their
  `SponsorCompanyId`; any contact of a company can complete that company's unassigned company-level
  tasks; a sponsor with no `SponsorCompanyId` sees a "contact the organizers" message.
- **Attendee** — pre-day master-class status + remove-booking + deep-link to Zoho Bookings.

**Organizer hub** (in-handler `Role == Organizer` gate):
- Participants (`/Organizer/Participants`) — filter by role/active, toggle `IsActive` (blocks login).
  `/Organizer/EditParticipant` adds/edits one person: in addition to name/email/role/active it now
  exposes the **`SponsorCompanyId`** field (set or clear the Company Manager / WooCommerce company a
  sponsor contact belongs to, for sponsor-area scoping), and a **welcome hook** — a "Send the welcome
  email now" tick on create plus a "Send/Resend welcome email" action on edit. Both route through
  `WelcomeEmailService.SendWelcomeAsync`, which is idempotent via the `SentReminder` ledger (same
  guarantee as the Sessionize import path), so a person is never welcomed twice.
- Data grids — `/Organizer/DataGrid` (Participant + HotelBooking inline edits, CSV export) and
  `/Organizer/TasksTable` (task inline edits, CSV export) via `CsvWriter`. Attendee data is
  intentionally **not** editable (Zoho-synced).
- Dashboard (`/Organizer/Dashboard`) — `ReportingService` `DashboardReport`: form-completion rates,
  participants by role, task status + overdue count, sponsor completion, attendee-mismatch count,
  volunteer shift coverage, pending volunteer applicants, survey summary. CSS bar charts, no chart
  library.
- Sessionize import (`/Organizer/SessionizeImport`) — §6.
- Domain management areas (per the concept): speakers, sponsors, volunteers, organizer tasks, hotel
  (rooming-list export + confirmation import + updated calendar invite), travel reimbursement,
  swag, Bella group event (lunch/dinner overviews, booth overview), group photos, app-game sponsor
  participation.

**Hotel room-night forecast** — distinct feature: from all bookings' check-in/check-out ranges,
compute per-night room counts (double-with-partner = 1 room) and export the date→rooms table for
the hotel.

**Public anonymous pages:** `/volunteer/signup` (`[AllowAnonymous]`, creates a pending
`Volunteer` with `IsActive=false`; honeypot + plausible-email + `(EventId,Email)` dedup; organizer
Approve/Decline), and `/survey/{slug}` + `/survey/{slug}/results` (JSON-defined survey catalog,
3-step wizard, server-validated picks, public weighted-results dashboard for Call-for-Speakers).

---

## 9. Cross-cutting decisions

- **Email gated everywhere** — DEV `RedirectAllTo` redirects ALL outbound mail to the DEV test
  address; PROD uses an `OnlySendTo` allowlist. This gating is CEH-only — do not generalize.
- **Embedding** — CSP `frame-ancestors` + `SameSite=None; Secure` to run inside the Backstage iframe.
- **Resilience** — EF retry for Azure SQL serverless cold-start; cheapest tier + auto-pause.
- **Zero-downtime prod** — S1 plan + staging slot, deploy → warm-up → swap; DEV is B1.
- **Secret hygiene** — Key Vault only; PINs/keys hashed; a raw API key is shown once.
- **Config as JSON** — every event-/community-/edition-specific value lives in JSON; no value in a
  config file may also be hard-coded; config is validated on load against its `_schema` key and
  fails fast; secrets are KV references by name only.
- **No fragile deps** — own all the code; integrations are external REST clients, optional + gated.
- **GDPR** — crew/sponsor PII lives in Azure SQL (DK/EU); keep SQL private, enforce TLS, restrict
  firewall, never export PII to repo or logs.
- **Legacy automation is not a hub task** — flows the prior-year PowerShell already automates
  (Webshop→Backstage sponsor/exhibitor sync, ERP customer/contact sync, currency check,
  master-class reconciliation) must not be surfaced as sponsor "tasks". The legacy reference
  material is the source-of-truth for editorial decisions (task lists, classification rules), not
  the hand-written JSON.

---

## 10. Build & local dev

**Prerequisites:** .NET 8 SDK; EF Core tools (`dotnet tool install --global dotnet-ef`); Azure CLI
+ Bicep (`az bicep install`); a SQL target (Azure SQL or LocalDB); Azure Functions Core Tools.

**Build:**
```bash
dotnet restore CommunityHub.sln
dotnet build CommunityHub.sln
```

**Local config (never committed)** — `src/CommunityHub/appsettings.Development.json` (git-ignored):
```json
{
  "Sql": {
    "ConnectionStringTemplate": "Server=(localdb)\\MSSQLLocalDB;Database=CommunityHub;TrustServerCertificate=True;",
    "AdminUser": "",
    "AdminPassword": ""
  },
  "Email": { "SmtpUsername": "<brevo-smtp-username>", "SmtpKey": "<brevo-smtp-key>" },
  "Embedding": { "BackstageOrigin": "" }
}
```
For LocalDB the template uses integrated auth, so `AdminUser`/`AdminPassword` can stay empty. The
Jobs project takes the same settings via `local.settings.json` (also git-ignored).

**Schema:**
```bash
cd src/CommunityHub.Core
dotnet ef migrations add <Name> --startup-project ../CommunityHub
dotnet ef database update --startup-project ../CommunityHub
```

**Run locally:**
```bash
dotnet run --project src/CommunityHub                 # web
cd src/CommunityHub.Jobs && func start                # scheduler (separate terminal)
```
Go to `/Login`, enter a seeded email, collect the PIN from the email (or the logs in dev), sign in.
`tools/CommunityHub.OneShot` runs a single job once locally against the same services.

> The code is written and statically reviewed; the first `dotnet build` after any large change may
> surface real errors (most plausibly the Azure Functions worker API or EF Core query translation)
> — fix before deploying. Run a real build/test, not just a parse check.

---

## 11. Infrastructure (Bicep) & environments

`infra/main.bicep` provisions the complete environment for the evergreen app into one resource
group, parameterised for `dev` + `prod` (selected by `environmentName`; names suffixed so both
coexist). The year never appears in infra — only in a DNS hostname and an `Events` row.

| Resource | Module | Purpose |
|---|---|---|
| Log Analytics + Application Insights | `monitoring.bicep` | telemetry, logs, job run history |
| Key Vault | `keyvault.bicep` | all secrets; RBAC auth (`enableRbacAuthorization=true`); soft-delete + 90-day purge protection |
| Azure SQL server + database | `sql.bicep` | structured data |
| Storage account + `uploads` container | `storage.bicep` | runtime uploads (+ a public assets container) |
| App Service plan + web app (Linux, .NET) | `appservice.bicep` | the hub |
| Functions app (consumption) + its storage | `functions.bicep` | timer scheduler |

**Two environments, shared upstream.** Only the CEH itself is split per environment; upstream
integrations are shared.

| Layer | dev | prod | Notes |
|---|---|---|---|
| Resource group | `rg-<event>hub-dev` | `rg-<event>hub-prod` | physically separate; one RG cannot affect the other |
| Web app / plan | per env | per env | DEV `B1`; PROD `S1` + staging slot |
| SQL server + DB | per env | per env | dev test data never touches prod |
| Storage account | per env | per env | uploads separate |
| Key Vault | per env | per env | separate stores (audit/RBAC don't co-mingle); same secret values typically |
| Application Insights | per env | per env | telemetry never mixes |
| Custom hostname | `dev.hub.yourevent.example` | `hub.yourevent.example` | different DNS + managed cert; bound post-deploy |
| **Zoho Backstage / Bookings** | **SHARED** | **SHARED** | same instance; dev in TestMode → read-only |
| **WooCommerce store** | **SHARED** | **SHARED** | same shop; dev TestMode → no order writes |
| **Brevo SMTP** | **SHARED** | **SHARED** | same account; dev redirects all mail to the DEV address |
| **Company Manager (WP)** | **SHARED** | **SHARED** | same WordPress identity source |

**Isolation guarantee:** dev never writes to prod CEH state, and **TestMode** (`TestMode__Enabled`
app setting, auto-set by `main.bicep` from `environmentName`, surfaced in the per-env parameter
files) means dev never writes to the SHARED upstreams — integrations READ normally but do not write
back; coordinator notifications route to the DEV test address only. Prod stays `TestMode=false`.

Promotion dev→prod is **explicit and manual** — a separate `deploy.sh prod` (or, when CI is added,
a separate `workflow_dispatch` gated by required-reviewer approval on the prod environment). There
is no automated dev→prod flow.

**Cost defaults** (idle most of the year): SQL General Purpose serverless, auto-pause after 60 min;
App Service B1 (scale up before the event, down after); Functions consumption; Storage
`Standard_LRS` Hot. Review/resize for the weeks around the event.

---

## 12. Deploy, rollback & zero-downtime

**Prerequisites:** Azure CLI (logged in to the ExpertsLive Denmark tenant with rights to create
resources in the target subscription — tenant/subscription ids are kept out of this published doc;
`deploy.sh` pins the subscription via `AZURE_SUBSCRIPTION_ID` + `az account set` so a deploy cannot
land in the wrong sub), Bicep (bundled with recent CLI), and `jq` (reads `baseName` from the param
file).

**Deploy infra:**
```bash
./scripts/deploy.sh dev --whatif     # preview, deploys nothing
./scripts/deploy.sh dev              # deploy dev
./scripts/deploy.sh prod             # deploy prod
```
`deploy.sh` creates the RG (`rg-<event>hub-<env>`) and deploys `main.bicep`. It asks for the SQL
admin password (supply via `ELDK_SQL_ADMIN_PASSWORD` env var or let it prompt — never written to a
file or committed). On success it prints the outputs (web app hostname, Functions app name, KV name,
SQL FQDN, blob endpoint). Bicep deployments are **incremental** — re-run after any Bicep change;
`--whatif` first.

**Post-deploy steps the Bicep deliberately leaves:**
1. **Store secret values** — `./scripts/set-secrets.sh <env>` prompts for each secret and writes it
   straight to Key Vault (the Bicep provisions the vault but stores no values). Skip any unused
   integration (leave blank; keep its `enabled` flag false).
2. **Bind the custom domain** — not in Bicep on purpose (needs a verified DNS record first). Create
   a CNAME in zone `your-domain.example` (`dev.eldk27.eventhub` / `eldk27.eventhub` → the deploy's
   `webAppHostname`.azurewebsites.net), wait for propagation, then:
   ```bash
   az webapp config hostname add --resource-group rg-<event>hub-<env> --webapp-name <webAppName> --hostname <customDomain>
   az webapp config ssl create   --resource-group rg-<event>hub-<env> --name        <webAppName> --hostname <customDomain>
   ```
   Both envs use App Service managed certs (auto-renew unless CNAME/TXT verification breaks). Next
   edition (ELDK28): add `hub.yournextevent.example` / `dev.…` the same way, pointing at
   the **same** prod/dev web apps — no redeploy.
3. **Deploy the application code** — publish + zip + deploy web and jobs:
   ```bash
   dotnet publish src/CommunityHub/CommunityHub.csproj           -c Release -o publish-out/web
   dotnet publish src/CommunityHub.Jobs/CommunityHub.Jobs.csproj -c Release -o publish-out/jobs
   # Compress-Archive each to web.zip / jobs.zip, then:
   az webapp deploy                            -g rg-<event>hub-<env> -n <webApp> --src-path publish-out/web.zip --type zip
   az functionapp deployment source config-zip -g rg-<event>hub-<env> -n <fnApp>  --src publish-out/jobs.zip
   ```
4. **Run EF migrations** against the env's SQL (temporarily add the deploy machine IP to the SQL
   firewall, run `dotnet ef database update`, then remove the rule).
5. **Seed** the env's Event row.

**Zero-downtime prod:** S1 plan + a staging slot — deploy to the slot, warm it up, then swap. A bad
deploy is rolled back by swapping the slot back.

**Tear down:** `az group delete --name rg-<event>hub-<env> --yes` (KV is recoverable for 90 days via
soft-delete/purge protection).

---

## 13. Governance, branching & publishing

**Two repos.** Private `KnudsenMorten/eldk-community-event-hub` (real config) → sanitized public
mirror `KnudsenMorten/community-event-hub` (the reusable template).

**The denylist is the single authority.** `tools/publish-to-public.ps1` is the ONLY place that
decides public vs private: a `$denylist` array (private-only paths) and a `$substitutions` map
(ship a sanitized version of a private file). To keep a new file private, add its path to
`$denylist`. **Publish from the maintainer's box via the local script** (ambient git creds) — not
the stale-PAT tag-fired workflow.

**Publish workflow** (`.github/workflows/publish-public.yml`) fires on tags `public-vX.Y.Z`,
`eldk-vX.Y.Z` (team-generic), or `eldkNN-vX.Y.Z` (event-specific — works for eldk27/eldk28/… with no
workflow change). It checks out both repos side-by-side, runs `publish-to-public.ps1`, and
force-pushes the sanitized tree to public `main`. A `dryRun` workflow input shows the WhatIf plan
without pushing.

**Branch protection & CI.** `main` requires PR + ≥1 approval + `pr-validate.yml` passing, no
force-push, linear history, no admin bypass (configured once via
`tools/setup-repo-governance.ps1`, which also invites Write collaborators and verifies the publish
PAT). `pr-validate.yml` blocks obvious secret patterns, warns on stray `eldkNN` references in shared
paths, and dry-runs the publish script. Daily flow: branch → PR → review → merge → maintainer tags
a release → publish.

**Publish chain (mental model):** team member branches → PR → `pr-validate` (secret scan +
event-leak warning + publish dry-run) → review/approve → maintainer merges to `main` → maintainer
tags → `publish-public.yml` runs `publish-to-public.ps1` → force-push sanitized tree to public.

> Security note: this DESIGN doc itself publishes to the public mirror via the denylist — keep real
> secrets, subscription/tenant ids, cert thumbprints, and personal addresses out of it; refer to
> config/Key Vault by name.

---

## 14. Dev → Prod parity

Code changes reach prod via the next zip deploy. **Runtime config drift** — app settings added by
`az` CLI that the Bicep doesn't yet emit — is what bites; those are lost on the next `bicep deploy`
unless re-applied or fixed in Bicep. The live parity checklist (with per-line applied/not-applied
status) lives in [`../infra/DEV_TO_PROD_PARITY.md`](../infra/DEV_TO_PROD_PARITY.md). Substance:

- **Critical app settings missing from the Bicep template** (must be re-applied per env or fixed in
  `infra/modules/appservice.bicep`):
  - `Sql__AdminUser` — without it the app falls back to a broken default and SQL login 500s. Fix:
    emit it from the module (value = `sqlAdminLogin`).
  - `Sql__AdminPassword` (a Key Vault reference) — the template emits an empty `VaultName=;` because
    `last(split(keyVaultUri,'/'))` returns empty when the URI ends in `/`. Fix: pass a
    `keyVaultName` parameter, or derive via `split(replace(keyVaultUri,'https://',''),'.')[0]`.
  - `Embedding__BackstageOrigin` — the CSP `frame-ancestors` list; empty = `'none'` = iframe blocked.
- **EF migrations** must be applied to prod SQL (prod starts empty).
- **Seed** — prod gets the real Event row + real participants via import paths, **not** the dev test
  participants.
- **TLS custom-domain certs** — managed App Service certs are SNI-bound per env (auto-renew unless
  DNS verification breaks).
- **Deliberately NOT replicated to prod:** `TestMode__Enabled=true` (dev-only), the dev test
  participants, and any temporary dev-machine SQL firewall rule.

Once the Bicep emits the three critical settings, the parity log shrinks and future deploys stop
drifting back.

---

## 15. Operational runbook

- **Telemetry / logs** — Application Insights (named in the deploy outputs) collects requests,
  exceptions, and job run history. The reminder job is idempotent — it re-evaluates what is due and
  not-yet-sent each run, so a missed run self-heals on the next.
- **Inspect what's deployed** — `az resource list --resource-group rg-<event>hub-<env> --output table`.
- **Redeploy after a Bicep change** — `./scripts/deploy.sh <env>` (incremental; `--whatif` first).
- **Tear down an environment** — `az group delete --name rg-<event>hub-<env> --yes` (KV recoverable
  90 days).
- **Rotate compromised secrets** — any credential ever shared in PowerShell scripts/exports is
  compromised; rotate (re-issue) and store only the rotated value via `set-secrets.sh`. Never commit
  a secret — git history is forever.
- **Cost** — resize SQL / App Service for the weeks around the event; scale back after.
- **PIN/login issues** — deactivating a participant (`IsActive=false`) blocks login; the PIN flow
  checks `IsActive`.

---

## 16. Testing strategy

Always run the app/jobs in a real process before committing — a parse check is necessary but not
sufficient. The smoke walkthrough (seeded per-role accounts, end-to-end login + hub checks), the
mobile Playwright suites, and the Pester smoke tests are documented in **[`TESTS.md`](TESTS.md)**
(and the suite READMEs under `../tests/`). Mobile-first is a hard requirement: every UI change must
work at ~360px, shipped in the same commit as the desktop CSS.

---

## 17. Configuration & Key Vault reference

**Per-edition config** (`config/*.<edition>.json`, examples ship as `*.eldk27.json`):

| File | Holds |
|---|---|
| `event.<edition>.json` | edition code, community identity, dates, venue, form deadlines, crew days, role list, PIN-auth settings, organizer/sender/support emails, `community.logoUrl` |
| `hotel.<edition>.json` | official hotel, rates, occupancy, coverage rules (nights paid by role + speaker type), `personOverrides` (per-person exceptions), `roomNightForecast` (stay window + export toggle) |
| `integrations.<edition>.json` | WooCommerce, Company Manager, Zoho, SharePoint, email (Brevo) — each with an `enabled` toggle; `scheduledJobs` cron + on/off; **secret NAMES only** |
| `sponsor.<edition>.json` | category-driven `productClassification.rules`, `deadlineRules`, `taskSets`, `boothWallSpecs.tiers`, sponsor contact roles, order-import column mapping |
| `content.<edition>.json` | speaker milestone deadlines, hub resources, task vocabularies (criticality / T-days / responsible teams), reminder schedule (cadence + recipients + escalation) |
| `speaker-deadlines.<edition>.json` | dated speaker milestone tasks (seeded daily by `ReminderJob`) |

Coverage resolves: per-person override → speaker-type rule → role rule → default.
`woocommerce.enabled=false` must leave a fully working hub (sponsor module then runs from manual CSV
import only).

**Key Vault secret inventory — by NAME only** (the Bicep provisions the vault but stores no values;
`set-secrets.sh` loads them at deploy time):

| Secret name | Purpose |
|---|---|
| `sql-admin-password` | SQL admin password used at deploy time |
| `brevo-smtp-username` | Brevo SMTP login (the Brevo-issued ID, not the account email) |
| `brevo-smtp-key` | Brevo SMTP key (the SMTP password) |
| `woocommerce-consumer-key` | WooCommerce REST key (Read-only) |
| `woocommerce-consumer-secret` | WooCommerce REST secret |
| `company-manager-wp-user` | Company Manager WordPress user |
| `company-manager-wp-app-password` | Company Manager WordPress application password |
| `zoho-client-id` | Zoho OAuth client ID (attendee reconciliation / Backstage / Bookings) |
| `zoho-client-secret` | Zoho OAuth client secret |
| `zoho-refresh-token` | Zoho OAuth refresh token |

WooCommerce key must be **Read-only**. Config files contain no secrets — only KV references by name.
