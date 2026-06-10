# ELDK Community Hub — Azure Rebuild — Context & Build Plan

> **Purpose of this file.** This is the single source of truth for rebuilding the
> ELDK Community Hub as an Azure-hosted .NET application. It carries every decision,
> the rationale behind it, the architecture, the staged build plan, and the open
> questions — so the project can be picked up in VS Code with Claude as a true
> dev solution, with no loss of context from the design conversation.
>
> Read this file first in any new session before writing code.

---

## 1. What this project is

A **role-personalized crew-management web app** for Experts Live Denmark (ELDK) —
a Microsoft-community conference. It handles, for each crew member, exactly what
their role needs: hotel booking, dinner signup, volunteer shifts, profile
questions, task management, downloadable resources, and sponsor automation.

**ELDK27** is 9–10 February 2027 at Bella Center Copenhagen (pre-day 8 Feb).

### Crew roles (8, single-role per person)
Organizer, Speaker, Volunteer, Sponsor, Speaker-Sponsor, Video, Photography, VIP.
Each role sees a tailored hub: speakers get profile + hotel + dinner + deadlines;
volunteers get shifts + availability; sponsors get their deliverable tasks; etc.

---

## 2. Project history — how we got here

This is the **third and final** architecture for this system. The history
matters because each step was a deliberate decision, not churn:

1. **Zoho Backstage** — rejected. Backstage is event-marketing only; it cannot
   host custom crew forms and logic.
2. **Zoho Creator** (low-code) — was prototyped, then **dropped**. The user has
   decided not to use Zoho at all. Creator is no longer part of this project,
   not a fallback, and not maintained. It is mentioned here only as history.
3. **Azure-hosted .NET app** (THIS project — the only solution going forward) —
   chosen because the user wants:
   - Independence from the Zoho platform.
   - To own and maintain 100% of the code themselves, in VS Code with Claude.
   - True infrastructure-as-code, no fragile manual console steps.
   - Simpler auth (PIN-by-email, see §5).
   - **Reusability by other communities** — the module must be generic, with
     everything event-/community-specific in JSON config (see §11a), so another
     conference can adopt it by writing config files, not editing code.

Note: because Creator is dropped, there is **no running fallback system** until
the Azure app is built and deployed. The Stages in §8 are the path to a live
system; finishing them is what produces a working hub.

### Why Azure (not a WordPress plugin)
A WordPress-plugin-on-expertslive.dk option was considered and rejected: it would
run the Community Hub *on the live WooCommerce webshop*, where a plugin bug could take
down payments. Azure isolates the app completely. The user already has an Azure
tenant (their old PowerShell used Azure AD / Graph), so Azure is home turf, not a
new dependency. Azure also gives first-class IaC (Bicep), which is the real
"no manual steps" story.

### Honest trade-offs accepted by choosing Azure
- It is a **full application build**, not a plugin — the largest of the options.
  A plugin would inherit a DB, admin UI, user system, email for free; on Azure
  we build the app itself.
- The WooCommerce integration is an **external REST API call** (same as the old
  PowerShell did), not a local DB read.
- Small ongoing **cost** (~€15–50/month for App Service + SQL).
- A **one-time** Azure setup: someone runs the first `az deployment` with a
  subscription. This is scripted and documented, but it exists.

---

## 3. Critical design principle — MULTI-EVENT, NOT per-year

**The solution is evergreen `community-hub`. It is NOT `eldk27-hub`.**

There are two different things that change, and they must be separated:

- **Application code does NOT change per year.** Forms, roles, auth, sponsor
  logic, task logic — none of it is ELDK27-specific. Repo, app, Azure resources,
  namespaces are all `community-hub` / `CommunityHub`, with no year.
- **Data and config DO change per year.** Event dates, deadlines, hotel, sponsor
  product IDs, the roster — all per-edition.

**Therefore:** the data model has an **`Events` table** (a.k.a. editions) — one
row per year (`ELDK27`, `ELDK28`, …), each carrying its own dates, deadlines,
hotel, lock date, sponsor rules. Every year-specific record (participant
profiles, hotel bookings, tasks, sponsor orders) carries an **`EventId`**
foreign key. An "active event" setting tells the hub which edition is current.

**The community name is data, too.** The `Event` entity has a `CommunityName`
field (e.g. "Experts Live Denmark"). The codebase is generic — `CommunityHub`,
no community baked in — and another community simply seeds its own
`CommunityName`. The UI and emails show that field; the code never hard-codes
"ELDK" or "Experts Live Denmark" anywhere.

**Three distinct names — do not conflate them:**
- **Code / infrastructure name = `CommunityHub`.** Namespaces, projects,
  `CommunityHubDbContext`, the Bicep `communityhub` base name. This is fixed;
  it is not renamed per community and is not the user-facing name. (`EventHub`
  was rejected as a code name — it collides with the Azure Event Hubs service.)
- **Product / tool display name = "Community Event Hub (CEH)".** Shown in the
  graphics layout (header + footer + page title). Defined as a constant in
  `_Layout.cshtml` — display only, no bearing on code identifiers.
- **Community name = `Event.CommunityName` data field**, e.g. "Experts Live
  Denmark". Per-edition data, shown alongside the product name in the layout.

Benefits: ELDK28 is a new *row*, not a new deployment; past data stays
queryable; a returning speaker is the same person across editions; one codebase
maintained forever, never a fork per year; and a different community reuses the
same code with only config + seed data of their own.

**The year is allowed in exactly two places:** user-facing labels (the active
event's `DisplayName` shown in the UI/emails) and the frontend hostname (§4).
Even the community name is not in code — it is the `CommunityName` data field.

---

## 4. Hosting & DNS

- **App host:** Azure App Service (Linux, .NET).
- **Frontend hostname:** the user will create a DNS record
  **`hub.eldk27.expertslive.dk`** pointing at the App Service. This is the
  *per-edition label* sitting on top of the evergreen app — next year add
  `hub.eldk28.expertslive.dk` pointing at the *same* App Service with ELDK28 as
  the active event. The hostname is a label; the app is not year-bound.
- App Service needs a **custom domain binding** + **managed TLS certificate**
  for that hostname (both expressible in Bicep / one-time CLI).
- The webshop stays at `expertslive.dk` (WordPress + WooCommerce) — untouched.

---

## 5. Authentication — PIN by email

Deliberately simple. Crew never get Azure AD or WordPress accounts.

- User enters their email on the hub login page.
- If the email matches an active `Participant`, the app generates a **6-digit
  PIN**, stores a **hash** of it with a **15-minute expiry**, and emails it.
- User enters the PIN; on match-and-not-expired, a session is established.
- PINs are single-use; expired/used PINs are rejected. Rate-limit PIN requests
  per email to prevent abuse. Never log the PIN in plaintext.
- Session = a signed cookie (ASP.NET Core auth cookie) carrying the crew
  member's identity + role + active event.

This replaces the Zoho portal login entirely. It is ~200 lines of code, not a
platform.

### 5a. Embedding in Zoho Backstage, and not logging in twice

The hub will be **embedded inside Zoho Backstage** via Backstage's *Embed
Widget* (an `<iframe>`), on a Backstage "Hub" page whose visibility is set to
*Exclusive Event Participants*. So Backstage already forces a login before
anyone reaches the iframe — and the goal is for the hub not to demand a
*second* login on top of that.

Two ways to achieve that — they are **not** equally safe:

- **Option A — verifiable SSO handoff.** Backstage passes the logged-in
  participant's identity into the iframe and the hub trusts it. This is only
  acceptable if the claim is **cryptographically verifiable** — a signed
  token / JWT the hub validates against a shared secret, or a Backstage API
  the hub calls back to confirm the session. A plain `?email=` in the iframe
  `src` is **forgeable** (anyone can load the hub as anyone else) and must not
  be trusted. Whether Backstage can emit a *signed* claim is unconfirmed and
  may not be possible.

- **Option B — one-tap PIN (default).** The hub keeps its own PIN auth (above),
  but inside the embed the flow is near-frictionless: the hub shows a single
  "Send my code" button, the participant clicks once, the code arrives, they
  enter it. No password; the hub's security does not depend on trusting the
  embed.

**[DECISION] Build Option B; design Option A as a drop-in.** B works
regardless of what Backstage exposes and keeps the hub's auth self-contained
and safe. The auth code must isolate identity establishment behind one seam
(an `IIdentityProvider` abstraction): the PIN flow is one implementation; a
verified-token flow is another. If Backstage is later confirmed to support a
signed handoff, Option A is added as a second provider — **no rewrite**. Until
then, an unsigned/forgeable identity claim is never accepted.

Embedding specifics for the build:
- The hub must be allowed to render inside the Backstage origin — set
  `Content-Security-Policy: frame-ancestors` (and not `X-Frame-Options: DENY`)
  to permit the Backstage domain only, not `*`.
- The session cookie must be issued with `SameSite=None; Secure` so it
  survives inside a cross-site iframe; without it the browser drops the cookie
  and the user appears logged out on every navigation.
- Open question (§12): confirm exactly what, if anything, Backstage can pass
  to an embedded iframe — that decides whether Option A is ever viable.

---

## 6. Technology stack (recommended defaults)

These were the recommended choices; the user had not explicitly overridden them
at handoff. Confirm in the first session.

| Layer            | Choice                       | Why |
|------------------|------------------------------|-----|
| Language/runtime | **.NET (C#), ASP.NET Core**  | Microsoft-community event, team knows MS stack, best Azure support, most reliably Claude-maintainable. |
| UI               | ASP.NET Core (Razor Pages or MVC; Blazor optional) | Server-rendered keeps it simple; no separate SPA build. |
| IaC              | **Bicep**                    | Azure-native, simpler than Terraform for an Azure-only target. |
| Hosting          | **Azure App Service (Linux)**| Managed; Azure patches OS/runtime. Container Apps is more power than needed. |
| Database         | **Azure SQL**                | Managed, Microsoft-aligned. Access via EF Core. Structured data + file references only. |
| File storage     | **Azure Blob Storage**       | Runtime-uploaded files (resources, sponsor artwork) + hosted assets (logo). Never in SQL or repo (§11f). |
| ORM/migrations   | **EF Core**                  | Schema-as-code; migrations are the DB IaC. |
| Secrets          | **Azure Key Vault**          | WooCommerce keys, Brevo SMTP creds — never in code/repo. App reads via managed identity. |
| Email            | **Brevo SMTP**               | `smtp-relay.brevo.com:587` STARTTLS, sender `info@expertslive.dk`. Credentials in Key Vault. |
| Scheduler        | **Azure Functions** (timer)  | Timer-triggered jobs for reminders + WooCommerce pull (§11c). Consumption plan. |

---

## 7. Repository layout (target)

```
community-hub/                         (repo root)
├── CONTEXT.md                    this file
├── README.md
├── .gitignore
├── .vscode/
├── config/                       per-edition JSON config (§11a) — the
│   │                             reusability layer; no code, no secrets
│   ├── event.eldk27.json
│   ├── hotel.eldk27.json
│   ├── integrations.eldk27.json
│   ├── sponsor.eldk27.json
│   └── content.eldk27.json
├── templates/
│   ├── emails/                   email bodies (§11d) — config, not code
│   │   ├── README.md
│   │   ├── _layout.html          branded email shell — all emails render into it
│   │   ├── speaker-deadline-reminder.html
│   │   ├── speaker-pending-tasks.html
│   │   ├── sponsor-overdue.html
│   │   ├── task-deadline-reminder.html
│   │   └── incomplete-form-chaser.html
│   └── assets/                   logo + image sources the emails/UI use
│       └── (logo files — also uploaded to public Blob Storage)
├── infra/                        Bicep — the whole Azure environment as code
│   ├── main.bicep                App Service, SQL, Key Vault, email, domain binding
│   ├── main.parameters.json
│   └── modules/                  (appservice.bicep, sql.bicep, keyvault.bicep, …)
├── src/
│   ├── CommunityHub/                  the ASP.NET Core web app
│   │   ├── CommunityHub.csproj
│   │   ├── Program.cs
│   │   ├── Features/             Hotel, Dinner, Volunteer, Tasks, Resources,
│   │   │                         Sponsors, Hub (role landing)
│   │   ├── Pages/ or Controllers+Views
│   │   └── wwwroot/
│   ├── CommunityHub.Jobs/             Azure Functions app — timer-triggered
│   │   │                         scheduled jobs (§11c)
│   │   ├── ReminderJob.cs
│   │   └── WooCommercePull.cs
│   └── CommunityHub.Core/             shared library both apps reference
│       ├── Config/               typed config loader + schema validation
│       ├── Domain/               entities: Event, Participant, HotelBooking,
│       │                         SentReminders, …
│       ├── Data/                 EF Core DbContext + migrations
│       ├── Auth/                 PIN generation, verification, session
│       ├── Email/                Brevo SMTP sender + templates
│       ├── Reminders/            the stateless reminder evaluation logic
│       └── Integrations/         WooCommerce client (optional, config-gated)
├── scripts/
│   ├── deploy.sh                 one-command provision + deploy
│   └── seed.sh                   seed the first event from config/ JSON
└── docs/
    ├── RUNBOOK.md                one-time Azure setup, deploy, rollback
    ├── DATA_MODEL.md
    └── SPONSOR_PIPELINE.md        sponsor pipeline architecture
```

The `config-examples/` folder in this handoff package contains the five
verified ELDK27 files — copy them into `config/` as the starting set.

---

## 8. Staged build plan

Build in reviewable stages. **Each stage ends committed and runnable.** Do not
attempt the whole app in one pass.

- **Stage 1 — Repo + Bicep infrastructure. [BUILT]** Complete Bicep in
  `infra/` — `main.bicep` orchestrating modules for Log Analytics + App
  Insights, Key Vault, Azure SQL, Blob Storage, the Linux App Service web app,
  and the Azure Functions (scheduler) app. Parameterised for `dev` + `prod`.
  `scripts/deploy.sh` (creates the RG + deploys), `scripts/set-secrets.sh`
  (loads Key Vault secret values), `docs/RUNBOOK.md`. The custom-domain binding
  for `hub.eldk27.expertslive.dk` is a documented post-deploy step in the
  RUNBOOK (it needs a verified DNS record first, so it cannot live in the
  template). Outcome: a real but empty Azure environment can be provisioned
  from code. NOTE: the Bicep was structurally validated here (bracket balance,
  module param/output cross-check, parameter JSON) but **not compiled** — no
  Azure CLI / Bicep CLI was available in the build environment. Run
  `az bicep build --file infra/main.bicep` (or `deploy.sh dev --whatif`) once
  before first real deploy to catch anything a compiler would.
- **Stage 2 — App skeleton + data model. [BUILT]** Three-project solution
  (`CommunityHub.sln`): `CommunityHub.Core` (shared library — domain entities, the EF
  Core `CommunityHubDbContext`, the `IIdentityProvider` auth seam), `CommunityHub` (the
  ASP.NET Core web app skeleton — boots, `/health` endpoint), `CommunityHub.Jobs`
  (the Azure Functions worker skeleton). Domain entities: `Event` (one row per
  edition — the multi-event design of §3), `Participant`, `LoginPin` (PIN-hash
  storage, 15-min expiry, single-use — §5), `ParticipantTask`, `SentReminder` (the
  reminder idempotency ledger of §11c, with a UNIQUE dedup index on
  EventId+recipient+type+occasion). Every year-specific entity carries
  `EventId`. The `IIdentityProvider` seam (§5a) is in place so the Stage 3 PIN
  provider — and a future verified-SSO provider — slot in without a rewrite.
  `.gitignore` excludes build output and any local secret config. NOTE: as
  with Stage 1, the C# was **statically reviewed** here (brace balance,
  namespace/reference/entity cross-checks, EF navigation-property pairing) but
  **not compiled** — no .NET SDK was available in the build environment. Run
  `dotnet build` and then `dotnet ef migrations add InitialCreate` (the first
  migration is intentionally NOT generated blind — it should be created with
  the SDK present so it matches the live provider). Outcome once built: app
  runs locally, schema deploys onto the Stage 1 infrastructure.
- **Stage 3 — PIN authentication (§5). [BUILT]** `PinService` (cryptographically
  random 6-digit PIN generation, PBKDF2-SHA256 salted hashing, constant-time
  verify), `PinLoginService` (request a PIN, rate-limited 5/hour per email,
  emails the plaintext, stores only the hash with a 15-min expiry; returns a
  neutral message so the endpoint cannot enumerate registered emails),
  `PinIdentityProvider` (the first `IIdentityProvider` implementation — verifies
  the PIN, enforces single-use + expiry, locks a PIN after 5 wrong tries), the
  `IEmailSender` seam with a `BrevoEmailSender` (SMTP, STARTTLS, credentials
  from Key Vault), and the login/logout Razor Pages issuing a signed session
  cookie (`SameSite=None; Secure` so it survives the Backstage iframe — §5a).
  The web `Program.cs` wires cookie auth and emits the `frame-ancestors` CSP.
  NOTE: statically reviewed (brace balance, type cross-checks, all auth
  references resolve) but **not compiled** — run `dotnet build` first.
- **Stage 4 — Participant roles + role-based hub. [BUILT]** The role-personalized hub: a shared Razor layout, the `CurrentParticipant` accessor (reads identity/role/event from the cookie claims), and an Index that shows role-specific sections per `ParticipantRole`. The three form entities (HotelBooking, DinnerSignup, VolunteerAvailability) added to the data model.
- **Stage 5 — Forms. [BUILT]** Hotel-preference, dinner-signup, and
  volunteer-availability Razor Pages writing the three form entities, each
  enforcing the edition lock date (Event.LockDate) - the form goes read-only
  once the lock date passes. Resources/profiles deferred (not critical-path).
- **Stage 6 — Tasks + reminders. [BUILT]** The task-list page (mark
  done/undone), the `ReminderEngine` (idempotent sending via the SentReminder
  ledger - skips anything already sent, self-heals a missed run, UNIQUE-index
  guarded against double-send), the `TaskReminderBuilder` (computes due
  deadline reminders at 14/7/3/1-day milestones - a cadence, not daily), and
  the `ReminderJob` timer Function (daily 08:00 UTC). Email rendering: the
  `EmailTemplateRenderer` + `EmailTemplateProvider` load the branded
  `_layout.html` shell and the per-type content templates and substitute
  `{{token}}` placeholders; `TaskReminderBuilder` renders via
  `task-deadline-reminder.html`. NOTE: the three attendee chasers in
  `AttendeeReconcileJob` still build inline HTML - the `attendee-missing-booking`,
  `attendee-missing-ticket`, and `attendee-duplicate-booking` content template
  *files* are specified in §9z but not yet created; once those files exist the
  chasers should render through the same `EmailTemplateProvider`.
- **Stage 7 — Sponsor pipeline + WooCommerce. [BUILT]** The read-only
  `WooCommerceClient` (paged completed-order pull), the category-driven
  `SponsorProductClassifier` (booth/session/pre-day/add-on + booth tier - rule
  based, no fixed product-ID lists), the `WooCommercePullJob` timer Function
  (daily 06:00 UTC, idempotent task creation keyed on SourceKey woo:order:product),
  and the sponsor area page.
- **Stage 8 — Attendee area + Zoho reconciliation (§9z).** The `Attendee`
  entity (added to the data model in Stage 2), the Zoho integration client
  (Backstage orders + Bookings appointments, EU data centre, OAuth refresh
  flow), the `AttendeeReconcileJob` timer function, the two chaser reminder
  templates routed through `SentReminder`, the attendee role hub showing
  Master Class booking status with a deep-link to Zoho Bookings, and the
  organizer mismatch view. Optional - gated by `integrations.zoho.enabled`.
  **[BUILT]** `ZohoClient` (EU OAuth refresh, Backstage ticket orders, the multipart Bookings fetchappointment - the C# port of the source scripts), `AttendeeReconciler` (the three-way mismatch logic), `AttendeeReconcileJob` timer Function (daily 07:00 UTC - upserts Attendee rows, sends the three chasers via the dedup-safe ReminderEngine), the attendee area page (status + Zoho Bookings deep-link), and the organizer mismatch view.

New repo `community-hub`. This is the only solution — there is no Zoho fallback.

---

## 9. Feature spec — the behaviour to build

This section is the functional specification — the behaviour the Azure app must
implement. (It was validated against an earlier Zoho Creator prototype, now
dropped; the spec below stands on its own.)

### Crew profiles
Fields: Name, Email (unique, login key), Gender, Mobile, Role, Active,
MS_Accreditation (MVP/Expert/RD/Microsoft Employee), Award, Polo_Request,
Polo_Size, Jacket_Size, Verification_Completed, Packed_Status. One row per
person per event (EventId).

### Hotel — single official hotel
Official hotel **Bella Sky Copenhagen**. "Do you need a room?" Yes/No. Occupancy
Single ("just me") / Double ("me + partner"). Live cost breakdown.
**Cost model:** ELDK pays the standard stay in full (1 night for a session
speaker, 2 nights for a master-class speaker, all nights for volunteers); the
speaker pays any extra nights. Rates: 1195 DKK single / 1395 double per room for
the whole standard stay (covers both occupants). `extraNightSingle` 598 /
`extraNightDouble` 698 are **PLACEHOLDERS** — user must confirm real rates.
Crew check out on the main day, so a standard booking owes 0.

Hotel **confirmation numbers**: an organizer-editable field, crew read-only;
calendar invite (.ics) sent to the crew member.

**Hotel room-night forecast — a distinct feature.** The hotel needs an early,
then revised, count of how many **rooms** to block **per night** — they do not
care who, only the nightly room count. The hub must compute, for each night in
the stay window, how many rooms are occupied (from all hotel bookings), and let
an organizer export that date→rooms table to hand to the hotel. Two points the
data model must respect: (1) a booking spans nights, so each booking contributes
a room to every night between its check-in and check-out — model the booking as
a date range, and derive nightly occupancy from it; (2) a **double room shared
with a partner is still ONE room** — the forecast counts rooms, not guests. The
old ELDK roster encoded this as a per-night yes/no column per person; in the new
model it is computed from each booking's check-in/check-out, not stored as
columns. Config: `hotel.<edition>.json → roomNightForecast` (stay window +
export toggle).

### Dinner
Appreciation/crew dinner signup. Attending Yes/No, dietary needs, plus-one if
applicable.

### Volunteer
Volunteer profile + availability across crew days: Setup (Mon 8 Feb), Pre-day
(Tue 9 Feb… note: confirm), Main day. Shift signup.

### Crew tasks
Rich model: Task_Title, T_Day, Task_Date, Time_Start, Time_End, Criticality,
Responsible_Team, Assignee_Type, Assignee_Email, Assignee_Name, Owner_Email,
Owner_Name, Resource_Names, Shift_Or_Deadline, Due_Date, Priority, Status,
Notes, **Link**. Tasks show in the assignee's hub, sorted by due date.

### Reminders
Reminders are **not daily** — each type has its own configurable cadence
(`content.<edition>.json → reminders`; mechanics in §11c):
- General task deadlines: milestone reminders at 3/1/0 days before due; once
  overdue, a **weekly** chase (not daily).
- Speaker milestone deadlines: reminders relative to each of the 4 deadline
  dates (7/1/0 days before, plus overdue).
- Speaker pending-tasks digest: **weekly** email of a speaker's still-open
  tasks, only if they have any.
- Sponsor overdue chase: **weekly** email of a sponsor's overdue tasks.
- Incomplete-form chaser: **weekly**, from a set number of days before the
  form deadline.
Reminder emails include the task Link when present. All cadences are
config-driven and retunable without code changes.

### Speaker deadlines
4 milestone tasks generated per speaker, each with date + link:
1. Validate speaker profile on Zoho Backstage — ~25 Sep 2026
2. Update Sessionize profile — ~1 Oct 2026
3. First PPT draft due — ~15 Jan 2027
4. Final presentation due — ~15 Feb 2027
Dates were shifted +1yr from the user's ELDK26 examples — **user must confirm
ELDK27 dates and the real URLs.** These feed the reminder system. In addition
to the milestone reminders, speakers get a **weekly** digest of any pending
tasks (see Reminders above).

### Resources
Hub-hosted downloads: speaker PPT template, speaker manual, volunteer handbook,
venue map, sponsor brand guidelines. Each resource is upload-a-file OR a link.
Role-filtered (speakers see speaker resources, etc.) plus a general/all section.
Organizers manage them; others are read-only.

### Change notifications
Editing a hotel/dinner/shift record AFTER the event lock date emails the
organizers, flagged `[LATE CHANGE]`. Edits before the lock date send nothing.

### Personalized role emails
Branded HTML summary emails: speaker/volunteer/sponsor summaries + an organizer
digest. Bulk-send capability.

### Sponsor pipeline — IN THE HUB
Sponsors' deliverable tasks are generated inside the hub (the old PowerShell +
Microsoft Planner pipeline is NOT reproduced — no M365 Groups, no Planner, no
Azure B2B guest invites; sponsor tasks are normal crew tasks; sponsors log in
with the PIN like everyone else).

Product classification — **category-driven, not fixed product IDs.** ELDK27
changed the webshop model: each booth is now its own product (Platinum E-1…E-21,
Diamond E-6…E-11, Gold E-12…E-30 — roughly 30 booth products), so the old
ELDK26 approach of a fixed product-ID list no longer works. Instead the hub
classifies each ordered product by its WooCommerce `Categories` string (and, as
a fallback, its name). Config: `sponsor.<edition>.json → productClassification`.
The rules:
- **booth** — category contains `Tier Packages With Exhibitor Booth` (covers
  Platinum/Diamond/Gold, the shared-Startup and Upgrade-from-Silver variants,
  and Pre-day track sponsors which bundle a Gold booth), OR the product name
  contains a booth code like `Booth E-NN` (catches Lounge / Appreciation
  feature packages that include a booth). → booth/wall task set.
- **session** — category contains `Sessions`. → session task set.
- **brandedFeature** — the branded-feature categories (Fruit, Popcorn, Coffee,
  Name Badge, Attendee Bag, Keynote Video, Pre-day sponsor, Swag, etc.).
- **preday** — category contains `Pre-day` (these also match booth).
- **addon** — Booth Options, Package Handling, Options, Addons, Uncategorized.
  Logistics line-items, **not sponsorships — generate no tasks.**
- Silver Sponsor is `Tier Packages Without Booth (Digital Only)` — matches no
  booth/session rule, so it gets only the baseline set.
A product may match several types; each matched type's task set is added. New
booths next year need no config change — they appear under the same category.
- **Per-tier booth tasks.** A booth's tier (Platinum / Diamond / Gold) is read
  from the category suffix (`…Exhibitor Booth, Platinum` etc.; no suffix →
  Gold). Every booth gets the shared `booth` base set (wall upload, booth
  layout, register booth staff, shipment, lead-app — identical across tiers),
  **plus** a tier set: `boothPlatinum`, `boothDiamond` or `boothGold`. The real
  per-tier differences from the catalogue: the wall-design task states the
  tier's wall size (Platinum 6m / Diamond 5m / Gold 4m wide), and Platinum &
  Diamond additionally get a "submit pre-keynote video content" task (Platinum
  3 pictures, Diamond 1; Gold has no pre-keynote video). Tiers do **not** get
  wholly separate lists — that would be unmaintainable and the catalogue
  doesn't justify it; it's a shared base + small tier additions.
- **Every sponsor** also gets the baseline (`allSponsors`) task set: logo upload
  (vector + raster), Backstage onboarding, company description, attendee-bag
  insert sign-up + shipment, app-game.
- **Per-tier wall-design spec URL + coupon.** A booth sponsor's onboarding email
  must include the wall-design spec PDF they have to design their booth wall to
  ("your sponsor wall must follow the size/template below") and the webshop
  coupon. These differ by booth tier: Platinum 6m, Diamond 5m, Gold 4m, Feature
  3m — four distinct spec PDFs. Config: `sponsor.<edition>.json →
  boothWallSpecs.tiers`. Source: ELDK26's `Sponsor_Onboarding_Automation.ps1`
  selected the URL/coupon via a `switch` on fixed product IDs (1132→Platinum,
  1229→Diamond, 1233/7546/7725→Gold, 1281/1289→Feature). ELDK27 has one product
  per booth, so the hub resolves the URL/coupon by **tier** (from the category
  suffix). The wall-design task carries `{{boothWallSpecUrl}}`. The ELDK26
  URLs/coupons are placeholders in the config (`boothWallSpecs._needsUpdate:
  true`) — confirm the ELDK27 PDFs and coupon codes before go-live.
- The CSV export has empty product IDs; the hub matches **live products from the
  WooCommerce API** (where IDs exist) against these category rules. The config
  is rules, never a static ID list.
- **Deadline model:** most deadlines are event-date minus N days; logo and
  description deadlines are the sponsor's first-order (contract) date plus N
  days, with a now-plus-N fallback when no order date exists. The exact N
  values are in `config/sponsor.<edition>.json → deadlineRules`.
- **Idempotent:** never duplicate a task a sponsor already has.

### Sponsor companies, contacts & task assignment
A sponsor is a **company**, not a person. **Company/contact/role data comes from
the Company Manager plugin on expertslive.dk** (§11g) — the hub does not own it.
The model:
- A **Sponsor** mirrors a Company Manager company (keyed on its company id),
  carrying the hub's own data; company facts (name, CVR, billing, etc.) are read
  from Company Manager.
- **Contacts** and their **roles** come from Company Manager: role 1 = Signer,
  role 2 = Event Coordinator. A hub-only `booth` role may exist for booth staff
  with no Company Manager equivalent.
- Sponsor tasks can be **assigned to a specific contact** (an `AssignedContactId`
  on the task, referencing a Company Manager user id). Unassigned = the
  company's collectively.

Capabilities:
- A **coordinator can assign tasks** to individuals. Adding/removing contacts
  and changing roles happens in Company Manager (WordPress) — see the §12 open
  question on whether to also surface contact editing inside the hub.
- Sign-in: each contact logs in with their own PIN (their Company Manager email
  is the login key) and sees their company's tasks.

**Reminder targeting for sponsors** (config: `sponsor.<edition>.json →
sponsorContacts.reminderTargeting`) — implements *"all reminders go to anyone in
the Event Coordinator role, not the signer/approver"*:
- A task **assigned** to a contact → that contact is primary, **all Event
  Coordinators are CC'd**.
- An **unassigned** task → **all Event Coordinators** are the primary recipients.
- **Signers never receive task reminders.** Booth staff are reminded only for a
  task explicitly assigned to them.
- The reminder resolver (§11e) expands a sponsor reminder to *every* Event
  Coordinator (Company Manager role 2) of the company.

Stage 7 builds the Company Manager API client and the sponsor module on top of
it; the WooCommerce pull uses each order's `_cm_company_id` to attach orders to
the right company.

### WooCommerce integration
Pull completed orders from `https://expertslive.dk/wp-json/wc/v3/orders`
(status=completed, paginated 100/page). Upsert into a `SponsorOrders` table
keyed on order number + product id. Each new line triggers sponsor task
expansion. Triggers: an organizer "Pull orders" button + a daily scheduled job.
Credentials (WooCommerce consumer key/secret, **Read** permission only) live in
**Key Vault**, never in code. This is the orders API only — it does NOT
replicate the old PowerShell's WordPress approver-meta enrichment (that was
slower/fragile; add later only if needed).

---

## 9z. Attendee area — Master Class reconciliation (Zoho)

A second audience for the hub: **attendees** who bought a ticket through Zoho
Backstage and reserved a Master Class (pre-day) seat through Zoho Bookings.
This is a self-contained extension — a new role, a new integration, a new
scheduled job — that does not disturb the crew side.

### What it does

Two Zoho systems hold two halves of the same fact:
- **Zoho Backstage orders** — who bought a ticket (a ticket class whose name
  matches `2-day`).
- **Zoho Bookings appointments** — who reserved a Master Class seat (a service
  whose name matches `master class`, appointment status ≠ cancelled).

The hub reconciles them and surfaces / chases **three** mismatches:
1. **Bought a 2-day ticket but has no Master Class booking** → reminder to
   reserve a seat.
2. **Booked a Master Class but has no 2-day ticket** → reminder to buy the
   ticket (commonly an email-mismatch: the two purchases used different
   addresses).
3. **Booked more than one Master Class** → the attendee is double-booked;
   reminder listing all their bookings with cancel links, asking them to keep
   one and cancel the rest. (`Attendee.BookingStatus = MultipleBookings`.)

### Design decisions

[DECISION — new role] `Attendee` is a sixth `ParticipantRole`. The §4 role hub already
personalizes per role, so an attendee gets their own landing area showing their
Master Class booking status. Attendees sign in with the same PIN-by-email as
crew (§5).

[DECISION — data location] Attendee records are **synced into the hub DB** (an
`Attendee` table, plus a `MasterClassBooking`/booking-status field), refreshed
by the reconciliation job — not fetched live from Zoho on every page load. This
keeps the attendee hub fast and independent of Zoho API latency/uptime, and
lets `SentReminder` dedup reference real rows. Booking *status* is re-synced
each run.

[DECISION — booking stays in Zoho] The hub **shows** an attendee their booking
status and **deep-links** to Zoho Bookings (`book.expertslive.dk`) to act. It
does **not** re-implement seat reservation, capacity, or waitlists — that is
Zoho Bookings' job and rebuilding it would be major scope creep. Zoho Bookings
remains the system of record; the hub reflects it.

[DECISION — mismatch handling] The hub surfaces both reconciliation lists to
**organizers** and sends the chaser emails, but does **not** auto-merge
identities. "Same person, two emails" is resolved by a human (or by the
attendee, prompted by the chaser email) — a fuzzy automatic merge could attach
the wrong person's booking.

### Integration & job

- **Zoho integration** — a new `zoho` block in `integrations.<edition>.json`:
  `enabled`, the EU API domain (`https://www.zohoapis.eu`), the Backstage
  portal id + event id, the Bookings service-name regex and 2-day ticket-class
  regex, and Key Vault secret names for the OAuth credentials
  (`zoho-client-id`, `zoho-client-secret`, `zoho-refresh-token`). Optional and
  toggleable, like WooCommerce and Company Manager.
- **Scheduled job** — a third Functions timer job, `AttendeeReconcileJob`,
  alongside `reminderJob` and `woocommercePull`. It pulls Backstage orders and
  Bookings appointments, upserts the `Attendee` table, computes the two
  mismatch sets, and emits reminders **through the existing reminder model**.
- **Reminders fold into §11c-e** — three content templates
  (`attendee-missing-booking`, `attendee-missing-ticket`,
  `attendee-duplicate-booking`). Crucially, the PowerShell scripts have **no
  dedup** (`$ReminderEveryDays = 1` re-sends daily); the hub routes these
  through `SentReminder`, so an attendee is chased on the configured cadence,
  not every single day. The duplicate-booking reminder lists every Master
  Class the attendee booked, each with its Zoho cancel link.

### Notes from the source script

- Zoho is the **EU** data centre — token endpoint `accounts.zoho.eu`, API
  `zohoapis.eu`. OAuth is refresh-token based.
- Bookings `fetchappointment` is a quirky multipart POST, paged 100/run,
  filtered by a from/to time window around the Master Class date.
- Backstage orders are per portal/event, paged; each order has tickets, each
  ticket a contact with email — the email is the reconciliation key.
- The script's OAuth refresh token and SMTP credentials are **live secrets** —
  rotate them; only the rotated values go into Key Vault (see §11).

---

## 9y. Post-Stage features (built after the 8-stage plan)

Built in response to follow-up requests, all to documented defaults:

- **Active participant filter [BUILT]** — organizer page `/Organizer/Participants`:
  lists all participants, filters by role and by active/inactive, toggles
  `IsActive`. Deactivating blocks login (the PIN flow already checks IsActive).
- **Volunteer sign-up wizard [BUILT]** — `/Forms/VolunteerWizard`, a 3-step
  wizard (shifts → role/hours → review) writing `VolunteerAvailability`. State
  carried in hidden fields. The single-page `/Forms/Volunteer` remains as well.
- **Sponsor task JSON expansion [BUILT]** — `SponsorConfig` + `SponsorConfigLoader`
  load `sponsor.<edition>.json`; `SponsorTaskExpander` turns a classified
  product into the concrete dated task list from the config's `taskSets` +
  `deadlineRules`. `WooCommercePullJob` now expands per-product instead of
  creating one generic task. What tasks a sponsor gets is pure JSON.
- **Welcome email [BUILT]** — `welcome.html` template + `WelcomeEmailService`,
  role-aware body (a `{{roleGuidance}}` token per role). Idempotent via the
  SentReminder ledger. NOT yet auto-triggered on participant creation — the
  service exists; wiring it to the import / a creation hook is the last step.
- **Sessionize speaker import [BUILT]** — **file-based**: the organizer
  exports the speaker list from Sessionize to `.xlsx` and uploads it (no API
  endpoint, no network dependency, nothing to configure).
  `SessionizeExcelParser` reads the file with ClosedXML — columns located by
  header name (Email / First Name / Last Name / Tag Line), any order, extra
  columns ignored; `SessionizeImportService` upserts `Participant` rows as
  Speakers (match on email, update name, never delete, never change role) and
  sends the welcome email to new speakers. Organizer page
  `/Organizer/SessionizeImport` is a file upload (.xlsx, max 5 MB). Rows with
  no email are skipped and reported as warnings. NOTE: imported speakers are
  all role Speaker; re-classifying a MasterclassSpeaker is a manual organizer
  action.

- **Organizer data grids [BUILT]** - Excel-like list management. `/Organizer/DataGrid`:
  one row per person joining Participant + HotelBooking, inline-edits IsActive
  and hotel check-in/out, role + status filters, CSV export. `/Organizer/TasksTable`:
  inline-edits task title/due/state/assignee, CSV export. `CsvWriter` in Core.
  Attendee data is intentionally NOT in an editable grid - it is Zoho-synced
  and would be overwritten. A full spreadsheet grid (formulas, drag-fill) was
  deliberately not built - wrong tool for validated data; these are filterable,
  inline-editable tables instead.
- **Organizer dashboard / reporting [BUILT]** - `ReportingService` computes a
  live `DashboardReport`; `/Organizer/Dashboard` shows form-completion rates,
  participants by role, task status + overdue count, sponsor completion,
  attendee mismatch count, and volunteer shift coverage. CSS bar charts, no
  chart-library dependency.
- **Welcome email trigger [BUILT]** - `SessionizeImportService` now calls
  `WelcomeEmailService` for each newly-created speaker after save; idempotent
  via the SentReminder ledger. (A creation hook for manually-added participants
  is still a possible future addition.)
- **Speaker deadline tasks [BUILT]** - `speaker-deadlines.<edition>.json` +
  `SpeakerDeadlineSeeder`: creates a dated task per configured deadline on each
  active speaker / MC speaker. Runs daily inside `ReminderJob` (idempotent), so
  the existing reminder engine sends speaker-deadline reminders automatically -
  no separate path. Config dates are PLACEHOLDERS (_needsUpdate: true).
- **Sponsor task completion + company scoping [BUILT]** - the sponsor area
  (`/Sponsor/Index`) lets a sponsor contact mark a task complete or reopen it.
  Tasks are scoped per company: `ParticipantTask.SponsorCompanyId` and
  `Participant.SponsorCompanyId` both carry the WooCommerce order's
  `_cm_company_id` (CONTEXT.md 11g). The WooCommerce client extracts that id
  from the order meta_data; the pull job stamps it on every sponsor task. A
  sponsor sees and edits ONLY their own company's tasks; any contact of a
  company may complete/reopen any of that company's (unassigned, company-level)
  tasks. A sponsor whose Participant row has no SponsorCompanyId set sees a
  "contact the organizers" message rather than other companies' tasks. NOTE:
  the hub has no company *entity* - SponsorCompanyId is just the external id
  carried for scoping. Participant.SponsorCompanyId must be populated when a
  sponsor contact is created (no UI for this yet - an open follow-up).
- **Backstage exhibitor sync + TESTMODE [BUILT]** - `BackstageSyncJob` (daily
  06:30 UTC) derives sponsors/exhibitors from the completed WooCommerce orders,
  then `BackstageSyncService` checks each against Zoho Backstage and, if
  missing, creates it and emails the event coordinator. `LiveBackstageExhibitorApi`
  is implemented against the VERIFIED Backstage v3 endpoint
  `POST /backstage/v3/portals/{portal}/events/{event}/exhibitor_requests`
  (OAuth scope `zohobackstage.exhibitor.CREATE`; reuses `zoho.backstagePortalId`
  / `backstageEventId`; needs `backstageExhibitor.defaultBoothCategoryId`). It
  creates an exhibitor *request* - pending an organizer's approval in Backstage.
  KNOWN LIMITATION: Backstage has no documented find-by-company lookup, so
  `ExistsAsync` returns false (treats every exhibitor as missing) - the
  coordinator email is the duplicate safeguard. TESTMODE (`TestModeOptions`,
  default ENABLED): no real Zoho calls, examines only test sponsor 2LINKIT,
  routes coordinator email to mok@2linkit.net. When TESTMODE is off the real
  coordinator email is mok@expertslive.dk. Set `testMode.enabled=false` and
  configure `defaultBoothCategoryId` before go-live.
- **Public anonymous volunteer signup [BUILT]** — `/volunteer/signup` (Razor Page
  `Pages/Volunteer/Signup.cshtml`). `[AllowAnonymous]` form for the wider
  community; submits to the currently-active Event. Creates a `Participant`
  row with `Role=Volunteer` and `IsActive=false` — the "applicant pending
  review" flag. Organizer dashboard surfaces pending applicants in a
  dedicated card (Approve flips `IsActive=true`, Decline hard-deletes the
  inactive row, safe because nothing has been wired up yet). Honeypot field,
  plausible-email check, and `(EventId, Email)` dedup are at the form level.
- **Public anonymous surveys [BUILT]** — `/survey/{slug}` + `/survey/{slug}/results`
  (Razor Pages `Pages/Survey/Index.cshtml` + `Results.cshtml`). Survey
  catalog (tracks → topics → category groupings → Introduction/Advanced/Expert
  examples) lives in JSON under `src/CommunityHub/App_Data/Surveys/<slug>.json`,
  loaded + cached by `SurveyDefinitionProvider`; editing the JSON does NOT
  require a code change or a DB migration. Only responses persist to the DB
  (`SurveyResponses` + `SurveyResponsePicks`, FK + cascade). Wizard is 3 steps:
  (1) pick one track, (2) rank 3 topic picks within that track, (3) pick a
  desired level per topic with per-track example copy. Single POST on submit,
  honeypot field, server-side validation that picks belong to the selected
  track + are distinct. Public results dashboard aggregates: track popularity,
  Top-15 weighted topics overall (rank 1 = 3pts, rank 2 = 2pts, rank 3 = 1pt),
  per-track expandable breakdown, level-distribution totals — designed to be
  shared with prospective speakers (Call for Speakers). First instance is
  `/survey/eldk27-topics` (7 tracks, ~8 topics each). Organizer dashboard
  surfaces a card with response count, top track, and a link to the public
  dashboard.
NOTE: all statically reviewed, not compiled — run `dotnet build`.

## 10. Confirmed event facts (seed data for the ELDK27 Event row)

- Event short name: ELDK27
- Community name: Experts Live Denmark (the `CommunityName` field)
- Main dates: day1 2027-02-09, day2 2027-02-10; pre-day 2027-02-08
- Venue: Bella Center Copenhagen
- Official hotel: Bella Sky Copenhagen
- Organizer / sender email: info@expertslive.dk
- Form deadlines: mid-January 2027; lock date 2027-01-20
- Crew days: Setup (Mon 8 Feb) + Pre-day + Main day

---

## 11. Security requirements

- **Secrets** (WooCommerce keys, Brevo SMTP creds) live ONLY in Azure Key Vault.
  The app and the Functions app read them via a managed identity. Nothing
  sensitive in the repo. The config files hold only the secret *names*.
- **Key Vault secret inventory** — the names the config references. The Bicep
  (Stage 1) provisions the vault but stores **no values**; `scripts/set-secrets.sh`
  prompts for each value at deploy time and writes it straight to Key Vault, so
  nothing secret ever touches disk or git. Inventory:
  - `sql-admin-password` — the SQL admin password used at deploy time.
  - `brevo-smtp-username` — the Brevo SMTP login (a Brevo-issued ID, e.g.
    `8xxxxxx@smtp-brevo.com`), **not** the account login email.
  - `brevo-smtp-key` — the Brevo SMTP key (the password).
  - `woocommerce-consumer-key` — WooCommerce REST key (Read-only).
  - `woocommerce-consumer-secret` — WooCommerce REST secret.
  - `company-manager-wp-user` — Company Manager WordPress user.
  - `company-manager-wp-app-password` — Company Manager WordPress app password.
  - `zoho-client-id` — Zoho OAuth client ID (attendee reconciliation, §9z).
  - `zoho-client-secret` — Zoho OAuth client secret.
  - `zoho-refresh-token` — Zoho OAuth refresh token.
- **The credentials in the user's PowerShell scripts must NOT be reused.** Those
  scripts contained LIVE secrets — WooCommerce keys, a WordPress app password,
  an Azure client secret, Zoho OAuth tokens, Brevo SMTP credentials — and were
  shared during design, so **they are compromised**. They must be **rotated**
  (freshly issued) and only the rotated values entered via `set-secrets.sh`.
  Secret values are **never** placed in the Bicep, the JSON config, or any
  repo file — a secret committed once lives in git history forever. This is
  the exact mistake the PowerShell scripts already made; do not repeat it.
- WooCommerce API key must be **Read-only**.
- Crew data and sponsor PII live in Azure SQL — this is **GDPR-relevant** (DK /
  EU). Keep the SQL server private where possible, enforce TLS, restrict
  firewall rules, do not export PII into the repo or logs.
- PIN auth: store only PIN hashes, enforce the 15-min expiry, single-use,
  rate-limit requests, never log PINs.

---

## 11a. Configuration as JSON — the reusability layer

**Goal: this module must be reusable by other communities.** Therefore every
event-, community-, or edition-specific value lives in JSON config files, and
the application code hardcodes none of them. A new community runs the hub by
writing new config files — never by editing code.

Five config files per edition (examples ship in `config-examples/`, named
`*.eldk27.json` — a working ELDK27 set, ready to copy and adapt):

| File | Holds |
|------|-------|
| `event.<edition>.json` | Edition code, community identity, dates, venue, form deadlines, crew days, the role list, PIN-auth settings. |
| `hotel.<edition>.json` | Official hotel, rates, occupancy, **coverage rules** (nights paid by role and by speaker type), and **`personOverrides`** — the per-person exceptions, e.g. "for this speaker we pay all nights". |
| `integrations.<edition>.json` | WooCommerce + Company Manager (each an **`enabled` toggle**), email provider + settings, calendar, scheduled jobs. **Secrets are NOT here** — only Key Vault secret *names*. |
| `sponsor.<edition>.json` | Product classification rules (category-driven), named deadline rules, the task sets, sponsor contact roles, order-import column mapping. |
| `content.<edition>.json` | Speaker milestone deadlines, hub resources, task vocabularies (criticality/teams/etc.), reminder schedule. |

Design rules for the build:
- The app loads config for the **active edition** at startup (or per request).
  Multi-edition (§3) means config is selected by edition code.
- **No value that appears in any config file may also be hardcoded in code.**
  If a test would break by swapping the config, the value is in the wrong place.
- Config is **validated on load** against the `_schema` key; a bad config fails
  fast with a clear error, never silently.
- Config files contain **no secrets** — secrets are Key Vault references by name.
- Where config seeds the database (roles, the event row, deadlines), provide a
  `seed` step that reads the JSON; do not duplicate the data.
- The hotel **`personOverrides`** array is the formal mechanism for the
  "exceptions" the user asked about. Coverage resolves: per-person override →
  speaker-type rule → role rule → default.
- `integrations.woocommerce.enabled = false` must leave a fully working hub: the
  sponsor module then runs from manual CSV import only, no API.

These five example files are the verified, real ELDK27 starting set. They are
the single source for what the app's config layer must support.

## 11b. Source-data verification (Excel + PowerShell review)

The user's real files were re-examined to confirm the spec is complete. What
the review confirmed and added:

- **Roster** (`ELDK26_Logistics_COMPLETE.xlsx`, "List" sheet) — the real crew
  record is richer than first captured. Per-person fields confirmed: Name,
  Email, Gender, Mobile, Role, plus hotel detail (booking yes/no, confirmation
  number, room type, partner, check-in, check-out, total nights, ELDK-covered
  nights, own nights, own price), appreciation-dinner (participation, special
  treatment, comments), volunteer participation per crew day, volunteer-selected
  flag, **pre-day master-class participation**, Microsoft accreditation, award,
  jacket size + count, polo request + size + count, verification completed,
  packed status. The data model (Stage 2) must cover these.
  - **The per-night hotel columns** (`Hotel_21-02-2026` … etc.) are NOT
    per-person stay data to store as columns. Their purpose is the **room-night
    forecast for the hotel** — the hotel needs the nightly *room count* in
    advance. In the new model, store each booking as a check-in/check-out date
    range and *compute* nightly occupancy from it (see §9 "Hotel room-night
    forecast"). A double room shared with a partner counts as one room.
- **Detailed plan** (`ELDK26_Detailed_Plan.xlsx`, 305 task rows) — confirmed the
  task vocabularies now in `content.<edition>.json`: criticality
  (Need-to-have / Nice-to-have), T-days (T-10…T+2), and the 15 responsible-team
  values (ELDK, ELDK-Volunteers, Speaker-team, Sponsor, Expo-Sponsor, Video,
  Photo, Community Reporter, Transporter, BeFree, BC-AV, BC-Event, BC-F&B,
  BC-Cloakroom, All).
- **Hotel confirmations** (`Confirmed_Hotel_Information-v3.xls`) — confirms the
  group-confirmation-list format: Title, First/Last name, Email, Confirmation
  Number, Cancellation Number, # Guests, # Rooms; group code "4040 ELDK 2026".
  Confirmation numbers import keyed on email.
- **Webshop exports + ELDK27 product catalogue** — `Webshop-OrderLineItems`
  columns (Order, ProductId, ProductName, Quantity, CompanyNameAnnouncement)
  confirm the order mapping. The ELDK27 product CSV (`ELDK27_products.csv`)
  showed the webshop model changed: each booth is now its own product (~30
  booth products), so the sponsor pipeline classifies by the `Categories`
  column, not a fixed ID list (see §9 and `sponsor.<edition>.json`).
- **PowerShell** — the deadline-offset values in `sponsor.<edition>.json`
  (`deadlineRules`) are the real values from `Sponsor_Onboarding_Automation.ps1`.

Open data point still to confirm with the user: the hotel **stay window**. The
ELDK26 roster's per-night columns spanned 21–28 Feb 2026 (that event was late
February). ELDK27 is 8–10 Feb 2027, so the stay window in
`hotel.<edition>.json → roomNightForecast.stayWindow` (currently 7–11 Feb 2027
as a placeholder) must be confirmed against the real arrival/departure pattern.

## 11b.1. Legacy reference material — **READ THIS BEFORE EDITORIAL CLAIMS**

The user's prior-year automation + design material lives at **`C:\tmp\ceh\`** on
the dev machine. Any session that's about to add / drop / reword a sponsor task,
a speaker task, a reminder cadence, a sponsor product classification, or any
editorial behaviour MUST consult this directory first. Don't invent items; don't
take `sponsor.<edition>.json` (or any `*.eldk27.json`) at face value as
ground truth — the JSON was hand-written and has been wrong before. The source
material wins.

### Directory map

| Path | What's in it | When to read |
|---|---|---|
| `C:\tmp\ceh\automation\` | 13 production PowerShell scripts the team ran for ELDK26/27 + `zoho-mapping.eldk27.json` + `Secrets.psm1`. NO sponsor-task tracking lives here — these are integration sync (ERP ↔ Webshop ↔ Backstage, attendee reconciliation, webhook handlers). | Before claiming an integration "should exist as a hub task" — check whether the legacy script already does it automatically (e.g. `Sync-Webshop-Sponsors-to-Zoho-Backstage.ps1` already creates the Backstage sponsor + exhibitor records, so "Zoho Backstage event system onboarding" is NOT a sponsor task). |
| `C:\tmp\ceh\docs\Experts Live Community Event Hub.docx` | The Word design doc (~1000 paragraphs). Sections: Goals, Hub features per role, Organizers Hub features, Automation (scripts), and a "Sponsors to-do" page that explicitly states "**assigns tasks to sponsor company (not contact). Contacts must see all tasks for sponsor company.**" | Before declaring any feature "done" or any task list "final". The docx is .docx — extract text with `unzip -p ... word/document.xml \| sed 's/<[^>]*>/ /g'` (no pandoc on this machine). |
| `C:\tmp\ceh\hotel\Confirmed_Hotel_Information-v3.xls` | The hotel's group confirmation list — Title, First/Last name, Email, Confirmation Number, Cancellation Number, Guests, Rooms. Group code "4040 ELDK 2026". | Before specifying hotel data import / confirmation flows. |
| `C:\tmp\ceh\sessionize\experts-live-denmark-2026 flattened accepted sessions - exported 2026-05-25.xlsx` | Real Sessionize speaker export, flattened. | Before specifying Sessionize import column shape. |
| `C:\tmp\ceh\speakermanual\*.pdf, *.pptx` | Reference speaker manuals from prior events / sibling communities (ELDK26, WPN Norway). | Before drafting speaker-deadline text or instructions. |
| `C:\tmp\ceh\sponsor\ELDK27 products.csv` | The actual ELDK27 sponsor product catalogue with Categories / Name (~30 booth products + tier packages + branded features + addons). | Before editing `sponsor.<edition>.json -> productClassification.rules` or claiming a product type doesn't exist. |

### Concrete rules this section enforces

1. **A sponsor task that maps to an existing legacy automation is NOT a sponsor task.** The Webshop → Backstage sponsor / exhibitor sync, ERP customer/contact sync, webhook flows, currency check, master-class reconciliation — all of these run automatically. Don't surface them as hub tasks for the sponsor to "complete."
2. **The sponsor task LIST is editorial, not derived.** `sponsor.<edition>.json -> taskSets` is hand-curated. There is no canonical source list. When editing, default to the docx's narrative + the user's stated intent, NOT to whatever the JSON already says.
3. **Tasks are per-company.** Per docx "Sponsors to-do": *"assigns tasks to sponsor company (not contact). Contacts must see all tasks for sponsor company."* Never embed an order id, product name, or contact email in a task's Description.
4. **`zoho-mapping.eldk27.json` is the source of truth for tier classification rules.** When changing `sponsor.<edition>.json -> productClassification.rules`, the two must stay aligned (or the legacy PS and the hub will classify differently).
5. **The webshop sales-window cutoff is 2026-05-01 (ELDK27 kick-off).** Hardcoded in `Sync-Webshop-Sponsors-to-Zoho-Backstage.ps1:24` (`$OrdersAfter`). Any hub-side date-filter logic must match.

If `C:\tmp\ceh\` is not present on a given machine, ask the user where the reference material lives — don't proceed without it.

## 11c. Scheduled jobs — how reminders get sent

Reminders (task deadlines, speaker deadlines, sponsor deadlines, incomplete-form
chasers, daily overdue nudges) and the WooCommerce pull all need something that
runs on a timer. **An App Service web app does not run on a timer** — it only
runs when a request arrives. A separate scheduler component is required.

**Chosen approach: an Azure Functions app with timer triggers.** Deployed from
the same repo, provisioned by Bicep, on the consumption plan (a once-a-day job
costs almost nothing). Two timer-triggered functions:

- **`reminderJob`** — wakes once a day. Evaluates every reminder type against
  its configured cadence and sends only what is due that day.
- **`woocommercePull`** — runs once a day. Pulls new completed orders, expands
  them into sponsor tasks. Skipped entirely when `integrations.woocommerce.
  enabled` is false.

Cron expressions and on/off switches are in `integrations.<edition>.json` →
`scheduledJobs`. Alternative approaches considered and rejected: App Service
WebJobs (simpler, but ties the scheduler to the web app) and Logic Apps (a
separate no-code moving part, against the own-all-the-code goal). Functions is
the clean, IaC-friendly, in-repo choice.

**Reminders are NOT sent daily — each type has its own configurable cadence.**
The Functions timer *wakes* daily, but that is only so weekly and milestone
rules can be checked each day. Whether anything is actually *sent* follows the
per-type cadence in `content.<edition>.json → reminders`:

- **Speaker milestone deadlines** — the 4 unique speaker deadlines; reminders
  fire relative to each deadline date (7/1/0 days before, plus overdue).
- **Speaker pending-tasks digest** — a **weekly** email to each speaker listing
  their still-open tasks. Sent once a week on a configured weekday, only if the
  speaker has open tasks.
- **Sponsor overdue chase** — a **weekly** email to each sponsor listing their
  **overdue** tasks. Once a week, configured weekday.
- **General task deadlines** — milestone reminders (3/1/0 days); once a task
  is overdue it switches to a **weekly** chase, not daily nagging.
- **Incomplete-form chaser** — **weekly**, starting a configured number of days
  before the form deadline.

So the model is: daily wake, per-audience cadence, all of it config-driven so a
community can retune without touching code.

**The reminder logic is stateless and idempotent — this is the important part.**
The job does NOT pre-schedule individual emails into a queue. Each run it
re-evaluates current state and the cadence: "for this reminder type, is today a
send day, and who is due?" It sends those, and writes a record of what was sent
so the same reminder is not repeated for the same logical occasion (e.g. a
given week, or a given deadline mark). Because it reasons from "what is true
now" plus "what have I already sent," a missed run is self-healing — the next
run catches up.

A `SentReminders` (or `ReminderLog`) table backs the dedup: keyed by
recipient + reminder-type + occasion (the deadline mark, or the ISO week for
weekly reminders), so the job can ask "did I already send this?" before
sending.

This adds a Functions app to the repo layout (`src/CommunityHub.Jobs/` or similar)
and to the Bicep. The web app and the Functions app share the same database and
the same config and email code (factor email + reminder logic into a shared
project both reference).

## 11d. Reminders are fully settings-controlled

Everything about reminders is controlled without code changes — the goal
"control reminders in a settings file" is met across four layers (on/off,
cadence, wording here in §11d; recipients in §11e):

1. **On/off** — every reminder type in `content.<edition>.json → reminders` has
   an `enabled` flag. A community can switch any reminder off entirely (e.g. a
   community with no sponsors sets `sponsorOverdueReminder.enabled = false`).
2. **Cadence** — `weekly` vs `milestone`, the weekday, the day-offsets before a
   deadline, the overdue behaviour — all in the same JSON block.
3. **Wording** — each reminder names a `template`; the email subject and body
   live in `templates/emails/<name>.html`. Editing what a reminder *says* is
   editing that file. Tokens like `{{firstName}}`, `{{taskTitle}}`,
   `{{dueDate}}`, `{{taskListHtml}}` are filled by the app at send time. See
   `templates/emails/README.md` for the full token list.

Why templates are files, not inline JSON: multi-line HTML email bodies are
awkward and unreadable inside JSON. A `templates/emails/` folder keeps the
settings JSON clean while still being pure config — no code, no compilation.

Build implication: the app needs a small **template engine** — load the named
`.html`, split off the `Subject:` first line, substitute `{{token}}`
placeholders, with an unknown token left blank (and logged) rather than
crashing. The reminder job and any other email (PIN code, summaries) use the
same engine, so PIN/summary emails should also become templates for
consistency.

The `config-examples/templates/emails/` folder in this handoff ships the five
reminder templates as the starting set.

## 11e. Reminder recipients are settings-controlled

Who receives each reminder is config, not code. Every reminder type in
`content.<edition>.json → reminders` has a `recipients` block:

- **`primary`** — the keyword `subject`: the person the reminder is *about*
  (the speaker for a speaker digest, the sponsor for a sponsor chase, the crew
  member for a task reminder). This is intrinsic to the reminder and is a
  keyword rather than a free address — a speaker's pending-task digest must go
  to that speaker. The keyword indirection still keeps it out of code.
- **`cc` / `bcc`** — arrays, each entry either a literal email address or a
  keyword: `organizers` (the configured organizer email) or `owner` (a task's
  Owner_Email). Empty array = nobody. Example shipped: `sponsorOverdueReminder`
  CCs `organizers`, matching the old ELDK pipeline where sponsor task mail went
  to the sponsor contact *and* the internal coordinators.
- **`replyTo`** — an address or keyword; replies route there.
- **`escalateTo` + an escalate trigger** (`escalateAfterDaysOverdue` or
  `escalateAfterFormDeadline`) — optional. When an item is badly overdue, this
  recipient is also notified. Shipped examples: sponsor tasks >21 days overdue
  escalate to `organizers`; general tasks >7 days overdue escalate to the
  task's `owner` (mirrors the old detailed-plan escalation); the form chaser
  escalates to `organizers` once the form deadline has passed.

Build implication: the reminder job resolves these keywords at send time —
`subject` → the person's email, `organizers` → the organizer email from
`event.<edition>.json`, `owner` → the task's owner. An unresolvable recipient
is skipped and logged, never a crash. With this, a community controls reminder
recipients (and CC/escalation policy) entirely from settings.

Together, §11d and §11e mean reminders are controlled **end to end** in the
settings layer — on/off, cadence, wording, and recipients — with zero code
changes. That fully satisfies the "control everything about reminders in a
settings file" requirement.

## 11f. Where files live — the storage proposal

Three different kinds of "file" exist in this solution; they belong in three
different places. This is the proposed convention:

1. **Settings & templates — in the repo, version-controlled.**
   - `config/*.json` — the per-edition settings.
   - `templates/emails/` — `_layout.html` (the branded shell), one content
     file per email, the README. Email is built layout + content (§11d):
     `_layout.html` is the email-safe table-based branded chrome; content
     templates hold only the inner content and are dropped into the layout.
   - `templates/assets/` — source image files the emails/UI reference (logo,
     etc.). NOTE: email images must be served from a **public URL** — email
     cannot embed repo files — so these assets are also uploaded to public
     Blob Storage and `event.<edition>.json → community.logoUrl` points there.
     The repo copy is the source of truth.

2. **Runtime-uploaded files — Azure Blob Storage, NOT the repo, NOT the DB.**
   Resources uploaded through the hub (the speaker manual PDF, the volunteer
   handbook, sponsor booth artwork, the venue map) are user content. They go in
   an **Azure Blob Storage** account, with the database storing only the blob
   URL/key. Never store binary files in Azure SQL, and never commit uploaded
   content to the repo. This adds a Storage Account to the Bicep (Stage 1), and
   a private container for crew uploads plus a public container for assets that
   must be hot-linked (logo).

3. **Generated exports — transient.** The hotel room-night forecast export, any
   CSV the organizer downloads — generated on demand, streamed to the browser,
   not persisted unless there is a reason to.

So: **repo** = code + settings + templates (the things developers and
organizers edit); **Blob Storage** = uploaded and hosted files; **Azure SQL** =
structured data and references (URLs) only. The Bicep provisions the Storage
Account; `CommunityHub.Core` gets a small storage service the web app uses for
uploads/downloads.

## 11g. Company Manager — the existing companies/contacts/roles system

**Important architectural finding.** expertslive.dk already runs a custom
WordPress plugin called **Company Manager** that models exactly what §9's
"Sponsor companies, contacts & task assignment" describes — companies, their
linked contacts, and contact roles. It exposes a REST API at
`/wp-json/company-manager/v1`. The hub should **integrate with it as the source
of truth for sponsor company/contact/role data**, not re-implement it. The
sponsor contract PDF (e.g. the CodeTwo ELDK27 contract) is generated from this
same data.

### The Company Manager REST API (verified from the user's ERP-sync scripts)

Base: `https://expertslive.dk/wp-json/company-manager/v1`, HTTP Basic auth
(a WordPress application password — store in Key Vault, never in code).

Endpoints in use:
- `GET  /companies?per_page=&page=` — list companies (paged).
- `GET  /companies/{id}` — one company, including `default_signer_id` and
  `event_coordination_default_contact_id`.
- `POST /companies` — create a company.
- `PUT  /companies/{id}` — update a company.
- `POST /companies/{id}/users` — link a user (contact) to a company,
  body `{ user_id }`.
- `GET  /users`, `GET/PUT /users/{id}` — the contact (person) records.

Company object fields (from create/update bodies): `id`, `name`,
`erp_customer_number`, `corporate_identification_number` (CVR), `currency`,
`vat_zone_number`, `phone`, `web_address`, `billing_address_1`, `billing_city`,
`billing_postcode`, `billing_country`, `billing_email`, `billing_state`,
`billing_reference`, plus `default_signer_id` and
`event_coordination_default_contact_id`.

### Contact roles — already defined

Company Manager already has the role concept this project needs. Role IDs:
- **Role 1 = Signer** → a company's `default_signer_id`.
- **Role 2 = Event Coordinator** → a company's
  `event_coordination_default_contact_id`.

This **confirms and supersedes** the `sponsorContacts.roles` guess in
`sponsor.<edition>.json` — use Company Manager's real role model. The hub's
"reminders go to Event Coordinators, never the Signer" rule maps directly:
Event Coordinator = role 2, Signer = role 1.

### Orders link to companies cleanly

WooCommerce orders created under the Company Manager design carry a
**`_cm_company_id`** order-meta field — a direct link from an order to its
Company Manager company. This **replaces the old, fragile approver-meta
correlation** entirely: the WooCommerce pull (§9) reads `_cm_company_id` to
attach each order to the right sponsor company. Orders without it are legacy
(pre-Company-Manager) and out of scope for the new hub.

### What this means for the build

- The hub does **not** own sponsor company/contact/role data — Company Manager
  does. The hub **reads** companies, contacts and roles from the Company
  Manager API, and stores its own hub-specific data (task assignments, PIN
  sessions, reminders) keyed to the Company Manager company id and user ids.
- This is a new entry in `integrations.<edition>.json` — a `companyManager`
  block: `enabled`, `baseUrl`, and the Key Vault secret name for the WP
  application-password credentials. Like WooCommerce, it is optional: a
  community without Company Manager falls back to the hub managing sponsor
  contacts itself (the §9 model).
- Sponsor sign-in: a contact's email (from Company Manager `users`) is their
  PIN-login identity, same as any crew member.
- This removes the need to build a company/contact/role admin UI for sponsors
  from scratch — that already exists in WordPress. The hub's sponsor module
  becomes: read companies/contacts/roles from Company Manager, generate and
  assign tasks, send role-targeted reminders.

Open question for the user: should sponsor contact management (adding people,
changing roles) stay in the Company Manager WordPress UI, or be surfaced inside
the hub too? Reading is clearly via the API; where *editing* happens is a
genuine choice (see §12).

## 12. Open questions — confirm with the user before/while building

1. **Azure subscription** — which subscription will the `community-hub` resources
   live in, and does the user have rights to create resources? Stage 1 deploy
   needs this. (Code can be written before this is answered.)
2. **Stack confirmation** — .NET / Bicep / App Service + Azure SQL assumed
   (§6). Confirm or override.
3. **Email — DECIDED: Brevo SMTP.** Provider `smtp`, host `smtp-relay.brevo.com`,
   port 587, STARTTLS, sender `info@expertslive.dk`. The user will provide the
   Brevo SMTP username (a Brevo-issued ID, not the login email) and SMTP key —
   these go into Key Vault as `brevo-smtp-username` and `brevo-smtp-key`. The
   sender address must be verified in Brevo. Still to do: user provides the two
   credentials, and confirms `info@expertslive.dk` is verified in Brevo.
4. **Hotel extra-night rates** — `extraNightSingle` 598 / `extraNightDouble` 698
   DKK are placeholders. Need real numbers.
5. **Speaker-deadline dates & URLs** — the 4 milestones (§9) are +1yr guesses
   from ELDK26. Need ELDK27-confirmed dates and real links (Backstage,
   Sessionize, presentation upload).
6. **Pre-day weekday** — confirm the pre-day / crew-day calendar (8 Feb is a
   Monday; earlier planning notes had a Setup-Mon + Pre-day-Tue + Main-Wed pattern
   that should be re-checked against the 9–10 Feb main dates).
7. **WooCommerce REST API key** — user must generate a Read-only key in
   WooCommerce → Settings → Advanced → REST API for the integration.
8. **Staging** — there is no staging environment. Strongly recommended: deploy
   Azure with a `dev`/`staging` slot or a second resource group before pointing
   the real DNS at it. The app is isolated from the webshop, so risk is lower
   than the WordPress option, but test before go-live.
9. **Company Manager API credentials** — the hub needs a WordPress application
   password for the Company Manager API (§11g). User to provide the WP user +
   app password; they go into Key Vault as `company-manager-wp-user` and
   `company-manager-wp-app-password`.
10. **Sponsor contact editing — where?** Company Manager (WordPress) already has
    a UI for managing companies, contacts and roles. Decide: does adding/editing
    sponsor contacts stay in Company Manager only (hub reads via API), or should
    the hub also offer contact editing (writing back via the API)? Reading is
    settled; editing location is a genuine choice. Default assumption until told
    otherwise: editing stays in Company Manager, the hub reads only.
11. **Company Manager role coverage** — confirmed roles are Signer (1) and Event
    Coordinator (2). If booth staff need to be real Company Manager contacts
    (not just a hub-only role), Company Manager may need a third role; confirm
    with the user.
12. **Booth wall-design spec PDFs + coupons** — `sponsor.eldk27.json → boothWallSpecs` currently holds the ELDK26 PDF URLs and coupon codes (placeholders). Confirm or replace with the ELDK27 wall-spec PDFs (Platinum 6m / Diamond 5m / Gold 4m / Feature 3m) and the ELDK27 booth-included coupon codes before go-live.
13. **Backstage embed handoff** — confirm what Zoho Backstage can pass to an embedded iframe (§5a). If it can emit a cryptographically *signed* token / JWT, or expose an API to confirm the session, Option A (true SSO, no second login) becomes viable. If it can only put an unsigned value in the URL, that is forgeable and the hub stays on Option B (one-tap PIN). Also confirm the exact Backstage domain to allow in the hub's `frame-ancestors` CSP.
14. **Scheduled-job language — DECIDED: all .NET.** The web app must be .NET, and the timer jobs (reminders, WooCommerce pull, Zoho reconciliation, duplicate detection) are .NET Azure Functions too. Rationale: the jobs and the web app share one data model and one `CommunityHub.Core` library, so the reminder logic, the `SentReminder` idempotency ledger, and the integration clients are written once and used by both. A PowerShell split would duplicate that logic and force the jobs to re-query Azure SQL by hand. The team's existing PowerShell scripts remain the behavioural *specification* that the C# ports are checked against. `CommunityHub.Jobs` is a .NET Functions app (as already scaffolded in Stage 2).

---

## 13. Working method for the dev solution

- Work stage by stage (§8). Commit at the end of each stage with a clear message.
- Keep `CONTEXT.md` updated as decisions are made or open questions close.
- Zoho Creator is dropped — not a fallback, not maintained, not referenced.
  This Azure solution is the only one.
- Infrastructure changes go through Bicep, never the Azure portal by hand, so
  the environment stays reproducible.
- Secrets via Key Vault only. If a secret must be set, document it in
  `docs/RUNBOOK.md` as a one-time CLI step, never commit it.

---

## 14. First actions in a fresh VS Code session

1. Read this file fully.
2. Confirm the open questions in §12 that block Stage 1 (subscription, stack,
   email mechanism).
3. Begin **Stage 1**: scaffold the repo per §7 and write the Bicep in `infra/`.
4. Write `docs/RUNBOOK.md` alongside it — the one-time Azure setup steps,
   the deploy command, and the rollback procedure.
5. Commit. Then proceed to Stage 2 only after Stage 1 deploys cleanly.
