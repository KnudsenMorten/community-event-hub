# CEH — SharePoint (DocLibrary) Audit & Remediation Work Order

**Target repos:** `C:\community-repos\community-event-hub` (upstream, generic) · `C:\community-repos\eldk-community-event-hub` (ELDK fork)
**Source of truth:** `EventHub_Sharepoint_paths.xlsx`, corrected by owner decisions recorded in §12
**Audience:** an executing Claude session with the codebase mounted
**Status:** all owner questions resolved. This document is executable end to end.

---

## 0. Read this first

The SharePoint folder structure was reorganised: folders renamed, moved, merged and deleted. Code still points at old locations in an unknown number of places. Your job is to find every reference, repoint it, verify the surrounding functionality still works, and build the small set of genuinely new features in §6.

**Scope framing — read this before planning anything.** The overwhelming majority of this work is **validation and repointing of existing functionality**, not new development. Where this document describes behaviour, that behaviour usually already exists in code; your task is to confirm it works against the corrected path. Only items explicitly marked **NEW** or **CHANGE** are build work. When in doubt, assume the feature exists and go read it.

**There is no migration.** Nothing is in production. No files need moving, renaming or preserving. Old folders and their contents are irrelevant — do not write migration logic, dual-read fallbacks, or transition periods. Repoint and move on.

**Working rules:**

1. **Phase 0 is read-only.** Produce the inventory and gap report before changing anything. Fixing during discovery contaminates the inventory.
2. **Never invent a path.** If a path isn't in §3, stop and record it. A wrong path fails silently at runtime — the exact failure mode this exercise exists to eliminate.
3. ~~**Work on a branch**~~ 🔒 **CORRECTED 2026-08-02 — COMMIT TO `main`.** One commit per phase, phase name in the message, **directly on `main`**. Operator: *"push to main only. i dont allow the others. it is mistake"*. The MAIN-ONLY rule in `CLAUDE.md` (operator 2026-07-23) **outranks this work order** — the §768 branch was merged fast-forward into `main` and deleted. Do not open another one.
4. **Vendor-neutral naming in code.** SharePoint is `DocLibrary` throughout. No "SharePoint" or "ELDK" strings in upstream `community-event-hub` — those belong in the fork's configuration. Existing conventions: `ExternalEventSystem` (Zoho Backstage), `ERPSystem` (e-conomic), `WebshopSystem` (WordPress), `DocLibrary` (SharePoint), `CallForSpeakersSystem` (Sessionize), `SignageSystem` (OptiSigns), `SessionEvaluationSystem`, `HotelBookings`, `SendMailSystem` (Brevo).
5. **Doc convention:** `requirements.md` holds only undelivered functionality; delivered functionality is documented in `features.md` and `readme.md`. Move items as you deliver.
6. **Work runs against the live ELDK 2027 folder.** There is no separate dev environment. Read operations are unrestricted. The one service that deletes files (§6.9) ships with dry-run logging enabled by default.

---

## 1. The SharePoint root

```
Site:            https://expertslivedk.sharepoint.com/sites/ExpertsLiveDK
Library:         Shared Documents
Root folder:     General/Events/ELDK 2027/EventHub
Server-relative: /sites/ExpertsLiveDK/Shared Documents/General/Events/ELDK 2027/EventHub
```

Every path in §3 is **relative to this root**, and the root is **one configuration value**. If paths are currently stored as absolute URLs or duplicated per service, that is itself a defect — it is what makes CEH non-generic and what allowed this drift to happen.

The root contains spaces (`Shared Documents`, `ELDK 2027`). Verify the DocLibrary client encodes Graph path segments correctly; a meaningful share of "wrong path" bugs are encoding bugs.

---

## 2. Phase 0 — Discovery (read-only)

### 2.1 Search terms

Across all projects — web app, Functions, shared libs, tests, seed scripts, `appsettings*.json`, IaC, `.razor`, `.cshtml`, `.cs`, `.ps1`, `.md`:

| Term | Finds |
|---|---|
| `expertslivedk.sharepoint.com` | hardcoded absolute URLs |
| `sharepoint`, `SharePoint`, `SPO` | naming violations + references |
| `Shared Documents`, `Shared%20Documents` | library references |
| `EventHub/`, `ELDK 2027` | root fragments |
| `graph.microsoft.com`, `/drives/`, `/drive/root:` | Graph drive-item calls |
| `DocLibrary`, `IDocLibrary`, `DocumentLibrary` | the intended abstraction |
| `UploadAsync`, `DownloadAsync`, `GetFileAsync`, `DriveItem` | I/O call sites |
| `Speakers/`, `Sponsors/`, `Sponsor/`, `Event/`, `Venue/`, `Volunteers/`, `Finance/` | path literals |
| `Grahics`, `LogoPack`, `SessionEval`, `Exhibitor Wall`, `Booth`, `Logo-` | old/misspelled folder literals |

### 2.2 `docs/audit/sharepoint-reference-inventory.md`

One row per call site: file · line · symbol · direction (upload/download/process/link) · path as coded · resolution (literal/config/computed) · canonical key from §3 · verdict (OK / STALE / MISSING / DUPLICATE / HARDCODED) · notes.

### 2.3 `docs/audit/sharepoint-gap-report.md`

- **A. Stale references** — pointing at a deleted or moved folder (§4)
- **B. Hardcoded paths** — not resolved through configuration
- **C. Orphan paths** — code touching a folder absent from §3. **Dangerous:** either the registry is incomplete or the code invented a folder
- **D. Unimplemented paths** — §3 entries with no code reference. Cross-check §6; some are new, some are silent breakage
- **E. Naming violations** — files written with a pattern other than §3/§5
- **F. Discovery answers** — the specific questions in §3 and §6 marked *report*

**Stop after Phase 0. Sections C and F need owner review before Phase 2.**

---

## 3. Canonical path registry

All paths relative to §1. `Key` values are the configuration keys the Organizer page (§6.1) binds to — use exactly these.

**Status:** ACTIVE = exists and in use · NEW = folder exists, feature to build · CHANGE = exists, behaviour changes

### 3.1 AI & Finance

| Key | Status | Path | Direction | Filenames | Notes |
|---|---|---|---|---|---|
| `DocLibrary:Paths:AiGrounding` | ACTIVE | `ExtraAIGroundingInfo` | read | Word, Excel, PDF | Drop-folder for Community Helper grounding material. Pipeline, instructions and OpenAI backend **all exist** — reuse unchanged. Only verify the folder it enumerates. Trigger: daily scheduled + manual on-demand (§6.8). |
| `DocLibrary:Paths:TravelReimbursement` | CHANGE | `Finance/Travel Reimbursement` | upload | `claim-{claimid}-{seq}.{ext}` | Speaker travel-reimbursement claims. **Up to 5 files per claim.** PDF and images both accepted — already built. Claim ID must appear in the filename **and** in the organizer notification mail, for payment reconciliation. See §6.10 for the COMPLETED-state change. |

### 3.2 Speakers

| Key | Status | Path | Direction | Filenames | Notes |
|---|---|---|---|---|---|
| `DocLibrary:Paths:SpeakerSessionGraphics` | ACTIVE | `Speakers/Graphics-SoMe/Sessions` | write + read | 1 speaker: `session-{id}.png` · 2+: `session-{id}.gif` (animated) | Graphics service output; SoMe publishing input; source for speaker My Sessions promotion. **Feature is built — repoint only.** Code currently points at the wrong folder. *Report:* how the existing code handles the extension switch when speaker count changes (does it remove the obsolete variant, or leave an orphan?). |
| `DocLibrary:Paths:SpeakerTrackGraphics` | ACTIVE | `Speakers/Graphics-SoMe/SpeakerTracks` | write + read | *report* | Graphics service output; SoMe publishing input. Correlation key `track:{name}`. **Repoint only.** *Report:* the current filename pattern, and whether tracks have a stable ID that could be keyed on instead of the name — a track rename currently desynchronises writer and reader silently. |
| `DocLibrary:Paths:SpeakerPhotos` | CHANGE | `Speakers/Photos` | write + upload + read + **delete** | `speaker-photo-{speakerid}.png` | Writers: Sessionize download service (`SpeakerCategory` Community, Guest); sponsor upload when a sponsor has a speaking session; organizer upload. Sole photo source for the Graphics service (Sessions and SpeakerTracks). **Filename changes** — the speaker name is dropped from the current `speaker-photo-{name}-{id}.png`. All writers plus the Graphics service update together. Also subject to §6.9 deletion. |
| `DocLibrary:Paths:SpeakerAvInstructions` | ACTIVE | `Speakers/PicturesInstructions` | read | static: `av.jpg`, `comfort-screen.jpeg`, `hdmi-swiitcher.jpeg`, `stage-timer.jpeg` | AV page images (HDMI, stage timer, comfort monitor). Filenames static in code — **including the misspelling `hdmi-swiitcher.jpeg`**. Verify character-for-character. |
| `DocLibrary:Paths:SessionPresentationsPreview` | ACTIVE | `Speakers/Presentations/Preview` | upload + download | PDF, PPT/PPTX | **Two separate speaker upload tasks already exist.** Verify this one targets Preview. |
| `DocLibrary:Paths:SessionPresentationsFinal` | ACTIVE | `Speakers/Presentations/Final` | upload + download | PDF, PPT/PPTX | Verify the second upload task targets Final. Both folders feed the attendee session-overview download. *Report:* the attendee page's current precedence rule when both a Preview and a Final exist — do not invent one. |
| `DocLibrary:Paths:SessionEvaluationQr` | ACTIVE | `Speakers/SessionEvaluations/QR` | write + download | stored by `session:{id}`, delivered by session **title** | Written by the Session Evaluation system, one QR per session for the speaker's deck. Read by My Sessions. |
| `DocLibrary:Paths:SessionEvaluationResults` | ACTIVE | `Speakers/SessionEvaluations/Result` | write + read + mail attach | stored by `session:{id}`, delivered by session **title** | Written when a session's results are final. Read by My Sessions. Attachment source for the speaker results mail. |
| `DocLibrary:Paths:SpeakerTemplate` | ACTIVE | `Speakers/SpeakerTemplate` | download | `ELDK27_PPT_Template.potx` | Presentation template. **Gated to `SpeakerCategory` Community and Guest only** — sponsors are not required to use a CEH template and see no download link. *(The spreadsheet's "folder has been deleted" note was wrong; the folder is live.)* |

### 3.3 Sponsors

| Key | Status | Path | Direction | Filenames | Notes |
|---|---|---|---|---|---|
| `DocLibrary:Paths:SponsorBoothCollateral` | ACTIVE | `Sponsors/Booth Collateral` | upload + read | *report* | Uploaded in the sponsor Get Started wizard; source for the booth-collateral sync to the external event system. |
| `DocLibrary:Paths:SponsorExhibitorWall` | ACTIVE | `Sponsors/Exhibitor Wall` | upload | `{sponsorname}-exhibitor-wall-{version}.*` | Sponsor task upload, any format. Versioned (§5.1). Notification to the reviewers on each upload **already exists** — verify recipients and move them to settings (§5.6). |
| `DocLibrary:Paths:SponsorLogoWeb` | CHANGE | `Sponsors/Logo/Web` | upload + read | `{sponsorname}-logo-web-{version}.png` | **PNG only.** Replaces both the old Zoho logo and the old SoMe-branding logo — one file now serves both. Consumers: SoMe Branding/Graphics service; the organizer mail/service carrying the logo to the external event system (the Zoho API cannot accept it programmatically). See §6.7. |
| `DocLibrary:Paths:SponsorLogoPrint` | ACTIVE | `Sponsors/Logo/Print` | upload | `{sponsorname}-logo-print-{version}.*` | Vector formats: EPS, AI, PDF. Versioned. |
| `DocLibrary:Paths:SponsorGraphicsSponsors` | ACTIVE | `Sponsors/Graphics-SoMe/Sponsors` | write + read | *report* | Per-sponsor promotion graphics. **Feature is built — repoint only**, then rebuild the files at the new path. |
| `DocLibrary:Paths:SponsorGraphicsCategories` | ACTIVE | `Sponsors/Graphics-SoMe/SponsorCategories` | write + read | *report* | Graphics for posts mentioning all sponsors in a group. **Built — repoint only.** *Report:* whether the existing grouping logic keys on track or on sponsor category; the folder name says category, the original spec description said track. If they disagree, raise it — a folder rename is cheap while nothing depends on it. |

### 3.4 Event — evaluations

| Key | Status | Path | Direction | Notes |
|---|---|---|---|---|
| `DocLibrary:Paths:EventEvalDuringSessions` | NEW | `Event/Evaluations/During the event/SessionEvaluations` | write | A **second copy** of every session evaluation result — not a replacement for `SessionEvaluationResults` — so all evaluation material sits together for post-event analysis. Also holds a **combined summary PDF across all sessions**. Notify `info@expertslive.dk` on completion. |
| `DocLibrary:Paths:EventEvalAfterSponsor` | NEW | `Event/Evaluations/After the event/Sponsor` | write | Summary output of the new post-event sponsor survey (§6.5). |
| `DocLibrary:Paths:EventEvalAfterAttendee` | NEW | `Event/Evaluations/After the event/Attendee` | write | Summary output of the new post-event attendee survey (§6.5). |
| `DocLibrary:Paths:EventEvalAfterSpeaker` | NEW | `Event/Evaluations/After the event/Speaker` | write | Summary output of the new post-event speaker survey (§6.5). |

### 3.5 Event — logistics

All files lowercase, prefixed with the **event short name** (`eldk27`), which is an **event-level setting**, never a constant.

| Key | Status | Path | Files | Notes |
|---|---|---|---|---|
| `DocLibrary:Paths:SwagAward` | NEW | `Event/Swag/Award` | `eldk27-award.xlsx` | name, role, amount |
| `DocLibrary:Paths:SwagPolo` | NEW | `Event/Swag/Polo` | `eldk27-polo.xlsx` | name, role, size, amount |
| `DocLibrary:Paths:SwagCredly` | NEW | `Event/Swag/Credly` | `eldk27-credly-{rolename}.xlsx` + `.csv` | One pair **per CEH role**. Columns: name, email |
| `DocLibrary:Paths:Hotel` | NEW | `Event/Hotel` | `eldk27-hotel-{hotelname}.xlsx` | **One file per hotel engagement.** From 3 weeks before the event, any change sends the file to that hotel's contact — **hotel contacts already exist in CEH**, use them |
| `DocLibrary:Paths:BcFood` | NEW | `Event/BC/Food` | `eldk27-breakfast-day1-preday.xlsx`<br>`eldk27-breakfast-day2-mainday.xlsx`<br>`eldk27-lunch-day1-preday.xlsx`<br>`eldk27-lunch-day2-mainday.xlsx`<br>`eldk27-appreciationdinner-preday.xlsx`<br>`eldk27-party-preday.xlsx` | Six files, one folder. Rebuilt daily. Weekly mail to `eldk2027@bellacenter.dk`. **Appreciation Dinner** counts accompanying guests and lists speakers with allergies by name, with their comments. **Party** counts from sign-ups |
| `DocLibrary:Paths:BcExpo` | NEW | `Event/BC/Expo` | `eldk27-expo-rental-tv.xlsx`<br>`eldk27-expo-rental-furniture.xlsx` | Built from webshop orders: sponsor + TV count; sponsor + furniture types + counts. Rebuilt daily. TV file weekly to `eldk2027@bellacenter.dk`; furniture file weekly to `mok@expertslive.dk` |

> **Note:** the spreadsheet's column A showed an `Event/Logistics/...` prefix for all of the above. That segment does not exist in SharePoint — the flat paths here are correct.

### 3.6 Event brand assets, venue, volunteers

| Key | Status | Path | Direction | Files | Notes |
|---|---|---|---|---|---|
| `DocLibrary:Paths:EventGraphicsTemplate` | ACTIVE | `Event/Graphics/Template` | read | `template.png`, `LOGO EXPERTS LIVE denmark WHITE_no shadow.png` | Background/overlay assets for the Graphics service. Static filenames. Replaces deleted `ELDK/Graphics/Template`. **Path and both filenames must be editable settings** on the Organizer page (§6.1). |
| `DocLibrary:Paths:EventLogoPack` | ACTIVE | `Event/LogoPack` | read | `EventLogo-ELDK27.png`, `EXPERTS LIVE Denmark - WHITE.png`, `EXPERTS LIVE Denmark.png`, `logopack.zip` | **Every** logo reference in CEH resolves here. `logopack.zip` moved into this folder — repoint it. Replaces deleted `ELDK/LogoPack`. |
| `DocLibrary:Paths:VenueGoodToKnow` | ACTIVE | `Venue/Good to know` | read | static — enumerate the folder | Images linked from event-info pages. See §3.7. |
| `DocLibrary:Paths:VenueWayfinding` | ACTIVE | `Venue/Wayfinding` | read | static — `A1-A2-A3-floor0.png`, `Checkin-ATE-floor0.png`, `Expo.png`, `Food.png`, `Lounge.png`, `Overview-floor0.png`, `Overview-floor1.png`, `Rooms-floor1.png`, `Sessions-floor1-overview.png`, … | Floor plans and wayfinding images linked from event-info pages. |
| `DocLibrary:Paths:VolunteerPhotos` | ACTIVE | `Volunteers/Photo` | upload + **delete** | *report* | Photo upload **already exists** in the volunteer sign-up flow — verify and repoint. Subject to §6.9 deletion. |

### 3.7 Venue folder hazards

Both Venue folders use static filenames referenced from content pages. The actual contents carry traps:

- **Mixed extension casing** — `.JPG`, `.PNG`, `.png`, `.jpg` coexist. **All static filename matching must be case-insensitive**, or `Food.JPG` fails a lookup for `Food.jpg`.
- **Spaces in filenames** — `Ask Questions.JPG`, `Conference schedule on display screen.png`. Encode Graph path segments properly.
- **A typo in a filename** — `SesssonFeedback.jpg` (three s's). Same class as `Grahics` and `hdmi-swiitcher`. Either rename in SharePoint now or carry the typo in code forever; flag it to the owner.
- **A stale asset** — `tv_with_ELDK26_EXPO.png` is a 2026 file in the 2027 folder. Flag; do not delete.
- **Enumerate both folders yourself.** The owner-supplied listings were truncated.

---

## 4. Repoint map

Left-hand folders no longer exist. Every surviving reference is a bug. **No migration — repoint the reference, ignore any old files.**

| Old | New |
|---|---|
| `ELDK/Graphics/Template` | `Event/Graphics/Template` |
| `ELDK/LogoPack` | `Event/LogoPack` (incl. `logopack.zip`) |
| `Speakers/Grahics-SoMe/*` | `Speakers/Graphics-SoMe/*` *(folder renamed in SharePoint — spelling now correct)* |
| `Speakers/SessionEvalsQR` | `Speakers/SessionEvaluations/QR` |
| `Speakers/SessionEvaluations` *(root)* | `Speakers/SessionEvaluations/Result` — the root is now a **container only**; nothing reads or writes files at that level |
| `Speakers/SessionEvaluationsFinal` | `Speakers/SessionEvaluations/Result` |
| `Sponsors/Booth Materials Zoho` | `Sponsors/Booth Collateral` |
| `Sponsor/Logo-Print` | `Sponsors/Logo/Print` *(note `Sponsor` → `Sponsors`)* |
| `Sponsor/Logo-SoMeBranding` | `Sponsors/Logo/Web` |
| `Sponsor/Logo-Zoho` | `Sponsors/Logo/Web` — both the external-event-system sync and the logo mail service repoint here |
| `Sponsor/Speaker Photo` | `Speakers/Photos`, with the new `speaker-photo-{speakerid}.png` naming |
| `Sponsor Upload` | `Sponsors/Logo/Web` or `Sponsors/Logo/Print` — determine per call site; if ambiguous, report rather than guess |

**Regression guard (required):** add a build-breaking test or Roslyn analyzer that fails if any left-hand string reappears, seeded from this table plus `Grahics`. This is the only thing preventing the same drift recurring.

---

## 5. Cross-cutting conventions

### 5.1 Versioning

Sponsor uploads (logo web, logo print, exhibitor wall) carry a version token in the filename.

- **Use the version format already implemented in code.** Do not redesign it.
- *Report:* whether all three upload types already use the same format. If not, raise it.
- "Latest" resolution must be one shared helper, prefix-scoped, not a per-call-site reimplementation.

### 5.2 Download-time renaming

Internal IDs must never reach a human.

- Speakers don't know the internal session ID. QR codes and evaluation results are renamed to the **session title** at download or attach time.
- Attendees likewise: presentation downloads show the session title — no ID, no version token.
- Renaming happens **at delivery only**. Stored filenames keep their ID-based, version-bearing form.

### 5.3 Filename sanitisation

One shared, deterministic sanitiser used by every writer and every download path.

| Characters | Action | Why |
|---|---|---|
| `\ / : * ? " < > \|` | → `-` | Windows-illegal; download fails outright |
| `#` | → `-` | **Highest risk.** Legal in SharePoint but is the URL fragment delimiter — truncates Graph paths and download links silently |
| `%` | → `-` | Breaks percent-decoding |
| `&` | → `-` | Breaks as an unescaped query separator |
| `!` `'` `,` `(` `)` `+` `=` | keep | Legal and safe |
| `æ ø å Æ Ø Å` and other Unicode letters | **keep** | Owner decision — preserve Danish characters |
| Leading/trailing spaces and dots | trim | SharePoint rejects trailing dots; trailing spaces vanish silently |
| Runs of spaces or `-` | collapse | Predictability |
| Total path length | cap | SharePoint URL length limit |

Session titles are the higher risk (`C# and .NET — what's new?` trips three rules at once). Unit-test against real Danish sponsor names and punctuation-heavy session titles.

**Stability matters for sponsor names:** if a sponsor name is corrected, its sanitised prefix shifts and version-matching stops finding earlier files. *Report:* whether sponsor uploads key on name or on sponsor ID underneath.

### 5.4 Generated-file naming

All CEH-generated filenames are **lowercase**, prefixed with the event short name (`eldk27`) from event settings.

### 5.5 Correlation keys

| Key | Applies to |
|---|---|
| `claim:{id}` | Travel reimbursement |
| `session:{id}` | Session graphics, presentations, evaluation QR, evaluation results |
| `track:{name}` | Speaker track graphics |
| `speaker:{id}` | Speaker photos |
| `sponsor:{name}` + `version:{version}` | Exhibitor wall, logo web, logo print |

Persist these as SharePoint column metadata where the library supports it, rather than relying on filename parsing.

### 5.6 Notification recipients

All recipients become **settings**, seeded in the ELDK fork and editable on the Organizer page (§6.1). Upstream ships the keys empty. No address appears as a literal in code.

| Setting | Value | Used by |
|---|---|---|
| `Notifications:VenueOperations` | `eldk2027@bellacenter.dk` | Weekly food files; weekly AV/TV rental file |
| `Notifications:ExhibitorWallReview` | `sb@homeworkers.dk`, `sb@expertslive.dk` | Exhibitor wall upload notifications (both recipients) |
| `Notifications:LogisticsOrganizer` | `mok@expertslive.dk` | Weekly furniture rental file |
| `Notifications:EventMailbox` | `info@expertslive.dk` | Session evaluation completion |
| *(hotel contacts)* | **already in CEH** | Change-triggered hotel files — use existing records, do not add settings |

`eldk2027@bellacenter.dk` carries the year and will change for ELDK28 — another reason it belongs in settings.

### 5.7 Reconciliation

Verify whether a reconcile service exists that detects files removed directly in SharePoint and updates CEH state, so the UI never offers a download that 404s. If it exists, confirm it covers the new folder layout. If not, report — do not build it unasked.

---

## 6. Features

Items marked **VERIFY** already exist. Items marked **CHANGE** modify existing behaviour. Items marked **BUILD** are new.

### 6.1 Organizer → SharePoint Paths page — **BUILD**

A new Organizer page registering every DocLibrary path in the product. This is the highest-value item here: it is the tool that would have prevented this entire situation, and it makes everything after it verifiable.

- Lists every §3 key: friendly name, area, relative path, resolved URL, direction, status, filename pattern, correlation key, reading and writing services.
- **Editable.** Paths are settings, persisted, changeable without redeploy. This is what makes CEH adoptable by another community.
- **Static filenames are settings too** — explicitly required for `EventGraphicsTemplate` (path plus both background filenames).
- Also surfaces: the **event short name**, and the §5.6 **notification recipients**.
- **"Test path"** per row — resolves the folder, reports item count and last-modified, surfaces the real error on failure.
- **"Test all"** — pass/fail dashboard; the pre-event health check.
- Change history: who changed what, when, from what to what.
- Save validation: no leading slash, no absolute URL, no `..`, no trailing slash, no illegal characters.
- Organizer role only. Seed from §3 so a fresh install starts correct.

### 6.2 Organizer → Sessions page — **CHANGE**

The Organizer Sessions page **already exists**. Extend it with per-session file state:

| Artifact | Shows |
|---|---|
| Final presentation | link, present/absent, uploaded date, version |
| Preview presentation | link, present/absent, uploaded date, version |
| Session evaluation QR | link, generated or not |
| Session evaluation result | link, available or not |
| Session SoMe graphic | link, variant (`.png` single / `.gif` multi), last generated |

State must be visibly **missing / present / stale** (stale = source changed after the derived file was generated). The point is that an organizer can see at a glance which sessions lack a deck before the deadline.

### 6.3 Organizer → Logistics page — **CHANGE**

The page exists but links elsewhere. Repoint it to the §3.5 generated files.

- Per artifact: filename, SharePoint link, last generated timestamp, row count or headline figure.
- **"Generate now"** per artifact, overwriting the existing file, alongside the daily automatic run.
- Show the last automatic run's status and any error.

### 6.4 Logistics calculation service — **BUILD**

Produces every §3.5 file daily, plus the mail schedule:

- **Weekly** → `eldk2027@bellacenter.dk`: six food files
- **Weekly** → `eldk2027@bellacenter.dk`: TV rental file
- **Weekly** → `mok@expertslive.dk`: furniture rental file
- **From 3 weeks before the event**, change-triggered → each hotel's contact in CEH: that hotel's file
- Idempotent: unchanged input produces an identical file and, for change-triggered mail, no send

### 6.5 Post-event surveys — **BUILD**

Three surveys: sponsor, attendee, speaker.

- **Use the existing survey engine** — the one already running the preliminary sessions survey for attendees. **Not** the SessionEvaluationSystem.
- *Report first:* how the preliminary survey's results currently reach CEH. The requirement that "results go into CEH" while summaries go to SharePoint may or may not be an existing integration.
- **Draft the question sets yourself** — placeholder quality is expected and the owner will refine them. Store them in **editable configuration or seed data, never hardcoded**.
- Summaries rebuilt **daily** from responses so far, written to the three §3.4 folders.
- **Close 1 month after the event ends** — for ELDK27 that is 10 March 2027. Implement as a **derived date** (event end + 1 month), not a literal.
- **No notification mail** for these three.

### 6.6 Session evaluation — post-event consolidation — **BUILD**

After the event, the session evaluation service additionally:

- Copies all PDF results to `Event/Evaluations/During the event/SessionEvaluations` — a copy, not a move; per-speaker results stay in `Speakers/SessionEvaluations/Result`
- Generates a **combined summary PDF** across all sessions into the same folder
- Notifies `info@expertslive.dk`

### 6.7 Sponsor Get Started wizard — logo consolidation — **CHANGE**

Reduce to **two** logo uploads:

| Remove | Becomes |
|---|---|
| "Logo for the Event System (Zoho) lead system (PNG ~200×160, max 5 MB)" | *gone — merged into Logo for Web* |
| "Logo for Social Media branding (PNG, max 5 MB)" | **"Logo for Web"** → `Sponsors/Logo/Web` |
| *(existing print upload)* | **"Logo for Print"** → `Sponsors/Logo/Print` |

Update help text and validation (Web = PNG only; Print = EPS/AI/PDF), and every downstream consumer that expected three logo files.

### 6.8 AI grounding — **VERIFY**

Pipeline, Community Helper instructions and OpenAI backend all exist. Reuse unchanged.

- Confirm the service enumerates `ExtraAIGroundingInfo`.
- Trigger: **daily scheduled + manual on-demand**. Put the on-demand control in the organizer menu alongside the §6.3 "Generate now" buttons.

### 6.9 Photo cleanup on deactivation — **BUILD**

When a speaker or volunteer becomes **inactive** — the inactive state is **already defined in CEH**; read it, do not invent one — their photo is removed from SharePoint.

- **Scope: photos only.** `Speakers/Photos` and `Volunteers/Photo`. QR codes, evaluation results, presentations and session graphics are **untouched** — they are event material tied to the session, not personal data tied to the person.
- **Timing: immediate** on status change.
- **This is the only service in CEH that deletes SharePoint files.** The app identity needs delete permission on those two folders — verify it has it, and report if not.
- **Ships with dry-run logging enabled by default**, logging what it would delete without deleting. Every other failure mode here is visible; this one loses data.

### 6.10 Travel reimbursement — claim locking — **CHANGE**

Once a claim is submitted with its files, it becomes **COMPLETED** and is frozen: no further uploads, no replacements, no re-claiming.

- Enforce **server-side**, not by hiding a button. A stale browser tab or a direct POST must fail identically.
- The speaker task shows a completed claim with its files listed **read-only**, not an upload control that errors on use.
- *Report:* what transitions a claim to COMPLETED — speaker submission or organizer approval — and **whether an organizer can reopen one**. If they cannot, a speaker who submits after 3 of 5 receipts is locked out with no recovery path. Flag this to the owner.

### 6.11 Sponsor SoMe promotion — **VERIFY + repoint**

The graphics and publishing services are built. Repoint them to `Sponsors/Graphics-SoMe/Sponsors` and `Sponsors/Graphics-SoMe/SponsorCategories`, then regenerate the files at the new paths. Reuse the existing LinkedIn org-page flow (OAuth, Key Vault token storage, Azure Function publisher). Organisation URN auto-tagging only — individual person tagging stays out of scope (partner-restricted API).

---

## 7. Phase 2 — Configuration refactor

Do this **before** applying path fixes, or you will fix the same bug twice.

1. Consolidate onto a single `DocLibraryOptions` — site, drive, root, and a dictionary of §3 keys.
2. Introduce `IDocLibraryPathResolver` as the **only** thing permitted to construct a SharePoint path. Every call site goes through it.
3. Migrate every literal found in Phase 0 to a resolver call.
4. Bind the settings store behind §6.1 so runtime edits take effect.
5. Upstream ships **keys with empty defaults**; the ELDK fork ships **values**. No ELDK path, address or event name hardcoded upstream.
6. **Fail fast at startup:** an unset or unresolvable ACTIVE key logs a clear error naming the key. Silent fallback to a default path is how this drift happened.

---

## 8. Acceptance criteria

- [ ] Zero occurrences of any §4 left-hand path, including `Grahics`
- [ ] Zero hardcoded SharePoint URLs, path literals or email addresses outside configuration and seed data
- [ ] Every §3 ACTIVE key resolves and passes "Test path"
- [ ] Every inventory row has a verdict; every STALE row has a linked fix commit
- [ ] Every *report* item in §3 and §6 is answered in the gap report
- [x] All speaker-photo writers and the Graphics service use `speaker-photo-{speakerid}.{ext}` — **§768.16**. *Two corrections to this line as written: there are **four** writers (the Sessionize import copy was the missed one — it wrote `Speakers/speaker-{id}.{ext}` under the RETIRED graphics root), and the extension follows the **source** (§768.9 D3) rather than being forced to `.png`.*
- [ ] Graphics service writes to `Speakers/Graphics-SoMe/*`; SoMe publishing reads the same
- [ ] All sponsor logo consumers read `Sponsors/Logo/Web`
- [ ] No file delivered to a speaker or attendee exposes an internal ID or version token
- [ ] Sanitiser unit-tested against Danish characters, `#`, `%`, `&` and Windows-illegal characters
- [ ] Static filename matching is case-insensitive (Venue folders)
- [ ] Regression guard in the build, failing on a deliberately reintroduced old path
- [ ] Photo cleanup defaults to dry-run and logs intended deletions
- [ ] Claim locking enforced server-side
- [ ] `requirements.md` holds only undelivered items; delivered items in `features.md` and `readme.md`

---

## 9. Execution order

1. **Phase 0** — inventory + gap report (read-only). **Stop.** Owner reviews sections C and F.
2. **Phase 2** — configuration refactor, no behaviour change.
3. **§4 repoints**, then **§5 naming conventions**.
4. **§6.1** SharePoint Paths page — makes everything after it verifiable.
5. **§6.2 / §6.3** organizer page extensions.
6. **§6.7** sponsor wizard consolidation — small, unblocks logo consumers.
7. **§6.11** SoMe sponsor promotion repoint + regeneration.
8. **§6.10** claim locking.
9. **§6.9** photo cleanup (dry-run first).
10. **§6.4** logistics calculation service.
11. **§6.5 / §6.6** surveys and evaluation consolidation.
12. **§6.8** AI grounding verification.

---

## 10. Deliverables

In `docs/audit/`:

- `sharepoint-reference-inventory.md` — §2.2
- `sharepoint-gap-report.md` — §2.3, including all *report* answers
- `sharepoint-remediation-log.md` — file, old value, new value, phase, commit
- `sharepoint-open-questions.md` — anything unresolvable without an owner decision. **Never guess.**

---

## 11. Known traps

Collected from the source data — each has already caused or nearly caused a silent failure:

1. **`Grahics` → `Graphics`** — the folder was misspelled in SharePoint and has been renamed. Any code written against the misspelling now breaks.
2. **`Sponsor` vs `Sponsors`** — the spreadsheet used the singular; SharePoint uses the plural throughout.
3. **`Event/Logistics/...`** — appears in the spreadsheet, does not exist in SharePoint. Paths are flat.
4. **`hdmi-swiitcher.jpeg`** — a real misspelled filename that must be matched exactly.
5. **`SesssonFeedback.jpg`** — another, in `Venue/Good to know`.
6. **Mixed extension casing** in the Venue folders — case-insensitive matching required.
7. **`#` in session titles** — silently truncates URLs. The single most likely cause of a future "the file disappeared" report.
8. **Track graphics keyed on track name** — a rename desynchronises writer and reader with no error.
9. **Session graphic extension switching** between `.png` and `.gif` on speaker-count change — may orphan the previous variant.

---

## 12. Resolved decisions

Every ambiguity in the source spreadsheet, with the owner's ruling. Recorded so the executing session doesn't reopen them.

| # | Question | Resolution |
|---|---|---|
| 1 | `Grahics` misspelling | Folder **renamed in SharePoint** to `Graphics-SoMe`. Use correct spelling. |
| 2 | Missing `Logistics` segment | **No `Logistics` segment.** Flat: `Event/Swag`, `Event/Hotel`, `Event/BC`. |
| 3 | `Venue/Wayfinding` URL | Correct path confirmed. Both Venue folders use **static filenames** linked from event-info pages. |
| 4 | `Speakers/SpeakerTemplate` | **Active, not deleted.** Gated to Community and Guest. **Sponsors get no template** — they use their own company template. |
| 5 | `Event/Evaluations/Sessions` | **Dropped.** Superseded by `During the event/SessionEvaluations`; the combined summary PDF moves there. |
| 6 | SpeakerTracks filename | **Read from code.** Also report whether a track ID exists to key on. |
| 7 | Preview vs Final | **Two upload tasks already exist.** Verify destinations; report the attendee precedence rule. |
| 8 | Session graphic `.png`/`.gif` | **Animated GIF, already built.** Repoint only. Report how the extension switch is handled. |
| 9 | SponsorCategories vs tracks | **Grouping already in code.** Repoint only; report which it keys on. |
| 10 | `Volunteers/Photo` | **Upload already exists** in volunteer sign-up. Verify and repoint. LinkedIn auto-download **dropped** (LinkedIn's API does not permit fetching other members' profile photos). |
| 11 | Filename casing | **All lowercase.** `eldk27` is the event short name, stored as an event setting. |
| 12 | Travel reimbursement | `claim-{claimid}-{seq}.{ext}`, **up to 5 files**. PDF and images, already built. |
| 13 | Version token format | **Use the existing format in code.** Report if the three upload types disagree. |
| 14 | Danish characters | **Preserve.** Sanitise `#`, `%`, `&` and Windows-illegal characters instead. |
| 15 | "Extend the sessions page" | Sessions page **exists** → extend it (§6.2). SharePoint Paths page is **new** (§6.1). |
| 16 | Survey content | **Draft questions** as placeholders, editable config. Close **1 month after event end** (10 Mar 2027). **No mail.** Use the **existing survey engine** (the preliminary attendee sessions survey one), not SessionEvaluationSystem. |
| 17 | AI grounding | **All exists** — pipeline, instructions, OpenAI backend. Reuse. Trigger daily + on-demand. |
| 18 | Notification recipients | Addresses per §5.6, stored as settings. Hotel contacts **already in CEH**. |
| 19 | Existing-file migration | **None.** Nothing is in production. Ignore old files and old paths entirely. |
| 20 | Environment | **Production directly.** No dev environment. Photo deletion keeps dry-run default. |
