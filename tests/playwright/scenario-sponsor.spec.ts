import { test, expect, BASE, USERS, PINS, login, narrowOnly, gotoHub, assertNoHorizontalScroll, onLoginPage }
    from './support/scenario';

/**
 * SCENARIO (GUI half): a sponsor contact manages a booth task and views the
 * leads pipeline. Pairs with the backend half SponsorBoothLeadsScenarioTests
 * (DB reflects the toggle; company name resolves to company_name_public; leads
 * exclude Junk).
 *
 * GUI assertions:
 *   - /Sponsor/Tasks lists the company's booth tasks with a Pending group,
 *   - "Mark complete" is a REAL postback that moves a task into the Completed
 *     group (proving the DB write),
 *   - the sponsor leads area is reachable and renders without overflow.
 *
 * Self-skips without SPONSOR_EMAIL + SPONSOR_PIN. DEV-only.
 */
test.describe('@scenario sponsor manages a booth task + views leads', () => {
    test.skip(!PINS.sponsor || !USERS.sponsor, 'SPONSOR_PIN/SPONSOR_EMAIL not set');
    narrowOnly();

    test('sponsor completes a booth task and it moves to Completed', async ({ page }) => {
        await login(page, USERS.sponsor, PINS.sponsor);
        await gotoHub(page);

        await page.goto(`${BASE}/Sponsor/Tasks`, { waitUntil: 'domcontentloaded' });
        expect(onLoginPage(page), 'sponsor should reach /Sponsor/Tasks').toBeFalsy();
        await expect(page.locator('h2', { hasText: 'Sponsor Tasks' })).toBeVisible();
        await assertNoHorizontalScroll(page);

        const markComplete = page.getByRole('button', { name: 'Mark complete' });
        const pendingBefore = await markComplete.count();
        test.skip(pendingBefore === 0, 'sponsor has no pending booth tasks');

        await markComplete.first().click();
        await page.waitForLoadState('domcontentloaded');

        // After the postback there is one fewer pending booth task and the
        // "Completed Tasks (N)" disclosure is present.
        await expect(page.locator('h2', { hasText: 'Sponsor Tasks' })).toBeVisible();
        const pendingAfter = await page.getByRole('button', { name: 'Mark complete' }).count();
        expect(pendingAfter).toBeLessThan(pendingBefore);
        await expect(page.locator('summary', { hasText: /Completed Tasks/i })).toBeVisible();
    });

    test('sponsor leads area renders for the signed-in company', async ({ page }) => {
        await login(page, USERS.sponsor, PINS.sponsor);
        await page.goto(`${BASE}/Sponsor/Leads`, { waitUntil: 'domcontentloaded' });
        expect(onLoginPage(page), 'sponsor should reach /Sponsor/Leads').toBeFalsy();
        await assertNoHorizontalScroll(page);
    });
});
