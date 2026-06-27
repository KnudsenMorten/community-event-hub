import { test, expect, BASE, USERS, PINS, login, narrowOnly, onLoginPage }
    from './support/scenario';

/**
 * FEATURE (GUI half): the organizer "Switch user" = REAL impersonation round-trip.
 *
 * The reported defect was that "Switch user" landed on the limited 2-field
 * "Modify on behalf" (/Organizer/EditOnBehalf) form instead of switching INTO
 * the user. This spec asserts the industry-standard behaviour end-to-end:
 *
 *   1. From the Participants grid, "Switch to user" on a non-organizer row
 *      switches INTO that user — lands on their OWN hub (NOT /Organizer/EditOnBehalf,
 *      NOT a 2-field form) and the acting-as banner appears.
 *   2. The organizer now navigates the WHOLE app as that user (the hub renders
 *      the target's view, and the organizer-only Participants grid is no longer
 *      reachable from the acting-as session — no nested impersonation).
 *   3. "Return to organizer" restores the organizer's own session (banner gone,
 *      Participants grid reachable again).
 *
 * Pairs with the backend half (tests/CommunityHub.Web.Tests/
 * SwitchUserImpersonationTests.cs) which asserts the session re-issue, the
 * landing path, the audit trail and the no-nested guard at the handler level.
 *
 * Self-skips without ADMIN_PIN. DEV-only. Mutates the SESSION only (no data
 * writes): it switches in and immediately returns.
 */
test.describe('@feature switch-user is real impersonation', () => {
    test.skip(!PINS.organizer || !USERS.organizer, 'ADMIN_PIN/ORGANIZER_EMAIL not set');
    narrowOnly();

    test('Switch to user enters the user view (not EditOnBehalf) and returns', async ({ page }) => {
        await login(page, USERS.organizer, PINS.organizer);

        // Open the Participants grid (the v2 grid that carries the Switch action).
        await page.goto(`${BASE}/Organizer/Participants`, { waitUntil: 'domcontentloaded' });
        expect(onLoginPage(page), 'organizer should reach the grid').toBeFalsy();
        await expect(page.locator('h1', { hasText: 'Participants' }).first()).toBeVisible();

        // Find a row whose persona is NOT Organizer (so switching is meaningful
        // and "switch to yourself" is not rejected), then click its
        // "Switch to user" button. The grid renders persona in a cell per row.
        const rows = page.locator('table tr', { has: page.getByRole('button', { name: 'Switch to user' }) });
        const rowCount = await rows.count();
        test.skip(rowCount === 0, 'no participant rows with a Switch to user button on this DEV edition');

        let switched = false;
        for (let i = 0; i < rowCount; i++) {
            const row = rows.nth(i);
            const persona = (await row.locator('td').nth(3).innerText()).trim();
            if (/organizer/i.test(persona)) continue; // skip organizer rows
            await row.getByRole('button', { name: 'Switch to user' }).click();
            switched = true;
            break;
        }
        test.skip(!switched, 'only organizer rows present — nothing to switch into');

        // (1) We landed on the user's view, NOT the 2-field EditOnBehalf form.
        await expect
            .poll(() => new URL(page.url()).pathname.toLowerCase())
            .not.toContain('/organizer/editonbehalf');
        await expect(page.locator('h2', { hasText: 'Modify on behalf' })).toHaveCount(0);

        // The acting-as banner is present and offers "Return to organizer".
        const banner = page.locator('[role="alert"]', { hasText: /acting as/i });
        await expect(banner).toBeVisible();
        await expect(page.getByRole('button', { name: 'Return to organizer' })).toBeVisible();

        // (2) No nested impersonation: from the acting-as session the
        // organizer-only grid is NOT reachable (it shows the org-only notice or
        // redirects), so "Switch to user" buttons are not available there.
        await page.goto(`${BASE}/Organizer/Participants`, { waitUntil: 'domcontentloaded' });
        await expect(page.getByRole('button', { name: 'Switch to user' })).toHaveCount(0);

        // (3) Return to organizer restores the real session.
        // Re-open any page that shows the banner, then click Return.
        await page.goto(`${BASE}/`, { waitUntil: 'domcontentloaded' });
        await page.getByRole('button', { name: 'Return to organizer' }).click();

        // Banner gone, and the organizer grid (with Switch buttons) is reachable again.
        await expect(page.locator('[role="alert"]', { hasText: /acting as/i })).toHaveCount(0);
        await page.goto(`${BASE}/Organizer/Participants`, { waitUntil: 'domcontentloaded' });
        await expect(page.getByRole('button', { name: 'Switch to user' }).first()).toBeVisible();
    });
});
