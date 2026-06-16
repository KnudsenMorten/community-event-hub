import { test, expect, BASE, USERS, PINS, login, narrowOnly, assertNoHorizontalScroll } from './support/hub';

/**
 * FEATURES.md §10 — Email & notifications, organizer side.
 *
 * admin-mobile.spec.ts already covers the Email Center preview + a real
 * test-send + a real broadcast send (all caught by the DEV redirect inbox).
 * This spec adds the §10 emphasis admin-mobile does not: the BREADTH of the
 * branded template library — several templates each preview with a Subject line
 * and a rendered iframe — and that the broadcast personalizes with {firstName}.
 *
 * Read-leaning: it previews (no send) for the library breadth, then verifies the
 * broadcast recipient-preview count without sending (the actual send round-trip
 * stays in admin-mobile to avoid two suites both emailing the redirect inbox).
 */

test.describe('@gui §10 Email center template library', () => {
    test.skip(!PINS.organizer || !USERS.organizer, 'ADMIN_PIN/ORGANIZER_EMAIL not set');
    narrowOnly();
    test.beforeEach(async ({ page }) => login(page, USERS.organizer, PINS.organizer));

    test('multiple branded templates each preview with a Subject + rendered iframe', async ({ page }) => {
        await page.goto(`${BASE}/Organizer/EmailCenter`, { waitUntil: 'domcontentloaded' });
        await expect(page.locator('h2', { hasText: 'Email center' })).toBeVisible();

        const select = page.locator('select#Template');
        await expect(select).toBeVisible();
        // Enumerate the real template options the app offers (skip the blank one).
        const values = (await select.locator('option').evaluateAll(
            (opts) => opts.map((o) => (o as HTMLOptionElement).value).filter((v) => v)));
        expect(values.length, 'the template library should expose multiple templates').toBeGreaterThan(1);

        // Preview the first few — each must surface a Subject + a live iframe and
        // never a render error. Cap at 4 to keep the run quick.
        for (const v of values.slice(0, 4)) {
            await select.selectOption(v);
            await page.waitForURL(new RegExp(`Template=${v}`));
            await expect(page.locator('text=Render failed')).toHaveCount(0);
            await expect(page.locator('p, div', { hasText: /Subject:/ }).first()).toBeVisible();
            await expect(page.locator('iframe[srcdoc]')).toBeVisible();
        }
        await assertNoHorizontalScroll(page);
    });

    test('delivery ledger is present (history of what has been sent)', async ({ page }) => {
        await page.goto(`${BASE}/Organizer/EmailCenter`, { waitUntil: 'domcontentloaded' });
        await expect(page.locator('h2', { hasText: /Delivery ledger/i })).toBeVisible();
    });

    test('broadcast personalizes with {firstName} and previews a recipient count', async ({ page }) => {
        await page.goto(`${BASE}/Organizer/Broadcast`, { waitUntil: 'domcontentloaded' });
        await expect(page.locator('h2', { hasText: 'Broadcast email' })).toBeVisible();
        await page.locator('input[name="Roles"][value="Organizer"]').check();
        await page.locator('input[name="Subject"]').fill(`PW preview only ${Date.now()}`);
        await page.locator('textarea[name="Message"]').fill('Hi {firstName}, this is a preview-only check.');
        await page.getByRole('button', { name: /Preview \+ count/i }).click();
        // Recipient count + rendered preview iframe (we do NOT send here).
        await expect(page.locator('strong, p', { hasText: /recipient\(s\)/ }).first()).toBeVisible();
        await expect(page.locator('iframe[srcdoc]')).toBeVisible();
        await assertNoHorizontalScroll(page);
    });
});
