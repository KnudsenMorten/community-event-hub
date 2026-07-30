# Role flows — what a new person experiences, per role (§240)

> Operator-facing walkthrough, traced from the code on `feat/jul07-roleflow-corrections` (post-§241–§247, 2026-07-07 review).
> For each role: **how they enter → the email timeline → what they see in the hub → what you (organizer) must prepare first.**

## 0. Shared plumbing (applies to every role)

- **One Participant table for every role.** Whatever the entry path, the person ends up as a row in `Participants` with a `ParticipantRole` (Organizer, Speaker, Volunteer, Sponsor, Attendee, Media, EventPartner). Only **Active** participants can sign in.
- **§169 magic link.** Every hub-CTA email carries the recipient's personal `/go/{token}` link (1-year, reusable, hashed at rest, revocable at `/Organizer/WelcomeLinks`). Clicking it signs them in and lands them on the intended page — no password, no PIN. Fallback sign-in: email + PIN code at `/Login`. Sessions persist 365 days ("Remember me" default on, §170).
- **First sign-in — NO interstitial (§248, corrected 2026-07-08).** The `/Welcome` first-sign-in redirect was **RETIRED** (operator 2026-07-07, fewest-clicks rule §249): every arrival — magic-link deep link or hub home — lands **directly on the target page**; the welcome **email** carries the orientation copy. The `/Welcome` page itself stays routable (it still stamps `WelcomeShownAt` on a manual visit) but nothing redirects there any more.
  - **Operator Q (2026-07-07): "/Welcome vs Get-Started?"** The **Get-Started wizard** is the persistent, step-by-step onboarding flow (forms, tasks, progress) the person returns to until done; its steps are entitlement-gated per person (see the speaker note in §3). The old `/Welcome` landing page no longer sits in front of it (§248).
- **Wizard ⇄ My Tasks sync (§173e).** Every Get-Started wizard step is mirrored as a task (`WizardStepTaskSeeder`, `PartyTaskSeeder`, `AttendeeMasterClassTaskSeeder`), and `FormTaskReconciler` syncs both ways on every page load: submit the form → task flips Done; data removed → task reopens.
- **Drop-out cascade (§253 G1–G8, added 2026-07-07/08).** Deactivating ANY participant (grid toggle, Delete, bulk, inline edit) runs ONE cascade (`ParticipantDeactivationService`): party RSVP cancelled (a sponsor's group reservation survives only while the company has another active contact), hotel room-block claim released, open tasks closed (reminders stop), volunteer shifts vacated **with backfill proposals seeded into your allocation queue**, audit entry written. Every headcount, roster and vendor export (hotel / dinner / lunch / swag / party / rota) counts ACTIVE people only. Re-activation restores nothing automatically and tells you which dormant dinner/lunch/swag rows rejoined the counts. Sponsor contacts you deactivate STAY deactivated (the 15-min sync respects the tombstone), and a whole company can be withdrawn from `/Organizer/Sponsors` (hides the public logo, cancels the group RSVP, deactivates the contacts — Zoho untouched per §56).
- **§232 reminder cadence.** `ReminderJob` runs daily 08:00 UTC. The welcome is the day-0 nudge; the **first reminder fires no earlier than 2 weeks after** the task was created, then **biweekly** (14-day windows, one mail per window) **until answered**. Answering (RSVP, selection, form submit) stops the nagging immediately.
- **🔒 RING GATE — current state = SAFE/INTERNAL, rings-only by design.** Every outbound email passes `BrevoEmailSender`, which enforces: the per-feature release ring (all email features sit at **Ring 1**), `Email:RedirectAllTo` (DEV redirects everything to the operator), the global `Email:KillSwitch`, and the `Email:AttendeeWelcomeMaxReleaseRing` ceiling (default Ring1). There is **no `Email:OnlySendTo` allowlist** — recipient safety is the ring model itself (a person only receives mail when their effective ring ≤ the feature's released ring). **Until you raise rings, only Ring-0/Ring-1 test accounts receive any mail.** Everything in the timelines below is written as it will behave *after* go-live.

**Global prerequisites (before anyone arrives, any role):**

| Prerequisite | Where |
|---|---|
| Active edition + event dates/timezone (`dates.preDay/day1/day2`) | `config/event.eldk27.json` |
| Feature gates ON: `outbound-email`, `welcome-email`, `reminder-jobs` (+ per-role gates below) | `/Organizer/Settings` |
| Rings released + test accounts assigned | `/Organizer/Settings` + `/Organizer/ResourceRings` |
| Email templates reviewed per role | `/Organizer/EmailTemplates` |
| CoC + Privacy pages live (URLs are hardcoded to expertslive.dk) | `AcceptFormService.cs` |

---

## 1. Attendee — 2-day ticket (Master Class + main day)

### How they enter
Bought a 2-day ticket in **Zoho Backstage**. `AttendeeBackstageSyncJob` pulls the **full** Backstage dataset (orders + every ticket) **every 10 minutes** (§231; a Zoho webhook + 1-minute drain job makes changes near-real-time, §233). Tickets map to `TicketStatus.TwoDay` vs `Other`. Every 2-day holder without a Participant gets an **Active, login-capable Attendee participant auto-provisioned** (idempotent, matched by email), and then — **automatically, since §241** — the sync **auto-sends the Master-Class selection invite (the de-facto 2-day welcome, §215)** to every eligible-not-invited active 2-day attendee. Both paths do it: the 10-minute pull AND the webhook drain, so a webhook-registered attendee never waits for the timer. Ring-gated like any welcome (`welcome-email` ring + the §217 attendee-welcome ceiling, keyed on the person); the invite stamp is **delivery-gated**, so a ring-dropped invite is auto-retried once rings widen.

### Email timeline
| When | Email | Notes |
|---|---|---|
| **Within 10 min of appearing in Backstage (or ~1 min via webhook)** | `masterclass-selection-invite` — **the de-facto 2-day welcome (§215)** | **AUTOMATIC since §241** (sync + webhook drain). Welcome intro + Party info; primary CTA = Get-Started wizard via magic link; secondary = Master-Class chooser deep link. Once per attendee (`MasterClassInviteSentAt`, stamped only on real delivery). The organizer bulk-send page on `/Organizer/MasterClasses` remains as a manual re-send/backstop only. |
| Next sync run, if still unselected | `pending-master-class-selection` chaser | Sent **once ever** per attendee (dedup key `pendingmc:{email}`), from the 10-min sync job. |
| Immediately on choosing | `masterclass-confirmed` (with .ics) / `masterclass-waitlisted` | Waitlist **promotion is automatic** → `masterclass-promoted`. **Operator Q: "15-min waitlist offers?"** — the `Offered` state is **RESERVED/UNUSED** (no code path ever creates `Status=Offered`); promotion happens directly, **no offers occur**. `WaitlistOfferExpiryJob` was **unscheduled (§252 F2)** — its `[Function]` timer was removed. |
| 2 weeks after task creation, then biweekly | Master-Class selection reminder + Party RSVP reminder (`task-deadline-reminder`) | §232 cadence; each stops the moment they select / RSVP. |
| ~~1 month before the class~~ | ~~`masterclass-month-reminder`~~ | **RETIRED (§244)** — **operator Q: the "daily job" was `MasterClassMonthReminderJob`**; it is no longer scheduled and the template left the catalog ("too confusing"). The confirmed-seat email's full-day calendar invite (§210) is the calendar touchpoint attendees keep. |
| **Ticket reassigned** | `masterclass-reassignment` validation to the new holder | Selection transfers with the ticket; crash-safe marker, retried until delivered (§234). |
| **Ticket cancelled** | `masterclass-cancelled-ticket` — "your ticket was cancelled" (**§243, NEW**) | Soft-cancel: seat released → waitlist promoted + notified, login locked out (§216), all open tasks go quiet — and the 2-day holder is **told** (once per cancellation, `SentReminder`-ledgered on `CancelledAt`; ring-gated via `welcome-email`; undelivered sends retried ≤7 days). A reappearing ticket restores everything, and a LATER cancellation mails again. |

### What they see
- **Nav (minimal):** Home · Get Started · Master Class (chooser) · Waitlist · Master Class Q&A · Games · Party Signup · Policies · Contact Organizers. No hotel/dinner/swag — the ticket covers food; attendees have **no** logistics entitlements.
- **No portal welcome page** — straight to the hub home (Attendee area card + checklist).
- **Get-Started wizard (2 steps, `AttendeeWizardService`):** ① **Master Class selection** (done = a CONFIRMED signup — waitlist doesn't count) → ② **Party signup** (explicit Yes/No, either answer completes it). Both steps exist as tasks (`masterclass-form:{id}`, `party-form:{id}`) and auto-complete/reopen with the data.

### Organizer prerequisites
- `attendee-reconcile` feature ON + **Zoho creds** (`Zoho:BackstagePortalId/EventId`, OAuth in Key Vault) + webhook secret for the push path.
- **Master-Class sessions created with capacities** (deferred item §26i — initial capacities still pending!) checked on `/Organizer/MasterClasses`.
- `welcome-email` + `reminder-jobs` features ON (§252 F5: the ENTIRE Master Class funnel — invite, confirmed, waitlisted, cancelled, promotion, reassignment — rides the ONE `welcome-email` ring; the old `masterclass-invites` key was removed); `Email:AttendeeWelcomeMaxReleaseRing` raised deliberately (phased: Ring1 → Ring2 → Broad; ~1500 inboxes).
- Party feature: nothing to configure beyond templates — the party date/window (9 Feb 2027, 16:00–18:30) is in code/copy.
- **§241: the selection invite (welcome) is fully automatic** once the gates above are open — the sync + webhook welcome every new eligible 2-day attendee within the ring. `/Organizer/MasterClasses` bulk-send is only a manual backstop/re-send.

---

## 2. Attendee — 1-day ticket — **SUSPENDED (§242, operator 2026-07-07)**

> **"1-day ticket holders do NOT get the party invite. They are synced over to CEH, but they do not get any welcome emails or sign-in capability. We might open up for this later."**

### Current behaviour (flag `attendee-1day-access` = OFF, the default)
- **Synced, nothing more.** The 10-minute Backstage sync still mirrors every 1-day ticket (`TicketStatus.Other`) — orders, cancellations, reassignments — so the data is complete and telemetry counts them.
- **No participant provisioning, no `welcome-attendee-1day`, no sign-in, no party task, no reminders.** The §208 provisioning + welcome loops in BOTH sync jobs no-op while the flag is off.
- **Existing 1-day participants are locked out** — the sync's `ReconcileOneDayAccessAsync` sweep deactivates every 1-day-only Attendee login each run (their magic links stop resolving; the party seeder skips inactive participants; the reminder builder skips deactivated logins, so pre-existing party tasks go quiet).
- **2-day holders are completely unaffected** — the sweep never touches an email that holds an active 2-day ticket (§216 owns those logins).

### If/when it re-opens (turn `attendee-1day-access` ON in `/Organizer/Settings`)
Fully reversible: the same sweep **restores** the locked-out 1-day logins (only those whose ticket is still active — a §216 cancellation lockout is never undone), provisioning + the `welcome-attendee-1day` send resume for NEW holders (new-only, ring-gated by `welcome-email` + the attendee ceiling, §219-paced), the party task seeds again and the §232 biweekly party cadence resumes. The (pre-§242) journey then is: minimal attendee nav **without** the Master Class / Waitlist / Q&A entries, **Get-Started wizard = 1 step: Party signup**.

### Organizer prerequisites
None while suspended (the mirror sync rides `attendee-reconcile`, which is already on for 2-day). To re-open: flip `attendee-1day-access` ON, review the `welcome-attendee-1day` template, and raise the ceiling deliberately — this is the largest single audience.

---

## 3. Speaker

### How they enter
**Sessionize → pre-selection queue → active.** `SessionizeImportJob` pulls hourly in **DELTA mode**: new speakers are added, empty bios filled, speaker-edited fields never overwritten — and the scheduled pull **never sends email** (`sendWelcome: false`). Speakers **with** an email are imported directly; **email-less** speakers land in the **pre-selection queue** (`/Organizer/PreselectionQueue`, `Inactive`, §204). **§245: speakers SKIP the Preselected state** — their pre-selection already happened in Sessionize, so a speaker row goes straight **`Inactive → Active` in one step** (the queue shows no Preselect button for speakers, and a bulk Preselect leaves speaker rows untouched); volunteers/media keep the 3-state `Inactive → Preselected → Active` flow. Activation (`ParticipantActivationService.ActivateAndOnboardAsync`) makes them login-capable and triggers onboarding. Sessions import + link in the same pull.

### Email timeline
| When | Email | Notes |
|---|---|---|
| Within 10 min of becoming Active | `welcome-speaker` | `WelcomeReconcileJob` (every 10 min) — once-ever per person (`welcome:{id}` ledger); if ring-dropped it is auto-retried when the ring widens. Magic-link CTA → hub. |
| First sign-in | *(no interstitial — §248)* | The magic link lands them directly on the target page; the welcome email carries the orientation. |
| Task due dates (hotel −30d, dinner/lunch/swag −21d) | `task-deadline-reminder` | One reminder, on the due day. |
| **Weekly** (when there are open questions) | `speaker-question-digest`; step-reset mail if you reopen a step | **§246: cadence is WEEKLY per speaker** (was effectively daily on new questions) — the daily job defers inside each speaker's 7-day quiet window and then sends ONE consolidated digest. Gated by `digest-emails` / `onboarding-step-reset`. |
| Event-driven | session change alerts, graphics-ready, group-photo invite, travel-paid, eval results | Each behind its own feature gate. |

### What they see
- **Nav:** Speaker Onboarding wizard · Speaker Details · My Tasks · My Sessions · My Evaluations · Master Class Q&A (if they run one) · Help Promote / Graphics · Event logistics fold-out (Hotel, Dinner, Lunch, Swag, Travel). **§247: the "Am I ready?" (Speaker Readiness) nav entry is DROPPED** — the readiness rollup at the top of Speaker My Tasks covers it; `/Speaker/Readiness` stays routable (organizer roster + testing) but has no menu entry.
- **Get-Started wizard (up to 10 steps, `SpeakerWizardService`, in order):** ① Calendar email (optional) → ② Speaker details (done only when *the speaker* edits the bio — import doesn't count) → ③ Hotel → ④ Appreciation dinner → ⑤ Swag → ⑥ Lunch → ⑦ Help promote your sessions (manual mark-done) → ⑧ Join Signal groups (manual mark-done) → ⑨ Party sign-up → ⑩ **Code of Conduct & Privacy (always last)**. Steps ③–⑥ appear only if the speaker's entitlement includes them. **Operator Q (2026-07-07): YES — entitlements differ by speaker funding** (per `OrderEntitlements`): an **ELDK-supported speaker** gets hotel + dinner + swag + lunch + travel; a **sponsor-funded (self-funded) speaker** gets **dinner + main-day lunch only**. Both see the same `/Welcome` page for the Speaker role — only the WIZARD steps (and matching logistics tasks) differ. Presentation uploads are **deadline tasks**, not wizard steps (§141/142).
- All steps mirror into My Tasks; logistics tasks carry due dates relative to the event start.

### Organizer prerequisites
- `sessionize-import` ON + **Sessionize endpoint id** set (`/Organizer/SessionizeEndpointSettings`).
- Staff the **pre-selection queue** — imported email-less speakers sit invisible until you activate them; nothing emails a speaker until they are Active.
- `welcome-email`, `reminder-jobs`, `digest-emails` ON; hotel list defined (`/Organizer/Hotels`) for the hotel step's downstream assignment; **speaker deadlines** seeded automatically on their first hub visit; Signal config (`config/signal-groups.eldk27.json`) must list `Speaker` for step ⑧ to appear.
- Entitlement overrides per person (e.g. a self-funded speaker who should get a hotel) via `ParticipantOrderOverride`.

---

## 4. Volunteer

### How they enter
**Self-service:** the public, anonymous **`/volunteer/signup`** form — 3 steps (about you + photo → per-day availability incl. packing/setup days → collaboration agreement). Submission creates a **pending** participant (`IsActive = false`, `QueueSource = VolunteerInterestForm`), per-day availability rows, and notifies the volunteer lead. You approve them in **`/Organizer/PreselectionQueue`** (`Inactive → Preselected → Active`), which enables sign-in and onboarding.

### Email timeline
| When | Email | Notes |
|---|---|---|
| On signup | notification to volunteer lead (not to the volunteer) | |
| Within 10 min of your approval → Active | `welcome-volunteer` (`WelcomeReconcileJob`) | Once-ever; magic-link CTA. |
| First sign-in | *(no interstitial — §248)* | Lands directly on the target page. |
| Task due dates + party biweekly | `task-deadline-reminder` | §232 party cadence; availability/logistics tasks due-date based. |
| On later availability edits | organizer delta-approval queue (no direct mail to volunteer) | §59. |

### What they see
- **Nav:** Get Started · My Onboarding Tasks · My Availability · My Assignments · Supervisor dashboard (supervisors only) · Event logistics fold-out (Hotel, Dinner, Lunch, Swag).
- **Get-Started wizard (`RoleWizardService`):** ① Your profile (done = phone filled) → ② **Complete your day availability** (volunteer-only step) → ③–n entitled logistics in order Hotel → Dinner → Lunch → Swag → Travel (volunteers are entitled to polo, swag, hotel, dinner, main-day lunch) → Signal groups (volunteers get chat + broadcast) → Party sign-up → **CoC & Privacy last**.

### Organizer prerequisites
- Publish the `/volunteer/signup` link; **watch the pre-selection queue** — signups are invisible to the volunteer until you activate them.
- `welcome-email` + `reminder-jobs` ON; hotels defined; volunteer structure/positions (`/Organizer/VolunteerStructure`) + allocation for the later shift work; Signal config listing `Volunteer`.
- Known gap §42: the signup agreement still lacks CoC/Privacy links.

---

## 5. Sponsor contact (incl. exhibitor extras)

### How they enter
**Webshop order → automatic provisioning.** `WooCommercePullJob` (every 15 min) pulls webshop/sponsor orders and creates sponsor companies, orders and contacts; `ErpWebshopReconcileJob` (every 30 min) reconciles e-conomic ERP ↔ webshop contacts; `SponsorZohoProvisionService` creates/links Zoho sponsor + exhibitor records. Contacts arrive with role flags: **Signer**, **Event Coordinator**, **Booth Member**. Package decides the shape: **Silver = digital-only; Gold/Diamond/Platinum = booth (`SponsorInfo.HasBooth`) ⇒ exhibitor extras**. Booth members get polo + main-day lunch entitlements; digital-only contacts get nothing from the sponsor hat.

### Email timeline
| When | Email | Notes |
|---|---|---|
| Within 15 min of appearing | `welcome-sponsor` — **event-coordinator contacts only** (signers-only excluded) | `SponsorWelcomeReconcileJob`; once per coordinator; **blocks until SharePoint upload folders are provisioned** for booth companies. Welcome names their actual role ("event coordinator and signer", …). |
| First sign-in | *(no interstitial — §248)* | Lands directly on the target page. |
| 2 weeks, then biweekly | Party reminder — **stops for the whole company once ANY contact answers** (§228 group reservation) | |
| Event-driven | sponsor reminders, app-game gift reminder, upload notifications | Gated `sponsor-reminders` etc. |

### What they see
- **Nav:** Sponsor Get Started · Sponsor Webshop fold-out (Buy Services, Orders + Linked Contacts) · Company Details · Attendee Telemetry · *(booth members)* Exhibitor & Booth fold-out (Exhibitor Profile, Booth Members, Materials, Banner, Your Booth, Leads/Capture Lead if enabled) · Tasks.
- **Get-Started wizard (`SponsorWizardService`) — company-scoped**, all steps deep-link into `/Sponsor/CompanyDetails`: ① Company Details (website/description) → ② Event Coordinator → ③ Your contacts (from e-conomic; fail-soft if ERP unavailable) → ④ Logos & artwork → *(exhibitors only)* ⑤ Booth members → ⑥ Booth materials → ⑦ **Booth check-in slot (§229**: pick a pre-day arrival window 07:30–15:00 or opt out**)** → ⑧ **Party (§228)** — one group answer; any contact's RSVP (+ head count) completes the step for the whole team.

### Organizer prerequisites
- `sponsor-order-pull`, `sponsor-zoho-provision`, `erp-webshop-reconcile`, `backstage-sync`, `sponsor-welcome` features ON; **WooCommerce + Company Manager + e-conomic creds** (Key Vault; e-conomic live wiring still partial — services report "WouldCreate" until configured).
- **SponsorCompanyId links** on contacts + `SponsorInfo` per company (package/booth tier, booth label) — the package drives which wizard steps exist.
- **SharePoint upload folders provisioned** (event config `sharepoint.*`) — the coordinator welcome deliberately refuses to send for booth companies without them.
- Zoho booth-category ids pinned per tier in `event.eldk27.json`. Never delete a Zoho sponsor/exhibitor — update only.

---

## 6. Media

### How they enter
**Manual add** by an organizer (`/Organizer/Participants` → add/edit, role = Media). No sync feeds this role.

### Email timeline
`welcome-media` within 10 min of the account existing Active (`WelcomeReconcileJob`), once-ever, magic-link CTA (lands directly in the hub — no `/Welcome` interstitial, §248) → task-deadline reminders + biweekly party reminder per §232.

### What they see
Staff-like experience: Get Started · My Tasks · Resources · Hotel/Dinner/Lunch/Swag. **Wizard (`RoleWizardService`):** Profile → entitled logistics (media are entitled to polo, hotel, dinner, both lunches — no swag-gift, no travel) → Signal groups (**broadcast-only** for media) → Party (added for media §206) → CoC & Privacy.

### Organizer prerequisites
Create the participant with the right email **before** the event comms start; hotels defined; Signal config listing `Media` (broadcast link) if you want the Signal step; `welcome-email` ON. Nothing else — no integration to configure.

---

## 7. Event partner

Identical machinery to Media (manual add → `welcome-eventpartner` → RoleWizard), with two differences: entitlements include both lunches + hotel/dinner/polo, and Signal scope gives event partners **chat + broadcast**. Wizard order: Profile → Hotel → Dinner → Lunch → Swag → Signal → Party → CoC & Privacy (entitlement-gated). Prerequisites: manual account, hotels, Signal config listing `EventPartner`, `welcome-email` ON.

---

## 8. Organizer

### How they enter
Manual add by an existing organizer (role = Organizer). No queue, no sync.

### Email timeline
**None.** Organizers get **no welcome email and no portal welcome** (`WelcomeVariants` returns null). They receive only operational mail (digests to info@, alerts) and any task-deadline reminders on their own tasks.

### What they see
- Full nav: Home · Command Center · Dashboard · Telemetry · People / Content / Comms / SoMe / Sponsors / Volunteers / Logistics / Setup hubs · Quizzes · Impersonation Log — plus everything a participant sees.
- **They still have a Get-Started wizard** (`RoleWizardService`): Profile → entitled logistics (organizers get polo, swag, hotel, dinner, both lunches) → Party → CoC & Privacy. Signal step is out of scope for organizers.

### Organizer prerequisites
An existing organizer account (bootstrap/seed) and correct role assignment. Consider Ring0/Ring1 assignment so organizers see gated features first.

---

## 9. Quick reference — first-email trigger per role

| Role | Enters via | First email | Sent by | Automatic? |
|---|---|---|---|---|
| 2-day attendee | Backstage sync (10 min) / webhook (~1 min) | `masterclass-selection-invite` (de-facto welcome §215) | **The sync jobs themselves (§241)** — `/Organizer/MasterClasses` bulk-send is only a manual backstop | **Yes — fully automatic** (delivery-gated, auto-retries when rings widen) |
| 1-day attendee | Backstage sync (10 min) — **mirror only** | **— (SUSPENDED, §242)** — no welcome, no sign-in, no party while `attendee-1day-access` is OFF | — | — (reversible via the flag) |
| Speaker | Sessionize import → pre-selection queue → **you activate** (Inactive → Active in ONE step, §245) | `welcome-speaker` | `WelcomeReconcileJob` (10 min) | Yes, once Active |
| Volunteer | `/volunteer/signup` → **you approve** | `welcome-volunteer` | `WelcomeReconcileJob` (10 min) | Yes, once Active |
| Sponsor coordinator | Webshop order → auto-provision | `welcome-sponsor` (coordinators only) | `SponsorWelcomeReconcileJob` (15 min) | Yes (blocked until SharePoint folders exist for booth companies) |
| Media / Event partner | Manual add | `welcome-media` / `welcome-eventpartner` | `WelcomeReconcileJob` (10 min) | Yes |
| Organizer | Manual add | — (no welcome by design) | — | — |

All of the above are **ring-gated** (rings-only by design — there is no `OnlySendTo` allowlist): until go-live (rings raised), only Ring-0/Ring-1 test accounts receive anything.

---

*Sources traced 2026-07-07: `src/CommunityHub/Forms/{Speaker,Role,Attendee,Sponsor}WizardService.cs`, `WizardStepTaskSeeder.cs`, `Core/Config/{PartyTaskSeeder,AttendeeMasterClassTaskSeeder}.cs`, `Core/Participants/FormTaskReconciler.cs`, `Core/Navigation/NavBuilder.cs`, `Jobs/{AttendeeBackstageSync,WelcomeReconcile,SponsorWelcomeReconcile,Reminder,MasterClassMonthReminder,WaitlistOfferExpiry,SessionizeImport,WooCommercePull,ErpWebshopReconcile,ZohoWebhookDrain}Job.cs`, `Core/Reminders/{AttendeeWelcomeProvisioningService,AttendeeOneDayWelcomeEmailService,WelcomeEmailService,AttendeePartyReminderBuilder,AttendeeMasterClassReminderBuilder}.cs`, `Core/Email/{WelcomeVariants,MasterClassEmailService,EmailTemplateCatalog}.cs`, `Core/Auth/EmailMagicLinkService.cs`, `Core/Entitlements/OrderEntitlements.cs`, `Core/Settings/{FeatureCatalog,Ring}.cs`, `Pages/{Welcome,Index,Go}.cshtml.cs`, `Pages/Volunteer/Signup.cshtml.cs`, `Pages/Organizer/{MasterClasses,PreselectionQueue,SendWelcomeLogin}.cshtml.cs`.*
