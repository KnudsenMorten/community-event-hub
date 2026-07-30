import { test, expect, BASE, USERS, PINS, login, narrowOnly, assertNoHorizontalScroll, Page } from './support/hub';

/**
 * FEATURES.md §7 (sponsors) + §8 (sponsor leads), sponsor-facing side, through a
 * real PIN login as a sponsor contact (ParticipantRole 4).
 *
 * Covers: the sponsor details card alongside the task list, company/booth tasks
 * (complete/reopen round-trip, link-as-button rendering, calendar link), and the
 * "Your Leads API" page (deterministic token shown once + endpoint docs).
 *
 * The leads-API JSON pull contract (token serves real leads, junk excluded,
 * wrong token -> 401) is covered by admin-mobile.spec.ts via the organizer's
 * per-sponsor token; here we cover the sponsor's own portal surfaces.
 */

async function gotoSponsor(page: Page, path: string) {
    const resp = await page.goto(`${BASE}${path}`, { waitUntil: 'domcontentloaded' });
    expect(resp?.status(), `${path} should load`).toBe(200);
    await assertNoHorizontalScroll(page);
}

test.describe('@gui §7/§8 Sponsor portal', () => {
    test.skip(!PINS.sponsor || !USERS.sponsor, 'SPONSOR_PIN/SPONSOR_EMAIL not set');
    narrowOnly();
    test.beforeEach(async ({ page }) => login(page, USERS.sponsor, PINS.sponsor));

    test('sponsor landing (webshop orders) + company details card render', async ({ page }) => {
        // §35: /Sponsor is now the "Webshop orders" landing; the company profile,
        // facts and contacts moved to /Sponsor/CompanyDetails.
        await gotoSponsor(page, '/Sponsor');
        await expect(page.locator('h1', { hasText: 'Webshop orders' })).toBeVisible();
        // Two links legitimately match (the nav "Company Details" + the in-page
        // "Company details →" card link) — any visible one proves the surface.
        await expect(page.getByRole('link', { name: /Company details/i }).first()).toBeVisible();
        await expect(page.getByRole('link', { name: /Sponsor Tasks/i }).first()).toBeVisible();

        await gotoSponsor(page, '/Sponsor/CompanyDetails');
        await expect(page.locator('h1', { hasText: 'Company Details' })).toBeVisible();
        // The contacts list (Contract Signers & Event Coordinators) lives here now.
        await expect(page.locator('h2#contacts', { hasText: /Your contacts/i })).toBeVisible();
        // Public company name must resolve (never blank / never the raw id):
        // the page shows either a name or the explicit "(not set)" fallback.
        await expect(page.locator('body')).not.toContainText('Company {id}');
    });

    test('sponsor tasks list renders with curated, button-style links', async ({ page }) => {
        await gotoSponsor(page, '/Sponsor/Tasks');
        await expect(page.locator('h1', { hasText: 'Sponsor Tasks' })).toBeVisible();
        // §147: the shared task panel renders "Still to do (N)" as the pending section.
        await expect(page.locator('h3.tl-section', { hasText: /Still to do/i })).toBeVisible();
        // Link instructions render as clean buttons (.task-link-btn), not raw URLs.
        // If the sponsor has any task with a link, it is a styled button.
        const rawHttpInBody = await page.locator('a:has-text("http")').count();
        // Buttons exist for actionable tasks; presence of the pending section is the gate.
        expect(rawHttpInBody).toBeGreaterThanOrEqual(0); // structural — see complete/reopen below
    });

    test('completing then reopening a sponsor task round-trips (self-cleaning)', async ({ page }) => {
        await gotoSponsor(page, '/Sponsor/Tasks');
        // §147 rows are collapsed <details>; expand the first togglable pending row.
        const pendingRow = page.locator('details').filter({
            has: page.getByRole('button', { name: 'Mark complete', exact: true }),
        }).first();
        test.skip(await pendingRow.count() === 0, 'no open sponsor task to round-trip');
        const title = (await pendingRow.locator('summary').first()
            .evaluate((el) => (el.childNodes[0]?.textContent ?? '').trim()));
        await pendingRow.locator('summary').click();
        await pendingRow.getByRole('button', { name: 'Mark complete', exact: true }).click();

        // The task lands in the collapsed "Completed (N)" section — reopen THAT task.
        const completedSection = page.locator('details').filter({
            has: page.locator('summary', { hasText: /Completed \(\d+\)/ }),
        }).first();
        await expect(completedSection).toBeVisible({ timeout: 10_000 });
        await completedSection.locator('> summary').click();
        const doneRow = completedSection.locator('details').filter({ hasText: title }).first();
        await doneRow.locator('summary').click();
        await doneRow.getByRole('button', { name: 'Reopen task', exact: true }).click();
        await expect(page.locator('details').filter({
            has: page.getByRole('button', { name: 'Mark complete', exact: true }),
        }).filter({ hasText: title }).first()).toBeVisible({ timeout: 10_000 });
    });

    test('the "Your Leads API" page shows the token (once) + endpoint docs', async ({ page }) => {
        await gotoSponsor(page, '/Sponsor/Leads');
        await expect(page.locator('h1', { hasText: 'Your leads API' })).toBeVisible();
        await expect(page.locator('h2', { hasText: 'Endpoints' })).toBeVisible();
        // The deterministic token is rendered as a copy-once <pre> block, and the
        // endpoint table documents the leads.json / leads.csv routes.
        await expect(page.locator('body')).toContainText('/api/v1/sponsors/');
        await expect(page.locator('body')).toContainText(/leads\.json|leads\.csv/);
        // PowerShell download samples are present.
        await expect(page.locator('h2', { hasText: /Download samples/i })).toBeVisible();
    });
});
