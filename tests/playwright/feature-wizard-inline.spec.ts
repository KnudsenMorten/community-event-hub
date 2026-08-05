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

        // A real postback from the step advances (the wizard's forward button is
        // now labelled "Save & next"; the last step says "Finish").
        await page.getByRole('button', { name: /save & next|next|finish/i, exact: false }).first().click();
        // We either moved to another step or landed on the hub — never a server error.
        expect(new URL(page.url()).pathname.toLowerCase()).not.toContain('/error');
    });
});

/**
 * REQUIREMENTS §680 — the WELCOME step (step 1, every role).
 *
 * 🔒 This is the gate the §708.10a incident says must exist. Both unit suites are green on
 * the welcome step and neither RENDERS IT: the copy is a file that has to be packaged into
 * the bundle, resolved at runtime and passed through Markdig, and the partial is declared by
 * path. Every one of those can be right in a unit test and wrong in Azure.
 */
test.describe('@gui §680 Inline wizard — the welcome step renders for real', () => {
    test.skip(!PINS.volunteer || !USERS.volunteer, 'VOLUNTEER_PIN/VOLUNTEER_EMAIL not set');
    narrowOnly();

    test('step=welcome is 200, shows the role copy, and leaks no unresolved token', async ({ page }) => {
        await login(page, USERS.volunteer, PINS.volunteer);

        const resp = await page.goto(`${BASE}/Forms/Wizard?step=welcome`, { waitUntil: 'domcontentloaded' });
        expect(resp?.status(), '/Forms/Wizard?step=welcome must not 500').toBe(200);

        // The step's own card heading (from the resx), not the page title.
        await expect(page.locator('h2', { hasText: /^welcome$/i })).toBeVisible();

        // The CONFIG copy actually reached the page: this line is in every role's file, and it
        // only appears if the .md was packaged, found and rendered.
        const card = page.locator('.ceh-welcome');
        await expect(card).toBeVisible();
        await expect(card).toContainText(/what you will find here/i);
        // Markdown was RENDERED, not dumped as literal asterisks.
        await expect(card.locator('li').first()).toBeVisible();

        // No unresolved placeholder ever reaches a participant (§688).
        await expect(card).not.toContainText('{{');
        // The volunteer's welcome greets a person, not a blank.
        await expect(card).toContainText(/^\s*Hi\s+\S/m);

        await assertNoHorizontalScroll(page);
    });

    test('the welcome is the FIRST chip on the rail and the wizard advances out of it', async ({ page }) => {
        await login(page, USERS.volunteer, PINS.volunteer);
        await page.goto(`${BASE}/Forms/Wizard?step=welcome`, { waitUntil: 'domcontentloaded' });

        // Step 1 of N — the welcome is first in the plan, for every role.
        await expect(page.locator('.ceh-wiz__rail .ceh-wiz__chip').first()).toContainText(/welcome/i);

        // It stores nothing, so "Save & next" must simply move on — never re-render itself,
        // never error. (The step is Done: true, so it can never be the thing blocking anyone.)
        await page.getByRole('button', { name: /save & next|next|finish/i, exact: false }).first().click();
        const url = new URL(page.url());
        expect(url.pathname.toLowerCase()).not.toContain('/error');
        expect(url.searchParams.get('step'), 'the welcome must not re-render itself').not.toBe('welcome');
    });
});

/**
 * REQUIREMENTS §713 — the wizard's sticky action bar at 360px.
 *
 * The reported symptom was "Save & exit" rendering as three stacked lines INSIDE its button:
 * three actions need ~382px and the card gives ~292px, so the browser broke the words. Asserted
 * by MEASURING height rather than by eye — a wrapped label is a button several lines tall.
 */
test.describe('@gui §713 Inline wizard — no button label is ever split', () => {
    test.skip(!PINS.volunteer || !USERS.volunteer, 'VOLUNTEER_PIN/VOLUNTEER_EMAIL not set');
    narrowOnly();

    test('every nav button is a single line on a step that has all three', async ({ page }) => {
        await login(page, USERS.volunteer, PINS.volunteer);
        // A MIDDLE step is the worst case: Previous + Save & exit + Save & next all present.
        await page.goto(`${BASE}/Forms/Wizard?step=profile`, { waitUntil: 'domcontentloaded' });

        const buttons = page.locator('.ceh-wiz__nav button');
        await expect(buttons).toHaveCount(3);

        const heights = await buttons.evaluateAll(bs =>
            bs.map(b => ({ label: (b.textContent || '').trim(), h: b.getBoundingClientRect().height })));

        for (const { label, h } of heights) {
            // One line of 15px text in a 9px-padded button is ~35px. Two lines clears 50px.
            expect(h, `"${label}" wrapped onto more than one line (${Math.round(h)}px tall)`)
                .toBeLessThan(48);
        }
    });
});

/**
 * REQUIREMENTS §714 — the Community Helper must not sit ON the wizard's action bar.
 *
 * ⚠️ Measure with the bar SCROLLED INTO VIEW. At scroll 0 the sticky bar is outside the viewport,
 * `elementFromPoint` returns null and everything reads as "nothing on top" — a false pass that
 * already fooled me once.
 */
test.describe('@gui §714 Inline wizard — the helper yields to the action bar', () => {
    test.skip(!PINS.volunteer || !USERS.volunteer, 'VOLUNTEER_PIN/VOLUNTEER_EMAIL not set');
    narrowOnly();

    test('the helper launcher overlaps no nav button, and is still there', async ({ page }) => {
        await login(page, USERS.volunteer, PINS.volunteer);
        await page.goto(`${BASE}/Forms/Wizard?step=profile`, { waitUntil: 'domcontentloaded' });
        await page.locator('.ceh-wiz__nav').scrollIntoViewIfNeeded();

        // The helper is LIFTED, not removed — the wizard is where someone asks for help.
        await expect(page.locator('#ai-helper-launcher')).toBeVisible();

        const clashes = await page.evaluate(() => {
            const l = document.getElementById('ai-helper-launcher')!.getBoundingClientRect();
            return Array.from(document.querySelectorAll('.ceh-wiz__nav button'))
                .filter(b => {
                    const r = b.getBoundingClientRect();
                    return !(l.right < r.left || l.left > r.right || l.bottom < r.top || l.top > r.bottom);
                })
                .map(b => (b.textContent || '').trim());
        });

        expect(clashes, `the helper is sitting on: ${clashes.join(', ')}`).toEqual([]);
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
