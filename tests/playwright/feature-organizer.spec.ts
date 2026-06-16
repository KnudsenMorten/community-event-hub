import { test, expect, BASE, USERS, PINS, login, sweep, narrowOnly, assertNoHorizontalScroll, runTag, Page } from './support/hub';

/**
 * FEATURES.md §9 (attendees) + §10 (email) + §11 (organizer hub), through a real
 * PIN login as the operator organizer.
 *
 * Complements admin-mobile.spec.ts (which already deeply covers Attendees,
 * Email Center, Broadcast, leads admin/API, Group photos and App game): this
 * spec covers the organizer surfaces admin-mobile does NOT, plus a full
 * organizer-area sweep:
 *   - Dashboard live tiles                                  §11
 *   - Participants inline activate/deactivate round-trip    §3/§11
 *   - EditParticipant SponsorCompanyId field + send-welcome  §3/§6 (2026-06-14)
 *   - DataGrid + TasksTable inline-edit grids               §11
 *   - Swag export, Lunch overview, Travel reimbursements    §11
 *   - Speakers / Sponsors / SendInvitations / SpeakerReminders / SessionizeImport
 *   - SponsorAdmin Tasks + status Dashboard                  §11
 *   - Attendee browser CSV export contract                   §9
 */

test.describe('@gui §9/§10/§11 Organizer hub', () => {
    test.skip(!PINS.organizer || !USERS.organizer, 'ADMIN_PIN/ORGANIZER_EMAIL not set');
    narrowOnly();
    test.beforeEach(async ({ page }) => login(page, USERS.organizer, PINS.organizer));

    test('§11 dashboard renders live stat tiles', async ({ page }) => {
        await page.goto(`${BASE}/Organizer/Dashboard`, { waitUntil: 'domcontentloaded' });
        await expect(page.locator('h2', { hasText: 'Dashboard' }).first()).toBeVisible();
        // Live stat tiles (participants / overdue / mismatches etc.).
        await expect(page.locator('.stat').first()).toBeVisible();
        await expect(page.locator('.stat .num').first()).toBeVisible();
        await assertNoHorizontalScroll(page);
    });

    test('§3/§11 participants: inline activate/deactivate round-trips (self-cleaning)', async ({ page }) => {
        await page.goto(`${BASE}/Organizer/Participants`, { waitUntil: 'domcontentloaded' });
        await expect(page.locator('h2', { hasText: 'Participants' })).toBeVisible();
        // Toggle the first row, then toggle it back to leave state unchanged.
        const firstToggle = page.locator('form[asp-page-handler="ToggleActive"] button, button:has-text("Deactivate"), button:has-text("Reactivate")').first();
        const initial = (await firstToggle.innerText()).trim();
        test.skip(await firstToggle.count() === 0, 'no participant row to toggle');
        await firstToggle.click();
        // After toggling, the inverse control is now present somewhere on the page.
        const inverse = initial === 'Deactivate' ? 'Reactivate' : 'Deactivate';
        const back = page.getByRole('button', { name: inverse, exact: true }).first();
        await expect(back).toBeVisible({ timeout: 10_000 });
        await back.click(); // restore
    });

    test('§3 edit-participant exposes SponsorCompanyId + send-welcome controls', async ({ page }) => {
        // Open the participant grid, edit the first row.
        await page.goto(`${BASE}/Organizer/Participants`, { waitUntil: 'domcontentloaded' });
        const editLink = page.locator('a[href*="/Organizer/EditParticipant/"]').first();
        test.skip(await editLink.count() === 0, 'no participant to edit');
        await editLink.click();
        await expect(page.locator('h2', { hasText: /Edit participant/i })).toBeVisible();
        // The SponsorCompanyId field (set/clear to link/unlink a sponsor contact).
        await expect(page.locator('input[name="SponsorCompanyId"]')).toBeVisible();
        // The send-welcome control (idempotent — once per person) is present on edit.
        await expect(page.getByRole('button', { name: /welcome email/i })).toBeVisible();
        await assertNoHorizontalScroll(page);
    });

    test('§21 participants grid: per-row Edit/Modify opens the real editor', async ({ page }) => {
        await page.goto(`${BASE}/Organizer/Participants`, { waitUntil: 'domcontentloaded' });
        await expect(page.locator('h2', { hasText: 'Participants' })).toBeVisible();
        const modify = page.getByRole('link', { name: /Edit \/ Modify/i }).first();
        test.skip(await modify.count() === 0, 'no participant row to modify');
        await modify.click();
        // Lands on the full editor with the core fields the organizer manages.
        await expect(page.locator('h2', { hasText: /Edit participant/i })).toBeVisible();
        await expect(page.locator('input[name="FullName"]')).toBeVisible();
        await expect(page.locator('input[name="Email"]')).toBeVisible();
        await expect(page.locator('select[name="Role"]')).toBeVisible();
        await assertNoHorizontalScroll(page);
    });

    test('§21 participants grid: per-row Delete opens a confirmation modal', async ({ page }) => {
        await page.goto(`${BASE}/Organizer/Participants`, { waitUntil: 'domcontentloaded' });
        await expect(page.locator('h2', { hasText: 'Participants' })).toBeVisible();
        const del = page.locator('button.js-delete-participant').first();
        test.skip(await del.count() === 0, 'no participant row to delete');
        // The modal starts hidden and only appears on click (no destructive
        // single-click). We open it then Cancel — leaving all rows intact.
        const modal = page.locator('#deleteModal');
        await expect(modal).toBeHidden();
        await del.click();
        await expect(modal).toBeVisible();
        await expect(modal.getByRole('button', { name: 'Delete', exact: true })).toBeVisible();
        await modal.locator('#deleteModalCancel').click();
        await expect(modal).toBeHidden();
        await assertNoHorizontalScroll(page);
    });

    test('§11 inline-edit grids (DataGrid + TasksTable) render with save controls', async ({ page }) => {
        await page.goto(`${BASE}/Organizer/DataGrid`, { waitUntil: 'domcontentloaded' });
        await expect(page.locator('h2', { hasText: /data grid/i })).toBeVisible();
        await expect(page.locator('.grid').first()).toBeVisible();
        await expect(page.getByRole('link', { name: /Export to CSV/i }).first()).toBeVisible();
        await assertNoHorizontalScroll(page);

        await page.goto(`${BASE}/Organizer/TasksTable`, { waitUntil: 'domcontentloaded' });
        await expect(page.locator('h2', { hasText: 'Tasks' }).first()).toBeVisible();
        await expect(page.getByRole('button', { name: 'Save', exact: true }).first()).toBeVisible();
    });

    test('§11 swag export offers the vendor spreadsheet download', async ({ page }) => {
        await page.goto(`${BASE}/Organizer/Swag`, { waitUntil: 'domcontentloaded' });
        await expect(page.locator('h2', { hasText: 'Swag order' })).toBeVisible();
        await expect(page.getByRole('link', { name: /Download .xlsx/i })).toBeVisible();
        await assertNoHorizontalScroll(page);
    });

    test('§11 lunch + travel overviews render', async ({ page }) => {
        await page.goto(`${BASE}/Organizer/Lunch`, { waitUntil: 'domcontentloaded' });
        await expect(page.locator('h2', { hasText: 'Lunch headcount' })).toBeVisible();
        await assertNoHorizontalScroll(page);

        await page.goto(`${BASE}/Organizer/TravelReimbursements`, { waitUntil: 'domcontentloaded' });
        await expect(page.locator('h2', { hasText: 'Travel reimbursements' })).toBeVisible();
        await assertNoHorizontalScroll(page);
    });

    test('§6/§11 speaker + sponsor + import tools render', async ({ page }) => {
        await page.goto(`${BASE}/Organizer/Speakers`, { waitUntil: 'domcontentloaded' });
        await expect(page.locator('h2', { hasText: 'Speakers' }).first()).toBeVisible();

        await page.goto(`${BASE}/Organizer/Sponsors`, { waitUntil: 'domcontentloaded' });
        await expect(page.locator('h2', { hasText: 'Sponsors' }).first()).toBeVisible();

        await page.goto(`${BASE}/Organizer/SessionizeImport`, { waitUntil: 'domcontentloaded' });
        await expect(page.locator('h2', { hasText: /Import speakers/i })).toBeVisible();
        // The import is a file upload (no network dependency) — the file input exists.
        await expect(page.locator('input[type="file"][name="UploadFile"]')).toBeVisible();

        await page.goto(`${BASE}/Organizer/SendInvitations`, { waitUntil: 'domcontentloaded' });
        await expect(page.locator('h2', { hasText: 'Send invitations' })).toBeVisible();

        await page.goto(`${BASE}/Organizer/SpeakerReminders`, { waitUntil: 'domcontentloaded' });
        await expect(page.locator('h2', { hasText: 'Speaker reminders' })).toBeVisible();
    });

    test('§11 sponsor admin: task catalog + status dashboard (overdue-first)', async ({ page }) => {
        await page.goto(`${BASE}/Organizer/SponsorAdmin/Tasks`, { waitUntil: 'domcontentloaded' });
        await expect(page.locator('h2', { hasText: 'Sponsor tasks' }).first()).toBeVisible();
        // Create-a-task form targets all sponsor companies.
        await expect(page.locator('input[name="title"]')).toBeVisible();
        await assertNoHorizontalScroll(page);

        await page.goto(`${BASE}/Organizer/SponsorAdmin/Dashboard`, { waitUntil: 'domcontentloaded' });
        await expect(page.locator('h2', { hasText: 'Sponsor status dashboard' })).toBeVisible();
        await assertNoHorizontalScroll(page);
    });

    test('§9 attendee browser supports search + CSV export', async ({ page }) => {
        await page.goto(`${BASE}/Organizer/Attendees`, { waitUntil: 'domcontentloaded' });
        await expect(page.locator('h2', { hasText: 'Attendees' })).toBeVisible();
        // A no-match search returns the friendly empty state (filter round-trips).
        await page.locator('input[name="Search"]').fill('zzz-no-such-attendee');
        await page.getByRole('button', { name: 'Apply', exact: true }).click();
        await expect(page.locator('.info, .error').first()).toBeVisible();
        // CSV export link carries the current filter (the Export handler).
        await expect(page.getByRole('link', { name: /Export|CSV/i }).first()).toBeVisible();
    });

    test('§11 full organizer-area sweep: every page 200 + no horizontal overflow', async ({ page }) => {
        await sweep(page, [
            '/Organizer', '/Organizer/Dashboard', '/Organizer/Attendees',
            '/Organizer/EmailCenter', '/Organizer/Broadcast',
            '/Organizer/GroupPhotos', '/Organizer/AppGame',
            '/Organizer/Participants', '/Organizer/Speakers',
            '/Organizer/Sponsors', '/Organizer/Swag', '/Organizer/Lunch',
            '/Organizer/TravelReimbursements', '/Organizer/DataGrid',
            '/Organizer/TasksTable', '/Organizer/SendInvitations',
            '/Organizer/SpeakerReminders', '/Organizer/SessionizeImport',
            '/Organizer/SponsorAdmin/Index', '/Organizer/SponsorAdmin/Dashboard',
            '/Organizer/SponsorAdmin/Tasks', '/Organizer/SponsorAdmin/Leads',
        ]);
    });
});
