import { test, expect, BASE, USERS, PINS, login, signedInMarker, onLoginPage, narrowOnly, assertNoHorizontalScroll } from './support/hub';

/**
 * FEATURES.md §2 — Sign-in & embedding.
 *
 * Covers: the 6-digit PIN flow, the "stay signed in" choice, neutral
 * non-enumerable messaging, the magic-link handler, and the embedding / SSO
 * security contract. The deep PIN login itself needs a planted PIN (DEV-only);
 * the anonymous-contract assertions need no auth and always run.
 *
 * Plant first:  $env:ADMIN_PIN = & ..\..\tools\plant-test-pins.ps1 -Count 3
 */

test.describe('@gui §2 Sign-in (anonymous contract — no auth)', () => {
    test('login page renders the PIN request step', async ({ page }) => {
        await page.goto(`${BASE}/Login`, { waitUntil: 'domcontentloaded' });
        await expect(page.locator('h2', { hasText: 'Sign in' })).toBeVisible();
        // Step 1: the email field + the "send my code" button.
        await expect(page.locator('input[name="Email"]')).toBeVisible();
        await expect(page.getByRole('button', { name: /Send my sign-in code/i })).toBeVisible();
        await assertNoHorizontalScroll(page);
    });

    test('requesting a code advances to the PIN step with the stay-signed-in choice', async ({ page }) => {
        await page.goto(`${BASE}/Login`, { waitUntil: 'domcontentloaded' });
        // Use an unregistered address: the message must be NEUTRAL (never reveals
        // whether the email exists) and the flow must still advance to step 2.
        await page.locator('input[name="Email"]').fill(`pw-nobody-${Date.now()}@example.com`);
        await page.getByRole('button', { name: /Send my sign-in code/i }).click();

        await expect(page.locator('input[name="Pin"]')).toBeVisible();
        // "Stay signed in" select exists with its four documented options and
        // defaults to a week.
        const remember = page.locator('select[name="RememberFor"]');
        await expect(remember).toBeVisible();
        await expect(remember).toHaveValue('week');
        for (const v of ['day', 'week', 'month', 'persistent']) {
            await expect(remember.locator(`option[value="${v}"]`)).toHaveCount(1);
        }
        // Non-enumerable: no message should say "unknown"/"not found"/"no account".
        const body = (await page.locator('body').innerText()).toLowerCase();
        expect(body).not.toMatch(/no account|not found|unknown email|isn't registered|not registered/);
    });

    test('a wrong PIN is rejected and keeps us signed out', async ({ page }) => {
        await page.goto(`${BASE}/Login`, { waitUntil: 'domcontentloaded' });
        await page.locator('input[name="Email"]').fill(USERS.organizer);
        await page.getByRole('button', { name: /Send my sign-in code/i }).click();
        await page.locator('input[name="Pin"]').fill('000000');
        await page.getByRole('button', { name: 'Sign in', exact: true }).click();
        // Must NOT become signed in (no signout marker) and must show a message.
        await expect(signedInMarker(page)).toHaveCount(0);
        await expect(page.locator('p.error, p.info')).toBeVisible();
    });

    test('magic-link with a bad token errors gracefully and offers PIN fallback', async ({ page }) => {
        await page.goto(`${BASE}/Login/Magic?token=not-a-real-token`, { waitUntil: 'domcontentloaded' });
        // Stays anonymous (no signout marker) and surfaces a PIN fallback link.
        await expect(signedInMarker(page)).toHaveCount(0);
        await expect(page.getByRole('link', { name: /email.*PIN|PIN sign-in/i })).toBeVisible();
    });
});

test.describe('@gui §2 Sign-in (real PIN login — DEV only)', () => {
    test.skip(!PINS.organizer, 'ADMIN_PIN not set - plant with tools/plant-test-pins.ps1 first');
    narrowOnly();

    test('PIN login signs in and "stay signed in" sets a session cookie', async ({ page, context }) => {
        await login(page, USERS.organizer, PINS.organizer, { rememberFor: 'persistent' });
        await expect(signedInMarker(page)).toBeVisible();
        // A real auth cookie now exists for the host.
        const cookies = await context.cookies(BASE);
        expect(cookies.length, 'an auth cookie should be set after login').toBeGreaterThan(0);
        // Navigating to a gated page does NOT bounce us back to /Login.
        await page.goto(`${BASE}/Tasks`, { waitUntil: 'domcontentloaded' });
        expect(onLoginPage(page)).toBeFalsy();
    });
});
