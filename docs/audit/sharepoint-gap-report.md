# SharePoint gap report — Phase 0

Work-order §2.3. Sections A–F. Companion to `sharepoint-reference-inventory.md`.

> **§2.3 says: "Stop after Phase 0. Sections C and F need owner review before Phase 2."**
> That review has **happened** — incrementally, as the operator asked (*"take one at a time until
> you have what you need"*). Every C and F item below carries his ruling. See §768.6–§768.10 in
> REQUIREMENTS and `sharepoint-open-questions.md`.

---

## A. Stale references — pointing at a deleted or moved folder

| Ref | What | Now | State |
|---|---|---|---|
| A1 | `Graphics__SharePoint__TemplateFolderPath` → `…/ELDK/Graphics/Template` | `Event/Graphics/Template` | ✅ **fixed** (emergency repoint) |
| A2 | `…SessionsFolderPath` → `…/Grahics-SoMe/Sessions` | `Speakers/Graphics-SoMe/Sessions` | ✅ **fixed** |
| A3 | `…MasterClassFolderPath` → `…/Grahics-SoMe/MasterClass` | shares the Sessions folder (§768.6) | ✅ **fixed** |
| A4 | `…SponsorLogosFolderPath` → `…/Logo-SoMeBranding` | `Sponsors/Logo/Web` | ✅ **fixed** |
| A5 | `…LogoPackFolderPath` → `…/EventHub/ELDK` | `Event/LogoPack` | 🔴 **open** — `ELDK/` is gone; web-only, so nothing crashes, it simply finds nothing |
| A6 | `…SessionEvalsQrFolderPath` → `…/Speakers/SessionEvals-QR` | `Speakers/SessionEvaluations/QR` | 🔴 **open** |
| A7 | `…SessionEvalPdfFolderPath` → `…/Speakers/SessionEvaluations` (root) | `Speakers/SessionEvaluations/Result` | 🔴 **open** — the root is now a container only |
| A8 | `config` `logoSoMeBrandingFolderPath` → `…/Logo-SoMeBranding` | `Sponsors/Logo/Web` | 🔴 **open** |
| A9 | `config` `logoPrintFolderPath` → `…/Logo-Print` | `Sponsors/Logo/Print` | 🔴 **open** |
| A10 | `config` `logoZohoFolderPath` → `…/Logo-Zoho` | **retired** (§6.7 merges into Web) | 🔴 **open** — delete the key |
| A11 | `config` `logoCollectionFolderPath` → `Sponsors/Logo` | **retired** (D7) | 🔴 **open** — delete the key + the second write |
| A12 | `appsettings.json:29` guidance text contains `Grahics-SoMe` | — | 🔴 **open** — will trip the §4 regression guard |
| A13 | `SoMePhase2GraphicsScenarioTests.cs:43` `"Graphics/Logo-SoMeBranding"` | test-local | 🔴 **open** |

⚠️ **A5–A7 are the dangerous ones**: all three are *reads*. A read against a missing folder returns
empty, which every caller treats as "nothing to do". They fail **silently** — no exception, no alert.

## B. Hardcoded paths — not resolved through configuration

| Ref | What | Verdict |
|---|---|---|
| B1 | 8 `subfolder:` literals in `GraphicsService` (`"Speakers"`, `"Sponsors"`, `"Sessions"`, `"SpeakerTracks"`, `"SponsorCategories"`) | **All move** under §768.7 and must resolve through the §7 path resolver, not string literals |
| B2 | `SubfolderFor(GraphicAssetType)` — the 6-way map | same; it is the single point every overrule resolves through |
| B3 | `GraphicsService.cs:62` `$"Pictures/speaker-{id}"` | hardcoded **and** an orphan — see C1 |
| B4 | `GraphicsSharePointOptions.RootFolderPath` defaults to `"Graphics"` | 🔴 A **default path in code** is exactly what §7.6 forbids (*"silent fallback to a default path is how this drift happened"*). Must become fail-fast |
| B5 | `config/event.eldk27.json` `siteUrl` | Acceptable per §7.5 (fork ships values) — but must be the SAME value the graphics options use, not a second copy |

## C. Orphan paths — code touching a folder absent from §3 · **OWNER-REVIEWED**

| Ref | Orphan | Ruling |
|---|---|---|
| C1 | `Pictures/speaker-{id}.{ext}` — where `FetchAndStoreSpeakerPictureAsync` stores the Sessionize portrait | ✅ Resolved **from §3.2**, which names *"Sessionize download service"* as a writer to `Speakers/Photos`. ⇒ repoint into `Speakers/Photos` with the D3 naming. `Pictures/` is retired |
| C2 | §165 external-designer build folders (per session / master class / track) | ✅ **RETIRE the whole pipeline** (§768.9 D4) — *"some graphics service builds now. it replaces human build process"* |
| C3 | `logoCollectionFolderPath` — the second, "all logos in one place" write | ✅ **RETIRE** (D7) — `Sponsors/Logo/Web` already is that place |
| C4 | `Sponsors/Sponsor Upload` — 10 company folders, each with an empty `SPONSORWALL` | ⚪ Live per-sponsor upload root (`config` `rootFolderPath`); in §3 only implicitly. Left as-is |

## D. Unimplemented — §3 entries with no code reference

| Ref | §3 key | Note |
|---|---|---|
| D1 | `EventEvalDuringSessions`, `EventEvalAfter{Sponsor,Attendee,Speaker}` | **NEW** — §6.5/§6.6 build work, folders already exist |
| D2 | `SwagAward`, `SwagPolo`, `SwagCredly`, `Hotel`, `BcFood`, `BcExpo` | **NEW** — §6.4 logistics service |
| D3 | `TravelReimbursement` | **CHANGE** — exists; §6.10 adds locking + reopen (§768.10 D5) |
| D4 | `SpeakerAvInstructions` (`Speakers/PicturesInstructions`) | ⚠️ **No `Graphics__SharePoint__` key exists for it.** 4 files present in prod. Either the AV page hardcodes them or reads them another way — **verify during Phase 2** |
| D5 | `SponsorBoothCollateral` | Exists via `config` `boothCollateralFolderPath` — OK |

## E. Naming violations — files written with a pattern other than §3/§5

| Ref | Writer | Writes today | Must write |
|---|---|---|---|
| E1 | `SponsorUploadKinds` + its duplicate in `CompanyDetails.ResolveKind` | `SoMeBrandingLogo_{Sponsor}_v{N}.png` | `{sponsorname}-logo-web-{version}.png` (§768.8 D1) |
| E2 | same | `PrintLogo_…`, `ExhibitorWall_…` | `{sponsorname}-logo-print-{v}.*`, `{sponsorname}-exhibitor-wall-{v}.*` |
| E3 | speaker photo writers (archive, sponsor upload, organizer upload) | `speaker-photo-{name}-{id}.{ext}` | ✅ **CLOSED — §768.16.** `speaker-photo-{id}.{ext}`, source extension kept (D3), composed by `SpeakerPhotoFileName.Build`. A **fourth** writer was found while doing it: the Sessionize import copy wrote `Speakers/speaker-{id}.{ext}` under the RETIRED graphics root — repointed onto the registry. Legacy names stay READABLE (a sponsor-uploaded photo is never re-archived). |
| E4 | §767 logo matcher `ResolveNewestSponsorLogo` | parses `_v{N}` after the first `_` | 🔴 **must be rewritten with E1** or every sponsor graphic silently stops building |

## F. Discovery answers — the *report* items · **OWNER-REVIEWED**

| Ref | Question (§) | Answer |
|---|---|---|
| F1 | Session graphic `.png`↔`.gif` switch — orphan handling? (§3.2) | Row is **upserted and repointed in place**; the old file is **orphaned**. §326af retire sweep is the cleanup. *(From code.)* |
| F2 | SpeakerTracks filename + is there a stable track ID? (§3.2) | `track-{slug}.gif`, keyed `track:{slug}` from the track **name**. **No stable track ID exists** — tracks are a free-text `Session.Track` string. ⇒ a rename desynchronises writer and reader silently (trap #8). *Unresolved by design; flagged.* |
| F3 | Do all sponsor upload types share one version format? (§5.1) | **Yes** — all four kinds (`some`/`print`/`zoho`/`wall`) go through one `SponsorUploadSpec` and one `_v{N}` parser. They agree today; E1 changes all of them together |
| F4 | Preview vs Final — attendee precedence when both exist? (§3.2) | ⚠️ **Not yet answered** — needs reading `SpeakerPresentationService` + the attendee page. Carry into Phase 2 |
| F5 | `SponsorCategories` — keys on track or category? (§3.3) | **Tier** (`sponsor-tier:{slug}` from `SponsorPackage`). Folder name is right; the old spec text was wrong. Type groupings deliberately absent pending the webshop category |
| F6 | Do sponsor uploads key on name or ID underneath? (§5.3) | **Name** (slugified company name). He was offered ID-keying and chose the readable name ⇒ a sponsor rename orphans earlier versions. Accepted (§768.8 D1) |
| F7 | Does a reconcile service exist for files deleted in SharePoint? (§5.7) | **Yes** — §326af, inside `PullSessionGraphicsAsync` (`Retired` count). ⚠️ It covers the *pull* only, not the generated-graphics folders. Report, don't build |
| F8 | How do preliminary-survey results reach CEH? (§6.5) | ⚠️ **Not yet answered** — carry into §6.5 |
| F9 | Delete permission on `Speakers/Photos` + `Volunteers/Photo`? (§6.9) | ⚠️ **Not yet verified.** The app identity's delete rights are untested — must be confirmed before §6.9 ships (it defaults to dry-run, so a missing permission would otherwise surface only when it's switched live) |
| F10 | Claim → COMPLETED: what triggers it, can it be reopened? (§6.10) | Reopen: **yes, organizer-only, audit-logged** (§768.10 D5). The *trigger* still needs reading in Phase 2 |

---

## Phase 0 verdict

**28 findings.** 4 fixed under the emergency repoint; **9 stale references remain**, of which A5–A7
fail silently on read. The two structural defects — **two config systems** (baseline §1) and a
**write root outside both configured roots** (§768.7) — are what Phase 2 exists to remove.

**Remaining unanswered, and deliberately carried rather than guessed:** F4, F8, F9.
