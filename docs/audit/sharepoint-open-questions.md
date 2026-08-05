# SharePoint audit — open questions

Work-order §10 deliverable. **"Never guess."** Anything here is blocked on an owner decision;
everything answerable from code has been answered in the gap report instead.

**Status:** ✅ resolved · 🔴 blocking a phase · 🟠 needed before that item ships · ⚪ informational

---

## ✅ RESOLVED

| # | Question | Answer |
|---|---|---|
| Q1 | Is there a dev environment? Work-order rule 6 / decision #20 say no. | **Yes, now.** `General/DEVELOPMENT/EventHub` (2026-08-02). Supersedes both. |
| Q2 | Where do master-class graphics live, now that the folder is gone? | **Same folder as technical sessions** — `Speakers/Graphics-SoMe/Sessions`. Applied. |
| Q3 | Keep `MasterClassFolderPath` and `SessionsFolderPath` as separate keys? | *"i dont see a need to split"* — **collapse to one key in Phase 2**, delete the other. |
| Q4 | Does `SponsorCategories` key on track or on sponsor category? (work order §3.3) | **Tier** — `sponsor-tier:{slug}` from `SponsorPackage`. Folder name is right; the old spec text was wrong. *Answered from code, not asked.* |
| Q5 | How is the `.png`→`.gif` switch handled when speaker count changes? (§3.2) | The row is **upserted and repointed in place**; the old file is **orphaned** in SharePoint. §326af retire sweep is the existing cleanup. *Answered from code.* |

---

## 🔴 BLOCKING — needed before Phase 2 / the repoint

### ✅ Q6 — RESOLVED 2026-08-02: two roots, everything under them, fresh start

> *"i have given you 2 root paths including subfolders. use them. dont care about existing files and
> old paths. use the new paths and let the services regenerate fies. we do NOT migrate any old files
> nor focus on old logo files in old temporary paths. this is a fresh start"*

**PROD** `General/Events/ELDK 2027/EventHub` · **DEV** `General/DEVELOPMENT/EventHub`. The drive-root
`Graphics/` write root is **retired**; generated artwork moves inside the tree. Orphans at the old
root are explicitly not our problem — regenerate, don't move. Both sub-questions below are answered
by this: yes to (a), and yes the Sessions folder is deliberately both input and output.

🔑 **Carries a trap — see §768.7:** `InputHash` does not include the destination path, so repointing
the root alone rebuilds **nothing** and the log still reads `N already current`. The repoint commit
**must bump `DesignVersion`**, and verification must be *listing the new folder*, not reading counts.

<details><summary>Original question, kept for the record</summary>

#### Q6 — The graphics WRITE ROOT is outside the EventHub tree. Confirm the move.

Today `GraphicsSharePointOptions.RootFolderPath` defaults to **`Graphics`** — the **drive root**, a
sibling of `General/`, entirely outside `…/EventHub`. So §767 writes:

```
Graphics/Sessions/session-12.gif        ← generated session graphics
Graphics/SpeakerTracks/track-azure.gif  ← generated track GIFs
Graphics/Sponsors/sponsor-23.png
Graphics/SponsorCategories/sponsor-tier-diamond.gif
```

The work order's §3 places all of these **inside** the tree (`Speakers/Graphics-SoMe/…`,
`Sponsors/Graphics-SoMe/…`).

**This is not a repoint — it is merging a second write root into the main tree.** It changes
`SubfolderFor()`, every stored `GraphicAsset.SharePointPath` / `SharePointUrl`, and leaves the
current 21 generated files orphaned at the old root. Cheap now (decision #19: nothing is in
production), expensive later.

⚠️ **And it makes one folder both an input and an output.** `Speakers/Graphics-SoMe/Sessions` would
hold *your uploaded* session artwork **and** the generated `session-{id}.png|gif`. §3.2 marks that
folder "write + read", so it appears intended — and it is safe today, because the pull matches on
**title-slug** and generated files are named `session-12.gif`, which no title slugs to. **Confirm
that's what you want**, because it means the folder stops being "artwork I placed" and becomes mixed.

> **Decide:** (a) move writes into the tree as §3 says, accepting the orphans, or (b) keep a separate
> generated-output root and treat §3's write paths as read paths only.

</details>

### Q7 — DEV tree: how much of it, and who creates it?

`General/DEVELOPMENT/EventHub` is empty. To make DEV usable I need to create the §3 folder structure
there **and** create ~16 `Graphics__SharePoint__*` settings on the DEV hosts (the DEV **jobs host has
none at all** — not even `SiteUrl`/`Enabled`).

> **Decide:** mirror the **whole** §3 tree, or only the folders the graphics + evaluation features
> touch? And confirm I should create the DEV app settings myself.

🔒 Whatever the answer: **DEV must never point at `ELDK 2027`** — DEV runs write graphics and would
overwrite live artwork.

---

## 🟠 NEEDED BEFORE THAT ITEM SHIPS

### ✅ Q8 — Speaker photo filename — ANSWERED, both halves. CLOSED.
§3.2 specifies `speaker-photo-{speakerid}.png`. The 22 live files were a **mix of `.jpg` and `.png`**
(`speaker-photo-Andreas-Sobczyk-38.jpg`, `speaker-photo-Jesper-Nielsen-61.png`). Writing a JPEG under
a `.png` name is a real bug (wrong content type, some clients refuse it).

- **Extension (§768.9 D3):** *keep the SOURCE extension* — the `.png` in §3.2 is illustrative.
- **Name (§768.16, operator 2026-08-02):** *"use id only (not speaker name and id)"* — so
  `speaker-photo-{id}.{ext}`, and the writers align to the registry rather than the registry to the
  folder.

⚠️ **One assumption in the original note was WRONG and cost a fix.** *"Both old and new shapes
resolve"* was not true: `TryParse` required a dash after the prefix, so `speaker-photo-73` parsed as
**nothing**. Corrected in §768.16 — and the tolerance now runs the other way too, permanently: a
sponsor-uploaded photo is never re-archived, so legacy names must keep parsing for ever.

### Q9 — Travel-reimbursement claims: can an organizer REOPEN a completed one?
Work order §6.10 asks this explicitly and flags the consequence: a speaker who submits after
uploading 3 of 5 receipts is **locked out with no recovery path**.
> **Decide:** organizer can reopen (and where the control lives), or submission is final.

### Q10 — Merged sponsor logo: does the external event system have size constraints?
§6.7 removes the Zoho lead-system logo upload (*"PNG ~200×160, max 5 MB"*) and merges it into a
single **Logo for Web** PNG. If the Zoho consumer needs that specific geometry, one general-purpose
web logo may not satisfy it.
> **Decide:** is the merged PNG genuinely sufficient for the lead system, or does that consumer need
> a derived/resized variant?

### Q11 — Venue filename typo: rename in SharePoint, or carry it forever?
`Venue/Good to know` contains **`SesssonFeedback.jpg`** (three s's) — same class as `Grahics` and
`hdmi-swiitcher`. Work-order §3.7 says flag it rather than decide.
> **Decide:** rename it in SharePoint now (cheap, nothing depends on it yet) or match the typo in code.

---

## ⚪ INFORMATIONAL — flagged, no action taken

- **`tv_with_ELDK26_EXPO.png`** — a 2026 asset sitting in the 2027 `Venue` folder. §3.7 says flag,
  do not delete. Flagged; untouched.
- **§767 sponsor-logo TEST data is still half-present.** The 12 uploaded logos vanished with the
  `Logo-SoMeBranding` folder during your reorg, but the **generated output** (`Graphics/Sponsors`,
  `Graphics/SponsorCategories`) and **15 `GraphicAsset` rows** remain.
  `tools/revert-767-sponsor-logo-test.ps1` still has work to do. Awaiting your go-ahead.
- **"Upstream" is a generated mirror, not a second dev repo.** `C:\community-repos\community-event-hub`
  is produced by `tools/publish-to-public.ps1` from this fork (`Publish eldk27-v1.3.0 from
  eldk-community-event-hub@…`), and **`config/*` is denylisted** — public consumers get
  `config-examples/`. So work-order §7.5 (*"upstream ships keys with empty defaults; the fork ships
  values"*) is **already true structurally**; the rule to enforce is the *naming* one (no
  `SharePoint`/`ELDK` strings in mirrored **code**), not a second repo to edit.
  > Minor: should `config-examples/` gain a sanitized template carrying the new `DocLibrary:Paths:*`
  > keys, so a fresh adopter starts from a complete file?
