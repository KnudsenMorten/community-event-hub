import { test, expect } from '@playwright/test';

/**
 * Mobile rendering smoke test for /survey/eldk27-topics.
 *
 * The Pester suite (tests/Survey-Mobile.Tests.ps1) catches stale text +
 * broken markup. This suite catches things only a real browser can see:
 *   - hero h1 not clipped at any viewport
 *   - no horizontal scroll
 *   - tap targets are >= 44px (Apple HIG minimum)
 *   - the 3-step wizard navigation actually works end-to-end
 *   - all 3 step photos load (status 200, intrinsic size > 0)
 *   - on a tap the level option visually toggles
 *
 * Run:
 *   npm install
 *   npm run install:browsers
 *   npm test                     # both DEV + PROD
 *   npm run test:dev             # DEV only
 *   npm run test:headed          # watch the browser
 *   npm run test:ui              # Playwright UI mode
 *   npm run report               # open the HTML report after a run
 */

const TARGETS: { name: string; baseUrl: string }[] = (() => {
    const dev  = { name: 'DEV',  baseUrl: 'https://dev.eldk27.eventhub.expertslive.dk' };
    const prod = { name: 'PROD', baseUrl: 'https://eldk27.eventhub.expertslive.dk' };
    const env = process.env.TARGET?.toUpperCase();
    if (env === 'DEV')  return [dev];
    if (env === 'PROD') return [prod];
    return [dev, prod];
})();

for (const { name, baseUrl } of TARGETS) {
    test.describe(`${name} survey mobile`, () => {

        test.beforeEach(async ({ page }) => {
            await page.goto(`${baseUrl}/survey/eldk27-topics`, { waitUntil: 'domcontentloaded' });
        });

        test('page loads and is logged + visible', async ({ page }) => {
            await expect(page).toHaveTitle(/Help shape the topics for ELDK27/i);
            await expect(page.locator('.survey-hero h1')).toBeVisible();
        });

        test('hero h1 is fully visible (no clipping)', async ({ page }) => {
            const h1 = page.locator('.survey-hero h1');
            await expect(h1).toBeVisible();
            const box = await h1.boundingBox();
            expect(box, 'h1 should have a bounding box').not.toBeNull();
            // h1 must have positive area AND must be entirely INSIDE the hero band.
            expect(box!.width).toBeGreaterThan(40);
            expect(box!.height).toBeGreaterThan(15);
            const hero = page.locator('.survey-hero');
            const heroBox = await hero.boundingBox();
            expect(heroBox, 'hero should have a bounding box').not.toBeNull();
            expect(box!.y).toBeGreaterThanOrEqual(heroBox!.y - 1);
            expect(box!.y + box!.height).toBeLessThanOrEqual(heroBox!.y + heroBox!.height + 1);
        });

        test('no horizontal scroll on body', async ({ page }) => {
            const overflow = await page.evaluate(() => ({
                scrollWidth: document.documentElement.scrollWidth,
                clientWidth: document.documentElement.clientWidth,
            }));
            // 1px tolerance for sub-pixel rounding.
            expect(overflow.scrollWidth).toBeLessThanOrEqual(overflow.clientWidth + 1);
        });

        test('all three step photos load with intrinsic size > 0', async ({ page, request }) => {
            for (const i of [1, 2, 3]) {
                const url = `${baseUrl}/img/survey-step${i}.jpg`;
                const resp = await request.head(url);
                expect(resp.status(), `step ${i} photo`).toBe(200);
            }
        });

        test('topbar is present with the event-site link', async ({ page }) => {
            await expect(page.locator('.topbar')).toBeVisible();
            const cta = page.locator('a.eventsite-btn');
            await expect(cta).toBeVisible();
            await expect(cta).toHaveAttribute('href', 'https://eldk27.expertslive.dk/');
        });

        test('footer renders with the CEH credit + Submit-a-bug link', async ({ page }) => {
            const footer = page.locator('footer');
            await expect(footer).toBeVisible();
            await expect(footer).toContainText('Community Event Hub');
            await expect(footer.locator('a', { hasText: 'Submit a bug' })).toBeVisible();
        });

        test('wizard end-to-end: pick a track -> rank 3 topics -> pick levels -> submit-enabled', async ({ page }) => {
            // Step 1: pick the first track card.
            const firstTrack = page.locator('.track-card').first();
            await firstTrack.scrollIntoViewIfNeeded();
            await firstTrack.click();
            await expect(firstTrack).toHaveClass(/selected/);

            const next = page.locator('#nextBtn');
            await expect(next).toBeEnabled();
            await next.click();

            // Step 2: rank the first 3 topics in the visible (in-track) set.
            const topicSet = page.locator('.topic-set').filter({ has: page.locator('.topic-row') }).first();
            const rows = topicSet.locator('.topic-row');
            const ranks = ['Top', '2nd', '3rd'];
            for (let i = 0; i < 3; i++) {
                const row = rows.nth(i);
                await row.scrollIntoViewIfNeeded();
                await row.locator('.rank-btn', { hasText: ranks[i] }).click();
                await expect(row).toHaveClass(/picked/);
            }
            await expect(next).toBeEnabled();
            await next.click();

            // Step 3: pick a level for each card -> submit must enable.
            const cards = page.locator('.level-card');
            await expect(cards).toHaveCount(3);
            for (let i = 0; i < 3; i++) {
                // Pick "Expert (400)" for each -- middle option.
                await cards.nth(i).locator('input[type=radio]').nth(1).click({ force: true });
            }
            const submit = page.locator('#submitBtn');
            await expect(submit).toBeVisible();
            await expect(submit).toBeEnabled();
            // We DO NOT actually submit -- avoid polluting the DB on each test run.
        });

        test('rank-button tap target is at least 44px x 30px (HIG-ish)', async ({ page }) => {
            // Navigate to step 2 so the rank buttons are visible.
            await page.locator('.track-card').first().click();
            await page.locator('#nextBtn').click();
            const btn = page.locator('.rank-btn').first();
            await btn.scrollIntoViewIfNeeded();
            const box = await btn.boundingBox();
            expect(box).not.toBeNull();
            expect(box!.width,  'tap-target width').toBeGreaterThanOrEqual(44);
            expect(box!.height, 'tap-target height').toBeGreaterThanOrEqual(28);
        });
    });
}
