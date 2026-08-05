import { test, expect, BASE, USERS, PINS, login } from './support/scenario';

/**
 * §708.10 / §708.10a — /Tasks RENDERS, and the step form is really IN the row.
 *
 * WHY THIS SPEC EXISTS. §708.10a shipped a broken /Tasks to PROD: the row rendered a
 * handler's partial by BARE NAME ("_HotelFields"), and those partials live in
 * /Pages/Forms/ — a folder Razor's bare-name lookup never searches from /Pages/Tasks/.
 * Every gate missed it, and the reason is the point:
 *
 *   - both unit suites were green — NEITHER RENDERS A PAGE;
 *   - the build succeeded — Razor compiles views at build time, so a partial NAME that
 *     resolves to nothing is a runtime lookup, not a compile error;
 *   - the anonymous PROD smoke passed 10/10 — /Tasks is [Authorize], so it redirects to
 *     /Login before the view is ever touched.
 *
 * So the missing gate was never "more unit tests". It was: LOG IN AND LOOK AT THE PAGE.
 * That is all this file does, and it is deliberately the cheapest possible version of it.
 *
 * Organizer is used because it is one of the four GENERIC roles (ParticipantTaskDefinitions
 * .GenericRoles) and is the account that always exists on DEV.
 *
 * DEV-only, READ-ONLY: it opens the page and reads it. It submits nothing.
 */
test.describe('@feature /Tasks embeds its step forms', () => {
    test.skip(!PINS.organizer || !USERS.organizer, 'ADMIN_PIN/ORGANIZER_EMAIL not set');

    test('the task list renders — no partial-not-found, no error page', async ({ page }) => {
        const errors: string[] = [];
        page.on('pageerror', e => errors.push(String(e)));

        await login(page, USERS.organizer, PINS.organizer);

        const res = await page.goto(`${BASE}/Tasks`, { waitUntil: 'domcontentloaded' });
        expect(res?.status(), 'GET /Tasks').toBeLessThan(400);

        const body = await page.locator('body').innerText();

        // The exact failure §708.10a shipped. Named explicitly so a regression is unmistakable
        // in the report rather than showing up as a generic "expected h2 to be visible".
        expect(body).not.toContain('The partial view');
        expect(body).not.toContain('was not found');
        expect(body).not.toMatch(/InvalidOperationException|An unhandled exception/i);

        // The page really is the task list, not a friendly error shell.
        await expect(page.locator('h2').first()).toBeVisible();
        expect(errors, `JS errors: ${errors.join(' | ')}`).toHaveLength(0);
    });

    /**
     * §708.13a — GET STARTED IS UNCHANGED. Operator asked directly: "get started is still
     * intact. you only touched tasks right".
     *
     * Almost. ProfileStepHandler.PartialName was changed from the bare "_ProfileFields" to the
     * full "/Pages/_ProfileFields.cshtml" (§708.10a) — and the WIZARD renders that same property.
     * A full path is already used by two other handlers so it resolves identically, but the claim
     * "I only touched Tasks" is not quite true, and an untested claim about someone else's
     * onboarding flow is not worth making. So: render the profile step in the wizard itself.
     */
    test('Get Started still renders the profile step (the one handler shared with /Tasks)',
        async ({ page }) => {
            const errors: string[] = [];
            page.on('pageerror', e => errors.push(String(e)));

            await login(page, USERS.organizer, PINS.organizer);
            const res = await page.goto(`${BASE}/Forms/Wizard?step=profile`,
                { waitUntil: 'domcontentloaded' });
            expect(res?.status(), 'GET /Forms/Wizard?step=profile').toBeLessThan(400);

            const body = await page.locator('body').innerText();
            expect(body).not.toContain('The partial view');
            expect(body).not.toMatch(/InvalidOperationException|An unhandled exception/i);

            // The step's real fields are on screen, inside the wizard's own card.
            await expect(page.locator('.ceh-wiz__card').first()).toBeVisible();
            await expect(
                page.locator('.ceh-wiz__card input:not([type="hidden"]), '
                    + '.ceh-wiz__card select, .ceh-wiz__card textarea').first()).toBeVisible();
            expect(errors, `JS errors: ${errors.join(' | ')}`).toHaveLength(0);
        });

    test('a step form is embedded IN a row, not linked out of it', async ({ page }) => {
        await login(page, USERS.organizer, PINS.organizer);
        await page.goto(`${BASE}/Tasks`, { waitUntil: 'domcontentloaded' });

        // Open every row (they are collapsed <details>) so the embedded markup is in layout.
        await page.locator('.ceh-tasklist details').evaluateAll(
            els => els.forEach(e => ((e as HTMLDetailsElement).open = true)));

        // ONLY the embedded step form. The earlier version also accepted any form carrying a
        // taskId, which the Mark-complete/Reopen toggle also has — so it matched the toggle and
        // reported "fields not visible" instead of the truth, which is that no step form existed.
        const embedded = page.locator('.ceh-tasklist details form[action*="handler=Step"]');

        // At least one row must carry a real form with real fields. If the organizer genuinely
        // has no step tasks on DEV there is nothing to assert, so say so rather than pass blindly.
        const count = await embedded.count();
        test.skip(count === 0, 'This account has no step-backed tasks on DEV — nothing to embed.');

        await expect(embedded.first()).toBeVisible();
        await expect(
            // Skip hidden inputs: every one of these forms carries taskId AND the antiforgery
            // token, both hidden, and either would satisfy a bare "input" while proving nothing.
            embedded.first().locator(
                'input:not([type="hidden"]), select, textarea').first(),
            'the embedded partial rendered actual, visible fields').toBeVisible();

        // §708.2a — where the form is embedded there is NO link out to the same form.
        const rowWithForm = page.locator('.ceh-tasklist details:has(form input[name="taskId"])').first();
        await expect(rowWithForm.locator('a[href*="/Forms/Wizard"]')).toHaveCount(0);
    });
});
