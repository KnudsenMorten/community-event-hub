import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { BASE, USERS, PINS, NARROW, login, narrowOnly } from './support/hub';

/**
 * Accessibility (a11y) smoke — axe-core via @axe-core/playwright.
 *
 * Covers the participant-facing pages the a11y pass audited (REQUIREMENTS
 * "Accessibility"). We assert ZERO violations at the WCAG 2.1 A + AA tags,
 * which is the conformance target the pass set.
 *
 * Two tiers:
 *   1. ANONYMOUS pages (Login + the public survey) need no PIN, so they run on
 *      every `npm test` against DEV + PROD.
 *   2. AUTHENTICATED hubs run only on the narrow viewport and SELF-SKIP when the
 *      matching planted PIN is absent — the established convention in
 *      support/hub.ts, so a contributor with no DEV DB still gets green.
 *
 * Scope note: axe is run with the same WCAG-AA ruleset on every page; if a
 * single shared component regresses (skip-link, focus ring, nav landmark, lang)
 * it trips here regardless of which page surfaced it.
 */

const WCAG_TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'];

const TARGETS: { name: string; baseUrl: string }[] = (() => {
    const dev  = { name: 'DEV',  baseUrl: 'https://dev.eldk27.eventhub.expertslive.dk' };
    const prod = { name: 'PROD', baseUrl: 'https://eldk27.eventhub.expertslive.dk' };
    const env = process.env.TARGET?.toUpperCase();
    if (env === 'DEV')  return [dev];
    if (env === 'PROD') return [prod];
    return [dev, prod];
})();

const SURVEY_SLUG = process.env.SURVEY_SLUG ?? 'eldk27-topics';

async function expectNoViolations(page: import('@playwright/test').Page, label: string) {
    const results = await new AxeBuilder({ page }).withTags(WCAG_TAGS).analyze();
    const summary = results.violations
        .map(v => `  [${v.impact ?? 'n/a'}] ${v.id}: ${v.help} (${v.nodes.length} node(s))`)
        .join('\n');
    expect(results.violations, `${label} a11y violations:\n${summary}`).toEqual([]);
}

// ---------------------------------------------------------------------------
// Tier 1 — anonymous, no PIN needed.
// ---------------------------------------------------------------------------
for (const { name, baseUrl } of TARGETS) {
    test.describe(`@a11y ${name} anonymous pages`, () => {
        test('Login (email step) has no WCAG A/AA violations', async ({ page }) => {
            await page.goto(`${baseUrl}/Login`, { waitUntil: 'domcontentloaded' });
            await expectNoViolations(page, 'Login');
        });

        test('public survey wizard has no WCAG A/AA violations', async ({ page }) => {
            await page.goto(`${baseUrl}/survey/${SURVEY_SLUG}`, { waitUntil: 'domcontentloaded' });
            await expectNoViolations(page, 'Survey wizard');
        });

        test('survey results dashboard has no WCAG A/AA violations', async ({ page }) => {
            await page.goto(`${baseUrl}/survey/${SURVEY_SLUG}/results`, { waitUntil: 'domcontentloaded' });
            await expectNoViolations(page, 'Survey results');
        });
    });
}

// ---------------------------------------------------------------------------
// Tier 2 — authenticated participant hubs (narrow viewport only; self-skip
// when the planted PIN is absent). Each entry is a [role, email, pin, paths].
// ---------------------------------------------------------------------------
const AUTHED: { role: string; email: string; pin: string; paths: string[] }[] = [
    { role: 'speaker',   email: USERS.speaker,   pin: PINS.speaker,
      paths: ['/', '/Speaker', '/Forms/Speaker', '/Forms/Hotel', '/Tasks', '/Profile'] },
    { role: 'volunteer', email: USERS.volunteer, pin: PINS.volunteer,
      paths: ['/', '/Volunteer/MyTasks', '/Forms/Lunch', '/Forms/Swag', '/Profile'] },
    { role: 'attendee',  email: USERS.attendee,  pin: PINS.attendee,
      paths: ['/', '/Attendee', '/Attendee/MyEvent', '/Profile'] },
    { role: 'sponsor',   email: USERS.sponsor,   pin: PINS.sponsor,
      paths: ['/', '/Sponsor', '/Sponsor/Tasks', '/Sponsor/CaptureLead', '/Profile'] },
];

for (const { role, email, pin, paths } of AUTHED) {
    test.describe(`@a11y ${role} hub pages`, () => {
        narrowOnly();
        test.beforeEach(() => {
            test.skip(!email || !pin, `no planted PIN for ${role}; set ${role.toUpperCase()}_PIN to run`);
        });

        test(`${role} pages have no WCAG A/AA violations`, async ({ page }) => {
            await login(page, email, pin);
            const failures: string[] = [];
            for (const path of paths) {
                await page.goto(`${BASE}${path}`, { waitUntil: 'domcontentloaded' });
                // Skip pages the role bounced off (covered by feature-role-hubs).
                if (new URL(page.url()).pathname.toLowerCase().startsWith('/login')) continue;
                const r = await new AxeBuilder({ page }).withTags(WCAG_TAGS).analyze();
                if (r.violations.length) {
                    failures.push(`${path}:\n` + r.violations
                        .map(v => `    [${v.impact ?? 'n/a'}] ${v.id}: ${v.help}`).join('\n'));
                }
            }
            expect(failures, '\n' + failures.join('\n')).toEqual([]);
        });
    });
}
