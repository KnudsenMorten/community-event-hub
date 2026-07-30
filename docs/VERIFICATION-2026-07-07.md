# Verification plan — 2026-07-07 fix program (§224–§236, rerunnable)

> Operator requirement (§237): every fix below must be re-verifiable on demand. Four scenarios are
> MUST-PASS before/after any future change: **(A) no double master-class booking, (B) never book
> beyond capacity, (C) waitlist works end-to-end, (D) emails + Get-Started work for every role.**
> All commands run from the repo root on mgmt1. PROD is the validation target (§235) — prod email
> is Ring-1-gated and Ring 1 = the operator's own accounts.

## 0. The one-stop gate (run all of this, in order)

```powershell
# 1. Unit + integration suites (must be 0 failed; ~2,500 tests)
dotnet test CommunityHub.sln

# 2. Static feature contracts (must be 0 failed)
pwsh -NoProfile -Command "Invoke-Pester tests/Features.Tests.ps1 -Output Detailed"

# 3. Browser GUI suite per role — PROD by default (§235); plants single-use PINs
$env:AZURE_CONFIG_DIR='C:\work\.azcfg-eldk-deploy'
./tests/playwright/run-gui-suite.ps1            # -Target dev for pre-merge

# 4. Master-class concurrency harness (scenario A+B+C at scale) — PROD Azure SQL,
#    ISOLATED synthetic event, never EventId=1. See §2A below for the env vars.
```

---

## 1. MUST-PASS scenario procedures

### A. No DOUBLE master-class booking (one confirmed seat per attendee)

*Invariant:* an attendee can hold **at most one Confirmed** `MasterClassSignup` per edition,
regardless of concurrent clicks, waitlist promotion racing a signup, or ticket reassignment.

- **Automated:** `dotnet test --filter "FullyQualifiedName~MasterClass"` — includes the signup
  service suite + the Wave-2 regression test that re-validates attendee state **inside** the
  UPDLOCK/HOLDLOCK region (the §234 race fix: previously the "already has a seat" check was read
  outside the lock, so a signup racing a promotion could double-confirm).
- **At scale:** the load-sim (below) asserts per-attendee uniqueness in its verify step.
- **Manual (PROD, Ring-1 attendee account):** open two browser tabs on `/Attendee`, pick class X
  in tab 1 and class Y in tab 2, submit both fast. Expected: exactly one Confirmed booking; the
  second attempt shows an **error style** message (fixed: errors no longer render as a green ✓).

### B. Never book beyond capacity (oversell = 0)

*Invariant:* Confirmed count per class ≤ `MasterClassCapacity` under any concurrency. The design
is a **derived count** (no counter column) claimed under a brief `WITH (UPDLOCK, HOLDLOCK)` locking
read — RCSI-immune (this is the §222 prod-validated fix; RCSI is ON in prod).

- **At scale (the authoritative check)** — `tools/CommunityHub.MasterClassLoadSim`, PROD Azure SQL,
  isolated synthetic event, FK-safe cleanup:
  ```powershell
  $env:AZURE_CONFIG_DIR='C:\work\.azcfg-eldk-deploy'
  $env:CEH_LOADSIM_AZURE   = '1'
  $env:CEH_LOADSIM_TOKEN   = az account get-access-token --resource https://database.windows.net/ --query accessToken -o tsv
  $env:CEH_LOADSIM_AZURE_CS= 'Server=tcp:eldk27hub-sql-prodpdrq.database.windows.net,1433;Database=eldk27hub-db;Encrypt=True;'
  $env:CEH_LOADSIM_USERS_A = '400'   # concurrent users; 2026-06-30 prod runs: 400 & 1000 → OVERSELL=0
  $env:CEH_LOADSIM_CAP_A   = '8'     # low caps force full classes
  dotnet run --project tools/CommunityHub.MasterClassLoadSim
  ```
  **PASS = the harness prints OVERSELL=0 and the cleanup verify succeeds.** (Baseline: 0 oversell
  at 400 and at 1000 concurrent, 2026-06-30.)
- **Automated:** the RCSI/UPDLOCK unit suite runs in step 0.1.

### C. Waitlist must be a success

*Invariant chain:* class full → new signup becomes **Waitlisted** (never Confirmed) → a
cancellation/reassignment-release **promotes the first waitlisted** person → they are emailed →
their status shows Confirmed in the hub. (Note: the "Offered/hold" mode was dead code — resolved
in Wave 2; auto-promotion is the one true flow and the waitlist page copy now says exactly that.)

- **Automated:** `dotnet test --filter "FullyQualifiedName~Waitlist|FullyQualifiedName~Promotion"`
  plus the lifecycle e2e suite (`AttendeeLifecycleEndToEndTests`) covering cancel→promote and
  reassign→seat-transfer (seat stays HELD on reassignment — no double-count, no false promotion).
- **At scale:** the load-sim's cancellation phase asserts promotion correctness.
- **Manual (PROD, two Ring-1 attendee accounts):** cap a synthetic test class at 1. Account 1
  books it (Confirmed). Account 2 books it → must show **Waitlisted**. Account 1 cancels →
  account 2 must flip to Confirmed AND receive the promotion email. Then use the organizer
  MasterClasses page to confirm counts match.

### D. Emails must work + Get-Started for all roles

**Emails (per role, PROD, your Ring-1 accounts):**
1. Open `/Organizer/EditParticipant?id=<your test account>` → **"🔄 Reset welcome — send again"**
   (§236). Expected: fresh welcome arrives for that role; body says *"complete the tasks in the
   Get Started flow"*; the CTA button (**Open the Event Hub — Get Started**) signs you in via the
   magic link and lands ON that role's wizard (§226/§212). Repeat per role: speaker, volunteer,
   sponsor, media, event partner, attendee.
2. **Ring-drop honesty (Wave 1a — the go-live-critical fix):** the editor's "welcome already
   sent" indicator must stay FALSE when a send was ring-dropped. Test: temporarily move a test
   account to Ring 2 (`/Organizer/ResourceRings`), reset its welcome → no mail arrives AND the
   participant is NOT stamped as welcomed (check the editor indicator); move it back to Ring 1,
   run reset again → the mail arrives. Before the fix, the drop was recorded as "sent" forever.
3. **Reminder cadence (§232):** reminders must not exist earlier than 14 days after the welcome —
   automated: `dotnet test --filter "FullyQualifiedName~Reminder"` (window-1-first tests).
4. **Reassignment (§230):** rename a synthetic test ticket in Zoho (or via the webhook payload) →
   within 10 min (§231) the OLD account's magic link must stop working, the NEW address gets its
   own welcome/validation mail. Automated: `ReassignmentLoginLifecycleTests` (4 tests).
5. **Webhook burst (§233):** fire 5 webhook POSTs within a minute (different order ids, shared
   secret) → `/Organizer` sync health shows ONE drain-pull; `ZohoOrderSyncRequests` shows 5 rows
   processed by one run. Zoho API is hit at most once per minute.

**Get-Started per role (PROD, Ring-1 accounts, also covered by the GUI suite step 0.3):**
| Role | Route | Must show |
|---|---|---|
| Speaker | /Forms/SpeakerWizard | all steps, Continue targets first open step |
| Volunteer / Media / Event partner | /Forms/GetStarted | role step-set incl. profile + CoC + logistics |
| Sponsor | /Sponsor/GetStarted | details→coordinator→contacts→logos (+ booth-members, booth-materials, **booth-checkin §229** for exhibitors) + **party group step §228** |
| Attendee (2-day) | /Forms/GetStarted | **Master Class step link works** (fixed: /Attendee/Index — the Continue CTA was dead) + party step |
| Attendee (1-day) | /Forms/GetStarted | party step only, no MC nav noise (Wave 3a) |
Each step's card must open its page; completing the step must flip both the wizard card AND the
matching task (two-way reconcile); the §228 sponsor party step must show DONE for a *second*
company contact once the first registered the group.

---

## 2. Fix catalog (what changed, where, and its specific check)

**Shipped PRs #358–#361 (already on prod):**
| Fix | Where | Verify |
|---|---|---|
| §226 welcome wording + Get-Started CTA | templates/emails/* + config/email-templates/* | D.1 + `WelcomeGetStartedDeepLinkTests` |
| §227 per-role party copy | Pages/Party.cshtml(.cs) | open /Party per role |
| §228 sponsor group party | PartyRsvpService (SubmitGroupAsync/GetCompanyReservationAsync), Party page, FormTaskReconciler, reminder builder, SponsorWizardService | `SponsorPartyGroupReservationTests` (5) + manual: 2 contacts, one reservation |
| §229 booth check-in | SponsorInfo + migration SponsorBoothCheckIn, CompanyDetails #booth-checkin, wizard step | sponsor wizard shows the step; answer persists + audits who/when |
| §230 reassignment lockout | AttendeeTicketSyncService (loginAffected both emails) | `ReassignmentLoginLifecycleTests` + D.4 |
| §231 10-min sync | AttendeeBackstageSyncJob timer | Azure portal: function runs every 10 min |
| §232 2-week reminder delay | AttendeeParty/MasterClassReminderBuilder (`daysSince < 14 → skip`) | D.3 |
| §233 webhook coalescing | ZohoOrderWebhook (enqueue-only) + ZohoWebhookDrainJob + ZohoOrderSyncRequest (+migration) | D.5 |
| Task deep-links rendered raw | TaskTextLinkifier accepts relative /routes | /Tasks: every seeded task shows a working button |
| Wizard logistics tasks undated | WizardStepTaskSeeder due dates + backfill | tasks show due dates + overdue badges |
| Data-signal task toggle reverts | _ParticipantTaskRow hides toggle + explains | /Tasks on profile/CoC/party rows |
| MC-step dead link | AttendeeWizardService → /Attendee/Index | D table (attendee row) |
| Decline-confirm apostrophe; onboarding re-open confirm; WelcomeLinks scroll; backfill person picker; ring filter; dashboard empty state; crew intro card; false webshop warning; copy fixes | organizer/crew pages | spot-check each page |
| §235 PROD test target | plant-test-pins -Env / run-gui-suite -Target (default prod) | step 0.3 runs against prod |
| §236 welcome reset | EditParticipant ResetWelcome handler + button | D.1/D.2 |

**Wave 1–3 (branch `fix/jul07-email-safety`, single test pass then deploy):**
| Fix | Where | Verify |
|---|---|---|
| Ring-drops never recorded as sent | IEmailDeliveryOutcome seam in BrevoEmailSender; Welcome*/ReminderEngine/LoggingEmailSender only ledger on real delivery | D.2 + new unit tests (drop ⇒ no stamp; later in-ring send delivers) |
| Ring gate by participant (contact-email override) | ShouldRingDropAsync falls back to EmailContext.ParticipantId | unit test + manual: speaker with override receives mail |
| Open-redirect backslash | Login/Magic/EmailMagicLinkService guards | unit tests reject `/\evil.com`, `//evil.com` |
| IsActive revalidated on 365-day cookie | cookie OnValidatePrincipal | deactivate a signed-in test account → next request signed out |
| PIN enumeration oracle | PIN request path | unknown vs known email indistinguishable (message + rate-limit) |
| Organizer POSTs need IsRealOrganizer | Organizer OnPost sweep | `OrganizerActingAsAuthzTests` extended |
| Duplicate email crashes sync | Attendees (EventId,Email) index → non-unique (migration AttendeeEmailNonUnique) | sync test: 2 tickets/1 email completes; cancelled+active coexists (operator scenario) |
| Cancelled attendees still reminded | reminder builders skip non-Active-mirror attendees | unit test + manual cancel → nagging stops |
| MC seat race under lock | MasterClassSignupService | scenario A |
| Reassignment email observable/retryable | jobs + MasterClassEmailService | log on failure; retry on next run |
| Dead Offered flow resolved | waitlist copy/settings | scenario C |
| Eval-PDF override + ring gate | evaluation notification service | send respects ContactEmailOverride + EmailContext |
| Real calendar invites | IcsCalendarBuilder METHOD:REQUEST + ATTENDEE | invite is acceptable in Outlook/Google |
| Task cross-wiring (volunteer/travel/speaker dupes) | seeder/reconciler | unit tests + /Tasks shows no dupes |
| UX majors + FEATURES.md scrub | per §234 list | GUI suite + read FEATURES annotations |

**Won't-fix by operator decision:** `Email:OnlySendTo` allowlist (rings are the only audience
control; dev uses `RedirectAllTo`).

---

## 3. When to re-run what

- **Before every deploy:** step 0.1 + 0.2 (fast, ~1 min).
- **After every deploy:** step 0.3 (GUI, ~3 min) — per the post-deploy validation rule.
- **After ANY change to master-class/signup/waitlist code or DB tier:** step 0.4 load-sim
  (scenarios A+B+C at scale) on prod with the synthetic event.
- **Before raising email rings (go-live):** ALL of §1.D — especially D.2, which is the guarantee
  that people gated during Ring 1 WILL receive their mail when rings widen.
