# Contributing to ELDK Community Event Hub

This is the **private** ExpertsLive Denmark fork of `community-event-hub`. Daily
work lives here. Generic improvements flow back to the public repo automatically
on a publish tag; event-specific files are stripped before publish.

## Two concepts that look similar but are different

| Term | What it means | Lifetime |
|------|---------------|----------|
| **ELDK** (no number) | The **team** that runs ExpertsLive Denmark year over year. Codebase, infra patterns, tooling, daily PRs. | Forever |
| **ELDK27** (or eldk28, eldk29, ...) | The **specific event** being planned right now (`dev.eldk27.eventhub.expertslive.dk` is the 2027 instance). Sponsor data, agenda config, runbook tweaks. | One event cycle |

Branch prefixes, tag prefixes, and denylist patterns follow this split -- `eldk*`
for team-generic work, `eldk[0-9][0-9]*` for event-specific work. When ELDK28
spins up, none of the workflows or scripts should need editing -- the patterns
already cover any two-digit event year.

If you're new: skim this whole page once, then bookmark it. Three short reads
after this one before you write code:

- [`BUILD.md`](./BUILD.md) -- local dev setup
- [`CONTEXT.md`](./CONTEXT.md) -- architecture + key decisions
- [`docs/RUNBOOK.md`](./docs/RUNBOOK.md) -- operations for the live ELDK27 site

---

## One-time machine setup

You need a **fully equipped dev machine** with the same toolchain Morten uses,
so you can manage the environment with the same capabilities (build locally,
deploy DEV / PROD, read Key Vault, talk to SQL, open PRs, tail logs, and
pair-program with Claude).

### Required tools

| Tool                   | Why                                                                | Install                                                                          |
|------------------------|--------------------------------------------------------------------|----------------------------------------------------------------------------------|
| **Git**                | Source control                                                     | <https://git-scm.com/downloads>                                                  |
| **.NET 8 SDK**         | Build + run + EF migrations                                        | <https://dotnet.microsoft.com/download/dotnet/8.0>                               |
| **Visual Studio Code** | Recommended editor                                                 | <https://code.visualstudio.com>                                                  |
| **GitHub CLI** (`gh`)  | PRs, secrets, workflow runs from the terminal                      | <https://cli.github.com>                                                         |
| **Azure CLI** (`az`)   | Deploy + Key Vault access + SQL firewall rules                     | <https://aka.ms/installazurecliwindows>                                          |
| **PowerShell 7**       | Some build / publish scripts assume `pwsh`                         | <https://aka.ms/PSWindows>                                                       |
| **Node.js** (LTS)      | Optional -- pre-flight syntax checks on any JS we ship             | <https://nodejs.org>                                                             |
| **Claude Code**        | AI pair-programmer (CLI + VS Code extension Morten uses daily)     | <https://docs.anthropic.com/claude/docs/claude-code> + API key from `console.anthropic.com` |

### VS Code extensions

| Extension                                             | Purpose                                                       |
|--------------------------------------------------------|---------------------------------------------------------------|
| **C# Dev Kit** (`ms-dotnettools.csdevkit`)             | C# / .NET 8 IntelliSense + debugging + test runner            |
| **C#** (`ms-dotnettools.csharp`)                       | Underlying language server (installed alongside Dev Kit)      |
| **Bicep** (`ms-azuretools.vscode-bicep`)               | `infra/*.bicep` editing + lint                                |
| **Azure Tools** (`ms-vscode.vscode-node-azure-pack`)   | Browse RGs / App Services / KVs in-IDE                        |
| **Azure Account** (`ms-vscode.azure-account`)          | Sign-in for the Azure Tools pack                              |
| **GitHub Pull Requests** (`github.vscode-pull-request-github`) | Create + review PRs inside VS Code                    |
| **GitHub Actions** (`github.vscode-github-actions`)    | Tail workflow runs without leaving the IDE                    |
| **GitLens** (`eamodio.gitlens`)                        | Inline blame / branch view -- invaluable for archaeology      |
| **Claude Code** (`anthropic.claude-code`)              | AI pair-programmer; see § "Pairing with Claude" below         |

One-liner install (PowerShell):

```powershell
$exts = @(
  'ms-dotnettools.csdevkit',
  'ms-dotnettools.csharp',
  'ms-azuretools.vscode-bicep',
  'ms-vscode.vscode-node-azure-pack',
  'ms-vscode.azure-account',
  'github.vscode-pull-request-github',
  'github.vscode-github-actions',
  'eamodio.gitlens',
  'anthropic.claude-code'
)
$exts | ForEach-Object { code --install-extension $_ }
```

### GitHub authentication

```powershell
gh auth login            # pick GitHub.com, HTTPS, "Login with a web browser"
gh auth status           # confirm
```

For automated work (pushing tags, opening PRs from the CLI), `gh auth login`
handles the everyday flow. If you need a personal-access token for scripts,
create a **fine-grained PAT** at
<https://github.com/settings/personal-access-tokens> scoped to
`KnudsenMorten/eldk-community-event-hub` with:

- Contents: **Read and write**
- Pull requests: **Read and write**
- Workflows: **Read and write** (only if you'll edit `.github/workflows/*`)

Save the token in a password manager. Never commit it.

### Azure authentication + access

```powershell
az login                                          # opens a browser
az account set --subscription "ELDK Event Hub"    # the subscription that
                                                  # owns rg-eldk27hub-{dev,prod}
az account show --query 'name'                    # should print "ELDK Event Hub"
```

You need at least these role grants (ask Morten):

| Resource                                           | Role                        | Why                                |
|----------------------------------------------------|-----------------------------|------------------------------------|
| `rg-eldk27hub-dev`                                 | **Contributor**             | Deploy code, read app settings     |
| `rg-eldk27hub-prod`                                | Contributor (when ready)    | Deploy PROD                        |
| `kveldk27hubdevz237e` (Key Vault, DEV)             | **Key Vault Secrets User**  | Read DEV SQL admin password        |
| `kveldk27hubprodpdrq` (Key Vault, PROD)            | Key Vault Secrets User      | Read PROD SQL admin password       |
| `eldk27hub-sql-devz237e` (Azure SQL, DEV)          | -- (you whitelist your IP per session) | Run EF migrations from your laptop |

### Repo clone

1. **Get a GitHub invite** to `KnudsenMorten/eldk-community-event-hub`
   (ask Morten if you don't have it yet)
2. **Accept the invite** in the email -- you'll get `Write` access
3. **Clone the private repo**:
   ```bash
   git clone https://github.com/KnudsenMorten/eldk-community-event-hub.git
   cd eldk-community-event-hub
   ```
   You do NOT need access to anyone's local machine -- GitHub is the source of truth.
4. **(Optional) Clone the public repo side-by-side** if you want to run the
   publish-to-public script locally to preview what would land in the public
   mirror:
   ```bash
   cd ..
   gh repo clone KnudsenMorten/community-event-hub
   ```

---

## Daily workflow

You never push directly to `main`. Always:

```bash
git checkout main && git pull

# pick a meaningful branch name -- see naming below
git checkout -b feature/<your-name>/<short-topic>

# ... edit, build (BUILD.md), test ...

git add -A
git commit -m "feat: <one-line summary in imperative mood>"
git push -u origin feature/<your-name>/<short-topic>

# open the PR (CLI or github.com)
gh pr create --base main --title "feat: <one-line>" --body "<what + why>"
```

A maintainer reviews, approves, and merges. The PR validation workflow
(`.github/workflows/pr-validate.yml`) must pass before merge.

### Branch naming convention

| Prefix             | When to use it                                                                       |
|--------------------|--------------------------------------------------------------------------------------|
| `feature/<name>/`  | New functionality (generic, applies to any event)                                    |
| `fix/<name>/`      | Bug fix (generic, applies to any event)                                              |
| `chore/<name>/`    | Refactor, dependency bump, doc tidy, infra rename                                    |
| `eldk/<topic>`     | ELDK team-wide work that applies across multiple events (e.g. new email template that ELDK28 will also use) |
| `eldk27/<topic>`   | Work that is explicitly ELDK27-event-specific (this year's sponsor config, runbook tweaks for the 2027 venue, manuals). Next year switch to `eldk28/<topic>` |

`<name>` = your GitHub username or initials, so two people can have a
`feature/foo` branch at the same time without colliding.

### Commit messages

[Conventional Commits](https://www.conventionalcommits.org/) style. Examples:

```
feat(agenda): make session table responsive on small viewports
fix(email): RedirectAllTo test-mode also bypasses BCC list
chore(infra): bump appservice.bicep apiVersion to 2024-04-01
eldk(notify): new no-show reminder template (team-wide, used by all future events)
eldk27(sponsor): refresh Gold tier logos to v2027 set
docs: add deployment troubleshooting section to RUNBOOK
```

Body (optional) explains WHY, not WHAT. The diff already shows the what.

---

## What goes where (public vs private)

| Type of change                                | Where it ends up                          | What you need to do |
|-----------------------------------------------|-------------------------------------------|---------------------|
| Generic feature / bug fix / refactor          | Private + Public (auto-published on tag)  | Nothing special     |
| ELDK team-wide workflow / template change     | Private + Public (the team's process travels with the codebase) | Nothing special     |
| Event-specific config update (current event = ELDK27, next year = ELDK28) | Private only        | Put it in `config/<name>.eldk[NN].json` -- the denylist strips anything in `config/` from public mirror |
| Event-specific infra (RG names, key vault, hostnames) | Private only                       | Use `infra/main.dev.parameters.json` / `infra/main.prod.parameters.json` -- they're denylisted |
| Customer-internal docs / runbook with credentials | Private only                          | Put under `docs/internal-*` -- already denylisted |
| Manuals shipped to event speakers / volunteers | Private only                            | Put under `wwwroot/manuals/` -- denylisted |
| Sanitised config template for the public repo  | Both                                     | Put in `config-examples/` (NOT `config/`) and avoid event-specific tokens (`eldkNN`, real venue names, etc.) |
| Secrets / API keys                            | **Neither**                               | Never commit. Use Key Vault references. CI fails the PR if it spots common secret patterns |

The denylist is defined in `tools/publish-to-public.ps1`. If you need to add a
new file pattern that should stay private, edit that script (and PR it the same
way as any other change).

---

## Releasing (maintainers only)

Publishing the sanitised tree to `KnudsenMorten/community-event-hub` is
automated. Three trigger options, pick whichever fits the release semantic:

```bash
# Option 1 -- public-only release (no team / event milestone semantic)
git tag public-v0.4.0
git push origin public-v0.4.0

# Option 2 -- ELDK team milestone (generic, applies across events)
#             Use this when the release marks team-process or team-wide
#             code progress that future ELDK events will also use.
git tag eldk-v0.4.0
git push origin eldk-v0.4.0

# Option 3 -- Event-specific milestone (current event = ELDK27, next year switch to eldk28)
#             Use this when the release ties to a specific event milestone
#             (registration open, agenda lock, sponsors confirmed, etc.)
git tag eldk27-v0.4.0
git push origin eldk27-v0.4.0
```

All three patterns fire `.github/workflows/publish-public.yml`, which:

1. Checks out this private repo + the public repo
2. Runs `tools/publish-to-public.ps1` (the existing, tested denylist + substitution logic)
3. Force-pushes the sanitised tree to `KnudsenMorten/community-event-hub@main`

The `eldk[0-9][0-9]-v*` tag glob means **no workflow / script edits are needed**
when ELDK28 starts -- just tag `eldk28-vX.Y.Z` and it'll publish the same way
ELDK27 does.

You can also manually trigger from the Actions tab with `workflow_dispatch`
(useful for `-WhatIf` dry runs before a real release).

### Pre-flight before tagging a public release

- Run `pwsh ./tools/publish-to-public.ps1 -WhatIf` locally to see what would publish + what would be deleted from public
- Read the diff carefully -- the public repo is force-pushed, so anything you
  intended to keep must either be in `private` and on the include list, or in
  the public repo's `.publish-keep` allow-list

---

## Things NOT to do

- **Don't push directly to `main`** -- branch protection blocks it
- **Don't commit secrets, tokens, customer GUIDs**, or anything from a Key
  Vault. CI will fail the PR; if it slips through, rotate the secret immediately
- **Don't introduce new event-specific tokens (`eldk27`, `eldk28`, real venue
  names, customer-specific GUIDs) in shared code paths** without adding the
  file to the denylist OR replacing the token with a placeholder
- **Don't rewrite shared history** (`git push --force` to `main`, `git rebase`
  on shared branches). Use `git commit --amend` only on YOUR un-pushed work
- **Don't merge your own PR** without a review from another maintainer
  (override is allowed for trivial doc fixes, but be a good citizen)

---

## Build, run, deploy -- the recipes

### Build

```powershell
dotnet build src/CommunityHub/CommunityHub.csproj
```

Green build is the source of truth.

### Run locally

The app needs a SQL connection. **Easiest path**: point local at DEV SQL with
your laptop's IP whitelisted on the SQL firewall:

```powershell
# 1. open DEV SQL firewall to your current IP (one-time per IP)
$myip = (Invoke-RestMethod https://api.ipify.org)
az sql server firewall-rule create -g rg-eldk27hub-dev `
  -s eldk27hub-sql-devz237e -n ($env:USERNAME + '-laptop') `
  --start-ip-address $myip --end-ip-address $myip

# 2. read the SQL admin password from Key Vault
$sqlPwd = az keyvault secret show --vault-name kveldk27hubdevz237e `
  --name sql-admin-password --query value -o tsv

# 3. set env vars + run
$env:Sql__ConnectionStringTemplate = 'Server=tcp:eldk27hub-sql-devz237e.database.windows.net,1433;Database=eldk27hub-db;Encrypt=True;TrustServerCertificate=False;Connection Timeout=60;'
$env:Sql__AdminUser     = 'eldk27hubadmin'
$env:Sql__AdminPassword = $sqlPwd
dotnet run --project src/CommunityHub/CommunityHub.csproj
# -> https://localhost:7xxx
```

> Heads-up: you're writing to the real DEV database. Don't seed throw-away
> test data casually. For destructive experiments, ask Morten to set up a
> personal sandbox DB or use SQL LocalDB.

### Email is gated everywhere

- **DEV**: `Email__RedirectAllTo = mok@expertslive.dk` -- every outbound mail
  goes there regardless of original recipient.
- **PROD**: `Email__OnlySendTo = @expertslive.dk, mok@2linkit.net,
  mok@mortenknudsen.net, knudsen_morten@hotmail.com,
  mortenknudsen1974@gmail.com` -- non-matching addresses are silently dropped
  (logged at Information).
- **Local**: inherits whatever you set in env vars. If you set neither, mail
  goes out for real -- always set `RedirectAllTo` locally.

Before any change that triggers PROD mail, **verify the allowlist is still
in place** (`az webapp config appsettings list -g rg-eldk27hub-prod -n
eldk27hub-web-prodpdrq --query "[?name=='Email__OnlySendTo']"`).

### EF migrations

Add migration after a DbContext change:

```powershell
dotnet ef migrations add <MeaningfulName> `
  --project src/CommunityHub.Core --startup-project src/CommunityHub
```

Apply to DEV (env vars from "Run locally" above):

```powershell
dotnet ef database update --project src/CommunityHub.Core `
  --startup-project src/CommunityHub
```

PROD: same command with PROD env vars + PROD SQL firewall opened.

### Deploy to DEV / PROD

There is **no auto-deploy** from `main`. Deploys are explicit, via the
deploy script (build + timestamped artifact + deploy + health check):

```powershell
.\tools\deploy-app.ps1 -Env dev     # always first
.\tools\deploy-app.ps1 -Env prod    # only after DEV smoke-tested
```

**Downtime:** on the current B1 plan a direct deploy restarts the app
(~1-2 min). For near-zero-downtime deploys run the one-time
`.\tools\enable-slot-deploys.ps1` (upgrades the prod plan to S1 --
costs more -- creates a `staging` slot and grants its managed identity
Key Vault access). After that, `deploy-app.ps1` automatically switches to
deploy-to-slot &rarr; warm-up &rarr; swap, and production traffic moves in
seconds.

**Rollback:**

```powershell
.\tools\rollback-app.ps1 -Env prod          # previous build (instant swap-back when slots are on)
.\tools\rollback-app.ps1 -Env prod -List    # list kept artifacts (last 10)
```

The raw commands (what the script wraps) remain:

```powershell
dotnet publish src/CommunityHub/CommunityHub.csproj -c Release -o publish-out
Compress-Archive -Path 'publish-out\*' -DestinationPath 'publish-out.zip' -Force
az webapp deploy -g rg-eldk27hub-dev  -n eldk27hub-web-devz237e  --src-path publish-out.zip --type zip
az webapp deploy -g rg-eldk27hub-prod -n eldk27hub-web-prodpdrq --src-path publish-out.zip --type zip
```

### Tail an App Service log

```powershell
az webapp log tail -g rg-eldk27hub-dev -n eldk27hub-web-devz237e
```

---

## Pairing with Claude (the AI assistant)

Morten works with **Claude Code** (Anthropic's CLI + VS Code extension) as a
pair-programmer. Same install steps for everyone joining the team.

### Where Claude helps

- **Writing EF migrations** -- describe the schema change, Claude generates the
  migration + DbContext config.
- **Razor + CSS** -- give a screenshot + description, Claude proposes the
  markup + style change. Verify in the browser before merge.
- **Bicep** -- Claude is fluent in Azure Verified Modules.
- **Triage** -- paste a failing log + Claude proposes the fix.
- **JSON content** -- e.g. extending survey catalogs in
  `src/CommunityHub/App_Data/Surveys/*.json` (no migration needed -- pure
  JSON edits, app picks them up on restart).

### What to verify before trusting Claude

- **`dotnet build` is green** -- the only objective truth.
- **The migration matches your intent** -- read the `Up()` method.
- **The diff is what you expected** -- never `git add -A` after a Claude
  session; review hunks (`git diff --cached`).
- **PROD env vars + secrets** -- Claude can read `az keyvault secret show`
  for DEV, but never paste a PROD secret into a chat that you don't control
  end-to-end.

### House rules with Claude

- **Always have Claude commit + push to a feature branch**, never directly
  to `main`. Branch protection is configured to allow admin bypass; please
  don't use it from an unattended Claude session.
- **Email is gated** -- see the "Email is gated everywhere" section above.
  If Claude proposes a change that sends email, verify both
  `Email__RedirectAllTo` (DEV) and `Email__OnlySendTo` (PROD) before any
  PROD-touching action.
- **Surveys are JSON-driven** -- content edits to
  `src/CommunityHub/App_Data/Surveys/*.json` need no migration, no code.
  Ask Claude to extend the catalog, not the schema, unless the schema is
  actually inadequate.
- **No customer names in shipped artifacts** -- never let Claude leave real
  customer names in commit messages, README, release notes, or sample data.
  Use generic descriptors. (This rule is enforced by Morten's review.)

---

## When you're stuck

1. Re-read `CONTEXT.md` and the closest README to the file you're editing
2. Look at recent merged PRs -- mirror their style
3. Open a draft PR and ask in the description -- review-time feedback is fine
4. DM Morten if it's urgent / blocked
