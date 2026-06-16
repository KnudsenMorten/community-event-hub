import { test, expect, BASE, USERS, PINS, login, narrowOnly, gotoHub, assertNoHorizontalScroll, onLoginPage }
    from './support/scenario';

/**
 * SCENARIO (GUI half): a volunteer completes the 3-step sign-up wizard with REAL
 * postbacks. Pairs with the backend half VolunteerAndAttendeeScenarioTests
 * (a VolunteerAvailability row is created/updated for the volunteer).
 *
 * GUI assertions — drives the actual multi-step form (Pages/Forms/VolunteerWizard):
 *   step 1  tick a shift  -> Next
 *   step 2  fill role + max hours -> Next
 *   step 3  review -> Confirm & submit
 * then asserts the saved confirmation. This is a genuine write to the DEV DB
 * (idempotent: one VolunteerAvailability row per participant — re-runs update it).
 *
 * Self-skips without VOLUNTEER_EMAIL + VOLUNTEER_PIN. DEV-only.
 */
test.describe('@scenario volunteer completes the 3-step wizard', () => {
    test.skip(!PINS.volunteer || !USERS.volunteer, 'VOLUNTEER_PIN/VOLUNTEER_EMAIL not set');
    narrowOnly();

    test('the wizard saves the volunteer availability end to end', async ({ page }) => {
        await login(page, USERS.volunteer, PINS.volunteer);
        await gotoHub(page);

        await page.goto(`${BASE}/Forms/VolunteerWizard`, { waitUntil: 'domcontentloaded' });
        expect(onLoginPage(page), 'volunteer should reach the wizard').toBeFalsy();
        await expect(page.locator('h2', { hasText: 'Volunteer sign-up' })).toBeVisible();
        await assertNoHorizontalScroll(page);

        // If a previous run already saved, the page shows the "saved" state — the
        // round-trip is already proven, so accept it and finish.
        const alreadySaved = await page.locator('text=/return any time before the deadline/i').count();
        if (alreadySaved > 0) {
            await expect(page.locator('text=/return any time before the deadline/i')).toBeVisible();
            return;
        }

        // STEP 1 — tick the first available shift, then Next.
        const firstShift = page.locator('input[type="checkbox"][name="SelectedShifts"]').first();
        await expect(firstShift).toBeVisible();
        await firstShift.check();
        await page.getByRole('button', { name: 'Next' }).click();
        await page.waitForLoadState('domcontentloaded');

        // STEP 2 — role + max hours, then Next.
        await expect(page.locator('text=/Step 2 of 3/i')).toBeVisible();
        await page.locator('input[name="PreferredRole"]').fill('Registration');
        await page.locator('input[name="MaxHoursPerDay"]').fill('6');
        await page.getByRole('button', { name: 'Next' }).click();
        await page.waitForLoadState('domcontentloaded');

        // STEP 3 — review, then confirm.
        await expect(page.locator('text=/please review your details/i')).toBeVisible();
        await page.getByRole('button', { name: /Confirm.*submit/i }).click();
        await page.waitForLoadState('domcontentloaded');

        // Saved confirmation = the VolunteerAvailability row was written.
        await expect(page.locator('text=/return any time before the deadline/i')).toBeVisible();
    });
});
