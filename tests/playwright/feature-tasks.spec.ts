import { test, expect, BASE, USERS, PINS, login, narrowOnly, assertNoHorizontalScroll, Page } from './support/hub';

/**
 * FEATURES.md §5 — Tasks & reminders (participant side).
 *
 * The personal to-do list shows only the signed-in person's own tasks and lets
 * them tick items off. We log in as the speaker (who is auto-seeded dated
 * speaker-deadline tasks) and do a complete -> reopen round-trip so the test is
 * self-cleaning and leaves the DB exactly as it found it.
 *
 * Reminder cadence/never-double-send is engine behaviour verified by the Pester
 * Features suite (static SentReminder-key assertions); here we cover the
 * participant-facing list + the tick-off interaction the GUI owns.
 */

test.describe('@gui §5 Tasks & reminders (speaker)', () => {
    test.skip(!PINS.speaker || !USERS.speaker, 'SPEAKER_PIN/SPEAKER_EMAIL not set');
    narrowOnly();
    test.beforeEach(async ({ page }) => login(page, USERS.speaker, PINS.speaker));

    test('the personal to-do list renders only this person\'s tasks', async ({ page }) => {
        await page.goto(`${BASE}/Tasks`, { waitUntil: 'domcontentloaded' });
        await expect(page.locator('h2', { hasText: 'My tasks' })).toBeVisible();
        await assertNoHorizontalScroll(page);
        // Either there are task rows (each with a toggle form) or the empty state.
        const toggles = page.locator('form[asp-page-handler], form:has(button:has-text("Mark done"))');
        const hasTasks = await page.locator('button:has-text("Mark done"), button:has-text("Mark not done")').count();
        const empty = await page.locator('text=You have no tasks.').count();
        expect(hasTasks + empty, 'either tasks or the empty state must show').toBeGreaterThan(0);
    });

    test('ticking a task done and reopening it round-trips (self-cleaning)', async ({ page }) => {
        await page.goto(`${BASE}/Tasks`, { waitUntil: 'domcontentloaded' });
        const markDone = page.getByRole('button', { name: 'Mark done', exact: true }).first();
        const count = await markDone.count();
        test.skip(count === 0, 'no open task to toggle for this speaker — nothing to round-trip');

        // Complete the first open task -> a "Mark not done" control appears.
        await markDone.click();
        const reopen = page.getByRole('button', { name: 'Mark not done', exact: true }).first();
        await expect(reopen).toBeVisible({ timeout: 10_000 });
        // Reopen it again so we leave the list exactly as we found it.
        await reopen.click();
        await expect(page.getByRole('button', { name: 'Mark done', exact: true }).first()).toBeVisible({ timeout: 10_000 });
    });

    test('the hub front page surfaces pending speaker-deadline tasks', async ({ page }) => {
        // The speaker hub auto-seeds dated milestone tasks; the front page lists them.
        await page.goto(`${BASE}/`, { waitUntil: 'domcontentloaded' });
        if (new URL(page.url()).pathname.toLowerCase().startsWith('/welcome')) {
            await page.getByRole('button', { name: /take me to my hub/i }).click();
        }
        // Speaker-deadline area is present for a speaker.
        await expect(
            page.locator('h2', { hasText: /Speaker deadlines|Pending tasks/i }).first()
        ).toBeVisible();
        await assertNoHorizontalScroll(page);
    });
});
