# Community Event Hub

A self-service participant portal for community-run tech events. One web app that every participant of an edition logs in to with a PIN, sees a personalized landing page for their role, and self-services everything they need to do before the event — book a hotel night, RSVP to the appreciation dinner, pick a polo size, accept a speaker slot, fill in travel reimbursement, upload a sponsor logo, etc.

Built to be **evergreen and multi-community**: the codebase is generic (`CommunityHub`), the per-event data — community name, dates, venue, hostname, deadlines — lives in the `Events` table. A new edition or a different community is a new row, not a code change. Open-sourced from the **Experts Live Denmark** instance that runs the conference.

---

## Why

Most event organizers fan logistics out over email, spreadsheets, Slack threads, and shared drives — and end up answering the same 50 questions every week. This app replaces that with:

- One link participants get on day one. They sign in with a PIN sent to their email.
- A personalized dashboard per role: an attendee sees ticket + master class booking; a speaker sees session deadlines, hotel, travel; a sponsor sees their company info form + asset upload; a volunteer sees shifts; an organizer sees everything.
- Every form participants fill marks the corresponding task **Done** automatically — no double-bookkeeping. Organizers see overdue tasks in a single dashboard.
- Email reminders go out on configured cadences (per-deadline, per-role), driven by a `Jobs` worker — not by hand.

The hub is **pull-not-push** for sponsors and speakers: instead of an organizer emailing each sponsor a logo-request, sponsors self-upload through the portal. The organizer's job becomes review-and-approve, not chase.

---

## What it does

| Area | Behavior |
|---|---|
| **Login** | PIN to email (no passwords). Per-edition scope: same person at two editions = two rows. |
| **Welcome flow** | First sign-in shows a one-page intro + role-specific call-to-action; gated by `WelcomeShownAt`. |
| **Personalized landing** | Each role sees a different home (Organizer / Speaker / MasterclassSpeaker / Volunteer / Sponsor / Attendee / Video / Camera). |
| **Hotel** | Date picker + policy text, generates ICS, emails participant when organizer marks confirmed. |
| **Appreciation dinner** | RSVP yes/no + plus-one count + comments + ICS. |
| **Lunch** | Pre-day / main-day per-role lunch selection. |
| **Swag** | Polo size + jacket size + opt-outs; rolls-up to organizer count sheet. |
| **Speaker** | Sessionize-imported profile; manual edit fallback; per-speaker deadlines (slides, photo, bio); pre-day / main-day flags drive polo + hotel coverage. |
| **Travel reimbursement** | Origin city + claim amount in EUR + explanation; organizer marks paid. |
| **Sponsor info** | Per-company logo (vector + raster) + 80-char short description + 1000-char long description + 600-char social media intro. Scoped by `SponsorCompanyId` so any contact of the company edits the same row. |
| **Organizer admin** | Participants list, tasks-table view, role-specific export (Hotel, Dinner, Lunch, Speakers, Sponsors, Travel reimbursements). |
| **Background jobs** | Welcome mail, sponsor reminders, speaker-deadline chasers, incomplete-form reminders. Cron-equivalent inside the `Jobs` Worker Service. |
| **Sessionize import** | One-click `.xlsx` upload of accepted sessions → upserts SpeakerProfile rows (idempotent on email). |
| **Embedded mode** | Designed to embed inside a sponsor management tool (e.g. Zoho Backstage) via iframe; CSP + `X-Frame-Options` configurable per environment. |

---

## Architecture (one paragraph)

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

Full step-by-step including DNS, certs, and the operate playbook: see `docs/RUNBOOK.md`.

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

## License

MIT — see `LICENSE`. Use it for your community event, fork it, redistribute, no warranty.

---

## Full design spec

See **[docs/CONCEPT.md](docs/CONCEPT.md)** for the source-of-truth design document — goals, problems-solved, role-by-role hub specs, the full organizer admin surface (Speakers, Sponsors, Volunteers, Hotel, Travel reimbursement, Swag, Bella group event, Group photos, App game sponsor participation), and the automation scope (sponsor + attendee scripts). The README above is "how to deploy + what's wired"; the CONCEPT doc is "what the platform is for and why."

Other docs:
- [`docs/RUNBOOK.md`](docs/RUNBOOK.md) — first-deploy + operate playbook
- [`docs/TEST_WALKTHROUGH.md`](docs/TEST_WALKTHROUGH.md) — end-to-end smoke walkthrough
- [`docs/DESIGN_NOTES_emails_sponsor_sessionize.md`](docs/DESIGN_NOTES_emails_sponsor_sessionize.md) — email + sponsor + Sessionize import design notes

## Cost

Estimated platform cost per instance: **~€15 / month** (≈€30 / month for dev + prod combined) on Azure SQL Basic + App Service B1 + Functions FC1 + Log Analytics free tier. See `docs/CONCEPT.md` for the full architecture.

## Status

Active development for the ELDK27 edition (May 2027). The public mirror is updated milestone-by-milestone — see commit messages tagged with the private-repo source sha for traceability. Issues / PRs welcome.
