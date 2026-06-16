# CEH GUI feature suite — coverage matrix

True browser-driven (Playwright) coverage of **every released feature** in
[`docs/FEATURES.md`](../../docs/FEATURES.md). One row per FEATURES.md bullet,
mapped to the spec + test that exercises it in a real browser. "Kind":

- **anon** — no login; runs on DEV + PROD, always.
- **auth** — real PIN login as the owning role (DEV-only; self-skips without a
  planted PIN). All "real send" actions are caught by the DEV redirect inbox.
- **shared** — already owned by the pre-existing `survey-mobile` /
  `admin-mobile` / `portal-mobile` suites; this suite does not duplicate it.

Tests are tagged `@gui` in their title. Run the whole suite with
`npm run test:gui` (or `./run-gui-suite.ps1`); run the no-DB part with
`npm run test:gui:anon`.

| FEATURES.md § | Feature | Spec → test | Kind |
|---|---|---|---|
| **2** Sign-in | One-time 6-digit PIN by email | `feature-signin` → *login page renders the PIN request step*; *requesting a code advances to the PIN step* | anon |
| 2 | Neutral, non-enumerable messaging | `feature-signin` → *requesting a code…* (asserts no "no account / not found") | anon |
| 2 | Wrong-PIN rejected (lockout/rate-limit safeguards) | `feature-signin` → *a wrong PIN is rejected and keeps us signed out* | anon |
| 2 | "Stay signed in" choice (day/week/month/persistent, week default) | `feature-signin` → *…stay-signed-in choice*; *PIN login…sets a session cookie* | anon + auth |
| 2 | Magic-link login | `feature-signin` → *magic-link with a bad token errors gracefully and offers PIN fallback* | anon |
| 2 | Real PIN login end-to-end (session cookie) | `feature-signin` → *PIN login signs in and "stay signed in" sets a session cookie* | auth |
| 2 | Hero render / mobile layout of survey & public pages | `survey-mobile.spec.ts` (pre-existing) | shared |
| **3** Crew & roles | Tailored hub per role (each role lands on its own hub) | `feature-role-hubs` → one test per role (Organizer/Speaker/Volunteer/Sponsor/Attendee) | auth |
| 3 | Per-role data isolation (a role sees only its own areas) | `feature-role-hubs` → *speaker…denied the organizer area*; *attendee…denied organizer + sponsor* | auth |
| 3 | One-time welcome page (once per edition) | `feature-role-hubs` → `gotoHub()` clicks through `/Welcome` once | auth |
| 3 | Activate / deactivate a person in a click | `feature-organizer` → *participants: inline activate/deactivate round-trips* | auth |
| 3 | Link a sponsor contact to their company (SponsorCompanyId set/clear) *(2026-06-14)* | `feature-organizer` → *edit-participant exposes SponsorCompanyId + send-welcome controls* | auth |
| **4** Forms | Appreciation dinner (RSVP + allergies) | `feature-forms` → *appreciation dinner form renders with RSVP + allergy capture* | auth |
| 4 | Hotel (room booking, conditional detail block) | `feature-forms` → *hotel form…room-detail block toggles with NeedsRoom* | auth |
| 4 | Lunch (pre-day + main-day) | `feature-forms` → *lunch form renders the pre-day / main-day choices* | auth |
| 4 | Speaker info (imported session details at a glance) | `feature-forms` → *speaker info form shows the imported Sessionize details read-only* | auth |
| 4 | Swag (polo / jacket / award prefs) | `feature-forms` → *swag form renders polo/gift/badge preferences* | auth |
| 4 | Travel (reimbursement claim → payout task) | `feature-forms` → *travel form reveals the claim block only when requested* | auth |
| 4 | **Volunteer wizard** (single guided multi-step) | `feature-forms` → *the wizard walks step 1 → 2 → 3 via real postbacks and shows a review* | auth |
| 4 | Public no-login volunteer signup (+ honeypot) | `feature-forms` → *the anonymous signup page renders with its required fields + honeypot* | anon |
| 4 | Late-change deadline gating (locked-form message) | covered by Pester `Features.Tests.ps1` (static) + form `.error` lock state surfaces in renders | shared |
| **5** Tasks & reminders | Personal to-do list (own tasks only) | `feature-tasks` → *the personal to-do list renders only this person's tasks* | auth |
| 5 | Tick a task off (complete / reopen) | `feature-tasks` → *ticking a task done and reopening it round-trips* | auth |
| 5 | Speaker deadlines scheduled & surfaced | `feature-tasks` → *the hub front page surfaces pending speaker-deadline tasks* | auth |
| 5 | Reminder engine cadence / never-double-send | Pester `Features.Tests.ps1` (SentReminder key, static) | shared |
| **6** Sessions & surveys | Public 3-step survey (track → rank → level), anonymous, slug-addressed | `feature-surveys` → *the survey page is anonymous, slug-addressed and 3-step*; full wizard in `survey-mobile` | anon |
| 6 | Live results dashboard (KPIs, weighted ranks, per-track, level dist.) | `feature-surveys` → *the results dashboard renders KPIs and the level distribution* | anon |
| 6 | Per-track deep-links | `feature-surveys` → *per-track deep-links jump to the matching track section* | anon |
| 6 | Spam protection (honeypot) on surveys | `survey-mobile` wizard + Pester (static) | shared |
| 6 | Sessionize import (file upload, no network dep) | `feature-organizer` → *speaker + sponsor + import tools render* (file input present) | auth |
| 6 | Manual-create welcome hook (idempotent) *(2026-06-14)* | `feature-organizer` → *edit-participant exposes…send-welcome controls* | auth |
| **7** Sponsors | Sponsor details card alongside task list | `feature-sponsors` → *sponsor engagement details card + linked contacts render* | auth |
| 7 | Company/booth tasks (curated, link-as-button) | `feature-sponsors` → *sponsor tasks list renders with curated, button-style links* | auth |
| 7 | Complete / reopen a sponsor task | `feature-sponsors` → *completing then reopening a sponsor task round-trips* | auth |
| 7 | Always-right public company name (fallback chain) | `feature-sponsors` → details card asserts no `Company {id}` raw fallback | auth |
| 7 | Full sponsor management for organizers (task catalog) | `feature-organizer` → *sponsor admin: task catalog + status dashboard* | auth |
| **8** Sponsor leads | "Your Leads API" page (token shown once + endpoint docs) | `feature-sponsors` → *the "Your Leads API" page shows the token + endpoint docs* | auth |
| 8 | Leads API serves real leads as JSON, junk excluded, 401 on bad token | `admin-mobile.spec.ts` → *sponsor leads API: deterministic token…* | shared |
| 8 | Lead pipeline grid (Reply/Processed/Interest/Ignore/Junk), soft-status | `admin-mobile.spec.ts` → *sponsor leads admin: counters, grid, status action…* | shared |
| 8 | Notification prefs (digest/real-time, skip junk) | `admin-mobile.spec.ts` → *…notification-prefs save round-trip* | shared |
| **9** Attendees | Attendee browser (search, filters, tiles, CSV export) | `feature-organizer` → *attendee browser supports search + CSV export*; `admin-mobile` render | auth |
| 9 | Reconciliation mismatches surfaced | `feature-organizer` → dashboard tiles + organizer Index mismatch table | auth |
| **10** Email | Email Center preview (branded template library breadth) | `feature-email` → *multiple branded templates each preview with a Subject + iframe* | auth |
| 10 | Test-send delivers (caught by DEV redirect) | `admin-mobile.spec.ts` → *email center: test-send delivers* | shared |
| 10 | Delivery ledger / history | `feature-email` → *delivery ledger is present* | auth |
| 10 | Broadcast personalized `{firstName}` + recipient preview | `feature-email` → *broadcast personalizes with {firstName} and previews a count* | auth |
| 10 | Broadcast real send (resilient batch) | `admin-mobile.spec.ts` → *broadcast: preview counts + send to organizer group* | shared |
| **11** Organizer hub | Live dashboard (tiles, pipeline cards) | `feature-organizer` → *dashboard renders live stat tiles* | auth |
| 11 | Practical data grids (inline edit + CSV) | `feature-organizer` → *inline-edit grids (DataGrid + TasksTable)…* | auth |
| 11 | Hotel management | `feature-organizer` sweep + DataGrid rooming-list export | auth |
| 11 | Group photos (register, schedule, calendar invite) | `admin-mobile.spec.ts` → *group photos: register + schedule + send invite* | shared |
| 11 | App game (register sponsor + gift reminder) | `admin-mobile.spec.ts` → *app game: register sponsor + send gift reminder* | shared |
| 11 | Travel payouts (claims + register payout) | `feature-organizer` → *lunch + travel overviews render* | auth |
| 11 | Swag ordering (vendor spreadsheet export) | `feature-organizer` → *swag export offers the vendor spreadsheet download* | auth |
| 11 | Lunch & dinner overviews | `feature-organizer` → *lunch + travel overviews render* | auth |
| 11 | Sponsor admin area (catalog, leads pipeline, status dashboard overdue-first) | `feature-organizer` → *sponsor admin: task catalog + status dashboard*; `admin-mobile` leads | auth + shared |
| 11 | Every organizer page renders (no horizontal overflow) | `feature-organizer` → *full organizer-area sweep* | auth |
| **12** Hosting | `/health`, IaC, scheduled jobs, deploys, public mirror | Pester `Features.Tests.ps1` + infra checks (out of GUI scope) | shared |

## What's intentionally NOT duplicated

The pre-existing `admin-mobile.spec.ts` already does deep, write-path coverage of
Attendees render, Email Center test-send, Broadcast real-send, the leads
admin grid + API, Group photos and App game. This suite **references** those
rather than re-emailing the DEV inbox twice; the new `feature-organizer` /
`feature-email` specs cover the organizer surfaces those suites do not
(Dashboard tiles, Participants toggle, EditParticipant, inline grids, Swag /
Lunch / Travel, Speaker/Sponsor/Import tools, sponsor-admin catalog & dashboard,
template-library breadth). §12 Hosting is infrastructure, verified by the Pester
suite and deploy tooling — not a GUI concern.
