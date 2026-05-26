# Dev → Prod parity log

Every dev-only change made this session that prod needs (or explicitly does
NOT need). Code changes are persisted in the repo and will reach prod via the
next zip deploy. **Runtime config drift** (app settings added via `az` CLI
that the Bicep doesn't yet emit) is what bites — those would be lost on the
next `bicep deploy` if not re-applied.

Mark each line `[ ]` until applied to prod, `[x]` after.

---

## 1. Critical app settings missing from Bicep (must re-apply or fix Bicep)

| Setting | Dev value | Prod value | Why critical |
|---|---|---|---|
| `Sql__AdminUser` | `eldk27hubadmin` | `eldk27hubadmin` | App falls back to `communityhubadmin` (broken template default) and SQL login fails 500. |
| `Sql__AdminPassword` (KV ref) | `@Microsoft.KeyVault(VaultName=kveldk27hubdevz237e;SecretName=sql-admin-password)` | `@Microsoft.KeyVault(VaultName=kveldk27hubprodpdrq;SecretName=sql-admin-password)` | Bicep template emits `VaultName=;` (empty) -- `last(split(keyVaultUri, '/'))` returns `""` because `keyVaultUri` ends in `/`. KV ref then never resolves. |
| `Embedding__BackstageOrigin` | `https://*.zohobackstage.com https://*.zohobackstage.eu https://*.zoho.com https://*.zoho.eu https://*.zohopublic.com https://*.zohoexternal.com` | (same — apply when embedding prod into Backstage) | CSP frame-ancestors. Empty = `'none'` = iframe blocked. |

### Prod-side commands

```bash
az webapp config appsettings set -g rg-eldk27hub-prod -n eldk27hub-web-prodpdrq \
  --settings \
    Sql__AdminUser=eldk27hubadmin \
    "Sql__AdminPassword=@Microsoft.KeyVault(VaultName=kveldk27hubprodpdrq;SecretName=sql-admin-password)" \
    "Embedding__BackstageOrigin=https://*.zohobackstage.com https://*.zohobackstage.eu https://*.zoho.com https://*.zoho.eu https://*.zohopublic.com https://*.zohoexternal.com"

az webapp restart -g rg-eldk27hub-prod -n eldk27hub-web-prodpdrq
```

**Better long-term fix:** patch `infra/modules/appservice.bicep` so these are emitted by the template (see §5).

- [ ] dev `Sql__AdminUser` applied (✅ done this session)
- [ ] prod `Sql__AdminUser` applied
- [ ] dev `Sql__AdminPassword` KV ref fixed (✅ done this session)
- [ ] prod `Sql__AdminPassword` KV ref fixed
- [ ] dev `Embedding__BackstageOrigin` set (✅ done this session)
- [ ] prod `Embedding__BackstageOrigin` set

---

## 2. EF Core migration on prod SQL

Dev has `InitialCreate` applied. Prod is empty.

```bash
SQL_FQDN="eldk27hub-sql-prodpdrq.database.windows.net"
export Sql__ConnectionStringTemplate="Server=tcp:${SQL_FQDN},1433;Database=eldk27hub-db;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;"
export Sql__AdminPassword='<prod sql admin password from KV>'
export Sql__AdminUser='eldk27hubadmin'

# add my IP to prod SQL firewall (delete after)
az sql server firewall-rule create -g rg-eldk27hub-prod \
  --server eldk27hub-sql-prodpdrq --name dev-machine-mok \
  --start-ip-address <ip> --end-ip-address <ip>

cd /c/community-repos/eldk-community-event-hub
dotnet ef database update --project src/CommunityHub.Core --startup-project src/CommunityHub

az sql server firewall-rule delete -g rg-eldk27hub-prod \
  --server eldk27hub-sql-prodpdrq --name dev-machine-mok
```

- [x] dev `InitialCreate` migration applied
- [x] dev `AppreciationDinnerFields` migration applied
- [ ] prod migrations applied (`InitialCreate` + `AppreciationDinnerFields`)

---

## 3. Seed data

Dev seeded with 1 Event (ELDK27 = "Experts Live Denmark 2027") and 5 test
participants. **Prod should NOT get the same 5 test emails.** Prod needs:

- The real Event row (same code `ELDK27`, real venue, real dates, real lock date)
- Real participants sourced from Sessionize import / WooCommerce / Zoho rather
  than seeded by hand.

When you run prod seed: copy `tools/seed-dev.ps1` to `tools/seed-prod.ps1`,
strip the test participants, keep just the Event row, set `Server` to the
prod SQL FQDN.

- [x] dev Event + test participants
- [ ] prod Event (real data)
- [ ] prod first participants (via real import path, not seed script)

---

## 4. TLS custom-domain certs

| Env | Hostname | Cert thumbprint | Status |
|---|---|---|---|
| dev | `dev.eldk27.eventhub.expertslive.dk` | `A99B473BC3C157D844E1CB8A8E5E71B760F8AAA8` | ✅ SNI-bound |
| prod | `eldk27.eventhub.expertslive.dk`     | `E9FD15976C4E61A4A1A8E4EFACF57B7C37FF3566` | ✅ SNI-bound |

Both DigiCert-issued App Service managed certs, valid May 25 → Nov 25 2026.
They auto-renew unless the CNAME / TXT verification breaks.

---

## 5. Bicep changes recommended (so future deploys don't drift back)

Patch `infra/modules/appservice.bicep`:

1. Add `Sql__AdminUser` to the `appSettings` array with value `sqlAdminLogin`
   (the parameter already on this module).
2. Fix the `Sql__AdminPassword` KV ref to derive the vault name without the
   `last(split(keyVaultUri, '/'))` trick that returns empty (the URI ends in
   `/`). Either:
   - Pass a new `keyVaultName` parameter into the module directly, or
   - Use `split(replace(keyVaultUri, 'https://', ''), '.')[0]`.

Once that's in main, every future `bicep deploy` of either env auto-sets the
right values and this parity log shrinks.

- [ ] Bicep patch landed
- [ ] Re-deploy dev with patched Bicep (verify no drift)
- [ ] Re-deploy prod with patched Bicep

---

## 6. Code changes (already in repo, will reach prod on next zip deploy)

These are NOT runtime drift — they're in source. Just need a prod deploy.

- `src/CommunityHub/Program.cs` — XFO stripping middleware; `ActiveEventNameProvider` DI; `using Microsoft.Extensions.Configuration`.
- `src/CommunityHub/Pages/Shared/_Layout.cshtml` — Experts Live brand palette (#1D3380 / #008BD2 / #E3E3E3), logo, `@inject ActiveEventNameProvider`.
- `src/CommunityHub/Pages/Organizer/Dashboard.cshtml` — `ParticipantsByRole` / `TotalParticipants` renames.
- `src/CommunityHub/Pages/Organizer/DataGrid.cshtml` + `TasksTable.cshtml` — stripped literal `<form>` from CSS comments (RZ1034 fix).
- `src/CommunityHub/Branding/ActiveEventNameProvider.cs` (new).
- `src/CommunityHub/wwwroot/img/logo.png` + `logo-white.png` (new).
- `src/CommunityHub/CommunityHub.csproj` — added `Microsoft.EntityFrameworkCore.Design`.
- `src/CommunityHub.Jobs/Program.cs` — added `using Microsoft.Extensions.Configuration`.
- `src/CommunityHub.Core/Migrations/*` (new — `InitialCreate`).
- `tools/seed-dev.ps1`, `tools/backstage-embed-snippet.html` (new).

### Prod deploy commands (when ready)

```bash
cd /c/community-repos/eldk-community-event-hub
dotnet publish src/CommunityHub/CommunityHub.csproj           -c Release -o publish-out/web
dotnet publish src/CommunityHub.Jobs/CommunityHub.Jobs.csproj -c Release -o publish-out/jobs

cd publish-out/web  && powershell -Command "Compress-Archive -Path * -DestinationPath ../web.zip  -Force" && cd -
cd publish-out/jobs && powershell -Command "Compress-Archive -Path * -DestinationPath ../jobs.zip -Force" && cd -

az webapp deploy        -g rg-eldk27hub-prod -n eldk27hub-web-prodpdrq --src-path publish-out/web.zip  --type zip
az functionapp deployment source config-zip -g rg-eldk27hub-prod -n eldk27hub-fn-prodpdrq --src publish-out/jobs.zip
```

- [ ] prod web zip deployed
- [ ] prod jobs zip deployed

---

## 7. Things deliberately NOT replicated to prod

- `TestMode__Enabled=true` -- dev-only. Prod stays `false`.
- The 5 hand-seeded test participants (organizer / speaker / attendee / sponsor / volunteer emails).
- SQL firewall rule for my dev machine IP -- deleted after migration.
