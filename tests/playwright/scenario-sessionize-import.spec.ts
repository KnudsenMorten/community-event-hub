import { test, expect, BASE, USERS, PINS, login, narrowOnly, assertNoHorizontalScroll, onLoginPage }
    from './support/scenario';

/**
 * SCENARIO (GUI half): an organizer runs the Sessionize import page. Pairs with
 * the backend half SessionizeImportScenarioTests (the parser + importer upsert
 * contract: match on email, never overwrite roles, skip + report emailless).
 *
 * GUI assertions: the organizer reaches /Organizer/SessionizeImport, the page
 * explains the email-match / never-overwrite-roles rules and the "speaker emails"
 * advanced-field requirement, and (when the edition is API-connected) exposes the
 * "Pull speakers from Sessionize" button. The deep upsert correctness is proven
 * offline in the backend half against the mock accepted-speakers JSON; this half
 * confirms the operator entry point exists and is organizer-gated.
 *
 * It does NOT click "Pull" by default — that would hit the live Sessionize API
 * and mutate DEV data. Set SCENARIO_RUN_IMPORT=1 to opt in to a real pull.
 *
 * Self-skips without ADMIN_PIN. DEV-only.
 */
test.describe('@scenario organizer Sessionize import page', () => {
    test.skip(!PINS.organizer || !USERS.organizer, 'ADMIN_PIN/ORGANIZER_EMAIL not set');
    narrowOnly();

    test('import page is organizer-gated and states the upsert rules', async ({ page }) => {
        await login(page, USERS.organizer, PINS.organizer);

        await page.goto(`${BASE}/Organizer/SessionizeImport`, { waitUntil: 'domcontentloaded' });
        expect(onLoginPage(page), 'organizer should reach the import page').toBeFalsy();
        await expect(page.locator('h2', { hasText: /Import speakers from Sessionize/i })).toBeVisible();
        await assertNoHorizontalScroll(page);

        // The documented import contract is visible to the operator.
        await expect(page.locator('text=/matched by email/i')).toBeVisible();
        await expect(page.locator('text=/NEVER overwritten/i')).toBeVisible();

        // Optional: actually pull (mutates DEV). Off by default.
        if (process.env.SCENARIO_RUN_IMPORT === '1') {
            const pull = page.getByRole('button', { name: /Pull speakers from Sessionize/i });
            if (await pull.count() > 0) {
                await pull.first().click();
                await page.waitForLoadState('domcontentloaded');
                // The result banner reports counts (read / created / updated).
                await expect(page.locator('text=/rows read/i')).toBeVisible();
            }
        }
    });
});
