# Attendee lifecycle — deep end-to-end test results

**Scope:** validate the full ATTENDEE journey for BOTH ticket types (1-day and 2-day/Master-Class),
covering the just-merged **§206–§210** plus the existing attendee flow: sync/provision → welcome +
§169 magic-link → task seeding → Get-Started stepper → nav/menus → party RSVP → Master-Class
selection + waitlist → 2-week reminders → §210 confirmed-seat invite → §209 reassignment /
cancellation resets → idempotency → headless GUI render.

**Method:** two new test classes, EF in-memory + the REAL shipped email templates + an ephemeral
DataProtection provider (so the §169 magic-link service actually mints grants) + a deterministic
mutable clock + a capturing email sender. Page GUIs are exercised **headlessly** through the
page-models (no browser, no `Start-Process`). Fake names/data only.

- `tests/CommunityHub.Core.Tests/AttendeeLifecycleEndToEndTests.cs` — 19 tests (DB + emails + tasks + reminders + lifecycle)
- `tests/CommunityHub.Web.Tests/AttendeeLifecycleGuiTests.cs` — 9 tests (nav/menus + page-model render)

**Verdict: PASS.** All 28 new tests green; full regression on both projects green; Release build green.
The §206–§210 work behaves as specified. Three low-severity findings (doc/semantic nuances, no
blockers) + explicit residual scale/concurrency risks are listed below.

---

## 1. Scenario-by-scenario results

| # | Scenario | 1-day | 2-day | DB / output asserted |
|---|----------|:-----:|:-----:|----------------------|
| 1 | New attendee sync | PASS | PASS | `Attendee` row: `TicketStatus` (Other / TwoDay), `MirrorState=Active`, `CancelledAt=null`, email lower-cased |
| 2 | Provision login participant | PASS | PASS | `Participant` created: `Role=Attendee`, `IsActive`, `LifecycleState=Active`, email match; re-run creates none |
| 3 | Welcome email + §169 magic link | PASS | PASS¹ | 1-day: `welcome-attendee-1day` sent, subject "Welcome to…", body has Party + 16:00 + `…/go/` magic CTA, `WelcomeWithLoginSentAt` stamped. 2-day: selection invite sent, `MagicLinkGrant` minted, `MasterClassInviteSentAt` stamped |
| 4 | Tasks seeded | PASS | PASS | 1-day: party task ONLY (no MC task). 2-day: `masterclass-form:{pid}` (links `/Attendee`) + `party-form:{pid}`; both `Open`, no due date; idempotent re-seed = 0 |
| 5 | Get-Started stepper | PASS | PASS | 1-day = `[party]`; 2-day = `[masterclass, party]`; routes `/Attendee`, `/Party`; nothing done initially |
| 6 | Nav / menus | PASS | PASS | "Party Sign-up" in MAIN nav (no section) + "Party Signup" under Event-logistics + Get-Started + `/Attendee`; NO management group; no `/Tasks` `/Profile` `/Forms/Hotel` `/Sponsor/*` `/Speaker` |
| 7 | Party: unanswered | PASS | PASS | task `Open`, no `PartyRsvp` row |
| 7 | Party: submit **No** | PASS | PASS | `PartyRsvp.Attending=false`; task `Done` |
| 7 | Party: submit **Yes** | PASS | PASS | `Attending=true`; calendar-invite window resolves (16:00–18:30); page exposes invite button |
| 7 | Party: neither chosen | PASS | PASS | page-model rejects (no default), `SubmittedOk=false`, **no row created** |
| 7 | Party: un-answer reopens | PASS | PASS | removing the row → reconciler reopens task (`Open`, `CompletedAt=null`) |
| 8 | Master-Class select | n/a | PASS | `MasterClassSignup` Confirmed; seat decremented (`Free` drops); MC task → `Done` |
| 8 | Master-Class full → waitlist | n/a | PASS | 2nd attendee `Waitlisted`; `Confirmed=1, Waitlisted=1, Free=0` |
| 9 | §210 confirmed-seat email | n/a | PASS | `.ics` is **09:00–16:00** with `VTIMEZONE` + `DTSTART;TZID=`; body has "Doors open at 08:00"/"breakfast"; subject has "see the calendar invite" |
| 10 | Reminders (2-week, no 1-Dec gate) | PASS | PASS | window 0 fires immediately at welcome; +14d → wk1; STOP once answered (party + MC). 1-day has no MC reminder |
| 11 | §209 reassignment | PASS | PASS | same ticket id, new email on same row, `Active`. 2-day: MC seat KEPT/HELD/transferred (cap 1 proves no double-count → fresh attendee can only waitlist), `MasterClassInviteSentAt` set (validate flag). OLD party `Attending=false` (row kept), NEW holder unanswered |
| 12 | §209 cancellation | PASS | PASS | `MirrorState=Cancelled` + `CancelledAt`. 2-day: MC seat RELEASED → capacity freed (fresh attendee confirms). Party cleared (`Attending=false`) |
| 13 | Idempotency | PASS | PASS | re-run sync/provision/seed/reconcile: `Created=0`, single Participant / signup / RSVP, exactly 2 tasks (2-day) or 1 (1-day), no second welcome |

¹ For 2-day the §169 link is asserted via the minted `MagicLinkGrant` — see Finding F1 for why the
`/go/` URL is not in the selection-invite **body**.

---

## 2. Menus each ticket type sees (validated)

Both ticket types are role `Attendee`, so the menu is the same minimal attendee menu
(`NavBuilder.Build(Attendee)`), ticket-type differences live in the **Get-Started stepper content**,
not the nav:

- **Main nav:** Home `/` · Get Started `/Forms/GetStarted` · Master Class `/Attendee` · Waitlist
  `/Attendee/Waitlist` · Master Class Q&A · Games · **Party Sign-up `/Party`** (prominent, §177).
- **Event-logistics fold-out:** Sessions, **Party Signup `/Party`**, survey results, content pages,
  Policies, Contact.
- **NOT present (server-side gate):** no organizer/management group, no `/Tasks`, `/Profile`,
  `/Forms/Hotel`, `/Sponsor/*`, `/Speaker/*`.
- **Stepper:** 2-day = Master Class → Party; 1-day = Party only.

---

## 3. Findings (honest — recorded even though tests are green)

### F1 — No standalone "welcome" for 2-day holders; the magic link is a grant, not a body URL · **Low / by-design, confirm intent**
A 2-day holder gets **no dedicated welcome email**. Provisioning mints only the login identity; the
first attendee-facing send is the **Master-Class selection invite** (gated by
`Attendee.MasterClassInviteSentAt`). Its visible CTA is the `MyMasterClass?t=` self-service
deep-link, so the §169 `…/go/{token}` magic URL **does not appear in that email body** — instead the
personal `MagicLinkGrant` is minted (durable 1-year one-click sign-in). The 1-day welcome
(`welcome-attendee-1day`) *does* render the `/go/` magic CTA in its body. This matches the code's
design and the existing `MasterClassMagicLinkTests`, but differs from the brief's wording
("2-day = master-class welcome … both carry a §169 1-year magic link"). **Action:** confirm the
operator is happy that a 2-day holder's "welcome" is the selection invite and that their magic link
rides as a grant rather than a visible button.

### F2 — A sync-driven party reset declines the row but does NOT reopen the task · **Low / informational**
`AttendeeTicketSyncService.CancelPartyRsvpAsync` (used by both §209 cancellation and reassignment)
sets `PartyRsvp.Attending=false` + `HeadCount=null` but **keeps the row and its `ParticipantId`**.
`FormTaskReconciler`'s party signal is row **existence** by `ParticipantId`, not `Attending`, so a
sync-declined party row still reads as "answered" → the party **task stays `Done`** (only a user
*removing* the row reopens it). In the modelled flows this is harmless — on cancellation the attendee
is gone; on reassignment the OLD holder is matched by their old email and the NEW holder (new email)
has no row → correctly unanswered. The asymmetry only bites a still-active participant whose own
participant-linked RSVP is force-declined by the sync, which the current flows don't produce. Noted
so the team is aware "task Done" and "Attending=false" can diverge for that edge case. Also note: the
sync does **not** deactivate the login `Participant` on cancellation (only the `Attendee` mirror flips
to `Cancelled`) — fine today, but a cancelled attendee keeps a usable login.

### F3 — Stale class-summary comment on `AttendeeWelcomeProvisioningService` · **Low / doc hygiene**
The class XML summary says *"New Participants are created at `Ring.Ring1`"*, but the code creates them
at `Rings.Default` (Broad/GA) with an inline "RELEASE SAFETY" comment explaining the opposite. The
**behaviour is intentional and correct** (Broad attendees are NOT auto-welcomed until the email
feature is deliberately promoted, preventing a rogue blast to ~1500 holders); only the top-of-class
comment is contradictory and should be corrected to avoid future confusion.

---

## 4. Test counts (before / after)

| Project | Before | New | After | Result |
|---------|-------:|----:|------:|--------|
| CommunityHub.Core.Tests | 1948 | +19 | **1967** | all green |
| CommunityHub.Web.Tests | 410 | +9 | **419** | all green |
| **Total** | **2358** | **+28** | **2386** | **0 failed, 0 skipped** |

`dotnet build -c Release src/CommunityHub` → success. Both `dotnet test` runs → 0 failed.

---

## 5. Residual risks for ~1500-person scale (NOT covered here — be aware)

1. **Concurrency / serializable-isolation is NOT exercised.** The §93/§94 seat race
   (`MasterClassSignupService.InSerializableTxAsync`) is a **no-op on the EF in-memory provider** —
   these tests assert the *logic* of confirm/waitlist/promote/switch, but never the real DB-level
   serializable locking. Simultaneous give-ups/sign-ups against Azure SQL (last-seat contention,
   the promotion cascade) are unverified. Recommend a focused relational/concurrency test before peak.
2. **No relational constraint enforcement.** In-memory EF ignores FK + unique indexes (e.g. the
   `(EventId, SessionizeId)` MC uniqueness, order↔ticket FK). A constraint regression would pass here.
3. **Email ring-gate / kill-switch bypassed.** Tests use a capturing/no-op `IEmailSender`, so actual
   delivery gating is not validated. Because provisioning creates attendees at `Rings.Default`, the
   real welcomes/reminders are **NOT auto-delivered until the email feature is promoted** (F3) —
   whether 1500 inboxes actually receive mail depends on ring/feature config this suite does not test.
4. **Throughput of the hourly full sync + chasers + reminders.** Each run materialises all attendees,
   orders, open tasks, RSVPs and confirmed signups into memory and does per-attendee awaits
   (soft-cancel runs a serializable tx per MC seat). Correct at small N; not load/perf-profiled at
   1500 attendees × the 8 master classes.
5. **Time-boundary cases not traversed:** magic-link 1-year expiry boundary; month-before MC reminder
   window edges; very-late onboarding (per-person 2-week cadence anchor) beyond a couple of windows.
6. **Reassignment chains / rapid re-pulls** (A→B→C on one ticket, reappear-after-cancel loops) are
   covered singly but not as long chains.
