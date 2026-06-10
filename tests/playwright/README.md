# Playwright mobile-rendering tests

True browser-driven tests for the CEH survey pages. Complements the
fast Pester smoke test (`../Survey-Mobile.Tests.ps1`) by actually
rendering the page in headless Chromium / WebKit at mobile viewports.

## What it covers

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
