import { test, expect, BASE, USERS, PINS, login, narrowOnly, assertNoHorizontalScroll, Page } from './support/hub';

/**
 * FEATURES.md §4 — Self-service forms, exercised through a real PIN login as the
 * crew member who owns each form. We RENDER each form and INTERACT with its
 * controls (assert the fields, toggle the conditional blocks), and drive the
 * volunteer wizard through all three steps via real postbacks. Forms are NOT
 * submitted/persisted except where the flow is inherently read-safe — we keep
 * the suite re-runnable and avoid polluting the DEV DB.
 *
 * Speaker block covers: Hotel, Dinner, Lunch, Speaker info, Swag, Travel.
 * Volunteer block covers: the multi-step Volunteer wizard.
 */

async function gotoForm(page: Page, path: string) {
    const resp = await page.goto(`${BASE}${path}`, { waitUntil: 'domcontentloaded' });
    expect(resp?.status(), `${path} should load`).toBe(200);
    await assertNoHorizontalScroll(page);
}

test.describe('@gui §4 Self-service forms (speaker)', () => {
    test.skip(!PINS.speaker || !USERS.speaker, 'SPEAKER_PIN/SPEAKER_EMAIL not set');
    narrowOnly();
    test.beforeEach(async ({ page }) => login(page, USERS.speaker, PINS.speaker));

    test('hotel form renders and the room-detail block toggles with NeedsRoom', async ({ page }) => {
        await gotoForm(page, '/Forms/Hotel');
        await expect(page.locator('h2', { hasText: 'Hotel preference' })).toBeVisible();
        // Choosing "needs a room" reveals the date/notes block (JS-driven).
        await page.locator('input[name="NeedsRoom"][value="true"]').check();
        await expect(page.locator('#CheckInDate')).toBeVisible();
        await expect(page.locator('#CheckOutDate')).toBeVisible();
        // Declining hides it again.
        await page.locator('input[name="NeedsRoom"][value="false"]').check();
        await expect(page.locator('#CheckInDate')).toBeHidden();
    });

    test('appreciation dinner form renders with RSVP + allergy capture', async ({ page }) => {
        await gotoForm(page, '/Forms/Dinner');
        await expect(page.locator('h2', { hasText: 'Appreciation Dinner' })).toBeVisible();
        await expect(page.locator('input[name="Rsvp"]').first()).toBeVisible();
        await page.locator('input[name="Rsvp"][value="Yes"]').check();
        await expect(page.locator('#AllergyNotes')).toBeVisible();
        await expect(page.getByRole('button', { name: /Save my RSVP/i })).toBeVisible();
    });

    test('lunch form renders the pre-day / main-day choices', async ({ page }) => {
        await gotoForm(page, '/Forms/Lunch');
        await expect(page.locator('h2', { hasText: 'Lunch logistics' })).toBeVisible();
        // PRE-DAY lunch is a required Yes/No radio group (operator §62), not a checkbox.
        await expect(page.locator('input[name="LunchPreDay"][value="true"]')).toBeVisible();
        await expect(page.locator('input[name="LunchPreDay"][value="false"]')).toBeVisible();
        await expect(page.getByRole('button', { name: /Save my lunch preferences/i })).toBeVisible();
    });

    test('speaker bio renders as editable tabs (Bio / Tagline / Links / Photo / Sessions)', async ({ page }) => {
        await gotoForm(page, '/Forms/Speaker');
        await expect(page.locator('h2', { hasText: 'Speaker details' })).toBeVisible();
        // Email is the identity, shown read-only (not editable).
        await expect(page.locator('input[name="Email"]')).toHaveAttribute('readonly', /.*/);

        // The bio is now a speaker-editable tab strip, not a read-only panel.
        await expect(page.locator('h3', { hasText: /Your public bio/i })).toBeVisible();
        const tablist = page.locator('.bio-tablist[role="tablist"]');
        await expect(tablist).toBeVisible();
        for (const name of ['Bio', 'Tagline', 'Links & Social', 'Photo', 'Sessions']) {
            await expect(tablist.getByRole('tab', { name })).toBeVisible();
        }

        // Default tab (Bio) shows the editable Biography textarea; the Tagline
        // field is on a different (hidden) tab until selected.
        await expect(page.locator('#Biography')).toBeVisible();
        await expect(page.locator('#Tagline')).toBeHidden();

        // Selecting the Tagline tab (CSS-only) reveals its input; clicking the
        // Links tab reveals the social fields. Pure-CSS tabs need no JS.
        await page.locator('label[for="biotab-tagline"]').click();
        await expect(page.locator('#Tagline')).toBeVisible();
        await page.locator('label[for="biotab-links"]').click();
        await expect(page.locator('#LinkedIn')).toBeVisible();
        await expect(page.locator('#Twitter')).toBeVisible();

        // One Save submits the whole form (hub-collected + bio).
        await expect(page.getByRole('button', { name: /Save speaker details/i })).toBeVisible();
        await assertNoHorizontalScroll(page);
    });

    test('swag form renders polo/gift/badge preferences', async ({ page }) => {
        await gotoForm(page, '/Forms/Swag');
        await expect(page.locator('h2', { hasText: 'Swag preferences' })).toBeVisible();
        await expect(page.locator('#PoloChoice')).toBeVisible();
        await expect(page.getByRole('button', { name: /Save preferences/i })).toBeVisible();
    });

    test('travel form reveals the claim block only when reimbursement is requested', async ({ page }) => {
        await gotoForm(page, '/Forms/Travel');
        await expect(page.locator('h2', { hasText: 'Travel reimbursement' })).toBeVisible();
        // Default: claim block hidden. Requesting reimbursement reveals it.
        await page.locator('#ReqYes').check();
        await expect(page.locator('#ClaimBlock')).toBeVisible();
        await expect(page.locator('#AmountChoice')).toBeVisible();
        await page.locator('#ReqNo').check();
        await expect(page.locator('#ClaimBlock')).toBeHidden();
    });
});

test.describe('@gui §4 Volunteer wizard (multi-step)', () => {
    test.skip(!PINS.volunteer || !USERS.volunteer, 'VOLUNTEER_PIN/VOLUNTEER_EMAIL not set');
    narrowOnly();

    test('the wizard walks step 1 -> 2 -> 3 via real postbacks and shows a review', async ({ page }) => {
        await login(page, USERS.volunteer, PINS.volunteer);
        await gotoForm(page, '/Forms/VolunteerWizard');
        await expect(page.locator('h2', { hasText: 'Volunteer sign-up' })).toBeVisible();

        const step = page.locator('text=/Step \\d of 3/');
        await expect(step).toContainText('Step 1 of 3');

        // Step 1: pick at least one shift, then Next.
        const firstShift = page.locator('input[name="SelectedShifts"]').first();
        await expect(firstShift).toBeVisible();
        await firstShift.check();
        await page.getByRole('button', { name: 'Next', exact: true }).click();

        // Step 2: role + hours, then Next. (Back is also present here.)
        await expect(step).toContainText('Step 2 of 3');
        await expect(page.getByRole('button', { name: 'Back', exact: true })).toBeVisible();
        await page.locator('#PreferredRole').fill('Registration desk');
        await page.locator('#MaxHoursPerDay').fill('6');
        await page.getByRole('button', { name: 'Next', exact: true }).click();

        // Step 3: review shows our choices and a Confirm button (we do NOT submit
        // to keep the run re-runnable / non-polluting).
        await expect(step).toContainText('Step 3 of 3');
        await expect(page.locator('body')).toContainText('Registration desk');
        await expect(page.getByRole('button', { name: /Confirm.*submit/i })).toBeVisible();
        await assertNoHorizontalScroll(page);

        // Back navigation works (returns to step 2 with state carried).
        await page.getByRole('button', { name: 'Back', exact: true }).click();
        await expect(step).toContainText('Step 2 of 3');
    });
});

test.describe('@gui §4 Public volunteer signup (no login)', () => {
    test('the anonymous signup page renders with its required fields + honeypot', async ({ page }) => {
        await gotoForm(page, '/volunteer/signup');
        await expect(page.locator('h1', { hasText: /Volunteer at/i })).toBeVisible();
        await expect(page.locator('#FullName')).toBeVisible();
        await expect(page.locator('#Email')).toBeVisible();
        await expect(page.getByRole('button', { name: /Send my application/i })).toBeVisible();
        // The honeypot field exists as a spam trap: kept out of the tab order
        // (tabindex=-1, autocomplete=off) and wrapped in an aria-hidden container
        // so real users never see or fill it.
        const hp = page.locator('#Website');
        await expect(hp).toHaveCount(1);
        await expect(hp).toHaveAttribute('tabindex', '-1');
        await expect(hp).toHaveAttribute('autocomplete', 'off');
        await expect(page.locator('.vol-hp[aria-hidden="true"]')).toHaveCount(1);
    });
});
