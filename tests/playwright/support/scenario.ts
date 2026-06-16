import { test, expect, Page } from '@playwright/test';

/**
 * Self-contained harness for the CEH SCENARIO simulation suite (scenario-*.spec.ts).
 *
 * These specs drive the GUI through a REAL PIN login (no auth bypass) and assert
 * the role's end-to-end flow. The BACKEND half of each scenario lives in the
 * xUnit suite (tests/CommunityHub.Core.Tests/Scenario/*ScenarioTests.cs); see
 * docs/TESTS.md for the lockstep mapping.
 *
 * This file deliberately duplicates the small login/sweep helpers rather than
 * importing tests/playwright/support/hub.ts, because that shared harness ships
 * on the separate GUI-feature branch (PR #8 / feat/ceh-gui-tests) and is NOT on
 * main yet. When PR #8 merges, these helpers can be collapsed into support/hub.ts
 * (coordination note in docs/TESTS.md).
 *
 * Self-skip: every authenticated describe-block skips when its planted PIN is
 * absent, so `npm test` stays green for anyone without a reachable hub + DEV DB.
 */

export const BASE = process.env.CEH_BASE_URL
    ?? 'https://dev.eldk27.eventhub.expertslive.dk';

/** The narrowest device profile is the strictest layout check; the flows are
 *  viewport-independent, so the authenticated specs run on it only. */
export const NARROW = 'iPhone SE';

/** DEV test users + planted PINs, read from env (kept out of git). */
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

/** Skip a describe-block unless we are on the narrow viewport (avoids burning
 *  single-use planted PINs three times over across device projects). */
export function narrowOnly() {
    test.beforeEach(({ }, testInfo) => {
        test.skip(!testInfo.project.name.includes(NARROW),
            'scenario suite runs on the narrowest viewport only');
    });
}

/**
 * Real PIN login. Mirrors Pages/Login.cshtml:
 *   step 1  RequestPin -> input[name=Email] + "Send my sign-in code"
 *   step 2  VerifyPin  -> input[name=Pin] + "Sign in"
 * Success = the signed-in marker the shared layout renders only when authed.
 */
export async function login(page: Page, email: string, pin: string) {
    await page.goto(`${BASE}/Login`, { waitUntil: 'domcontentloaded' });
    await page.locator('input[name="Email"]').fill(email);
    await page.getByRole('button', { name: /send.*code|email me|request/i }).click();
    const pinInput = page.locator('input[name="Pin"]');
    await expect(pinInput).toBeVisible();
    await pinInput.fill(pin);
    await page.getByRole('button', { name: 'Sign in', exact: true }).click();
    await expect(signedInMarker(page)).toBeVisible({ timeout: 15_000 });
}

export function signedInMarker(page: Page) {
    return page.locator('header .user-tools button.signout, button.signout');
}

export function onLoginPage(page: Page): boolean {
    return new URL(page.url()).pathname.toLowerCase().startsWith('/login');
}

export async function assertNoHorizontalScroll(page: Page) {
    const o = await page.evaluate(() => ({
        scrollWidth: document.documentElement.scrollWidth,
        clientWidth: document.documentElement.clientWidth,
    }));
    expect(o.scrollWidth,
        `horizontal overflow: ${o.scrollWidth}px wide on a ${o.clientWidth}px viewport`)
        .toBeLessThanOrEqual(o.clientWidth + 1);
}

/** Land on the hub front page, clicking through the one-time /Welcome gate. */
export async function gotoHub(page: Page) {
    await page.goto(`${BASE}/`, { waitUntil: 'domcontentloaded' });
    if (new URL(page.url()).pathname.toLowerCase().startsWith('/welcome')) {
        const cont = page.getByRole('button', { name: /take me to my hub|continue/i });
        if (await cont.count() > 0) {
            await cont.first().click();
            await page.waitForURL((u) => !u.pathname.toLowerCase().startsWith('/welcome'));
        }
    }
}

/** A unique-per-run token so per-run artifacts don't collide on re-runs. */
export function runTag(prefix = 'scn'): string {
    return `${prefix}-${Date.now()}`;
}

export { test, expect, Page };
