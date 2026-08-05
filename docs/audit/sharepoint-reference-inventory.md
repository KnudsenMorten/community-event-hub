# SharePoint reference inventory — Phase 0

Work-order §2.2. One row per call site. **Read-only pass — nothing here was changed.**
*(Exception: the four emergency setting repoints, logged in `sharepoint-remediation-log.md`. They
happened before this table was built, so the "as coded" column reflects the post-restore state where
noted.)*

**Coverage:** `src/` (web, Jobs, Core), `config/`, `tools/`, `tests/`, `appsettings*.json`.
Excluded: `bin/`, `obj/`, `publish*/`, `deploy-artifacts/`, and `docs/REQUIREMENTS.md` (prose, not
code).

**Verdicts:** OK · STALE (points somewhere dead) · HARDCODED (not config-resolved) ·
DUPLICATE (same folder owned twice) · MOVES (correct today, relocates under the §768.7 fresh start)

---

## 1. Configuration surfaces

| # | File · line | Symbol | Resolution | Canonical key (§3) | Verdict | Notes |
|---|---|---|---|---|---|---|
| 1 | `Core/Integrations/Graphics/GraphSharePointFileStore.cs:13` | `GraphicsSharePointOptions` | app settings `Graphics__SharePoint__*` | — | **DUPLICATE** | System B. 18 folder properties. Azure-only; leaves no git diff (§4.4 of the baseline) |
| 2 | `Core/Config/EventEditionConfig.cs:337` | `SharePointEditionConfig` | `config/event.eldk27.json` → `sharepoint` | — | **DUPLICATE** | System A. Ships in the repo |
| 3 | `config/event.eldk27.json:116` | `siteUrl` | literal | `DocLibrary:Site` | **HARDCODED** | Fork config — acceptable per §7.5, but must become one shared value with System B |
| 4 | `config/event.eldk27.json:119` | `rootFolderPath` | literal | — | OK | `…/Sponsors/Sponsor Upload` — an *upload* root, distinct from the §1 root |
| 5 | `config/event.eldk27.json:121` | `logoCollectionFolderPath` | literal | — | **STALE** | `…/Sponsors/Logo` — that is now a *container* (`Logo/Web`, `Logo/Print`), not a leaf |
| 6 | `config/event.eldk27.json:126` | `speakerPhotoFolderPath` | literal | `SpeakerPhotos` | **DUPLICATE** | Same folder as System B's `SpeakerPhotosFolderPath` |
| 7 | `config/event.eldk27.json:128` | `logoSoMeBrandingFolderPath` | literal | `SponsorLogoWeb` | **STALE** | `…/Logo-SoMeBranding` → `Sponsors/Logo/Web` |
| 8 | `config/event.eldk27.json:129` | `logoPrintFolderPath` | literal | `SponsorLogoPrint` | **STALE** | `…/Logo-Print` → `Sponsors/Logo/Print` |
| 9 | `config/event.eldk27.json:130` | `logoZohoFolderPath` | literal | *(retired)* | **STALE** | §6.7 merges Zoho logo into `Logo/Web`; key is deleted, not repointed |
| 10 | `config/event.eldk27.json:131-132` | `exhibitorWallFolderPath`, `boothCollateralFolderPath` | literal | `SponsorExhibitorWall`, `SponsorBoothCollateral` | OK | Folders still exist under the new tree |
| 11 | `src/CommunityHub/appsettings.json:29` | `_trackGraphicsComment` | doc comment | — | **STALE** | Contains the `Grahics-SoMe` misspelling in guidance text — will trip the §4 regression guard |
| 12 | `web/Program.cs:785,804` · `Jobs/Program.cs` | `Configure<GraphicsSharePointOptions>` | binding | — | OK | Both hosts bind System B |

## 2. Write-path literals — the §768.7 root move

🔴 **Every row here writes under `RootFolderPath`, which defaults to `"Graphics"` — the drive root,
outside both configured roots.** All of them relocate.

| # | File · line | Symbol | Path as coded | Canonical key | Verdict |
|---|---|---|---|---|---|
| 13 | `GraphicsService.cs:104` | `GenerateSpeakerGraphicAsync` | `Speakers` | *(none — legacy, no prod caller)* | **MOVES** |
| 14 | `GraphicsService.cs:124` | `GenerateSponsorGraphicAsync` | `Sponsors` | *(none — legacy, no prod caller)* | **MOVES** |
| 15 | `GraphicsService.cs:144` | `GenerateSessionGraphicAsync` | `Sessions` | *(none — legacy, no prod caller)* | **MOVES** |
| 16 | `GraphicsService.cs:186` | `GenerateTrackBundleAsync` | `SpeakerTracks` | `SpeakerTrackGraphics` | **MOVES** |
| 17 | `GraphicsService.cs:245` | `GenerateSponsorCategoryBundleAsync` | `SponsorCategories` | `SponsorGraphicsCategories` | **MOVES** |
| 18 | `GraphicsService.cs:304` | `GenerateSessionBundleAsync` | `Sessions` | `SpeakerSessionGraphics` | **MOVES** |
| 19 | `GraphicsService.cs:341` | `GenerateSponsorSingleAsync` | `Sponsors` | `SponsorGraphicsSponsors` | **MOVES** |
| 20 | `GraphicsService.cs:1222` | `SubfolderFor(type)` | 6-way map | all of the above | **MOVES** | the single map every overrule resolves through |
| 21 | `GraphicsService.cs:62` | `FetchAndStoreSpeakerPictureAsync` | `Pictures/speaker-{id}` | ⚠️ **none in §3** | **ORPHAN** — see gap report C |

## 3. Read/upload call sites (config-resolved — no literals)

| # | File · line | Symbol | Folder from | Canonical key | Verdict |
|---|---|---|---|---|---|
| 22 | `SoMeBundleBuildService.cs` | `BuildAsync` | `TemplateFolderPath`, `SpeakerPhotosFolderPath`, `SponsorLogosFolderPath` | `EventGraphicsTemplate`, `SpeakerPhotos`, `SponsorLogoWeb` | OK *(post-restore)* |
| 23 | `GraphicsService.cs:302` | `PullSessionGraphicsAsync` | `SessionsFolderPath`, `MasterClassFolderPath`, `TrackGraphicsFolderPath` | `SpeakerSessionGraphics` | OK *(post-restore)* — the MC branch collapses (§768.6) |
| 24 | `SessionEvalPdfService.cs:223` | `UploadToFolderAsync` | `SessionEvalPdfFolderPath` | `SessionEvaluationResults` | **STALE** | points at `Speakers/SessionEvaluations` root; §4 says `/Result` |
| 25 | `SessionEvalsQrService.cs:204,208` | `UploadToFolderAsync`, `DeleteFromFolderAsync` | `SessionEvalsQrFolderPath` | `SessionEvaluationQr` | **STALE** | points at `Speakers/SessionEvals-QR`; §4 says `SessionEvaluations/QR` |
| 26 | `SpeakerPresentationService.cs:300` | `UploadToFolderAsync` | `PresentationPreviewFolderPath` / `PresentationFinalFolderPath` | `SessionPresentations*` | OK | ⚠️ **absent on the PROD jobs host** — web-only today |
| 27 | `VenueImageService.cs` | enumerate | `VenueRootFolderPath` | `VenueGoodToKnow`, `VenueWayfinding` | OK | ⚠️ web-only; §3.7 casing hazards unverified |
| 28 | `LogoPackService.cs` | enumerate | `LogoPackFolderPath` | `EventLogoPack` | **STALE** | PROD value is `…/EventHub/ELDK` — `ELDK/` no longer exists |
| 29 | `SpeakerTemplateService.cs` | download | `SpeakerTemplateFolderPath` | `SpeakerTemplate` | OK | |
| 30 | `SharePointGroundingProvider.cs` | enumerate | `GroundingFolderPath` | `AiGrounding` | OK | |
| 31 | `ExternalDesignerGraphicsService.cs:187,251` | `UploadToFolderAsync` | §165 designer folders | ⚠️ **none in §3** | **ORPHAN** — gap report C |
| 32 | `SponsorUploadKinds.cs:35` | `Resolve(kind, sp)` | System A folders | `SponsorLogo*`, `SponsorExhibitorWall` | **STALE** | 4 kinds; `zoho` retires (§6.7) |
| 33 | `Sponsor/CompanyDetails.cshtml.cs:466` | `ResolveKind` | System A folders | same | **DUPLICATE** | a second copy of #32 — the file's own comment admits *"Two copies of those rules is exactly the"* problem |
| 34 | `Volunteer/Signup.cshtml.cs:319` | upload | `volunteerPhotoFolderPath` | `VolunteerPhotos` | OK | |
| 35 | `SpeakerPhotoArchiveService.cs` | write | `speakerPhotoFolderPath` | `SpeakerPhotos` | OK | filename convention changes (§3.2) |

## 4. Tests / tools carrying old literals

| # | File · line | Verdict | Notes |
|---|---|---|---|
| 36 | `tests/.../SoMePhase2GraphicsScenarioTests.cs:43` | **STALE** | `"Graphics/Logo-SoMeBranding"` — test-local constant; update with the repoint |
| 37 | `tools/revert-767-sponsor-logo-test.ps1:43` | **STALE (intentional)** | Names the now-deleted folder by design — it is a cleanup script for that exact test |
| 38 | `tests/.../SpeakerPhotoArchiveServiceTests.cs:255`, `SpeakerPhotoUrlTests.cs:20` | OK | `sharepoint.com` appears in URL-shape assertions, not as a configured path |
