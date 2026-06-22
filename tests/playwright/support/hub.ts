import { test, expect, Page, APIRequestContext } from '@playwright/test';

/**
 * Shared harness for the CEH GUI suite (page objects + helpers).
 *
 * Design goals (see tests/playwright/README.md "GUI feature suite"):
 *  - One real PIN login flow, reused by every authenticated spec — no auth
 *    bypass, no injected cookies. PINs are planted by tools/plant-test-pins.ps1
 *    immediately before a run (single-use, ~14 min lifetime, future-dated so
 *    they win the "newest redeemable PIN" race).
 *  - Every authenticated describe-block SELF-SKIPS when its credentials are
 *    absent, so plain `npm test` stays green for a contributor with no DEV DB.
 *  - DEV-only and read-leaning: any "real send" a spec triggers is caught by
 *    the DEV `Email:RedirectAllTo` inbox, never a real recipient.
 */

export const BASE = process.env.CEH_BASE_URL
    ?? 'https://dev.eldk27.eventhub.expertslive.dk';

/** The narrowest device profile is the strictest layout check; the flows are
 *  viewport-independent, so the authenticated specs run on it only (matching
 *  the established admin/portal convention). */
export const NARROW = 'iPhone SE';

/** Canonical DEV test users (CLAUDE.md "test users"). Emails are read from env
 *  so they can be overridden / kept out of git; sensible DEV defaults match the
 *  documented canonical accounts. */
export const USERS = {
    organizer: process.env.ORGANIZER_EMAIL ?? 'mok@expertslive.dk',
    speaker:   process.env.SPEAKER_EMAIL   ?? '',
    volunteer: process.env.VOLUNTEER_EMAIL ?? '',
    attendee:  process.env.ATTENDEE_EMAIL  ?? '',
    sponsor:   process.env.SPONSOR_EMAIL   ?? '',
};

export const PINS = {
    organizer: process.env.ADMIN_PIN     ?? '',
    speaker:   process.env.SPEAKER_PIN   ?? '',
    volunteer: process.env.VOLUNTEER_PIN ?? '',
    attendee:  process.env.ATTENDEE_PIN  ?? '',
    sponsor:   process.env.SPONSOR_PIN   ?? '',
};

/** Skip a whole describe-block unless we are on the narrow viewport. Keeps the
 *  authenticated flows from burning planted PINs three times over. */
export function narrowOnly() {
    test.beforeEach(({ }, testInfo) => {
        test.skip(!testInfo.project.name.includes(NARROW),
            'GUI feature suite runs on the narrowest viewport only');
    });
}

/**
 * Real PIN login. Mirrors the exact markup of Pages/Login.cshtml:
 *   step 1  form asp-page-handler="RequestPin"  -> input[name=Email] + input[name=RememberMe] (checkbox) + "Send my sign-in code"
 *   step 2  form asp-page-handler="VerifyPin"   -> input[name=Pin] + hidden RememberMe + "Sign in"
 * Success is detected by the signed-in marker the shared layout renders only
 * when authenticated: header .user-tools button.signout.
 */
export async function login(
    page: Page, email: string, pin: string,
    opts: { rememberMe?: boolean } = {},
) {
    await page.goto(`${BASE}/Login`, { waitUntil: 'domcontentloaded' });
    await page.locator('input[name="Email"]').fill(email);
    if (opts.rememberMe) {
        await page.locator('input[name="RememberMe"]').check();
    }
    await page.getByRole('button', { name: /send.*code|email me|request/i }).click();

    const pinInput = page.locator('input[name="Pin"]');
    await expect(pinInput).toBeVisible();
    await pinInput.fill(pin);
    await page.getByRole('button', { name: 'Sign in', exact: true }).click();
    await expect(signedInMarker(page)).toBeVisible({ timeout: 15_000 });
}

/** The element the shared layout renders only when a session is present. */
export function signedInMarker(page: Page) {
    return page.locator('header .user-tools button.signout, button.signout');
}

/** True when the browser is sitting on the login card (anonymous / bounced). */
export function onLoginPage(page: Page): boolean {
    return new URL(page.url()).pathname.toLowerCase().startsWith('/login');
}

/** Assert no horizontal overflow on the body (the most common mobile bug). */
export async function assertNoHorizontalScroll(page: Page) {
    const o = await page.evaluate(() => ({
        scrollWidth: document.documentElement.scrollWidth,
        clientWidth: document.documentElement.clientWidth,
    }));
    expect(o.scrollWidth,
        `horizontal overflow: ${o.scrollWidth}px wide on a ${o.clientWidth}px viewport`)
        .toBeLessThanOrEqual(o.clientWidth + 1);
}

/**
 * GET each path; assert HTTP 200, that we did not bounce to /Login (i.e. the
 * role is actually allowed here), and no horizontal overflow. Read-only.
 * Collects ALL failures so one run names every broken page.
 */
export async function sweep(page: Page, paths: string[]) {
    const failures: string[] = [];
    for (const path of paths) {
        const resp = await page.goto(`${BASE}${path}`, { waitUntil: 'domcontentloaded' });
        if (!resp || resp.status() !== 200) {
            failures.push(`${path}: HTTP ${resp?.status()}`);
            continue;
        }
        if (onLoginPage(page)) {
            failures.push(`${path}: redirected to login (role not allowed?)`);
            continue;
        }
        const o = await page.evaluate(() => ({
            scrollWidth: document.documentElement.scrollWidth,
            clientWidth: document.documentElement.clientWidth,
        }));
        if (o.scrollWidth > o.clientWidth + 1) {
            failures.push(`${path}: ${o.scrollWidth}px wide on a ${o.clientWidth}px viewport`);
        }
    }
    expect(failures, '\n' + failures.join('\n')).toEqual([]);
}

/** A unique-per-run token so per-subject dedup never makes a re-run "0 sent"
 *  and idempotent-named test artifacts don't collide. */
export function runTag(prefix = 'pw'): string {
    return `${prefix}-${Date.now()}`;
}

// ---------------------------------------------------------------------------
// Comprehensive post-deploy validation helpers (comprehensive-validation.spec.ts)
// ---------------------------------------------------------------------------

/** The two CEH environments, selectable via TARGET=DEV|PROD (default DEV). */
export const ENVS = {
    DEV:  process.env.CEH_DEV_URL  ?? 'https://dev.eldk27.eventhub.expertslive.dk',
    PROD: process.env.CEH_PROD_URL ?? 'https://eldk27.eventhub.expertslive.dk',
};

/** Resolve the base URL the validator runs against. CEH_BASE_URL wins (a single
 *  explicit target — e.g. a slot URL during a deploy); otherwise TARGET picks
 *  DEV/PROD; default DEV (DEV is the documented test target — CLAUDE.md rule 6). */
export function targetBase(): string {
    if (process.env.CEH_BASE_URL) return process.env.CEH_BASE_URL;
    const t = process.env.TARGET?.toUpperCase();
    return t === 'PROD' ? ENVS.PROD : ENVS.DEV;
}

/** Attach a page-error / console-error collector. Returns the array (mutated
 *  as errors arrive). Filters benign noise (favicon, ad-blocked beacons) so a
 *  failure means a real script/render error on the page. */
export function collectPageErrors(page: Page): string[] {
    const errors: string[] = [];
    const benign = /favicon|gtag|googletag|doubleclick|analytics|net::ERR_BLOCKED/i;
    page.on('console', (m) => {
        if (m.type() === 'error' && !benign.test(m.text())) errors.push(`console.error: ${m.text()}`);
    });
    page.on('pageerror', (e) => {
        if (!benign.test(String(e))) errors.push(`pageerror: ${e.message ?? e}`);
    });
    return errors;
}

/** A page is "alive" when it has the shared landmarks and non-trivial body text
 *  — i.e. not a blank/dead page even when it renders an honest empty state.
 *  Returns the list of problems (empty == healthy). */
export async function pageHealth(page: Page): Promise<string[]> {
    const problems: string[] = [];
    const checks = await page.evaluate(() => {
        const hasMain   = !!document.querySelector('main, [role="main"]');
        const hasHeader = !!document.querySelector('header, [role="banner"]');
        const hasFooter = !!document.querySelector('footer, [role="contentinfo"]');
        const bodyText  = (document.body?.innerText ?? '').replace(/\s+/g, ' ').trim();
        const root = document.documentElement;
        return {
            hasMain, hasHeader, hasFooter,
            textLen: bodyText.length,
            scrollWidth: root.scrollWidth,
            clientWidth: root.clientWidth,
            lang: document.documentElement.lang,
        };
    });
    // Universal landmarks the shared layout renders on EVERY page (signed in or
    // not). The primary <nav> is signed-in-only, so it is NOT required here —
    // the authed sweep exercises it implicitly.
    if (!checks.hasMain)   problems.push('no <main> landmark');
    if (!checks.hasHeader) problems.push('no <header>/banner landmark');
    if (!checks.hasFooter) problems.push('no <footer>/contentinfo landmark');
    if (!checks.lang)      problems.push('no <html lang> attribute');
    // A genuinely dead page renders almost nothing; an honest empty state still
    // renders its heading + the empty-state copy (well over 40 chars).
    if (checks.textLen < 40) problems.push(`body has only ${checks.textLen} chars of text (blank/dead page?)`);
    if (checks.scrollWidth > checks.clientWidth + 1) {
        problems.push(`horizontal overflow: ${checks.scrollWidth}px on a ${checks.clientWidth}px viewport`);
    }
    return problems;
}

export { test, expect, Page, APIRequestContext };
