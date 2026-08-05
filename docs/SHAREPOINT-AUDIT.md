# SharePoint integration — AUDIT BASELINE

> **Status: BASELINE ONLY — measured 2026-08-02, before the operator's change list arrives.**
> Nothing here is a proposed change. This is *what is true today*, established by reading the code
> and the live Azure app settings, so that the operator's instructions can be checked against
> something factual rather than against an assumption.
>
> 🔒 **READ THIS FIRST if you are auditing SharePoint paths.** The operator's concern, verbatim
> (2026-08-02): *"we use sharepoint lots of places, but the paths and functionality have been moved
> around and I am worried they dont link correctly."* He is right to worry — see the findings.

---

## 1. THERE ARE TWO INDEPENDENT PATH SYSTEMS. This is the root of the problem.

CEH configures SharePoint folders in **two places that do not know about each other**:

| | **System A — edition config** | **System B — Azure app settings** |
|---|---|---|
| Where | `config/event.eldk27.json` → `"sharepoint"` block | App settings `Graphics__SharePoint__*` |
| Bound to | `SharePointEditionConfig` (`src/CommunityHub.Core/Config/EventEditionConfig.cs`) | `GraphicsSharePointOptions` (`src/CommunityHub.Core/Integrations/Graphics/GraphSharePointFileStore.cs`) |
| Ships how | **in the repo**, deployed with the code | **set per host in Azure**, ⚠️ swaps with the slot |
| Changed by | a commit + deploy | `az webapp config appsettings set` — no deploy, no audit trail |
| Used for | sponsor/volunteer/speaker **uploads** (people putting files IN) | graphics **read + write** (the engine) |

⚠️ **They already point at the same folders from two sources.** Change one and the other keeps the
old value, silently:

| Folder | System A key | System B key |
|---|---|---|
| `…/Speakers/Photos` | `speakerPhotoFolderPath` | `Graphics__SharePoint__SpeakerPhotosFolderPath` |
| `…/Sponsors/Logo-SoMeBranding` | `logoSoMeBrandingFolderPath` | `Graphics__SharePoint__SponsorLogosFolderPath` |

🔑 **Any consolidation proposal must decide which system OWNS a path**, and make the other read from
it — not "update both", which is the state that produced this audit.

---

## 2. Live settings matrix — measured 2026-08-02

`SET` = key present with a value · `-` = **key absent entirely**

| `Graphics__SharePoint__…` | PROD fn | PROD web | DEV fn | DEV web | PROD value |
|---|:--:|:--:|:--:|:--:|---|
| `Enabled` | SET | SET | **-** | SET | `true` |
| `SiteUrl` | SET | SET | **-** | SET | `https://expertslivedk.sharepoint.com/sites/ExpertsLiveDK` |
| `DriveName` | SET | SET | **-** | SET | `Documents` |
| `TemplateFolderPath` | SET | SET | **-** | **-** | `…/EventHub/ELDK/Graphics/Template` |
| `SpeakerPhotosFolderPath` | SET | SET | **-** | **-** | `…/EventHub/Speakers/Photos` |
| `SponsorLogosFolderPath` | SET | SET | **-** | **-** | `…/EventHub/Sponsors/Logo-SoMeBranding` |
| `SessionsFolderPath` | SET | SET | **-** | **-** | `…/Speakers/Grahics-SoMe/Sessions` |
| `MasterClassFolderPath` | SET | SET | **-** | **-** | `…/Speakers/Grahics-SoMe/MasterClass` |
| `SessionEvalsQrFolderPath` | SET | SET | **-** | **-** | `…/Speakers/SessionEvals-QR` |
| `SessionEvalPdfFolderPath` | SET | SET | **-** | **-** | `…/Speakers/SessionEvaluations` |
| `GroundingFolderPath` | **-** | SET | **-** | SET | `…/EventHub/ExtraAIGroundingInfo` |
| `LogoPackFolderPath` | **-** | SET | **-** | **-** | `…/EventHub/ELDK` |
| `PresentationPreviewFolderPath` | **-** | SET | **-** | **-** | `…/Speakers/Presentations/Preview` |
| `PresentationFinalFolderPath` | **-** | SET | **-** | **-** | `…/Speakers/Presentations/Final` |
| `SpeakerTemplateFolderPath` | **-** | SET | **-** | **-** | `…/Speakers/SpeakerTemplate` |
| `VenueRootFolderPath` | **-** | SET | **-** | **-** | `…/EventHub/Venue` |
| `RootFolderPath` | **-** | **-** | **-** | **-** | *(defaults to `Graphics` in code)* |
| `TracksFolderPath` / `TrackGraphicsFolderPath` | **-** | **-** | **-** | **-** | *(unset — §158 track pull is inert BY DESIGN, see §767 Round 9)* |

🔒 **`…/Speakers/Grahics-SoMe/…` is misspelled IN PRODUCTION and that is the real folder name.**
Do not "correct" it in config without renaming the SharePoint folder first — they must match.

---

## 3. Which host consumes which setting

Measured by where each service is referenced (`src/CommunityHub.Jobs` vs `src/CommunityHub`).

| Setting | Consumer | Runs in |
|---|---|---|
| `TemplateFolderPath`, `SpeakerPhotosFolderPath`, `SponsorLogosFolderPath` | `SoMeBundleBuildService` (§767 sweep) | **jobs** |
| `SessionsFolderPath`, `MasterClassFolderPath`, `TrackGraphicsFolderPath` | `GraphicsService.PullSessionGraphicsAsync` | **jobs** |
| `SessionEvalPdfFolderPath` | `SessionEvalPdfService` | jobs **+** web |
| `SessionEvalsQrFolderPath` | `SessionEvalsQrService` | jobs **+** web |
| `GroundingFolderPath` | `SharePointGroundingProvider` | web only |
| `LogoPackFolderPath` | `LogoPackService` | web only |
| `PresentationPreviewFolderPath`, `PresentationFinalFolderPath` | `SpeakerPresentationService` | web only |
| `SpeakerTemplateFolderPath` | `SpeakerTemplateService` | web only |
| `VenueRootFolderPath` | `VenueImageService` | web only |
| `RootFolderPath` | `GraphSharePointFileStore` — the WRITE root for generated artwork | both |

---

## 4. FINDINGS — what the baseline already shows

### 4.1 ✅ The six settings missing from the PROD jobs host are (currently) harmless
`GroundingFolderPath`, `LogoPackFolderPath`, `Presentation*`, `SpeakerTemplateFolderPath`,
`VenueRootFolderPath` are absent on `eldk27hub-fn-prodpdrq` — and every one of them is consumed by a
**web-only** service. So nothing is broken today.

⚠️ **But it is fragile and undocumented.** The moment any JOB touches one of those services, it goes
**inert and says nothing** — the store returns "not configured" and the code path treats that as a
legitimate zero (the §757/§764 "a silent zero reads like success" family). ⇒ **If the change list
moves any of that work into a job, the setting must move with it.**

### 4.2 🔴 DEV CANNOT EXERCISE THE SHAREPOINT INTEGRATION AT ALL
- `eldk27hub-fn-devz237e` has **no `Graphics__SharePoint__*` settings whatsoever** — not even
  `Enabled` / `SiteUrl`. Every SharePoint-dependent job on DEV is inert.
- `eldk27hub-web-devz237e` has only `Enabled`, `SiteUrl`, `DriveName`, `GroundingFolderPath`.

🔑 **This is why §767 could only ever be verified in PRODUCTION** — every graphics change this week
was tested against live data because DEV had nowhere to read from or write to. That is a real
risk carried by the whole feature, and it is a configuration gap, not a code one.

⇒ **A serious audit should decide whether DEV gets its own folder tree** (e.g. a `DEV/` prefix under
the same site) so this work stops being prod-only. **Do not simply copy the PROD values to DEV** —
that would point DEV at the live folders and let a DEV run overwrite real artwork.

### 4.3 ⚠️ Two systems, one folder — the drift trap
See §1. `…/Speakers/Photos` and `…/Sponsors/Logo-SoMeBranding` each have **two** owners.

### 4.4 ⚠️ Settings are invisible to code review
System B lives only in Azure. There is no file in the repo that records what PROD's paths are, so a
path change leaves **no diff, no commit, no review** — and a slot swap can carry a different set
(see the standing note that app settings swap with the slot; they must be set on BOTH web slots).

---

## 5. Method — how to reproduce every number above

**Auth is two paths, chosen by TOOL** (see `CLAUDE.md`): `Connect-AzAccount -CertificateThumbprint`
for `Get-AzWebApp`; `az login --service-principal --certificate <pem>` for anything `az`.

```powershell
# Settings for one host
$s=@{}; (Get-AzWebApp -ResourceGroupName 'rg-eldk27hub-prod' -Name 'eldk27hub-fn-prodpdrq').SiteConfig.AppSettings |
    ForEach-Object { $s[$_.Name]=$_.Value }
$s.Keys | Where-Object { $_ -like 'Graphics__SharePoint__*' } | Sort-Object | ForEach-Object { "$_ = $($s[$_])" }
```

⚠️ **Distinguish ABSENT from EMPTY.** A key that is missing and a key set to `""` behave the same at
runtime but mean different things to an audit — the first was never configured, the second was
deliberately disabled. Report them differently.

**To LIST a SharePoint folder** (never guess a naming convention — §767 shipped a matcher that
matched nothing for four prod runs because the convention was assumed): the recipe is in `CLAUDE.md`
under the SharePoint block. Note the **deployment SPN answers `/drives` with HTTP 200 and an empty
array** — a permissions gap that reads exactly like an empty library. `SharePoint__ClientId` is the
Management SPN and is the one that works.

---

## 6. Open questions for the operator's change list to answer

1. **Which system owns a path** when both define it (§1)?
2. **Does DEV get its own SharePoint tree** (§4.2), or does the integration stay prod-only by design?
3. **Should the `Grahics-SoMe` typo be fixed** — which means renaming the live folder, not just the config?
4. Are `TracksFolderPath` / `TrackGraphicsFolderPath` **retired for good** (§767 Round 9 says the
   per-speaker track still is dropped and the folder must stay unconfigured), or do they come back?
5. Should the PROD settings be **captured in the repo** (a checked-in manifest + a drift check) so a
   path change is reviewable (§4.4)?
