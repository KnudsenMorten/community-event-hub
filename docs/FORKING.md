# Forking the Community Event Hub — and staying upgradable

This guide is for a community that wants to **run its own copy** of the hub and still take
improvements from upstream later. It answers two questions: *what may I change without painting
myself into a corner?* and *what will upstream not break?*

If you only want to contribute a change back to this project, see the pull-request flow at the end.

---

## 1. The one rule that decides everything

> **Customize through configuration, content and NEW files. Do not edit files that upstream ships.**

Git merges cleanly in every file you never touched. Follow that rule and an upstream release merges
silently, even years later. Break it — by editing a shipped service to talk to your own systems —
and every release becomes a manual conflict, until you stop upgrading altogether.

Everything below exists to make the first path possible.

**Safe to own (yours, upstream rarely touches them):**

| Yours | What it holds |
|---|---|
| `config/*.json` + `config/content/` | Your edition: community name, dates, venue, tasks, welcome copy, deadlines |
| `App_Data/Surveys/` | Your survey definitions |
| App settings / Key Vault | Endpoints, credentials, on/off switches |
| New files you add | Your own integrations, pages, jobs, styles |

**Upstream's (expect changes in every release):** everything under `src/`, `tests/`, `docs/` and the
build and deploy scripts.

---

## 2. Set up the fork so upgrading is one command

```bash
git clone https://github.com/<you>/community-event-hub
cd community-event-hub
git remote add upstream https://github.com/KnudsenMorten/community-event-hub
git fetch upstream --tags
```

Track **released tags**, not upstream `main`. Releases are tagged `public-vX.Y.Z` and are the only
points the maintainer has tested and written notes for.

```bash
git fetch upstream --tags
git log --oneline HEAD..public-v1.5.0     # what you are about to take
git merge public-v1.5.0
```

Then, before deploying: apply database migrations, run the test suites, and deploy to a staging
environment first. §3 of `docs/DESIGN.md` covers migrations; the README covers the build.

---

## 3. Turning off what you do not use

Every external integration is **optional and off by default**. Three independent layers, all of
which must allow an action before it happens:

1. **`<System>:Enabled` app settings** — `Zoho:Enabled`, `Sessionize:Enabled`, `WooCommerce:Enabled`,
   `EconomicErp:Enabled`, `LinkedIn:Enabled`, `CompanyManager:Enabled`. False makes the client inert:
   services return a "disabled" result instead of calling out, and nothing throws.
2. **Per-edition feature flags** (Settings → Features, stored per edition). Advanced features default
   to off. A disabled feature is inert — the caller no-ops, and the Jobs page says the job is off
   because its feature is off.
3. **The external-write guard** — `Integrations:ExternalWrites:{Zoho|Erp|LinkedIn|SharePoint|Webshop}`,
   default **false**. Reads may work while every write is refused. Keep this off until you have
   deliberately decided each system is safe to write to.

So a community with none of these systems runs the hub with participants, PIN sign-in, tasks,
reminders, forms, sessions, surveys, sponsors and e-mail — and no external calls at all.

> ⚠️ **`config/integrations.*.json` is documentation, not a switchboard.** It describes each
> integration and carries non-secret settings, but the running application reads its switches from
> **app settings**, not from this file. Turning something off here changes nothing. (Making this file
> authoritative is a known upstream improvement.)

### 3a. 🔴 First run: the settings the template does NOT set — `scripts/Set-CommunitySettings.ps1`

**This is the step that catches everyone.** The infrastructure template provisions your resources and
sets the plumbing — database, Key Vault, storage, telemetry, the write guards. It does **not** set
your **e-mail** or your **integration switches**, because it cannot know them.

> 🔴 **A hub without e-mail settings looks perfectly healthy and cannot sign anybody in.**
> There is no startup validation. The site boots, `/health` returns 200, every page renders — and the
> first symptom is that a sign-in PIN never arrives, because sign-in *is* a PIN sent by e-mail. The
> real cause sits several layers down inside an SMTP retry, where nobody looks.

> ⚠️ **And the defaults are not neutral — they are the upstream event's.** An unset from-address,
> display name, organizer inbox or relay falls back to *Experts Live Denmark*'s own values. Your
> relay will refuse to send as a domain you do not own, and any organizer notice that does go out
> lands in their inbox. This is why the script treats an inherited value as an **error**, not as
> "not configured yet".

```powershell
# Ask a deployed environment what is wrong with it. Changes nothing.
./scripts/Set-CommunitySettings.ps1 -Check -ResourceGroup <rg> -WebAppName <web> -JobsAppName <jobs>

# Produce the settings for a first environment, with ALL mail captured to one inbox.
./scripts/Set-CommunitySettings.ps1 -OutFile settings.json `
    -SmtpHost smtp.example.net -SmtpUsername <user> `
    -SmtpKey '@Microsoft.KeyVault(VaultName=<vault>;SecretName=<secret>)' `
    -FromAddress 'info@mycommunity.org' -FromDisplayName 'My Community' `
    -OrganizerInbox 'info@mycommunity.org' -RedirectAllTo 'me@mycommunity.org'

# Or apply straight to both hosts.
./scripts/Set-CommunitySettings.ps1 -Apply -ResourceGroup <rg> `
    -WebAppName <web> -JobsAppName <jobs> `
    -SmtpHost smtp.example.net -SmtpUsername <user> -SmtpKey '<kv-ref>' `
    -FromAddress 'info@mycommunity.org' -EnableSessionize
```

**Required** — the hub does not work without them: `-SmtpHost -SmtpUsername -SmtpKey -FromAddress`.
The script refuses to write a half-configured environment rather than leaving you to discover it.

**`-RedirectAllTo` is the one to understand.** It sends *every* outgoing message to a single address
instead of its real recipient, so nothing can reach your community while you are still setting up.
Clearing it is how you go live — it is the single switch that opens the floodgates, so do it
deliberately rather than by never having set it.

**Integration switches** — `-EnableZoho`, `-EnableSessionize`, `-EnableWooCommerce`,
`-EnableEconomicErp`, `-EnableEconomicRoles`, `-EnableLinkedIn`, `-EnableCompanyManager`,
`-EnableSharePoint`, `-EnableOpenAI`, `-EnableCvrLookup`, `-EnableFxRates`. Anything you do not pass
is written as **false**, so one run produces a known posture rather than whatever was there before.

> ⚠️ **Both hosts.** The Functions host runs every scheduled job and needs the same settings. Set
> them on the web app only and you get a working site with a silent back end — which, again, looks
> fine. Pass `-JobsAppName`; the script warns if you don't.

> 🔑 **`-Check` verifies settings exist. Only a real PIN proves your relay accepts them.** After
> applying, request a sign-in code and confirm it arrives. That is the end-to-end test; everything
> else is a proxy for it.

### 3b. Setting the feature posture repeatably — `scripts/Set-FeatureBaseline.ps1`

*(This is the **second** step, and only once the environment above is working — it writes to the
database, which means an edition must already exist. For a brand-new deployment, do §3a first.)*

Layer 2 above is per-edition and lives in the database, so out of the box the only way to set it is
the Settings page, one switch at a time, across dozens of features. That is fine once. It stops
being fine when you stand up a second environment, or want to answer *"what is this environment
actually set to?"* six months later.

`scripts/Set-FeatureBaseline.ps1` captures that posture as a JSON file you can commit, review and
re-apply. Three modes, **only one of which writes**:

```powershell
# 1. Capture what an environment is set to. Writes nothing to the database.
./scripts/Set-FeatureBaseline.ps1 -Export -Path baseline.json `
    -Server <sql-host> -Database <db> -EventId 1 -AccessToken $tok

# 2. See what applying it would change. Writes nothing.
./scripts/Set-FeatureBaseline.ps1 -Apply -Path baseline.json -WhatIf `
    -Server <sql-host> -Database <db> -EventId 1 -AccessToken $tok

# 3. Apply it. -AllowEnable is required before anything is switched ON.
./scripts/Set-FeatureBaseline.ps1 -Apply -Path baseline.json -AllowEnable `
    -Server <sql-host> -Database <db> -EventId 1 -AccessToken $tok
```

`config-examples/feature-baseline.sample.json` shows the file format. **Don't hand-write it** —
`-Export` against a working environment and delete the entries you don't care about. That way the
feature keys come from your build rather than from a doc that can drift.

**What it will not do to you:**

- **It never deletes.** A feature in the database but not in your file is listed and left alone. The
  file is a baseline, not an authority on everything.
- **It is idempotent.** Re-applying an applied baseline changes nothing and says so.
- **It refuses to switch anything ON without `-AllowEnable`**, and prints every feature it would
  enable first.

> 🔴 **Enabling a feature can start sending real e-mail within minutes**, because many hub features
> send mail as part of normal operation. The script **cannot tell you which ones** — that mapping
> lives in the application's template catalog, not the database, and a duplicate of it inside a
> script would drift and then lie to you. So it makes you look: it lists what it is about to switch
> on and stops unless you pass `-AllowEnable`.
>
> ⇒ **On anything live, run `-WhatIf` first, then apply a file containing ONE feature**, confirm the
> result, and only then apply the rest. An untested bulk write is how you find out that your file
> meant something slightly different from what you thought.

> ⚠️ **One writer at a time.** The Settings page writes this same table. Don't apply a baseline while
> somebody is editing settings in the UI — the last writer wins and neither is told.

Authentication is `-ConnectionString`, or `-Server`/`-Database` plus either `-SqlUser`/`-SqlPassword`
or `-AccessToken`. There is deliberately no "use my Entra login" mode: the underlying SQL client has
no such connection-string option, and offering one would fail with a message that reads like a broken
script rather than a missing credential.

### 3c. Do I need to redeploy after changing something?

Usually **no** — but it depends which of three places the setting lives in, and the third one catches
people out.

| What you changed | Redeploy? | What happens |
|---|---|---|
| **App settings** — `Email__*`, `<System>__Enabled`, `Integrations__*` (§3a) | ❌ **No** | Azure App Service **restarts the app automatically** when app settings change. Give it a minute; the new values are live. |
| **Feature flags** — Settings page, or `Set-FeatureBaseline.ps1` (§3b) | ❌ **No, and no restart either** | They live in the database and are read on every call. The change is live immediately. |
| **Edition config files** — `config/event.*.json`, `config/integrations.*.json`, `config/content/**` | ✅ **YES** | 🔴 These are **compiled into the deployment artifact** (`CopyToPublishDirectory`) and read from disk. Editing the file in your repo changes **nothing** on a running site until you deploy again. |

> 🔴 **The third row is the one that wastes an afternoon.** `config/event.<edition>.json` holds your
> rooms, session levels, lengths and dates — things you edit often early on — and nothing anywhere
> tells you the running site is still serving the version that was deployed. If you changed a config
> file and the site disagrees with you, you have not deployed since.

> ⚠️ **Restart ≠ redeploy, and both are different from "it picked it up".** An app-settings change
> restarts the app, which means a brief cold start; on a production environment with deployment
> slots, prefer applying settings to the slot and swapping. If your hosting is not Azure App
> Service, an app-settings change may need a restart you perform yourself — the values are read
> once at startup.

---

## 4. Replacing an integration with your own

Some seams are already interfaces: implement the interface, register it in `Program.cs` /
`JobsServiceRegistration.cs` in place of the shipped one, and the rest of the application is unaware.

| You want your own… | Interface to implement | Status |
|---|---|---|
| E-mail delivery | `IEmailSender` | ✅ Clean seam (the shipped one is plain SMTP, so most relays work with configuration alone) |
| Document storage | `ISharePointFileStore` | ✅ Clean seam, with a Null implementation to copy |
| ERP / invoicing | `IEconomicErpClient`, `IEconomicInvoiceClient`, `IEconomicContactAdminClient`, `IEconomicRoleClient` | ✅ Clean seam, each with a TestMode implementation to copy |
| Session/speaker source | `ISessionSource` | ✅ Two shipped implementations to copy |
| Social publishing | `ILinkedInPostPublisher` | ✅ Clean seam |
| Company/tax-id lookup, FX rates | `ICvrValidator`, `IExternalCvrLookup`, `IFxRateProvider` | ✅ Clean seams |
| **Event platform** (agenda, speakers, attendees) | — | 🔴 **Not yet.** The shipped client is a concrete class used directly by several services. Switch it off and use the hub as the source of truth, or wait for the planned interface. |
| **Webshop / orders** | — | 🔴 **Not yet.** Same situation. Switch it off and the sponsor module works from manual entry. |

The two red rows are the honest limits today. If you need them, say so in an issue — it raises their
priority, and it is far better than forking the services and diverging.

---

## 5. What upstream promises

This is the **contract**. Changes to anything on this list are breaking and get a major version and
an upgrade note; everything else can change in any release.

- Configuration keys and file names, and the shape of the config files
- Feature keys
- Database schema and migrations
- The integration interfaces listed above
- The content folder layout and template names

**How upstream changes things:**

- **Additively.** New config keys ship with a default, new columns are nullable, new features default
  to off. An additive change cannot break your fork.
- **With aliases.** When a config or feature key is renamed, the old name keeps working for at least
  one minor release, and the release notes say so.
- **Never silently.** A change that needs you to act appears in the release notes for that version.

**What upstream does not promise:** internal class names, private methods, page markup, CSS, and the
wording of e-mails and UI text. Treat those as implementation detail — if you depend on them, pin a
version and merge deliberately.

---

## 6. Versioning

`public-vMAJOR.MINOR.PATCH`:

- **MAJOR** — something on the contract list changed. Read the upgrade note before merging.
- **MINOR** — new features and improvements; safe to merge, nothing required of you.
- **PATCH** — fixes only.

Upstream also carries its own edition's tags (`eldkNN-v*`); those are the maintainer's internal event
milestones. **As a fork, follow `public-v*` only.**

---

## 7. Before you go live with your own edition

Known upstream-specific leftovers still exist in code defaults — an ops mailbox address, a content
folder name, default config file names and a handful of URLs. The README's *Known limitations of the
public edition* section lists them, and moving them into configuration is in progress. Check that
list, and send yourself a test of each e-mail type before your first real send: the hub has a kill
switch, a redirect-all setting and per-audience rings for exactly this purpose.

---

## 8. Contributing back

Improvements that are not specific to your community are very welcome, and contributing them back is
also the cheapest way to stop maintaining a patch forever.

1. Fork, branch, and make the change.
2. Keep it configurable rather than hard-coded to your community, and add tests.
3. Open a pull request describing the problem it solves, not only the change.

A change that makes a hard-coded assumption configurable — especially one that turns a concrete
client into an interface — is the most valuable kind of contribution this project can receive.
