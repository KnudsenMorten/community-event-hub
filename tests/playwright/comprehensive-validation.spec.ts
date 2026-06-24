import {
    test, expect, login, narrowOnly, USERS, PINS,
    targetBase, collectPageErrors, pageHealth, signedInMarker, onLoginPage, Page,
} from './support/hub';

/**
 * COMPREHENSIVE POST-DEPLOY GUI VALIDATION  (@validate)
 * =====================================================
 * A single, data-driven sweep over EVERY key route in the app, designed to be
 * run as a post-deploy gate after a DEV or PROD deploy (see
 * tools/run-post-deploy-validation.ps1 and docs/TESTS.md).
 *
 * For every page it asserts the page is genuinely ALIVE — not a blank/dead page
 * and not a 500:
 *   1. renders (the route exists: not 404; not 5xx)             — deploy-lag + crash
 *   2. no uncaught JS / console.error on load                   — broken page
 *   3. shared landmarks present (main / header / footer, html lang) — a11y skeleton
 *   4. mobile (~360px) layout intact (no horizontal overflow)   — mobile-first
 *   5. body has real content (heading + copy) — an honest empty state counts,
 *      a blank page does not.
 *   6. (data-driven pages) a key content marker OR an honest empty state.
 *
 * Two layers, mirroring the established suite design (support/hub.ts):
 *   • ANONYMOUS layer — public routes + the "role required?" contract. No DB,
 *     runs on DEV + PROD. Catches deploy-lag (a route that 404s because the
 *     environment is behind main) and crashes with zero setup.
 *   • AUTHENTICATED layer — real PIN login per role, deep page health on every
 *     route that role can reach, PLUS negative role-gating (a non-organizer is
 *     refused the Organizer area). DEV-only; self-skips without a planted PIN,
 *     so a contributor with no DEV DB still gets a green run.
 *
 * It does NOT submit forms or send mail (read-only) and does NOT duplicate the
 * write-path coverage in admin-mobile / portal-mobile / feature-* — it is the
 * breadth gate (every route alive) those depth suites assume.
 */

const BASE = targetBase();
const TARGET = (process.env.CEH_BASE_URL ? 'TARGET' : (process.env.TARGET?.toUpperCase() ?? 'DEV'));
const SURVEY_SLUG = process.env.SURVEY_SLUG ?? 'eldk27-topics';

/** Navigate + run the full health battery on one path. Returns problems[] (empty
 *  == healthy). `expectMarker` (optional) is a content assertion: at least one of
 *  the given substrings (case-insensitive) must appear in the body, OR an honest
 *  empty-state phrase. */
async function validatePage(
    page: Page,
    path: string,
    opts: { markers?: string[]; allowEmptyState?: boolean } = {},
): Promise<string[]> {
    const problems: string[] = [];
    const errs = collectPageErrors(page);
    let resp;
    try {
        resp = await page.goto(`${BASE}${path}`, { waitUntil: 'domcontentloaded' });
    } catch (e) {
        return [`${path}: navigation threw ${e}`];
    }
    const status = resp?.status() ?? 0;
    if (status === 0)            return [`${path}: no response`];
    if (status === 404)         return [`${path}: HTTP 404 — route missing (deploy lag / unregistered page)`];
    if (status >= 500)          return [`${path}: HTTP ${status} — server error`];
    // 2xx/3xx that landed on a real page is fine; an authed-only page hit
    // anonymously lands on /Login (200) — that's a contract, handled by callers.

    const health = await pageHealth(page);
    problems.push(...health.map(h => `${path}: ${h}`));

    if (opts.markers && opts.markers.length) {
        const body = (await page.locator('body').innerText()).toLowerCase();
        const hasMarker = opts.markers.some(m => body.includes(m.toLowerCase()));
        const emptyState = /no .* (yet|found|match)|nothing (here|to show)|not (active|available|found)|empty|coming soon|no data/i.test(body);
        if (!hasMarker && !(opts.allowEmptyState && emptyState)) {
            problems.push(`${path}: none of [${opts.markers.join(', ')}] present and no honest empty state`);
        }
    }

    if (errs.length) problems.push(...errs.map(e => `${path}: ${e}`));
    return problems;
}

// ===========================================================================
// LAYER 1 — ANONYMOUS public routes + the "login required" contract.
// Runs on whatever TARGET resolves to (DEV or PROD). No DB / no PIN.
// ===========================================================================
test.describe(`@validate ${TARGET} public routes (anonymous)`, () => {
    narrowOnly();

    // Public, no-login pages that must render real content (or an honest empty
    // state) — NOT a blank/dead page. Markers are env-neutral page content.
    const PUBLIC: { path: string; markers?: string[]; allowEmptyState?: boolean }[] = [
        { path: '/Contributors',                      markers: ['contributor', 'thank'] , allowEmptyState: true },
        { path: '/Sessions',                          markers: ['session'],               allowEmptyState: true },
        { path: '/Speakers',                          markers: ['speaker'],               allowEmptyState: true },
        { path: '/Sponsors',                          markers: ['sponsor'],               allowEmptyState: true },
        { path: '/volunteer/signup',                  markers: ['volunteer', 'sign up', 'name'] },
        { path: `/survey/${SURVEY_SLUG}`,             markers: ['track', 'topic', 'survey'] },
        { path: `/survey/${SURVEY_SLUG}/results`,     markers: ['result', 'response', 'rank'], allowEmptyState: true },
        { path: '/Login',                             markers: ['sign-in code', 'email', 'sign in'] },
    ];

    for (const p of PUBLIC) {
        test(`public ${p.path} is alive (renders, no console error, mobile, landmarks)`, async ({ page }) => {
            const problems = await validatePage(page, p.path, { markers: p.markers, allowEmptyState: p.allowEmptyState });
            expect(problems, '\n' + problems.join('\n')).toEqual([]);
        });
    }

    test('MasterClass slug route resolves (renders a page, not a 500)', async ({ page }) => {
        // Unknown slug must render the friendly "not found" page, never a 500.
        const problems = await validatePage(page, '/MasterClass/__pw-unknown-slug__',
            { markers: ['master class', 'not found'], allowEmptyState: true });
        expect(problems, '\n' + problems.join('\n')).toEqual([]);
    });

    test('login is REQUIRED for participant + organizer areas (anonymous is bounced)', async ({ page }) => {
        // Authenticated routes must NOT serve content to an anonymous visitor:
        // they redirect to /Login (the [Authorize] contract). A 200 page of real
        // content here would be a serious auth regression.
        const guarded = [
            '/Tasks', '/Profile', '/Forms/Hotel', '/Forms/Dinner', '/Forms/Lunch',
            '/Forms/Swag', '/Forms/Travel', '/Forms/OnboardingWizard',
            '/Organizer/Dashboard', '/Organizer/Participants', '/Organizer/EmailCenter',
            '/Sponsor', '/Speaker', '/Attendee', '/Volunteer/MyTasks',
        ];
        const leaks: string[] = [];
        for (const path of guarded) {
            const resp = await page.goto(`${BASE}${path}`, { waitUntil: 'domcontentloaded' });
            const status = resp?.status() ?? 0;
            // 404 == deploy lag (route not deployed) — reported by the authed sweep,
            // not an auth leak. We only flag a route that served content anonymously.
            if (status === 404) continue;
            if (!onLoginPage(page) && (await signedInMarker(page).count()) === 0) {
                // Did it actually serve the area's content rather than bouncing?
                const body = (await page.locator('body').innerText()).toLowerCase();
                const bounced = body.includes('sign in') || body.includes('sign-in code');
                if (!bounced) leaks.push(`${path}: served to an anonymous visitor (HTTP ${status})`);
            }
        }
        expect(leaks, '\n' + leaks.join('\n')).toEqual([]);
    });

    // NOTE: full per-page WCAG A/AA (axe-core) is owned by a11y.spec.ts and is
    // NOT duplicated here. This validator's a11y dimension is landmark presence
    // (pageHealth: main/header/footer + html lang on EVERY route) — the
    // structural skeleton that must exist even on a route a11y.spec.ts doesn't
    // enumerate. Run a11y.spec.ts (`npm run test:a11y`) for the contrast/ARIA
    // ruleset.
});

// ===========================================================================
// LAYER 2 — AUTHENTICATED deep route health, per role. DEV-only; self-skips
// without a planted PIN. Each role sweeps EVERY route it can reach.
// ===========================================================================

/** Every route grouped by the role that owns it. The organizer set is the full
 *  Organizer area (every page named in the task), incl. the recently-added
 *  Buckets/SoMe/SessionEvaluations/VolunteerStructure/Sessionize-endpoint pages
 *  and the hotel rooming-list (DataGrid). */
const ORGANIZER_ROUTES = [
    '/Organizer', '/Organizer/Index', '/Organizer/Dashboard', '/Organizer/Overview',
    '/Organizer/Onboarding', '/Organizer/ActionQueue', '/Organizer/PreselectionQueue',
    '/Organizer/Participants', '/Organizer/Attendees', '/Organizer/Speakers',
    '/Organizer/Sponsors', '/Organizer/Sessions', '/Organizer/SessionEvaluations',
    '/Organizer/SessionQuestions',
    '/Organizer/EmailCenter', '/Organizer/Broadcast', '/Organizer/EmailLog',
    '/Organizer/DataGrid', '/Organizer/TasksTable',
    '/Organizer/Swag', '/Organizer/Lunch', '/Organizer/TravelReimbursements',
    '/Organizer/Hotels', '/Organizer/HotelAssignments',
    '/Organizer/GroupPhotos', '/Organizer/AppGame', '/Organizer/Graphics',
    '/Organizer/SendInvitations', '/Organizer/SpeakerReminders',
    '/Organizer/SessionizeImport', '/Organizer/SessionizeEndpointSettings',
    '/Organizer/BucketAllocation', '/Organizer/VolunteerStructure',
    '/Organizer/SoMeQueue', '/Organizer/SoMeSettings',
    '/Organizer/AssetLocations', '/Organizer/CalendarSettings', '/Organizer/SecureLink',
    '/Organizer/SponsorAdmin/Index', '/Organizer/SponsorAdmin/Dashboard',
    '/Organizer/SponsorAdmin/Tasks', '/Organizer/SponsorAdmin/Leads',
];

const ROLE_ROUTES: Record<string, { email: string; pin: string; routes: string[] }> = {
    organizer: { email: USERS.organizer, pin: PINS.organizer, routes: ORGANIZER_ROUTES },
    speaker:   { email: USERS.speaker,   pin: PINS.speaker,
        routes: ['/', '/Speaker', '/Speaker/Questions', '/Speaker/Graphics',
                 '/Forms/Speaker', '/Forms/Hotel', '/Forms/Dinner', '/Forms/Travel',
                 '/Forms/OnboardingWizard', '/Tasks', '/Profile', '/Resources'] },
    volunteer: { email: USERS.volunteer, pin: PINS.volunteer,
        routes: ['/', '/Volunteer/MyTasks', '/Volunteer/Supervisor',
                 '/Forms/VolunteerWizard', '/Forms/Lunch', '/Forms/Swag', '/Tasks', '/Profile', '/Resources'] },
    attendee:  { email: USERS.attendee,  pin: PINS.attendee,
        routes: ['/', '/Attendee', '/Attendee/MyEvent', '/Tasks', '/Profile', '/Resources'] },
    sponsor:   { email: USERS.sponsor,   pin: PINS.sponsor,
        routes: ['/', '/Sponsor', '/Sponsor/CompanyDetails', '/Sponsor/Tasks', '/Sponsor/Logistics',
                 '/Sponsor/Leads', '/Sponsor/Contact', '/Sponsor/CaptureLead', '/Profile', '/Resources'] },
};

for (const [role, cfg] of Object.entries(ROLE_ROUTES)) {
    test.describe(`@validate ${role} routes (authenticated, DEV)`, () => {
        narrowOnly();
        test.skip(!cfg.email || !cfg.pin, `no planted PIN for ${role}; set ${role.toUpperCase()}_PIN to run`);

        test(`${role}: every reachable route is alive (renders, no console error, mobile, landmarks)`, async ({ page }) => {
            await login(page, cfg.email, cfg.pin);
            const problems: string[] = [];
            for (const path of cfg.routes) {
                const sub = await validatePage(page, path);
                // A route this role can't reach bounces to /Login or shows the
                // organizers-only message — that's the role contract, not a
                // dead page, so only count genuine render/health failures.
                if (onLoginPage(page)) continue;
                problems.push(...sub);
            }
            expect(problems, '\n' + problems.join('\n')).toEqual([]);
        });
    });
}

// ===========================================================================
// LAYER 2b — NEGATIVE role-gating. A non-organizer must NOT see the Organizer
// area. Organizer pages return 200 with an "organizers only" message (not a
// redirect), so we assert the refusal copy, never the page's real content.
// ===========================================================================
const NON_ORGANIZER_ROLES = ['speaker', 'volunteer', 'attendee', 'sponsor'] as const;

for (const role of NON_ORGANIZER_ROLES) {
    const cfg = ROLE_ROUTES[role];
    test.describe(`@validate ${role} is refused the Organizer area (role-gating)`, () => {
        narrowOnly();
        test.skip(!cfg.email || !cfg.pin, `no planted PIN for ${role}; set ${role.toUpperCase()}_PIN to run`);

        test(`${role} cannot read organizer pages`, async ({ page }) => {
            await login(page, cfg.email, cfg.pin);
            const probe = [
                '/Organizer/Dashboard', '/Organizer/Participants',
                '/Organizer/EmailCenter', '/Organizer/SponsorAdmin/Leads',
            ];
            const leaks: string[] = [];
            for (const path of probe) {
                const resp = await page.goto(`${BASE}${path}`, { waitUntil: 'domcontentloaded' });
                const status = resp?.status() ?? 0;
                if (status === 404) continue;            // not deployed here — not a leak
                if (onLoginPage(page)) continue;          // bounced — refused
                const body = (await page.locator('body').innerText()).toLowerCase();
                const refused = /organizers? only|for organizers|access denied|not authori|this (page|area) is for organizers/i.test(body);
                if (!refused) leaks.push(`${path}: ${role} was NOT refused (no organizers-only message; HTTP ${status})`);
            }
            expect(leaks, '\n' + leaks.join('\n')).toEqual([]);
        });
    });
}
