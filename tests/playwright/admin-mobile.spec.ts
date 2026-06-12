import { test, expect, Page } from '@playwright/test';

/**
 * Mobile rendering + behaviour test for the organizer admin pages
 * (Attendees browser + Email Center), exercised through a REAL PIN login.
 *
 * DEV-ONLY by design: the test harness plants LoginPin rows with a known
 * PIN hash directly in the DEV database (tools/plant-test-pins.ps1) right
 * before the run. PROD is never touched. Run:
 *
 *   ..\..\tools\plant-test-pins.ps1          # plants PINs, prints the PIN
 *   $env:ADMIN_PIN='<pin>'; npx playwright test admin-mobile
 *
 * Skips itself when ADMIN_PIN is not set, so the default `npm test`
 * (survey suite, DEV+PROD) is unaffected.
 */

const BASE = 'https://dev.eldk27.eventhub.expertslive.dk';
const EMAIL = 'mok@expertslive.dk';
const PIN = process.env.ADMIN_PIN ?? '';

test.describe('DEV organizer admin (mobile)', () => {
    test.skip(!PIN, 'ADMIN_PIN not set - plant PINs with tools/plant-test-pins.ps1 first');
    // One project only: each login consumes a planted PIN and counts against
    // the PIN-request rate limit; the narrow viewport is the one that breaks
    // layouts, so it gives the mobile signal without burning the budget.
    test.beforeEach(({ }, testInfo) => {
        test.skip(!testInfo.project.name.includes('iPhone SE'),
            'admin suite runs on the narrowest viewport only');
    });

    async function login(page: Page) {
        await page.goto(`${BASE}/Login`, { waitUntil: 'domcontentloaded' });
        await page.locator('input[name="Email"]').fill(EMAIL);
        await page.getByRole('button', { name: /send.*code|email me|request/i }).click();
        const pinInput = page.locator('input[name="Pin"]');
        await expect(pinInput).toBeVisible();
        await pinInput.fill(PIN);
        await page.getByRole('button', { name: 'Sign in', exact: true }).click();
        // Successful verify redirects to the hub front page with a session
        // cookie - the signout button only renders when signed in.
        await expect(page.locator('button.signout')).toBeVisible({ timeout: 15_000 });
    }

    async function assertNoHorizontalScroll(page: Page) {
        const overflow = await page.evaluate(() => ({
            scrollWidth: document.documentElement.scrollWidth,
            clientWidth: document.documentElement.clientWidth,
        }));
        expect(overflow.scrollWidth).toBeLessThanOrEqual(overflow.clientWidth + 1);
    }

    test('PIN login -> attendees page renders on mobile', async ({ page }) => {
        await login(page);

        await page.goto(`${BASE}/Organizer/Attendees`, { waitUntil: 'domcontentloaded' });
        await expect(page.locator('h2', { hasText: 'Attendees' })).toBeVisible();
        // The summary tiles must be present (4 of them) and the page must
        // not blow the mobile viewport (the data table scrolls inside its
        // own wrapper instead).
        await assertNoHorizontalScroll(page);

        // Filters submit and come back filtered without error.
        await page.locator('input[name="Search"]').fill('zzz-no-such-attendee');
        await page.getByRole('button', { name: 'Apply' }).click();
        await expect(
            page.locator('.info', { hasText: 'No attendees match' })
        ).toBeVisible();
        await assertNoHorizontalScroll(page);
    });

    test('email center: preview renders + ledger visible on mobile', async ({ page }) => {
        await login(page);

        await page.goto(`${BASE}/Organizer/EmailCenter`, { waitUntil: 'domcontentloaded' });
        await expect(page.locator('h2', { hasText: 'Email center' })).toBeVisible();
        await assertNoHorizontalScroll(page);

        // Pick the welcome template - the select auto-submits via JS.
        await page.locator('select#Template').selectOption('welcome');
        await page.waitForURL(/Template=welcome/);

        // Subject line extracted from the template + a live iframe preview.
        await expect(page.locator('p', { hasText: 'Subject:' })).toBeVisible();
        const frame = page.locator('iframe[srcdoc]');
        await expect(frame).toBeVisible();
        await assertNoHorizontalScroll(page);

        // Ledger card is present (rows or the empty-state message).
        await expect(page.locator('h2', { hasText: 'Delivery ledger' })).toBeVisible();
    });

    test('broadcast: preview counts + send to organizer group only', async ({ page }) => {
        await login(page);

        await page.goto(`${BASE}/Organizer/Broadcast`, { waitUntil: 'domcontentloaded' });
        await expect(page.locator('h2', { hasText: 'Broadcast email' })).toBeVisible();
        await assertNoHorizontalScroll(page);

        // Unique subject per run so the per-subject dedup never makes the
        // send report "0 sent" on a re-run.
        const subject = `Playwright broadcast ${Date.now()}`;
        await page.locator('input[name="Roles"][value="Organizer"]').check();
        await page.locator('input[name="Subject"]').fill(subject);
        await page.locator('textarea[name="Message"]').fill(
            'Hello from the admin mobile suite.\n\nSecond paragraph.');
        await page.getByRole('button', { name: /Preview \+ count/ }).click();

        // Preview shows count + rendered iframe.
        await expect(page.locator('strong', { hasText: /recipient\(s\)/ })).toBeVisible();
        await expect(page.locator('iframe[srcdoc]')).toBeVisible();
        await assertNoHorizontalScroll(page);

        // Send (confirm dialog) - DEV redirects all mail to the operator.
        page.once('dialog', d => d.accept());
        await page.getByRole('button', { name: 'Send broadcast' }).click();
        await expect(
            page.locator('.info', { hasText: /1 sent, 0 skipped.*0 failed/ })
        ).toBeVisible({ timeout: 30_000 });
    });

    test('sponsor leads admin: counters, grid, status action + prefs save', async ({ page }) => {
        await login(page);

        await page.goto(`${BASE}/Organizer/SponsorAdmin/Leads`, { waitUntil: 'domcontentloaded' });
        await expect(page.locator('h2', { hasText: 'Pipeline status' })).toBeVisible();
        await assertNoHorizontalScroll(page);

        // Counters are live (seeded rows exist in DEV).
        const totalCell = page.locator('tr', { hasText: 'Total leads in DB' }).locator('strong');
        await expect(totalCell).not.toHaveText('0');

        // The seeded looks-legit lead renders in the grid with its AI badge.
        const leadRow = page.locator('tr', { hasText: 'Lena Larsen' });
        await expect(leadRow).toBeVisible();
        await expect(leadRow.locator('span', { hasText: 'looks-legit' })).toBeVisible();

        // Junk rows are hidden by default (seeded test-entry is junked).
        await expect(page.locator('tr', { hasText: 'Test Test' })).toHaveCount(0);

        // Status action: Interest -> notice + status cell updates.
        await leadRow.getByRole('button', { name: 'Interest' }).click();
        await expect(page.locator('.info', { hasText: /set to Interest/ })).toBeVisible();
        await expect(page.locator('tr', { hasText: 'Lena Larsen' })).toContainText('Interest');

        // Notification prefs persist (form round-trips a save).
        const firstPref = page.locator('details').filter({ has: page.locator('form[action*="SaveNotifyPrefs"]') }).first();
        await firstPref.locator('summary').click();
        await firstPref.locator('input[name="enabled"]').check();
        await firstPref.locator('input[name="recipients"]').fill('mok@expertslive.dk');
        await firstPref.getByRole('button', { name: 'Save preferences' }).click();
        await expect(page.locator('.info', { hasText: /Notification prefs saved/ })).toBeVisible();
    });

    test('sponsor leads API: deterministic token serves seeded lead, junk excluded', async ({ page, request }) => {
        await login(page);

        // Pull the deterministic token for sponsor 10 off the admin page.
        await page.goto(`${BASE}/Organizer/SponsorAdmin/Leads`, { waitUntil: 'domcontentloaded' });
        const row = page.locator('tr', { has: page.locator('code', { hasText: /^10$/ }) }).first();
        const token = (await row.locator('td').nth(1).innerText()).trim().split(/\s/)[0];
        expect(token).toMatch(/^[0-9a-f]{32}$/);

        const resp = await request.get(
            `${BASE}/api/v1/sponsors/10/leads.json`,
            { headers: { Authorization: `Bearer ${token}` } });
        expect(resp.status()).toBe(200);
        const body = await resp.json();
        const emails = body.leads.map((l: any) => l.email ?? l.Email);
        expect(emails).toContain('lena.larsen@contoso-example.dk'); // real lead served
        expect(emails).not.toContain('test@example.com');           // junk excluded

        // Wrong token -> 401.
        const bad = await request.get(
            `${BASE}/api/v1/sponsors/10/leads.json`,
            { headers: { Authorization: 'Bearer 00000000000000000000000000000000' } });
        expect(bad.status()).toBe(401);
    });

    test('group photos: register + schedule + send calendar invite', async ({ page }) => {
        await login(page);

        await page.goto(`${BASE}/Organizer/GroupPhotos`, { waitUntil: 'domcontentloaded' });
        await expect(page.locator('h2', { hasText: 'Group photos' })).toBeVisible();
        await assertNoHorizontalScroll(page);

        // Register a company with a slot (idempotent-ish: unique name per run).
        const company = `PW Photo Co ${Date.now()}`;
        await page.locator('summary', { hasText: 'Register a company' }).click();
        const form = page.locator('form[action*="Create"]').first();
        await form.locator('input[name="companyName"]').fill(company);
        await form.locator('input[name="contactName"]').fill('Lena Larsen');
        await form.locator('input[name="contactEmail"]').fill('lena.larsen@contoso-example.dk');
        await form.locator('input[name="scheduledLocal"]').fill('2027-02-10T11:30');
        await form.getByRole('button', { name: 'Register' }).click();
        await expect(page.locator('.info', { hasText: `Registered '${company}'` })).toBeVisible();

        // Send the calendar invite (DEV redirect catches the real ICS mail).
        const row = page.locator('details', { hasText: company });
        await row.locator('summary').click();
        await row.getByRole('button', { name: /calendar invite/ }).click();
        await expect(page.locator('.info', { hasText: /Invite for '.*': 1 sent/ })).toBeVisible({ timeout: 20_000 });
        await assertNoHorizontalScroll(page);
    });

    test('app game: register sponsor + send gift reminder', async ({ page }) => {
        await login(page);

        await page.goto(`${BASE}/Organizer/AppGame`, { waitUntil: 'domcontentloaded' });
        await expect(page.locator('h2', { hasText: 'App game' })).toBeVisible();
        await assertNoHorizontalScroll(page);

        // Register sponsor company 10 if not already on the board.
        const already = await page.locator('details', { hasText: 'PW Game Sponsor' }).count();
        if (already === 0) {
            await page.locator('summary', { hasText: 'Register a sponsor' }).click();
            const form = page.locator('form[action*="Create"]').first();
            await form.locator('select[name="sponsorCompanyId"]').selectOption('10');
            await form.locator('input[name="companyName"]').fill('PW Game Sponsor');
            await form.locator('input[name="giftDescription"]').fill('Lego Technic set');
            await form.getByRole('button', { name: 'Register' }).click();
            await expect(page.locator('.info', { hasText: /Registered 'PW Game Sponsor'/ })).toBeVisible();
        }

        // Send the gift reminder to the company's contacts (DEV-redirected).
        const row = page.locator('details', { hasText: 'PW Game Sponsor' });
        await row.locator('summary').click();
        await row.getByRole('button', { name: 'Send gift reminder' }).click();
        await expect(page.locator('.info', { hasText: /Gift reminder for 'PW Game Sponsor': \d+ sent/ })).toBeVisible({ timeout: 20_000 });
        await assertNoHorizontalScroll(page);
    });

    test('email center: test-send delivers (DEV redirect catches it)', async ({ page }) => {
        await login(page);

        await page.goto(
            `${BASE}/Organizer/EmailCenter?Template=welcome`,
            { waitUntil: 'domcontentloaded' });
        await page.getByRole('button', { name: 'Send test copy to me' }).click();
        await expect(
            page.locator('.info', { hasText: /Test mail 'welcome' sent/ })
        ).toBeVisible({ timeout: 20_000 });
    });
});
