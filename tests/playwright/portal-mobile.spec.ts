import { test, expect, Page } from '@playwright/test';

/**
 * Mobile sweep for the SPONSOR and ATTENDEE portals, exercised through a
 * REAL PIN login (no auth bypass) - the participant-facing counterpart of
 * the organizer sweep in admin-mobile.spec.ts.
 *
 * DEV-ONLY by design. Plant PINs first (single-use, ~14 min lifetime):
 *
 *   # sponsor contact (ParticipantRole 4) of the seeded sponsor company
 *   $env:SPONSOR_EMAIL = '<sponsor-contact-email>'
 *   $env:SPONSOR_PIN   = & ..\..\tools\plant-test-pins.ps1 -OrganizerEmail $env:SPONSOR_EMAIL -Role 4 -Count 2
 *
 *   # attendee (ParticipantRole 5)
 *   $env:ATTENDEE_EMAIL = '<attendee-email>'
 *   $env:ATTENDEE_PIN   = & ..\..\tools\plant-test-pins.ps1 -OrganizerEmail $env:ATTENDEE_EMAIL -Role 5 -Count 2
 *
 *   npx playwright test portal-mobile --reporter=list
 *
 * Each block self-skips when its env vars are missing, so plain `npm test`
 * is unaffected. Sweeps are read-only: GET each page, assert HTTP 200 +
 * no horizontal overflow at the narrow viewport.
 */

const BASE = 'https://dev.eldk27.eventhub.expertslive.dk';

async function login(page: Page, email: string, pin: string) {
    await page.goto(`${BASE}/Login`, { waitUntil: 'domcontentloaded' });
    await page.locator('input[name="Email"]').fill(email);
    await page.getByRole('button', { name: /send.*code|email me|request/i }).click();
    const pinInput = page.locator('input[name="Pin"]');
    await expect(pinInput).toBeVisible();
    await pinInput.fill(pin);
    await page.getByRole('button', { name: 'Sign in', exact: true }).click();
    await expect(page.locator('button.signout')).toBeVisible({ timeout: 15_000 });
}

async function sweep(page: Page, paths: string[]) {
    const failures: string[] = [];
    for (const path of paths) {
        const resp = await page.goto(`${BASE}${path}`, { waitUntil: 'domcontentloaded' });
        if (!resp || resp.status() !== 200) {
            failures.push(`${path}: HTTP ${resp?.status()}`);
            continue;
        }
        // A page that bounces an unauthorized role back to /Login "passes"
        // the HTTP check, so also assert we stayed on the page we asked for.
        if (new URL(page.url()).pathname.toLowerCase().startsWith('/login')) {
            failures.push(`${path}: redirected to login (role not allowed?)`);
            continue;
        }
        const overflow = await page.evaluate(() => ({
            scrollWidth: document.documentElement.scrollWidth,
            clientWidth: document.documentElement.clientWidth,
        }));
        if (overflow.scrollWidth > overflow.clientWidth + 1) {
            failures.push(`${path}: ${overflow.scrollWidth}px wide on a ${overflow.clientWidth}px viewport`);
        }
    }
    expect(failures, failures.join('\n')).toEqual([]);
}

test.describe('DEV sponsor portal (mobile)', () => {
    const EMAIL = process.env.SPONSOR_EMAIL ?? '';
    const PIN = process.env.SPONSOR_PIN ?? '';
    test.skip(!EMAIL || !PIN,
        'SPONSOR_EMAIL/SPONSOR_PIN not set - plant with tools/plant-test-pins.ps1 -Role 4');
    test.beforeEach(({ }, testInfo) => {
        test.skip(!testInfo.project.name.includes('iPhone SE'),
            'portal sweep runs on the narrowest viewport only');
    });

    test('mobile sweep: every sponsor page renders without horizontal overflow', async ({ page }) => {
        await login(page, EMAIL, PIN);
        await sweep(page, [
            '/Sponsor', '/Sponsor/Tasks', '/Sponsor/Leads',
            '/Sponsor/Logistics', '/Sponsor/Contact',
        ]);
    });
});

test.describe('DEV attendee portal (mobile)', () => {
    const EMAIL = process.env.ATTENDEE_EMAIL ?? '';
    const PIN = process.env.ATTENDEE_PIN ?? '';
    test.skip(!EMAIL || !PIN,
        'ATTENDEE_EMAIL/ATTENDEE_PIN not set - plant with tools/plant-test-pins.ps1 -Role 5');
    test.beforeEach(({ }, testInfo) => {
        test.skip(!testInfo.project.name.includes('iPhone SE'),
            'portal sweep runs on the narrowest viewport only');
    });

    test('mobile sweep: attendee page renders without horizontal overflow', async ({ page }) => {
        await login(page, EMAIL, PIN);
        await sweep(page, ['/Attendee']);
    });
});
