import { test, expect, BASE, USERS, PINS, login, narrowOnly, Page } from './support/hub';

/**
 * ⚠️ NOT CURRENTLY RUNNABLE — the shared PIN `login()` helper fails against prod (2026-07-28).
 * These specs have NEVER passed. Do not read their presence as coverage.
 * See the header of feature-direct-upload.spec.ts for the full diagnosis and the next step.
 *
 * They are kept because they encode the EXPECTED behaviour of the 27–28 July change set in
 * executable form — worth more as a written contract than as a deleted file, provided nobody
 * mistakes them for a passing suite.
 */

/**
 * The 27–28 July change set (§440, §467–§494), asserted through real PIN logins.
 *
 * This exists because I shipped §486 — a change that blocked EVERY nav link on production —
 * with 3000+ unit tests green. Those tests assert the NavBuilder model; the defect lived in
 * Razor rendering plus a CSS selector, one layer above. Nothing rendered the layout and
 * clicked a link. This file is that missing layer.
 *
 * Assertions are written to survive missing data (a company with no booth, an event with no
 * oversized deck) by skipping rather than failing: a false red is as costly as a false green
 * when someone is deciding whether to trust a release.
 */

async function go(page: Page, path: string) {
    const resp = await page.goto(`${BASE}${path}`, { waitUntil: 'domcontentloaded' });
    expect(resp?.status(), `${path} should load`).toBe(200);
}

// ===================================================================
//  §486 — THE regression check: ordinary navigation must still work
// ===================================================================
test.describe('@gui §486 Navigation is not blocked by the Zoho interstitial', () => {
    test.skip(!PINS.sponsor || !USERS.sponsor, 'SPONSOR_PIN/SPONSOR_EMAIL not set');
    narrowOnly();
    test.beforeEach(async ({ page }) => login(page, USERS.sponsor, PINS.sponsor));

    test('a normal menu link still navigates', async ({ page }) => {
        // The exact production failure: every nav link carried an empty
        // data-zoho-interstitial attribute, a presence selector matched them all, and
        // preventDefault() swallowed the click. Get Started stopped working.
        await go(page, '/Sponsor');

        const before = page.url();
        const link = page.locator('nav.primary a', { hasText: /Get Started/i }).first();
        if (await link.count() === 0) test.skip(true, 'Get Started not in this menu');

        await link.click();
        await page.waitForLoadState('domcontentloaded');
        expect(page.url(), 'the click must actually navigate').not.toBe(before);

        // And no dialog hijacked it.
        await expect(page.locator('#zoho-interstitial')).toBeHidden();
    });

    test('ONLY Zoho links carry the interstitial marker', async ({ page }) => {
        await go(page, '/Sponsor');

        // The empty-attribute bug in one assertion: any link marked must be marked "1".
        const marked = await page.locator('a[data-zoho-interstitial]').evaluateAll(
            els => els.map(e => ({
                href: (e as HTMLAnchorElement).getAttribute('href') ?? '',
                val: e.getAttribute('data-zoho-interstitial'),
            })));

        for (const m of marked) {
            expect(m.val, `${m.href} must be marked "1", not empty`).toBe('1');
        }
    });

    test('a (Zoho) link shows the dialog and does not navigate until Continue', async ({ page }) => {
        await go(page, '/Sponsor');

        const zoho = page.locator('a[data-zoho-interstitial="1"]').first();
        if (await zoho.count() === 0) test.skip(true, 'No Zoho links for this account');

        const before = page.url();
        await zoho.click();

        const dialog = page.locator('#zoho-interstitial');
        await expect(dialog).toBeVisible();
        expect(page.url(), 'the dialog must not navigate on its own').toBe(before);

        // §487 — the copy is emphasised, not raw markdown.
        await expect(dialog.locator('strong', { hasText: 'SIGN IN' })).toBeVisible();
        await expect(dialog).not.toContainText('**');

        // Cancel returns without opening anything.
        await dialog.locator('#zoho-interstitial-cancel').click();
        await expect(dialog).toBeHidden();
        expect(page.url()).toBe(before);
    });
});

// ===================================================================
//  §483 / §488 / §489 — the sponsor menu
// ===================================================================
test.describe('@gui §483/§488/§489 Sponsor menu', () => {
    test.skip(!PINS.sponsor || !USERS.sponsor, 'SPONSOR_PIN/SPONSOR_EMAIL not set');
    narrowOnly();
    test.beforeEach(async ({ page }) => login(page, USERS.sponsor, PINS.sponsor));

    test('the four Zoho SETUP links are gone; Leads/Inquiries remain', async ({ page }) => {
        await go(page, '/Sponsor');
        const nav = page.locator('nav.primary');

        for (const gone of ['Exhibitor Profile', 'Booth Members (Zoho)', 'Exhibitor Materials', 'Promotional Banner']) {
            await expect(nav.getByText(gone, { exact: false })).toHaveCount(0);
        }
    });

    test('"Sponsor Tasks" now reads "Tasks"', async ({ page }) => {
        await go(page, '/Sponsor');
        await expect(page.locator('nav.primary').getByText('Sponsor Tasks', { exact: false })).toHaveCount(0);
    });

    test('the Zoho Event System sub-fold-out is OPEN by default', async ({ page }) => {
        await go(page, '/Sponsor');
        const sub = page.locator('details.nav-subsection', { hasText: 'Zoho Event System' }).first();
        if (await sub.count() === 0) test.skip(true, 'No booth fold-out for this account');
        // §317: nested fold-outs render open so nobody has to click twice.
        await expect(sub).toHaveAttribute('open', /.*/);
    });
});

// ===================================================================
//  §474–§477 — the sponsor Get Started wizard
// ===================================================================
test.describe('@gui §474-§477 Sponsor Get Started', () => {
    test.skip(!PINS.sponsor || !USERS.sponsor, 'SPONSOR_PIN/SPONSOR_EMAIL not set');
    narrowOnly();
    test.beforeEach(async ({ page }) => login(page, USERS.sponsor, PINS.sponsor));

    test('the Booth members STEP is gone from the wizard', async ({ page }) => {
        await go(page, '/Forms/Wizard');
        // §474: it is a task now, not a step. The stepper must not offer it.
        const stepper = page.locator('.wizard-stepper, [class*=stepper]').first();
        if (await stepper.count() === 0) test.skip(true, 'No wizard stepper rendered');
        await expect(stepper.getByText('Booth members', { exact: false })).toHaveCount(0);
    });

    test('booth materials offers SIX video rows and does not block Save & next', async ({ page }) => {
        await go(page, '/Forms/Wizard?step=booth-materials');

        const rows = page.locator('input[name^="Video"][name$="Url"]');
        if (await rows.count() === 0) test.skip(true, 'Booth materials step not applicable');
        expect(await rows.count(), '§476: six video rows').toBe(6);

        // §475: an EMPTY step must advance rather than refuse.
        const before = page.url();
        await page.getByRole('button', { name: /Save & next/i }).first().click();
        await page.waitForLoadState('domcontentloaded');
        expect(page.url(), 'an empty optional step must advance').not.toBe(before);
    });

    test('FULL ROUND-TRIP: add a booth video, see it saved, then remove it', async ({ page }) => {
        // A real simulation rather than a rendered-text check: type → save → verify persisted
        // → tick remove → save → verify gone. This is the only way to prove §476's add and
        // remove halves actually work together, and it CLEANS UP after itself so running it
        // against prod leaves no residue.
        await go(page, '/Forms/Wizard?step=booth-materials');
        const first = page.locator('input[name="Video1Url"]');
        if (await first.count() === 0) test.skip(true, 'Booth materials step not applicable');

        const url = `https://youtu.be/ceh-gui-test-${Date.now()}`;
        await first.fill(url);
        await page.getByRole('button', { name: /Save & next/i }).first().click();
        await page.waitForLoadState('domcontentloaded');

        // Come back and confirm it PERSISTED (a save that only looks successful is the bug
        // this catches).
        await go(page, '/Forms/Wizard?step=booth-materials');
        const saved = page.getByText(url, { exact: false });
        await expect(saved, 'the video should be listed after saving').toBeVisible();

        // Now remove it via the §476c tick box and confirm it is really gone.
        const row = page.locator('li', { hasText: url }).first();
        await row.locator('input[type=checkbox][name=RemoveIds]').check();
        await page.getByRole('button', { name: /Save & next/i }).first().click();
        await page.waitForLoadState('domcontentloaded');

        await go(page, '/Forms/Wizard?step=booth-materials');
        await expect(page.getByText(url, { exact: false }),
            'the removed video must be gone').toHaveCount(0);
    });

    test('booth check-in: blank count is REFUSED when attending, ACCEPTED when not', async ({ page }) => {
        await go(page, '/Forms/Wizard?step=booth-checkin');

        const count = page.locator('#MemberCount');
        if (await count.count() === 0) test.skip(true, 'Booth check-in step not applicable');

        // (a) attending + blank → refused (§477).
        await page.locator('input[name=Slot]').first().check();
        await count.fill('');
        await page.getByRole('button', { name: /Save & next/i }).first().click();
        await page.waitForLoadState('domcontentloaded');
        await expect(page.locator('.error, .field-validation-error').first(),
            'attending with no head count must be refused').toBeVisible();

        // (b) the SAME blank count must be accepted once they are not participating — the
        // exemption that makes the rule sane. Both branches, or the test proves half a rule.
        const optOut = page.locator('input[name=Slot]').last();
        await optOut.check();
        await page.locator('#MemberCount').fill('');
        const before = page.url();
        await page.getByRole('button', { name: /Save & next/i }).first().click();
        await page.waitForLoadState('domcontentloaded');
        expect(page.url(), 'not-participating must advance with a blank count').not.toBe(before);
    });

    test('FULL ROUND-TRIP: a head count entered while attending is persisted', async ({ page }) => {
        await go(page, '/Forms/Wizard?step=booth-checkin');
        if (await page.locator('#MemberCount').count() === 0) test.skip(true, 'Step not applicable');

        await page.locator('input[name=Slot]').first().check();
        await page.locator('#MemberCount').fill('4');
        await page.getByRole('button', { name: /Save & next/i }).first().click();
        await page.waitForLoadState('domcontentloaded');

        await go(page, '/Forms/Wizard?step=booth-checkin');
        await expect(page.locator('#MemberCount'), 'the count must survive a reload')
            .toHaveValue('4');
    });

    test('the contacts step shows role TICKS, never "Role:1,2"', async ({ page }) => {
        await go(page, '/Forms/Wizard?step=contacts');
        // §472: the ERP id list must never reach a sponsor's screen.
        await expect(page.locator('body')).not.toContainText(/Role:\s*\d/);
    });

    test('the party step reads "we\'ll" for a sponsor', async ({ page }) => {
        await go(page, '/Forms/Wizard?step=party');
        const label = page.locator('label[for=Attending]');
        if (await label.count() === 0) test.skip(true, 'Party step not applicable');
        await expect(label).toContainText(/we'll attend/i);   // §479 — a group reservation
    });
});

// ===================================================================
//  §480 — task descriptions must render, not show raw markdown
// ===================================================================
test.describe('@gui §480 Deadlines step formatting', () => {
    test.skip(!PINS.sponsor || !USERS.sponsor, 'SPONSOR_PIN/SPONSOR_EMAIL not set');
    narrowOnly();
    test.beforeEach(async ({ page }) => login(page, USERS.sponsor, PINS.sponsor));

    test('descriptions render as HTML, not literal ** and [](  )', async ({ page }) => {
        await go(page, '/Forms/Wizard?step=deadlines');

        const details = page.locator('details.dl-item');
        if (await details.count() === 0) test.skip(true, 'No dated items outside the wizard');

        await details.first().click();
        const body = await page.locator('details.dl-item[open]').first().innerText();
        expect(body, 'no raw bold markers').not.toContain('**');
        expect(body, 'no raw underline markers').not.toContain('__');
        expect(body, 'no raw markdown links').not.toMatch(/\]\(https?:\/\//);
    });
});

// ===================================================================
//  §440 / §491b — organizer jobs page
// ===================================================================
test.describe('@gui §440/§491b Organizer jobs', () => {
    test.skip(!PINS.organizer || !USERS.organizer, 'ADMIN_PIN/ORGANIZER_EMAIL not set');
    narrowOnly();
    test.beforeEach(async ({ page }) => login(page, USERS.organizer, PINS.organizer));

    test('schedules read in plain English and are marked "set in code"', async ({ page }) => {
        await go(page, '/Organizer/Jobs');
        const body = await page.locator('body').innerText();
        expect(body).toMatch(/Every \d+ (minutes|hours)|Every (minute|hour|day)/);
        expect(body).toContain('set in code');
    });

    test('the platform monitoring panel is present', async ({ page }) => {
        await go(page, '/Organizer/Jobs');
        await expect(page.getByText('Platform monitoring', { exact: false })).toBeVisible();
    });
});

// ===================================================================
//  §485 — Webshop orders cross-links removed
// ===================================================================
test.describe('@gui §485 Webshop orders footer', () => {
    test.skip(!PINS.sponsor || !USERS.sponsor, 'SPONSOR_PIN/SPONSOR_EMAIL not set');
    narrowOnly();
    test.beforeEach(async ({ page }) => login(page, USERS.sponsor, PINS.sponsor));

    test('only "Back to hub" remains', async ({ page }) => {
        await go(page, '/Sponsor');
        const card = page.locator('.card').first();
        await expect(card.getByText('Your deliverables', { exact: false })).toHaveCount(0);
        await expect(card.getByRole('link', { name: /Back to hub/i })).toBeVisible();
    });
});
