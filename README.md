# Community Event Hub

> Open-source platform for running tech community conferences without the spreadsheet chaos, static forms, and follow-up email threads. Speakers, volunteers, sponsors, and attendees get self-service hubs; organizers get dashboards, reminders, and one place to run hotels, travel, swag, and tasks. Fork it, customize via JSON, and deploy on Azure.

> **Free for any community to use.** Built by Microsoft MVP **Morten Knudsen** ([aka.ms/morten](https://aka.ms/morten)).
> Public mirror: <https://github.com/KnudsenMorten/community-event-hub>.

A self-service participant portal for community-run tech events. One web app that every participant of an edition signs in to with a PIN, sees a personalized landing page for their role, and self-services everything they need to do before the event — book a hotel night, RSVP to the appreciation dinner, pick a polo size, accept a speaker slot, fill in travel reimbursement, upload a sponsor logo, etc.

Built to be **evergreen and multi-community**: the codebase is generic (`CommunityHub`), and the per-event data — community name, dates, venue, hostname, deadlines — lives in the `Events` table plus per-edition JSON. A new edition or a different community is a new row + config, not a code change. Open-sourced from the **Experts Live Denmark** instance that runs the conference.

![Organizer hub](docs/img/image12.png)

This README is the public front door — it summarizes each capability area. The detailed public feature catalog is **[`docs/FEATURES.md`](docs/FEATURES.md)**; the architecture, build, deploy and runbook are in **[`docs/DESIGN.md`](docs/DESIGN.md)**.

---

## Table of contents

- [Goals](#goals)
- [Problems it solves](#problems-it-solves)
- [Feature areas](#feature-areas)
  - [1. Platform — built for every edition](#1-platform--built-for-every-edition)
  - [2. Sign-in & embedding](#2-sign-in--embedding)
  - [3. Crew profiles & roles](#3-crew-profiles--roles)
  - [4. Self-service forms](#4-self-service-forms)
  - [5. Tasks & reminders](#5-tasks--reminders)
  - [6. Sessions & surveys](#6-sessions--surveys)
  - [7. Sponsors](#7-sponsors)
  - [8. Sponsor leads](#8-sponsor-leads)
  - [9. Attendees & masterclass reconciliation](#9-attendees--masterclass-reconciliation)
  - [10. Email & notifications](#10-email--notifications)
  - [11. Organizer hub](#11-organizer-hub)
  - [12. Hosting & reliability](#12-hosting--reliability)
- [Deploy your own instance](#deploy-your-own-instance)
- [Configuration model](#configuration-model)
- [Embedding](#embedding)
- [Repository layout](#repository-layout)
- [Documentation](#documentation)
- [Recent additions / release history](#recent-additions--release-history)
- [License](#license)
- [Status](#status)

---

## Goals

- Build an **open-source community event platform** that other communities can re-use — to help scale the Microsoft (and adjacent) community by automating many manual tasks.
- Provide a central hub for **speakers / volunteers / sponsors / attendees** to collect data and complete tasks, and to see and manage their own submissions (self-service).
- **Centralize tasks** — one overview, not five.
- **Better change management** — hotel changes, speaker changes, etc. flow through the hub instead of email threads.
- **More automation** — fewer manual touches per participant.
- **Sync data to subsystems** — Backstage / webshop / company directory, read where it makes sense.
- **Automate deliverables to partners** — hotel rooming list, catering overviews, swag / polo orders.

---

## Problems it solves

(vs. how organizing a conference usually works today)

- Move out of spreadsheets into a database — automation vs. manual.
- Avoid static forms — they generate endless follow-up and manual merges in Excel.
- Simplify the system landscape — drop Microsoft Planner with tenant integration for sponsors.
- Collect info away from Sessionize / generic forms — only from the selected people.
- Provide more self-service so organizers don't have to update on people's behalf (avoids human mistakes).
- Minimize email — only send for overdue tasks.

![Architecture overview](docs/img/image1.png)
![Architecture detail](docs/img/image2.png)

---

## Feature areas

The sections below mirror the twelve chapters of the public feature catalog. Each one is a summary — see **[`docs/FEATURES.md`](docs/FEATURES.md)** for the full per-audience detail, and **[`docs/DESIGN.md`](docs/DESIGN.md)** for how it works.

### 1. Platform — built for every edition

One hub, every year, every community. The codebase, repo, Azure resources and namespaces are all generic `CommunityHub` — the year appears only in the web address and the event's display name. Launching a new edition (or onboarding a new community) is a new `Events` row plus per-edition JSON, never a rebuild or a fork. Everything about an edition — event details, sponsors, content, hotel, integrations, speaker deadlines — is configuration you edit, not code you change. A sanitized public template is published openly while your real config, logos and production settings stay private.

→ [`docs/FEATURES.md` §1](docs/FEATURES.md#1-platform--built-for-every-edition) · architecture in [`docs/DESIGN.md` §1–2](docs/DESIGN.md#1-system-overview)

### 2. Sign-in & embedding

- **One-time PIN by email — no new account.** Crew sign in with just their email; the hub sends a 6-digit PIN that expires in 15 minutes and works once. Safeguards built in: rate limiting (5/hour per email), lockout after repeated wrong tries, constant-time verification, and neutral messaging that never reveals whether an email is registered. PINs are never logged in plaintext.
- **"Stay signed in" your way.** At login you choose a session length — a day, a week (default), a month, or until you sign out — and the session refreshes itself as you keep using the hub.
- **Magic-link login.** Invitation emails can carry a tap-to-sign-in link (valid 7 days) so crew land straight in their hub without typing a PIN.
- **Ready for single sign-on.** Identity is isolated behind an `IIdentityProvider` seam so a verified SSO provider can be added later without disrupting the PIN experience.
- **Embeds safely in your event portal.** The hub runs inside an existing conference platform (e.g. a Zoho Backstage embed) with CSP `frame-ancestors` and `SameSite=None; Secure` cookies; security never depends on trusting the embed.

→ [`docs/FEATURES.md` §2](docs/FEATURES.md#2-sign-in--embedding--frictionless-no-new-passwords) · design in [`docs/DESIGN.md` §4](docs/DESIGN.md#4-auth-identity--embedding)

### 3. Crew profiles & roles

Each person has one profile per edition — name, contact details, role, accreditation (MVP / Expert / RD / MS Employee), awards, clothing sizes, and status flags. Every role gets a hub built around what that person needs to do: Organizer, Speaker, Masterclass Speaker, Volunteer, Sponsor, Speaker-Sponsor, Video, Photography, VIP, Attendee. New crew get a one-time welcome page (once per edition). Organizers can filter crew by role/status and activate or deactivate anyone in a click — deactivated people can no longer sign in.

→ [`docs/FEATURES.md` §3](docs/FEATURES.md#3-crew-profiles--roles--the-right-hub-for-each-person)

### 4. Self-service forms

Short, mobile-friendly forms wired so completing them does the right follow-up automatically:

- **Appreciation dinner** — RSVP with a calendar invite; captures allergies.
- **Hotel** — book a room and get a hotel calendar invite; feeds the rooming list and room-night forecast.
- **Lunch** — sign up for pre-day and main-day lunch.
- **Speaker info** — speakers see their imported session details.
- **Swag** — choose polo, jacket and award preferences.
- **Travel** — submit a reimbursement claim, which automatically creates the matching payout task.
- **Volunteer sign-up** — a guided multi-step wizard that sets up the right tasks per volunteer.
- **Late-change alerts** — edits to hotel/dinner/shift details *after* the change deadline notify organizers; edits before the deadline stay quiet.

→ [`docs/FEATURES.md` §4](docs/FEATURES.md#4-self-service-forms--crew-fill-in-their-own-details)

![Speaker hub](docs/img/image4.png)
![Volunteer hub](docs/img/image7.png)

### 5. Tasks & reminders

Every person sees only their own tasks, ticks them off, and the list fills itself from the forms they complete and the role they hold. Each speaker automatically gets a dated task for every key milestone. A gentle, reliable reminder engine sends on a per-type cadence — speaker milestones counting down (plus an overdue nudge), a weekly pending-tasks digest, weekly sponsor and form chasers, a short series for general tasks — and it never double-sends, quietly catching up if a day is missed. Everything (on/off, cadence, wording, recipients incl. CC/BCC/escalation) is tuned through settings, not code. The guiding principle is to nudge only when something is actually overdue.

→ [`docs/FEATURES.md` §5](docs/FEATURES.md#5-tasks--reminders--nothing-slips-no-inbox-spam) · jobs in [`docs/DESIGN.md` §5](docs/DESIGN.md#5-jobs-scheduled-timers)

### 6. Sessions & surveys

- **Import speakers from a spreadsheet.** Upload a Sessionize export; the hub reads columns in any order, creates/updates speakers (matched on email, never overwriting roles), reports skipped rows, and welcomes new speakers automatically (once). No network dependency — just the file.
- **Public, no-login surveys.** A 3-step survey at its own web address (pick a track → rank topics → set your level), with a live results dashboard anyone can view, spam protection built in, and no sign-in required.
- **Call-for-speakers demand survey.** Weighted topic rankings, per-track breakdowns and a level distribution on a shareable results page that helps shape the agenda. First instance: the ELDK27 Technical Session Topics survey (seven tracks). Surveys are mobile-first with per-step imagery and per-track deep links, and are defined entirely in JSON under `src/CommunityHub/App_Data/Surveys/<slug>.json` — adding one is a config change, not a migration.

→ [`docs/FEATURES.md` §6](docs/FEATURES.md#6-sessions--surveys--from-call-for-speakers-to-the-schedule) · import design in [`docs/DESIGN.md` §6](docs/DESIGN.md#6-integrations)

### 7. Sponsors

A sponsor is a **company, not a single contact** — every contact at a company sees that company's shared tasks. Company and contact details (including who signs and who coordinates) come from your central company directory, which the hub reads as source-of-truth and never duplicates; sponsor-facing text always shows the company's chosen public name (with a fallback chain). Booth tasks are generated automatically from what each sponsor bought — shared booth basics plus per-tier extras (Platinum / Diamond / Gold) — de-duplicated across orders so a company never sees an item twice. A baseline checklist (logo, onboarding, description, attendee-bag insert, app-game) is set up for every sponsor; deadlines are anchored to the event date or first order, all configurable. Task wording is hand-curated for clarity; instructions render long URLs as clean buttons; each task can have its own upload folder with change alerts. Organizers can add/link/remove coordinators, set the default signer and coordinator, and create or edit tasks targeted at all exhibitors, all sponsors, or a specific tier. Work the platform handles behind the scenes is never shown to sponsors as a to-do.

→ [`docs/FEATURES.md` §7](docs/FEATURES.md#7-sponsors--managed-as-companies-with-the-right-tasks) · integrations in [`docs/DESIGN.md` §6](docs/DESIGN.md#6-integrations)

![Sponsor management](docs/img/image13.png)

### 8. Sponsor leads

A full lead pipeline for booth leads. Each sponsor gets a secured **Leads API** (JSON or CSV) with ready-made script samples and a browser-friendly "Your Leads API" page; each sponsor has its own revocable access key/token (shown once, stored only as a secure hash). Leads live in a real pipeline with a live admin grid — Reply, mark Processed, set Interest, flag Ignore/Junk — and nothing is ever hard-deleted (soft status preserves rows so the screen keeps learning from operator overrides). Sponsors can opt into a daily digest or near-real-time alerts of new leads, junk skipped, recipients defaulting to all the company's contacts. Each lead gets a 0–100 heuristic quality score and label; only unmistakable test entries are auto-junked, everything else stays advisory.

→ [`docs/FEATURES.md` §8](docs/FEATURES.md#8-sponsor-leads--capture-screen-and-route-booth-leads) · pipeline + Zoho CRM pull (gated off by default) in [`docs/DESIGN.md` §6](docs/DESIGN.md#6-integrations)

![Sponsor hub](docs/img/image10.png)

### 9. Attendees & masterclass reconciliation

The hub compares two-day tickets against masterclass bookings and surfaces the mismatches — no booking, no ticket, or duplicate bookings — with branded chaser emails to sort them out. Attendees are synced in for visibility with deep links back to the booking system; the hub never re-does seat reservations, capacity or waitlists. "Same person, two emails" cases are resolved by a human or the attendee via a chaser, never auto-merged. Organizers get a clean, read-only attendee browser with summary tiles, search, filters and a CSV export that handles accented names; corrections happen at the source system.

→ [`docs/FEATURES.md` §9](docs/FEATURES.md#9-attendees--masterclass-reconciliation--one-clear-picture) · reconciler in [`docs/DESIGN.md` §6](docs/DESIGN.md#6-integrations)

![Attendee hub](docs/img/image11.png)

### 10. Email & notifications

All mail is sent through a professional relay from your event sender address, rendered by one branded template engine (a shared branded shell + per-type content + `{{token}}` substitution) built to render correctly across clients including Outlook. A library of templates covers welcome notes, reminders, chasers, app-game and broadcast messages. Organizers get an **Email Center** to preview any template safely, send a one-click test to themselves, and watch a delivery pulse with a filterable history of what's been sent. **Broadcast** sends one personalized message ("Hi {firstName}") to selected role groups (and optionally attendees) with a recipient preview; sending is resume-safe — a single failure never stops the batch.

→ [`docs/FEATURES.md` §10](docs/FEATURES.md#10-email--notifications--on-brand-controllable-safe) · email system in [`docs/DESIGN.md` §7](docs/DESIGN.md#7-email-system)

### 11. Organizer hub

Run the whole event from one place. A **live dashboard** shows form completion, participants by role, tasks and overdues, sponsor completion, attendee mismatches and volunteer coverage, plus live pipeline cards for leads and event prep. Practical **data grids** for participants and hotel bookings (inline active and check-in/out toggles, filters) and tasks (inline edit), each with CSV export. Plus the management areas:

- **Speakers** — import from a Sessionize Excel, set participation, activate/deactivate, dashboard, send overdue reminders, add/update/delete dated tasks.
- **Hotel** — export the rooming list (hotel-grade `.xlsx`), import confirmation IDs, send updated calendar invites, dashboard.
- **Travel reimbursement** — overview of claims, register payout, send confirmation.
- **Swag** — multi-sheet vendor spreadsheet for polo/award/jacket orders, dashboard.
- **Group photos** — register a company + contact, schedule a slot (Danish wall-clock, stored UTC), send calendar invites that *update* rather than duplicate (stable ICS UID).
- **App game** — register a sponsor's gift and send the branded gift reminder to every active sponsor contact.
- **Lunch & dinner overviews** — pre-/main-day lunch numbers and the appreciation-dinner list with allergies; booth overview.
- **Sponsor admin area** — manage the sponsor task catalog, run the leads pipeline (issue/rotate/revoke keys, set notification preferences, action leads), and watch a sponsor status dashboard sorted overdue-first.

→ [`docs/FEATURES.md` §11](docs/FEATURES.md#11-organizer-hub--run-the-whole-event-from-one-place) · feature surface in [`docs/DESIGN.md` §8](docs/DESIGN.md#8-feature-surface-hubs--organizer-areas)

![Hotel management](docs/img/image14.png)
![Travel reimbursement management](docs/img/image15.png)
![Swag management](docs/img/image16.png)

### 12. Hosting & reliability

The full environment (database, web app, scheduled jobs, storage, secret vault, logging and monitoring) is **defined as code** (Bicep): Azure SQL, App Service, Azure Functions, Storage, Key Vault, Log Analytics + Application Insights — separate dev + prod instances per event (e.g. `rg-<event>hub-dev`, `rg-<event>hub-prod`). Background jobs handle reminders, order pulls, attendee reconciliation, portal sync, sponsor-lead delivery and upload-change watching on their own schedules, each individually switchable. **Scripted, safe deploys** build a versioned artifact, deploy and health-check, with one-command rollback; **production releases are zero-downtime** (deploy to a staging slot, warm up, then swap — dev stays on B1, prod runs S1 with a slot). The app absorbs Azure SQL cold-starts gracefully (EF retry) so it runs happily on cost-efficient, auto-pausing infrastructure (~€25/month per instance; +~€50/month for the prod S1 slot). Schema is versioned via EF migrations; publishing to the public template runs through a controlled, allow-listed (denylist) process with a dry-run pre-flight; protected branches, required reviews and secret scanning keep the codebase safe; each environment binds its own verified custom domain with a managed certificate.

DNS: dev `dev.hub.yourevent.example`, prod `hub.yourevent.example`.

→ [`docs/FEATURES.md` §12](docs/FEATURES.md#12-hosting--reliability--production-grade-by-design) · infra/deploy/runbook in [`docs/DESIGN.md` §11–15](docs/DESIGN.md#11-infrastructure-bicep--environments)

---

## Deploy your own instance

Prerequisites:

- Azure subscription you can deploy to (one resource group per environment is fine)
- `az` CLI (>= 2.50), `bicep` (bundled with az), `dotnet` 8 SDK, `gh` CLI optional
- A Brevo (or any SMTP-relay) account for transactional email
- A DNS zone you can add a CNAME to

```bash
# 1. clone
gh repo clone KnudsenMorten/community-event-hub
cd community-event-hub

# 2. deploy infra (App Service Plan, SQL Server + DB, Key Vault, Functions
#    plan, Storage, Log Analytics, App Insights)
export ELDK_SQL_ADMIN_PASSWORD='<strong password you keep>'
./scripts/deploy.sh dev          # or `prod` (preview first with --whatif)

# 3. set secret values straight into Key Vault (Brevo SMTP, WooCommerce, Zoho, ...)
./scripts/set-secrets.sh dev

# 4. apply EF migrations (temporarily add your IP to the SQL firewall first)
dotnet ef database update --project src/CommunityHub.Core --startup-project src/CommunityHub

# 5. seed your Event row + a few test participants (edit values first)
./tools/seed-dev.ps1

# 6. publish + deploy the app code (web + jobs)
./tools/deploy-app.ps1 -Env dev            # build -> timestamped artifact -> deploy -> health check
./tools/deploy-app.ps1 -Env dev -App jobs  # the Functions app, same scripted way

# 7. bind your custom domain (after the CNAME verifies)
az webapp config hostname add --resource-group rg-<event>hub-dev \
  --webapp-name <webAppName> --hostname hub.yourevent.example
```

`tools/rollback-app.ps1` redeploys any kept artifact (or, on prod, an instant slot swap-back). Full step-by-step — DNS, certs, post-deploy settings, and the operate playbook — is in **[`docs/DESIGN.md` §12 (deploy)](docs/DESIGN.md#12-deploy-rollback--zero-downtime)** and **[§15 (runbook)](docs/DESIGN.md#15-operational-runbook)**.

---

## Configuration model

Two layers decide "which event are we serving":

**The active `Events` row** (`IsActive = 1`) — login, dashboard, and reminder jobs all resolve "current event" via that flag. Roll over to a new edition by inserting a new row, marking it active, and (optionally) deactivating the previous. Per-event fields: `Code` (e.g. `ELDK27`), `CommunityName`, `DisplayName`, `StartDate` / `EndDate` / `PreDayDate`, `VenueName` / `HubHostname` / `IsActive` / `LockDate`.

**Per-edition JSON** under `config/*.<edition>.json` — event, hotel, integrations, sponsor, content and speaker-deadline files (each validated on load against its `_schema` key; secrets are Key Vault references by name only). See the full table in **[`docs/DESIGN.md` §17](docs/DESIGN.md#17-configuration--key-vault-reference)**.

App-wide settings live in App Service configuration and resolve to Key Vault references for secrets:

- `Sql:ConnectionStringTemplate` + `Sql:AdminUser` (+ `Sql:AdminPassword` → KV)
- `Email:SmtpHost` / `SmtpPort` / `FromAddress` (+ `SmtpUsername` / `SmtpKey` → KV)
- `Email:RedirectAllTo` — **dev-only test mode**; when set, every outbound mail is redirected here and the subject is prefixed `[TEST -> original@addr]`. Leave EMPTY in prod (prod uses an `Email:OnlySendTo` allowlist instead).
- `Embedding:BackstageOrigin` — CSP `frame-ancestors` (the Backstage origin list; empty blocks the iframe).

---

## Embedding

The hub is designed to embed inside an existing event-management tool (the upstream ELDK instance embeds it inside Zoho Backstage). To embed:

1. Set `Embedding:BackstageOrigin` to the embedding origin(s) (e.g. `https://backstage.example.com`).
2. The app strips `X-Frame-Options` and emits a CSP `frame-ancestors <origin>` header on every response.
3. PIN login + magic-link tokens work inside an iframe (cookies are `SameSite=None; Secure` behind HTTPS).

Embed snippet template lives at `tools/backstage-embed-snippet.html`. More in **[`docs/DESIGN.md` §4](docs/DESIGN.md#4-auth-identity--embedding)**.

---

## Repository layout

```
src/
  CommunityHub/             ASP.NET Core 8 Razor Pages web app (+ Leads API, /health)
  CommunityHub.Core/        Domain + Data + Email + Reminders + Integrations (shared)
  CommunityHub.Jobs/        Azure Functions worker — reminders, pulls, reconciliation, watchers

infra/
  main.bicep                Infra (App Service, SQL, KV, Functions, Storage, Log Analytics, App Insights)
  modules/                  One Bicep per Azure resource type
  main.{dev|prod}.parameters.json    Per-environment parameters (provide your own in a fork)
  DEV_TO_PROD_PARITY.md     Live dev->prod parity checklist

scripts/
  deploy.sh                 Idempotent `az deployment group create` of infra/main.bicep
  set-secrets.sh            Write KV secret values from prompts
  seed-eldk27.sql           Example seed SQL

docs/
  FEATURES.md               Public feature catalog (delivered features)
  ROADMAP.md                Planned / upcoming features (auto-generated, public)
  DESIGN.md                 Architecture + data model + integrations + build + deploy + runbook
  REQUIREMENTS.md           Internal backlog — NOT in the public mirror
  TESTS.md                  Internal test plan — NOT in the public mirror

templates/
  emails/                   Branded email templates (layout + per-type content); packaged into
                            BOTH publish bundles by the csproj files — rendered at runtime, so
                            first-class code, not an example

config-examples/
  templates/emails/         Historical copies kept for the community fork docs

tools/
  seed-dev.ps1              Seed an Event row + test participants (one per role)
  deploy-app.ps1            Build + zip (forward-slash entries) + deploy web/jobs; slot-swap on prod
  rollback-app.ps1          Instant slot swap-back / artifact redeploy (web + jobs)
  enable-slot-deploys.ps1   One-time S1 + staging-slot + slot-MSI Key Vault grant (done for prod)
  plant-test-pins.ps1       DEV-only: plant known-PIN LoginPin rows for the Playwright suites
  publish-to-public.ps1     The denylist-driven public-mirror publisher (run from maintainer's box)
  setup-repo-governance.ps1 One-time branch protection + collaborator setup
  CommunityHub.OneShot/     Console CLI to run one job once locally
```

The PRIVATE upstream repo (`eldk-community-event-hub`) holds the ELDK27 production data (event row, real logos, prod parameter files). The PUBLIC repo (this one) is a sanitized template — no event-specific data.

---

## Documentation

| Doc | What it covers |
|---|---|
| **[`docs/FEATURES.md`](docs/FEATURES.md)** | Public feature catalog — the delivered feature set, by audience (the 12 areas above). |
| **[`docs/ROADMAP.md`](docs/ROADMAP.md)** | Planned / upcoming features — high-level, auto-generated from the backlog. |
| **[`docs/DESIGN.md`](docs/DESIGN.md)** | Architecture, data model, integrations, jobs, email, build, infra, deploy, and the operational runbook. |

Internal docs (kept in the private repo, **not** in this public mirror): `REQUIREMENTS.md` (backlog), `TESTS.md` (test plan), `CONTRIBUTING.md`, `CLAUDE.md`.

---

## Recent additions / release history

The public mirror is updated milestone-by-milestone. For the per-release detail, see the commit history — every public commit message carries the private-repo source sha for traceability:

```bash
git log --oneline
```

Newly built capabilities are folded directly into the relevant feature area above (and into [`docs/FEATURES.md`](docs/FEATURES.md)); not-yet-built or partial work lives in [`docs/REQUIREMENTS.md`](docs/REQUIREMENTS.md).

---

## License

MIT — see [`LICENSE`](LICENSE). Use it for your community event, fork it, redistribute, no warranty.

---

## Status

Active development for the ELDK27 edition (Feb 2027). The public mirror is updated milestone-by-milestone — see commit messages tagged with the private-repo source sha for traceability. Issues / PRs welcome.
