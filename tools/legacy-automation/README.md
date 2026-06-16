# Legacy automation (PowerShell tree → CEH)

This folder consolidates the **prior-year PowerShell automation** that runs the
ERP ⇄ webshop ⇄ Zoho/Bookings ⇄ Sessionize flows for the event. It is **tooling**,
not part of the .NET hub (it is not compiled by `CommunityHub.sln`). See
[`docs/DESIGN.md` §18](../../docs/DESIGN.md) for the webhook + deployment topology and
[`docs/REQUIREMENTS.md` §7b](../../docs/REQUIREMENTS.md) for the consolidation work items.

## Secrets: Key Vault by name (no plaintext)

All credentials are read from **Azure Key Vault by name at runtime** — no secret value
lives in this repo. The shared bootstrap is [`scripts/Secrets.psm1`](scripts/Secrets.psm1):

```powershell
$env:ELDK_KEYVAULT_NAME = '<your-key-vault-name>'   # operator config, NOT a secret
Import-Module "$PSScriptRoot\Secrets.psm1" -Global -Force
Import_Secrets                                       # reads KV, populates the same $global:* vars
```

It exposes the **same `$global:*` surface** the old plaintext module did, so the scripts
are unchanged. Auth: the host must be signed in to Azure with read access to the vault
(`Connect-AzAccount` on the VM, or a Function managed identity / cert-based SPN). The
module prefers `Get-AzKeyVaultSecret` and falls back to the `az` CLI.

**Key Vault secret names used** (values live only in KV — see DESIGN §17 for the full
inventory): `eldk-entra-tenant-id`, `eldk-entra-client-id`, `eldk-entra-client-secret`,
`economic-app-secret-token`, `economic-agreement-grant-token`, `brevo-smtp-username`,
`brevo-smtp-key`, `woocommerce-consumer-key`, `woocommerce-consumer-secret`,
`company-manager-wp-user`, `company-manager-wp-app-password`, `zoho-client-id`,
`zoho-client-secret`, `zoho-refresh-token`, **`currency-api-key`** (new),
**`visualcron-api-password`** (new).

> **Operator step — rotate everything.** Every value that previously lived in plaintext
> on disk must be treated as compromised and rotated before/while populating the vault.

The Azure Function path reads the same credentials from **App Settings (env vars)** via
`Webhook-Function-Source/func-eldk-source/profile.ps1` (KV-backed in Azure) — it does not
load `Secrets.psm1`.

## Active scripts (consolidated)

### Webhook surface — Azure Function (`func-eldk-webhook`)
`Webhook-Function-Source/` — the keep-candidate webhook host (see DESIGN §18.5):
- `Deploy-FuncEldk.ps1` — build a zip from `func-eldk-source/` and push via the Kudu
  zip-deploy API; `Status` / `Test` / `Logs` actions. Subscription id and host names are
  **env-driven** (`ELDK_FUNC_SUBSCRIPTION_ID`, `ELDK_FUNC_RESOURCE_GROUP`, …).
- `func-eldk-source/webhook-customer` — idempotent e-conomic customer create.
- `func-eldk-source/webhook-contact` — idempotent e-conomic contact create.
- `func-eldk-source/webhook-syncorders` — **implemented in this consolidation**: real
  order→e-conomic-draft-invoice (resolves company → ERP customer, currency conversion,
  idempotent on `references.other = WebshopOrderId-<num>`). Ported from
  `scripts/Sync-Webshop-Orders-Create-ERP-Invoice.ps1`.
- `func-eldk-source/health-probe` — App Gateway health endpoint.

### Webhook-triggered (VM, called by the webshop)
- `scripts/Create-ERP-Customer-via-Webhook.ps1` — create e-conomic customer.
- `scripts/Create-ERP-Contact-via-Webhook.ps1` — create e-conomic contact.

### Scheduled / on-demand syncs (VisualCron timer jobs today)
- `scripts/Sync-Webshop-Orders-Create-ERP-Invoice.ps1` — batch order→draft-invoice sweep
  (the webhook endpoint above is the per-order equivalent).
- `scripts/Sync-ERP-Customers-to-Webshop.ps1`
- `scripts/Sync-ERP-Contacts-to-Webshop.ps1`
- `scripts/Sync-Webshop-CompanyNumber-to-PublicEntryNumber-in-ERP.ps1`
- `scripts/Sync-Webshop-Sponsors-to-Zoho-Backstage.ps1`
- `scripts/Sync-Sessionize-Speakers-to-Zoho-Backstage.ps1`
- `scripts/Create-ERP-Invoice-Coupon-Tickets.ps1` — coupon/ticket invoicing.
- `scripts/Fix-Missing-SponsorInfo-in-Webshop.ps1` — backfill helper.
- `scripts/MissingMasterClassBooking_MissingTicket.ps1` — reconciliation report.
- `scripts/Detect_Duplicates-MasterClass_Send_Mail-Zoho-Booking-Appointments.ps1` — duplicate detection + mail.

### Watchdog
- `scripts/MonitorFix-Webhook-Is-Active.ps1` — logs into the VisualCron JSON API,
  reactivates inactive HTTP triggers, watches for `CLOSE_WAIT` socket leaks (alerts past a
  threshold). Password now read from KV (`visualcron-api-password`). Retire once the App
  Gateway backend is fully on the Function App (DESIGN §18.5).

### Utility
- `scripts/Security_Tokens/Get_Zoho_Access_Token.ps1` — one-time Zoho OAuth code→token
  exchange. Client id/secret read from KV (no longer duplicated in source).

## Retired (NOT imported)

These were dead/superseded and were intentionally left out of the repo:

| Retired file | Reason |
|---|---|
| `__Sync-Economic-Webshop.ps1` | Superseded monolith — split into the cleanly-named `Sync-ERP-*` / `Sync-Webshop-*` scripts. |
| `__Sync-Economic-Webshop - Copy.ps1` | Editor copy of the above. |
| `__Sync-Contacts-to-Webshop.ps1` | Superseded by `Sync-ERP-Contacts-to-Webshop.ps1`. |
| `__Sync-Zoho-Orders.ps1` | Superseded Zoho-orders monolith. |
| `__Sync-Zoho-Booking-Appointments.ps1` | Superseded by the current Detect/Missing master-class scripts. |
| `__Get-Economic-Invoices.ps1` | One-off probe. |
| `__get info.ps1` | One-off order-meta probe with a hardcoded order id. |
| `Test_Webhook.ps1` | Ad-hoc manual webhook tester. |

## Trigger model

Consolidating on the **Azure Function** path; VisualCron jobs are kept noted as current
state and retired by an operator cutover. See DESIGN §18.5.
