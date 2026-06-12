# Playwright mobile-rendering tests

True browser-driven tests for the CEH pages. Complements the fast
Pester smoke test (`../Survey-Mobile.Tests.ps1`) by actually rendering
the pages in headless Chromium / WebKit at mobile viewports.

Three suites:

| Suite | File | Targets | Auth |
|---|---|---|---|
| Survey (public) | `survey-mobile.spec.ts` | DEV + PROD | none |
| Organizer admin | `admin-mobile.spec.ts` | DEV only | real PIN login (see below) |
| Sponsor / speaker / volunteer / attendee portals | `portal-mobile.spec.ts` | DEV only | real PIN login per role |

## What the survey suite covers

- Hero h1 fully visible at iPhone 13, Pixel 5, iPhone SE — catches the
  vertical-clip regression we hit on 2026-06-10.
- No horizontal scroll on the body (most common mobile-layout bug).
- All 3 step photos return HTTP 200.
- Topbar with event-site button is visible.
- Footer with CEH credit + Submit-a-bug link is visible.
- **End-to-end wizard flow**: pick a track → rank 3 topics → pick a
  level → submit button enables. Does NOT actually submit (avoids
  polluting the DB on each run).
- Rank buttons hit Apple HIG-ish 44 × 28 tap-target size.
- **Public pages sweep** — every page reachable without a login
  (`/`, `/Contributors`, `/volunteer/signup`,
  `/survey/eldk27-topics/results`) renders HTTP 200 with no horizontal
  overflow, on both DEV and PROD.

## What the admin suite covers

`admin-mobile.spec.ts` logs in to **DEV** as the operator organizer via a
real email + PIN login (no auth bypass, no cookie injection) and then:

- Attendees page renders on mobile (search box + grid visible).
- Email Center: template preview renders, send-ledger visible.
- Broadcast: recipient preview counts, then a real send to the
  organizer group only (DEV redirects all mail to the operator inbox).
- Sponsor leads admin: live counters, grid, a lead status action and
  notification-prefs save round-trip.
- Sponsor leads API: a deterministic per-sponsor token serves the
  seeded `pwtest-*` lead and excludes the junk-status one.
- Group photos: register a company, schedule the slot, send the
  calendar invite (stable ICS UID).
- App game: register a sponsor + send the gift reminder.
- **Mobile sweep**: visits every organizer page and asserts no
  horizontal overflow on the body (the v1.2.8 run caught 3 real bugs).
- Email Center test-send delivers end to end (caught by the DEV
  redirect).

It is DEV-only and runs only on the iPhone SE project (narrowest
viewport = strictest layout check; the flows themselves are
viewport-independent). Without `ADMIN_PIN` in the environment the
whole suite self-skips, so plain `npm test` stays green for anyone
without DB access.

### Running the admin suite

PIN logins are single-use and PINs are stored hashed (PBKDF2), so the
suite needs freshly planted rows. `tools/plant-test-pins.ps1` inserts
future-dated single-use PIN rows for the operator organizer directly
into the DEV DB (requires az CLI login + DEV SQL firewall access) and
prints the clear-text PIN:

```powershell
cd tests\playwright
$pin = & ..\..\tools\plant-test-pins.ps1 -Count 9   # one row per login the run will do
$env:ADMIN_PIN = $pin
npx playwright test admin-mobile --reporter=list
```

Notes:

- Plant at least as many rows as the run performs logins (each test
  that logs in consumes one). 9 covers the current suite.
- Rows expire after ~14 minutes — plant immediately before running.
- Test data conventions in the DEV DB: seeded `SponsorLeads` rows
  `pwtest-001`/`pwtest-002`, an `AppGameParticipation` "PW Game
  Sponsor" for company 10, and `GroupPhotoRegistrations` named
  `PW Photo Co <ts>` which accumulate per run (clean old ones via SQL
  when they get noisy).
- All outbound email in DEV is redirected to the operator inbox
  (`Email__RedirectAllTo`), so the send tests are safe to run.

## What the portal suite covers

`portal-mobile.spec.ts` is the participant-facing counterpart of the
organizer sweep: it logs in as a **sponsor contact** (ParticipantRole 4),
a **speaker** (Role 1), a **volunteer** (Role 3) and an **attendee**
(Role 5) via the same real PIN flow and sweeps their areas (`/Sponsor*`;
the logged-in hub front page + `/Tasks` + every `/Forms/*` form the
role sees; `/Attendee`) asserting HTTP 200, no bounce back to `/Login`,
and no horizontal overflow. Read-only — it never submits forms. Its
first run caught a real overflow on `/Sponsor/Leads` (unwrapped
endpoint + key tables).

`plant-test-pins.ps1 -Role` selects which participant role the PIN rows
are planted for. Each describe-block self-skips when its env vars are
missing:

```powershell
cd tests\playwright
$env:SPONSOR_EMAIL  = '<sponsor-contact-email>'        # any active Role-4 contact in DEV
$env:SPONSOR_PIN    = & ..\..\tools\plant-test-pins.ps1 -OrganizerEmail $env:SPONSOR_EMAIL -Role 4 -Count 2
$env:SPEAKER_EMAIL  = '<speaker-email>'                # any active Role-1 participant in DEV
$env:SPEAKER_PIN    = & ..\..\tools\plant-test-pins.ps1 -OrganizerEmail $env:SPEAKER_EMAIL -Role 1 -Count 2
$env:VOLUNTEER_EMAIL = '<volunteer-email>'             # any active Role-3 participant in DEV
$env:VOLUNTEER_PIN   = & ..\..\tools\plant-test-pins.ps1 -OrganizerEmail $env:VOLUNTEER_EMAIL -Role 3 -Count 2
$env:ATTENDEE_EMAIL = '<attendee-email>'               # any active Role-5 participant in DEV
$env:ATTENDEE_PIN   = & ..\..\tools\plant-test-pins.ps1 -OrganizerEmail $env:ATTENDEE_EMAIL -Role 5 -Count 2
npx playwright test portal-mobile --reporter=list
```

## One-time setup

```bash
cd tests/playwright
npm install
npm run install:browsers   # downloads headless Chromium + WebKit (~250 MB)
```

## Running

```bash
# Both DEV + PROD across 3 device profiles (default)
npm test

# DEV only
npm run test:dev

# PROD only
npm run test:prod

# Watch the browser (headed mode)
npm run test:headed

# Interactive Playwright UI -- best for debugging a failure
npm run test:ui

# After a run, open the HTML report (screenshots + traces)
npm run report
```

## Adding more devices / viewports

Edit `playwright.config.ts` → `projects:` array. See Playwright's
device list for built-ins: <https://playwright.dev/docs/emulation#devices>.

## CI integration (future)

A GitHub Action could run `npm test` on every PR; failure blocks merge.
Not wired up yet — wire it when DEV+PROD URLs stabilise and we're
confident the spec isn't flaky.
