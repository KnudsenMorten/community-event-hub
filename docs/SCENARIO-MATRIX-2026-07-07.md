# §253 Lifecycle scenario matrix — every role × every flow, verified against code (2026-07-07)

> ## ✅ RESOLUTION (2026-07-07 late / re-verified 2026-07-08) — G1–G16 FIXED, merged + deployed
>
> Operator approved autonomous fixing ("i need response and fixes autonomously", §255). PR #369 =
> welcome-pipeline smoke probe (§255 test family 5); combined gate 2,558 tests + 69 Pester, ~34 new
> regression tests. Every 🔴 gap below (G1–G16) was fixed the same night — **PR #370** (G9–G16: reminder IsActive
> gates, travel entitlement, role-change reconciliation via `RoleChangeTaskReconciler`, TasksTable
> active-assignee default, attendee-export MirrorState filter, public-agenda gate, hard-delete FK
> safety, re-purchase re-engagement) and **PR #371** (G1–G8: the central
> `ParticipantDeactivationService` cascade behind all four deactivation entry points, IsActive truth
> across every hotel/party/lunch/swag/dinner/volunteer count and vendor export, sponsor
> `DeactivatedByOrganizerAt` tombstone + `SponsorInfo.Status=Withdrawn` company withdrawal).
> **Independently re-verified against main on 2026-07-08** (five parallel code audits): all 16
> confirmed present with file:line evidence. Residuals found by that re-verification were closed
> the same day (branch `fix/jul08-matrix-residuals`):
>   - **G7 completion** — the cascade now SEEDS backfill drafts for the vacated shifts into the
>     acting organizer's allocation queue (`SeedBackfillForTasksAsync`), instead of the manual-only
>     re-plan button;
>   - **G8d** — refunded/cancelled Woo orders now surface as organizer action-queue items
>     (`sponsor-order-refunded`, one per order id, forever-deduped);
>   - **G17 partials** — sponsor sync normalizes emails at write + Sessionize import matches
>     case-insensitively; `WizardStepTaskSeeder` prunes stale override-excluded step tasks;
>     draft→commit re-checks IsActive (`SkippedInactive`) and AddDraft refuses inactive volunteers;
>     re-activation reports the dormant dinner/lunch/swag rows (and still-held MC seats) that
>     silently rejoin the counts; the stale "14/7/3/1" doc string in
>     `config/speaker-deadlines.eldk27.json` now says due-day-only (§81).
>
> **Still open by design/decision** (operator to decide, unchanged behaviour):
>   - volunteer queue has no Rejected state / self-withdraw for pending applicants (R1 F1 🟡);
>   - sponsor sync role flags stay SET-only (a revoked CM signer keeps `IsSigner` until an
>     organizer clears it) and a contact listed under two CM companies flaps last-synced-wins;
>   - organizer deactivation of an attendee still leaves their confirmed MC seat held (release
>     is the §216 ticket-cancel path's job; the reactivation report now at least surfaces it);
>   - `ReminderJob` still relies on page-load reconciliation rather than running
>     `FormTaskReconciler` itself (one theoretical spurious due-day mail, G17 note).
>
> The verdict tables below are the ORIGINAL 2026-07-07 findings, kept as the audit record —
> read every 🔴 through the resolution above.

> Traced against `main` @ `b990291`. Method: three parallel code audits (deactivation-cascade +
> headcount inventory; task/reminder/entitlement machinery; entry variants + ticket lifecycle),
> with every load-bearing 🔴 claim re-verified by direct read of the cited lines.
> Verdicts: ✅ covered · 🟡 partial/indirect · 🔴 GAP. All paths relative to repo root.
>
> **Headline finding: there is NO central deactivation cascade.** Every organizer-side
> deactivation is a pure `IsActive=false` flag flip that keeps hotel/dinner/lunch/swag/party/shift
> rows intact, and MOST logistics counts and vendor exports never filter on `IsActive` — so the
> operator's fear (a) is confirmed on almost every surface. The one well-engineered cascade is the
> §216 attendee-ticket path (`AttendeeTicketSyncService`), which is the pattern the fix should copy.
> Fear (b) (irrelevant prompts) is *mostly* handled by `OrderEntitlements` gating — with one direct
> hit: the **travel-reimbursement task is not entitlement-gated**, so a sponsor-funded speaker gets it.

---

## A. Shared lifecycle mechanics (applies to every role)

### A1. Deactivation entry points — what each actually does

| # | Path | Evidence | Cascades anything? |
|---|---|---|---|
| D1 | Organizer grid Active toggle | `src/CommunityHub/Pages/Organizer/Participants.cshtml.cs:158-183` (`target.IsActive = false;` L175) | 🔴 Nothing. Doesn't even set `LifecycleState` |
| D2 | Organizer soft "Delete" | `src/CommunityHub.Core/Organizer/ParticipantDeletionService.cs:92-109` (doc L89-90: "Keeps every dependent row intact.") | 🔴 Nothing, by documented design |
| D3 | Bulk deactivate | `src/CommunityHub.Core/Organizer/ParticipantBulkOperationService.cs:85` | 🔴 Nothing |
| D4 | DataGrid inline row save | `src/CommunityHub/Pages/Organizer/DataGrid.cshtml.cs:100` | 🔴 Nothing; also skips `LifecycleState` → diverges from D2 |
| D5 | §216 attendee ticket cancel | `src/CommunityHub.Core/Reminders/AttendeeTicketSyncService.cs:471-499` (`SoftCancelAttendeeAsync`) + `:520-563` (`ReconcileParticipantLoginsAsync`) | ✅ **The only real cascade**: MC seat released + waitlist promoted (L476-482), party RSVP cancelled (`CancelPartyRsvpAsync:581-599` → `Attending=false; HeadCount=null`), login locked, §243 mail |
| D6 | 1-day access sweep | `AttendeeWelcomeProvisioningService.cs:82-120` (`ReconcileOneDayAccessAsync`) | 🟡 Login-gate only (by design; 1-day holders have no logistics rows) |
| D7 | Volunteer "withdraw" | `src/CommunityHub.Core/Volunteers/VolunteerShiftService.cs:108-110` — withdraws a *decline decision*, not the volunteer | 🔴 No self-service volunteer-withdrawal path exists at all |
| D8 | Hard delete | `ParticipantDeletionService.cs:119-144` + `CleanSafeDependentsAsync:206-238` | 🟡 Cleans HotelBookings/Swag/Dinner/Lunch/Pins — but **not PartyRsvp** (`DeleteBehavior.NoAction`, `CommunityHubDbContext.cs:570-573`) and **not volunteer availability rows** (Restrict FKs, `CommunityHubDbContext.cs:1737-1756`) → ghost RSVP row or SQL FK error |

### A2. Two competing "active" definitions

- Canonical: `src/CommunityHub.Core/Domain/ParticipantActivation.cs:29-30` — `IsActive && LifecycleState == Active`.
- D1/D4 set only the flag; D2 sets both. Any filter using `LifecycleState` alone (e.g. `OnboardingService.cs:361-381`) **misses** D1/D4 drop-outs. Any fix must funnel all paths through one service and one filter expression.

### A3. Login vs lifecycle — ✅ solid everywhere

| Surface | Evidence | Verdict |
|---|---|---|
| PIN request/verify requires active | `PinLoginService.cs:82-93`, `PinIdentityProvider.cs:59-62` | ✅ |
| Magic link of deactivated person fails | `EmailMagicLinkService.ResolveAsync:160-166` ("Account is inactive.") + `Pages/Go.cshtml.cs:66` | ✅ |
| Live session of just-deactivated user | `src/CommunityHub/Program.cs:303-357` — per-request revalidation, ≤5-min cache, `RejectPrincipal()`+`SignOutAsync` | ✅ (≤5-min window) |
| Re-activation resumes with the OLD magic link | `EmailMagicLinkService.GetOrCreateTokenAsync:81-96`; grant never revoked by deactivation; organizer `RevokeAsync/RotateAsync:264-301` for deliberate invalidation | ✅ by design |

---

## B. MASTER drop-out surface inventory (family 4) — every headcount / export / report query

This is the exhaustive answer to "does ANY code exclude on deactivation, or do counts silently
include ghosts?". Role sections below reference these IDs.

### B1. Hotel

| ID | Query | Evidence | IsActive? | Verdict |
|---|---|---|---|---|
| H1 | Room-block math (assigned/needing/confirmed/Remaining/Over/PlanLooksOk on `/Organizer/HotelRoomBlocks`) | `HotelRoomBlockService.BuildAsync` — `src/CommunityHub.Core/Organizer/HotelRoomBlockService.cs:145-175`: `_db.HotelBookings.Where(hb => hb.EventId == eventId)` + `_db.Participants.Where(p => p.EventId == eventId)` | **No** | 🔴 entire room-block plan counts ghosts, both in `assigned` and `UnassignedNeedingRoom` |
| H2 | Per-hotel rosters + "(N people, M confirmed)" badges | `HotelManagementService.GroupByHotelAsync:260-308` — filters `Role != Sponsor` (L275) but never IsActive | **No** | 🔴 |
| H3 | Confirmed hotel calendar invites | `HotelManagementService.ListReserversForInviteAsync:152-180` + `Pages/Organizer/Hotels.cshtml.cs:236-303` | **No** | 🔴 **emails CONFIRMED invites to dropped-out people** and stamps their booking Confirmed |
| H4 | Command Center hotel tile | `CommandCenterService.cs:335-336` — `CountAsync(h => h.EventId == eventId && h.NeedsRoom)`, no Participants join | **No** | 🔴 |
| H5 | Rooming-list xlsx (the list the hotel receives) | `Pages/Organizer/DataGrid.cshtml.cs:177-186` — `.Join(_db.Participants,…).Where(x => x.IsActive)` | Yes | ✅ the pattern to copy |
| H6 | Hotel completion stats | `ReportingService.cs:95-126` (active-audience intersection) | Yes | ✅ |

### B2. Appreciation dinner

| ID | Query | Evidence | IsActive? | Verdict |
|---|---|---|---|---|
| DN1 | Command Center dinner headcount (`dinner = rows.Count + rows.Sum(PlusOneCount)`) | `CommandCenterService.cs:343-347` — `_db.DinnerSignups.Where(d => d.EventId == eventId && d.Attending)` | **No** | 🔴 sums ghosts' plus-ones too |
| DN2 | Dinner completion stats | `ReportingService.cs:127-130` | Yes | ✅ |
| DN3 | Onboarding dinner rollup | `OnboardingService.cs:361-381` — filters `LifecycleState` only | Partial | 🟡 misses D1/D4 drop-outs (§A2) |

### B3. Lunch

| ID | Query | Evidence | IsActive? | Verdict |
|---|---|---|---|---|
| L1 | `/Organizer/Lunch` counts (TotalResponses, EarlySetupDay, SetupDay, DeclaredPreDay) | `Pages/Organizer/Lunch.cshtml.cs:60-67,93-97` — join to Participants without IsActive | **No** | 🔴 (auto pre-day at L80-81 uses `ParticipantActivation.IsActiveExpr` ✅ → `PreDayCount` is mixed 🟡) |
| L2 | Lunch headcount + caterer run-sheet/CSV | `OrganizerExportsService.cs:141-143` (headcount), `:158-159` (people list) | **No** | 🔴 **caterer order includes ghosts** |
| L3 | Dashboard lunch tiles | `Pages/Organizer/Dashboard.cshtml.cs:260-266` | **No** | 🔴 |
| L4 | Command Center lunch tile | `CommandCenterService.cs:340-341` | **No** | 🔴 |

### B4. Swag

| ID | Query | Evidence | IsActive? | Verdict |
|---|---|---|---|---|
| S1 | `/Organizer/Swag` overview aggregates | `Pages/Organizer/Swag.cshtml.cs:227-256` | **No** (zero `IsActive` in the whole file — verified) | 🔴 |
| S2 | Vendor xlsx: Polo Order / Jacket / Award / Credly / Raw sheets | `Swag.cshtml.cs:56-66,77-80,106-109,137-138,166-167,197` | **No** | 🔴 **polo/jacket size orders include drop-outs → over-ordering money** |
| S3 | Command Center swag tile | `CommandCenterService.cs:337-339` | **No** | 🔴 |

### B5. Party

| ID | Query | Evidence | IsActive? | Verdict |
|---|---|---|---|---|
| P1 | Party counts ("attending — food count") + list | `PartyRsvpService.GetAllAsync:233-235` + `CountsAsync:240-243` — `.Where(r => r.EventId == eventId)`, **no Participants join** (verified); consumed by `Pages/Organizer/PartyRsvps.cshtml.cs:42-43` | **No** | 🔴 |
| P2 | Party CSV/XLSX export (incl. sponsor group HeadCount column) | `PartyRsvps.cshtml.cs:67-76` | **No** | 🔴 |
| P3 | **Root asymmetry**: `CancelPartyRsvpAsync` is called ONLY from ticket sync (`AttendeeTicketSyncService.cs:229,419,492`) | grep-verified — no organizer-deactivation caller | — | 🔴 a dropped speaker/volunteer/sponsor (or a withdrawn company's group HeadCount) counts toward the venue food order forever |
| P4 | Company group reservation lookup | `PartyRsvpService.FindCompanyReservationAsync:154-161` — joins Participants without IsActive | **No** | 🟡 a deactivated contact's group answer stays canonical |
| P5 | Party biweekly reminder | `AttendeePartyReminderBuilder.cs:115` — `if (!t.Participant.IsActive) continue;` | Yes | ✅ model citizen |
| P6 | Hard delete leaves PartyRsvp | §A1/D8 (`NoAction` FK, not probed, not cleaned) | — | 🔴 ghost `Attending=true` row or SQL error |

### B6. Volunteer shifts / coverage — systemic

`p.IsActive` appears in `Core/Volunteers/` only on *candidate pools*, never on *assigned counts*:

| ID | Query | Evidence | Verdict |
|---|---|---|---|
| V1 | Coverage per task (`GroupBy(TaskId).Count()`) | `VolunteerAllocationService.LoadCoverageAsync:111-115,141-142`; same shape `OrganizerAllocationService.cs:78-82,108-109`, `AllocationScenarioService.cs:226-229` | 🔴 a task held by 2 withdrawn volunteers shows green/covered (no DecisionStatus filter either — declined shifts also count) |
| V2 | Auto-assign gap math | `AvailabilityAutoAssignEngine.cs:167-170` (`gap = ResourcesNeeded - have`); candidate pool `:148-151` DOES filter IsActive | 🔴 ghosts shrink the gap → the engine under-proposes replacements for exactly the shifts the drop-out vacated |
| V3 | Designed drop-out backfill | `VolunteerAllocationService.SeedDropoutBackfillAsync:339-345` | 🔴 counts *other* ghosts in `assignedNow` AND is organizer-manual — nothing invokes it on deactivation |
| V4 | "assigned" rollups / attention tile | `OrganizerOverviewService.cs:245-259` (`AssignmentCount = t.Assignments.Count`), `CommandCenterService.cs:407-411` | 🔴 ghost-held tasks vanish from the unassigned-attention tile |
| V5 | Day-of rota export | `OrganizerExportsService.cs:256-258` — filters only `Task.Status != Cancelled` | 🔴 withdrawn volunteers print on the printed rota |
| V6 | Draft→commit + plan import | `VolunteerAllocationService.AddDraftAsync:167-176`/`CommitAsync:263`, `VolunteerPlanImportService.cs:60-62` | 🟡 never re-check IsActive; can (re)link an inactive volunteer |

### B7. Tasks & reminder builders

| ID | Surface | Evidence | Verdict |
|---|---|---|---|
| T1 | **Due-date task reminders** (ALL dated tasks: speaker deck uploads, travel, hotel/dinner/swag/lunch speakerdl, crew party) | `TaskReminderBuilder.cs:114-118` — filters only `State != Done && DueDate != null && AssignedParticipantId != null`; **zero `IsActive` in the file** (verified) | 🔴 deactivated people still get due-day mail (mitigation: fires once per task, `task:{id}:due` L188) |
| T2 | Master-Class biweekly nag | `AttendeeMasterClassReminderBuilder.cs:86-95` — §234 mirror gate only; **the IsActive line its party sibling has is missing** (verified) | 🔴 an organizer-deactivated (not ticket-cancelled) attendee is nagged biweekly forever |
| T3 | Party biweekly / Get-Started digest / speaker question digest | `AttendeePartyReminderBuilder.cs:115`; `GetStartedDigestBuilder.cs:106`; `SpeakerQuestionDigestService.cs:112` | ✅ all filter IsActive |
| T4 | Step-reset reminder | `OnboardingStepResetEmailService.cs:44-68` + transport `ParticipantEmailService.cs:60-104` | 🔴 no participant-state check end-to-end |
| T5 | Sponsor reminder audience | `SponsorRecipientResolver.cs:120` — `.Where(p => p.IsActive)` | ✅ |
| T6 | Deactivation closes open tasks? | D1-D4 touch nothing else (§A1) | 🔴 tasks stay Open |
| T7 | Organizer tasks grid + export | `Pages/Organizer/TasksTable.cshtml.cs:172-174` — no assignee-IsActive filter (only the assignee *dropdown* L191 filters) | 🔴 drop-outs' open tasks pollute the grid + CSV/XLSX forever |
| T8 | Job-side seeders | `PartyTaskSeeder.cs:76`, `AttendeeMasterClassTaskSeeder.cs:57`, `SpeakerDeadlineSeeder.cs:114-117` — all filter IsActive | ✅ no NEW tasks for inactive people |
| T9 | Speakerdl orphan prune reach | `SpeakerDeadlineSeeder.cs:210-224` prune loop iterates only ACTIVE speakers | 🔴 a deactivated/ex-speaker's dated tasks are never pruned → feeds T1 |

### B8. Other exports / public surfaces

| ID | Surface | Evidence | Verdict |
|---|---|---|---|
| X1 | Attendee list/CSV export | `OrganizerExportsService.cs:98-99` — `.Where(a => a.EventId == eventId)`, **no `MirrorState == Active` filter** | 🔴 soft-cancelled tickets appear on the on-site attendee list |
| X2 | Badge export | `OrganizerExportsService.cs:311-312` — `p.IsActive && !p.IsTestUser` | ✅ |
| X3 | Organizer Attendees page | `Pages/Organizer/Attendees.cshtml.cs:167,188` — `MirrorState == Active` | ✅ |
| X4 | Public agenda | `PublicAgendaService.cs:100-102` — `s.SessionSpeakers.Select(ss => ss.Participant.FullName)`, no IsActive/publish gate | 🔴 deactivated speakers render on the public agenda |
| X5 | Public speakers page | `PublicSpeakersService.cs:115-119` — gates `sp.Participant.IsActive` | ✅ |
| X6 | Public session detail | `PublicSessionsService.cs:181-183` — inactive co-speaker as plain text (documented design) | 🟡 |
| X7 | Public sponsors page | `PublicSponsorsService` groups all `SponsorInfos` by tier, no status gate | 🟡 withdrawn company's logo stays until the facts row is deleted |

---

## R1. Volunteer

### Family 1 — entry variants

| Scenario | Behaviour | Evidence | Verdict |
|---|---|---|---|
| New signup via `/volunteer/signup` | pending row `IsActive=false, LifecycleState=Inactive, QueueSource=VolunteerInterestForm` + per-day availability + lead notified | `Pages/Volunteer/Signup.cshtml.cs:216-218` | ✅ |
| Duplicate signup, email already ACTIVE | rejected: "already registered for this event" | `Signup.cshtml.cs:194` | ✅ |
| Duplicate signup, email is another role | rejected: "already on file… contact the organizer team" | `Signup.cshtml.cs:199` | ✅ |
| Pending applicant re-submits | same row refreshed, no second pending row | `Signup.cshtml.cs:202-205` | ✅ |
| Organizer manual add of existing email | rejected ("already exists in this edition"); email-change collisions also rejected | `Pages/Organizer/EditParticipant.cshtml.cs:116-122,164-172` | ✅ |
| Queue rejection / self-withdraw before activation | no Rejected state, no self-withdraw endpoint — row stays Inactive forever (or organizer deletes) | `PreselectionQueueService.cs:29-30`; `Pages/Organizer/PreselectionQueue.cshtml.cs:114-150` | 🟡 |
| Queue hard-delete of an applicant | `VolunteerDayAvailability`/`VolunteerAvailability` have Restrict FKs but are neither in the blocker probe nor the safe-cleaner → unhandled `DbUpdateException` (page doesn't catch) | `CommunityHubDbContext.cs:1737-1756`; `ParticipantDeletionService.cs:171-238`; `PreselectionQueue.cshtml.cs:122-138` | 🔴 |

### Family 2 — happy path

| Scenario | Behaviour | Evidence | Verdict |
|---|---|---|---|
| Approve → welcome → wizard → all forms → all tasks Done, reminders stop, headcounts correct | 3-state queue advance flips IsActive+onboarding once; `welcome-volunteer` ledgered; wizard steps mirror to tasks; two-way reconcile; §232 stops on answer | `PreselectionQueueService.AdvanceAsync:123-138`; `WizardStepTaskSeeder.cs:172-186`; `FormTaskReconciler.cs:73-93`; §232 evidence below | ✅ |

### Family 3 — partial engagement

| Scenario | Behaviour | Evidence | Verdict |
|---|---|---|---|
| First reminder ≥14d after welcome, then biweekly, one per window | `daysSince < IntervalDays` guard; `…:wk{windowIndex}` occasion keys + `SentReminder` UNIQUE ledger | `AttendeePartyReminderBuilder.cs:133-136,158`; `GetStartedDigestBuilder.cs:126-129,159`; `ReminderEngine.cs:48-52,99-121` | ✅ |
| Answering stops the nag immediately | builders filter `State != Done` + live-data belt-and-braces; digest stops at `openKeys.Count == 0` | `AttendeePartyReminderBuilder.cs:66,116`; `GetStartedDigestBuilder.cs:136` | ✅ |
| Dated logistics tasks | due-DAY only, once ever (§81) — not §232 | `TaskReminderBuilder.cs:140-145,188` | ✅ |

### Family 4 — DROP-OUT (organizer sets IsActive=false / volunteer stops showing up)

| Surface | What happens | Evidence | Verdict |
|---|---|---|---|
| (i) Hotel booking + room-block counts | booking row survives; block math, rosters, tile all count them | B1: H1, H2, H4 | 🔴 |
| (i-b) Hotel confirmed-invite mail | drop-out still receives the CONFIRMED calendar invite | B1: H3 | 🔴 |
| (ii) Dinner signup + headcount | row survives; Command-Center headcount incl. their plus-ones | B2: DN1 | 🔴 |
| (iii) Lunch signup + overview counts + caterer export | rows survive; all counts/exports include them | B3: L1-L4 | 🔴 |
| (iv) Swag prefs + vendor export | rows survive; polo/jacket order sheets include them | B4: S1-S3 | 🔴 |
| (v) Party RSVP + headcount | RSVP survives (`CancelPartyRsvpAsync` never called from D1-D4); counts have no Participants join | B5: P1-P3 | 🔴 |
| (vi) Open tasks | stay Open; organizer TasksTable + export unfiltered | B7: T6, T7 | 🔴 |
| (vi-b) Reminders — due-date track | still mailed on due day | B7: T1 | 🔴 |
| (vi-c) Reminders — biweekly party/digest | stop (IsActive-gated) | B7: T3 | ✅ |
| (vii) Shift assignments + coverage math | assignments survive; coverage green; auto-assign never backfills; rota prints them | B6: V1-V5 | 🔴 |
| (viii) Login / magic link | blocked ≤5 min | §A3 | ✅ |
| Availability rows | persist forever; never cleaned on deactivation | agent-verified, no cleanup path | 🟡 |

### Family 5 — re-activation

| Scenario | Behaviour | Evidence | Verdict |
|---|---|---|---|
| Toggle back Active | every logistics row silently resurrects (never deleted); no duplication — unique index `(TaskId,ParticipantId)`, SourceKey-keyed task upserts, `SentReminders` dedup; old magic link works again | `CommunityHubDbContextModelSnapshot.cs:4739-4740`; `PartyTaskSeeder.cs:36,100`; `FormTaskReconciler.cs:73-93`; §A3 | ✅ (resurrection is silent — organizer gets no "their old hotel booking is live again" signal) |
| Bulk reactivate | bypasses `ParticipantActivationService` — no onboarding trigger | `ParticipantBulkOperationService` (ReactivateAsync) | 🟡 |

### Family 6 — role change (volunteer→speaker)

| Scenario | Behaviour | Evidence | Verdict |
|---|---|---|---|
| Organizer changes Role | bare field write, zero task reconciliation; `availability:{pid}` task orphaned Open (undated → no mail, but pollutes My Tasks + TasksTable); new role's steps seed alongside | `EditParticipant.cshtml.cs:178-181`; `WizardStepTaskSeeder.cs:79-84,133`; `WizardStepTaskKeys.cs:22-26` (keys not role-prefixed) | 🔴 |

### Family 7 — entitlements

| Scenario | Behaviour | Evidence | Verdict |
|---|---|---|---|
| Volunteer set = Polo, Swag, Hotel, Dinner, LunchMainDay; wizard steps gated on it | `OrderEntitlements.cs:111-117` (verified); `RoleWizardService.cs:91,115-147` | ✅ |
| Pre-day lunch checkbox | role-gated (`role is Speaker or Sponsor or Volunteer`), not day-entitlement-gated — volunteer can declare a pre-day lunch they're not entitled to | `LunchFormService.cs:148-150` | 🟡 |

**R1 tally: ✅ 13 · 🟡 4 · 🔴 11**

---

## R2. Speaker — ELDK-supported

### Family 1 — entry variants

| Scenario | Behaviour | Evidence | Verdict |
|---|---|---|---|
| Sessionize import, new speaker with email | direct import; hourly DELTA; scheduled pull never emails | `SessionizeImportService.cs` (delta doc :62-65); ROLE-FLOWS §3 | ✅ |
| Re-import of existing speaker | matched by email within edition; updates FullName only; Role/IsActive never touched; speaker-edited bio fields protected (`FillIfUntouched`) | `SessionizeImportService.cs:59,111-113,145-157,204-206,395-401` | ✅ |
| Email-less speaker | §204 placeholder `sessionize-<id>@no-email.sessionize.invalid` queue row, reconciled onto the real email on the SAME row later | `SessionizeImportService.cs:158-173,285-337` | ✅ |
| Case-sensitivity seam | in-memory `existing` dict is case-sensitive over raw `p.Email`; a mixed-case email written by sponsor sync (which doesn't normalize) can miss the match → near-duplicate row | `SessionizeImportService.cs:111-113`; `SponsorContactSyncService.cs:111` | 🟡 |
| Deleted in Sessionize, then re-appears | never auto-deleted (alert-only §56/§58); re-appearance = plain upsert | `SessionizeDisappearanceDetector.cs:24-26`; `SessionizeApiImportService.cs:120-155` | ✅ (see F4 for the ghost side) |
| §245 queue: speaker skips Preselected | `Inactive → Active` in one step; bulk Preselect leaves speaker rows | `PreselectionQueueService.AdvanceAsync:125-131` | ✅ |

### Family 2 — happy path

| Scenario | Behaviour | Evidence | Verdict |
|---|---|---|---|
| Activate → welcome → 10-step wizard → speakerdl deadline tasks → all done, all reminders stop | `ParticipantActivationService.ActivateAndOnboardAsync`; `welcome:{id}` ledger; entitlement-gated steps ③-⑥; speakerdl seeded on first hub visit; §232/§81 stop rules verified | `SpeakerWizardService.cs:67,94-134`; `SpeakerDeadlineSeeder.cs:114-117,174` | ✅ |

### Family 3 — partial engagement

| Scenario | Behaviour | Evidence | Verdict |
|---|---|---|---|
| Weekly question digest (not daily), IsActive-gated | 7-day quiet window; `p.IsActive && Role == Speaker` | `SpeakerQuestionDigestService.cs:112` | ✅ |
| Deck-upload / logistics deadlines fire on due day only, once | §81; `task:{id}:due` occasion | `TaskReminderBuilder.cs:140-145,188` | ✅ |
| ReminderJob sends without reconciling first | a speakerdl mirror task whose form data exists but was never page-load-reconciled can fire one spurious due-day mail | `ReminderJob.cs` (no `FormTaskReconciler` call) | 🟡 |
| Stale doc: config still claims "14/7/3/1 days before due" | behaviour is §81 due-day-only | `config/speaker-deadlines.eldk27.json` `_doc` | 🟡 |

### Family 4 — DROP-OUT (speaker cancels / organizer deactivates)

| Surface | What happens | Evidence | Verdict |
|---|---|---|---|
| (i)-(v) hotel / dinner / lunch / swag / party rows + all counts/exports | identical ghost behaviour | B1-B5 | 🔴 |
| Hotel confirmed-invite mail to the cancelled speaker | B1: H3 | | 🔴 |
| (vi) speakerdl tasks + due-day reminders | tasks never pruned (prune loop only reaches active speakers); TaskReminderBuilder mails them anyway | B7: T9 + T1 | 🔴 |
| (ix) Sessions — does the session lose its speaker? | **No.** Sessionize disappearance is alert-only ("We never delete or deactivate the row"); vanished speaker stays `IsActive=true` with `SessionSpeakers` links intact. Per-session speaker-set reconcile self-heals ONLY while the session itself is still in Sessionize | `SessionizeDisappearanceDetector.cs:24-26,126-128`; `SessionImportService.cs:175-185` | 🔴 (manual action required; org deactivation touches sessions not at all) |
| Public rendering of a deactivated speaker | speakers page gates IsActive ✅; public agenda does NOT | B8: X4, X5 | 🔴 (agenda) |
| SpeakerDeletionService | removes SpeakerProfile only, blocked while session links exist; "participant — identity, login, logistics — untouched" | `SpeakerDeletionService.cs:26,104-131` | 🟡 |
| Out-of-band purge script | `INTERNAL/purge-removed-speaker-tasks.ps1` (raw SQL, `-Apply`, matches by title) — manual DBA, no in-app equivalent | script header | 🟡 |
| Biweekly party/digest reminders stop; login blocked | B7: T3; §A3 | | ✅ |

### Family 5 — re-activation

Same as R1: rows resurrect silently, no duplication; §216-style restore does not apply (speakers have no mirror). ✅ / silent-resurrection caveat.

| Verdict | ✅ (1 cell) |
|---|---|

### Family 6 — role change (speaker→other / ex-speaker)

| Scenario | Behaviour | Evidence | Verdict |
|---|---|---|---|
| Role changed away from Speaker | speakerdl prune never reaches them (loop filters `Role == Speaker`) → dated deck/hotel tasks stay Open and STILL FIRE due-day reminders; wizard tasks orphaned | `SpeakerDeadlineSeeder.cs:114-117,210-224`; B7: T1 | 🔴 |
| Multi-hat (speaker + sponsor contact) | ONE Participant row (email-unique per edition); `Role=Sponsor` + `SpeakerProfile`; entitlements = union of hats | `EditParticipant.cshtml.cs:164-171`; `OrderEntitlements.cs:6-10` | ✅ |

### Family 7 — entitlements (supported speaker)

| Scenario | Behaviour | Evidence | Verdict |
|---|---|---|---|
| Supported set = Polo, Swag, Award, Hotel, TravelReimbursement, Dinner, +LunchPreDay if SpeakingPreDay, +LunchMainDay if SpeakingMainDay | verified verbatim | `OrderEntitlements.cs:63-71` | ✅ |
| `ParticipantOrderOverride` honored | wizard immediately (`FormEntitlementGate.cs:45-49`); speakerdl on next nightly run incl. prune (`SpeakerDeadlineSeeder.EffectiveItemsAsync:263-281`) | `SpeakerReview.cshtml.cs:211-246` (write) | ✅ |
| Override-exclude leaves already-seeded generic wizard tasks | WizardStepTaskSeeder honors overrides at CREATION but never removes an existing task | `WizardStepTaskSeeder.cs:79-84` | 🟡 |

**R2 tally: ✅ 13 · 🟡 6 · 🔴 6**

---

## R3. Speaker — sponsor-funded (SpeakerFunding.SponsorSelfFunded)

> Inherits every R2 row; this section is the family-7 delta — the operator's fear (b) case.
> Funding is an organizer-set flag on `SpeakerProfile.SpeakerFunding` (`Domain/SpeakerProfile.cs:8-17,73`), not order-derived.

| Scenario | Behaviour | Evidence | Verdict |
|---|---|---|---|
| Entitlement set = AppreciationDinner + LunchMainDay ONLY | verified verbatim | `OrderEntitlements.cs:74-77` | ✅ |
| Wizard hides Hotel / Swag steps | `if (entitled.Contains(OrderItem.Hotel))` etc. | `SpeakerWizardService.cs:94,102,110` | ✅ |
| Travel is not a wizard step for anyone (§141/142) | deadline-task-only | `SpeakerWizardService.cs:129-134` | ✅ |
| Task seeding: hotel/dinner/swag/lunch speakerdl deadlines gated on entitlement (P12) | `DeadlineAllowedByEntitlement` branches hotel/dinner/swag/lunch | `SpeakerDeadlineSeeder.cs:169-173,242-253` | ✅ |
| **Travel deadline NOT gated** — no `travel` branch; `return true; // non-logistics` catches `submit-travel-reimbursement` → a sponsor-funded non-Denmark speaker GETS the travel-reimbursement task + its due-day reminder, despite no `TravelReimbursement` entitlement. Directly contradicts the method's own comment ("should not get a hotel/travel/swag task") | verified by direct read | `SpeakerDeadlineSeeder.cs:242-253` + `config/speaker-deadlines.eldk27.json` (`nonDenmarkOnly: true`) | 🔴 |
| "Pre-day Lunch" task/step passes on the OR-gate (`LunchPreDay \|\| LunchMainDay`) — a main-day-only speaker gets a pre-day-lunch task/step | `SpeakerDeadlineSeeder.cs:250-251`; `SpeakerWizardService.cs:122`; `LunchFormService.cs:148-150` | | 🟡 |
| Reminders layer has zero entitlement awareness — correctness depends entirely on the task set being right (so the travel gap above also REMINDS) | `TaskReminderBuilder.cs:114-118` | | 🔴 |
| Dinner + main-day-lunch steps/tasks present and working | wizard `:102,122` + seeder gates | | ✅ |

**R3 tally (delta): ✅ 5 · 🟡 1 · 🔴 2**

---

## R4. Sponsor contact (booth vs digital · coordinator vs signer vs booth member)

### Family 1 — entry variants

| Scenario | Behaviour | Evidence | Verdict |
|---|---|---|---|
| Woo pull creates company/orders/contacts; contact email already exists as ANOTHER role | skipped + warned — "NEVER overwrite an Organizer / Speaker / … row even if the email collides" | `SponsorContactSyncService.cs:23-27,143-150` | ✅ |
| Email normalization | `p.Email == u.Email` with no trim/lowercase — relies on SQL CI collation; stores raw CM casing (feeds the Sessionize dict seam) | `SponsorContactSyncService.cs:111` | 🟡 |
| Welcome audience | event-coordinator contacts only, signer-only excluded; blocks until SharePoint folders exist for booth companies | `SponsorWelcomeReconcileJob`; ROLE-FLOWS §5 | ✅ |

### Family 2/3 — happy path & partial

| Scenario | Behaviour | Evidence | Verdict |
|---|---|---|---|
| Company-scoped wizard (①-④ + booth-only ⑤-⑦ §229 + party §228); any contact's party answer completes the step for the whole team | booth steps gated `if (info?.HasBooth == true)` | `SponsorWizardService.cs:114`; `AttendeePartyReminderBuilder.cs:124-127` (group answered check) | ✅ |
| Party reminder stops company-wide once ANY contact answers; sponsor reminders route via IsActive-filtered resolver | B5: P5 + B7: T5 | | ✅ |
| Get-Started digest sponsor audience uses only the manual `IsEventCoordinator` flag (not the §7c ERP resolver) | `GetStartedDigestBuilder.cs:123` | | 🟡 |

### Family 4 — contact leaves / company withdraws — **the sponsor lifecycle has NO exit path**

| Surface | What happens | Evidence | Verdict |
|---|---|---|---|
| Organizer deactivates a contact → **next sync run RE-ACTIVATES them** | `if (!existing.IsActive) { existing.IsActive = true; … }` — sync treats inactive as a defect to repair; deactivation does not stick, login comes back | `SponsorContactSyncService.cs:175-179` | 🔴 |
| Contact removed in ERP/webshop | nothing mirrors the removal — sync is insert/update-only; ERP reconcile "only creates MISSING webshop users… never overwriting" | `ErpWebshopReconcileJob.cs:19-21` | 🔴 |
| Booth-member registration of a departed contact | `SponsorBoothMember` is keyed `(EventId, SponsorCompanyId, Email)` with NO ParticipantId — deactivating the participant does not tombstone the booth row; the person keeps their Zoho booth/lead-capture registration until manual removal | `Domain/SponsorBoothMember.cs:44-52`; delete path `CompanyDetails.cshtml.cs:963-999` | 🔴 |
| Personal/group party RSVP of a departed contact (incl. group HeadCount) | survives; counts don't join Participants; company reservation lookup doesn't check IsActive | B5: P1-P4 | 🔴 |
| **Whole company withdraws** | no concept exists: `SponsorInfo` has no status/withdrawn field; repo-wide `Withdraw` grep hits only the volunteer decline-undo. Orders, booth, company tasks, SharePoint folders, Zoho records (§56 forbids delete), group party HeadCount and the public logo all persist | `Domain/SponsorInfo.cs`; B8: X7 | 🔴 |
| Woo order refunded/cancelled | invisible — client fetches `status=completed` only; no order mirror/soft-cancel; tier/package are RAISE-ONLY | `WooCommerceClient.cs:97,109`; `SponsorOrderPullService.cs:292-334` | 🔴 |
| `SponsorInfoDeletionService` | refuses while active contacts exist; deletes ONLY the facts row (keeps contacts/orders/booth members/RSVP/tasks/SharePoint/Zoho) | `SponsorInfoDeletionService.cs:81-114` | 🟡 |
| Sponsor emails / login of a deactivated contact | resolver filters IsActive ✅; login blocked ✅ — *until the sync re-activates them (row 1)* | B7: T5; §A3 | 🟡 |

### Family 5/6 — re-activation & flags

| Scenario | Behaviour | Evidence | Verdict |
|---|---|---|---|
| Contact switches company in CM | `SponsorCompanyId` overwritten to the syncing company — last-synced-company-wins; a contact listed under two companies flaps between pulls | `SponsorContactSyncService.cs:153-157` | 🟡 |
| Signer/coordinator flag revoked in CM | flags are SET-only ("never cleared") — revoked signer keeps `IsSigner` until an organizer clears it | `SponsorContactSyncService.cs:180-194` | 🟡 |
| Multi-hat: booth member who also speaks | gets dinner via the speaker hat (booth hat deliberately excludes dinner) | `OrderEntitlements.cs:98-106` (NOTE comment) | ✅ |

### Family 7 — entitlement variants

| Scenario | Behaviour | Evidence | Verdict |
|---|---|---|---|
| Booth member = Polo + LunchMainDay; digital-only contact = NOTHING from the sponsor hat | verified verbatim | `OrderEntitlements.cs:98-109` | ✅ |
| Booth vs digital wizard difference | exhibitor steps ⑤-⑦ only when `HasBooth` | `SponsorWizardService.cs:114` | ✅ |
| Package/booth drives steps; Silver=digital, Gold+=booth | `SponsorInfo.HasBooth` from package | ROLE-FLOWS §5; `SponsorOrderPullService` tier mapping | ✅ |

**R4 tally: ✅ 8 · 🟡 6 · 🔴 6**

---

## R5. Attendee — 2-day ticket

### Family 1 — entry variants

| Scenario | Behaviour | Evidence | Verdict |
|---|---|---|---|
| Provisioning matched by email, normalized both sides, case-insensitive | `h.Email.Trim().ToLowerInvariant()` / lowercased hashset | `AttendeeWelcomeProvisioningService.cs:144-162` | ✅ |
| Email already exists as Speaker/Volunteer/any role | skipped — "a holder who already has a Participant (any role) is skipped"; no role overwrite | `AttendeeWelcomeProvisioningService.cs:17-18,150` | ✅ |
| Two tickets on one email / in-batch duplicates | one participant per email (`!seen.Add(email)`); entitlement = ≥1 active ticket per email | `AttendeeWelcomeProvisioningService.cs:163`; `AttendeeTicketSyncService.cs:544-548` | ✅ |

### Family 2/3 — happy path & partial

| Scenario | Behaviour | Evidence | Verdict |
|---|---|---|---|
| Auto MC-invite (§241, delivery-gated) → select → confirmed/.ics → party Yes/No → both tasks close | 2-step `AttendeeWizardService` (ticket-gated :36-45); `FormTaskReconciler` two-way for party (169-219) + MC (250-291) | ROLE-FLOWS §1 | ✅ |
| §232 cadence verified (≥14d, biweekly windows, stop-on-answer) | R1 family-3 evidence + `AttendeeMasterClassReminderBuilder.cs:97-102` | | ✅ |
| `pendingmc` chaser once-ever | dedup key `pendingmc:{email}` in `SentReminder` ledger | `AttendeeBackstageSyncJob.cs:364` | ✅ |

### Family 4 — organizer MANUAL deactivation (distinct from ticket cancel!)

| Surface | What happens | Evidence | Verdict |
|---|---|---|---|
| Login | blocked ≤5 min | §A3 | ✅ |
| Party biweekly nag | stops (IsActive gate) | B7: T3 | ✅ |
| **MC biweekly nag** | KEEPS FIRING — mirror gate only, no IsActive (their ticket is still Active, so the mirror gate passes) | B7: T2 | 🔴 |
| Party RSVP row + food count | survives + counted (cascade only exists on the ticket path) | B5: P1-P3 | 🔴 |
| MC seat | still held → blocks capacity for others | no release outside `SoftCancelAttendeeAsync` | 🟡 |

### Family 8 — ticket lifecycle (§216/§230/§234/§243) — all in `AttendeeTicketSyncService.cs`

| Scenario | Behaviour | Evidence | Verdict |
|---|---|---|---|
| CANCEL: mirror soft-cancel | `MirrorState=Cancelled; CancelledAt=now` (TicketStatus intact) | `:484-486,256-268` | ✅ |
| CANCEL: MC seat released + waitlist promoted atomically | signup hard-deleted → `PromoteNextAsync` | `:476-482`; `MasterClassSignupService.cs:611-634` | ✅ |
| CANCEL: party RSVP + headcount drop | `Attending=false; HeadCount=null`; count is `rows.Count(a => a)` | `:587-592`; `PartyRsvpService.cs:240-243` | ✅ |
| CANCEL: login lockout, multi-ticket-safe, role-safe | `IsActive = hasActiveTicket`, Attendee-role only ("a speaker… never locked out by this path") | `:520-563,513-517,539-541` | ✅ |
| CANCEL: §243 email | one per person per cancellation instant (`cancelled:{UtcTicks}`), ledgered on real delivery only, 7-day retry, 2-day holders only | `MasterClassEmailService.cs:781-853,906-908` | ✅ |
| CANCEL: organizer counts | Attendees page filters `MirrorState == Active`; chaser excludes cancelled | B8: X3; `AttendeeBackstageSyncJob.cs:326-331` | ✅ |
| CANCEL: attendee-list export | NO MirrorState filter → cancelled tickets on the on-site list | B8: X1 | 🔴 |
| CANCEL: open tasks | not closed — go quiet via builder gates (party ✅ / MC mirror-gate ✅ for CANCELLED); reconciler closes party+MC tasks for cancelled attendees on page load | `AttendeePartyReminderBuilder.cs:111-123`; `FormTaskReconciler.IsCancelledAttendeeAsync:228-241` | 🟡 |
| REASSIGN: seat transfers, never released; old holder party-RSVP cancelled + locked out; new holder provisioned/restored; validation mail crash-safe | `:203-236` ("seat stays HELD… no double-count"), §209 `:229`, `AttendeeBackstageSyncJob.cs:185-202` | | ✅ |
| RE-PURCHASE: mirror + login restored (`CancelledAt=null`) | `:199-201,238-240,550-553` | | ✅ |
| RE-PURCHASE: party answer stuck at "not attending" — the cancel-time `Attending=false` row counts as "answered", so they are NEVER re-asked (silent headcount loss, opposite-direction ghost) | `AttendeePartyReminderBuilder.cs:74-78,116` | | 🟡 |
| RE-PURCHASE: MC never re-prompted by email — `MasterClassInviteSentAt` keeps its stamp; `pendingmc:{email}` is once-ever-ledgered; seat was released at cancel (self-serve in hub still possible) | `AttendeeBackstageSyncJob.cs:364`; `ReminderEngine.cs:98-122` | | 🟡 |

**R5 tally: ✅ 16 · 🟡 4 · 🔴 3**

---

## R6. Attendee — 1-day ticket (SUSPENDED §242)

| Scenario | Behaviour | Evidence | Verdict |
|---|---|---|---|
| Mirror-only sync; sweep deactivates every 1-day-only login each run while flag OFF; restore on flag ON (never undoing a §216 lockout); 2-day holders untouched | `ReconcileOneDayAccessAsync` — `desired = enabled && m.HasActiveTicket` | `AttendeeWelcomeProvisioningService.cs:82-120` | ✅ |
| Party seeder / reminder builders skip deactivated logins | `PartyTaskSeeder.cs:76` ("the IsActive filter below is ALSO the 1-day suspension gate") | | ✅ |
| Attendee-list export includes 1-day + cancelled mirror rows | B8: X1 | | 🔴 |

**R6 tally: ✅ 2 · 🟡 0 · 🔴 1**

---

## R7. Media

| Family | Scenario | Evidence | Verdict |
|---|---|---|---|
| 1 | Manual add; duplicate-email guarded | `EditParticipant.cshtml.cs:116-122` | ✅ |
| 2 | `welcome-media` once-ever → RoleWizard (Profile → Hotel/Dinner/Lunch → Signal broadcast-only → Party §206 → CoC) | `WelcomeReconcileJob`; `RoleWizardService.cs:91,115-147` | ✅ |
| 3 | 3-state queue if entered via queue; §232 verified | `PreselectionQueueService.AdvanceAsync:122-138` | ✅ |
| 4 | DROP-OUT: identical ghost set — hotel (H1-H4), dinner (DN1), lunch (L1-L4), party (P1-P3), open tasks (T6-T7), due-day reminders (T1) | §B | 🔴 |
| 4 | Biweekly reminders stop; login blocked | B7: T3; §A3 | ✅ |
| 5 | Re-activation resurrects silently, no dup | R1 F5 evidence | ✅ |
| 7 | Set = Polo, Hotel, Dinner, LunchPreDay, LunchMainDay (no swag-gift, no travel); wizard gated on it | `OrderEntitlements.cs:119-126` (verified) | ✅ |

**R7 tally: ✅ 6 · 🟡 0 · 🔴 1** (the single 🔴 row spans the whole shared B-inventory)

## R8. Event partner

Identical machinery to Media (manual add → `welcome-eventpartner` → RoleWizard; same entitlement
line `OrderEntitlements.cs:119-126`, Signal = chat + broadcast). Every R7 verdict applies 1:1,
including the shared drop-out 🔴.

**R8 tally: ✅ 6 · 🟡 0 · 🔴 1**

## R9. Organizer

| Family | Scenario | Evidence | Verdict |
|---|---|---|---|
| 1 | Manual add, duplicate guard; no welcome by design (`WelcomeVariants` returns null) | `EditParticipant.cshtml.cs:116-122`; ROLE-FLOWS §8 | ✅ |
| 2 | RoleWizard (Profile → logistics → Party → CoC); entitlements Polo/Swag/Hotel/Dinner/both lunches | `OrderEntitlements.cs:89-96` (verified) | ✅ |
| 4 | DROP-OUT of an organizer: same shared ghost set (B1-B5, T1, T6-T7) — nothing organizer-specific mitigates it | §B | 🔴 |
| 4 | Login blocked; biweekly digests stop | §A3; B7: T3 | ✅ |
| 6 | Role-change tooling gap applies to any role an organizer edits | R1/R2 F6 | 🔴 (shared) |

**R9 tally: ✅ 3 · 🟡 0 · 🔴 2**

---

## 🔴 GAP LIST — consolidated, ordered by severity, with fix proposals

> **ALL FIXED — see the RESOLUTION block at the top.** G1–G8: PR #371 · G9–G16: PR #370 ·
> G7-completion/G8d/G17: `fix/jul08-matrix-residuals` (2026-07-08). Kept below as written for
> the audit record.

### P0 — logistics money is wrong today (operator fear (a) confirmed)

**G1. There is no deactivation cascade — root cause.**
All four organizer paths (D1-D4, §A1) are flag-only; `CancelPartyRsvpAsync` proves the cascade
pattern exists but is wired ONLY to ticket sync.
*Fix:* one `ParticipantDeactivationService.DeactivateAsync(pid, reason)` used by ALL entry points,
doing: set `IsActive=false` + `LifecycleState=Inactive` (kills the D1/D4 divergence), cancel party
RSVP (reuse `CancelPartyRsvpAsync` semantics: `Attending=false, HeadCount=null`, keep the row for
audit/re-ask), set `HotelBooking.NeedsRoom=false` (or leave row, rely on G2 filters), close or
suppress open tasks, release volunteer task assignments (or flag them `Vacated`), write an
AuditEntry. Re-activation path = the §216 restore pattern (idempotent, no duplication — the data
model already supports it, see R1-F5).

**G2. Hotel counts + rosters + invites ignore IsActive** — `HotelRoomBlockService.cs:145-175` (H1),
`HotelManagementService.cs:260-308` (H2), `CommandCenterService.cs:335-336` (H4), and worst:
**confirmed hotel calendar invites are emailed to drop-outs** (`HotelManagementService.cs:152-180` +
`Hotels.cshtml.cs:236-303`, H3).
*Fix:* add `ParticipantActivation.IsActiveExpr` to the participant/booking queries in all four;
H5 (`DataGrid.cshtml.cs:186`) is the in-repo reference implementation.

**G3. Party headcount includes ghosts** — counts/list/export never join Participants
(`PartyRsvpService.cs:233-243`, `PartyRsvps.cshtml.cs:42-76`, P1-P2) and nothing outside ticket-sync
ever cancels an RSVP (P3). A withdrawn sponsor company's group HeadCount inflates the venue food
order indefinitely.
*Fix:* (a) G1 cascade cancels the RSVP; (b) belt-and-braces: join Participants + `IsActiveExpr` in
`CountsAsync`/`GetAllAsync` (keep anonymous/company rows via the existing nullable-FK semantics);
(c) sum group HeadCount only when the answering contact's company still has ≥1 active contact.

**G4. Lunch counts + caterer exports ignore IsActive** — `Lunch.cshtml.cs:60-67,93-97`,
`OrganizerExportsService.cs:141-143,158-159`, `Dashboard.cshtml.cs:260-266`,
`CommandCenterService.cs:340-341` (L1-L4).
*Fix:* apply `IsActiveExpr` on the join (the file already uses it for the auto pre-day count at
L80-81 — extend to all queries).

**G5. Swag vendor orders ignore IsActive** — zero `IsActive` anywhere in `Swag.cshtml.cs`
(S1-S2) + `CommandCenterService.cs:337-339` (S3). Polo/jacket size sheets sent to the vendor
include drop-outs → direct over-ordering cost.
*Fix:* filter the participant join in the overview + all five sheet builders.

**G6. Dinner headcount sums ghost plus-ones** — `CommandCenterService.cs:343-347` (DN1).
*Fix:* join Participants + `IsActiveExpr`.

**G7. Volunteer coverage counts ghosts** (V1-V5): coverage `GroupBy(TaskId).Count()` with no
IsActive and no DecisionStatus filter; auto-assign under-backfills the exact shifts a drop-out
vacated; the purpose-built `SeedDropoutBackfillAsync` is manual-only and itself ghost-blind; rota
export prints withdrawn volunteers; ghost-held tasks vanish from the attention tile.
*Fix:* one shared "effective assignment" filter (`Assignment.Participant.IsActive &&
Decision != Declined`) used by `LoadCoverageAsync`, `AvailabilityAutoAssignEngine`,
`AllocationScenarioService`, `OrganizerOverviewService`, `CommandCenterService`, rota export; call
`SeedDropoutBackfillAsync` from the G1 cascade.

**G8. Sponsor lifecycle has no exit path** — and worse, **deactivation un-sticks**:
`SponsorContactSyncService.cs:175-179` re-activates any inactive contact on the next 15-min pull
(login returns); contact removal in ERP/webshop is never mirrored (`ErpWebshopReconcileJob.cs:19-21`);
Woo refunds are invisible (`WooCommerceClient.cs:97,109`); no company-withdrawal concept
(`SponsorInfo` has no status field); departed booth members keep their Zoho booth/lead registration
(`SponsorBoothMember` has no ParticipantId, `SponsorBoothMember.cs:44-52`); group party HeadCount,
tasks, SharePoint, public logo all persist.
*Fix:* (a) add `ManuallyDeactivatedAt` tombstone the sync respects (never auto-reactivate a
tombstoned contact); (b) add `SponsorInfo.Status` (Active/Withdrawn) + an organizer "withdraw
company" action that runs a company cascade (cancel group RSVP, close company tasks, deactivate
contacts, hide public logo — never touching Zoho per §56); (c) tombstone booth-member rows when
their matching contact is deactivated (match by email); (d) pull non-completed Woo order statuses
and surface refunds as an organizer alert.

### P1 — wrong prompts to wrong people (operator fear (b))

**G9. Reminder builders missing the IsActive gate** — `TaskReminderBuilder.cs:114-118` (T1: every
dated task — the whole speaker-deadline track — mails deactivated people on due day);
`AttendeeMasterClassReminderBuilder.cs:86-95` (T2: organizer-deactivated 2-day attendee biweekly-
nagged forever); `OnboardingStepResetEmailService.cs:44-68` + `ParticipantEmailService.cs:60-104`
(T4: no state check end-to-end).
*Fix:* add the one-line gate the party builder already has (`AttendeePartyReminderBuilder.cs:115`)
to all three; add an IsActive check in the shared `ParticipantEmailService` transport as a backstop.

**G10. Travel deadline not entitlement-gated** — `SpeakerDeadlineSeeder.DeadlineAllowedByEntitlement`
(`SpeakerDeadlineSeeder.cs:242-253`) has no `travel` branch; `return true` catches
`submit-travel-reimbursement` → **a sponsor-funded speaker gets the travel task + reminder** (the
operator's literal example), contradicting the method's own comment (L169-173).
*Fix:* `if (slug.Contains("travel")) return entitled.Contains(OrderItem.TravelReimbursement);` —
the nightly orphan-prune (L210-224) then auto-removes already-seeded travel tasks from non-entitled
active speakers.

**G11. Role change is a bare field write** — `EditParticipant.cshtml.cs:178-181`; old-role wizard
tasks orphaned (seeder is dedup-only, reconciler never closes vanished steps); an ex-speaker's dated
`speakerdl:` tasks are never pruned (prune loop filters `Role == Speaker`,
`SpeakerDeadlineSeeder.cs:114-117`) and keep firing due-day reminders via G9.
*Fix:* on Role change, run a reconciliation: close open wizard-mirror tasks whose step is absent
from the NEW role's wizard; extend the speakerdl prune to iterate participants WITH open
`speakerdl:` tasks (not only current speakers).

### P2 — organizer-view pollution & data hygiene

**G12. Deactivation never closes tasks + TasksTable unfiltered** — T6/T7
(`TasksTable.cshtml.cs:172-174`). *Fix:* G1 closes/suppresses; add an "assignee active" filter
(default on) to the grid + export.

**G13. Attendee list export lacks MirrorState filter** — `OrganizerExportsService.cs:98-99` (X1);
cancelled + 1-day tickets print on the on-site list. *Fix:* `.Where(a => a.MirrorState ==
MirrorState.Active)` (X3 shows the correct pattern).

**G14. Ghost speakers on the public agenda** — `PublicAgendaService.cs:100-102` (X4) has no
IsActive gate (speakers page X5 does); a Sessionize-vanished speaker additionally stays
active+linked by design (alert-only §56/§58). *Fix:* gate agenda speaker names on
`Participant.IsActive`; keep disappearance alert-only but add a one-click "deactivate + G1 cascade"
action on the disappearance alert.

**G15. Hard-delete FK holes** — PartyRsvp is `NoAction`, neither probed nor cleaned
(`ParticipantDeletionService.cs:171-238`, `CommunityHubDbContext.cs:570-573`) → ghost RSVP or SQL
error; volunteer availability rows are `Restrict` and also missing → hard-deleting a queue
applicant likely 500s (`PreselectionQueue.cshtml.cs:122-138` doesn't catch `DbUpdateException`).
*Fix:* add both to `CleanSafeDependentsAsync`; catch `DbUpdateException` in the page and fall back
to deactivate.

**G16. Re-purchased attendee is never re-engaged** — the cancel-time `Attending=false` party row
counts as "answered" (`AttendeePartyReminderBuilder.cs:74-78,116`) and MC invite/chaser ledger
stamps are permanent (`MasterClassInviteSentAt`; `pendingmc:{email}`). Silent under-count this time.
*Fix:* on ticket-restore (`AttendeeTicketSyncService.cs:199-201`), delete the auto-cancelled party
row (only when `Attending=false` was set by the cancel path — track via `UpdatedAt`/marker) and
clear `MasterClassInviteSentAt` so §241 re-invites.

### P3 — hygiene / consistency

**G17.** Pre-day-lunch OR-gate lets main-day-only people get a pre-day step/task
(`SpeakerDeadlineSeeder.cs:250-251`, `SpeakerWizardService.cs:122`, `LunchFormService.cs:148-150`)
— split into per-day checks. · Email normalization inconsistent (sponsor sync stores raw casing,
`SponsorContactSyncService.cs:111`; Sessionize dedups case-sensitively in memory,
`SessionizeImportService.cs:111-113`) — normalize at write in every entry path. · Two "active"
definitions + D4 not setting LifecycleState (§A2) — G1 unifies. · Bulk `ReactivateAsync` bypasses
`ParticipantActivationService` (no onboarding). · `WizardStepTaskSeeder` never removes stale tasks
after an override-exclude. · Stale "14/7/3/1" doc string in `config/speaker-deadlines.eldk27.json`.
· `ReminderJob` doesn't run `FormTaskReconciler` before sending (possible one spurious due-day mail).

---

## Verdict totals

| Role | ✅ | 🟡 | 🔴 |
|---|---|---|---|
| R1 Volunteer | 13 | 4 | 11 |
| R2 Speaker (supported) | 13 | 6 | 6 |
| R3 Speaker (sponsor-funded, delta) | 5 | 1 | 2 |
| R4 Sponsor contact | 8 | 6 | 6 |
| R5 Attendee 2-day | 16 | 4 | 3 |
| R6 Attendee 1-day (suspended) | 2 | 0 | 1 |
| R7 Media | 6 | 0 | 1 |
| R8 Event partner | 6 | 0 | 1 |
| R9 Organizer | 3 | 0 | 2 |
| **Total cells** | **72** | **21** | **33** |

The ✅ backbone (entry dedup, login gating, §232 cadence, entitlement-gated wizards, the §216
ticket cascade) is genuinely solid. The 🔴 mass is concentrated in ONE structural hole — no
deactivation cascade + no IsActive filter on logistics counts (G1-G8) — plus the travel-entitlement
gate (G10) and the missing-IsActive reminder builders (G9). Fixing G1 + a mechanical IsActive
sweep over §B closes the majority of the matrix.
