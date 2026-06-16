import { test, expect, BASE, USERS, PINS, login, narrowOnly, gotoHub, assertNoHorizontalScroll }
    from './support/scenario';

/**
 * SCENARIO (GUI half): a speaker completes a milestone deadline in the Speaker
 * hub and the task list reflects it. Pairs with the backend half
 * SpeakerMilestoneScenarioTests (task rows flip Done in the DB).
 *
 * What this asserts on the GUI side:
 *   - the speaker's /Tasks page lists their milestone deadlines (real rows),
 *   - clicking "Mark done" on a pending task is a REAL postback that returns to
 *     /Tasks with that task now shown as done (the progress the hub renders),
 *   - the page renders without horizontal overflow at the narrow viewport.
 *
 * Self-skips without SPEAKER_EMAIL + SPEAKER_PIN. DEV-only (real PIN login).
 * Read-then-write: it toggles ONE task and is safe to re-run (the speaker can
 * re-toggle freely; nothing external is touched).
 */
test.describe('@scenario speaker completes a milestone deadline', () => {
    test.skip(!PINS.speaker || !USERS.speaker, 'SPEAKER_PIN/SPEAKER_EMAIL not set');
    narrowOnly();

    test('the speaker marks a pending task done and the list updates', async ({ page }) => {
        await login(page, USERS.speaker, PINS.speaker);
        await gotoHub(page);

        await page.goto(`${BASE}/Tasks`, { waitUntil: 'domcontentloaded' });
        await expect(page.locator('h2', { hasText: 'My tasks' })).toBeVisible();
        await assertNoHorizontalScroll(page);

        // Find a pending task (one whose toggle button reads "Mark done").
        const markDone = page.getByRole('button', { name: 'Mark done' });
        const pendingBefore = await markDone.count();
        test.skip(pendingBefore === 0, 'speaker has no pending tasks to complete');

        // Real postback: complete the first pending task.
        await markDone.first().click();
        await page.waitForLoadState('domcontentloaded');

        // The page now shows at least one fewer "Mark done" (it flipped to
        // "Mark not done"), proving the DB toggle round-tripped.
        await expect(page.locator('h2', { hasText: 'My tasks' })).toBeVisible();
        const doneMarkers = page.locator('text=/done/i');
        await expect(doneMarkers.first()).toBeVisible();
        const pendingAfter = await page.getByRole('button', { name: 'Mark done' }).count();
        expect(pendingAfter).toBeLessThan(pendingBefore);
    });
});
