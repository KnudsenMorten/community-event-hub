import { test, expect, BASE, USERS, PINS, login, narrowOnly, onLoginPage }
    from './support/scenario';

/**
 * FEATURE (GUI half): /Organizer/Settings RENDERS FOR A SIGNED-IN ORGANIZER.
 *
 * 🔒 WHY THIS EXISTS — §707.3 (2026-07-30). The §700 Batch B / §705.3b rework made this the most
 * heavily rewritten page in the app (per-role ring UI, one card per group, a row per mail), and it
 * shipped to PROD with NO coverage of any kind: no spec loaded it, and the anonymous post-deploy
 * validation cannot — an anonymous request redirects to /Login *before* the page model is
 * constructed, so a DI or query failure INSIDE the page is structurally invisible to it. That is
 * exactly the §687.9 incident, where every probe passed while /Sponsor/Tasks returned 500 for every
 * real sponsor for 20 minutes.
 *
 * It also guards the specific hazard the ring rework created: `EmailTemplateRingService.GetAllAsync`
 * builds a dictionary over the ring rows, and a template can now hold SEVERAL rows (one per role plus
 * the all-roles row). Keying that on TemplateKey alone throws a duplicate-key exception and 500s the
 * whole page — which only becomes reachable once a per-role ring actually exists, as it now does.
 *
 * Self-skips without ADMIN_PIN. DEV-only. READ-ONLY: it loads and reads, and writes nothing.
 */
test.describe('@feature organizer settings page renders', () => {
    test.skip(!PINS.organizer || !USERS.organizer, 'ADMIN_PIN/ORGANIZER_EMAIL not set');
    narrowOnly();

    test('/Organizer/Settings loads, renders its mail rings, and logs no console error', async ({ page }) => {
        const consoleErrors: string[] = [];
        page.on('console', m => { if (m.type() === 'error') consoleErrors.push(m.text()); });
        page.on('pageerror', e => consoleErrors.push(String(e)));

        await login(page, USERS.organizer, PINS.organizer);

        const res = await page.goto(`${BASE}/Organizer/Settings`, { waitUntil: 'domcontentloaded' });

        // A 500 here is the whole point of the spec — say so plainly when it happens.
        expect(res?.status(), '/Organizer/Settings must not return a server error').toBeLessThan(400);
        expect(onLoginPage(page), 'a signed-in organizer must not be bounced to /Login').toBeFalsy();

        // The page actually rendered its own content, not a generic error card.
        await expect(page.locator('h1').first()).toBeVisible();

        // The e-mail ring section is the part §705 rewrote: it must list real mails. Any one of the
        // shipped template keys proves the registry→page path resolved rather than silently emptying.
        const body = page.locator('body');
        await expect(body).toContainText(/welcome/i);

        // 🔒 The §326bx guard, asserted in the GUI: a mail whose send site is RingExempt must be shown
        // as "always sent", never offered a ring control it cannot honour. §707.3 found
        // masterclass-cancelled doing exactly that on PROD.
        await expect(body).toContainText(/always sent/i);

        expect(consoleErrors, `console errors on /Organizer/Settings: ${consoleErrors.join(' | ')}`)
            .toHaveLength(0);
    });
});
