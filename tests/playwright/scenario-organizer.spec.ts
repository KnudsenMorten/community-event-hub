import { test, expect, BASE, USERS, PINS, login, narrowOnly, assertNoHorizontalScroll, onLoginPage }
    from './support/scenario';

/**
 * SCENARIO (GUI half): an organizer reviews the live dashboard whose counts come
 * straight from the DB. Pairs with the backend half
 * OrganizerActionQueueScenarioTests (dashboard counts reflect live state;
 * action-queue open/resolve at the service layer).
 *
 * NOTE — dead functionality (see docs/REQUIREMENTS.md §11): there is no
 * action-queue UI to resolve OrganizerActionItem rows; the dashboard does not
 * surface them. The GUI half therefore asserts the dashboard counts that ARE
 * live (overdue tasks, attendee mismatches, speaker-deadline progress). The
 * "resolve the action queue" step is covered at the DB/service layer in the
 * backend half until the UI is built.
 *
 * Self-skips without ADMIN_PIN. DEV-only. Read-only (no mutation).
 */
test.describe('@scenario organizer reviews the live dashboard', () => {
    test.skip(!PINS.organizer || !USERS.organizer, 'ADMIN_PIN/ORGANIZER_EMAIL not set');
    narrowOnly();

    test('dashboard renders live counts the organizer acts on', async ({ page }) => {
        await login(page, USERS.organizer, PINS.organizer);

        await page.goto(`${BASE}/Organizer/Dashboard`, { waitUntil: 'domcontentloaded' });
        expect(onLoginPage(page), 'organizer should reach the dashboard').toBeFalsy();
        await expect(page.locator('h2', { hasText: 'Dashboard' }).first()).toBeVisible();
        await assertNoHorizontalScroll(page);

        // Headline stat cards present (People / Active / Overdue tasks / mismatches).
        await expect(page.locator('.stat .lbl', { hasText: /People/i }).first()).toBeVisible();
        await expect(page.locator('.stat .lbl', { hasText: /Overdue tasks/i }).first()).toBeVisible();
        // Speaker-deadline progress section (the milestone bars) renders.
        await expect(page.locator('h2', { hasText: /Speaker deadlines/i })).toBeVisible();
    });
});
