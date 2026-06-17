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
18. [Legacy automation: webhooks & deployment topology](#18-legacy-automation-webhooks--deployment-topology)

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

![Architecture overview — the web hub + Functions job host sharing one Core library and one Azure SQL database](img/image1.png)
*Architecture overview: the ASP.NET Core web hub and the Azure Functions job host share one domain library and one Azure SQL database.*

![Architecture detail — integrations, jobs and the embedded portal](img/image2.png)
*Architecture detail: integrations (Sessionize / Zoho / webshop / SharePoint), the timer jobs, and the hub embedded inside the public event portal.*

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
  venue, hotel, form deadlines, the lock date, role list, sponsor rules, and `CalendarSyncEnabled`
  (organizer master switch for calendar sync, default true — see §7a Calendar sync).
- **`Participant`** — one row per person per edition. Fields cover: Name, Email (login key),
  Gender, Mobile, Role, IsActive, `IsTestUser` (test/dummy-data flag — go-live cleanup
  deletes/deactivates WHERE `IsTestUser=true` without touching real registrations; default false),
  MS accreditation (MVP/Expert/RD/MS Employee), award, polo
  request/size, jacket size, verification, packed status, plus `SponsorCompanyId` (external
  Company Manager id, for sponsor scoping), and `CalendarFeedToken` (the per-participant secret
  for the subscribable iCal feed — see §7a Calendar sync; nullable, minted lazily on first hub
  view, unique filtered index, regenerable to revoke). 8 roles, single-role-per-person: Organizer, Speaker,
  Volunteer, Sponsor, Speaker-Sponsor, Video, Photography, VIP — plus **Attendee** added later
  (§6 Zoho) as a sixth participant audience.
- **`LoginPin`** — PIN-hash storage, 15-min expiry, single-use.
- **`HotelBooking`** — modeled as a **check-in/check-out date range** (not per-night columns).
  Nightly room occupancy is *computed* from the range for the hotel room-night forecast; a double
  room shared with a partner counts as **one room**. Carries occupancy (single/double), partner,
  confirmation number (organizer-editable, crew read-only). This is the participant's own
  room-need *preference*; the physical-venue *placement* is the `Hotel` model below.
- **`Hotel`** (multi-hotel management, added 2026-06-15) — an organizer-defined hotel for an
  edition (Name required + Address + ContactEmail + Notes), edition-scoped (`EventId`, cascade
  from `Event`). Rooms rarely fit in one hotel, so attendees are split across several. Distinct
  from `HotelBooking`: a `Hotel` is the venue an organizer *places* people into (one hotel → many
  participants). `Participant` gains **`HotelId`** (nullable FK to `Hotel`, `NoAction` to avoid a
  second cascade path from the `Event` root; index `(EventId, HotelId)` for the group-by-hotel
  query) and **`HotelConfirmationNumber`** (per-person, organizer-set). `HotelManagementService`
  is the single server-side authority (hotel CRUD, assign/clear placement, set confirmation
  number, and `GroupByHotelAsync` → one `HotelGroup` per hotel alphabetical + a trailing
  "Not assigned" group; empty hotels still appear). Organizer pages `/Organizer/Hotels` (CRUD)
  and `/Organizer/HotelAssignments` (assign + group view + confirmation). EF migration
  `MultiHotelManagement`. The hotel calendar invite/email is enriched via the pure, unit-tested
  `HotelEmailContentBuilder` (Core) — the participant's assigned hotel **name + address +
  confirmation number** fold into the venue/subject/body, the per-person number taking priority
  over any legacy `HotelBooking.ConfirmationNumber`; routed through the gated `IEmailSender`
  (DEV-redirected). See §8 organizer area + §7 email.
- **`DinnerSignup`**, **`VolunteerAvailability`** — the two other self-service form entities.
- **`DietaryRequirement`** (structured dietary/allergy capture, added 2026-06-16) — own-row-scoped
  structured catering data, **one row per participant per edition PER `DietarySurface`**
  (`SpeakerCatering` = the speaker form's day-catering, `Dinner` = the Appreciation Dinner), so a
  person can have both. Carries a `DietChoice` (None/Vegetarian/Vegan/Pescatarian/Halal/Kosher), the
  **14 EU FIC major allergens as discrete `bool` columns** (count-friendly — the caterer gets
  head-counts, not free text), and a free-text `OtherAllergens` for the long tail. Indexes
  `(EventId, ParticipantId, Surface)` unique (the upsert key) + `(EventId, Surface)` (the per-occasion
  catering roll-up). Event FK cascades; Participant FK is `Restrict` (no second cascade path). The
  legacy `DinnerSignup.AllergyNotes` free-text box is **kept** (re-labelled "anything else") so nothing
  is lost. The Dinner + Speaker forms render the SAME `_DietaryFieldset` partial (bound via the shared
  `DietaryInput`); the pure `DietaryAggregator` rolls a set of rows into per-allergen + per-diet counts.
  EF migration `StructuredDietaryRequirements` (single table; `has-pending-model-changes` = none).
- **Volunteer work structure** (added 2026-06-15) — 5 entities forming a 3-level
  tree plus assignments and a help channel, all edition-scoped:
  - **`VolunteerCategory`** (top) — Name, Description, `LeadParticipantId`
    (nullable FK to the **organizer** who oversees it), `SupervisorParticipantId`
    (nullable FK to the **volunteer** appointed to run it — the appointment row IS
    the management grant, no global role change).
  - **`VolunteerSubcategory`** — belongs to one category (`CategoryId`); `EventId`
    denormalized from the parent.
  - **`VolunteerTask`** — belongs to one subcategory (`SubcategoryId`); Title,
    Description, optional `DueDate` + free-text `Shift`, `Status`
    (`VolunteerTaskStatus`: Open/InProgress/Done/Cancelled). `EventId`
    denormalized so a task list is a single edition-scoped query.
  - **`VolunteerTaskAssignment`** — the many-to-many link Task↔volunteer; unique
    `(TaskId, ParticipantId)`; indexed `(EventId, ParticipantId)` for "my tasks".
  - **`VolunteerHelpRequest`** — the help channel: a volunteer assigned to a task
    raises a request (`TaskId` + denormalized `CategoryId` so the supervisor's
    inbox is a single filter); Message, `Status` (Open/Answered/Resolved),
    Response + audit (`RespondedByEmail`, `RespondedAt`, `ResolvedAt`).
  - All mutations go through one server-side authority, `VolunteerStructureService`
    (in `CommunityHub.Core.Domain`), which enforces the permission model:
    organizer = full across the edition; supervisor = elevated for their own
    category only; volunteer = own assigned tasks + raise help. It throws
    `VolunteerAccessDeniedException` (→ 403) / `VolunteerValidationException` (→
    friendly message). Like `OrganizerActionItem`, FKs to `Participant` and the
    cross-tree FKs use `NoAction` to avoid SQL Server multi-cascade-path errors;
    category→subcategory→task→assignment cascade-delete down the tree.
  - Migration: **`VolunteerWorkStructure`** (5 tables). Additive — feeds the
    existing reminder/calendar model, does not replace `VolunteerAvailability`
    (the shift-availability wizard) or `OrganizerActionItem` (the action queue).
  - **Help-request notification** (added 2026-06-15, no schema change) —
    `VolunteerHelpNotificationService` (in `CommunityHub.Core.Email`) emails the
    owning category's **supervisor** when a help request is raised, with the
    organizer **lead** copied (a separate per-person send so each gets correct
    effective-address/secondary-CC routing). It sends through the shared
    `ParticipantEmailService` → `IEmailSender`, so the **DEV redirect-all-to and
    the PROD allowlist gating apply automatically** — it never bypasses them, and
    uses the shipped `volunteer-help-raised` template. The volunteer's "Ask for
    help" page (`/volunteer/mytasks`) invokes it **best-effort** after the request
    is saved: a mail failure is logged and swallowed (the request already persists
    and shows in the supervisor's in-hub inbox). A category with no supervisor yet
    notifies only the lead.
- **Volunteer Buckets & allocation** (added 2026-06-15, migration
  **`VolunteerBucketsAllocation`**) — extends the structure above **additively** (the
  `VolunteerCategory` is the "Bucket" in the UI; no parallel stack). Schema changes:
  - **`VolunteerCategory`** gains `EldkLeadName` (free-text third-tier go-to person).
  - **`VolunteerBucketSupervisor`** — new join giving a bucket **one or more**
    supervisors (volunteers); unique `(CategoryId, ParticipantId)`. The legacy single
    `SupervisorParticipantId` still counts — `CanManageCategoryAsync` and
    `LoadSupervisorsAsync` union both. `AddSupervisorAsync`/`RemoveSupervisorAsync`
    manage it (organizer-only).
  - **`VolunteerTask`** gains `TimeEnd`, `Criticality` (`VolunteerTaskCriticality`
    Unspecified/NiceToHave/NeedToHave), `ResponsibleTeam`, `EldkLeadName` (per task),
    `ResourcesNeeded` (int), `Prerequisites`, `Expectations`, `Instructions`, and
    `CompletedAt`/`CompletedByEmail`. `MarkTaskCompletedByLeadAsync` is the lead's
    sign-off (status Done + audit stamp); `ReopenTaskAsync` clears it;
    `UpdateTaskDetailsAsync` edits the new fields. `CreateTaskAsync` gained optional
    params for the new fields (existing callers pass `ct:` by name).
  - **`TaskAllocationDraft`** — new per-organizer DRAFT queue (`OwnerParticipantId`),
    unique `(TaskId, ParticipantId)`. **`VolunteerAllocationService`** is the
    task-mapper engine: `LoadCoverageAsync`/`LoadTaskCoverageAsync` return
    `TaskCoverage` (needed vs assigned vs draft → red/green `IsCovered`);
    `AddDraftAsync`/`RemoveDraftAsync` queue a simulation; `CommitAsync` turns the
    queue into real `VolunteerTaskAssignment` rows and clears it; `DiscardAsync`
    resets without assigning. All allocation writes are organizer-only.
  - **CSV import** — `VolunteerPlanParser` (pure, RFC-4180-ish; handles quoted
    multi-line Resource Names) parses the semicolon plan into `ParsedPlanTask`,
    deriving the **Bucket from Responsible Team**. `VolunteerPlanImportService`
    upserts buckets+tasks idempotently under an "Imported plan" subcategory, matches
    Resource Names to existing volunteers by FullName (unmatched reported, never
    created), and fills missing Pre-req/Expectations via the guidance seam. The
    organizer `/Organizer/BucketAllocation` page drives import + gaps + draft→commit.
  - **AI guidance seam** — `ITaskGuidanceGenerator` with `HeuristicTaskGuidanceGenerator`
    (always-on, no secret, deterministic) and `LlmTaskGuidanceGenerator` (Claude
    Messages API over `HttpClient`, model `claude-opus-4-8`). Gated on
    `TaskGuidanceOptions.ApiKey` (bound from the `VolunteerGuidance` config section —
    SECRET, never committed; empty placeholder in `config/volunteer-guidance.sample.json`).
    DI registers the LLM generator only when a key is configured; otherwise the
    heuristic IS the generator. The LLM provider never hard-fails — it falls back to
    the heuristic on any error so import never breaks.
  - **Volunteer view** — `/volunteer/mytasks` now shows each task's
    instructions/pre-req/expectations and the bucket's supervisor(s) + ELDK lead.
- **Onboarding lifecycle** (added 2026-06-15) — adds **columns to `Participant`**
  (no new table), migration **`OnboardingLifecycle`**:
  - **`LifecycleState`** (`ParticipantLifecycleState`: `Inactive`=0 → `Preselected`=1
    → `Active`=2, default `Inactive`) — the **pre-selection gate**, distinct from the
    boolean `IsActive`. `IsActive` stays the withdrawal/cancellation switch; the two
    are orthogonal. **Login now requires BOTH `IsActive` AND `LifecycleState == Active`**
    (added to the predicate in `PinLoginService` + `PinIdentityProvider`), so a
    not-yet-activated queue entry cannot sign in. The migration **backfills every
    pre-existing row to `Active`** so current participants are never locked out.
    Indexed `(EventId, LifecycleState)` for the queue. Organizer hand-add
    (`EditParticipant`) writes `Active` (bypasses the queue); the public volunteer
    interest form writes `Inactive`.
  - **`QueueSource`** (`ParticipantQueueSource`: `Manual`/`SessionizeSync`/
    `VolunteerInterestForm`/`MediaTeamSignup`) — where the row entered the hub; drives
    the queue's source column + filter.
  - **`OnboardingCompleted_Bio` / `_Picture` / `_Hotel` / `_Appreciation` / `_Swag`**
    — five `bit` per-step completion flags the onboarding wizard sets; the organizer
    overview reads them; an organizer can flip one back to 0.
  - **`OnboardingCompleted_BioAt` / `_PictureAt` / `_HotelAt` / `_AppreciationAt` /
    `_SwagAt`** (added 2026-06-15, migration **`OnboardingStepTimestamps`**) — five
    nullable `datetimeoffset` per-step **completed-at** timestamps, stamped together
    with the matching bit and cleared back to null when an organizer re-opens the
    step (so done ⇔ has-a-timestamp stays an invariant).
  - **`PreselectionQueueService`** (in `CommunityHub.Core.Organizer`) — the queue read
    + advance authority: `GetQueueAsync` (non-active rows, optional source filter),
    `PreselectAsync` / `ActivateAsync` (single or multi), forward-only `AdvanceAsync`
    (never demotes, idempotent, event-scoped, one `SaveChanges` per batch; activation
    also flips `IsActive` on). Returns `QueueResult(Matched, Changed)` for honest
    banners — same shape as `ParticipantBulkOperationService`.
  - **`OnboardingStepSets`** (in `CommunityHub.Core.Organizer`, added 2026-06-15) —
    the **per-persona required-step sets**: maps a `PersonaGroup` (reusing
    `OnboardingEmailSets.PersonaFor`) → the ordered subset of `OnboardingStep` that
    persona must complete (speaker = all five; volunteer/media = hotel+appreciation+
    swag; sponsor/organizer = appreciation+swag). Code-defined, no schema. Exposes
    `For`, `Requires`, `DoneCount`, `RequiredCount`, `IsComplete`, `PercentComplete`
    so completion is "all of MY persona's steps", not a fixed checklist.
  - **`OnboardingService`** (same namespace) — `MarkStepCompleteAsync` (wizard sets a
    flag **+ stamps the `*At` timestamp** via injected `TimeProvider`, idempotent),
    `ResetStepAsync` (organizer flip-to-0 **+ clears the timestamp**; on a real 1→0 it
    **hands off to the email system** by raising an `OrganizerActionItem` of type
    `onboarding-step-reset` — the send itself is the email system's job),
    `ResetStepForPersonaAsync(eventId, persona, step)` (the **bulk re-open** sibling —
    re-opens one step for EVERY active/pre-selected person of a persona who currently
    has it done **and** whose persona requires it, looping through the single-row
    `ResetStepAsync` so the timestamp-clearing + per-person email hand-off stay in one
    place; people who never finished the step, or whose persona doesn't require it, are
    untouched; idempotent + edition-scoped; returns a `PersonaResetResult(Candidates,
    Reopened)` for an honest "Re-opened N" / no-op banner), and
    `BuildOverviewAsync(eventId, persona?)` (read-only, **persona-aware**: includes
    `Preselected` + `Active` rows, optionally filtered to one persona; derives a
    per-row `OnboardingStage` (**Preselected / Invited / In-progress / Completed**),
    and returns `OnboardingStageStat` counts, per-step `OnboardingStepStat` (counting
    only people who **require** that step), per-person `OnboardingRow` grid, and an
    overall completion %), and `BuildPendingAsync` / `BuildPendingCsvAsync`
    (`eventId, persona?`) — the **"who hasn't onboarded yet" export**: reuses
    `BuildOverviewAsync`'s persona-aware projection, keeps only rows whose stage is
    **not** `Completed`, computes each person's **missing steps** (`PendingOnboardingRow`),
    orders least-complete-first, and renders CSV via the shared `Export.CsvWriter`
    (columns Name/Email/Persona/Stage/Done/Required/Percent/MissingSteps). Read-only,
    no new table beyond the timestamp columns; the email hand-off reuses the existing
    `OrganizerActionItem` queue.
  - **UIs (mobile-first ~360px, a11y):** organizer `/Organizer/PreselectionQueue`
    (grid + select-all + bulk preselect/activate), participant
    `/Forms/OnboardingWizard` (**persona-driven** wizard — walks the participant's
    `OnboardingStepSets` sequence, showing only the steps that persona needs, writing
    to `SpeakerProfile` bio/photo, `HotelBooking`, `SwagPreference` + the completion
    flags/timestamps), organizer `/Organizer/Onboarding` (**by-stage count tiles** +
    **persona filter** + per-step progress tiles with `role="progressbar"` +
    per-person grid showing each person's stage/persona, with non-required steps
    marked n/a, a per-cell "re-open" hand-off button, a **"who hasn't onboarded
    yet" CSV download** (`OnGetPendingCsvAsync` handler, organizer-gated, UTF-8 BOM,
    carries the active persona filter through `asp-route-persona`), **and — when a
    single persona is filtered — a "re-open a step for the whole group" card**
    (`OnPostReopenPersonaStepAsync` → `ResetStepForPersonaAsync`; a step picker scoped
    to that persona's required steps + a count-aware confirm; redirects back filtered
    to the persona with an honest re-opened/no-op message)).
- **Organizer grid v2** (added 2026-06-15) — migration **`OrganizerGridV2`**, two new
  tables (no `Participant` column changes):
  - **`ParticipantSecretaryToken`** — the write-scoped secretary grant: `Token` (256-bit,
    URL-safe, unique index), `ParticipantId` (single-person scope; cascade-deletes with the
    participant), `ExpiresAt` (time-bound), `RevokedAt` (revocable), `Label`/`IssuedByEmail`/
    `LastUsedAt` (audit). Resolved by `SecretaryTokenService` (issue/list/revoke/resolve);
    `ResolveAsync` returns a valid grant only for an **active** participant (§3 combined rule).
    `GET /s/{token}` (`SecretaryController`) is the entry point.
  - **`ImpersonationAudit`** — append-only acting-as trail: `ActorKind`
    (`Organizer`/`SecretaryToken`), `ActorParticipantId` (null for a token), `ActorLabel`,
    `TargetParticipantId`, `Action` (`start`/`return`/`modify-hotel`/`modify-swag`/
    `secretary-use`), `Detail`. Written by `ImpersonationAuditService`, read at
    `/Organizer/ImpersonationLog`. Indexed `(EventId, CreatedAt)` + `(EventId, TargetParticipantId)`.
  - **`ParticipantActivation`** (in `CommunityHub.Core.Domain`) — the single lifecycle-correct
    "active" rule (`IsActive AND LifecycleState == Active`) as both an in-memory check and an
    EF-translatable expression; the grid's active/inactive filter + status badge use it.
  - **`ModifyOnBehalfService`** — organizer changes to `HotelBooking.NeedsRoom` /
    `SwagPreference.PoloSize` write the **same rows** the participant reads (so the change shows on
    their own view); a late change raises the existing `OrganizerActionItem` queue. Edition-scoped.
  - **Acting-as session mechanics** (organizer "switch to user" + secretary token) — see §4.
  - **UIs (mobile-first ~360px, a11y):** the existing `/Organizer/Participants` grid gains
    persona + sponsor-company filters and per-row Switch / Modify-on-behalf / Secure-link
    actions; new `/Organizer/EditOnBehalf`, `/Organizer/SecureLink` (old
    `/Organizer/SecretaryLink` redirects here),
    `/Organizer/ImpersonationLog`, `/Organizer/ReturnToOrganizer`; the acting-as banner lives in
    `_Layout` (`role="alert"` + "Return to organizer").
- **`ParticipantTask`** — rich task model (title, T-day, date, start/end, criticality, responsible
  team, assignee, owner, resources, shift-or-deadline, due date, priority, status, notes, **Link**,
  `AssignedContactId`, `SponsorCompanyId`). Tasks show in the assignee's hub sorted by due date.
- **`SentReminder`** — the reminder idempotency ledger (recipient + type + occasion).
- **`Attendee`** — Zoho-synced master-class reconciliation rows + booking status,
  plus a nullable `CheckedInAt` (set once when the attendee self-checks-in from the
  "My Event" dashboard; null = not checked in).
- **`SponsorOrder`**, **`SponsorLead`** — WooCommerce orders and sponsor leads.
  `SponsorLead` carries both Zoho-CRM-synced leads (with a `ZohoRecordId`) and
  **hub-captured booth leads** (no Zoho id; `CaptureMethod=ManualBooth` +
  `CapturedByEmail` provenance). The Zoho unique index is filtered
  (`ZohoRecordId <> ''`) so hub-local rows never collide; both kinds share the
  same screening + export path. The heuristic quality screen is split into a pure,
  deterministic `SponsorLeadScoreExplainer.Compute(lead)` (the SINGLE source of truth
  for the 0–100 math) and the thin `SponsorLeadScreeningService.Screen(...)` that
  delegates to it and persists `AiScreenScore`/`AiScreenLabel`. `Compute` returns a
  `SponsorLeadScoreBreakdown` — the starting baseline, each signed factor with a stable
  reason key (`LeadScore.*`, localised in the UI), the raw (pre-clamp) total and the
  final clamped score — so the organizer leads grid can render an expandable
  "why this score" decomposition under each badge that can never drift from the number
  the screen scored (drift-locked by a test that re-runs `Screen` and asserts equality).
- **`SurveyResponse` / `SurveyResponsePick`** — public survey persistence (FK + cascade).
- **`GraphicAsset`** (added 2026-06-15) — one generated/replaced SoMe graphic: `Type`
  (speaker/sponsor/session), `Status` (generated/released — the release gate), `StableKey`
  (edition-unique idempotency identity the SharePoint path derives from), `SharePointPath`/`Url`/
  `StorageItemId`, `IsOrganizerOverridden`, release audit. Unique `(EventId, StableKey)`. See §6.
- **`GraphicsAssetLocation`** (added 2026-06-15) — per (edition, persona group:
  volunteers/speakers/media/organizers) SharePoint pointers (site/drive/root/browse-URL/notes); no
  credentials. Unique `(EventId, PersonaGroup)`. Migration: **`GraphicsAssets`** (2 tables, additive).
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

**Login email prestage.** `/Login?email=<addr>` **pre-fills** the email field on the login page so the
participant lands with their address already typed and only has to request/enter the PIN. It is a
pure convenience prefill — it never authenticates by itself (a `?email=` is forgeable; security still
rests entirely on the emailed PIN / verified handoff above). It works in **both dev and prod**: the
link uses the environment's own base URL (dev `dev.hub.yourevent.example`, prod `hub.yourevent.example`),
so an invitation generated per environment points at the right host.

Embedding mechanics:
- `Content-Security-Policy: frame-ancestors` allows the Backstage origins (the `zohobackstage.*` /
  `zoho.*` / `zohopublic.*` / `zohoexternal.*` domains) — **not** `X-Frame-Options: DENY`, and
  never `*`. The exact origin list is the `Embedding__BackstageOrigin` app setting (§14).
- The session cookie is `SameSite=None; Secure` so it survives the cross-site iframe.

![The public front door / sign-in, the same surface that renders inside the embedded portal iframe](img/public-landing.png)
*The public front door + sign-in. The same surface renders inside the embedding portal's iframe — PIN login and magic-link tokens work in-frame because the cookie is `SameSite=None; Secure`.*

**Acting-as sessions (organizer "switch to user" + secretary token).** Both reuse the *same* cookie
mechanism, which is what makes "act on their behalf" and "modify-on-behalf reflects on the user's own
view" fall out for free: the session is signed in **as the target participant** (target identity +
role + `EventId` claims), so every existing page renders the target's view and every write lands on
the target's own rows — there is no parallel "organizer copy". What distinguishes an acting-as session
from a real login is a set of **marker claims** (`ActingAsClaims`: kind ∈ {Organizer, SecretaryToken},
the acting organizer's participant id, a human label). `CurrentParticipant.FromPrincipal` delegates to
the unit-tested `Core.Auth.ActingAsClaims.Parse`, exposing `IsActingAs` + `ActingAs`.
- **Server-enforced organizer-only.** "Switch to user" and the grid require
  `Role == Organizer **AND NOT** IsActingAs`. The `!IsActingAs` half is the **no-nested-impersonation**
  guard: even an organizer who switched into *another organizer's* view (role still Organizer) cannot
  drive the grid or start a further impersonation.
- **Lands on the user's own hub — full impersonation, not a form.** A successful
  `OnPostSwitchToUserAsync` redirects to the **hub root `/`**
  (`ParticipantsModel.SwitchToUserLandingPath`), i.e. the target's role-personalized
  My-Event view, so the organizer immediately navigates the **whole app as that user**.
  It deliberately does **not** land on `/Organizer/EditOnBehalf` — that 2-field
  hotel/swag page is the separate, lesser **"Modify on behalf"** quick-edit (a distinct
  grid link that stays in the organizer seat), not what "Switch to user" does. This
  landing contract is asserted in `CommunityHub.Web.Tests/SwitchUserImpersonationTests.cs`
  and the Playwright `feature-impersonation.spec.ts` round-trip.
- **Reversible.** The acting-as banner's "Return to organizer" re-issues the organizer's own (un-marked)
  session from the organizer participant id carried in the marker; if that organizer no longer exists /
  is no longer an organizer the session is signed out (fail safe).
- **Audited.** Every start, return, secretary-token use and on-behalf write appends an
  `ImpersonationAudit` row (actor kind + label, target, action, detail); the organizer reviews it at
  `/Organizer/ImpersonationLog`. The trail is append-only.
- **Secretary secure token** mirrors the calendar-feed token (256-bit, URL-safe, unique) but is
  write-scoped: `ParticipantSecretaryToken` is **single-person** (resolves to exactly one participant),
  **time-bound** (`ExpiresAt`), and **revocable** (`RevokedAt`). `GET /s/{token}` resolves a valid grant
  (not revoked, not expired, **active** participant per the §3 combined rule), signs the visitor in as
  that one participant with a `SecretaryToken` marker (so they can never reach organizer areas or start
  an impersonation), and lands them on the onboarding wizard. An invalid/revoked/expired token returns
  404 (no token-existence oracle, same as the calendar feed). The acting session is short-lived and
  non-persistent.

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

### e-conomic ERP + sponsor webshop (REQUIREMENTS §7a)
A clean .NET slice of the legacy PowerShell ERP/webshop pipeline, in
`CommunityHub.Core/Integrations/Erp/`. All writes go through one seam,
**`IEconomicErpClient`** — same TESTMODE-vs-live DI swap as the Backstage exhibitor API:
- **`TestModeEconomicErpClient`** (TESTMODE) — no real calls; `CanWrite=false`, so services
  record `WouldCreate`. **`LiveEconomicErpClient`** — reports `CanWrite=false` until the
  e-conomic App-Secret + Agreement-Grant tokens + base URL are configured; the actual REST
  payload/webshop HTTP wiring is the remaining **◻** item (needs operator creds/endpoints —
  config holds secret NAMES only, never values). A write method must throw rather than fake a
  call while `CanWrite` is false.
- **Customer create/sync** (`EconomicCustomerSyncService`) — maps a Company Manager company →
  `ErpCustomer` (name ALWAYS resolved through the shared `SponsorCompanyName` public→legal→
  billing→"Company {id}" chain; CVR / currency / VAT zone / existing customer number read from
  the company record). Idempotent via a hub-local **`ErpCustomerLink`** (one per event+company):
  a re-run drives an Update, never a duplicate Create. The link also records the last CVR
  validation outcome for operator visibility.
- **CVR (tax-id) validation on create** (`CvrValidator` + `ICvrValidator`) — a pure-offline
  Danish 8-digit modulus-11 gate is always applied and is a **hard block on first ERP create**.
  An external CVR-register lookup (`IExternalCvrLookup`) is wired behind a disabled-by-default
  toggle; a register outage falls back to the offline pass (never blocks creation on an outage).
  Live register call is ◻.
- **Contact/role create + webshop sync** (`SyncContactAsync`) — maps Company Manager users →
  `ErpContact` with the role derived from the company's `default_signer_id` (Role 1 → Signer)
  and `event_coordination_default_contact_id` (Role 2 → EventCoordinator); requires the company
  to already be linked. Webshop write-back rides the same ◻ live wiring.
- **Order creation from webshop orders** (`EconomicOrderCreationService`) — maps a `WooOrder` →
  `ErpOrder` and creates it idempotently via an **`ErpOrderLink`** (one per event+webshop order).
- **Currency/FX check** (`IFxRateProvider`) — runs before order creation: same-currency → rate
  1; live FX applied when a provider is configured; otherwise a known-currency gate that records
  a null rate and **never fabricates one**; a malformed currency fails the check and blocks
  creation. The applied rate + result are stored on the order link. Live FX endpoint is ◻.
- Config sections (secret NAMES + non-secret endpoints only): `EconomicErp`, `CvrLookup`,
  `FxRates`. All three default disabled, so a community without them runs unaffected.

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
Two interchangeable sources feed **one** upsert core. The upsert semantics live once in
`SessionizeImportService.ImportSpeakersAsync` (match on **email** within the edition, update name +
the speaker bio/social fields, **never delete**, **never change role**, send the welcome
email to new speakers). Rows/speakers with no email are skipped + reported. Imported speakers are all
role Speaker; reclassifying a MasterclassSpeaker is a manual organizer action.

**Speaker-owned bio + delta/full import modes (2026-06-15).** The bio fields — `Tagline`,
`Biography`, `Blog`, `LinkedIn`, `Twitter`, and `PhotoUrl` (Sessionize `profilePicture`, now stored)
— are **seeded from Sessionize but owned by the speaker** once they edit them on `/Forms/Speaker`.
`SessionizeImportService.ImportSpeakersAsync` takes a `SessionizeImportMode`:
- **`Delta`** (default — the scheduled job, the OneShot CLI, and the organizer "Sync new speakers"
  button): adds **new** speakers and fills a bio field **only when it is empty AND not
  speaker-edited** (`FillIfUntouched`). A speaker's own edit is **never** flushed by a re-import.
- **`Full`** (the organizer **"Full import from Sessionize"** button): force-refreshes **all** bio
  fields from Sessionize (a blank source value clears the field — a true re-seed) and **clears the
  speaker-edited set**. The deliberate operator override.

Per-field speaker-edit tracking lives on `SpeakerProfile`: `SpeakerEditedFields` (a comma-separated
dirty set of the `BioFields` tokens) + `BioLastEditedBySpeakerAt`, with `IsSpeakerEdited` /
`MarkSpeakerEdited` / `ClearSpeakerEdited` helpers. The Speaker page marks a field edited only when
its value actually changes (own-row scoped). Hub-collected fields (Accreditation, IsFirstTimeSpeaker,
Country, Gender, `ContactEmailOverride`) are never touched by **either** mode. The
`/Forms/Speaker` page renders the bio as **pure-CSS tabs** (radio + `:checked ~` sibling selectors,
no JS) — Bio · Tagline · Links & Social · Photo · Sessions — with ARIA `tablist`/`tab`/`tabpanel`
roles, keyboard focus styling, and mobile-first wrapping.

**Speaker contact-email override (2026-06-15).** The matched email is the speaker's **identity** and
the re-import match key — it never changes. A speaker may set a preferred contact address in
`SpeakerProfile.ContactEmailOverride` (nullable, hub-collected). The single routing rule is
`EffectiveEmail => ContactEmailOverride ?? Participant.Email` (instance property + static
`SpeakerProfile.EffectiveEmailFor`); **all** outbound speaker mail and the `.ics` calendar feed
resolve through it. The import writes Sessionize fields only, so a re-import (matched on the identity
email) **never** overwrites the override — same authoritative-vs-imported split as Accreditation /
Country. Reminder delivery uses `EffectiveEmail` while the `SentReminder` idempotency ledger keys on
the **identity** address (`ReminderMessage.RecipientEmail` = identity, `DeliverToEmail` = effective),
so changing the override never re-sends. **Zoho Backstage propagation** is a clean seam
(`IBackstageSpeakerEmailApi`) with a no-op default (`NullBackstageSpeakerEmailApi`, `CanWrite=false`,
**no faked call**) plus a durable upsert queue (`SpeakerBackstageEmailSync`, one Pending row per
speaker per edition); live wiring is **◻ pending** (Backstage has no documented speaker
contact-email endpoint implemented here) — register a live writer and the queue drains, no caller
changes.

**Speaker-bio sync to Zoho Backstage (OUTBOUND hub → Backstage, 2026-06-15).** Mirrors each hub
speaker's bio profile (Tagline → Backstage `designation`, Biography → `description`, plus
Blog/LinkedIn/Twitter) to a Backstage speaker record — the .NET replacement for the prior-year
PowerShell job `tools/legacy-automation/scripts/Sync-Sessionize-Speakers-to-Zoho-Backstage.ps1`.
Same clean-seam pattern as the exhibitor / email APIs: `IBackstageSpeakerBioApi` with a no-op default
(`NullBackstageSpeakerBioApi`, `CanWrite=false`, **no faked call**) and a `SpeakerBioBackstageSyncService`
that builds the request and applies two invariants regardless of the writer:
- **HARD GATE — never publish an unselected speaker.** The push carries a `SpeakerPublishState`; it is
  `Public` **only** when `SpeakerProfile.SelectedForPublish` is explicitly `true`. That column defaults
  **false** (single bit column, `defaultValue:false`, migration `SpeakerSelectedForPublish`) so until the
  lineup is selected every speaker is `Draft` / hidden — never exposed publicly. `BuildRequest` is a pure
  static so a dry-run/test asserts the state with no Zoho call. A Sessionize re-import never writes the
  flag (hub-collected).
- **Inactive by default.** `Backstage:SpeakerBioSync:Enabled` (bound from `BackstageSpeakerBioSyncOptions`)
  defaults **false** — no scheduled/automatic run; a manual opt-in trigger (organizer action / CLI) is the
  only way to invoke `SyncOneAsync` / `SyncAllAsync`. A disabled sync returns `Disabled` and makes no call
  even if a writer is wired and a speaker is selected.
- **Live wiring is ◻ pending (operator config).** The real Backstage portal/event ids + OAuth creds
  (refresh token / client id / secret) are operator config (gitignored `config/` or Key Vault),
  **placeholders only** in committed/public files. With the default Null writer the gated request is built
  (`BuiltOnly`) but no Zoho call is faked. Register a live writer + enable the flag once an endpoint/creds
  exist **and** the lineup is selected — no caller changes. **Live activation is ◻.**

- **API (v2 view endpoints, JSON)** — the primary, hands-off path. `SessionizeApiClient` pulls the
  configured event's view (`Speakers` by default; `All` also supported) from
  `https://sessionize.com/api/v2/<endpoint-id>/view/<view>` and maps each speaker into the same
  `SessionizeSpeaker` shape the Excel parser produces (links split by `linkType` →
  LinkedIn/Twitter/Blog). `SessionizeApiImportService` then runs the shared upsert core.
  Runnable three ways: scheduled (`SessionizeImportJob`, daily 02:00 UTC, **delta**), organizer
  buttons on `/Organizer/SessionizeImport` (**delta** "Sync new speakers" + **full** "Full import
  from Sessionize", handlers `OnPostApiAsync` / `OnPostApiFullAsync`), and the OneShot CLI
  (`import-speakers`, delta). The configured view is the **accepted-speakers** view, so only accepted
  speakers are imported. Scheduled/button pulls never email (`sendWelcome:false`).
  - **The endpoint id is ordinary operator config — NOT a secret.** It is bound to
    `Sessionize:EndpointId` from non-secret config: `integrations.<edition>.json → sessionize.endpointId`
    and/or, for local DEV, the gitignored `config/sessionize.<edition>.custom.json`. The real per-edition
    id stays OUT of the public mirror (private `config/` is denylisted from publish; public docs /
    config-examples keep the `REPLACE_WITH_YOUR_SESSIONIZE_API_ID` placeholder), but it is plain config,
    **not** a Key Vault secret.
  - **Emails require an advanced field.** Sessionize omits emails from the default JSON; the organizer
    must enable the "speaker emails" advanced field on the API view, or every speaker is skipped
    (the client emits a clear warning saying so). **Email is the import match key and is mandatory** —
    a speaker with no email is skipped + reported. The email is stored as a normal string in the plain
    `Participant.Email` column (not encrypted, not a secret). See the public how-to in README/§ below.

#### Sessionize → Endpoint admin settings + change handling (Replace/Merge) — 2026-06-15
The endpoint id can be set/edited from the hub at `/Organizer/SessionizeEndpointSettings`
(organizer-gated, mobile-first, a11y) on top of the config-bound default, and an **endpoint CHANGE**
drives a Replace-vs-Merge re-import choice. It is **config + flow only — it never runs an import.**

- **Persistence.** `SessionizeEndpointSetting` (EF migration `SessionizeEndpointSetting`, one row per
  edition, unique on `EventId`) stores the operator endpoint id + `View`, the `EndpointLastChangedAt`
  stamp + `PreviousEndpointId`, and the chosen `SessionizeChangeMode` (`None`/`Replace`/`Merge`) with
  `ChangeModeChosenAt`. The endpoint id is **operator config, not a secret** — placeholders only in
  committed/public files; the real id stays in the saved row / private config, never in the repo.
- **Effective id + live update.** `SessionizeEndpointSettingsService.GetEffectiveEndpointIdAsync` =
  the saved row's id when non-blank, else the config-bound `Sessionize:EndpointId`. On save the
  service updates the in-process singleton `SessionizeApiOptions.EndpointId` (+ `View`) so the live
  `SessionizeApiClient` uses the new id **without a restart**.
- **What counts as a "change".** Setting the endpoint for the **first time** (no prior effective id)
  is **not** a change — there is no already-imported data tied to a prior endpoint. Only an existing
  non-blank id → a **different** non-blank id flags a change (`SaveEndpointResult.EndpointChanged`),
  stamps `EndpointLastChangedAt`/`PreviousEndpointId`, and **resets any prior choice** to `None` so the
  page re-prompts. This is the ELDK26→ELDK27 switch.
- **Replace vs Merge → Full vs Delta.** When a change is flagged the page shows a confirmation prompt;
  `RecordChangeChoiceAsync` persists the choice (it does **not** import) and returns the import mode it
  maps to via the single mapping point `SessionizeEndpointSettingsService.ToImportMode`:
  - **Replace** ⇒ `SessionizeImportMode.Full` — replace existing data, re-import accepted speakers from
    the new endpoint (the **normal production path**; full re-seed, speaker-edited set cleared).
  - **Merge** ⇒ `SessionizeImportMode.Delta` — additive merge (**testing only**); **never flushes a
    speaker's own edits** (honours the speaker delta-sync rule).
  After recording, the page links the organizer to the matching button on `/Organizer/SessionizeImport`
  ("Full import from Sessionize" for Replace, "Sync new speakers (delta)" for Merge) — the deliberate
  two-step keeps the actual write under the operator's hand.

#### Sessionize → import dry-run / preview (REQUIREMENTS §21) — 2026-06-16
Before committing an import, the organizer can preview exactly what would change. `SessionizeImportPreviewService`
reads the **same source** as the real import (the v2 view API via `SessionizeApiClient`, or an uploaded `.xlsx`
via `SessionizeExcelParser`) and replays the **same upsert + bio-merge rules** as
`SessionizeImportService.ImportSpeakersAsync`, but in a **read-only** pass (`AsNoTracking`, no `SaveChanges`).
- **Result.** `SessionizeImportPreviewResult` carries `Fetched/Created/Updated/Skipped` (counted identically
  to the real importer — a row is `Updated` only when the participant NAME changes, else `Skipped`; bio
  changes do not move that counter) plus a per-speaker `Rows` list. Each `SessionizeImportPreviewRow` names
  the action (Create/Update) and the `OverwrittenFields` — the bio fields a **Full** import would replace,
  each flagged `SpeakerEdited` when the speaker had curated it in the hub (the dangerous case). Convenience
  accessors `RowsClobberingSpeakerEdits` / `SpeakerEditedFieldsOverwritten` / `FieldsOverwritten` drive the
  UI's alert.
- **Mode semantics (lock-step with the importer).** A **Full** preview lists every populated field whose
  current value differs from the incoming Sessionize value (replacing an identical value is not flagged),
  including speaker-edited ones. A **Delta** preview overwrites **nothing** (delta only fills genuinely-empty,
  never-edited fields), so it reports `FieldsOverwritten == 0`; it still previews new-speaker Creates and
  empty-field fills.
- **Wiring.** `/Organizer/SessionizeImport` adds Preview buttons — `PreviewApi` (full) / `PreviewApiDelta` /
  `PreviewUpload` (full, file) handlers — that render the counts + a would-overwrite table before any write.
  The real Full import button is gated by the shared `_ConfirmModal`. The page builds the preview only; the
  write stays a separate, explicit click (mirrors the endpoint Replace/Merge two-step).
- **File-based (`.xlsx` upload)** — the fallback / no-network path, unchanged. The organizer exports
  the accepted-speaker list to `.xlsx` and uploads it at `/Organizer/SessionizeImport` (max 5 MB);
  `SessionizeExcelParser` (ClosedXML) locates columns by header name (Email / First Name / Last Name
  / Tag Line), any order. (Speakers only — the **sessions** import below is API-only.)

**Sessions import (linked to speakers; in-hub only) — 2026-06-15.** The API pull also imports
**sessions** from the same v2 view (the `All` view carries both a `speakers` array and a `sessions`
array; the grouped `Sessions`/`GridSmart` views are also accepted). `SessionizeApiClient.ParseSessions`
maps each session to a `SessionizeSession` (id, title, abstract=`description`, room, track resolved from
the `categories`→`categoryItems` ids, start/end, `isServiceSession`, and a **speaker-id array**).
`SessionImportService.ImportSessionsAsync` then upserts a `Session` row **by the Sessionize session id**
within the edition (create-or-update-in-place — same new/changed **delta** semantics as speakers,
**never delete**) and reconciles the **`SessionSpeaker`** many-to-many link set: each Sessionize speaker
id is mapped **id → email** (from the parsed speakers) **→ Participant** (the row the speaker import
created), so a session can link to several speakers and a speaker to several sessions. A speaker id with
no matching participant (emailless/skipped) is **reported + left unlinked**, never dropped silently.
Sessions are **import-owned and in-hub only** — NOT a public/Backstage concern — so there are no
hub-collected session fields to protect; the importer overwrites the imported values and reconciles the
link set to exactly the current Sessionize speakers per session (stale links removed, missing added),
which flushes nothing an organizer owns. The combined `SessionizeApiImportService` runs **speakers
first, then sessions** in one pull (so participants exist to link to), in all three triggers — the
scheduled `SessionizeImportJob` (daily 02:00 UTC), the organizer button on `/Organizer/SessionizeImport`,
and the OneShot `import-speakers` CLI; a session-fetch failure is reported but never fails the
already-committed speaker import (speakers are the critical path). EF migration `SessionizeSessions`
(`Sessions` + `SessionSpeakers`; `Session`↔`Participant` link uses `NoAction` on the participant FK to
avoid a second cascade path to `Participant` from the `Event` root). The **speaker overview**
(`/Organizer/Speakers`) shows each speaker's linked session titles + a total "Sessions imported" header
stat (mobile-first, a11y).

**Session management (hub-only sessions, type/length, room, QR, evaluation) — 2026-06-15.** Sessions are
no longer import-only. The `Session` entity gained `Type` (`SessionType`: `CommunityMasterClass |
CommunityTechSession | SponsorSession`), `Length` (`SessionLength`: `FullDay | TwentyMin | FiftyMin |
SixtyMin`, stored as the int minute value), `IsHubAdded`, room-QR fields (`RoomQrUrl`,
`RoomQrGeneratedAt`), and evaluation fields (`EvaluationFormUrl`, `EvaluationEmailedAt`) — EF migration
`SessionManagement`, with indexes on `(EventId,Type)`, `(EventId,Length)` and `(EventId,Room)` for the
view filters and per-room lookup. Enums are stored as int (`HasConversion<int>()`, consistent with
`Participant.Role`). Four pieces:
- **Defaults for imported sessions.** `SessionDefaultsMapper` (pure/static) derives `Length` from the
  Sessionize start→end duration (nearest of 20/50/60; ≥4 h ⇒ `FullDay`; untimed ⇒ `SixtyMin`) and
  `Type` from length (full-day ⇒ master class, else tech session — the importer never infers
  `SponsorSession`). `SessionImportService` stamps these each run and forces `IsHubAdded=false`.
- **Hub-only sessions.** `SessionManagementService.AddHubSessionAsync` creates a session with a
  **synthetic `hub-<guid>` `SessionizeId`** that the Sessionize upsert (matched by Sessionize id) never
  hits, so a re-import never touches or deletes hub-added sessions. `UpdateSessionAsync` edits
  type/length/room + the QR-eval form URL.
- **Per-room QR (SharePoint seam).** Same clean-seam pattern as the Backstage/exhibitor APIs:
  `IRoomQrProvider` with a no-op default (`NullRoomQrProvider`, `CanProvision=false`, **no faked call**).
  `ProvisionRoomQrAsync` calls the provider to generate the QR (encoding the room deep-link) and store
  the image on **SharePoint**, then stamps the returned image URL + timestamp onto every session in that
  room. With the Null provider it stamps nothing and returns a "not configured" result. The per-session
  **"Download QR"** action (`/Organizer/Sessions?handler=DownloadQr&id=`) redirects the speaker to the
  stored SharePoint image. **Live wiring is ◻ pending (operator config):** the real SharePoint site /
  drive / root folder + the SPN cert that authorises the upload are operator config (Key Vault /
  gitignored), **not in this repo** — register a live `IRoomQrProvider` (e.g. over the existing
  `SharePointUploadClient`) once wired, no caller changes.
- **Evaluation mail hook.** HappyOrNot is a physical box with **no API**, so per-session results arrive
  **manually**; `SessionEvaluationMailService.EmailResultsToSpeakersAsync(sessionId, resultsText)` emails
  the pasted results to every linked speaker (at their preferred address — `ContactEmailOverride ??
  Email`, same routing as the welcome mail), through the `IEmailSender` seam (so the DEV redirect / PROD
  allowlist apply and test sends never reach real people), and stamps `EvaluationEmailedAt`. The
  **results-text argument is the seam** for a future own-devices-via-API ingester (◻) — it would
  populate that text and reuse the same send. A **QR-code evaluation** form URL is stored per session
  (`EvaluationFormUrl`).
The organizer page `/Organizer/Sessions` (`SessionsModel`, organizer-only) surfaces all of it: a Type +
Length **filter** (querystring-bound), **add a hub session**, **provision a room QR**, an inline **edit**
(type/length/room/eval-url) and **email-evaluation** form per row, the **Download QR** link, and a
**"show evaluate link"** action that mints/reuses the session's `PublicToken` and shows the absolute
`/sessions/{token}/evaluate` URL to encode in the room QR; mobile-first (responsive flex, horizontal-scroll
table wrapper) + a11y (labelled controls, semantic table with off-screen caption). Wired into the organizer
nav as `Nav.OrgSessions` (en "Sessions" / da-DK "Sessioner").

**Session delete (REQUIREMENTS §21 CRUD gap) — 2026-06-16.** `SessionDeletionService` (Core/Organizer) is
the single server-side authority for removing a session, mirroring `ParticipantDeletionService`'s safe
pattern. `DeleteAsync(eventId, sessionId)` is **edition-scoped** and:
- **Cleans speaker links first** — `SessionSpeaker` rows are import-state, not engagement, so they are
  removed with the session (never orphaned). The FK from `SessionSpeaker` → `Session` is `Cascade`; removing
  them explicitly keeps the single `SaveChanges` atomic and the intent clear.
- **Refuses on attendee engagement** — a session with `SessionQuestion`s, `SessionEvaluation`s, or
  `MasterClassParticipant` bookings returns `DeletionStatus.Blocked` with human-readable counts and is left
  untouched, so attendee-supplied data is never silently destroyed. (Those FKs are `NoAction`, so a blind
  delete would fail the FK anyway — this turns that into a clear, safe refusal the UI explains.)
- **Flags imported sessions** — a deleted non-`IsHubAdded` session sets `WasImported=true` so the page warns
  that a re-import (matched on its Sessionize id) will recreate it unless removed in Sessionize too.
`GetBlockersAsync` is the read-only probe the grid uses to decide whether to offer the Delete button
(`Row.CanDelete` = no questions/evaluations/bookings) or a "has attendee data" note. The `OnPostDeleteAsync`
handler is gated by the shared `_ConfirmModal` (`Danger`, named via `data-ceh-summary`); en/da strings under
`Sessions.Delete*`.

**Bulk session + volunteer-task operations (§20 universal CRUD + bulk) — 2026-06-16.** Two pure,
edition-scoped Core/Organizer services extend the multi-select bulk pattern (established by
`ParticipantBulkOperationService`) to the next two high-traffic grids, applying the SAME safe semantics as
their single-row counterparts:
- `SessionBulkOperationService.DeleteAsync(eventId, sessionIds)` deletes a SELECTION of sessions exactly the
  way `SessionDeletionService` deletes one: attendee-engaged rows (questions / evaluations / bookings) are
  **left untouched and counted as `Blocked`**, clean rows are removed with their speaker links, and the count
  of deleted **imported** (non-`IsHubAdded`) sessions is returned for the re-import warning. Engagement is
  probed in three **set-based** queries (not per-row N+1); the whole batch is one `SaveChangesAsync`. Returns
  `BulkResult(Matched, Deleted, Blocked, ImportedDeleted)` with a `Skipped(requested)` helper. The Sessions
  grid offers a checkbox **only on `Row.CanDelete` rows** (engaged rows can't be ticked, keeping the bar
  honest) and a `BulkDelete` handler gated by a live-count confirm modal; en/da strings under
  `Sessions.BulkDelete*`.
- `VolunteerTaskBulkOperationService` adds `ChangeStatusAsync(eventId, taskIds, status)` (idempotent — a task
  already in that status is skipped; returns `BulkResult(Matched, Changed)`) and `DeleteAsync(eventId,
  taskIds)` which is **linked-data-safe**: a task with `VolunteerHelpRequest` history is **kept** (counted as
  `Blocked`) so coordination history is never lost, while clean tasks are removed with their import-state
  `VolunteerTaskAssignment` links — one `SaveChangesAsync`. The volunteer work structure already had full
  single-row CRUD via `VolunteerStructureService`; `/Organizer/VolunteerStructure` now adds a sticky bulk bar
  whose checkboxes (plain — the task rows host their own forms) feed a JS-built hidden form to the
  `BulkStatus` / `BulkDeleteTasks` handlers, confirm-modal gated. Both services are event-scoped (foreign-edition
  ids are silently ignored) and DI-registered in `Program.cs`.
- `HotelBulkOperationService.DeleteAsync(eventId, hotelIds)` (REQUIREMENTS §20, 2026-06-17) deletes a SELECTION
  of hotels exactly the way `HotelManagementService.DeleteHotelAsync` deletes one: every participant placed in
  a doomed hotel is **un-assigned first** (`Participant.HotelId` → null) in one set-based query (not per-row
  N+1) so no foreign key dangles, then the hotels are removed in a single `SaveChangesAsync`. Unlike sessions a
  hotel carries no attendee-supplied data to protect, so nothing is `Blocked`; the result
  `BulkResult(Matched, Deleted, Unassigned)` (with a `Skipped(requested)` helper) reports the un-assign side
  effect so the banner is honest. Event-scoped (foreign-edition ids silently ignored), DI-registered in
  `Program.cs`. `/Organizer/Hotels` adds a select-all + per-row checkbox + a `BulkDelete` handler gated by a
  live-count confirm modal; en/da strings under `OrgHotels.BulkDelete*` / `OrgHotels.SelectRow`. No schema change.

**Speaker delete + sponsor company-facts delete (REQUIREMENTS §22 CRUD sweep) — 2026-06-17.** Two more pure,
edition-scoped Core/Organizer services close the remaining delete gaps on the high-traffic organizer grids,
both mirroring `SessionDeletionService`'s delete-safely shape (probe → refuse-on-engagement → clean →
one `SaveChangesAsync`):
- `SpeakerDeletionService` "un-speakers" a person: a speaker is a `Participant` (role Speaker /
  MasterclassSpeaker) plus an optional `SpeakerProfile`, and `DeleteAsync(eventId, participantId)` removes
  the **profile only** (bio / photo / accreditation / publish flag / contact override) so they stop being a
  speaker, while the **participant row — identity, login, logistics — is never touched** (removing the whole
  person stays `ParticipantDeletionService`'s job). It is **agenda-safe**: a speaker still linked to a
  `SessionSpeaker` returns `DeletionStatus.Blocked` with the session count (the running order is never
  silently orphaned); a clean speaker has their profile removed plus the stale `SpeakerBackstageEmailSync`
  propagation artifact cleaned with it. `DeleteManyAsync` is the bulk counterpart (set-based session-link
  probe, one `SaveChanges`, `BulkDeleteResult(Matched, Deleted, Blocked)` + `Skipped`), and
  `GetBlockingSessionCountAsync` is the read-only probe driving `Row.CanRemove`. `/Organizer/Speakers` adds a
  per-row **Remove from speakers** button (bespoke confirm modal naming the speaker) and a **bulk** remove
  button (live-count confirm modal), each posting a hidden out-of-grid form (the grid is already wrapped in
  the set-flag `bulkForm`, so nested forms are avoided); en/da strings under `Speakers.Remove*` /
  `Speakers.BulkRemove*` / `Speakers.OnAgenda`.
- `SponsorInfoDeletionService` deletes a stale `SponsorInfo` company-facts row (the one-row-per-company
  logos/description/website/tier that drives the public `/Sponsors` page; sponsor **contacts** are
  `Participant` rows already covered by the Participants grid). `DeleteAsync(eventId, sponsorInfoId)` is
  **live-company-safe**: it refuses (`DeletionStatus.Blocked` + active-contact count) while the company still
  has any **active** sponsor contact in the edition (its public card is live), and removes only a genuinely
  orphaned row. `GetActiveContactCountAsync` is the probe; `/Organizer/Sponsors` computes the orphan set
  (facts rows whose company id is not in the active-contact set) into a **Stale company facts** section, each
  row deletable behind a bespoke confirm modal posting a hidden `DeleteFacts` form; en/da strings under
  `SponsorFacts.*`. Both services are DI-registered in `Program.cs`; no schema change.

**Pre-selection queue delete (REQUIREMENTS §21) — 2026-06-16.** Queue rows ARE `Participant`s (Inactive /
Preselected lifecycle), so `/Organizer/PreselectionQueue` reuses the shared `ParticipantDeletionService`:
`OnPostDeleteAsync` hard-deletes a clean row and **safely falls back to deactivate** if the row somehow has
dependent data (never orphans links) — the same semantics as the Participants grid delete. Confirm-modal
gated; en/da strings under `Queue.Delete*`.

**Per-session attendee evaluation (HappyOrNot-style public rating + organizer dashboard) — 2026-06-15.**
A quick public rating, distinct from the pre-event attendee *questions* (§ below): a new `SessionEvaluation`
entity (EF migration `SessionEvaluations`) holds a **1–5 `Rating`**, an optional `Comment`, a soft per-attendee
de-dup `VoterKey`, and an `IpHash` — edition-scoped, FK to `Session` with `NoAction` (the `Event` cascade
already covers sessions + evaluations), with a **filtered-unique** index on `(SessionId, VoterKey)` (the
one-per-attendee/session upsert key — NULLs, i.e. cookie-less submits, never collide), plus `(EventId,SessionId)`
and `(EventId,IpHash)` indexes for the dashboard and the rate-limit. `SessionEvaluationService` is the single
authority:
- **Public token is SHARED with the ask page.** `EnsurePublicTokenAsync` / `ResolveByPublicTokenAsync` mint/resolve
  the **same** `Session.PublicToken` the ask page uses (one unguessable 256-bit token addresses both
  `/sessions/{token}/ask` and `/sessions/{token}/evaluate`), so the room QR encodes the evaluate URL without a
  second secret.
- **Public submit (no auth), one rating per attendee/session (soft).** `SubmitPublicEvaluationAsync` validates the
  rating range, caps the comment, and **upserts on `VoterKey`** (a same-device re-rate updates the existing row,
  never stacks a duplicate); a cookie-less submit just adds a row. Returns null for an unknown token (→ 404).
- **Organizer results dashboard.** `BuildDashboardAsync(eventId, type?, room?)` returns **per-session** aggregates
  (count, rounded average, anonymous comments newest-first) and **per-room** roll-ups (session count, total ratings,
  average), plus edition totals — **filterable by `SessionType` and room**; `ListRoomsAsync` feeds the room filter.
The public page `/Sessions/Evaluate` (`EvaluateModel`, `[AllowAnonymous]`) renders a mobile-first (~360px) smiley
scale (radio-group, keyboard-navigable, `aria-label`led) + optional comment, with the **same spam-resistance as the
ask / survey forms** — a honeypot (`Website` → silent 200, no write), a soft IP-hash rate-limit, and a per-session
`HttpOnly`/essential **voter cookie** (`ceh_eval_{sessionId}`) carrying the de-dup token. The organizer dashboard
`/Organizer/SessionEvaluations` (`SessionEvaluationsModel`, organizer-only) renders the per-room + per-session
tables with smiley averages and collapsible comment lists, mobile-first + a11y (scoped table headers, off-screen
captions, `role="status"` summary); wired into the organizer nav as "Session evaluations". **Future ◻
(own-devices-via-API ingestion):** a live ingester would write the same `SessionEvaluation` rows and reuse the same
dashboard — no caller changes.

**Public sessions overview — 2026-06-15.** A read-only, no-login page `/Sessions`
(`Sessions/IndexModel`, `[AllowAnonymous]`) lists the **active edition's** sessions for anyone to browse,
backed by `PublicSessionsService` (in `CommunityHub.Core.Reminders`). The service resolves the active
event the same way the public volunteer-signup page does (`Events.Where(IsActive).OrderByDescending(Id)`),
projects each non-service `Session` to a `PublicSessionRow` (title, abstract, Type/Length, room, track,
start/end, the linked speaker name(s), and the two public deep-links a session may expose), and applies
the filters: **Type**, **Length**, **Room** (case-insensitive exact), and a free-text **search** over
title / abstract / speaker / room / track. It returns a `PublicSessionsView` (rows + the distinct-room
facet + a total/match count) or **null** when there is no active event (the page renders a friendly empty
state; a no-match filter renders zero rows but keeps the total/facets). Rows are ordered scheduled-first
(by start), then room/title. Each row deep-links to the session's **master-class logistics page**
(`/MasterClass/{PublicSlug}`, surfaced only for `CommunityMasterClass` rows that have a minted slug) and
its **public ask page** (`/sessions/{PublicToken}/ask`, when a token has been minted). Service sessions
(breaks/lunch) are excluded; the page is edition-scoped so another edition's sessions never leak.
Mobile-first (single-column session cards, 2-up filter grid ≥640px) + a11y (`role="search"` filter
form with labelled controls, `role="status"` live result count, semantic list). Reachable from the
primary nav for every signed-in role and from a small anonymous nav for visitors; nav label
`Nav.Sessions` (en "Sessions" / da-DK "Sessioner").

**Public speaker lineup — 2026-06-15.** A read-only, no-login page `/Speakers` (`Speakers/IndexModel`,
`[AllowAnonymous]`) lists the **active edition's** published speakers, backed by `PublicSpeakersService`
(in `CommunityHub.Core.Reminders`, same active-event resolution). **The HARD GATE lives in the query's
`Where` clause:** a speaker is included ONLY when `SpeakerProfile.SelectedForPublish == true` AND the
participant `IsActive` AND holds a speaker role (`Speaker`/`MasterclassSpeaker`). `SelectedForPublish`
defaults **false** (the same flag the Backstage bio sync gates on — §6), so until the organizer selects the
lineup the service returns a `PublicSpeakersView` with an **empty** speaker list and the page renders a
"lineup coming soon" empty state; a selected speaker appears automatically with no second switch. Each
`PublicSpeakerRow` carries name, tagline, `PhotoUrl`, and the linked non-service session titles (ordered by
start); a missing photo renders a two-initial monogram (`PublicInitials.From`). Returns **null** when there
is no active event. Edition-scoped (another edition's selected speakers never leak). Mobile-first (1-up
cards, 2-up ≥560px) + a11y (semantic list, photo `alt`, `role="status"` count). Anonymous nav label
`Nav.Speakers` (en "Speakers" / da-DK "Talere"). **No schema change** — reuses the existing
`SpeakerProfile.SelectedForPublish` + `PhotoUrl` + `Tagline` fields.

**Public landing & detail pages (front door + session/speaker detail + cross-linking) — 2026-06-15.**
The site root and the per-item detail pages that make the public programme deep-linkable. **No schema
change** — all read-only projections over existing `Event` / `Session` / `SpeakerProfile` fields.
- **Landing at `/`.** The root `IndexModel` is now `[AllowAnonymous]` and serves two audiences from one
  route: an **anonymous** request renders the public landing branch (`IsAnonymous=true`, `Landing` set
  from `PublicLandingService`); a **signed-in** participant gets the unchanged role hub (the hub branch
  still redirects genuinely-gated data to Login, e.g. the first-run `/Welcome` hop). `PublicLandingService`
  (in `CommunityHub.Core.Reminders`, same active-event resolution) returns a `PublicLandingView` with the
  edition name/dates/`PreDayDate`/venue, the **non-service session count**, and `HasSelectedSpeakers`
  computed from the **same §6 hard gate** as the speakers page — or **null** when no event is active (the
  view shows a friendly empty state). The view renders an event hero (name + `IndexModel.FormatDateRange`
  + venue + blurb), a **Visit-event / Sign-in** CTA, and four cards into `/Sessions`, `/Speakers`,
  `/Sponsors`, and `/Sessions?FilterType=CommunityMasterClass`. The `[AllowAnonymous]` is what lifts the
  old `[Authorize]` bounce — anonymous visitors reach the landing in place rather than being redirected
  to Login.
- **Session detail `/Sessions/{id:int}`.** `Sessions/DetailModel` (`[AllowAnonymous]`) →
  `PublicSessionsService.GetByIdAsync(id)`: resolves one non-service session **scoped to the active
  edition** (404 otherwise, so an old-edition or service-session id can't be poked), projecting a
  `PublicSessionDetail` with title/abstract/Type/Length/room/track/time, the master-class slug + ask
  token, and the speaker list as `PublicSessionSpeaker(ParticipantId, Name, IsPublished)`. The route
  `{id:int}` constraint keeps it distinct from the two-segment ask route `/sessions/{token}/ask`. The
  session-list rows now link the **title** to this page.
- **Per-session `.ics` (2026-06-15).** `PublicSessionCalendarController` (`[AllowAnonymous]`) serves
  `GET /Sessions/{id:int}.ics` → `PublicSessionsService.BuildIcsAsync(id, host)`, which calls
  `GetByIdAsync` (same active-edition / non-service / real-id gate) and returns `null` when the session
  isn't publicly resolvable **or has no `StartsAt`** (an unscheduled talk → 404). It reuses the existing
  `IcsCalendarBuilder.BuildFeed` (the same builder as the per-user feed) to emit a single
  `METHOD:PUBLISH` VEVENT with a **stable UID `session:{id}@{host}`** (re-download updates, never
  duplicates), `LOCATION` = "Room, Venue", and a description of speaker name(s) + truncated abstract; no
  `ORGANIZER`/`ATTENDEE` address (a public talk, not a personal invite). `PublicSessionDetail` gained a
  `VenueName` field (from the active `Event.VenueName`) for the location line. The detail page renders an
  "Add to my calendar" download link **only when the talk is scheduled**. `Cache-Control: no-store` so a
  moved/renamed talk re-downloads fresh.
- **Speaker detail `/Speakers/{id:int}`.** `Speakers/DetailModel` (`[AllowAnonymous]`) →
  `PublicSpeakersService.GetByIdAsync(participantId)`, which enforces the **same hard gate as the
  lineup** (selected + active + speaker role in the active edition) — an unselected, withdrawn, or
  non-speaker id 404s. Returns a `PublicSpeakerDetail` (name, tagline, `Biography`, photo, sessions).
- **Cross-linking + the gate.** `PublicSessionRow.Speakers` and the detail speaker list both carry
  `IsPublished` (the §6 gate, evaluated per speaker as a correlated `SpeakerProfiles.Any(...)`); the
  views link a speaker name **only when published** and render an unpublished co-speaker as plain text —
  so a session can list a co-speaker honestly without ever exposing an unselected one. `PublicSpeakerRow`
  / `PublicSpeakerDetail` sessions are `PublicSpeakerSession(SessionId, Title)` so the speaker pages link
  each session back to its detail page. All pages mobile-first + a11y; landing/detail copy is bilingual
  via `Landing.*` / `SessionDetail.*` / `SpeakerDetail.*` resx keys (en + da-DK).
- **Public day-by-day agenda `/Agenda` (2026-06-17).** `AgendaModel` (`[AllowAnonymous]`) →
  `PublicAgendaService.BuildAsync()` (in `CommunityHub.Core.Reminders`, same active-event resolution as
  the other public pages). The service runs ONE flat, SQL-translatable projection of the active edition's
  **non-service** sessions into `RawAgendaSession` rows (id/title/type/length/room/track/start/end + the
  raw speaker-name list), then hands them to the **pure** `PublicAgendaBuilder.Build(displayName, rows)`,
  which: drops the **unscheduled** talks (no `StartsAt` → not on a timetable) while counting them
  (`UnscheduledCount`), groups the rest by **venue-local day** (`DateOnly.FromDateTime(StartsAt.Date)`,
  i.e. the date in the talk's own offset, matching how Sessionize publishes the grid), orders the days
  chronologically and, within a day, orders items by **start → room → title** (deterministic), joining +
  alphabetising each talk's speaker names for display. Returns a `PublicAgendaView` (display name +
  ordered `PublicAgendaDay`s + scheduled/unscheduled counts, `IsEmpty` helper) or **null** when no event
  is active. **No schema change** — read-only over existing `Session` fields. The page renders one
  `<section>` per day (sticky day heading) with time-labelled `<article>` slots that deep-link the title
  to `/Sessions/{id}`; it complements (does not replace) the flat, filterable `/Sessions` list. The
  public front door (`/`) gained an **Agenda** card linking it. Mobile-first + a11y (per-day landmarks,
  screen-reader time label, `role="status"` summary); bilingual via `Agenda.*` / `Landing.Agenda*` resx
  keys (en + da-DK). Keeping the day-grouping pure (no DbContext) is what makes the ordering / grouping /
  drop-unscheduled logic directly unit-testable.

**Public sponsors page — 2026-06-15.** A read-only, no-login page `/Sponsors` (`Sponsors/IndexModel`,
`[AllowAnonymous]`) lists the active edition's sponsor companies **grouped by tier** (Platinum → Diamond →
Gold → Feature → "Other supporters"), backed by `PublicSponsorsService`. Sponsors are public, so there is
**no publish gate** — every `SponsorInfo` row for the edition is shown. The display name resolves through the
shared `SponsorCompanyName` fallback chain (the per-company `SponsorUploadLocation.CompanyName` captured at
order-pull time → `"Company {id}"`); the logo uses `SponsorInfo.LogoRasterPath` normalised to a root-relative
URL (non-browser-renderable vector formats `.eps/.ai/.pdf` are dropped so the page falls back to an initials
monogram, never a broken image); the optional link surfaces only an absolute http(s) `SponsorInfo.WebsiteUrl`.
Note the SoMe `GraphicAsset` of `Type=Sponsor` is **internal-only** (§8) and is deliberately **not** used
here — the public logo is the sponsor-uploaded raster, not the internal SoMe graphic. Returns **null** when
there is no active event; an active event with no sponsors renders zero groups (a "sponsors coming soon"
empty state). Edition-scoped. Mobile-first (2-up grid, 3-up ≥560px, 4-up ≥820px) + a11y (per-tier
`<section>`, logo `alt` / monogram fallback, `role="status"` count). Anonymous nav label `Nav.Sponsors`
(en "Sponsors" / da-DK "Sponsorer"). **Schema:** EF migration `SponsorPublicListing` adds
`SponsorInfo.Tier` (a `BoothTier`, default `None`) + `SponsorInfo.WebsiteUrl` (nullable) + an
`(EventId, Tier)` index — the only two additive columns.

**"Become a sponsor" CTA (REQUIREMENTS §21) — 2026-06-17.** The same public `/Sponsors` page now renders a
prospective-sponsor call-to-action. The href is built by the pure `BecomeSponsorCtaBuilder.Build(options,
eventDisplayName)` (no DB/clock/I/O) from a config-bound `BecomeSponsorOptions` (`BecomeSponsor` section,
registered via `Configure<>` in `Program.cs`, injected into `Sponsors/IndexModel` as `IOptions<>`):
precedence is a configured external `ContactUrl` (a hosted prospectus/form, opened in a new tab) **over**
`ContactEmail` (which yields a `mailto:` with the event name URL-encoded into a `subject` via
`EmailSubjectFormat` / a default `"Sponsorship enquiry — {0}"`); when **neither** is set the builder returns
`null` and the page renders **no CTA** (no dead link). It shows in both the "coming soon" and populated
states. **No secret** (a sponsorship contact is public) but the shipped `appsettings.json` carries only a
**blank placeholder** so no real address reaches the public mirror — operators set it per edition in private
config. **No schema change.** Mobile-first; copy bilingual via `Sponsors.Become*` resx keys (en + da-DK).

**Master-class master-class features (public logistics page + Zoho Booking participant sync) — 2026-06-15.**
Built on `Session` + `SessionType.CommunityMasterClass`. Schema additions (migration `MasterClassFeatures`):
`Session.{PublicSlug, LogisticsText, LogisticsUpdatedAt, LogisticsUpdatedByEmail, BookingEndpointUri,
BookingLastSyncedAt}` (filtered-unique index on `PublicSlug`), and a new `MasterClassParticipant`
link entity (`(EventId, SessionId, BookingRecordId)` **unique** = the idempotency key; `Session` +
`Participant` FKs are `NoAction` because the `Event` cascade already covers the edition root).
- **Public logistics page.** `MasterClassLogisticsService` mints an unguessable URL-safe `PublicSlug`
  (144-bit, lazy on first "show public link"), resolves it to a read-only view, and applies edits gated
  by `CanEditAsync` (TRUE for an **Organizer** in the edition, or a participant **linked as a speaker of
  that session**). The page `/MasterClass/{slug}` (`MasterClass/IndexModel`, `[AllowAnonymous]`) renders
  the published text to **anyone with no auth** (logistics text is HTML-encoded with line breaks
  preserved — no sensitive data); a signed-in eligible viewer additionally sees an inline edit form whose
  POST **re-checks the scope server-side**, so there is **no anonymous write path** (spam-resistant by
  construction). The organizer session view + the speaker hub both expose a **"show public link"**
  affordance (the speaker hub adds a "My master classes" card listing each linked master class with its
  public link + an edit/view shortcut).
- **Zoho Booking 1-way participant sync.** The per-master-class **Booking endpoint URI** is
  organizer-set in master-class management (stored on `Session.BookingEndpointUri`; plain config, not a
  secret). `MasterClassBookingSyncService.SyncSessionAsync` pulls bookings through the
  `IMasterClassBookingFetcher` seam — **no-op default `NullMasterClassBookingFetcher`** (`CanFetch=false`,
  **no faked call**, same clean-seam pattern as `IRoomQrProvider`) — then **upserts** each booking by
  `(EventId, SessionId, BookingRecordId)`: matches/creates the hub participant by email (lower-cased),
  links it, and is **idempotent** (re-sync updates in place, never duplicates). A brand-new booked
  participant is created `LifecycleState=Inactive` / `Role=Attendee` so it **cannot sign in** until an
  organizer validates it in the pre-selection queue; a cancelled booking flips the link's `IsActive`
  to false rather than deleting it (history preserved). **Live wiring is 🟡 pending (operator config):**
  the real Booking endpoint URI is per-master-class config and the fetch creds (OAuth) are Key Vault —
  register a live `IMasterClassBookingFetcher` (e.g. over the existing `ZohoClient` Bookings call) once
  wired, no caller changes.

**Public how-to — get a Sessionize API endpoint id:** in Sessionize open the event → **API/Embed** →
create a new API endpoint → name it → choose **JSON** → include all built-in fields → enable the
**speaker emails** advanced field (required, or every speaker is skipped) → configure the
**accepted-speakers** view → **save** → copy the endpoint id. The URL shape is
`https://sessionize.com/api/v2/<your-event-id>/view/All` (or `/view/Speakers`); the `<your-event-id>`
segment is the endpoint id. It is ordinary operator config (not a secret): put the real id in your
private `integrations.<edition>.json` / gitignored custom config; keep the
`REPLACE_WITH_YOUR_SESSIONIZE_API_ID` placeholder in the public mirror.

**Attendee questions per session (pre-event, public, hub-only) — 2026-06-15.** Attendees can ask a
question for a session before the event via a PUBLIC, no-login page. A new `SessionQuestion` entity is
linked to `Session` (FK `NoAction` on both `Session` and the responder `Participant`, since the `Event`
root already cascade-deletes the edition's sessions + questions — a second cascade path would be
ambiguous to SQL Server) and carries: optional `AskerName`/`AskerEmail` (anonymous asks allowed),
required `QuestionText` (≤2000), `IpHash` (soft rate-limit, never PII'd back), `Status`
(Open→Answered→Closed), and the response (`ResponseText`, `RespondedByParticipantId`/`Email`,
`RespondedAt`). The public page is addressed by an **unguessable per-session token**
`Session.PublicToken` (256-bit URL-safe, filtered-unique index so the many NULLs before first-mint don't
collide), minted on demand — NOT the sequential id, so the URL `/sessions/{token}/ask` cannot be
enumerated. EF migration `SessionQuestions` (adds the table + the `Sessions.PublicToken` column/index).

One server-side authority, `SessionQuestionService` (in `CommunityHub.Core.Domain`), enforces the
visibility model in one place regardless of caller: PUBLIC submit (no auth) lands the question in the
hub ONLY and never auto-public; ORGANIZERS see/answer everything in the edition
(`LoadAllForEventAsync`); a SPEAKER sees/answers only sessions they're linked to via `SessionSpeaker`
(`CanAccessSessionAsync` / `LoadForSessionAsync` / `LoadMySessionsAsync`), and because the per-session
read returns the same set to every linked speaker, a co-speaker's response is visible to the others on
the same session. It throws `SessionQuestionAccessDeniedException` (→403) /
`SessionQuestionValidationException` (→friendly message). Spam handling mirrors the public survey:
the page (`/sessions/{token}/ask`, `[AllowAnonymous]`) has a CSS-off-screen honeypot (`Website`; any
non-empty value → silent 200, no write) and a soft per-IP rate-limit (`CountRecentByIpHashAsync`, 8/hr,
SHA-256-truncated hash). UIs are mobile-first + a11y: the public ask page, organizer
`/Organizer/SessionQuestions` (all questions grouped by session + each session's shareable ask link),
and speaker `/Speaker/Questions` (own sessions, co-speaker-visible replies). Use case: masterclass
logistics / topics attendees want covered.

### Brevo (email transport)
SMTP relay `smtp-relay.brevo.com:587` STARTTLS, sender `info@your-domain.example`. The SMTP **username is
a Brevo-issued login (e.g. `8xxxxxx@smtp-brevo.com`), not the account email**; credentials in Key
Vault. See §7.

### SoMe graphics & SharePoint asset store
The hub generates social-media graphics for speakers and sponsors, stores graphics + speaker pictures on
SharePoint, and lets speakers self-share — built as a **clean seam + stub for every external system** so
the engine, schema and gates are real and tested offline while real creds/site/URLs stay operator config.

**Code shape** (`CommunityHub.Core/Integrations/Graphics/`):
- **`GraphicCompositor`** — pure **SixLabors.ImageSharp** (Apache-2.0 3.1.x line; **not** the
  commercial 3.2+/4.0 split-license line) compositing. Merges a template background PNG + a speaker photo
  / sponsor logo + a name/title and returns PNG bytes. **No System.Drawing** (no libgdiplus on Linux App
  Service). Degrades gracefully: if the host has no font it still composites the image without the text
  overlay rather than throwing.
- **`ISharePointFileStore`** (seam) — store bytes under a stable key-derived path, return a download URL,
  delete by path. `NullSharePointFileStore` (`CanStore=false`) is the default; `GraphSharePointFileStore`
  (wraps the existing `SharePointUploadClient`: Graph client-credentials, cert-preferred, byte
  upload/delete) is selected **only** when `Graphics:SharePoint` is configured (site URL present) **and**
  the upload client has SPN creds. Selection happens in the web `Program.cs`.
- **`ISpeakerPictureFetcher`** (seam) — fetch a Sessionize-provided picture URL **down** to bytes;
  `HttpSpeakerPictureFetcher` is the default (tolerant: blank/failed fetch → null). The service stores the
  bytes on SharePoint so the hub holds the copy, not just a foreign URL.
- **`ISocialShareGateway`** (seam) — builds LinkedIn/X **drafts** (prefilled text + composer-intent URL);
  `DraftOnlySocialShareGateway` is the default and **cannot post** (`CanPost=false`). Per-user OAuth
  posting is a future slice; even when wired the "I'm speaking" button always yields a draft.
- **`GraphicsService`** (orchestrator) — generate (composite → store → upsert by stable key, status
  `Generated`), the **release gate**, the **overrule** (replace bytes at the same stable path, keep the
  link), the visibility queries (speaker sees only `Released` + never sponsor; sponsor surface is always
  empty of sponsor graphics), the **fetch-and-store picture** path, and the **share-draft** builders.
- **`AssetLocationService`** — per-persona-group SharePoint location admin (pointers only, no creds).
- **`IBrandingGraphicsProvider`** (consumer contract) — a **read-only seam that EXPOSES publishable
  branding graphics** for a downstream consumer. The §19 SoMe scheduling queue **is now that consumer**
  (`SoMeQueueService` calls this from its auto path — see §19); the contract stays generic so any other
  consumer can use it too. For a speaker / session / sponsor it
  returns a `BrandingGraphicRef` (stable key, type, **image ref** in the exact shape a `SoMePost.ImageRef`
  expects, file name, and a ready **draft text**). **The gate is already applied:** a speaker / session
  graphic is exposed **only once released**; a sponsor graphic is exposed because it is internal-only. The
  default `BrandingGraphicsProvider` reads the `GraphicAsset` rows + resolves the speaker/session display
  strings and builds the draft via the same `GraphicsService` share-draft builders, so a consumer never
  reaches into the graphics tables or re-implements the gate. **Direction:** Graphics exposes; the consumer
  calls — Graphics never references a SoMe type, so the two slices stay decoupled. The draft text is built
  from a caller-supplied `BrandingEventContext` (event name / dates / public ticket URL) so the provider
  hard-codes nothing.

**Schema** (EF migration `GraphicsAssets`, see §3): `GraphicAsset` (`Type` speaker/sponsor/session,
`Status` generated/released, `StableKey` edition-unique idempotency identity, `SharePointPath`/`Url`/
`StorageItemId`, `IsOrganizerOverridden`, release audit) + `GraphicsAssetLocation` (per (edition,
persona group): site/drive/root/browse-URL/notes).

**The stable-key contract.** `GraphicStableKey` builds a deterministic key (`speaker:{id}`,
`session:{sid}:speaker:{pid}`, `sponsor:{companyId}`); the SharePoint path/file name derive from it. A
regenerate **upserts by the key**; an **overrule replaces the bytes at the same path** and sets
`IsOrganizerOverridden` (so a later regenerate won't clobber the human design) — the hub→SharePoint link
never changes.

**Gates & visibility.** Speaker/session graphics default `Generated` and are **hidden from the speaker
until an organizer releases** them (mirrors the §6 `SelectedForPublish` bio gate). Sponsor graphics are
**internal-only** — the named `GetSponsorFacingAsync` returns empty by construction so the invariant can't
be violated by accident.

**Surfaces.** `/Speaker/Graphics` (released graphics: download + LinkedIn/X share drafts + the "I'm
speaking at ELDK27" announcement draft), `/Organizer/Graphics` (review/release queue + overrule upload +
internal sponsor graphics), `/Organizer/AssetLocations` (per-persona SharePoint links). All mobile-first
(~360px) + a11y (labels, `role="status"/"alert"`, new-tab hints).

**Operator config (flagged ◻/🟡 — never committed).** `Graphics:SharePoint` (site/drive/root) +
`SharePoint` SPN creds (Key Vault) for the live store; the persona-group SharePoint links (operator-entered
in the admin page); per-user LinkedIn/X OAuth for posting. Until set, the null store / draft-only gateway
keep everything inert with nothing faked.

### LinkedIn company-page SoMe scheduling queue (REQUIREMENTS §19)

An organizer-curated **scheduled-post queue** that publishes to the event's LinkedIn **company page** on a
timer (a "social-media calendar"). It is **distinct from §18's `ISocialShareGateway`**: that builds per-user
DRAFT share-intent links a speaker finalizes himself; THIS is the scheduled queue that publishes to the
event's own company page. Same clean-seam-+-null-stub design tenet — the queue, schedule, gates, tagging,
pre-alert and notifications are all real and tested offline; only the actual LinkedIn POST is inert until the
operator wires it.

**Schema** (EF migration `SoMeQueue`, see §3):
- **`SoMePost`** — one queued post: `Type` (`SoMePostType`: Sponsor/Speaker/AdHoc), optional links
  (`ParticipantId`/`SessionId` soft pointers with `NoAction` FKs; `SponsorCompanyId` external id),
  `ScheduledAtUtc`, `Status` (`SoMePostStatus`: Queued/Published/Failed), `IsActive` (the
  publish-or-not toggle, distinct from status), `AutoText` + `ManualTextOverride` (the override wins via
  `EffectiveText`), `ImageRef`, `Tags` (newline-separated handle/URN list → `TagList`), `AutoGenerated`,
  and the dispatch audit (`PublishedAtUtc`, `ExternalPostId`, `LastError`, `SpeakerPreAlertSent`). Indexed
  `(EventId, Status, IsActive, ScheduledAtUtc)` for the dispatcher's due-query.
- **`SoMeSettings`** — one row per edition: `Enabled`, `CompanyPageUrlOrOrgId` (operator config, NOT a
  secret — placeholder only in committed files, like the Sessionize endpoint id),
  `SpeakerPreAlertOrganizerEmail`, `NotificationEmails` + `NotifyOnPublish`. **The LinkedIn OAuth access
  token is intentionally NOT a column** — it is a Key Vault secret (`linkedin-some-access-token`) read by
  the live publisher only.

**Code shape** (`CommunityHub.Core/Integrations/`):
- **`ILinkedInPostPublisher`** (gated seam) — `NullLinkedInPostPublisher` is the default (`CanPublish=false`,
  throws if called, **no faked post id**). A live publisher (LinkedIn UGC/Posts API,
  `w_organization_social`, post to `urn:li:organization:{id}`) returns a real post id; no caller changes.
- **`SoMeQueueService`** (curation authority) — create speaker/sponsor/ad-hoc posts, fine-tune text/image,
  Active/Inactive toggle, reschedule, **Preview** (`SoMePostPreview` = the exact `EffectiveText`/image/tags
  + a `WillPublish` flag = Active && Queued + an `AwaitingApprovedGraphic` flag), and `RefreshAutoAsync`
  (re-populate auto text/tags/image; a no-op when `AutoGenerated` is off — static wins; never touches the
  override). **The auto path is wired to the §18 graphics contract** (see below).
- **The auto-branding ↔ graphics link (§18↔§19).** `SoMeQueueService` consumes `IBrandingGraphicsProvider`
  (constructor-injected; **optional** — null = no image is attached, the post stays "awaiting"). When a post
  is `AutoGenerated`, `PopulateAutoAsync` (used by create + `RefreshAutoAsync`) pulls `SoMePost.ImageRef`
  from the provider for the linked speaker / session / sponsor — a Speaker post with a `SessionId` prefers
  the per-session graphic and falls back to the speaker graphic; a Sponsor post takes the internal sponsor
  graphic. **The release gate is the provider's**, already applied: a gated / un-released / missing graphic
  comes back `null`, so the auto path attaches **no image** rather than a broken ref and the post reports the
  derived **`SoMePost.IsAwaitingApprovedGraphic`** = `AutoGenerated && ImageRef is blank` ("awaiting approved
  graphic"). **Override / auto-off win:** an image is only filled when `ImageRef` is currently blank (a
  manually-set image is never clobbered), and an `AutoGenerated = false` post never consults the provider at
  all (static wins, refresh is a no-op). **One-way dependency:** SoMe consumes the graphics contract; Graphics
  never references a SoMe type. **No schema change** — `IsAwaitingApprovedGraphic` is `[NotMapped]` (derived).
- **`SoMeDispatchService`** (publish authority) — for an edition: send the T-5 speaker pre-alerts, then
  publish every due Active Queued post through the gated publisher. **Idempotent: the post status is the
  sent-marker** — a published post flips to Published with an `ExternalPostId`, so a re-run never re-posts
  (same contract as the `SentReminder` ledger); a failure flips to Failed + records `LastError`, never
  silently dropped. Gated: posting requires `Enabled` + a configured page + `CanPublish` — otherwise due
  posts are SKIPPED and left Queued, nothing faked.
- **`SoMeTagBuilder`** (pure) — the compliance authority: `ForSponsor` = signer + event coordinator +
  resolved company name (`SponsorCompanyName.Resolve` public→legal→billing chain); `ForSpeaker` =
  organizers only.
- **`SoMeTextComposer`** (pure) — default auto bodies for speaker/sponsor posts.
- **`SoMeSettingsService`** — per-edition settings upsert (token never persisted here).
- **`SoMeDispatchJob`** (`CommunityHub.Jobs`, NCRONTAB `0 */5 * * * *`) — runs the dispatcher every 5 min
  for each active edition; the short cadence is needed because posts fire at exact times and the T-5
  pre-alert needs ~5-minute granularity (the daily reminder job is too coarse).

**Compliance / tagging limit (the rule that bites).** The LinkedIn API **cannot tag an external speaker**
from a company-page post (they are not members of the posting organization and the Posts API has no
person-mention for non-connections). So a **speaker post tags organizers only**, never the speaker —
instead the **T-5-minute pre-alert** emails the designated organizer 5 minutes before publish so they can
manually insert the speaker's real LinkedIn handle. A **sponsor post** tags the signer + event coordinator
+ the sponsor company (name via the `company_name_public` chain).

**Surfaces.** `/Organizer/SoMeQueue` (list + Preview + Active/Inactive + reschedule + fine-tune + ad-hoc
compose), `/Organizer/SoMeSettings` (enable/disable, company page, pre-alert organizer, notification array
+ toggle). Both organizer-gated, mobile-first (~360px) + a11y. All outbound (pre-alert + publish
notifications) goes through `IEmailSender`, so the DEV redirect / PROD allowlist apply.

**Operator config (flagged 🟡 — never committed).** The LinkedIn company-page URL / organization id
(operator config, placeholder only) and the LinkedIn OAuth access token (Key Vault secret
`linkedin-some-access-token`). Until both are wired + posting is enabled, the Null publisher keeps the
queue inert with nothing faked.

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

### Welcome email for all roles with one-click auto-login (DEV-only)

A second, distinct welcome path (separate from the once-ever `welcome.html` above): the
**welcome-with-login** email (`WelcomeWithLoginEmailService` in `CommunityHub.Core/Reminders/`,
template `welcome-login.html`). It is the onboarding nudge for the brand-new Hub — sent to **every
role**, mobile-first (~360px), single CTA, **HTML + plain-text** (a `multipart/alternative`), and it
explains how the Hub fits alongside Zoho Backstage.

- **Real auto-login (magic-link), not a `?email=` prefill.** The CTA — *"Open my Event Hub — signs
  you in automatically"* — points at `/Login/Magic?token=…`, carrying a per-recipient
  **DataProtection-signed token** (`{ParticipantId, ExpiresAtUtc, Nonce}`, URL-safe, tamper-evident,
  no secret in the URL). Following it authenticates the recipient and the handler redirects to `/`,
  i.e. their role hub. The token mechanism is the **same one the invitation flow already uses**,
  factored into Core behind `IMagicLinkTokenFactory` (shipped impl `MagicLinkTokenFactory`) so the
  Core email service can mint it without depending on the web project; the web-project
  `MagicLinkService` now delegates to it, so links minted before and after the refactor are
  byte-compatible. The welcome uses a **7-day** TTL (shorter than the invitation's 14 days). The URL
  is built from the **per-environment base URL** (`Request.Scheme`/`Request.Host`), e.g. dev
  `https://dev.hub.your-event.example`.
- **Per-role copy.** One role-specific line per recipient via `WelcomeWithLoginEmailService.RoleLine`
  — every `ParticipantRole` (Organizer / Speaker / MasterclassSpeaker / Volunteer / Sponsor /
  Attendee / Video / Camera) has a distinct, non-empty line. Plus the Backstage explanation and the
  "brand-new this year, reply with feedback" framing.
- **DEV-ONLY hard guard.** The send is refused unless the host is Development, read via the Core
  `IEnvironmentInfo` seam (web adapter `HostEnvironmentInfo` over `IHostEnvironment.IsDevelopment()`).
  This is a **code-level gate, independent of `Email:RedirectAllTo`** — even a misconfigured redirect
  cannot leak this email from a non-DEV host. In DEV the normal redirect (all mail → the DEV test
  address) still applies, so test sends never reach real people. The organizer page
  (`/Organizer/SendWelcomeLogin`) also hides the send control and refuses early outside DEV.
- **Re-sendable + recorded.** Deliberately *not* idempotent (so it can be tested repeatedly): each
  send stamps `Participant.WelcomeWithLoginSentAt` with the send time, so the DB records who was sent
  and when, without gating a re-send. (Contrast the once-ever `WelcomeEmailService`, which dedups via
  `SentReminder`.)
- **Plain-text part.** `IEmailSender` gained a `SendAsync(to, subject, html, text, ct)` overload;
  `BrevoEmailSender` sends it as a true `multipart/alternative` (text + HTML `AlternateView`s).

### Organizer email center — broadcast (audience filters + reusable templates)

The organizer **broadcast** (`/Organizer/Broadcast`) composes one message and sends it individually
(branded `broadcast.html` shell, personal "Hi {firstName}," header) to a chosen audience. The
**Email Center** (`/Organizer/EmailCenter`) sits alongside it for template preview, a test-send to
self, and the delivery ledger. Both are organizer-gated and event-scoped.

- **Reusable message templates** are **code constants** (`BroadcastTemplates.BuiltIn` in
  `CommunityHub.Core/Email/`) — *blank*, *generic announcement*, *friendly reminder*, *welcome /
  introduction*. The organizer picks one (the `LoadTemplate` handler fills Subject + Message), then
  **edits it freely** before previewing/sending. **No schema / no migration**: there is nothing
  per-edition to persist because the loaded text is always customised in place. If persisted custom
  templates are ever needed, that is the only thing that would warrant a table.
- **`{Token}` substitution** (`BroadcastTemplates.Substitute`, single-brace, case-insensitive) is
  applied to the organizer's subject + body **per recipient**: `{FirstName}` (falls back to "there")
  and `{EventName}` (the edition display name). An unknown token is left **verbatim** (a mistyped
  `{Foo}` survives rather than vanishing). Distinct from the branded shell's own `{{token}}` layer —
  the shell still renders the greeting header, so the built-in bodies deliberately do **not** repeat
  "Hi {FirstName},".
- **Audience filters** (`BroadcastAudienceFilter.Resolve`, a pure function) narrow the candidate
  rows: by **role group** (any subset of `ParticipantRole`), optionally **attendees** (reconciled
  from Zoho — always treated as active, no role/test flag), by **status**
  (`ActiveOnly` (default) / `InactiveOnly` / `All`, applied to participant roles only), and an
  **exclude-test-users** toggle (default **on**, so a real broadcast never mails the synthetic
  `IsTestUser` go-live cast). The result is deduped by email (case-insensitive; a participant wins
  over an attendee with the same address) and ordered for a stable preview.
- **Preview = what is sent.** The page loads the rows and hands them to the same `Resolve` call used
  by the send loop, so the previewed **recipient count + recipient list** (email / first name /
  group, capped at 200 for display) is exactly the set the send iterates. Send remains resume-safe:
  one `SentReminder` row per delivery keyed `broadcast:<subject-slug>`, so re-sending the same
  subject only mails recipients not yet in the ledger, and per-recipient failures are counted without
  aborting the batch.
- **Test-send path intact.** The DEV redirect of all mail to a single test inbox
  (`Email:RedirectAllTo`) and the PROD `Email:OnlySendTo` allowlist apply unchanged — the broadcast
  goes through the same `IEmailSender`.
- **Mobile-first + a11y.** The audience controls are a `<fieldset>`/`<legend>`; the role checkboxes a
  labelled `role="group"`; the recipient list a captioned `<table>` inside a `<details>`; status
  messages carry `role="status"`; the preview `<iframe>` is sandboxed + titled. A reusable `.sr-only`
  utility was added to the layout.

**Change notifications:** editing a hotel/dinner/shift record **after** the event lock date emails
the organizers flagged `[LATE CHANGE]`; edits before the lock date send nothing.

### 7a. Email system build-out (REQUIREMENTS §10a)

Built on the existing center: the shared `IEmailSender` + `EmailTemplateProvider` + the resume-safe
`SentReminder` ledger + the DEV redirect / PROD allowlist are reused, not duplicated.

- **Central audit log via a decorator.** `LoggingEmailSender` (in `CommunityHub.Core/Email/`)
  **decorates** `BrevoEmailSender` and is registered as the `IEmailSender` everything resolves, so no
  call site can bypass it. It writes one **`EmailLog`** row per send (category, the original To, the
  post-redirect *actual* To, CC, participant, name, subject, success/error) then delegates. It reuses
  the sender's own `BrevoEmailSender.ResolveDelivery` so the logged outcome matches the real
  redirect/allowlist gate. A send failure is logged **then re-thrown** (preserving the throw-on-failure
  contract); a log-write failure is swallowed (an audit miss must never drop mail). The decorator is a
  singleton and opens a fresh scoped `CommunityHubDbContext` per write via `IServiceScopeFactory`. Rich
  fields come from an ambient **`EmailContext`** (`IEmailContextAccessor`, `AsyncLocal`-backed) a caller
  sets in a `using` just before sending. Organizer view: `/Organizer/EmailLog`, edition-scoped, newest
  first, **filter by name OR email** (substring) + category; indexed on `(EventId, SentAt)`,
  `(EventId, ToEmail)`, `(EventId, ParticipantId)`. **No FK** to Event/Participant — it is an audit
  trail that must survive a delete (and a bootstrap mail has `EventId 0`).
- **Participant routing in one place.** `ParticipantEmailService` resolves the **primary To** =
  `SpeakerProfile.EffectiveEmailFor(identity, override)` and the **CC** = the participant's
  `SecondaryEmail` (10a-5), sets the `EmailContext`, renders the template and sends. It is the single
  seam for the manual re-send (10a-2), the onboarding set (10a-1) and the step-reset reminder (10a-6).
- **Onboarding sets + auto-send (10a-1).** `OnboardingEmailSets` maps `ParticipantRole` → a
  `PersonaGroup` (organizer / speaker (incl. Master Class) / volunteer / media-team (video+camera) /
  sponsor) → an ordered list of `(stepKey, templateName)` (code-defined, no schema). `OnboardingEmail`
  templates `onboarding-getting-started.html` + `onboarding-your-tasks.html` are shipped.
  `OnboardingEmailService.SendOnboardingSetAsync` sends each **not-yet-sent** email, recording each in
  the `SentReminder` ledger keyed `onboarding` / `onboarding:{step}` on the **identity** address — so a
  second activation pass never re-sends. The activation hook: `PreselectionQueueService.AdvanceAsync`
  now returns the **newly-activated ids**, and `ParticipantActivationService.ActivateAndOnboardAsync`
  (used by the single + bulk Activate buttons on `/Organizer/PreselectionQueue`) activates **then**
  auto-sends, no approval. The pure queue state-machine stays email-free + independently testable.
- **Persona-aware scheduled reminders (10a-4).** `ReminderMessage` gained `Persona`, `ParticipantId`,
  `RecipientName`, `Cc`; `TaskReminderBuilder` fills them (persona from the assigned role, CC from the
  secondary email); `ReminderEngine` sets the `EmailContext` (category `task-deadline:{Persona}`) around
  each send and passes the CC. Same daily job (08:00 UTC), same 14/7/3/1 milestones, same idempotency.
- **Step-reset consume (10a-6).** `OnboardingStepResetEmailService.SendPendingAsync` drains OPEN
  `OrganizerActionItem`s of type `onboarding-step-reset` (raised by `OnboardingService.ResetStepAsync`),
  emails the person (`onboarding-step-reset.html`, step label parsed from the action summary), then
  **resolves** the item so it is consumed exactly once (a re-run finds nothing → idempotent). Wired into
  the nightly `ReminderJob` and exposed as a "send now" button on `/Organizer/ActionQueue`.
- **Speaker Q&A question digest (REQUIREMENTS §21).** `SpeakerQuestionDigestService.SendPendingAsync`
  emails each speaker a digest of the **open** (unanswered) `SessionQuestion`s on the sessions they are
  linked to. `BuildPendingDigestsAsync` is a pure read: it joins open questions → `SessionSpeaker` →
  active speakers (Speaker / MasterclassSpeaker), edition-scoped, and reduces to one `SpeakerDigest`
  per speaker (open-question count, distinct-session count, and a **fingerprint** = the highest
  open-question id). The send routes through `ParticipantEmailService.SendTemplateToParticipantAsync`
  (`speaker-question-digest.html`) so it reuses the established effective-To + secondary-CC routing AND
  the allowlist-gated `LoggingEmailSender` — there is **no new mail path**, and nothing reaches a real
  speaker until their address passes the allowlist. Idempotency keys on the `SentReminder` ledger
  (`speaker-question-digest` / `upto:{fingerprint}` on the **identity** address, mirroring
  `CalendarInviteEmailService`): a run with the same open set is a no-op; a brand-new question raises
  the fingerprint → a fresh occasion → one updated digest; answering/closing a question shrinks the open
  set but never raises the fingerprint, so it never re-sends. Wired into the nightly `ReminderJob`
  alongside the deadline reminders + step-reset consume. The speaker `/Speaker/Questions` page surfaces
  a live open-count + a digest note (en + da-DK). **No schema change** (reuses `SessionQuestion` +
  `SentReminder`).
- **Schema delta.** One migration `EmailSystem`: `EmailLog` table + `Participant.SecondaryEmail`
  (nullable nvarchar(320)). `has-pending-model-changes` = none.
- **Mobile-first + a11y.** Email Log filter is a `role="search"` form with labelled inputs, a captioned
  table + `.sr-only` caption, `role="status"` count; the Email Center "send to a person" panel is a
  `<fieldset>`/`<legend>`; the Profile secondary-email field is labelled + `type="email"` validated.

### Calendar sync (per-user subscribable iCal feed)

Speakers, volunteers and organizers can **sync their reminders / deadlines to their own calendar**.
The mechanism is a per-user, token-secured, read-only iCal feed — add it once and it auto-updates.

- **Endpoint:** `GET /calendar/{token}.ics` (`Api/CalendarController`, anonymous). Returns a valid
  RFC 5545 `VCALENDAR` (`METHOD:PUBLISH`, `X-WR-CALNAME`, `REFRESH-INTERVAL`/`X-PUBLISHED-TTL` so
  clients re-poll). `Cache-Control: no-store` keeps every poll live. An unknown/revoked token →
  **404** (never reveals whether a token existed). The feed is built fresh from the DB on each
  request, so new reminders, moved deadlines and completed items sync on the client's next poll —
  there is no separate sync store.
- **Token security:** `Participant.CalendarFeedToken` is a 256-bit cryptographically-random,
  URL-safe (no `+`/`/`/`=`) bearer secret, **minted lazily** on first hub view by
  `CalendarFeedTokenService.EnsureTokenAsync`, looked up via a **unique filtered index**, and
  **regenerable** (`RegenerateTokenAsync`) to revoke — a previously-shared URL then 404s. Resolution
  is active-participants-only. The token is the credential (calendar clients fetch without a
  session); it scopes the feed to **exactly one participant's own items** and nothing else.
- **Feed contents (`ParticipantCalendarBuilder`):** one `VEVENT` per item, role coverage falling
  out of the data, not a per-role branch — speaker milestone deadlines (the dated `ParticipantTask`
  rows the `SpeakerDeadlineSeeder` creates), the participant's assigned dated tasks (organizers,
  volunteers), sponsor-company-scoped tasks for a matching sponsor contact, and `VolunteerAvailability`
  shifts (all-day informational entries — shift times are not modelled). Deadlines are all-day
  (`DTSTART;VALUE=DATE`) with `VALARM` reminders **7 and 1 days before** due. Every item has a
  **stable `UID`** (`task:{id}@{host}`, `shift:{volId}:{slug}@{host}`) so a re-fetch UPDATES the
  entry instead of duplicating it; the single-item "Download .ics" reuses the same UID, so
  download-then-subscribe never doubles up. The owner's e-mail used for `ORGANIZER`/`ATTENDEE` is the
  speaker's **`EffectiveEmail`** (`SpeakerProfile.ContactEmailOverride ?? Participant.Email`) — so a
  speaker who set a preferred contact address gets their calendar invites there. Non-speakers / unset
  override fall back to `Participant.Email`. *(delivered 2026-06-15 — see §3 data model + §7 email.)*
- **UI:** the hub landing (`/Index`) shows an **"Add to my calendar"** card for speaker / masterclass
  / volunteer / organizer roles, with the `webcal://` subscribe URL + copy button + a "Subscribe"
  link + collapsible Outlook/Google/Apple instructions, plus a per-item **"Download .ics"** link on
  each dated pending task (the `OnGetCalendarItem` page handler). Mobile-first (~360px): controls
  go full-width below 380px. The base URL is **per-environment** — derived from the request host
  (`Request.Scheme`/`Request.Host`), so dev and prod each emit their own URL with no extra config.
- **Reuse:** the shared `IcsCalendarBuilder` gained `BuildFeed(...)` (multi-VEVENT `METHOD:PUBLISH`
  + VALARM + RFC 5545 CRLF line endings and 75-octet line folding) alongside the existing single
  `BuildVEvent(...)` used by the e-mailed hotel/dinner/group-photo invites.

**Calendar sync enhancements (2026-06-15):**
- **Short canonical route:** the feed is served at **`GET /cal/{token}.ics`** (canonical) with
  `GET /calendar/{token}.ics` kept as an alias so already-shared subscriptions keep working
  (both `[HttpGet]` on `CalendarController.GetFeedAsync`). The hub now advertises the `/cal/` URL.
- **.ics invite on activation:** `CalendarInviteEmailService.SendActivationInviteAsync` sends one
  calendar-invite email when a person is activated — an RFC 5545 `VEVENT` (`METHOD:REQUEST`) for the
  edition itself (all-day, pre-day→end-date, stable UID `event-{eventId}-{email}`), routed to the
  speaker **effective address** (override ?? identity) and sent via the existing
  `IEmailSender.SendWithIcsAsync` so the **DEV redirect / PROD allowlist apply unchanged**. It is
  **idempotent** via the `SentReminder` ledger (type `calendar-invite`, occasion `activation`, keyed
  on identity), and wired into `ParticipantActivationService.ActivateAndOnboardAsync` so it fires
  alongside the persona onboarding set for each newly-activated id.
- **Organizer enable/disable switch:** `Event.CalendarSyncEnabled` (bool, **defaults true**, EF
  migration `CalendarSyncSetting` with SQL default `1` so existing editions stay enabled). When OFF:
  `CalendarFeedTokenService.ResolveParticipantIdAsync` returns null → the feed **404s** for the whole
  edition (token value preserved, so re-enabling restores access without re-minting); the hub's
  "Add to my calendar" card is hidden (`IndexModel` skips token minting); and the activation invite
  is skipped. Toggled on the organizer-gated page **`/Organizer/CalendarSettings`** (mobile-first,
  a11y; no new table — the flag lives on the edition row).
- **Assigned volunteer tasks now in the feed (Top-8 #8):** `ParticipantCalendarBuilder` adds a
  volunteer's **assigned** `VolunteerTask` rows (via `VolunteerTaskAssignment`) that carry a due date
  and are not `Cancelled`, as all-day VEVENTs with stable UID `voltask:{id}` (a 1-day-before VALARM;
  the free-text shift/time-end window goes in the DESCRIPTION since the catalogue is config-driven).
  `BuildSingleVolunteerTaskAsync` powers the per-shift "Download .ics" on `/volunteer/myschedule`,
  scoped to the volunteer's own assignment. No schema change.

---

## 8. Feature surface (hubs & organizer areas)

Each role lands on a hub built around what that person needs to do. The screenshots below are
captured headlessly against a locally-run instance seeded with synthetic demo data.

| Organizer command center | Organizer dashboard |
|---|---|
| [![Organizer command center](img/organizer-command-center.png)](img/organizer-command-center.png) | [![Organizer dashboard](img/organizer-dashboard.png)](img/organizer-dashboard.png) |

| Speaker hub | Volunteer "My schedule" |
|---|---|
| [![Speaker hub milestone tracker](img/speaker-hub.png)](img/speaker-hub.png) | [![Volunteer My schedule](img/volunteer-schedule.png)](img/volunteer-schedule.png) |

| Sponsor portal | Attendee "My Event" |
|---|---|
| [![Sponsor portal](img/sponsor-portal.png)](img/sponsor-portal.png) | [![Attendee My Event](img/attendee-my-event.png)](img/attendee-my-event.png) |

*The role-gated hubs: organizer command center + live dashboard, the speaker milestone tracker, the volunteer schedule, the sponsor portal, and the attendee "My Event" home.*

**Navigation / information architecture — two role-gated groups.** The signed-in top nav is split
into two clearly separated, role-gated groups instead of one flat list that mixed personal and admin
items:
1. **Participant / "My event" group** — the always-visible primary `nav` (`aria-label` `Nav.MyEvent`):
   Home, My tasks, My profile, Resources, plus the self-service hubs/forms each role is entitled to
   (Hotel, Dinner, Lunch, Swag, Travel, Volunteer shifts, the Speaker hub, the Sponsor/Attendee areas).
2. **Organizer / "Organizer area" group** — a distinct second-level management bar (`nav.manage-bar`)
   rendered as a no-JS disclosure dropdown (native `<details>`/`<summary>`, so it carries
   `aria-expanded` for free, is keyboard-operable, and **collapses to a single toggle on ~360px** with
   the long admin list flowing inline rather than overflowing the phone viewport). It consolidates
   *every* organizer tool under one heading — the previously-flat links **plus** the organizer pages
   that existed but were never in the nav (Overview, Participants, Pre-selection queue, Onboarding,
   Action queue, Volunteer structure, Sessionize endpoint settings, Asset locations, Group photos,
   App game, Acting-as log).

The split + gating is computed server-side by the pure, unit-tested **`CommunityHub.Core.Navigation.NavBuilder`**
(`NavModel`/`NavGroup`/`NavItem`): `Build(role)` returns the participant group for everyone and **only**
appends the management group when `role == Organizer`. A non-organizer's `NavModel` therefore never
contains a single `/Organizer/*` item — the gate is genuine server-side composition, not a CSS hide
(covered by `NavBuilderTests`). `_Layout` renders from the model and resolves each item's label from a
resx key (`Nav.*`) so i18n stays in the view; no route is renamed or removed — links are only regrouped
and a few existing-but-unwired organizer pages are surfaced.

**Grouped organizer menu (REQUIREMENTS §21).** The management menu is a second IA layer on top of the
consolidated hubs: each management `NavItem` carries an optional `SectionKey` (a `Nav.OrgSection*` resx
key), and `NavGroup.Sections()` buckets the flat `Items` list into ordered, named `NavSection`s
(first-seen order preserved, items keep their order within a section). The three most-used entries —
Organizer home, Command center, Dashboard — have a `null` `SectionKey` and form a leading headingless
"prominent" bucket; the rest are grouped into six named sections: **People** (`/Organizer/People`),
**Sessions** (`/Organizer/Content`), **Comms** (`/Organizer/Comms` + `/Organizer/SoMe`), **Sponsors**
(`/Organizer/SponsorAdmin/Index`), **Volunteers** (`/Organizer/Volunteers`) and **Logistics**
(`/Organizer/Logistics` + `/Organizer/Setup` + `/Organizer/ImpersonationLog`). `_Layout` renders the
prominent bucket as plain links, then each named section as a nested native `<details>/<summary>`
disclosure (no JS, keyboard-operable, mobile-first, `.org-section*` styling) — the section containing the
current page is rendered `open` so `aria-current` is visible without a click. This is pure information
architecture: `Sections()` is a view-only projection of `Items`, so the menu membership, the routes, and
the server-side organizer gate are all unchanged (`NavBuilderTests` asserts the flattened sections equal
the flat list exactly — no link dropped or duplicated).

**Welcome** — a one-time per-participant landing page per edition.

**Evergreen, every-role pages** (`[Authorize]`, shown to every signed-in role in the primary nav):
- **My profile** (`/Profile`) — a participant views/edits their own basics. Reuses the existing
  `Participant.FullName` + `Participant.Phone` fields (no schema, no migration); `Email` (sign-in
  identity) and `Role` (organizer-set) are shown read-only. Every read/write is scoped to the
  signed-in participant's OWN row (`Id == me.ParticipantId && EventId == me.EventId`) — a participant
  can never load or save another person's profile. The header greeting reads the name from the
  session cookie (stamped at login), so a changed name shows everywhere after the next sign-in; the
  profile page itself always shows the saved value. Name is required + trimmed (≤200), phone optional
  + trimmed (≤40, nulled when blank).
- **Resources** (`/Resources`) — a shared, read-only page of practical info / links / downloads the
  organizers maintain for all crew (venue + floor plan, event site, exhibitor/crew guide, "email the
  organizers", etc.). **Content is pure config, not a database row** — it lives in
  `event.<edition>.json → resources` (a sibling of `edition`) and is read through the existing
  `EventEditionConfigLoader` (`ResourcesConfig` / `ResourceSection` / `ResourceLink`). The loader
  defensively drops links missing a label or url and sections with no title and no links, so the page
  never renders a dead/blank entry; a missing/broken/empty block renders a friendly "nothing here
  yet" empty state (never a 500). `ResourcesConfig.IsEmpty` is the empty-state signal. Same content
  for all roles by design. `config/event.<edition>.json` is publish-denylisted, so the real links
  stay private; the public mirror degrades to the empty state until a community fills in its own block.

**Role hubs** (each personalized by `ParticipantRole`):
- **Speaker** — tasks + overdue-only reminders; collect/maintain hotel (with .ics), appreciation
  dinner (with .ics), swag/polo, travel-reimbursement claim, lunch participation, speaker info
  (accreditation, country, first-time). Generated milestone deadline tasks feed the reminder engine.
  The ELDK27 milestone deadlines are **absolute dates** (configured in
  `speaker-deadlines.<edition>.json`, seeded daily by `ReminderJob`):
  - **Submit title + abstract — 20 Jun 2026** *(masterclass speakers only)*.
  - **Verify bio + photo in the hub — 1 Oct 2026**.
  - **Upload draft preview deck — 20 Jan 2027**.
  - **Upload final deck — 3 Feb 2027**.

  *(There is no "Confirm A/V + room" task.)*

  A dedicated **Speaker hub** (`/Speaker/Index`, speaker/master-class-speaker only) presents those
  milestone tasks as a self-service journey: a `SpeakerMilestoneService` (Core) read-model loads the
  speaker's `speakerdl:`-keyed deadline tasks and derives per-milestone status + a live countdown
  (`DaysUntilDue`, overdue/due-today) and overall progress (done/total/percent/overdue, "next up");
  the page renders a progress bar + per-milestone cards with a scoped **mark-done / reopen** toggle
  (`ToggleAsync` re-asserts the `(EventId, ParticipantId, speakerdl:)` scope so a speaker can flip
  only their own milestones). It seeds deadline tasks on entry (idempotent, same `SpeakerDeadlineSeeder`
  as `/Index`) so a freshly-imported speaker never sees an empty tracker, and is deliberately decoupled
  from how those tasks are dated (absolute vs offset), so it keeps working whatever deadline model is in
  effect. Mobile-first (single-column cards, full-width actions at ~360px). The service is registered in
  `Program.cs` DI.

  Beyond the milestone tracker, the Speaker hub (REQUIREMENTS §20 Speaker) also surfaces four
  self-service blocks, all read-only and own-row scoped:
  - **"My sessions"** — a new Core `SpeakerSessionsService.GetMySessionsAsync(eventId, participantId, role)`
    returns the signed-in speaker's OWN (non-service) sessions in this edition. **Own-row scope is
    server-enforced**: the query filters to `EventId == eventId` AND `SessionSpeakers.Any(ss =>
    ss.ParticipantId == participantId)`, so a speaker can never see another speaker's other sessions; a
    non-speaker role returns an empty list. Each row carries room, start/end, a master-class flag
    (`Type == CommunityMasterClass`), the open-question count
    (`Questions.Count(q => q.Status == Open)`), and the co-speaker names (the viewer excluded). The card
    shows time/room (with clear "to be scheduled" / "room to be assigned" placeholders), the master-class
    badge, a per-session link to the speaker's attendee-questions page (`/Speaker/Questions`, with the
    open count), and — only when the speaker is selected for publish — a link to the public session page
    (`/Sessions/{id}`).
  - **"My session ratings"** (`/Speaker/Evaluations`, 2026-06-17) — a new Core
    `SpeakerEvaluationsService.GetMyEvaluationsAsync(eventId, participantId, role)` projects the
    post-session attendee evaluations (`SessionEvaluation`, the HappyOrNot-style 1–5 rating + optional
    anonymous comment gathered via the room-QR public page) for the signed-in speaker's OWN sessions only.
    **Own-row scope is server-enforced** with the SAME filter as "My sessions" (`EventId == eventId` AND
    `SessionSpeakers.Any(ss => ss.ParticipantId == participantId)`, non-service sessions only); a
    non-speaker role returns an empty result. Each session row carries the rating count, the rounded mean
    (`null` until the first rating), the master-class flag and room, and the non-blank comments **newest
    first**; the payload also rolls up an overall cross-session count + average. The page (linked from a
    Speaker-hub card + the speaker nav, `Nav.SpeakerEvaluations`) shows a per-session smiley/score plus the
    anonymous comments, mobile-first + a11y (`role="status"`, per-comment rating `aria-label`), en + da-DK.
    The same evaluations already feed the organizer dashboard (`SessionEvaluationService.BuildDashboardAsync`)
    and the organizer-triggered results email (`SessionEvaluationMailService`); this is the speaker's
    always-on self-service view of the same data for their own talks. Read-only; **no schema change**
    (projects over existing `Session` / `SessionEvaluation` fields). The comments are anonymous —
    `SessionEvaluation` never stores attendee identity, so nothing here exposes who rated.
  - **Public-profile preview** — gated on the §6 hard gate: the hub resolves `/Speakers/{id}` (via
    `Url.Page("/Speakers/Detail")`) and shows the preview link ONLY when the speaker's
    `SpeakerProfile.SelectedForPublish` is `true` (which is exactly when `/Speakers/{id}` resolves rather
    than 404s). While unselected the hub explains the preview unlocks at line-up announcement and links to
    `/Forms/Speaker` to polish the bio/photo.
  - **Calendar reminders** — reuses the per-person `.ics` feed (`CalendarFeedTokenService.EnsureTokenAsync`
    → `webcal://{host}/cal/{token}.ics`, the same feed the volunteer My-schedule uses), shown only when the
    edition's `Event.CalendarSyncEnabled` is on; token-minting failures are logged and degrade gracefully.

  These services are registered in `Program.cs` DI. All four blocks are read-only and add **no schema
  change** (they project over existing `Session` / `SpeakerProfile` / `SessionEvaluation` / `Event` fields).
- **Volunteer** — interest sign-up (unconfirmed); a confirmed-only view (congrats mail, hotel,
  dinner, swag, lunch, assigned tasks with sync-to-calendar). The in-hub shift form is the single
  3-step `/Forms/VolunteerWizard` (shifts → role/hours → review), which writes `VolunteerAvailability`
  and wires the follow-up tasks; the older single-page `/Forms/Volunteer` was retired (2026-06-14) so
  there is one canonical path. The public, login-free `/volunteer/signup` page remains the external
  recruitment entry point. **Volunteer work structure** (2026-06-15) adds three pages on top of the
  shift wizard, all driven by `VolunteerStructureService`:
  - `/Organizer/VolunteerStructure` — the organizer tree-management page: create/rename/delete
    categories, set the lead (organizer) and **appoint the supervisor** (volunteer), add/remove
    subcategories and tasks, assign volunteers, and see per-category coverage. Organizer-only.
  - `/volunteer/supervisor` — the supervisor dashboard: for the categories a volunteer supervises,
    manage subcategories/tasks, assign volunteers, and answer help requests. Scoped server-side to
    exactly their categories; a volunteer who supervises nothing sees an empty-state.
  - `/volunteer/mytasks` — the volunteer "My tasks" view: assigned tasks grouped Category →
    Subcategory, update own progress, and **"Ask supervisor for help"** (raises a `VolunteerHelpRequest`
    on the task, routed to the category's supervisor with the lead able to see it; on save it also
    fires `VolunteerHelpNotificationService` to **email the supervisor + CC the lead**, best-effort).
    The hub home page surfaces the assigned-task count and a supervisor-dashboard link when the
    volunteer runs a category.
  - `/volunteer/myschedule` — the volunteer **unified "My schedule" / "My day"** (REQUIREMENTS Top-8 #8 /
    §20 Volunteer; 2026-06-15). ALL of a volunteer's assigned tasks flattened into ONE **time-ordered**
    list (dated first by due date, then shift window, then title; undated last), each entry showing
    where/when (bucket + subcategory, due date, free-text shift window) and the **go-to people** (the
    bucket's supervisor(s) via `LoadSupervisorsAsync` + the ELDK lead). The aggregation is the pure,
    unit-tested **`CommunityHub.Core.Volunteers.VolunteerScheduleBuilder`** (`VolunteerSchedule`/
    `VolunteerScheduleEntry`). The page reuses the same in-page progress update + "ask for help" as
    `/volunteer/mytasks`, and adds a **personal calendar**: the per-user feed (`/cal/{token}.ics`) now
    also carries a volunteer's assigned dated tasks (stable UID `voltask:{id}`, cancelled excluded —
    see §7a Calendar sync), plus a single-shift `.ics` download handler scoped to the volunteer's own
    assignment. **No self event-check-in** (out of scope — Zoho Backstage). Volunteer-only in the nav.
  - `/volunteer/myshifts` — the volunteer **self-service shift management** page (REQUIREMENTS §20
    Volunteer "Shift availability + decline/swap + per-task instructions"; 2026-06-16). For each shift a
    volunteer is assigned (the same `VolunteerScheduleBuilder` entries, now carrying the per-shift
    `DecisionStatus`/`DecisionNote`), they can **confirm** they can take it, **decline** it, or **request a
    swap**, each with an optional note, and read the shift's **per-task instructions**. All writes go through
    the pure, unit-tested **`CommunityHub.Core.Volunteers.VolunteerShiftService`**, which loads the
    `(EventId, ParticipantId, TaskId)` assignment from the session-supplied participant id (never the client)
    and **403s** if the volunteer is not assigned — `Decline`/`SwapRequested` stamp the assignment and raise
    ONE coordinator-visible item per volunteer on the existing organizer action queue (new type
    `OrganizerActionItemService.TypeVolunteerShiftReassign` = `volunteer-shift-needs-reassignment`,
    summarising all their flagged shifts); `Confirmed`/withdraw clear the decision and resolve the item when
    nothing is left. The decision lives on `VolunteerTaskAssignment` (additive columns `DecisionStatus` int,
    `DecisionNote` nvarchar(1000), `DecisionAt` — migration `VolunteerShiftDecision`); the assignment is
    never deleted (a coordinator reassigns). **No self event-check-in / live headcount** (out of scope — Zoho
    Backstage). Volunteer-only in the nav.
- **Sponsor** — `/Sponsor/Portal` is the **single self-service home** (REQUIREMENTS §20 Sponsor;
  2026-06-16): one Sponsor-role-gated page scoped to the signed-in sponsor's `SponsorCompanyId` that
  aggregates company profile + raster logo (resolved public name via the shared `SponsorCompanyName`
  chain, monogram initials fallback) + booth **tier**, booth/logistics quick-links (floor-plan /
  exhibitor-guide from the event-edition config + a link to `/Sponsor/Logistics`), the shared
  **deliverables checklist** (`_ChecklistCard` over `ParticipantChecklist`), a **read view of leads**
  (visible count + the most-recent few, junk excluded), and **order/invoice status** read straight from
  the ERP link entities (`ErpCustomerLink` / `ErpOrderLink`): an order with an `ErpOrderNumber` shows its
  invoice, one without shows *pending*, and when no ERP customer link carries a number the page says
  *invoicing not configured yet* — it never fabricates an invoice/amount (the live e-conomic seam is gated,
  REQUIREMENTS §7a). All read-only aggregation in the unit-tested Core service
  `CommunityHub.Core.Integrations.Sponsors.SponsorPortalService` (`SponsorPortalView`); the page model only
  resolves the company id, enforces the role gate, and renders. The portal **links out** to — and does not
  replace — the existing deep pages: `/Sponsor/Index` (full company details + orders + contacts, where a
  contact marks tasks complete/reopen scoped to their `SponsorCompanyId`), `/Sponsor/Tasks`,
  `/Sponsor/Logistics`, and `/Sponsor/Contact`. A sponsor with no `SponsorCompanyId` sees a "contact the
  organizers" message; a non-sponsor hits the server-side access gate.
  `/Sponsor/CaptureLead` lets booth staff capture leads in-hub (`SponsorLeadCaptureService`
  → a `ManualBooth` `SponsorLead` row, screened on the way in, surfaced in a recent-leads list and
  out the existing leads export); mobile-first single-column form, Sponsor-role only.
  `/Sponsor/Leads` shows the company's leads-download API/token setup.
- **Attendee** — two pages. `/Attendee/Index` is the focused master-class status +
  deep-link to Zoho Bookings. `/Attendee/MyEvent` is the **"My Event" dashboard**:
  a mobile-first home consolidating a live countdown / "happening now" badge
  (computed from the `Event` row's dates), the master-class status line, the
  practical info (pre-day + conference dates, venue as a map link), and **self
  check-in**. The dashboard view-model is computed by the pure, unit-tested
  `AttendeeDashboardBuilder.Build(record, EventPracticalInfo, now)` in
  `CommunityHub.Core/Attendees/` (no DbContext / no clock, so the date windows are
  testable); the page model only loads data, renders, and persists. **Self
  check-in** (`OnPostCheckInAsync`) stamps `Attendee.CheckedInAt` once, idempotently,
  and re-validates the window server-side (ticket-holder + event-live) so a stale
  page cannot force an out-of-window check-in. Booking itself still stays in Zoho
  Bookings — the hub deep-links out and never re-implements seat reservation.
  **Personal schedule (§20 Attendee My-event, 2026-06-16):** `MyEvent` additionally
  renders a **My sessions** card, the **full agenda**, per-session **ask / evaluate**
  deep-links, and a **quick-links** strip (Hotel / Swag / Lunch + public agenda). The
  agenda data comes from the SAME `PublicSessionsService.BuildAsync` projection the
  public `/Sessions` page uses (active-edition + published-speaker gate), and the
  per-attendee shaping is a second pure, unit-tested builder
  `MyEventScheduleBuilder.Build(publicSessions, record)` in
  `CommunityHub.Core/Attendees/` (no DbContext / no clock). It marks the session(s)
  the attendee booked as **mine** by matching the reconciled `Attendee.MasterClassName`
  (a comma-separated list when double-booked) against the public session titles,
  case-/whitespace-insensitively, and shapes each row's links: `/Sessions/{id}`
  (always), and `/sessions/{token}/ask` + `/sessions/{token}/evaluate` only once the
  session's public token exists. Read-only aggregation — no schema change, no write.

**Unified participant checklist** (REQUIREMENTS Top-8 #7 / §21 Participant; 2026-06-15). The Hub-home
"what's still needed" list is now a SHARED component so the Hub home, the **Tasks page** (`/Tasks`) and
the attendee **My-event** page render one identical "what do I still owe" view instead of competing
landing surfaces. Two parts:
- **Core builder** `CommunityHub.Core.Participants.ParticipantChecklistBuilder` (`ParticipantChecklist`/
  `ChecklistRow`) — pure read over `ParticipantTask`: splits pending/completed, flags **overdue** with a
  day-count (open + past due, against an injected `TimeProvider` so it's deterministic in tests), covers a
  sponsor contact's **company-scoped** tasks (`AssignedParticipantId == null && SponsorCompanyId == mine`),
  and centralises the `SourceKey → form page` deep-link map. It never mutates (the Hub's form-auto-task
  backfill, which DOES write rows, stays on the Hub page).
- **Shared partial** `Pages/Shared/_ChecklistCard.cshtml` (model `ParticipantChecklist`) — renders the
  pending/completed tables + overdue badges, mobile-first + en/da via `@Localizer`, self-contained styles.
  A page that has a per-task `.ics` handler passes its name via `ViewData["ChecklistIcsHandler"]` (the Hub
  passes `CalendarItem`); other hosts simply omit the download link. Covered by `ParticipantChecklistBuilderTests`.

**Organizer hub** (in-handler `Role == Organizer` gate):
- Participants (`/Organizer/Participants`) — filter by role/active, toggle `IsActive` (blocks login).
  **Bulk operations**: tick several rows and *deactivate*, *reactivate*, or *change role* in one
  action. The mutation runs in `CommunityHub.Core.Organizer.ParticipantBulkOperationService`
  (`DeactivateAsync` / `ReactivateAsync` / `ChangeRoleAsync`), which is **event-scoped** (ids from
  another edition are silently ignored — an organizer can only act inside their own event),
  **idempotent** (re-running a deactivate or re-assigning the role a row already has changes nothing),
  and commits the whole batch in one `SaveChangesAsync`. It returns a `BulkResult(Matched, Changed)`
  so the page reports an honest banner (`N participant(s) deactivated, M already in that state, K not
  found.`). No schema change — it only writes the existing `IsActive` / `Role` fields. The page wraps
  the grid in **one** `<form>`: checkboxes post together for a bulk handler, while the per-row toggle
  button targets its own handler via `formaction` (no invalid nested forms). Mobile-first: the bulk
  bar wraps/stacks at ~360px.
  **Search / sort / pagination (server-side, REQUIREMENTS §21).** The grid also takes a free-text
  `Search` (matched over `FullName` + `Email`), a `Sort` column key (`name|email|persona|status`) with
  a `Desc` direction, and a 1-based `PageNo` — all `[BindProperty(SupportsGet=true)]` so links and
  bookmarks keep their state. `LoadAsync` applies the search `Where`, **counts the filtered set**, then
  resolves the page through the shared `CommunityHub.Core.Organizer.GridPaging.Resolve(page, size,
  total)` (clamps the page into `1..TotalPages`, caps size at `MaxPageSize`, computes the SQL `Skip`),
  and finally orders + `Skip`/`Take`s **in the database** — the page never materializes the whole
  edition. Every sort carries an `Id` tiebreak so paging is deterministic. The column headers are
  `<a>` links that toggle direction and carry `aria-sort`; per-row and bulk redirects thread
  `Search/Sort/Desc/PageNo` through so an action returns you to the same place. **Confirm-modal on
  bulk:** the three bulk buttons are `type=button` that open one shared, accessible confirm dialog
  (`role=dialog`, focus move, Esc/backdrop close) showing the **live selected-row COUNT**
  (`OrgGrid.BulkConfirmBody`) before the form is submitted to the chosen `?handler=`; the server still
  re-validates the selection (empty selection is a safe no-op with a "pick at least one" banner).
  **Participant search is one authority — `ParticipantSearchService` (REQUIREMENTS §20 Organizer,
  delivered 2026-06-16).** The filter + sort rules above no longer live inline in the page-model: they
  are factored into a focused, **pure, read-only** `CommunityHub.Core.Organizer.ParticipantSearchService`
  so the grid and the global search can never drift. `Parse(...)` normalizes the loosely-typed query
  strings (unknown sort → name, unparseable status → active, blank text/company → null) into a trusted
  `ParticipantSearchRequest`; `Query(eventId, request)` returns the deferred, event-scoped,
  filtered+sorted `IQueryable<Participant>` (status via `ParticipantActivation.IsActiveExpr` so "active"
  == who can log in; role; **persona** collapsing related roles via `RolesFor`; sponsor company;
  free-text on name+email; `Id`-tiebroken ordering). `ParticipantsModel.LoadAsync` now just calls
  `Parse` → `Query` → `CountAsync` → `GridPaging.Resolve` → `Skip/Take`. A second method
  `GlobalSearchAsync(eventId, text, limit)` powers a dedicated **global "find a person fast"** page
  `/Organizer/FindPerson` (`FindPersonModel`, real-organizer-only + not-acting-as, server-enforced):
  an event-scoped free-text match on name+email returning at most `DefaultGlobalLimit` (clamped to
  `MaxGlobalLimit`) name-ordered `PersonHit`s, each with a lifecycle-correct `IsActive` flag and a jump
  to `/Organizer/EditParticipant` (or "open in the full grid"). The page is mobile-first + a11y (labelled
  `role="search"` box, `role="status" aria-live="polite"` result region with a live count, captioned
  results table) and localized (`Find.*` / `Nav.OrgFindPerson` / `OrgPeople.FindPerson` in
  `SharedResource[.da-DK].resx`); it is a **prominent** organizer-nav entry and a People-hub card.
  Query-time only — **no schema change**.
  **Speakers** (`/Organizer/Speakers`) and **Attendees** (`/Organizer/Attendees`) use the same
  `GridPaging` helper and the same search/sort/paging shape; Speakers' "Apply to selection" bulk is
  confirm-modal-gated identically, while Attendees stays **read-only** (Zoho-reconciled) so it has no
  bulk writes. Strings are localized (`OrgGrid.*` / `OrgAttendees.*` / `OrgSpeakers.*` in
  `SharedResource[.da-DK].resx`). The list/sort wiring is provider-agnostic SQL; **note:** the EF
  *InMemory* test provider mis-orders some string columns (e.g. `Attendee.Email`), so the grid tests
  assert on collation-stable columns + direction-reversal rather than exact alphabetical email order.
  **Sponsors / Leads / Sessions (delivered 2026-06-16)** complete the sweep on the same `GridPaging`
  contract and `OrgGrid.*` strings (no new resx keys, no schema change — all query-time):
  - **Sessions** (`/Organizer/Sessions`) — the "All sessions" table takes `Search` (matched in SQL
    over `Title` + `Room` + any linked speaker name via `SessionSpeakers.Any(...)`), a `Sort` of
    `title|type|length|room` with `Desc`, and a 1-based `PageNo`; `LoadAsync` filters → counts →
    `GridPaging.Resolve` → orders (with an `Id` tiebreak) → `Skip/Take` in the database, so the
    per-row joins (speakers, booked-count) only touch the current page. The page is POST-heavy (Add /
    Edit / ProvisionQr / EmailEval / master-class actions return `Page()`), so a small Razor
    `GridStateFields()` helper re-emits `Search/FilterType/FilterLength/Sort/Desc/PageNo` as hidden
    inputs in every inline form, keeping the organizer on the same filtered/sorted page after an action.
  - **Sponsor leads** (`/Organizer/SponsorAdmin/Leads`, section 4) — adds `Search` (name / email /
    sponsor company id) + `Sort` (`captured|name|company|status`, default `captured` **descending** =
    newest first) + `PageNo` on top of the existing `ShowHidden` / `SponsorFilter` filters; the
    per-lead actions (Processed / Interest / Ignore / Junk / Reply) thread the full state through their
    redirect (`GridRoute`) so an action returns you to the same place. Ignore/Junk stay hidden by default.
  - **Sponsors** (`/Organizer/Sponsors`) — the roster is grouped per company in memory (one edition's
    sponsors is a small, bounded set), then **searched** (company id or any contact name/email),
    **sorted** (`company|contacts|open|done|overdue|total|nextdue`) and **paged** through the same
    `GridPaging.Resolve`; the header company/contact counts stay totals over the whole set. Each company
    row's id is a **drill-down link** to `/Organizer/Participants?SponsorCompanyFilter={id}&RoleFilter=Sponsor&ActiveFilter=all`,
    reusing the Participants grid's existing GET-bound filters — no new endpoint.
  `/Organizer/EditParticipant` adds/edits one person: in addition to name/email/role/active it now
  exposes the **`SponsorCompanyId`** field (set or clear the Company Manager / WooCommerce company a
  sponsor contact belongs to, for sponsor-area scoping), and a **welcome hook** — a "Send the welcome
  email now" tick on create plus a "Send/Resend welcome email" action on edit. Both route through
  `WelcomeEmailService.SendWelcomeAsync`, which is idempotent via the `SentReminder` ledger (same
  guarantee as the Sessionize import path), so a person is never welcomed twice.
- Data grids — `/Organizer/DataGrid` (Participant + HotelBooking inline edits, CSV export) and
  `/Organizer/TasksTable` (task inline edits, CSV export) via `CsvWriter`. Attendee data is
  intentionally **not** editable (Zoho-synced).
- Event overview (`/Organizer/Overview`) — `CommunityHub.Core.Organizer.OrganizerOverviewService`
  builds an `OrganizerOverview` snapshot: a **read-only cross-role aggregation** over the entities that
  already exist for the edition. It surfaces participation counts by role (`RoleCount`), task completion
  overall + per assignee-role + per category (sponsor vs. general) (`CompletionRate`), **speaker
  milestone-deadline progress** (tasks assigned to (masterclass) speakers grouped by title →
  done / overdue / of-N-speakers, `MilestoneProgress`), **volunteer-structure coverage** (of the
  `VolunteerTask` rows, how many have ≥1 assignment vs. open, per category, excluding `Cancelled`,
  `VolunteerCoverage`), sponsor task + lead totals, attendee check-in count, and the **"needs
  attention"** integers (overdue tasks, unassigned volunteer tasks, open `VolunteerHelpRequest`s,
  pending volunteer applications). Every query is **event-scoped** and the service **never writes** —
  calling `BuildAsync` twice yields the same numbers; it is distinct from `ReportingService`
  (operational form-completion + shift coverage) and the Action Queue. **No schema change** — pure
  aggregation, no new table / migration. The page is mobile-first (single-column → wrapping grids at
  ≥560px) and accessible (progress bars carry `role="progressbar"` + `aria-valuenow` with a visible
  numeric caption — never colour alone; the "needs attention" region is `role="status"`; tables use
  scoped header cells).
- Command center (`/Organizer/CommandCenter`) — `CommunityHub.Core.Organizer.CommandCenterService`
  builds a `CommandCenterSnapshot`: the **actionable landing** that answers "is the event on track and
  what do I do next" at one glance. Where `OrganizerOverviewService` is the exhaustive cross-role
  *report* (§11 above), the command center is the *triage* view — it deep-links every number to the grid
  that lets you act on it. It surfaces total/active registrations + attendee count, **onboarding
  completion %** overall and per persona (reusing `OnboardingService.BuildOverviewAsync` so the numbers
  match the Onboarding page exactly, ordered most-outstanding-first), **hotel / swag / lunch / dinner
  headcounts**, **session status** (scheduled = has `StartsAt` + `Room`, service sessions excluded) and
  **sponsor status** (count + sponsor-task %), a prioritized **"what needs my attention"** call-out of
  eight clickable tiles (overdue tasks, due-today, unassigned volunteer tasks, open help requests,
  pending volunteers, attendee reconciliation mismatches, open action items, unscheduled sessions —
  each warns while > 0, reads calm at 0, and carries the page + pre-filter query that lands on exactly
  the matching rows), plus the literal **today's / overdue tasks** list (≤15, overdue flagged). The
  snapshot exposes `AllClear` (no attention tile > 0 and no overdue/today task) for an honest empty-event
  state, and `GeneratedAtUtc` for a "last updated" stamp. Read-only, event-scoped, **never writes**;
  calling `BuildAsync` twice yields the same snapshot. **No schema change** — pure aggregation over
  existing entities, no new table / migration. It is distinct from `OrganizerOverviewService` (the full
  report) and `ReportingService` (operational form-completion / shift coverage). The page is the
  prominent **Command center** entry in the organizer-nav leading bucket (§21 nav grouping), mobile-first
  (two-column stat grid on ~360px phones → wrapping grids at ≥560px) and accessible (per-persona progress
  bars carry `role="progressbar"` + `aria-valuenow` with numeric captions; the all-clear banner is
  `role="status"`, access-denied is `role="alert"`; every tile is a full-card link with an `aria-label`
  carrying its label + count). Localized en + da-DK.
- Comms cockpit (`/Organizer/Comms`) — `CommunityHub.Core.Organizer.CommsCockpitService` builds a
  `CommsCockpitSnapshot` (REQUIREMENTS §20 Organizer "Comms cockpit"): the single place that schedules /
  sends / tracks all outreach, consolidating the previously-fragmented comms tools. It is a **read-mostly,
  edition-scoped aggregation** over data that already exists, reusing three sources: the **`EmailLog`**
  audit (every outbound email after the redirect/allowlist gate — its `Success`/`Error` are read straight
  through, so a row the allowlist dropped is reported `Dropped`, a hard failure `Failed`, never `Sent`),
  the **`SoMePost`** LinkedIn scheduled-post queue (§19) and the **`SentReminder`** ledger. The snapshot
  carries: a unified **`Timeline`** of `CommsTimelineItem`s over both channels (email + SoMe), ordered
  future-scheduled-first then newest-first, windowed to the last 30 days + capped; per-recipient
  **`WhoGotWhat`** (`WhoGotWhatRow`, grouped by `ToEmail`, sent/dropped/failed counts, undelivered-first)
  and per-campaign **`Campaigns`** (`CampaignRow`, grouped by `EmailLog.Category`) — both the **real**
  outcome, never optimistic; **`UpcomingScheduled`** (the next Active-Queued posts that have not fired);
  **`ResendCandidates`** (`ResendCandidate`, one per participant — the most-recent participant-linked
  undelivered email, since a resend needs a person to target); and honest headline counters +
  `AllDelivered`. SoMe outcome mapping: `Published`→Sent, `Failed`→Failed, an **Inactive** Queued post →
  `Dropped` (it will never fire), an Active Queued post → `Scheduled`. The **only write** is the resend,
  performed by the page (not the service) through the existing per-person `ParticipantEmailService`
  (category `manual-resend`) so routing (effective To + secondary CC), branding and EmailLog auditing all
  behave identically — and the resend re-appears on the same cockpit. **No schema change** — pure
  projection, no new table / migration. Distinct from `CommandCenterService` (it extends the
  "attention" idea into comms) and from the Email Center / Email Log / Broadcast / SoMe-queue pages it
  links out to (it consolidates, never duplicates). Mobile-first (wrapping stat grid + horizontally
  scrollable tables on ~360px), accessible (`role="status"` live regions on the counters / banners /
  empty-states + the resend confirmation, captioned tables, `role="alert"` access-denied), localized en +
  da-DK.
- Exports & run-sheets (`/Organizer/Exports`) — `CommunityHub.Core.Organizer.OrganizerExportsService`
  (REQUIREMENTS §20 Organizer). On-site operations run on paper, so this is the **download + print** surface
  for five offline artifacts, each a **pure, read-only, edition-scoped projection** of existing entities:
  **attendee list** (`AttendeeListRow` ← `Attendee`), **lunch headcount** (`LunchHeadcountRow` per pre-day +
  `LunchPersonRow` who-eats-which-day ← `LunchSignup`), **room/session sheets** (`RoomSheetRow` ← `Session` +
  `SessionSpeaker`, service sessions excluded, ordered room→start; the existing `Session.RoomQrUrl` +
  `Session.PublicToken` are **rendered** for printing onto a room sheet, never a check-in surface),
  **volunteer rota** (`VolunteerRotaRow` ← `VolunteerTaskAssignment`, cancelled tasks excluded, carrying
  bucket/subcategory/task + due/shift/time-end), and **badge data** (`BadgeRow` ← active, non-test
  `Participant`: name/role/sponsor-company id — the minimal badge-printer/mail-merge field set, no contact
  details). The screen page renders each as a captioned table; the page model serves the **same** projection
  as CSV via the shared `Export.CsvWriter` (UTF-8 BOM so Excel reads Danish names) through one GET handler
  per artifact. The view is **print-optimized** — an `@media print` block hides the app chrome + the
  download/print buttons (`.ex-no-print`) and drops colour so a browser **Print → PDF** produces a clean
  run-sheet. It introduces **no event check-in / live-headcount** capability (out-of-scope §20): the lunch
  numbers are the preference already collected. Read-only, **no schema change** — distinct from
  `OrganizerOverviewService` / `CommandCenterService` (which aggregate into counts); this flattens to
  row-level artifacts. Organizer-gated server-side (page + every CSV handler re-checks the role); linked from
  the **Logistics hub** (`/Organizer/Logistics`) and the organizer Index (kept off the top-level nav so the
  consolidated menu stays short). Mobile-first ~360px (tables scroll horizontally; cards stack), accessible
  (captioned tables, labelled/`aria-label`'d download buttons, `role="alert"` access-denied), en + da-DK.
- Data freshness (`/Organizer/DataFreshness`) — `CommunityHub.Core.Organizer.DataFreshnessService`
  (REQUIREMENTS §21 Organizer [M] "last synced at"). Answers a different question than the count
  dashboards: *is each data source still being fed, or has a sync silently stopped?* For each
  `FreshnessFeed` (Email ← `EmailLog.SentAt`; AttendeeSync ← `Attendee.LastSyncedAt`;
  MasterClassBookingSync ← `MasterClassParticipant.LastSyncedAt`; SponsorLeads ← the later of
  `SponsorLead.CapturedAt` / `LastSyncedAt`; SpeakerImport ← `SpeakerProfile.LastSessionizeImportAt`;
  SessionImport ← `Session.LastSessionizeImportAt`; SessionQuestions ← `SessionQuestion.CreatedAt`;
  SessionEvaluations ← `SessionEvaluation.CreatedAt`; SoMePublished ← `SoMePost.PublishedAtUtc`) it
  resolves a **single max-timestamp server-side** (one `MaxAsync` per feed — no client-side nested
  `Select`/`OrderBy`, so it is SQL-translatable) and emits a `FreshnessRow(Feed, LastActivityUtc,
  StaleAfter)`. The row computes its **age** relative to the snapshot's `GeneratedAtUtc` (clamped to
  zero on clock skew) and an **`IsStale`** flag (`age > StaleAfter`); a feed with **no data** is a
  distinct state (`HasData=false`), never flagged stale, so a fresh edition does not light up red. The
  per-feed `StaleAfter` windows are plain constants (recency hints, not SLAs; no config/secret). The
  snapshot rolls up `StaleFeeds` / `HasStaleFeeds` for the page banner. Read-only, **no schema change** —
  distinct from `OrganizerOverviewService` / `CommandCenterService` (counts) and `CommsCockpitService`
  (the comms timeline): this is the per-source recency view. Organizer-gated server-side; linked from the
  **Logistics hub** (kept off the top-level nav so the consolidated menu stays short). Mobile-first ~360px
  (table scrolls horizontally), accessible (captioned table, `role="status"` banner, per-row state pill),
  en + da-DK.
- Dashboard (`/Organizer/Dashboard`) — `ReportingService` `DashboardReport`: form-completion rates,
  participants by role, task status + overdue count, sponsor completion, attendee-mismatch count,
  volunteer shift coverage, pending volunteer applicants, survey summary. CSS bar charts, no chart
  library.
- Sessionize import (`/Organizer/SessionizeImport`) — §6.
- Action queue (`/Organizer/ActionQueue`) — the read/resolve surface over the
  `OrganizerActionItem` table. Self-service form handlers (Hotel, Dinner) call
  `OrganizerActionItemService.RaiseIfLateAsync` when a participant edits an
  **already-submitted** record inside the late-change window
  (`LateChangeWindowDays` = 14 days before `Event.LockDate`); first-time
  submissions and early edits stay quiet, and after the lock date the forms are
  read-only so no edit reaches the queue. The service upserts one open row per
  (event, type, participant) — repeat edits refresh the summary rather than pile
  up. The page lists open items grouped by type with live counts, supports
  mark-resolved-with-note / re-open (both edition-scoped so an organizer can never
  touch another edition's item), and CSV-exports the open list via `CsvWriter`.
  The organizer landing page (`/Organizer/Index`) shows the open-item count as a
  badge. No schema change was needed — the `OrganizerActionItem` table + its
  index shipped earlier (migration `20260525161523_OrgActionsAndTravelPaid`); this
  feature wired the until-now-dormant table to writes and a UI.
- **Participant change-request after lock** (✅ 2026-06-17) — a second PRODUCER of
  action-queue items, this time participant-initiated. Once `Event.LockDate` passes
  the Forms/* pages are read-only; rather than leave that a dead end, a locked form
  shows a **"Request a change"** link (shared `_RequestChangeLink` partial) to
  `/Forms/RequestChange?topic=<topic>`. The pure `FormChangeRequestService`
  (`src/CommunityHub.Core/Reminders/`) validates the free-text message (required,
  ≤1000 chars) and the participant's edition membership, then upserts ONE open
  item of type `change-requested:<topic>` (a per-topic suffix under the
  `OrganizerActionItemService.TypeChangeRequestedPrefix` family, so each form keeps
  its own row while `LabelFor` still labels the whole family "Change requested
  (after lock)"). It reuses `UpsertOpenAsync`, so it is idempotent per
  (event, participant, topic) and edition-scoped, and surfaces in the SAME Action
  Queue grid with no organizer-side changes. **No new table, no email send** — the
  queue IS the hand-off (organizers follow up through existing comms). The page
  reads the participant's own open requests back via `GetOpenForParticipantAsync`
  (a `StartsWith('change-requested:')` filter, SQL-translatable). Unlike
  `RaiseIfLateAsync`, a change REQUEST is never window-gated — it must always go
  through, since post-lock is exactly the dead end it fixes. The lock itself stays
  authoritative: this is a recourse, not a self-unlock back door.
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

### 9a. Internationalization (i18n) — English + Danish

Participant-facing UI copy is localized with ASP.NET Core's built-in resource-based
localization. **Resources + markup only — no schema/DB involvement, no per-request data.**

- **Resource store.** One shared resource, two cultures, in `CommunityHub.Core`:
  `Resources/SharedResource.resx` (English = default + invariant fallback) and
  `Resources/SharedResource.da-DK.resx` (Danish). Keys are area-dotted (`Nav.*`, `Login.*`,
  `Hub.*`, `Tasks.*`, `MyEvent.*`, `Speaker.*`, `Common.*`) so the same word resolves
  identically everywhere; a missing da-DK key silently falls back to English. The marker type
  `CommunityHub.Core.Resources.SharedResource` lives in the matching namespace, so the embedded
  resource base name equals the type's full name — wire `AddLocalization()` with an **empty**
  `ResourcesPath` (a non-empty path would be prefixed twice and every lookup would miss).
- **Negotiation.** `RequestLocalizationOptions` supports **en + da-DK**, default **en**. Provider
  order: the culture **cookie** (set by the switcher) → `Accept-Language` → the en default. The
  `QueryStringRequestCultureProvider` is removed so a stray `?culture=` can't override a user's
  saved choice. `CurrentUICulture` drives both resource lookup and the dynamic `<html lang>`;
  `CurrentCulture` drives date/number formatting. (Previously the app pinned every request to
  da-DK in `Program.cs`; that pin is gone.)
- **Switcher.** A two-button **English / Dansk** form in the layout top bar (reachable on every
  page incl. anonymous Login) posts to a minimal `MapPost("/set-language")` endpoint that writes
  the standard ASP.NET Core culture cookie (`SameSite=None; Secure` so it survives the Backstage
  iframe; `returnUrl` treated as local-only) and redirects back. Works without JS; `aria-pressed`
  marks the active language.
- **Views.** `_ViewImports` injects `IHtmlLocalizer<SharedResource>` as `@Localizer`; pages use
  `@Localizer["Key"]` (and `@Localizer["Key", arg]` for `{0}` substitution, which HTML-encodes the
  arg). For attribute values / JS that can't take HTML, pages read `Localizer["Key"].Value` into a
  local (JS strings are run through `JavaScriptEncoder`; the Survey wizard exposes a small
  `@functions { string SurveyJs(key) }` helper that JS-encodes a key for inline `<script>` use).
  Nullable runtime args (dates, optional names) are coalesced (`?? ""`) before they hit the `params
  object[]` overload so the build stays warning-clean.
- **Scope.** **Fully bilingual** across the entire participant + organizer-nav surface. First slice
  covered the high-traffic journeys (Login/Magic, `_Layout`, hub `Index`, `Tasks/Index`,
  `Attendee/MyEvent`, `Speaker/Index`). The completion slice externalized every remaining
  participant page: the self-service `Forms/*` (Hotel, Dinner, Lunch, Swag, Travel, Speaker,
  VolunteerWizard), the Sponsor lead-capture (`Sponsor/CaptureLead`) + tasks (`Sponsor/Tasks` and
  the `_SponsorTaskRow` partial), the Attendee detail page (`Attendee/Index`), the Survey wizard
  (`Survey/Index`, incl. its client-side JS strings) + live Results (`Survey/Results`), the
  organizer-area nav entries in `_Layout`, and the hub `Index` role sub-card status strings
  (`Hub.*`, `Status.*`). A follow-up onboarding slice externalized the two remaining
  English-only first-run surfaces: the one-time **Welcome** landing (`Welcome.cshtml`, keys
  `Welcome.*`) and the mandatory **onboarding wizard** (`Forms/OnboardingWizard.cshtml`, keys
  `Onboard.*`; reuses `Common.*`/`Swag.PoloSize`). The wizard's progress-chip label still comes
  from `OnboardingService.LabelFor` (a Core helper shared with the organizer admin view + email
  hooks) — kept as-is to avoid threading a localizer into Core / changing email copy; only the
  per-step view headings and form chrome are localized. Keys are namespaced per area (`Hotel.*`,
  `Dinner.*`, `Travel.*`, `SpeakerForm.*`, `VolWiz.*`, `Lead.*`, `SponsorTasks.*`, `TaskRow.*`,
  `Attendee.*`, `Survey.*`, `Results.*`, `Nav.Org*`, `Welcome.*`, `Onboard.*`). Factual
  deadline-date bullets and event-team/social links stay as data.
  Adding a culture = a new `SharedResource.<culture>.resx` + an entry in the supported-cultures list
  + a switcher button.
- **a11y interplay.** The just-merged accessibility pass declared a static `lang="en"`; with i18n
  that attribute becomes the negotiated culture so a screen reader keeps the correct pronunciation
  in either language. The Danish-formatted flatpickr date picker is unchanged in both languages.

### 9b. Shared UX components (flash toast, inline validation, confirm modal)

Three reusable, accessible building blocks live in `src/CommunityHub/Pages/Shared/`. Their CSS and
behaviour JS are defined **once** in `_Layout` (classes prefixed `.ceh-*`, JS scoped to `data-ceh-*`
attributes) so any page adopts them with markup only — no per-page styling or script. All three are
mobile-first (work at ~360px), en/da localized, and degrade gracefully with JS off.

1. **Flash toast** — `_Flash.cshtml` + `FlashModel` (record).
   - *Source of message:* either an explicit `<partial name="_Flash" model='new FlashModel(msg, "success")' />`
     (for pages that re-render `Page()` and hold the text on the model), **or** `TempData["Flash"]` /
     `TempData["FlashKind"]` set before a `RedirectToPage()` (the post-redirect-get pattern; the partial
     consumes TempData once when no model is passed).
   - *a11y:* `role="status"` (polite) for success, `role="alert"` (assertive) for errors, with matching
     `aria-live`; the banner is in the DOM only when there is a message, so nothing is announced on a
     plain load. Success auto-dismisses (~6s); errors persist; a keyboard-reachable Dismiss button is
     always present (label `Common.Dismiss`).
   - *Adoption:* generalises the original bespoke MasterClass `mc-flash-*` markup, which now renders
     `_Flash`; the dead `.mc-flash-*` CSS was removed. **As of 2026-06-16 every self-service `Forms/*`
     submit renders `_Flash` on save** (Hotel, Dinner, Lunch, Swag, Travel, Speaker, VolunteerWizard,
     OnboardingWizard) — each holds the outcome text on a model `Message` and re-renders `Page()`, so
     the partial gets `new FlashModel(Model.Message, "success")` (the volunteer wizard flips to
     `"error"` when editing is locked). The bespoke `<p class="info">` confirmations they used before
     are gone.

2. **Inline form validation** — a *pattern*, not a new type. Pages use the built-in
   `asp-validation-summary="All"` (styled `.ceh-validation-summary`) plus per-field
   `<span asp-validation-for="Field" class="ceh-field-error">`; the page model adds field errors via
   `ModelState.AddModelError(nameof(Field), Localizer["…"])` and returns `Page()` on `!ModelState.IsValid`
   without touching the database. Optional client-side mirroring uses HTML5 `setCustomValidity` so the
   message shows without a round-trip, but **the server always re-validates**. Representative application:
   the **Travel reimbursement** form (`Forms/Travel`), which is also the **demonstrating bug fix** — see
   §7 / the `OtherAmountEur` note below. **As of 2026-06-16 the same pattern extends to the other
   forms**: Hotel (need chosen + check-in/out present + check-out-after-check-in), Dinner (an explicit
   RSVP pick), Swag (a polo choice) and Speaker — each replaces its old single-string `Message`
   pseudo-validation with `ModelState.AddModelError(nameof(Field), Localizer["…"])`, an
   `asp-validation-summary="All"` summary and per-field `asp-validation-for` spans, returning `Page()`
   on `!ModelState.IsValid` with nothing persisted.

   **Structured dietary capture (2026-06-16).** The Dinner and Speaker forms share one
   `_DietaryFieldset` partial (a diet `<select>` + a `<fieldset>`/`<legend>` group of the 14
   common-allergen checkboxes + a free-text box), bound through a single `DietaryInput` so both forms
   use identical markup; `DietaryInput.LoadFrom`/`ApplyTo` map it to/from the `DietaryRequirement`
   entity, and the checkbox `name` == the entity property == the `DietaryAggregator` key so view, store
   and roll-up agree. See the `DietaryRequirement` data-model entry above.

3. **Confirmation modal** — `_ConfirmModal.cshtml` + `ConfirmModalModel` (record).
   - *Markup:* render the modal once (`Id`, `Title`, `Body` with an optional `{count}` token, confirm/cancel
     labels, `Danger` flag, `DefaultCount`), then mark any trigger (submit button or link) with
     `data-ceh-confirm="<Id>"` and `data-ceh-count="…"`. The layout JS opens the modal instead of running
     the trigger's action, fills the `{count}` pill, and on Confirm replays the original action (submits the
     trigger's form, re-emitting its `name`/`value` so `asp-page-handler` survives — or follows a link).
   - *a11y:* `role="dialog"` + `aria-modal="true"` + `aria-labelledby` (title); Esc, backdrop click, and
     Cancel all close it; focus moves to Confirm on open, is **trapped** (Tab/Shift+Tab cycle), and is
     restored to the trigger on close.
   - *Adoption:* wired to the **Broadcast Send** action (`Organizer/Broadcast`), replacing the bare native
     `confirm()` so an organizer sees the recipient COUNT and a clear summary before a bulk send. With JS
     off the Send button submits directly (the modal markup is inert).

**The Travel `OtherAmountEur` fix.** Previously `OnPostAsync` composed the claim via `ComposeAmount`,
where `ChoiceOther` with a null/zero `OtherAmountEur` returned `null` — so selecting *Other* and leaving
the amount blank **silently saved a NULL claim with no feedback**. The handler now validates *before*
composing: claiming requires a chosen amount; choosing *Other* requires `OtherAmountEur > 0` **and** an
explanation, each as a **field-level** `ModelState` error. On failure it re-renders with the inline
messages and persists nothing. Covered by `TravelValidationTests` (6 cases).

**4. Honest action-result confirmations (organizer send + QR provisioning, 2026-06-16).** Every organizer
SEND action (Broadcast, session-evaluation results to speakers) and the QR PROVISIONING action (room QR;
the master-class Booking sync rides the same shape) now renders an **honest** success / failure / no-op
confirmation through the shared `_Flash` toast, instead of an ad-hoc `<p>` that could read like a success
when nothing actually happened. The shaping is a **pure, side-effect-free** helper
`CommunityHub.Core.Organizer.ActionResultSummarizer` (no DB / clock / I/O):

   - `ForSend(anySent, recipientCount, at, reason, failed, formats)` → a `Succeeded` summary carrying the
     **timestamp + recipient count** ("Sent at &lt;time&gt; — N recipient(s).") only when something
     really went out; a send that reached **nobody** (allowlist-dropped / zero eligible / all
     already-sent) is a `NoOp` carrying the reason, and a run where **every attempt failed** is `Failed` —
     never a green success. A partial run (some sent, some failed) is a success that **names the failures**.
   - `ForProvision(provisioned, at, url, reason, formats)` → a `Succeeded` summary carrying the
     **timestamp + stored URL** ("Provisioning done at &lt;time&gt; — stored at &lt;url&gt;.") only when the
     seam actually stored a QR; a not-wired / missing-room outcome is a `NoOp` with the reason.
   - `Failure(reason)` / `NoOp(reason)` shape an explicit caught-exception failure / a clean nothing-to-do.

   The honest outcome is a three-way `ActionOutcome` (`Succeeded` / `NoOp` / `Failed`) mapped to a
   `FlashKind` (`Success` green / `Info` blue / `Error` red) — so a no-op is **visibly not** a green
   "done". The `_Flash` partial gained the matching **`info`** kind (blue, `role="alert"`/`aria-live`,
   no auto-dismiss) alongside success/error. The structured facts (`At`, `Count`, `Url`, `Reason`) ride on
   the `ActionResultSummary` record so a test asserts the real outcome rather than re-parsing the text. The
   confirmation lines are localized (en + da-DK) via a `Formats` bundle built from the page's
   `IStringLocalizer<SharedResource>` (`Action.Sent` / `Action.Provisioned` / `Action.ProvisionedNoUrl` /
   `Action.NoOp` / `Action.Failed`); the helper's English defaults are the fallback. Covered by
   `ActionResultSummarizerTests` (13 facts) + the `Action.*` i18n case in `LocalizationResourcesTests`.

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
| Resource group | `rg-<baseName>-dev` | `rg-<baseName>-prod` | physically separate; one RG cannot affect the other (`baseName` = `communityhub` in the public template) |
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

**Prerequisites:** Azure CLI (logged in to your tenant with rights to create resources in the target
subscription — set your own subscription via the `AZURE_SUBSCRIPTION_ID` env var, which `deploy.sh`
pins with `az account set` so a deploy cannot land in the wrong sub), Bicep (bundled with recent
CLI), and `jq` (reads `baseName` from the param file).

**Deploy infra:**
```bash
./scripts/deploy.sh dev --whatif     # preview, deploys nothing
./scripts/deploy.sh dev              # deploy dev
./scripts/deploy.sh prod             # deploy prod
```
`deploy.sh` creates the RG (`rg-<baseName>-<env>`, where `baseName` comes from the per-env parameter
file — `communityhub` in the public template) and deploys `main.bicep`. **No SQL admin password is
needed**: the SQL server is Azure-AD-only and the web + Functions apps authenticate to it via their
managed identities (passwordless), so `main.bicep` takes no `sqlAdminPassword` and nothing is
prompted. On success it prints the outputs (web app hostname, Functions app name, KV name, SQL FQDN,
blob endpoint). Bicep deployments are **incremental** — re-run after any Bicep change; `--whatif`
first.

**Post-deploy steps the Bicep deliberately leaves:**
1. **Store secret values** — `./scripts/set-secrets.sh <env>` prompts for each secret and writes it
   straight to Key Vault (the Bicep provisions the vault but stores no values). Skip any unused
   integration (leave blank; keep its `enabled` flag false).
2. **Bind the custom domain** — not in Bicep on purpose (needs a verified DNS record first). Create
   a CNAME in your DNS zone (`dev.hub.your-event.example` / `hub.your-event.example` → the deploy's
   `webAppHostname`.azurewebsites.net), wait for propagation, then:
   ```bash
   az webapp config hostname add --resource-group rg-<baseName>-<env> --webapp-name <webAppName> --hostname <customDomain>
   az webapp config ssl create   --resource-group rg-<baseName>-<env> --name        <webAppName> --hostname <customDomain>
   ```
   Both envs use App Service managed certs (auto-renew unless CNAME/TXT verification breaks). A next
   edition adds `hub.yournextevent.example` / `dev.…` the same way, pointing at the **same** prod/dev
   web apps — no redeploy.
3. **Deploy the application code** — publish + zip + deploy web and jobs:
   ```bash
   dotnet publish src/CommunityHub/CommunityHub.csproj           -c Release -o publish-out/web
   dotnet publish src/CommunityHub.Jobs/CommunityHub.Jobs.csproj -c Release -o publish-out/jobs
   # Compress-Archive each to web.zip / jobs.zip, then:
   az webapp deploy                            -g rg-<baseName>-<env> -n <webApp> --src-path publish-out/web.zip --type zip
   az functionapp deployment source config-zip -g rg-<baseName>-<env> -n <fnApp>  --src publish-out/jobs.zip
   ```
   (`tools/deploy-app.ps1 -Env <env>` wraps these build → zip → deploy → health-check steps.)
4. **Run EF migrations** against the env's SQL — `dotnet ef database update`. The SQL server is
   Azure-AD-only, so authenticate as an Entra principal that is a database user (no SQL login); if a
   firewall rule is needed for your client IP, add it temporarily and remove it afterwards.
5. **Seed** the env's Event row.

**Zero-downtime prod:** S1 plan + a staging slot — deploy to the slot, warm it up, then swap. A bad
deploy is rolled back by swapping the slot back.

**Tear down:** `az group delete --name rg-<baseName>-<env> --yes` (KV is recoverable for 90 days via
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

**Schema-sync is a release requirement.** Every release keeps the **dev and prod database schemas in
sync via EF migrations** — the DB is code (§3), so any schema change ships as a new migration applied
to **both** environments as part of that release, never to one only. Each release that touches the
schema must carry a short **per-release schema note** (which migration(s) were added and that both
dev and prod were updated) so the two environments never drift. This is part of the publish/release
flow: do not tag/publish a release with a schema change unless its migrations have been applied to
dev and prod and the schema note is recorded.

> **Schema note — Calendar sync (2026-06-14):** migration
> `20260614211323_ParticipantCalendarFeedToken` adds the nullable `Participant.CalendarFeedToken`
> column (`nvarchar(64)`) plus a unique filtered index `IX_Participants_CalendarFeedToken`
> (`WHERE [CalendarFeedToken] IS NOT NULL`). Additive and back-compatible (existing rows get NULL;
> the token is minted lazily on first hub view). Apply to **both** dev and prod as part of this
> release (startup `Migrate()` applies it automatically on deploy of each environment).

> **Schema note — Calendar sync enable/disable (2026-06-15):** migration
> `20260615104744_CalendarSyncSetting` adds the non-nullable `Event.CalendarSyncEnabled` column
> (`bit`, **SQL default `1`**), the organizer master switch for calendar sync per edition. Additive
> and back-compatible — existing editions get `1` (calendar sync stays ON) on upgrade. Apply to
> **both** dev and prod (startup `Migrate()` applies it automatically on deploy).

> **Schema note — Speaker contact-email override (2026-06-15):** migration
> `20260615033809_SpeakerContactEmailOverride` adds the nullable
> `SpeakerProfiles.ContactEmailOverride` column (`nvarchar(320)`) and a new table
> `SpeakerBackstageEmailSyncs` (the Backstage email-propagation queue: `EventId`/`ParticipantId` FKs,
> `IdentityEmail`/`DesiredEmail` `nvarchar(320)`, `State` int, timestamps, `LastError`; unique
> `IX_SpeakerBackstageEmailSyncs_EventId_ParticipantId`). Additive and back-compatible (existing
> speaker rows get NULL = no override). Apply to **both** dev and prod as part of this release
> (startup `Migrate()` applies it automatically on deploy of each environment).

> **Schema note — Welcome-with-login (2026-06-15):** migration
> `20260615033908_ParticipantWelcomeWithLoginSentAt` adds the nullable
> `Participant.WelcomeWithLoginSentAt` column (`datetimeoffset`). Additive and back-compatible
> (existing rows get NULL). It records the last time the DEV-only welcome-with-login email was sent to
> a participant (re-sendable audit). Apply to **both** dev and prod as part of this release (startup
> `Migrate()` applies it automatically on deploy of each environment).

> **Schema note — Speaker-owned bio (2026-06-15):** migration
> `20260615073042_SpeakerBioOwnedBySpeaker` adds three nullable columns to `SpeakerProfiles`:
> `PhotoUrl` (`nvarchar(1000)`, the Sessionize `profilePicture`, now stored + speaker-editable),
> `SpeakerEditedFields` (`nvarchar(200)`, the comma-separated per-field "speaker-edited" dirty set
> read by the delta Sessionize sync), and `BioLastEditedBySpeakerAt` (`datetimeoffset`). Additive and
> back-compatible (existing speaker rows get NULL = no photo, no speaker edits, so the delta sync
> still fills empty bio fields from Sessionize as before). `has-pending-model-changes` = none after
> this migration. Apply to **both** dev and prod as part of this release.

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

**Data parity — dev ≡ prod, email redirect is the ONLY difference.** The goal is that the dev and
prod environments hold the **same data** so dev is a faithful rehearsal of prod. The single
deliberate divergence is outbound email: in dev/local **all** outbound mail is redirected to the DEV
test address (`Email:RedirectAllTo`, subject-prefixed `[TEST -> original]`), while prod sends to the
real recipients (constrained by the `Email:OnlySendTo` allowlist until go-live). Everything else —
schema, config, imported speakers/sponsors/attendees — is intended to match. (Seed test rows are the
exception during early dev; prod is seeded from the real import paths, not the hand-seeded test
emails.)

**`IsTestUser` — tagging prod vs test users.** A participant carries an `IsTestUser` flag that marks
whether a row is a synthetic test user (created for rehearsal / Playwright / smoke runs) or a real
production user. This keeps dev≡prod data honest: test rows can coexist with real rows and be
filtered out of operator views, exports and counts, without deleting them. In prod the flag is
`false` for real imported participants; test rehearsal users are tagged `true` so they never skew
real dashboards or get counted as real attendees.

### 14a. Data parity tool — `tools/Sync-CehParity.ps1`

A re-runnable, env-targeted tool brings any environment's **data** to the same shape from a single
source of truth, an edition seed SQL script (e.g. `scripts/seed-<edition>.sql`). The seed is the
canonical definition of dev≡prod data:

- the active **`Event`** row,
- the **real organizer** rows (your own organizer addresses), tagged
  `IsTestUser = 0` — real participants, never removed,
- the seeded **sponsor + speaker sample tasks** (idempotent `SourceKey`s),
- the **role-coverage test users** — one per role (Speaker, Volunteer, Sponsor, Attendee), e.g.
  `speaker@example.com` / `volunteer@example.com` / `sponsor@example.com` / `attendee@example.com`,
  plus one example masterclass speaker — all tagged `IsTestUser = 1`.

> The real organizer and seed addresses live only in your private, gitignored edition seed script —
> never in the published template. Use placeholder `@example.com` addresses in any committed sample.

`IsTestUser` (a `bit` column on `Participant`, default `false`) is what makes prod-vs-test state
distinguishable and reconciles the "don't seed prod test data" rule with keeping the role-coverage
accounts in prod for the TestMode authed PROD sweep (see `TESTS.md` §5.4): the test accounts are
present **and** removable — go-live cleanup is `… WHERE [IsTestUser] = 1`.

The tool is **data-only** — it never touches the email flow, which is the one intended dev/prod
difference and is environment **configuration** (dev/local redirects all mail to one inbox; prod
uses the send allowlist), not data. It hard-codes **no** prod secret or identifier: target
server/database/user are parameters, and the SQL password comes from `-KeyVault <name>` (the env's
`sql-admin-password` secret), the `CEH_SQL_ADMIN_PASSWORD` env var, or `-SqlPassword`. `-WhatIf`
previews without connecting; `-ApplyMigrations` runs `dotnet ef database update` against the target
first so the `IsTestUser` column exists before seeding.

**Schema note (this release):** adds `Participant.IsTestUser` via EF migration
`20260614185046_ParticipantIsTestUser` (a non-destructive `bit` column, default `false`), plus a
design-time `DesignTimeDbContextFactory` so EF tooling scaffolds without a runtime connection
string. Keep dev↔prod schema in sync by applying this migration to both environments' SQL.

---

## 15. Operational runbook

- **Telemetry / logs** — Application Insights (named in the deploy outputs) collects requests,
  exceptions, and job run history. The reminder job is idempotent — it re-evaluates what is due and
  not-yet-sent each run, so a missed run self-heals on the next.
- **Inspect what's deployed** — `az resource list --resource-group rg-<baseName>-<env> --output table`.
- **Redeploy after a Bicep change** — `./scripts/deploy.sh <env>` (incremental; `--whatif` first).
- **Tear down an environment** — `az group delete --name rg-<baseName>-<env> --yes` (KV recoverable
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
| `integrations.<edition>.json` | WooCommerce, Company Manager, Sessionize, Zoho, SharePoint, email (Brevo) — each with an `enabled` toggle; `scheduledJobs` cron + on/off; secret NAMES only (plus the non-secret `sessionize.endpointId` operator config) |
| `sessionize.<edition>.custom.json` *(gitignored)* | optional per-developer override holding the real `Sessionize:EndpointId` — ordinary operator config, **not** a secret. The real id may equally live in the private `integrations.<edition>.json`; this gitignored file just keeps it off the public mirror. Template ships as `sessionize.<edition>.custom.sample.json` |
| `sponsor.<edition>.json` | category-driven `productClassification.rules`, `deadlineRules`, `taskSets`, `boothWallSpecs.tiers`, sponsor contact roles, order-import column mapping |
| `content.<edition>.json` | speaker milestone deadlines, hub resources, task vocabularies (criticality / T-days / responsible teams), reminder schedule (cadence + recipients + escalation) |
| `speaker-deadlines.<edition>.json` | speaker milestone tasks with **absolute** `dueDate` per deadline (+ optional `masterclassOnly` audience flag), seeded daily by `ReminderJob` |

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
The **Sessionize endpoint id is deliberately NOT in this inventory**: it is ordinary operator config
(`sessionize.endpointId` in the private `integrations.<edition>.json` / gitignored custom config), not
a Key Vault secret.

---

## 18. Legacy automation: webhooks & deployment topology

> This section documents the **prior-year PowerShell automation tree** that still runs the 7a/7b
> ERP ⇄ webshop ⇄ Zoho flows on a self-hosted VM. It is **not** part of the .NET hub; it is recorded
> here so the consolidation effort (REQUIREMENTS §7b) has a single source of truth for the runtime,
> triggers, webhooks and deployments. No secret values, customer names or tenant IDs are recorded here.

### 18.1 Two execution paths run in parallel

The legacy webhook surface is served **twice** today — pick one to keep during consolidation:

| Path | What it is | Trigger surface | Notes |
|---|---|---|---|
| **VisualCron (self-hosted)** | A VisualCron instance on the automation VM hosts the scheduled syncs **and** HTTP/REST trigger jobs in a dedicated event-automation job group. | HTTP/REST triggers on local ports (e.g. 9992–9994) fronted by an Azure **Application Gateway** at the custom domain `webhook.automation.your-event.example`. | A watchdog (`MonitorFix-Webhook-Is-Active.ps1`) logs into the VisualCron JSON API, reactivates inactive HTTP triggers, and watches for `CLOSE_WAIT` socket leaks (alerts past a threshold rather than self-restarting). |
| **Azure Function** (`func-eldk-webhook`) | A PowerShell-worker Function App that re-implements the same webhook endpoints. | HTTP triggers, `authLevel: anonymous`, fronted by App Gateway (the `health-probe` exists so the gateway can mark the backend healthy). | Secrets are read from **App Settings** (env vars) in `profile.ps1` — not from the legacy `Secrets.psm1`. This is the cleaner of the two and the natural keep-candidate. |

### 18.2 Webhook endpoints (Azure Function)

| Route (relative) | Method | Function | Behavior |
|---|---|---|---|
| `webshop/customer` | POST | `webhook-customer` | Idempotent create of an e-conomic customer (dedupe by name), success/failure email via Brevo. |
| `webshop/contact` | POST | `webhook-contact` | Idempotent create of an e-conomic contact under a customer (dedupe by email), success/failure email via Brevo. |
| `webshop/syncorders` | POST | `webhook-syncorders` | Idempotent order→invoice: resolves the order's Company Manager company → e-conomic customer, builds invoice lines (EUR with per-currency conversion), and creates an e-conomic **draft** invoice (`references.other = WebshopOrderId-<num>`); skips legacy (no `_cm_company_id`) and already-invoiced orders; Brevo success/failure email. Ported from the VM batch script during consolidation. |
| `health` | GET | `health-probe` | Returns `200 OK` for App Gateway health checks. |

Public custom domain: `https://webhook.automation.your-event.example/...` → App Gateway → backend (Function
App or VisualCron listener). The Function App's direct host is a generated `*.azurewebsites.net` name in
**West Europe**.

### 18.3 Deployment

- **Function App deploy:** `Webhook-Function-Source\Deploy-FuncEldk.ps1` builds a zip from
  `func-eldk-source\` and pushes it via the **Kudu zip-deploy API** (`/api/zipdeploy`) using publishing-
  profile basic auth pulled at runtime via `Get-AzWebAppPublishingProfile`. The script also offers
  `Status` (list registered functions), `Test` (exercise the 3 endpoints), and `Logs` (stream Kudu
  logstream). Source layout: `host.json`, `profile.ps1`, `requirements.psd1` (intentionally empty —
  built-in cmdlets only), and one folder per function with `function.json` + `run.ps1`.
- **Hosting:** Function App in a dedicated automation resource group (e.g. `rg-<event>-automation`), West Europe; Application Insights
  sampling enabled (excludes `Request`). Extension bundle `[4.*, 5.0.0)`.
- **VisualCron deploy:** jobs are maintained in the VisualCron UI on the VM (no source-controlled export
  found in the tree); the watchdog script and the timer-style sync scripts live on the VM and are invoked
  by VisualCron jobs.

### 18.4 Secrets posture (must change on consolidation)

Today the VM scripts load **plaintext** credentials from `Secrets.psm1` (`Import_Secrets`), and the
watchdog embeds the VisualCron API password inline. The Function App is already env-var based. On
consolidation, **all** of these move to Key Vault by name (most names already exist — see §17), values are
**rotated**, and the scripts read from KV at runtime. A currency-API secret name must be added (no entry
today). See REQUIREMENTS §7b for the work items.

**Done in this consolidation:** the legacy `Secrets.psm1`'s plaintext `Import_Secrets` is replaced by a
**Key-Vault-by-name bootstrap** (`tools/legacy-automation/scripts/Secrets.psm1`) that resolves every
credential from the vault at runtime via `Get-AzKeyVaultSecret` (with an `az` CLI fallback) — it exposes
the **same `$global:*` surface** the old module did, so call sites are unchanged. Two new secret names were
added to the inventory below:

| Secret name | Purpose |
|---|---|
| `currency-api-key` | One Simple API exchange-rate token (EUR→customer-currency conversion) |
| `visualcron-api-password` | VisualCron JSON-API login for the trigger watchdog |

The watchdog's inline VisualCron password and the duplicated Zoho client-secret in
`Get_Zoho_Access_Token.ps1` are removed and read from KV. The vault name is **operator config**
(an operator-set env var such as `$env:CEH_KEYVAULT_NAME`), not a secret. **Rotation of the real values remains an operator step** (treat
all previously-plaintext values as compromised).

### 18.5 Trigger-model decision: consolidate on the Azure Function

**Decision: standardise the webhook flows on the Azure Function (`func-eldk-webhook`) and retire the
VisualCron HTTP/REST triggers over time.** Rationale:

- The Function App is already **env-var / App-Settings (KV-backed)**, has no plaintext `Secrets.psm1`
  dependency, and is the single documented public webhook surface (§18.2). VisualCron's HTTP triggers are
  fragile in practice — they go inactive and leak `CLOSE_WAIT` sockets, which is the *only* reason the
  `MonitorFix-Webhook-Is-Active.ps1` watchdog exists. Removing those triggers removes the failure mode the
  watchdog babysits.
- With `webhook-syncorders` now implemented, **all three** order/customer/contact webhook endpoints have
  real bodies on the Function path, so there is functional parity to switch the App Gateway backend fully to
  the Function App.
- The **timer-style syncs** (the `Sync-*` scripts that reconcile speakers/sponsors/contacts/customers and
  the master-class/duplicate detectors) are *not* webhooks; they are scheduled jobs. Where they overlap with
  hub jobs they should fold into the hub's existing `scheduledJobs` model (§5); the rest can run as Function
  **timer triggers** or remain VM cron until migrated.

**Current state (unchanged here):** VisualCron and the Function App still run **in parallel** behind the App
Gateway at `webhook.automation.your-event.example`. This PR does not flip the gateway backend or delete any
VisualCron job — that is an operator cutover step (REQUIREMENTS §7b). The watchdog and the consolidated
`Sync-*` scripts are kept in the tree so the current VM state stays operable until the cutover happens.

### 18.6 Consolidated location in this repo

The surviving (active) scripts now live under **`tools/legacy-automation/`** — see that folder's `README.md`
for the full active-vs-retired inventory. The dead/superseded scripts (`__`-prefixed monoliths,
`__Sync-Economic-Webshop - Copy.ps1`, `__get info.ps1`, `Test_Webhook.ps1`) were **not** imported.
