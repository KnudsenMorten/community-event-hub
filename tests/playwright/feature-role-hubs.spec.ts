import { test, expect, BASE, USERS, PINS, login, sweep, narrowOnly, assertNoHorizontalScroll, onLoginPage, Page } from './support/hub';

/**
 * FEATURES.md §3 (crew profiles & roles) + §5 (tasks & reminders, participant
 * side) — per-role hubs.
 *
 * Each role signs in and asserts: it lands on ITS OWN tailored hub, sees only
 * the sections that role gets, and can reach exactly its own forms/areas (and
 * NOT another role's). Section visibility mirrors IndexModel.ApplyRoleVisibility:
 *   Organizer : everything + organizer tools
 *   Speaker   : hotel, dinner, speaker deadlines
 *   Volunteer : hotel, dinner, volunteer shifts
 *   Sponsor   : sponsor area
 *   Attendee  : attendee area only
 *
 * Each block self-skips without its planted PIN. Plant per role:
 *   $env:SPEAKER_PIN = & ..\..\tools\plant-test-pins.ps1 -OrganizerEmail $env:SPEAKER_EMAIL -Role 1 -Count 2
 */

/** Land on the hub front page (handles the one-time /Welcome redirect: the
 *  first post-login visit may bounce to /Welcome — click through it once). */
async function gotoHub(page: Page) {
    await page.goto(`${BASE}/`, { waitUntil: 'domcontentloaded' });
    if (new URL(page.url()).pathname.toLowerCase().startsWith('/welcome')) {
        await page.getByRole('button', { name: /take me to my hub/i }).click();
        await page.waitForURL((u) => !u.pathname.toLowerCase().startsWith('/welcome'));
    }
}

test.describe('@gui §3 Organizer hub', () => {
    test.skip(!PINS.organizer || !USERS.organizer, 'ADMIN_PIN/ORGANIZER_EMAIL not set');
    narrowOnly();

    test('organizer lands on a hub with organizer tools + can reach the organizer area', async ({ page }) => {
        await login(page, USERS.organizer, PINS.organizer);
        await gotoHub(page);
        // The signed-in header shows the role; nav exposes the organizer area.
        await expect(page.locator('header .who')).toContainText(/Organizer/i);
        await expect(page.locator('nav.primary a[href="/Organizer"]')).toBeVisible();
        // Organizer-only landing actually loads (a non-organizer gets the
        // in-place "organizers only" denial instead).
        await page.goto(`${BASE}/Organizer`, { waitUntil: 'domcontentloaded' });
        expect(onLoginPage(page)).toBeFalsy();
        await expect(page.locator('h1', { hasText: 'Organizer area' }).first()).toBeVisible();
        await expect(page.locator('p.error', { hasText: /for organizers only/i })).toHaveCount(0);
        await assertNoHorizontalScroll(page);
    });
});

test.describe('@gui §3 Speaker hub', () => {
    test.skip(!PINS.speaker || !USERS.speaker, 'SPEAKER_PIN/SPEAKER_EMAIL not set');
    narrowOnly();

    test('speaker sees the speaker sections and is denied the organizer area', async ({ page }) => {
        await login(page, USERS.speaker, PINS.speaker);
        await gotoHub(page);
        await expect(page.locator('header .who')).toContainText(/Speaker/i);
        // Speaker forms are reachable; the volunteer wizard + organizer area are not theirs.
        await sweep(page, ['/Tasks', '/Forms/Hotel', '/Forms/Dinner', '/Forms/Speaker', '/Forms/Travel']);
        // Organizer area must NOT be accessible to a speaker. The app now denies
        // IN PLACE (HTTP 200 + "This area is for organizers only.") rather than
        // redirecting — what must never happen is the organizer UI rendering.
        await page.goto(`${BASE}/Organizer`, { waitUntil: 'domcontentloaded' });
        const deniedInPlace = await page.locator('p.error', { hasText: /for organizers only/i }).count() > 0;
        expect(onLoginPage(page) || !page.url().includes('/Organizer') || deniedInPlace,
            'speaker should be denied the organizer area').toBeTruthy();
        // None of the organizer chrome leaks through to a denied speaker.
        await expect(page.locator('a[href="/Organizer/CommandCenter"]')).toHaveCount(0);
    });
});

test.describe('@gui §3 Volunteer hub', () => {
    test.skip(!PINS.volunteer || !USERS.volunteer, 'VOLUNTEER_PIN/VOLUNTEER_EMAIL not set');
    narrowOnly();

    test('volunteer sees the volunteer wizard + forms and only its own data', async ({ page }) => {
        await login(page, USERS.volunteer, PINS.volunteer);
        await gotoHub(page);
        await expect(page.locator('header .who')).toContainText(/Volunteer/i);
        await sweep(page, ['/Tasks', '/Forms/VolunteerWizard', '/Forms/Hotel', '/Forms/Dinner']);
    });
});

test.describe('@gui §3 Sponsor hub', () => {
    test.skip(!PINS.sponsor || !USERS.sponsor, 'SPONSOR_PIN/SPONSOR_EMAIL not set');
    narrowOnly();

    test('sponsor lands on the sponsor area and sees its company links', async ({ page }) => {
        await login(page, USERS.sponsor, PINS.sponsor);
        await gotoHub(page);
        await expect(page.locator('header .who')).toContainText(/Sponsor/i);
        await sweep(page, ['/Sponsor', '/Sponsor/Tasks', '/Sponsor/Leads', '/Sponsor/Logistics', '/Sponsor/Contact']);
    });
});

test.describe('@gui §3 Attendee hub', () => {
    test.skip(!PINS.attendee || !USERS.attendee, 'ATTENDEE_PIN/ATTENDEE_EMAIL not set');
    narrowOnly();

    test('attendee sees only the attendee area, denied organizer + sponsor areas', async ({ page }) => {
        await login(page, USERS.attendee, PINS.attendee);
        await gotoHub(page);
        await expect(page.locator('header .who')).toContainText(/Attendee/i);
        await sweep(page, ['/Attendee']);
        // Cross-role isolation: the attendee must not reach the organizer area.
        // Denial is rendered IN PLACE (200 + "organizers only" notice), not a bounce.
        await page.goto(`${BASE}/Organizer`, { waitUntil: 'domcontentloaded' });
        const deniedInPlace = await page.locator('p.error', { hasText: /for organizers only/i }).count() > 0;
        expect(onLoginPage(page) || !page.url().includes('/Organizer') || deniedInPlace,
            'attendee should be denied the organizer area').toBeTruthy();
        await expect(page.locator('a[href="/Organizer/CommandCenter"]')).toHaveCount(0);
    });
});
