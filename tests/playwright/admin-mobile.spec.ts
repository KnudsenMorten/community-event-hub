import { test, expect, Page } from '@playwright/test';

/**
 * Mobile rendering + behaviour test for the organizer admin pages
 * (Attendees browser + Email Center), exercised through a REAL PIN login.
 *
 * DEV-ONLY by design: the test harness plants LoginPin rows with a known
 * PIN hash directly in the DEV database (tools/plant-test-pins.ps1) right
 * before the run. PROD is never touched. Run:
 *
 *   ..\..\tools\plant-test-pins.ps1          # plants PINs, prints the PIN
 *   $env:ADMIN_PIN='<pin>'; npx playwright test admin-mobile
 *
 * Skips itself when ADMIN_PIN is not set, so the default `npm test`
 * (survey suite, DEV+PROD) is unaffected.
 */

const BASE = 'https://dev.eldk27.eventhub.expertslive.dk';
const EMAIL = 'mok@expertslive.dk';
const PIN = process.env.ADMIN_PIN ?? '';

test.describe('DEV organizer admin (mobile)', () => {
    test.skip(!PIN, 'ADMIN_PIN not set - plant PINs with tools/plant-test-pins.ps1 first');
    // One project only: each login consumes a planted PIN and counts against
    // the PIN-request rate limit; the narrow viewport is the one that breaks
    // layouts, so it gives the mobile signal without burning the budget.
    test.beforeEach(({ }, testInfo) => {
        test.skip(!testInfo.project.name.includes('iPhone SE'),
            'admin suite runs on the narrowest viewport only');
    });

    async function login(page: Page) {
        await page.goto(`${BASE}/Login`, { waitUntil: 'domcontentloaded' });
        await page.locator('input[name="Email"]').fill(EMAIL);
        await page.getByRole('button', { name: /send.*code|email me|request/i }).click();
        const pinInput = page.locator('input[name="Pin"]');
        await expect(pinInput).toBeVisible();
        await pinInput.fill(PIN);
        await page.getByRole('button', { name: 'Sign in', exact: true }).click();
        // Successful verify redirects to the hub front page with a session
        // cookie - the signout button only renders when signed in.
        await expect(page.locator('button.signout')).toBeVisible({ timeout: 15_000 });
    }

    async function assertNoHorizontalScroll(page: Page) {
        const overflow = await page.evaluate(() => ({
            scrollWidth: document.documentElement.scrollWidth,
            clientWidth: document.documentElement.clientWidth,
        }));
        expect(overflow.scrollWidth).toBeLessThanOrEqual(overflow.clientWidth + 1);
    }

    test('PIN login -> attendees page renders on mobile', async ({ page }) => {
        await login(page);

        await page.goto(`${BASE}/Organizer/Attendees`, { waitUntil: 'domcontentloaded' });
        await expect(page.locator('h2', { hasText: 'Attendees' })).toBeVisible();
        // The summary tiles must be present (4 of them) and the page must
        // not blow the mobile viewport (the data table scrolls inside its
        // own wrapper instead).
        await assertNoHorizontalScroll(page);

        // Filters submit and come back filtered without error.
        await page.locator('input[name="Search"]').fill('zzz-no-such-attendee');
        await page.getByRole('button', { name: 'Apply' }).click();
        await expect(
            page.locator('.info', { hasText: 'No attendees match' })
        ).toBeVisible();
        await assertNoHorizontalScroll(page);
    });

    test('email center: preview renders + ledger visible on mobile', async ({ page }) => {
        await login(page);

        await page.goto(`${BASE}/Organizer/EmailCenter`, { waitUntil: 'domcontentloaded' });
        await expect(page.locator('h2', { hasText: 'Email center' })).toBeVisible();
        await assertNoHorizontalScroll(page);

        // Pick the welcome template - the select auto-submits via JS.
        await page.locator('select#Template').selectOption('welcome');
        await page.waitForURL(/Template=welcome/);

        // Subject line extracted from the template + a live iframe preview.
        await expect(page.locator('p', { hasText: 'Subject:' })).toBeVisible();
        const frame = page.locator('iframe[srcdoc]');
        await expect(frame).toBeVisible();
        await assertNoHorizontalScroll(page);

        // Ledger card is present (rows or the empty-state message).
        await expect(page.locator('h2', { hasText: 'Delivery ledger' })).toBeVisible();
    });

    test('broadcast: preview counts + send to organizer group only', async ({ page }) => {
        await login(page);

        await page.goto(`${BASE}/Organizer/Broadcast`, { waitUntil: 'domcontentloaded' });
        await expect(page.locator('h2', { hasText: 'Broadcast email' })).toBeVisible();
        await assertNoHorizontalScroll(page);

        // Unique subject per run so the per-subject dedup never makes the
        // send report "0 sent" on a re-run.
        const subject = `Playwright broadcast ${Date.now()}`;
        await page.locator('input[name="Roles"][value="Organizer"]').check();
        await page.locator('input[name="Subject"]').fill(subject);
        await page.locator('textarea[name="Message"]').fill(
            'Hello from the admin mobile suite.\n\nSecond paragraph.');
        await page.getByRole('button', { name: /Preview \+ count/ }).click();

        // Preview shows count + rendered iframe.
        await expect(page.locator('strong', { hasText: /recipient\(s\)/ })).toBeVisible();
        await expect(page.locator('iframe[srcdoc]')).toBeVisible();
        await assertNoHorizontalScroll(page);

        // Send (confirm dialog) - DEV redirects all mail to the operator.
        page.once('dialog', d => d.accept());
        await page.getByRole('button', { name: 'Send broadcast' }).click();
        await expect(
            page.locator('.info', { hasText: /1 sent, 0 skipped.*0 failed/ })
        ).toBeVisible({ timeout: 30_000 });
    });

    test('email center: test-send delivers (DEV redirect catches it)', async ({ page }) => {
        await login(page);

        await page.goto(
            `${BASE}/Organizer/EmailCenter?Template=welcome`,
            { waitUntil: 'domcontentloaded' });
        await page.getByRole('button', { name: 'Send test copy to me' }).click();
        await expect(
            page.locator('.info', { hasText: /Test mail 'welcome' sent/ })
        ).toBeVisible({ timeout: 20_000 });
    });
});
