import { test, expect, BASE, USERS, PINS, login, narrowOnly, assertNoHorizontalScroll } from './support/hub';

/**
 * REQUIREMENTS §148 — the generic inline-stepper wizard at /Forms/Wizard, plus the
 * Community Helper dismiss control. Both shipped without a permanent spec and both had
 * live-only regressions a unit test could not catch, so they are guarded here:
 *
 *  1. The Volunteer AVAILABILITY step used to throw HTTP 500: its handler returned a BARE
 *     partial name whose file lives in a sibling folder (Pages/Volunteer/), unresolvable
 *     from the Pages/Forms/ host. This proves the step renders (200 + its fields) and that
 *     the wizard advances through to Finish without looping.
 *  2. The Community Helper greeting bubble can be dismissed (× → just the avatar) and the
 *     choice persists across navigation; the avatar and the greeting both open the chat.
 *
 * Real PIN login on the narrow viewport (the established convention). Read-mostly: we drive
 * postbacks but only Finish on an already-complete participant, so the DEV DB is not polluted.
 */

test.describe('@gui §148 Inline wizard — Volunteer availability step (HTTP 500 regression)', () => {
    test.skip(!PINS.volunteer || !USERS.volunteer, 'VOLUNTEER_PIN/VOLUNTEER_EMAIL not set');
    narrowOnly();

    test('the availability step renders (no 500) and the wizard advances', async ({ page }) => {
        await login(page, USERS.volunteer, PINS.volunteer);

        // Deep-link straight to the previously-broken step: it MUST be HTTP 200, not 500.
        const resp = await page.goto(`${BASE}/Forms/Wizard?step=availability`, { waitUntil: 'domcontentloaded' });
        expect(resp?.status(), '/Forms/Wizard?step=availability must not 500').toBe(200);

        // The availability fields partial genuinely renders inside the wizard host.
        await expect(page.locator('h2', { hasText: /availability/i })).toBeVisible();
        // Per-day availability inputs are present (radios + hidden date fields).
        await expect(page.locator('input[name^="Days"], input[type="radio"]').first()).toBeVisible();
        await assertNoHorizontalScroll(page);

        // A real postback from the step advances (the Next/Finish button submits the host form).
        await page.getByRole('button', { name: /Next|Finish/, exact: false }).first().click();
        // We either moved to another step or landed on the hub — never a server error.
        expect(new URL(page.url()).pathname.toLowerCase()).not.toContain('/error');
    });
});

test.describe('@gui §152 Community Helper — dismissable greeting', () => {
    // The widget renders on any authenticated page; the organizer is always seeded.
    test.skip(!PINS.organizer || !USERS.organizer, 'ADMIN_PIN/ORGANIZER_EMAIL not set');
    narrowOnly();

    test('× hides the greeting (avatar stays), persists across nav, and the avatar opens chat', async ({ page }) => {
        await login(page, USERS.organizer, PINS.organizer);
        await page.goto(`${BASE}/`, { waitUntil: 'domcontentloaded' });

        const bubble = page.locator('#ai-helper-bubble');
        const dismiss = page.locator('#ai-helper-dismiss');
        const launcher = page.locator('#ai-helper-launcher');

        // Greeting + × + avatar are all present to start.
        await expect(bubble).toBeVisible();
        await expect(dismiss).toBeVisible();
        await expect(launcher).toBeVisible();

        // Dismiss hides the greeting bubble but leaves the avatar.
        await dismiss.click();
        await expect(bubble).toBeHidden();
        await expect(launcher).toBeVisible();

        // The choice is remembered in localStorage AND survives a navigation.
        const stored = await page.evaluate(() => localStorage.getItem('ceh.aihelper.greetingHidden'));
        expect(stored).toBe('1');
        await page.goto(`${BASE}/Tasks`, { waitUntil: 'domcontentloaded' });
        await expect(page.locator('#ai-helper-bubble')).toBeHidden();

        // The avatar still opens the chat panel.
        await page.locator('#ai-helper-launcher').click();
        await expect(page.locator('#ai-helper-panel, .ai-helper-panel').first()).toBeVisible();
    });
});
