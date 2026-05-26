# Community Event Hub

A self-service participant portal for community-run tech events. One web app that every participant of an edition logs in to with a PIN, sees a personalized landing page for their role, and self-services everything they need to do before the event — book a hotel night, RSVP to the appreciation dinner, pick a polo size, accept a speaker slot, fill in travel reimbursement, upload a sponsor logo, etc.

Built to be **evergreen and multi-community**: the codebase is generic (`CommunityHub`), the per-event data — community name, dates, venue, hostname, deadlines — lives in the `Events` table. A new edition or a different community is a new row, not a code change. Open-sourced from the **Experts Live Denmark** instance that runs the conference.

---

## Table of contents

- [Goals](#goals)
- [Problems to solve](#problems-to-solve)
- [Features](#features)
  - [End-user hubs (per role)](#end-user-hubs-per-role)
  - [Organizer hub (event management)](#organizer-hub-event-management)
  - [Automation (scripts)](#automation-scripts)
- [Architecture](#architecture)
- [Repository layout](#repository-layout)
- [Quick start (deploy your own instance)](#quick-start-deploy-your-own-instance)
- [Configuration model](#configuration-model)
- [Resilience notes](#resilience-notes)
- [Embedding](#embedding)
- [Cost](#cost)
- [Full design spec](#full-design-spec)
- [License](#license)
- [Status](#status)

---

## Goals

- Provide a central hub for **speakers / volunteers / sponsors** to collect data and tasks — and let them see and manage their own submissions (self-service).
- **Centralize tasks** — one overview, not five.
- **Better change management** — hotel changes, speaker changes, etc. flow through the hub instead of email threads.
- **More automation** — fewer manual touches per participant.
- **Sync data to subsystems** — Zoho Backstage / ERP / Webshop, two-way where it makes sense.
- **Automate deliverables to partners** — hotel rooming list, Bella catering, swag / polo orders.
- **Automate invoicing** — faster money in the bank.

---

## Problems to solve

(vs. how organizing a conference usually works today)

- Move out of spreadsheets into a database — automation vs. manual.
- Avoid static forms — they generate endless follow-up and manual merges in Excel.
- Simplify the system landscape — drop Microsoft Planner with tenant integration for sponsors.
- Avoid manual follow-up by validating data at creation time (e.g. company tax ID).
- Separate collection of info away from Sessionize / generic forms — only collect from the selected people.
- Provide more self-service so we don't have to update on people's behalf (avoids human mistake).
- Minimize emails — only send for overdue tasks.

---

## Features

### End-user hubs (per role)

A first-time **Welcome** page is shown once per participant per edition. Then a role-specific dashboard:

| Role | What they get |
|---|---|
| **Organizer** | Everything below, plus the full Organizer admin surface. |
| **Speaker** | Hotel + dinner + speaker deadlines (slides, photo, bio), travel reimbursement, lunch participation (pre-day), swag (polo), speaker info form (accreditation, country, first-time speaker), per-deadline reminders. |
| **MasterclassSpeaker** | Same as Speaker plus pre-day items folded into the deadlines. |
| **Volunteer** | Sign-up form (unconfirmed) → on selection: congrats mail + hotel + dinner + swag + lunch (setup + pre-day) + tasks assigned with calendar-sync option. |
| **Sponsor** | Sponsor task list + reminders (overdue only) + company info form (auto-uploaded to Zoho Backstage for exhibitors). |
| **Attendee** | Pre-day attendees only: see confirmed master class, remove booking(s), check if a booking was made with another email. |
| **Video / Camera** | Crew variants — hotel + dinner like staff. |

Common across roles:
- **Mandatory tasks** are listed on the front with their state (Open / In-progress / Done / Overdue) and the matching form is auto-marked Done on save (no double-bookkeeping).
- **PIN login** to email (15 min validity) + a magic auto-login link valid 7 days.
- **Calendar invites** (ICS) for hotel + dinner — stable UID per (participant, event), so re-saves UPDATE the existing calendar entry instead of duplicating.

### Organizer hub (event management)

| Area | Capabilities |
|---|---|
| **Speakers** | Import from Sessionize Excel → into Hub AND into Zoho Backstage; set pre-day / main-day; activate / deactivate; pending-task dashboard; bulk reminder; add / update / delete tasks with deadlines. |
| **Sponsors** | Pending-task dashboard; add / link / delete event coordinator with automatic sync to ERP + Webshop + Hub; set default signer; add / update / delete tasks targeting `exhibitors-all`, `sponsors-all`, or `exhibitor-{gold,diamond,platinum}`. |
| **Volunteers** | Import tasks from Excel and assign; activate / deactivate; pending-submission dashboard. |
| **Organizer tasks** | Import / assign tasks to the ELDK leads. |
| **Hotel** | Send Excel rooming list to the hotel; import confirmation IDs; email participants the updated calendar invite with confirmation; pending-submission dashboard. |
| **Travel reimbursement** | Overview of claims; register payout + send confirmation email to speaker. |
| **Swag** | Excel overview for ordering polo / awards / jackets; pending-submission dashboard. |
| **Bella group event** | Lunch overview pre-day + main-day; appreciation dinner overview incl. allergies; tasks; book furniture (API, planned); exhibitor booth overview. |
| **Group photos** | Register company + contact details; create / update calendar invite + send to lead with internal participants. |
| **App-game sponsor participation** | Register the participating sponsor (gift) + send a reminder to bring the gift to the event. |

### Automation (scripts)

| Area | Capability |
|---|---|
| **Sponsors** | Automatic create / update of sponsors + exhibitors in **Zoho Backstage** from sponsor webshop orders; automatic create sponsors in **e-conomic ERP** + sync to sponsor webshop; tax-ID validation on creation; create / update customers + contacts via webhook / API; auto-set default signer / event coordinator in sponsor webshop; auto-create e-conomic orders from webshop orders; automatic currency check (today's FX) on order creation. |
| **Attendees** | Send info if no master class chosen; send info if more than one master class booked (with self-service removal). |
| **Sponsor sync** | Orders-only — create sponsor companies and contacts in the hub via script; link contact to company; tasks are assigned to the sponsor **company** (not the contact) so all contacts of a sponsor see all tasks. |

---

## Architecture

ASP.NET Core 8 Razor Pages (`CommunityHub`) on Azure App Service Linux B1; reminder + import worker (`CommunityHub.Jobs`) on Azure Functions Flex Consumption (FC1); EF Core 8 against Azure SQL (Basic 5 DTU is enough for thousands of participants); Brevo SMTP for transactional email; Microsoft DataProtection for the PIN + magic-link tokens; Key Vault for every secret; Bicep templates for the whole stack in `infra/`.

Every secret is a Key Vault reference. The App Service uses managed identity to pull them at startup — no secret ever lands in `appsettings.json`, deploy scripts, or git history.

---

## Repository layout

```
src/
  CommunityHub/             ASP.NET Core 8 Razor Pages web app
  CommunityHub.Core/        Domain + Data + Email + Integrations (shared)
  CommunityHub.Jobs/        Worker for reminders + Sessionize import jobs

infra/
  main.bicep                Stage-1 infra (App Service, SQL, KV, Function, Storage, Log Analytics)
  modules/                  One Bicep per Azure resource type
  main.{dev|prod}.parameters.json    Per-environment parameters (community fork: provide your own)

scripts/
  deploy.sh                 Idempotent `az deployment group create` of infra/main.bicep
  set-secrets.sh            Write KV secrets from environment variables

docs/
  CONCEPT.md                Source-of-truth design doc (goals, hubs, organizer admin, automation)
  RUNBOOK.md                Step-by-step "first deploy" + "operate" recipe
  TEST_WALKTHROUGH.md       End-to-end smoke walkthrough

config-examples/
  templates/emails/         Brevo / Razor email templates (welcome, reminders)

tools/
  seed-dev.ps1              Seed an Event row + 5 test participants (one per role)
```

The PRIVATE upstream repo (`eldk-community-event-hub`) holds the ELDK27 production data (event row JSON, real logo files, prod parameter files). The PUBLIC repo (this one) is a sanitized template — no event-specific data.

---

## Quick start (deploy your own instance)

Prerequisites:

- Azure subscription you can deploy to (one resource group per environment is fine)
- `az` CLI (>= 2.50), `bicep` (bundled with az), `dotnet` 8 SDK, `gh` CLI optional
- A Brevo (or any SMTP-relay) account for transactional email
- A DNS zone you can add a CNAME to

```bash
# 1. clone
gh repo clone KnudsenMorten/community-event-hub
cd community-event-hub

# 2. deploy infra (creates the App Service Plan, SQL Server + Basic DB,
#    Key Vault, Function Plan, Storage, Log Analytics, App Insights)
export ELDKHUB_SQL_ADMIN_PASSWORD='<strong password you keep>'
./scripts/deploy.sh dev      # or `prod`

# 3. set secrets in Key Vault (Brevo SMTP, WooCommerce, etc.)
./scripts/set-secrets.sh dev

# 4. apply EF migrations (creates the Events + Participants + ... tables)
export Sql__ConnectionStringTemplate="Server=tcp:<your-sql-server>.database.windows.net,1433;Initial Catalog=<your-db>;Encrypt=True;TrustServerCertificate=False;"
export Sql__AdminUser="communityhubadmin"
export Sql__AdminPassword="$ELDKHUB_SQL_ADMIN_PASSWORD"
dotnet ef database update --project src/CommunityHub.Core --startup-project src/CommunityHub

# 5. seed your Event row + a couple of test participants
./tools/seed-dev.ps1   # PowerShell; edit the values for your community/dates first

# 6. publish + deploy the app code
dotnet publish src/CommunityHub/CommunityHub.csproj -c Release -o publish
Compress-Archive publish/* publish.zip
az webapp deploy --resource-group rg-communityhub-dev --name communityhub-web-dev --src-path publish.zip --type zip

# 7. bind your custom domain
az webapp config hostname add --webapp-name communityhub-web-dev \
  --resource-group rg-communityhub-dev --hostname hub.yourevent.example
```

Full step-by-step including DNS, certs, and the operate playbook: see [`docs/RUNBOOK.md`](docs/RUNBOOK.md).

---

## Configuration model

There's one knob that determines "which event are we serving right now": the `Events` row whose `IsActive = 1`. The login, dashboard, and reminder jobs all resolve "current event" via that flag. You roll over to a new edition by inserting a new row, marking it active, and (optionally) deactivating the previous.

Per-event things in the `Events` row:

- `Code` (e.g. `ELDK27`) – short identifier, unique
- `CommunityName` (e.g. `Experts Live Denmark`) – shown in UI + emails
- `DisplayName` (e.g. `Experts Live Denmark 2027`) – page titles
- `StartDate` / `EndDate` / `PreDayDate` – drive deadlines + role gates
- `VenueName` / `HubHostname` / `IsActive` / `LockDate`

App-wide things in App Service settings (which then resolve to Key Vault refs):

- `Sql:ConnectionStringTemplate` + `Sql:AdminUser` (+ `Sql:AdminPassword` → KV)
- `Email:SmtpHost` / `SmtpPort` / `FromAddress` (+ `SmtpUsername` / `SmtpKey` → KV)
- `Email:RedirectAllTo` — **dev-only test mode**; when set, every outbound mail's recipient is replaced with this address and the subject is prefixed `[TEST -> original@addr]` so the organizer can verify content without participants receiving anything during dev/test. Leave EMPTY in prod.
- `Embedding:BackstageOrigin` – CSP `frame-ancestors` (set `*` if embedding inside any iframe)
- `Sessionize:ApiUrl` (optional, only if you want auto-import)

---

## Resilience notes

The app uses EF Core's `EnableRetryOnFailure(maxRetryCount=6, maxRetryDelay=30s)` on both DbContexts so:

- Azure SQL Serverless **cold-start** (`SqlException 40613 "Database is not currently available"`) is silently absorbed — the user's first request after a 60+ min idle just takes 30-60s instead of failing.
- **Transient transport hiccups** (TCP reset, brief throttling) are retried with exponential back-off.
- Storage (data files + log) is always-on regardless of compute state, so paused-DB scenarios are zero data loss — any in-flight COMMIT either succeeds after the retry or never started.

Result: you can run on the cheapest Azure SQL tier and pause compute aggressively without users seeing errors.

---

## Embedding

The hub is designed to embed inside an existing sponsor / attendee management tool (the upstream ELDK instance embeds it inside Zoho Backstage). To embed:

1. Set `Embedding:BackstageOrigin` App Setting to the embedding origin (e.g. `https://backstage.example.com`), or `*` to allow any.
2. The app strips `X-Frame-Options` and emits a CSP `frame-ancestors <origin>` header on every response.
3. PIN login + magic-link tokens work inside an iframe (cookies are SameSite=None Secure when behind HTTPS).

Embed snippet template lives at `tools/backstage-embed-snippet.html`.

---

## Cost

Estimated platform cost per instance: **~€15 / month** (≈€30 / month for dev + prod combined) on Azure SQL Basic + App Service B1 + Functions FC1 + Log Analytics free tier.

---

## Full design spec

See **[docs/CONCEPT.md](docs/CONCEPT.md)** for the source-of-truth design document — goals, problems-solved, role-by-role hub specs, the full organizer admin surface (Speakers, Sponsors, Volunteers, Hotel, Travel reimbursement, Swag, Bella group event, Group photos, App game sponsor participation), and the automation scope (sponsor + attendee scripts). The README above is "how to deploy + features digest"; the CONCEPT doc is "what the platform is for and why" in full.

Other docs:
- [`docs/RUNBOOK.md`](docs/RUNBOOK.md) — first-deploy + operate playbook
- [`docs/TEST_WALKTHROUGH.md`](docs/TEST_WALKTHROUGH.md) — end-to-end smoke walkthrough
- [`docs/DESIGN_NOTES_emails_sponsor_sessionize.md`](docs/DESIGN_NOTES_emails_sponsor_sessionize.md) — email + sponsor + Sessionize import design notes

---

## License

MIT — see `LICENSE`. Use it for your community event, fork it, redistribute, no warranty.

---

## Status

Active development for the ELDK27 edition (Feb 2027). The public mirror is updated milestone-by-milestone — see commit messages tagged with the private-repo source sha for traceability. Issues / PRs welcome.
