# ELDK Hub — Infrastructure Runbook

How to deploy and operate the Azure infrastructure for the ELDK Hub. This
covers **Stage 1 only** — the infrastructure. The application code (web app,
Functions, database schema) is Stages 2–7 and is deployed *onto* this
infrastructure later.

For the architecture and the reasoning behind it, see `CONTEXT.md`.

---

## 1. What this deploys

`infra/main.bicep` provisions, into one resource group, the complete
environment for the evergreen `eldk-hub` application:

| Resource | Module | Purpose |
|----------|--------|---------|
| Log Analytics + Application Insights | `monitoring.bicep` | Telemetry, logs, reminder-job run history |
| Key Vault | `keyvault.bicep` | All secrets (Brevo, WooCommerce, Company Manager, SQL) |
| Azure SQL server + database | `sql.bicep` | Crew, roles, bookings, tasks, the Events table, SentReminders |
| Storage account + `uploads` container | `storage.bicep` | Runtime-uploaded files (manuals, artwork, logo) |
| App Service plan + web app (Linux, .NET) | `appservice.bicep` | The crew-facing hub |
| Functions app (consumption) + its storage | `functions.bicep` | Timer-triggered scheduler (`reminderJob`, `woocommercePull`) |

The same template deploys **two environments** — `dev` and `prod` — selected by
the `environmentName` parameter. Resource names are suffixed so both coexist.
The app is evergreen: the year (ELDK27, ELDK28…) never appears in the
infrastructure, only in a DNS hostname and an `Events` table row.

### 1.1 Dual-environment model — what is and isn't duplicated

The CEH itself is the only thing split per environment. Upstream integrations
are shared.

| Layer | dev | prod | Notes |
|---|---|---|---|
| Resource group | `rg-eldkhub-dev` | `rg-eldkhub-prod` | Physically separate; one RG cannot affect the other. |
| Web app | `<name>-web-dev<hash>` | `<name>-web-prod<hash>` | Separate App Service plan + site per env. |
| SQL server + DB | per env | per env | dev test data never touches prod DB. |
| Storage account | per env | per env | Uploaded files / blobs separate. |
| Key Vault | per env | per env | Same secret VALUES typically (integration creds are shared) but separate stores so audit + RBAC role-assignments don't co-mingle. Auth model: **RBAC** (`enableRbacAuthorization=true`). |
| Custom hostname | `test.hub.eldk27.expertslive.dk` | `hub.eldk27.expertslive.dk` | Different DNS, different managed cert. Bound post-deploy (§4.2). |
| Application Insights | per env | per env | Telemetry never mixes. |
| **Zoho Backstage** | **SHARED** | **SHARED** | Same Backstage instance both envs read from. dev is in TestMode → READ-only. |
| **Zoho Booking** | **SHARED** | **SHARED** | Same booking calendar. dev TestMode prevents writes. |
| **WooCommerce store** | **SHARED** | **SHARED** | Same shop. dev TestMode prevents order writes. |
| **Brevo SMTP** | **SHARED** | **SHARED** | Same Brevo account for email sends. dev TestMode routes coordinator notifications to TestCoordinatorEmail only. |
| **Company Manager (WP)** | **SHARED** | **SHARED** | Same WordPress identity provider. |

**Isolation guarantee — dev never writes to prod CEH state.** Each env has its
own RG / SQL / KV / Storage / web app. Nothing in dev's infra has any reference
to prod's resources. Promotion from dev to prod is an **explicit, manual**
decision — there is no automated dev→prod flow:

- Local: a separate `./scripts/deploy.sh prod` invocation, distinct from
  `./scripts/deploy.sh dev`.
- CI (when added): a separate `workflow_dispatch` run with
  `environment: prod`, gated by a required-reviewer approval on the prod
  GitHub Environment.

**TestMode — dev never writes to SHARED upstream services.** The
`TestMode__Enabled` app setting (auto-set by `main.bicep` based on
`environmentName`, surfaced explicitly in `main.dev.parameters.json` /
`main.prod.parameters.json`) controls
`CommunityHub.Core.Integrations.TestModeOptions.Enabled`:

- `dev`: `true` (default). Integrations READ from Zoho / WooCommerce /
  Backstage normally but DO NOT write back. Coordinator notifications route
  to `TestCoordinatorEmail` only.
- `prod`: `false`. Real writes proceed.

This combination lets you exercise new dev code against live upstream data
without risking real bookings, emails, sponsor records, or customer state.

---

## 2. Prerequisites

- **Azure CLI** installed — `az version`.
- Logged in to the **ExpertsLive Denmark tenant** —
  `az login --tenant 7825c48b-861b-41fd-b635-ffab1aff7d13` (or just `az login`
  if it's already your default tenant). Account must have permission to
  create resource groups + resources in the target sub.
- **Subscription** — both dev + prod RGs live in the **ExpertsLive Denmark**
  subscription `772440e1-adf8-4fbe-82f9-bb977b55bc8b` (tenant
  `7825c48b-861b-41fd-b635-ffab1aff7d13`). `deploy.sh` selects it
  automatically (it defaults `AZURE_SUBSCRIPTION_ID` to this value and runs
  `az account set` explicitly, so a deploy cannot land in the wrong
  subscription). To target a different sub for a one-off, export
  `AZURE_SUBSCRIPTION_ID` before running the script. Environment separation
  is done by **resource group** (`rg-eldkhub-dev` vs `rg-eldkhub-prod`), not
  by subscription.
- **Bicep** — bundled with recent Azure CLI; check `az bicep version`.
- **jq** — used by `deploy.sh` to read `baseName` from the parameter file so
  the RG name follows whatever value is set there. Most Linux/macOS hosts
  have it; on Windows use `winget install jqlang.jq` or fall back to the
  hard-coded `communityhub` default if `jq` is absent.

---

## 3. Deploy

From the repository root:

```bash
# Preview what would change - deploys nothing
./scripts/deploy.sh dev --whatif

# Deploy the dev environment
./scripts/deploy.sh dev

# Deploy production
./scripts/deploy.sh prod
```

`deploy.sh` creates the resource group (`rg-eldkhub-<env>`) and deploys
`main.bicep`. It asks for the **SQL administrator password** — supply it via
the `ELDK_SQL_ADMIN_PASSWORD` environment variable, or let the script prompt.
The password is never written to a file or committed.

On success the script prints the deployment outputs — the web app hostname,
Functions app name, Key Vault name, SQL FQDN, and blob endpoint.

---

## 4. Post-deploy steps

The Bicep deploys infrastructure but deliberately leaves three things to do.

### 4.1 Store the secret values

`main.bicep` provisions the Key Vault but stores no secret *values*. Set them:

```bash
./scripts/set-secrets.sh dev
```

This prompts for each secret and stores it. Inventory (matches `CONTEXT.md`
§11 and the names referenced by `integrations.eldk27.json`):

| Secret name | Value |
|-------------|-------|
| `sql-admin-password` | The SQL admin password used at deploy time |
| `brevo-smtp-username` | Brevo SMTP username — the Brevo-issued ID (e.g. `8xxxxxx@smtp-brevo.com`), **not** the account login email |
| `brevo-smtp-key` | Brevo SMTP key (the SMTP password) |
| `woocommerce-consumer-key` | WooCommerce REST API key (read-only) |
| `woocommerce-consumer-secret` | WooCommerce REST API secret |
| `company-manager-wp-user` | Company Manager WordPress user |
| `company-manager-wp-app-password` | Company Manager WordPress application password |
| `zoho-client-id` | Zoho OAuth client ID — attendee reconciliation (CONTEXT.md 9z) |
| `zoho-client-secret` | Zoho OAuth client secret |
| `zoho-refresh-token` | Zoho OAuth refresh token |

Skip any integration you are not using yet (leave the value blank) — the
corresponding `enabled` flag in `integrations.eldk27.json` should also be
`false` until its secrets are in place.

> **Security note.** The PowerShell scripts and exports shared during design
> contained live credentials. Treat all of those as compromised — rotate
> them, and store only the rotated values here. Never commit a secret.

### 4.2 Custom domain — bind the env-specific hostname

This is **not** in the Bicep on purpose: binding a custom domain needs a DNS
record to exist and be verified first, which cannot happen inside the same
deployment. The `customDomain` parameter in `main.dev.parameters.json` /
`main.prod.parameters.json` is informational — it surfaces the target as a
deployment output and as the `Hub__CustomDomain` app setting, but the bind
itself is this manual step.

| Environment | DNS name to create | Target | Resource group |
|---|---|---|---|
| dev  | `test.hub.eldk27.expertslive.dk` | the deploy's `webAppHostname` output | `rg-eldkhub-dev`  |
| prod | `hub.eldk27.expertslive.dk`      | the deploy's `webAppHostname` output | `rg-eldkhub-prod` |

For each environment:

1. In your DNS provider, create a **CNAME** in zone `expertslive.dk`:
   - dev:  `test.hub.eldk27` → `<dev webAppHostname>.azurewebsites.net`
   - prod: `hub.eldk27`      → `<prod webAppHostname>.azurewebsites.net`
2. Wait for DNS to propagate (1–5 min on a low TTL).
3. Bind it and add a free managed certificate (replace `<env>` and `<webAppName>`):
   ```bash
   az webapp config hostname add \
     --resource-group rg-eldkhub-<env> \
     --webapp-name <webAppName> \
     --hostname <customDomain>          # test.hub.eldk27.expertslive.dk OR hub.eldk27.expertslive.dk

   az webapp config ssl create \
     --resource-group rg-eldkhub-<env> \
     --name <webAppName> \
     --hostname <customDomain>
   ```
4. Verify: `https://<customDomain>/` should serve the hub with a valid
   certificate.
5. Next edition (ELDK28) — add `hub.eldk28.expertslive.dk` and
   `test.hub.eldk28.expertslive.dk` the same way, pointing at the **same**
   prod / dev web apps respectively. No redeploy.

### 4.3 Deploy the application code

The web app and Functions app exist but are empty. Deploying the .NET app and
the Functions project is Stage 2 onwards — not covered here.

---

## 5. Operations

### Tear down an environment

```bash
az group delete --name rg-eldkhub-dev --yes
```

Key Vault has soft-delete + purge protection — a deleted vault is recoverable
(and its name reserved) for 90 days.

### Inspect what is deployed

```bash
az resource list --resource-group rg-eldkhub-prod --output table
```

### Logs and telemetry

Application Insights (named in the deploy outputs) collects requests,
exceptions, and the reminder-job history. The reminder job is **idempotent** —
it re-evaluates what is due and not-yet-sent on each run, so a missed run
self-heals on the next one.

### Redeploy after a Bicep change

Re-run `./scripts/deploy.sh <env>`. Bicep deployments are incremental — only
changed resources are touched. Run with `--whatif` first to preview.

---

## 6. Cost notes

Defaults are chosen low for an event portal that is idle most of the year:

- **SQL** — General Purpose serverless, auto-pauses after 60 min idle.
- **App Service** — `B1` plan. Scale up before the event, down after.
- **Functions** — consumption plan; pay per execution (the jobs run daily).
- **Storage** — `Standard_LRS`, Hot tier.

Review and resize for the weeks around the event.

---

## 7. What is intentionally NOT here

- **Application code** — Stages 2–7.
- **Database schema / tables** — created by EF Core migrations in Stage 2.
- **Custom-domain binding** — manual post-deploy step (§4.2), by design.
- **CI/CD pipeline** — could be added later; deployment is currently the
  `deploy.sh` script run by hand.
