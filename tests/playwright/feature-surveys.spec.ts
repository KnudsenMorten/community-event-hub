import { test, expect } from '@playwright/test';

/**
 * FEATURES.md §6 — Sessions & surveys (public, no-login).
 *
 * The 3-step wizard interaction + hero/photo/tap-target checks live in
 * survey-mobile.spec.ts; this spec is the complementary RESULTS-dashboard and
 * survey-feature coverage that FEATURES.md §6 calls out: weighted topic
 * rankings, per-track breakdowns, the level distribution, the shareable results
 * page, and per-track deep-links. No login required, so it runs on DEV + PROD.
 */

const TARGETS: { name: string; baseUrl: string }[] = (() => {
    const dev  = { name: 'DEV',  baseUrl: 'https://dev.eldk27.eventhub.expertslive.dk' };
    const prod = { name: 'PROD', baseUrl: 'https://eldk27.eventhub.expertslive.dk' };
    const env = process.env.TARGET?.toUpperCase();
    if (env === 'DEV')  return [dev];
    if (env === 'PROD') return [prod];
    return [dev, prod];
})();

const SLUG = 'eldk27-topics';

for (const { name, baseUrl } of TARGETS) {
    test.describe(`@gui §6 ${name} survey results dashboard`, () => {

        test('the survey page is anonymous, slug-addressed and 3-step', async ({ page }) => {
            await page.goto(`${baseUrl}/survey/${SLUG}`, { waitUntil: 'domcontentloaded' });
            await expect(page).toHaveTitle(/Help shape the topics for ELDK27/i);
            // Step 1 offers track cards (the wizard's first step).
            await expect(page.locator('.track-card').first()).toBeVisible();
            // No sign-in chrome: the survey is fully public.
            await expect(page.locator('button.signout')).toHaveCount(0);
        });

        test('the results dashboard renders KPIs and the level distribution', async ({ page }) => {
            await page.goto(`${baseUrl}/survey/${SLUG}/results`, { waitUntil: 'domcontentloaded' });
            await expect(page.locator('.res-hero h1')).toContainText(/live results/i);
            // The four KPI tiles (Responses / Tracks covered / Most-picked / Latest).
            await expect(page.locator('.kpi')).toHaveCount(4);

            // The "Responses" KPI is the one whose .label is exactly "Responses"
            // (the "Most-picked track" KPI also contains the word "responses").
            const responsesKpi = page.locator('.kpi').filter({
                has: page.locator('.label', { hasText: /^Responses$/ }),
            });
            const responses = parseInt(
                (await responsesKpi.locator('.value').innerText()).trim() || '0', 10);
            if (responses > 0) {
                // With data: ranked topics, per-track breakdown and level totals show.
                await expect(page.locator('.section', { hasText: 'Track popularity' })).toBeVisible();
                await expect(page.locator('.section', { hasText: 'Most-wanted topics overall' })).toBeVisible();
                await expect(page.locator('.lvl-totals .lt')).toHaveCount(3); // Advanced / Expert / Black Belt
                // The "Master (500)" rename must not leak.
                await expect(page.locator('body')).not.toContainText('Master (500)');
                // Shareable dashboard link.
                await expect(page.locator('a.share-btn')).toBeVisible();
            } else {
                // Empty state still offers a share link to the survey.
                await expect(page.locator('.empty-state')).toBeVisible();
            }
        });

        test('per-track deep-links jump to the matching track section', async ({ page }) => {
            await page.goto(`${baseUrl}/survey/${SLUG}/results`, { waitUntil: 'domcontentloaded' });
            const responsesKpi = page.locator('.kpi').filter({
                has: page.locator('.label', { hasText: /^Responses$/ }),
            });
            const responses = parseInt(
                (await responsesKpi.locator('.value').innerText()).trim() || '0', 10);
            test.skip(responses === 0, 'no responses yet — per-track sections only render with data');

            // Each per-track block has an id and a "jump to track" pill linking #id.
            const trackBlock = page.locator('details.track-block').first();
            const id = await trackBlock.getAttribute('id');
            expect(id, 'track block should carry an anchor id').toBeTruthy();
            await expect(page.locator(`a[href="#${id}"]`).first()).toBeVisible();
        });
    });
}
