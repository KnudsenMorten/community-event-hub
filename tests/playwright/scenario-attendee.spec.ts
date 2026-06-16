import { test, expect, BASE, USERS, PINS, login, narrowOnly, assertNoHorizontalScroll, onLoginPage }
    from './support/scenario';

/**
 * SCENARIO (GUI half): an attendee browses their area (agenda / Master Class
 * status). Pairs with the backend half VolunteerAndAttendeeScenarioTests
 * (the reconciled ticket + booking status that drives the attendee hub).
 *
 * GUI assertions: the attendee reaches /Attendee (not bounced to /Login), the
 * page renders, and there is no horizontal overflow at the narrow viewport.
 * Read-only — the attendee area is reconciled from Zoho, not editable in the hub.
 *
 * Self-skips without ATTENDEE_EMAIL + ATTENDEE_PIN. DEV-only.
 */
test.describe('@scenario attendee browses their area', () => {
    test.skip(!PINS.attendee || !USERS.attendee, 'ATTENDEE_PIN/ATTENDEE_EMAIL not set');
    narrowOnly();

    test('attendee reaches their own area and it renders', async ({ page }) => {
        await login(page, USERS.attendee, PINS.attendee);

        await page.goto(`${BASE}/Attendee`, { waitUntil: 'domcontentloaded' });
        expect(onLoginPage(page), 'attendee should reach /Attendee').toBeFalsy();
        await assertNoHorizontalScroll(page);

        // Isolation: an attendee must NOT reach the organizer area.
        await page.goto(`${BASE}/Organizer/Dashboard`, { waitUntil: 'domcontentloaded' });
        expect(onLoginPage(page) || !page.url().includes('/Organizer'),
            'attendee should be denied the organizer dashboard').toBeTruthy();
    });
});
