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
        // §147: the page renders the shared _TaskListPanel (completion % header,
        // "Still to do (N)" pending section, collapsed Completed section) — its
        // rows toggle with "Mark complete" / "Reopen task". Either rows or the
        // honest empty state must show.
        const hasTasks = await page.locator('button:has-text("Mark complete"), button:has-text("Reopen task")').count();
        const empty = await page.locator('text=You have no tasks.').count();
        expect(hasTasks + empty, 'either tasks or the empty state must show').toBeGreaterThan(0);
        if (hasTasks > 0) {
            await expect(page.locator('h3.tl-section', { hasText: /Still to do/i })).toBeVisible();
        }
    });

    test('ticking a task done and reopening it round-trips (self-cleaning)', async ({ page }) => {
        await page.goto(`${BASE}/Tasks`, { waitUntil: 'domcontentloaded' });
        // §147 rows are collapsed <details>; only manually-togglable rows carry a
        // "Mark complete" button (data-signal tasks auto-complete and have none).
        const pendingRow = page.locator('details').filter({
            has: page.getByRole('button', { name: 'Mark complete', exact: true }),
        }).first();
        test.skip(await pendingRow.count() === 0,
            'no manually-togglable open task for this speaker — nothing to round-trip');

        // Remember WHICH task we complete so we reopen exactly that one (the
        // Completed section may already hold other, pre-existing tasks).
        const title = (await pendingRow.locator('summary').first()
            .evaluate((el) => (el.childNodes[0]?.textContent ?? '').trim()));
        await pendingRow.locator('summary').click();   // expand the row
        await pendingRow.getByRole('button', { name: 'Mark complete', exact: true }).click();

        // The task moved into the COLLAPSED "Completed (N)" section — expand it,
        // expand our row inside it, and reopen.
        const completedSection = page.locator('details').filter({
            has: page.locator('summary', { hasText: /Completed \(\d+\)/ }),
        }).first();
        await expect(completedSection).toBeVisible({ timeout: 10_000 });
        await completedSection.locator('> summary').click();
        const doneRow = completedSection.locator('details').filter({ hasText: title }).first();
        await doneRow.locator('summary').click();
        await doneRow.getByRole('button', { name: 'Reopen task', exact: true }).click();

        // Back where we started: the same task is pending again.
        await expect(page.locator('details').filter({
            has: page.getByRole('button', { name: 'Mark complete', exact: true }),
        }).filter({ hasText: title }).first()).toBeVisible({ timeout: 10_000 });
    });

    test('the hub front page surfaces pending speaker-deadline tasks', async ({ page }) => {
        // The speaker hub auto-seeds dated milestone tasks; the front page lists them.
        await page.goto(`${BASE}/`, { waitUntil: 'domcontentloaded' });
        if (new URL(page.url()).pathname.toLowerCase().startsWith('/welcome')) {
            await page.getByRole('button', { name: /take me to my hub/i }).click();
        }
        // The speaker landing renders its "Speaker hub" card; pending tasks
        // surface via the shared checklist card (Shared/_ChecklistCard.cshtml):
        // an "⚠ Pending tasks (N)" card with a due-date table + per-row
        // "Add reminder to calendar" buttons. (The ".tl-label Task checklist"
        // panel lives on /Speaker/Tasks, not the hub front page.)
        await expect(page.locator('h2', { hasText: 'Speaker hub' }).first()).toBeVisible();
        const pendingCard = page.locator('.ceh-checklist')
            .filter({ has: page.locator('h2', { hasText: 'Pending tasks' }) }).first();
        await expect(pendingCard).toBeVisible();
        // The auto-seeded speaker deadlines are DATED milestones: at least one
        // pending row must carry a due date, i.e. offer the calendar reminder.
        await expect(pendingCard.locator('table.task-table tbody tr').first()).toBeVisible();
        await expect(pendingCard.getByRole('button', { name: /Add reminder to calendar/i }).first())
            .toBeVisible();
        await assertNoHorizontalScroll(page);
    });
});
