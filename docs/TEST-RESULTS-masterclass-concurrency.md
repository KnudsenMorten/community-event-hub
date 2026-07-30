# Test results — Master Class seat allocation under 400-concurrent load (§221)

Proves the **§218 OPTIMISTIC seat allocation** in
`src/CommunityHub.Core/Reminders/MasterClassSignupService.cs`
(`TryClaimSeatAsync` — a guarded conditional `UPDATE` on the capacity-bearing
`Session` row) is correct under **genuine wall-clock concurrency** against a
**real relational SQL Server**: it never oversells the last seat, keeps the
confirmed/waitlist counts exact, promotes a waitlisted user into a seat freed by
a cancellation (exactly once each), never double-counts, and survives without an
exception/deadlock storm.

---

## Full prod stress suite (§222/§223) — summary

The complete stress suite was run against the **REAL prod environment** (prod Azure SQL +
the prod web frontend + the Brevo SMTP relay), in an **isolated synthetic test event**
(never the live `EventId = 1`), with **FK-safe cleanup + verify (0 rows remain)** afterward.
Per the **§223 prod-only test policy** the **SQL Express path has been removed** from the
load-sim harness — load tests now run **only against dev/prod Azure SQL**, because SQL
Express has `READ_COMMITTED_SNAPSHOT` (RCSI) **OFF**, which **masked** the §222 oversell bug
(see §0/§0a). The seat allocation is **optimistic + RCSI-correct** via `WITH (UPDLOCK,
HOLDLOCK)` on the capacity read (never a global serializable lock).

| Stress test | Scale | Result |
|---|---|---|
| **SQL — master-class concurrency** | **400 concurrent** signups/waitlist + cancellation→promotion, prod Azure SQL (RCSI ON), S2 | **PASS** — confirmed == capacity exactly, **OVERSELL = 0**, **0 exceptions**, ~5.9 s, promotion exactly-once on concurrent frees, **cleanup verified (0 rows remain)** |
| **SQL — extreme single-class contention** | **1000 concurrent** (500 racing **one 8-seat** class) on S2 | **OVERSELL = 0** (correctness holds) but **~2 % (19/1000)** signups **errored** — the racers serialize on the `UPDLOCK/HOLDLOCK` seat lock and the S2 tier can't drain the convoy in time. **NOT a realistic load** (the real ~1500 spread across ~8 classes and over time). **Recommendation: scale the DB tier UP for the event window** (S2 → S4/S6 or vCore), scale back after. |
| **Brevo SMTP — bulk email** | **500 emails** (plus-addressing `mok+stresstestuserN@expertslive.dk`, Ring-1 allowlist) | **PASS** — **500/500 accepted, 0 fail, ~37/s, no throttle**. §219 pacing/retry held. Deliverability to confirm with operator (no Brevo API key to query the delivery log). |
| **Web frontend — concurrent logins** | **400 concurrent** users authenticating + hitting the app on prod | **PASS** — **400/400, 0 fail**, **p50 174 ms / p95 1.75 s**, ~47 req/s |

**Bottom line:** correctness holds everywhere (**0 oversell**, 0 lost emails, all logins served).
The only scale caveat is single-class lock-convoy latency at an unrealistic 1000-way contention,
mitigated operationally by temporarily scaling up the DB tier for the live event window.

---

## 0. PROD AZURE SQL run — real database engine validation (⚠ CRITICAL FINDING — NOW FIXED, see §0a)

> **The §218 optimistic seat-claim OVERSELLS on the real prod Azure SQL database.**
> **(RESOLVED by the §219 RCSI fix — see §0a below, which re-runs this same harness on
> prod and shows OVERSELL = 0. The bug described in this section is the PRE-FIX state.)**
> The local SQL Express run in §3 passes; the *same code* on prod Azure SQL **does
> not** — because Azure SQL Database has **`READ_COMMITTED_SNAPSHOT` (RCSI) ON by
> default** (it is **OFF** on SQL Express). This is exactly the kind of
> engine/tier-specific behaviour the operator asked to validate, and it is a real
> correctness bug, not a harness artifact.

**How this was run.** The harness was extended with an **Azure existing-DB mode**
(`CEH_LOADSIM_AZURE=1`): it does **not** CREATE/DROP a database (the deploy SPN has
no server-admin rights); instead it connects to the **existing prod database with an
AAD access token** (set on every `SqlConnection` via a `DbConnectionInterceptor`),
seeds an **isolated synthetic test event** (`Id = 900001`, well clear of the live
event `Id = 1`), runs the identical Phase 1 + Phase 2, then runs a **FK-safe row
cleanup and re-queries every touched table to prove zero rows remain**. The live
event (`EventId = 1`) and all existing data were never read or modified.

| | |
|---|---|
| **DB target** | `eldk27hub-sql-prodpdrq.database.windows.net` / `eldk27hub-db` — **Azure SQL Database, Standard S2** |
| **Auth** | AAD access token (deploy SPN `9e0974ed-…`, db user `dbo`; roles db_owner/db_ddladmin/db_datawriter) |
| **Isolation mode** | `read_committed_snapshot_on = 1`, `snapshot_isolation_state = ON` ← **root cause** |
| **Test event** | `Id = 900001` (synthetic; `MAX(Id)` was `1`; aborts if 900001 already exists). Forced via `IDENTITY_INSERT`, identity reseeded back to `1` afterward — no lasting prod side-effect. |
| **Live data** | `EventId = 1` never touched; only rows tagged to event `900001` written, then deleted. |

**Results — two consecutive prod runs (400 racers, Class A cap 5, Class B cap 10):**

| Metric | Run 1 | Run 2 |
|---|---:|---:|
| Class A confirmed (cap **5**) | **13** | **7** |
| Class B confirmed (cap **10**) | **18** | **29** |
| **OVERSELL (must be 0)** | **16** | **21** |
| Class A waitlisted | 187 | 193 |
| Class B waitlisted | 182 | 171 |
| Duplicate signups | 0 | 0 |
| Exceptions / deadlock storm | 0 | 0 |
| Phase 1 wall-clock | 3293 ms | 3627 ms |
| Phase 2 promotions (after 13 / 7 cancels) | 0 | 1 |
| **Cleanup verification** | **0 rows remain** ✅ | **0 rows remain** ✅ |

```
Isolation : read_committed_snapshot_on=1 snapshot_isolation_state=ON
Test event: Id = 900001 (synthetic, does NOT exist — safe to seed)
PHASE 1 — 400 CONCURRENT signups (A:200->cap5, B:200->cap10)
  Class A (cap 5): confirmed=13 offered=0 waitlisted=187 duplicates=0
  Class B (cap 10): confirmed=18 offered=0 waitlisted=182 duplicates=0
  OVERSELL   : 16   (must be 0)
RESULT users=400 A_cap=5 A_conf=13 A_wait=187 B_cap=10 B_conf=18 B_wait=182 oversell=16 dup=0 signup_ms=3293 cancel_ms=113 promoted=0 verdict=FAIL
CLEANUP (Azure existing-DB) — deleting every seeded row for test event 900001
  deleted: signups=387 settings=0 sessions=2 attendees=400 event=1
  identity: reseeded Events back to 1 (pre-run state restored)
  VERIFY remaining for event 900001: signups=0 settings=0 sessions=0 attendees=0 event=0
  CLEANUP VERIFIED — 0 rows remain
VERDICT: FAIL
```

### Root cause — RCSI defeats the count-subquery guard

`TryClaimSeatAsync` claims a seat with a guarded conditional update:

```
UPDATE Sessions SET UpdatedAt = now
WHERE Id = @sid AND ( Capacity IS NULL
   OR (SELECT COUNT(*) FROM MasterClassSignups
        WHERE SessionId = @sid AND Status IN (Confirmed,Offered)) < Capacity )
```

The X-lock on the one `Session` row **does** serialize the racing `UPDATE`s. But under
**RCSI**, the `COUNT(*)` sub-select against `MasterClassSignups` reads from the
**statement's pre-update row-version snapshot**, *not* the latest committed rows. So a
blocked racer, once it acquires the `Session` row lock, re-evaluates `COUNT(*)` against a
**stale snapshot that does not yet include the seats other transactions just committed**,
sees `< Capacity`, and grants a seat anyway → **oversell**. On SQL Express (RCSI off) the
same `COUNT(*)` takes shared locks and reads the latest committed data, so the guard holds
— which is why §3 passes and this does not. The "touch the `Session` row" trick locks the
*Session*, but the capacity decision depends on a snapshot read of a *different* table.

### Remediation (recommended — code change out of scope for this validation task)

Options, in rough order of preference:
1. **Maintain a seat counter on the `Session` row** (e.g. `ConfirmedSeats`) and make the
   guard compare that column to `Capacity` in the *same* row being X-locked
   (`WHERE ConfirmedSeats < Capacity` with `SET ConfirmedSeats += 1`). The decision then
   reads the locked row itself, immune to RCSI snapshot reads. (Counter-drift risk — keep it
   in the same transaction as the signup insert/delete.)
2. **Force the guard read past the snapshot**: run the claim under `SERIALIZABLE` (or read
   the count `WITH (READCOMMITTEDLOCK)` / `UPDLOCK, HOLDLOCK`) so the sub-select takes real
   locks instead of snapshot rows. Re-introduces range-lock contention §218 tried to remove.
3. **Application-level per-session claim lock** for promotions/signups.

The harness now reproduces this deterministically against prod Azure SQL, so any fix can be
re-validated by re-running it (it cleans up after itself).

---

## 0a. PROD AZURE SQL — POST-FIX validation (§219 RCSI fix → OVERSELL = 0) ✅

The §0 oversell is **fixed**. `TryClaimSeatAsync` now reads the capacity `COUNT(*)` on
**SQL Server / Azure SQL** under the table hint **`WITH (UPDLOCK, HOLDLOCK)`** (raw SQL,
since EF Core cannot emit table hints):

```sql
UPDATE [Sessions] SET [UpdatedAt] = @now
WHERE [Id] = @sid AND [EventId] = @ev AND [Type] = 2
  AND ([MasterClassCapacity] IS NULL
       OR (SELECT COUNT(*) FROM [MasterClassSignups] WITH (UPDLOCK, HOLDLOCK)
            WHERE [SessionId] = @sid AND [Status] IN (0,2)) < [MasterClassCapacity])
  -- (+ NOT EXISTS a waitlisted row, also WITH (UPDLOCK,HOLDLOCK), when blockWhenWaitlisted)
```

`UPDLOCK` forces a **locking read of the latest committed rows** (immune to the RCSI
pre-statement snapshot that caused §0); `HOLDLOCK` makes it a **key-range (serializable)
lock on the existing `(EventId, SessionId, Status)` index**, held to commit — so two
concurrent claims **for the same class** serialize and the blocked one re-reads the
now-committed count and correctly sees "full". Claims on *different* classes lock disjoint
key ranges (no global serialization — the operator-rejected approach). **No new column, no
counter, zero drift risk.** The SQLite test path keeps the plain guarded `ExecuteUpdate`
(SQLite has no RCSI and serializes its single write connection). The public API of
`MasterClassSignupService` is unchanged.

**Results — two consecutive POST-FIX prod runs (400 racers, Class A cap 5, Class B cap 10),
same harness / same prod DB / same RCSI=ON as §0:**

| Metric | Run 1 (event 900007) | Run 2 (event 900008) |
|---|---:|---:|
| Class A confirmed (cap **5**) | **5** | **5** |
| Class B confirmed (cap **10**) | **10** | **10** |
| **OVERSELL (must be 0)** | **0** ✅ | **0** ✅ |
| Class A waitlisted | 195 | 195 |
| Class B waitlisted | 190 | 190 |
| Duplicate signups | 0 | 0 |
| Exceptions / deadlock storm | 0 | 0 |
| Phase 1 wall-clock | 5576 ms | 5552 ms |
| Phase 2 (5 concurrent cancels) promoted | 5 distinct | 5 distinct |
| **Cleanup verification** | **0 rows remain** ✅ | **0 rows remain** ✅ |

```
MODE      : AZURE existing-DB (no CREATE/DROP DATABASE; isolated test event)
DB target : tcp:eldk27hub-sql-prodpdrq.database.windows.net,1433 / eldk27hub-db
Edition   : Standard / S2
Isolation : read_committed_snapshot_on=1 snapshot_isolation_state=ON   ← same RCSI as §0
Events    : MAX(Id) currently = 1
Test event: Id = 900007 (synthetic, does NOT exist — safe to seed)   [EventId=1 NEVER touched]
PHASE 1 — 400 CONCURRENT signups (A:200->cap5, B:200->cap10)
  Class A (cap 5): confirmed=5  offered=0 waitlisted=195 duplicates=0
  Class B (cap 10): confirmed=10 offered=0 waitlisted=190 duplicates=0
  OVERSELL   : 0   (must be 0)
PHASE 2 — 5 CONCURRENT cancellations of Class A confirmed seats (promotion race)
  promoted (total): 5   distinct: 5
  Class A after   : confirmed=5 offered=0 waitlisted=190 (was waitlist 195)
RESULT users=400 A_cap=5 A_conf=5 A_wait=195 B_cap=10 B_conf=10 B_wait=190 oversell=0 dup=0 signup_ms=5576 cancel_ms=12089 promoted=5 verdict=PASS
CLEANUP (Azure existing-DB) — deleting every seeded row for test event 900007
  deleted: signups=395 settings=0 sessions=2 attendees=400 event=1
  identity: reseeded Events back to 1 (pre-run state restored)
  VERIFY remaining for event 900007: signups=0 settings=0 sessions=0 attendees=0 event=0
  CLEANUP VERIFIED — 0 rows remain
VERDICT: PASS — §218 optimistic allocation holds on REAL Azure SQL under 400-way concurrency
```

**Verdict: PASS on prod Azure SQL (RCSI ON).** Confirmed == capacity exactly on both
classes, **oversell = 0** across two consecutive runs (was 16 then 21 pre-fix), counts
conserved, no duplicates, promotion still exactly-once on concurrent frees, no exception /
deadlock storm, and **cleanup verified 0 rows remain** each run. Synthetic isolated test
events `Id = 900007` / `900008` (both `> MAX(Id)=1`, aborts if pre-existing); the live event
`EventId = 1` was never read or written.

---

## 1. Setup — which DB target, and why

> **Historical (pre-§223).** The SQL Express run below is the ORIGINAL §221 validation. It
> has since been **superseded** — its RCSI-OFF engine **masked the §222 oversell bug** (§0).
> Per **§223** the **SQL Express path was removed** from the harness; load tests now run
> **only against dev/prod Azure SQL** (see §0a for the authoritative prod result, and the
> "Full prod stress suite" summary above).

| | |
|---|---|
| **DB target** | **SQL Server 2022 Express** on mgmt1 (`.\SQLEXPRESS`, 16.0.1180.1), Windows-integrated auth. This is §221 **option 1** (real local SQL — cleanest, fully isolated, true row-locking + parallel connections). LocalDB was not installed; prod Azure SQL was **not** used, so live event data (EventId=1) was never touched. **(This local path has since been removed per §223 — dev/prod Azure SQL only.)** |
| **Isolation** | Each run **creates a throwaway database** `ceh_loadsim_<guid>`, applies the full EF model via `EnsureCreated()` (same query-translation pipeline as prod Azure SQL), seeds an **isolated test event** (`Code = LOADSIM-<rand>`, low-cap classes, `*@loadsim.test` attendees), runs the sim, then **drops the database** in a `finally` block. Nothing outside that throwaway DB is read or written. |
| **Genuine parallelism** | Every simulated user runs on **its own `DbContext` + its own SQL connection** (connection-string `Max Pool Size` widened to ≥ user count). All tasks block on a shared gate (`TaskCompletionSource`) and are released together, so they genuinely race on real SQL row locks — unlike the §220 shared-connection SQLite test, which serializes its single connection and therefore cannot prove wall-clock concurrency. |
| **Faithful to prod** | The harness `DbContextOptions` mirror prod (`src/CommunityHub/Program.cs`): `EnableRetryOnFailure(maxRetryCount: 6, maxRetryDelay: 30s)`. `MasterClassSignupService.InTxAsync` deliberately runs through `Database.CreateExecutionStrategy()`, so this is the retrying strategy the §218 design relies on. |
| **Harness (rerunnable)** | `tools/CommunityHub.MasterClassLoadSim` (standalone console exe, added to `CommunityHub.sln`). It is **not** an xUnit project, so `dotnet test` never runs it and the normal suite is unaffected. When no SQL Server is reachable it prints a **SKIP** + caveat and exits 0 (never fails a pipeline). Run: `dotnet run -c Release --project tools/CommunityHub.MasterClassLoadSim`. Override the target with env `CEH_LOADSIM_SQL`; class caps / user counts via `CEH_LOADSIM_CAP_A/_B`, `CEH_LOADSIM_USERS_A/_B`. |

## 2. Scenario

- **2 master classes**, deliberately LOW capacity so they go **full**: Class A cap **5**, Class B cap **10**.
- **400 attendees**, split into two distinct pools (200 → A, 200 → B) — each attendee is a separate racer.
- **Phase 1 — 400 concurrent signups**: all 400 `SignUpAsync` calls released simultaneously, racing for the seats.
- **Phase 2 — concurrent cancellations**: every confirmed seat in Class A (5 of them) is given up **at the same time** via concurrent `RemoveAsync`; each freed seat must promote exactly one waitlisted attendee.

## 3. Results (representative run; stable across 3 consecutive runs)

```
DB target : .\SQLEXPRESS (REAL SQL Server)  —  Microsoft SQL Server 2022 (RTM-GDR) 16.0.1180.1
Schema    : EnsureCreated (full model — same translation pipeline as prod Azure SQL)
Seeded    : 1 event, 2 master classes (cap 5 + 10), 400 attendees

PHASE 1 — 400 CONCURRENT signups (A:200->cap5, B:200->cap10)
  wall-clock : 1828 ms for 400 concurrent signups (219/s)
  exceptions : 0
  Class A (cap 5): confirmed=5  offered=0  waitlisted=195  duplicates=0
  Class B (cap 10): confirmed=10 offered=0  waitlisted=190  duplicates=0
  OVERSELL   : 0   (must be 0)
    [PASS] Class A confirmed (5) == capacity (5)
    [PASS] Class B confirmed (10) == capacity (10)
    [PASS] zero oversell of the last seat (got 0)
    [PASS] Class A counts conserved (5+195 == 200)
    [PASS] Class B counts conserved (10+190 == 200)
    [PASS] no duplicate (attendee,session) signups (A=0 B=0)
    [PASS] no exceptions / deadlock storm (0 errors)

PHASE 2 — 5 CONCURRENT cancellations of Class A confirmed seats (promotion race)
  wall-clock      : 12939 ms for 5 concurrent cancellations
  cancel errors   : 0
  promoted (total): 5   distinct: 5
  Class A after   : confirmed=5 offered=0 waitlisted=190 (was waitlist 195)
    [PASS] Class A still exactly capacity after cancellations (5 == 5)
    [PASS] never above capacity (5 <= 5)
    [PASS] every freed seat promoted one waiter (5 == 5)
    [PASS] no double-promote (distinct 5 == 5)
    [PASS] promoted attendees were genuinely from the waitlist
    [PASS] waitlist shrank by exactly the seats freed (190 == 190)
    [PASS] no exceptions during cancellation race (0)

VERDICT: PASS — §218 optimistic allocation holds under 400-way concurrency
```

### The exact numbers

| Metric | Class A | Class B |
|---|---:|---:|
| Attendees racing | 200 | 200 |
| Capacity | 5 | 10 |
| **Confirmed** | **5** | **10** |
| Waitlisted | 195 | 190 |
| Offered | 0 | 0 |
| Rejected (Ok=false) | 0 | 0 |
| Duplicate signups | 0 | 0 |
| **OVERSELL COUNT** | **0** | **0** |

| Phase | Timing (3 runs) |
|---|---|
| 400 concurrent signups | 1828 / 1986 / 1739 ms (~200/s) |
| 5 concurrent cancellations + promotions | 12.9 / 16.0 / 15.7 s |

Promotion-after-cancellation: **5 seats freed → 5 distinct waitlisted attendees promoted**, each exactly once (no double-promote), confirmed count held at the cap of 5 the whole time, waitlist dropped 195 → 190.

## 4. Verdict

**PASS.** Under genuine 400-way wall-clock concurrency on a real SQL Server, the
§218 optimistic atomic seat-claim:

- never oversold the last seat (oversell = **0**, confirmed == capacity exactly, every run);
- kept counts exact and conserved (confirmed + waitlist == attendees), with zero duplicate signups;
- promoted exactly one distinct waitlisted attendee per freed seat on concurrent cancellation, never double-filling a seat and never exceeding capacity;
- raised no unhandled exception and no deadlock storm.

## 5. Findings / notes

1. **Signup path scales cleanly.** 400 concurrent signups completed in **< 2 s with zero
   deadlocks**. The guarded conditional `UPDATE` takes only a brief single-row X-lock on
   the `Session` row, so contenders serialize for microseconds and correctly observe "full"
   — exactly the optimistic behaviour §218 set out to deliver, and a clear win over the old
   serializable-range-lock approach.

2. **The promotion path serializes on the Session row and can deadlock under concurrent
   same-class frees — prod's retry policy absorbs it (correctly, but slowly).** Phase 2
   (5 simultaneous cancellations of the *same* class) took ~13–16 s, not milliseconds. Each
   `RemoveAsync` deletes a confirmed row **and** runs `PromoteNextAsync` (claim-then-pick-head)
   inside one transaction; several of these racing on the same `Session` row produce SQL
   Server deadlocks (error 1205). With `EnableRetryOnFailure` (prod config) the deadlock
   victims are retried with exponential back-off and **every** cancellation still completes
   correctly — proven here (0 errors, 5/5 promoted, no double-promote). **Without** the retry
   strategy an earlier run showed 4 of 5 cancellations throwing — i.e. the §218 promotion
   path's correctness under concurrency *depends on the retry execution strategy being
   configured*, which prod (web + Jobs) does. Real-world cancellations are far rarer and less
   bursty than this stress (5 give-ups landing in the same millisecond on one class), so the
   latency is not a production concern today. If concurrent give-ups ever become common, the
   back-off latency (not correctness) would be the thing to optimise — e.g. a shorter
   first-retry delay, or serialising promotions per class through a lighter app-level lock.

## 6. Reproduce

The harness runs **only against an existing dev/prod Azure SQL database** (§223 — the local
SQL Express path was removed). If `CEH_LOADSIM_AZURE_CS` / `CEH_LOADSIM_TOKEN` are not both
set it prints a clear message and exits 0 (it never falls back to any local/SQLite engine).

```powershell
# dev/prod Azure SQL — run INSIDE the existing DB, isolated synthetic event (default 900001)
$env:AZURE_CONFIG_DIR     = 'C:\work\.azcfg-eldk-deploy'
$env:CEH_LOADSIM_AZURE_CS = '<dev/prod Azure SQL connection string; no auth in the string>'
$env:CEH_LOADSIM_TOKEN    = (az account get-access-token --resource https://database.windows.net/ --query accessToken -o tsv)
dotnet run -c Release --project tools/CommunityHub.MasterClassLoadSim
# Optional: CEH_LOADSIM_EVENTID (synthetic event id, default 900001; aborts if it already exists),
#           CEH_LOADSIM_CAP_A/_B + CEH_LOADSIM_USERS_A/_B (class caps / racer counts).
```

It never CREATE/DROPs a database: it seeds an isolated synthetic event (never `EventId = 1`),
runs the phases, then deletes every row it created in FK-safe order and re-queries to prove
**0 rows remain** — no manual cleanup needed.

```
RESULT users=400 A_cap=5 A_conf=5 A_wait=195 B_cap=10 B_conf=10 B_wait=190 oversell=0 dup=0 signup_ms=5576 cancel_ms=12089 promoted=5 verdict=PASS
```
RESULT users=400 A_cap=5 A_conf=5 A_wait=195 B_cap=10 B_conf=10 B_wait=190 oversell=0 dup=0 signup_ms=1828 promoted=5 verdict=PASS
```
