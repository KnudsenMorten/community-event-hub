// TEMPORARY — runs the C3 sync via the organiser page and prints what it did. Delete after use.
import { test, expect } from '@playwright/test';
import { login, targetBase } from './support/hub';

const BASE = targetBase();

test('@c3 run the session sync', async ({ page }) => {
    test.setTimeout(180_000);
    await login(page, process.env.ORGANIZER_EMAIL!, process.env.ADMIN_PIN!);
    await page.goto(`${BASE}/Organizer/EvaluationSessions`, { waitUntil: 'domcontentloaded' });

    await page.getByRole('button', { name: 'Sync from CEH' }).click({ timeout: 60_000 });
    await page.waitForLoadState('domcontentloaded');

    const notice = await page.locator('p.notice').first().textContent().catch(() => null);
    console.log('\n===== SYNC RESULT =====\n' + (notice?.trim() ?? '(no notice rendered)'));

    const warnings = await page.locator('h3:has-text("Needs attention") ~ ul li, .card:has-text("Needs attention") li')
        .allTextContents();
    if (warnings.length) {
        console.log('\n----- WARNINGS (' + warnings.length + ') -----');
        warnings.forEach(w => console.log('  ! ' + w.trim()));
    } else {
        console.log('\n----- WARNINGS: none -----');
    }

    // The change log, which is where the speaker links show up.
    const changes = await page.locator('details li, ul li').allTextContents();
    const speaker = changes.filter(c => /Speaker (linked|unlinked)/i.test(c));
    console.log('\n----- SPEAKER LINK CHANGES (' + speaker.length + ') -----');
    speaker.slice(0, 40).forEach(c => console.log('  ' + c.trim()));

    console.log('\n===== END =====\n');
});
