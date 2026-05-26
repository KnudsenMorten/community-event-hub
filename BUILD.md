# CommunityHub — Build & Run Guide

How to compile, create the database schema, run locally, and deploy. Read
`CONTEXT.md` first for the architecture; this file is the mechanical steps.

> **Status:** all 8 build stages are written and statically reviewed, but the
> code has **not been compiled** — no .NET SDK was available in the build
> environment. The first `dotnet build` will likely surface real errors
> (most plausibly around the Azure Functions worker API or EF Core query
> translation). Fix those before deploying. This is expected, not a defect.

---

## 1. Prerequisites

- **.NET 8 SDK** — https://dotnet.microsoft.com/download
- **EF Core tools** — `dotnet tool install --global dotnet-ef`
- **Azure CLI** + **Bicep** — for deployment (`az bicep install`)
- A SQL Server to target — Azure SQL, or LocalDB / SQL Server for local dev
- Azure Functions Core Tools — to run `CommunityHub.Jobs` locally

## 2. Build

```bash
cd <repo root>
dotnet restore CommunityHub.sln
dotnet build CommunityHub.sln
```

Fix any compiler errors before continuing. Likely areas: Functions worker
package versions, EF Core query translation in the page models.

## 3. Local configuration (no secrets in the repo)

Create `src/CommunityHub/appsettings.Development.json` — **git-ignored**, never
committed:

```json
{
  "Sql": {
    "ConnectionStringTemplate": "Server=(localdb)\\MSSQLLocalDB;Database=CommunityHub;TrustServerCertificate=True;",
    "AdminUser": "",
    "AdminPassword": ""
  },
  "Email": {
    "SmtpUsername": "<brevo-smtp-username>",
    "SmtpKey": "<brevo-smtp-key>"
  },
  "Embedding": { "BackstageOrigin": "" }
}
```

For LocalDB the template already includes integrated auth, so `AdminUser` /
`AdminPassword` can stay empty (the connection-string composition appends them
only if present — adjust `Program.cs` if your local setup differs).

The `CommunityHub.Jobs` project takes the same settings via
`local.settings.json` (also git-ignored).

## 4. Create the database schema

The first EF migration was intentionally **not** generated without an SDK —
create it now so it matches your SQL provider:

```bash
cd src/CommunityHub.Core
dotnet ef migrations add InitialCreate --startup-project ../CommunityHub
dotnet ef database update --startup-project ../CommunityHub
```

## 5. Seed the ELDK27 edition

Run `scripts/seed-eldk27.sql` against the database (sqlcmd, Azure Data Studio,
or SSMS). It inserts the ELDK27 `Event` row and one test participant per role.
Replace the example emails with real addresses to receive PIN emails.

## 6. Run locally

```bash
# web app
dotnet run --project src/CommunityHub
# scheduler (separate terminal)
cd src/CommunityHub.Jobs && func start
```

Open the web app, go to `/Login`, enter a seeded email, collect the PIN from
the email (or the logs in dev), and sign in. Each role sees its own hub.

## 7. Deploy to Azure

1. `infra/` — deploy the Bicep: `cd infra && ../scripts/deploy.sh dev`
   (creates `rg-communityhub-dev` and all resources).
2. `scripts/set-secrets.sh dev` — store the **rotated** secret values into Key
   Vault. The old PowerShell credentials are compromised; rotate first.
3. Publish the web app and the Functions app to the provisioned App Service
   and Function App.
4. Run the EF migration against Azure SQL (step 4, pointed at the Azure DB).
5. Run the seed script (step 5) against Azure SQL.
6. Bind the custom domain `hub.eldk27.expertslive.dk` (see `docs/RUNBOOK.md`).

## 8. The three scheduled jobs

`CommunityHub.Jobs` hosts three timer Functions (all daily, UTC):

| Function | Schedule | What it does |
|---|---|---|
| `ReminderJob` | 08:00 | Sends due task-deadline reminders (14/7/3/1-day). |
| `WooCommercePullJob` | 06:00 | Pulls completed shop orders, creates sponsor tasks. Gated by `WooCommerce:Enabled`. |
| `AttendeeReconcileJob` | 07:00 | Reconciles Zoho tickets vs bookings, sends the 3 chasers. Gated by `Zoho:Enabled`. |

All three route email through `ReminderEngine`, which dedups via the
`SentReminders` table — so a missed run self-heals and nothing sends twice.
