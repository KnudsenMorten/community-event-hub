# SharePoint remediation log

Work-order §10 deliverable. One row per change: what, from, to, phase, commit.

> ⚠️ **App-setting changes leave no git diff.** That is precisely the defect the audit found
> (`docs/SHAREPOINT-AUDIT.md` §4.4), so every Azure-side change is logged here by hand until §6.1's
> Paths page makes them reviewable properly.

---

## 2026-08-02 — EMERGENCY REPOINT during Phase 0 (owner-approved)

**Why this breaks the work order's own rule.** §0 rule 1 says Phase 0 is read-only and nothing
changes during discovery. That rule was written before either of us knew the reorg had **already
happened**: the §767 graphics sweep went inert between **10:25 and 10:40 UTC** on 2026-08-02, and
stayed dark. The owner was shown the evidence and chose to restore immediately rather than leave a
live feature down for the length of the audit. **Recorded here so the inventory stays honest — these
four values were NOT in their original state when the Phase 0 inventory was taken.**

### Changed — 3 settings × 3 hosts

Hosts: `eldk27hub-fn-prodpdrq` · `eldk27hub-web-prodpdrq` (production slot) · same (**staging slot**).
🔒 Both web slots, because app settings **swap with the slot** — setting only one guarantees the
next swap silently reverts this.

| Setting | From (dead) | To (verified live) |
|---|---|---|
| `Graphics__SharePoint__TemplateFolderPath` | `…/EventHub/ELDK/Graphics/Template` | `…/EventHub/Event/Graphics/Template` |
| `Graphics__SharePoint__SessionsFolderPath` | `…/Speakers/Grahics-SoMe/Sessions` | `…/Speakers/Graphics-SoMe/Sessions` |
| `Graphics__SharePoint__SponsorLogosFolderPath` | `…/Sponsors/Logo-SoMeBranding` | `…/Sponsors/Logo/Web` |

Root for all: `General/Events/ELDK 2027/EventHub`. Each destination was **enumerated before the
change** — `Event/Graphics/Template` holds `Template.JPG` + `LOGO EXPERTS LIVE denmark WHITE_no
shadow.png`, so the sweep has what it needs. `Sponsors/Logo/Web` and
`Speakers/Graphics-SoMe/Sessions` are **empty**, which is correct-and-inert, not broken.

### 🔴 NOT changed — deliberately, because it would have been a guess

| Setting | Current (dead) | Why left alone |
|---|---|---|
| `Graphics__SharePoint__MasterClassFolderPath` | `…/Speakers/Grahics-SoMe/MasterClass` | **The work order has no MasterClass entry.** §3.2 lists only `Sessions` and `SpeakerTracks` under `Speakers/Graphics-SoMe`, and the live folder confirms it — there is no `MasterClass` folder any more. §4's repoint map does not mention it either. Pointing it at `Sessions` would be **inventing a path** (§0 rule 2). ⇒ **Open question for the owner** — see `sharepoint-open-questions.md`. |

**Consequence while it stays dead:** the master-class branch of `PullSessionGraphicsAsync` matches
nothing, so operator-uploaded master-class artwork does not reach the hub. It was equally dead before
this change; nothing regressed.

### Not touched

`TracksFolderPath` / `TrackGraphicsFolderPath` remain **unset**. That is deliberate and long-standing
(§767 Round 9: the per-speaker track graphic is dropped, and the folder must stay unconfigured so the
legacy §158 pull cannot resurrect). Do not "fix" them into a value.

### ✅ Verified restored — 11:32:33 UTC

```
§767 bundles: speaker-photo folder '…/Speakers/Photos' indexed 22 by participant id, 22 by name.
§767 bundles: 0 track GIF(s) rebuilt, 7 already current, 0 session graphic(s) rebuilt,
              10 already current, 0 sponsor grouping GIF(s) rebuilt, 0 sponsor graphic(s) rebuilt,
              0 track(s) + 2 session(s) waiting for a photo, 0 session(s) left to the operator's
              own upload (event 1).
```

The `no template found … sweep inert` line is gone; the template resolves, the photo folder indexes
22, and the existing 7 track + 10 session graphics are recognised as current rather than rebuilt —
so the hash guard survived the repoint and nothing was needlessly re-rendered. **Total outage
10:40 → 11:32 UTC.**

⚠️ `pulled 0 session-matched (12 had no file)` is now the CORRECT answer, not a fault:
`Speakers/Graphics-SoMe/Sessions` is empty, so there is no operator-uploaded artwork to pull. It read
the same before the fix for the opposite reason (the folder did not exist) — **the number did not
change, the meaning did.** A count alone would not have told you the repoint worked.

**Commit:** settings are Azure-side; the surrounding capture is `768 phase 0 start` on
`feat/sharepoint-doclibrary-audit`.

---

## §768.16 — speaker photo: the name drops, the id stays (2026-08-02)

Operator: *"use id only (not speaker name and id)"*. Closes gap **E3** and open question **Q8**.

| | |
|---|---|
| Writers moved | `SpeakerPhotoArchiveService`, `SponsorSessionFormService`, `SessionizeImportService` (the fourth writer, found here), all through `SpeakerPhotoFileName.Build` |
| Reader moved | `SoMeBundleBuildService.ListPhotosAsync` — id-only file WINS a collision; archive files no longer claim a NAME key |
| Deleted | both `Sanitize` helpers — a speaker's name is not part of any file name now |
| Repointed | the import copy: `Graphics/Speakers/speaker-{id}.{ext}` (retired root, `StoreAsync`) → `DocLibraryPaths.SpeakerPhotos` (`UploadToFolderAsync`, drive-relative) |
| Tests | 4258 green (was 4236). New: build/round-trip/`IsCurrentConvention`, re-archive-once, id-only-wins, and where the import copy lands |

**Two things to expect in the logs after deploy, neither of them a fault:**

1. `speaker-photo folder … indexed 22 by participant id, 0 by operator-dropped name` — the name index
   now holds ONLY human-dropped §165 files, and there are none. It read `22 by name` before because
   every archive file claimed both keys. **Watch the id count.**
2. One archive run re-fetches the ~22 community/guest photos ONCE and rewrites them as
   `speaker-photo-{id}.{ext}`; the run after that is back to `skipped`. Sponsor-uploaded photos are
   NOT re-fetched (§764.1) and keep their legacy names until re-uploaded — by design, which is why
   the parser stays tolerant permanently.

The old files are left in place as orphans (§768.7: *"we do NOT migrate any old files"*); §6.9's
photo cleanup is their disposal route. Nothing is deleted by this change.
