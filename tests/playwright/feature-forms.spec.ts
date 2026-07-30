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
        await expect(page.locator('h1', { hasText: 'Hotel preference' })).toBeVisible();
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
        await expect(page.locator('h1', { hasText: 'Appreciation Dinner' })).toBeVisible();
        await expect(page.locator('input[name="Rsvp"]').first()).toBeVisible();
        await page.locator('input[name="Rsvp"][value="Yes"]').check();
        // §21: the free-text allergy box became the structured dietary fieldset
        // (shared with the Speaker form) — always rendered, not RSVP-toggled.
        await expect(page.locator('h3', { hasText: 'Dietary needs & allergies' })).toBeVisible();
        await expect(page.locator('#Dietary_DietChoice')).toBeVisible();
        await expect(page.getByRole('button', { name: /Save my RSVP/i })).toBeVisible();
    });

    test('lunch form renders the pre-day / main-day choices', async ({ page }) => {
        await gotoForm(page, '/Forms/Lunch');
        await expect(page.locator('h1', { hasText: 'Lunch logistics' })).toBeVisible();
        // A speaker who speaks on the pre-day (Master Class) has the pre-day lunch
        // AUTO-COUNTED (LunchFormService.PreDayAutoCounted): the page then renders
        // an info notice INSTEAD of the form — an equally valid render.
        const preDay = page.locator('input[type="checkbox"][name="LunchPreDay"]');
        if (await preDay.count() === 0) {
            await expect(page.locator('p.info', { hasText: /automatically counted/i }))
                .toBeVisible();
            await expect(page.getByRole('link', { name: /Back to hub/i })).toBeVisible();
        } else {
            // §178c: the pre-day lunch is a CHECKBOX, matching the setup-day lunches
            // (operator wants ONE select/unselect style) — checked = yes, unchecked = no.
            await expect(preDay).toBeVisible();
            await expect(page.getByRole('button', { name: /Save my lunch preferences/i })).toBeVisible();
        }
    });

    test('speaker bio: /Forms/Speaker funnels to the consolidated Speaker Details editor', async ({ page }) => {
        // §26c: the old tabbed bio form is superseded — speakers are redirected
        // from /Forms/Speaker to the consolidated /Speaker/Details page (name +
        // bio + links + photo + accreditation + details in ONE flat form with a
        // single Save; see Pages/Speaker/_DetailsFields.cshtml).
        await gotoForm(page, '/Forms/Speaker');
        await expect(page).toHaveURL(/\/Speaker\/Details/i);
        await expect(page.locator('h1', { hasText: 'Speaker Details' })).toBeVisible();

        // The sign-in email is the identity + Sessionize match key — rendered as
        // the FIRST read-only input (display-only, never posted; it has no id/name).
        await expect(page.locator('input[readonly]').first()).toBeVisible();

        // The flat sections replace the former tab strip.
        for (const heading of ['Name', 'Bio & links', 'Photo', 'Microsoft accreditation', 'Details']) {
            await expect(page.locator('h3', { hasText: heading })).toBeVisible();
        }

        // All bio fields are editable and visible at once (no hidden tab panels).
        for (const id of ['#FirstName', '#LastName', '#Tagline', '#Biography',
                          '#LinkedIn', '#Twitter', '#Blog', '#PhotoUrl', '#Country']) {
            await expect(page.locator(id)).toBeVisible();
        }
        await expect(page.locator('input[name="SelectedAccreditations"]').first()).toBeVisible();

        // ONE Save button saves the hub edits AND syncs to the public event system (§195).
        await expect(page.getByRole('button', { name: 'Save', exact: true })).toBeVisible();
        await assertNoHorizontalScroll(page);
    });

    test('swag form renders polo/gift/badge preferences', async ({ page }) => {
        await gotoForm(page, '/Forms/Swag');
        await expect(page.locator('h1', { hasText: 'Swag preferences' })).toBeVisible();
        await expect(page.locator('#PoloChoice')).toBeVisible();
        await expect(page.getByRole('button', { name: /Save preferences/i })).toBeVisible();
    });

    test('travel form reveals the claim block only when reimbursement is requested', async ({ page }) => {
        await gotoForm(page, '/Forms/Travel');
        await expect(page.locator('h1', { hasText: 'Travel reimbursement' })).toBeVisible();
        // §48: Step 2 (the claim) lives in #Step2Fieldset which renders DISABLED
        // ("locked — complete Step 1 first") until ≥1 receipt is uploaded. We never
        // upload files (keeps the run re-runnable), so when this account has no
        // receipts on file we assert the locked contract instead of toggling.
        await expect(page.locator('#Step2Fieldset')).toBeVisible();
        if (await page.locator('#ReqNo').isDisabled()) {
            // Locked: the lock notice renders, and BOTH the claim radios and the
            // Step-2 save are unusable until Step 1 is completed.
            await expect(page.getByText(/complete Step 1 first/i)).toBeVisible();
            await expect(page.locator('#ReqYes')).toBeDisabled();
            await expect(page.getByRole('button', { name: /Request Travel Reimbursement/ }))
                .toBeDisabled();
        } else {
            // Unlocked: the claim block toggles with the reimbursement choice.
            await page.locator('#ReqYes').check();
            await expect(page.locator('#ClaimBlock')).toBeVisible();
            await expect(page.locator('#AmountChoice')).toBeVisible();
            await page.locator('#ReqNo').check();
            await expect(page.locator('#ClaimBlock')).toBeHidden();
        }
    });
});

test.describe('@gui §4 Volunteer wizard (multi-step)', () => {
    test.skip(!PINS.volunteer || !USERS.volunteer, 'VOLUNTEER_PIN/VOLUNTEER_EMAIL not set');
    narrowOnly();

    test('the wizard walks step 1 -> 2 -> 3 via real postbacks and shows a review', async ({ page }) => {
        await login(page, USERS.volunteer, PINS.volunteer);
        await gotoForm(page, '/Forms/VolunteerWizard');
        await expect(page.locator('h1', { hasText: 'Volunteer sign-up' })).toBeVisible();

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
        // The page is now a 4-step client-side wizard: step 1 shows "Next →"
        // (disabled until name/email/mobile validate) and the final submit
        // ("Submit application") stays hidden until the last step.
        await expect(page.locator('#vol-next')).toBeVisible();
        await expect(page.locator('#vol-submit')).toBeHidden();
        await expect(page.locator('#vol-submit')).toHaveText(/Submit application/i);
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
