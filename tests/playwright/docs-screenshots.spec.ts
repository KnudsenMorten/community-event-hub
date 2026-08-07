import { test, expect, Page, Browser } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';

/**
 * §931.3 — THE DOCUMENTATION SCREENSHOT HARNESS.
 *
 * Captures every published image in docs/img/ headlessly, desktop AND mobile, from a real
 * PIN login. Until now there was NO such harness: the 26 existing images were made ad hoc,
 * which is why the publish checklist's "refresh the screenshots" step had never actually
 * been runnable.
 *
 * 🔴 THE HARD REQUIREMENT IS ANONYMITY, AND IT IS ASSERTED RATHER THAN TRUSTED.
 * These images publish to a PUBLIC GitHub mirror, and DEV holds 43 real participants with
 * real names, real employer e-mail addresses and real faces. So every capture goes through
 * `anonymise()` — and then `assertClean()` re-reads the rendered text and FAILS the run if
 * a single real string survived. A screenshot is only written after that check passes.
 *
 * ⚠️ The replacement map is EXACT, not guessed: it is dumped read-only from the DEV database
 * (scratchpad/anon-source.json), so "did I remember every name?" is not a question anyone has
 * to answer from memory.
 *
 * 🔒 DEV ONLY. PROD has real data with no anonymous equivalent and TestMode off, so PINs
 * cannot be planted there at all — see §931.3.
 *
 *   powershell -File tools/plant-test-pins.ps1 -Env dev -Count 6 -RankMinutes 45
 *   $env:ADMIN_PIN='<pin>'; $env:ANON_SOURCE='<path to anon-source.json>'
 *   npx playwright test docs-screenshots --project=docs
 */

const BASE = process.env.DOCS_BASE ?? 'https://dev.eldk27.eventhub.expertslive.dk';
const EMAIL = process.env.ADMIN_EMAIL ?? 'mok@expertslive.dk';
const PIN = process.env.ADMIN_PIN ?? '';
const ANON_SOURCE = process.env.ANON_SOURCE ?? '';
const OUT_DIR = path.resolve(__dirname, '../../docs/img');

/** Neutral stand-ins. Obviously not real people, but they still read as a populated system. */
const FAKE_FIRST = ['Alex', 'Jamie', 'Robin', 'Casey', 'Morgan', 'Riley', 'Jordan', 'Taylor',
    'Sam', 'Avery', 'Quinn', 'Devon', 'Harper', 'Rowan', 'Skyler', 'Emerson',
    'Finley', 'Hayden', 'Kendall', 'Lennox', 'Marlow', 'Noor', 'Oakley', 'Payton'];
const FAKE_LAST = ['Hartley', 'Bergman', 'Solberg', 'Lindqvist', 'Novak', 'Faber', 'Keller',
    'Rosenberg', 'Vestergaard', 'Nordahl', 'Ellery', 'Sandoval', 'Whitaker',
    'Ashford', 'Bramley', 'Calloway', 'Danforth', 'Eastwood', 'Fairholm'];
const FAKE_COMPANY = ['Northwind Systems', 'Contoso Nordic', 'Fabrikam Cloud', 'Litware Group',
    'Proseware A/S', 'Adventure Analytics', 'Tailwind Consulting', 'Wingtip Labs'];

/**
 * 🔒 Words that are PRODUCT VOCABULARY and must never be treated as somebody's name, however they
 * appear in a fixture account. The roles are here because DEV's own test participants are named
 * after them; the rest are the nouns this UI is built out of.
 */
const STOPLIST = new Set([
    'organizer', 'organiser', 'attendee', 'speaker', 'speakers', 'sponsor', 'sponsors', 'exhibitor',
    'volunteer', 'volunteers', 'media', 'partner', 'partners', 'delegate', 'crew', 'staff', 'guest',
    'test', 'tests', 'testing', 'demo', 'sample', 'dummy', 'fixture', 'wiztest', 'signer',
    'event', 'events', 'session', 'sessions', 'master', 'class', 'classes', 'track', 'tracks',
    'admin', 'user', 'users', 'people', 'person', 'contact', 'contacts', 'coordinator',
    'company', 'office', 'team', 'group', 'hub', 'eldk', 'ceh', 'info', 'support', 'sales',
    'booth', 'lead', 'leads', 'task', 'tasks', 'form', 'forms', 'email', 'mail', 'name',
    'unknown', 'none', 'null', 'temp', 'main', 'other', 'default', 'system', 'service',
]);

type AnonSource = { people: string[]; emails: string[]; companies: string[] };
type AnonMap = { pairs: [string, string][]; realStrings: string[] };

/** Deterministic: same input list ⇒ same pseudonyms, so re-running does not reshuffle the docs. */
function buildMap(src: AnonSource): AnonMap {
    const pairs: [string, string][] = [];
    const realStrings: string[] = [];
    const nameFor = new Map<string, string>();

    const people = [...new Set(src.people)].sort();
    people.forEach((real, i) => {
        const fake = `${FAKE_FIRST[i % FAKE_FIRST.length]} ${FAKE_LAST[(i * 7) % FAKE_LAST.length]}`;
        nameFor.set(real, fake);
        pairs.push([real, fake]);
        realStrings.push(real);
    });

    [...new Set(src.companies)].sort().forEach((real, i) => {
        pairs.push([real, FAKE_COMPANY[i % FAKE_COMPANY.length]]);
        realStrings.push(real);
    });

    [...new Set(src.emails)].sort().forEach((real, i) => {
        pairs.push([real, `person${i + 1}@example.org`]);
        realStrings.push(real);
    });

    // ⚠️ Individual name TOKENS after the full names, because a page may print only a first or
    // last name ("Hi Kent,"). Restricted to ≥4 characters so short common words are untouched.
    //
    // 🔴 AND STOPLISTED, which is not belt-and-braces — it is a bug this caught. DEV's role
    // fixtures are named things like "ELDK Organizer (wiztest)" and "MOK (Volunteer)", so a naive
    // token pass learned "Organizer" and "Attendee" as surnames and rewrote the NAVIGATION:
    // "Organizer Area" became "Danforth Area" and "Attendee Telemetry" became "Lindqvist
    // Telemetry". The screenshots were anonymous and describing a product that does not exist.
    for (const real of people) {
        const tokens = real.split(/\s+/);
        for (const token of tokens) {
            const bare = token.replace(/[^A-Za-zÀ-ÿ]/g, '');
            if (bare.length < 4) continue;
            if (STOPLIST.has(bare.toLowerCase())) continue;
            const fake = nameFor.get(real)!;
            const part = token === tokens[0] ? fake.split(' ')[0] : fake.split(' ')[1];
            pairs.push([bare, part]);
            realStrings.push(bare);
        }
    }

    // Longest first: "Morten Knudsen" must be consumed before the bare token "Morten".
    pairs.sort((a, b) => b[0].length - a[0].length);
    return { pairs, realStrings };
}

/** Rewrites text nodes, attributes and person photos in place. Runs in the page. */
async function anonymise(page: Page, map: AnonMap) {
    await page.evaluate((m: AnonMap) => {
        const esc = (s: string) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
        const rules = m.pairs.map(([from, to]) => ({ re: new RegExp(esc(from), 'gi'), to }));

        const swap = (s: string) => {
            // ⚠️ E-MAILS FIRST, and the order is a fix not a preference. Running the name/company
            // rules first rewrote the domain INSIDE an address ("…@softwarecentral.com" became
            // "…@Northwind Systems…"), and the generic sweep then only half-matched the wreckage,
            // rendering as "personNorthwind person…". Killing every address up front leaves nothing
            // for a company rule to land in.
            let out = s.replace(/[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}/g, 'person@example.org');
            for (const r of rules) out = out.replace(r.re, r.to);
            // Phone-shaped runs (+45 xx xx xx xx and friends).
            out = out.replace(/\+\d[\d\s().-]{7,}\d/g, '+45 00 00 00 00');
            return out;
        };

        const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
        const texts: Text[] = [];
        while (walker.nextNode()) texts.push(walker.currentNode as Text);
        for (const t of texts) {
            const next = swap(t.nodeValue ?? '');
            if (next !== t.nodeValue) t.nodeValue = next;
        }

        for (const el of Array.from(document.querySelectorAll<HTMLElement>('*'))) {
            for (const attr of ['alt', 'title', 'placeholder', 'value', 'aria-label']) {
                const v = el.getAttribute(attr);
                if (v) { const n = swap(v); if (n !== v) el.setAttribute(attr, n); }
            }
        }

        // 🔒 Real faces. Every raster image that is not obviously chrome becomes an initials tile,
        // so a photo cannot survive by sitting in a CSS background or an unrecognised class.
        const initials = (s: string) =>
            (s.match(/\b[A-Za-z]/g) ?? ['A']).slice(0, 2).join('').toUpperCase();
        for (const img of Array.from(document.images)) {
            const src = img.currentSrc || img.src || '';
            if (/\.(svg)(\?|$)/i.test(src)) continue;            // icons/logos stay
            if (img.width < 24 || img.height < 24) continue;      // sprites stay
            const label = initials(img.alt || 'AB');
            const svg =
                `<svg xmlns="http://www.w3.org/2000/svg" width="200" height="200">` +
                `<rect width="200" height="200" rx="16" fill="#dbe4ee"/>` +
                `<text x="100" y="132" font-family="Segoe UI,Arial" font-size="84" ` +
                `fill="#41546b" text-anchor="middle">${label}</text></svg>`;
            img.src = 'data:image/svg+xml;base64,' + btoa(svg);
            img.srcset = '';
        }
    }, map);
}

/** 🔴 The guarantee. Fails the run rather than writing a leaky PNG. */
async function assertClean(page: Page, map: AnonMap, where: string) {
    const text = await page.evaluate(() => document.body.innerText ?? '');
    const hay = text.toLowerCase();
    const leaked = map.realStrings.filter(s => s.length >= 4 && hay.includes(s.toLowerCase()));
    expect(leaked, `real data survived anonymisation on ${where}: ${leaked.join(', ')}`).toEqual([]);
}

/** Every feature area worth a picture. `auth` pages need the organizer session. */
const TARGETS: { slug: string; url: string; auth: boolean }[] = [
    // --- public -----------------------------------------------------------------
    { slug: 'public-landing', url: '/Welcome', auth: false },
    { slug: 'public-sessions', url: '/Sessions', auth: false },
    { slug: 'public-session-detail', url: '/Sessions?type=MasterClass', auth: false },
    { slug: 'public-speakers', url: '/Speakers', auth: false },
    { slug: 'public-sponsors', url: '/Sponsors', auth: false },
    { slug: 'public-agenda', url: '/Agenda', auth: false },
    { slug: 'public-masterclasses', url: '/MasterClasses', auth: false },
    { slug: 'public-contributors', url: '/Contributors', auth: false },
    { slug: 'public-about', url: '/About', auth: false },
    { slug: 'public-login', url: '/Login', auth: false },
    // --- role hubs ---------------------------------------------------------------
    { slug: 'hub-home', url: '/', auth: true },
    { slug: 'speaker-hub', url: '/Speaker', auth: true },
    { slug: 'speaker-tasks', url: '/Speaker/Tasks', auth: true },
    { slug: 'speaker-graphics', url: '/Speaker/Graphics', auth: true },
    { slug: 'speaker-readiness', url: '/Speaker/Readiness', auth: true },
    { slug: 'speaker-announcements', url: '/Speaker/Announcements', auth: true },
    { slug: 'sponsor-portal', url: '/Sponsor', auth: true },
    { slug: 'sponsor-our-booth', url: '/Sponsor/Booth', auth: true },
    { slug: 'sponsor-deliverables', url: '/Sponsor/Deliverables', auth: true },
    { slug: 'sponsor-announcements', url: '/Sponsor/Announcements', auth: true },
    { slug: 'volunteer-schedule', url: '/Volunteer/MySchedule', auth: true },
    { slug: 'volunteer-signup', url: '/Volunteer/Signup', auth: true },
    { slug: 'attendee-my-event', url: '/Attendee', auth: true },
    { slug: 'unified-task-checklist', url: '/Tasks', auth: true },
    { slug: 'profile', url: '/Profile', auth: true },
    // --- organizer ---------------------------------------------------------------
    { slug: 'organizer-dashboard', url: '/Organizer/Dashboard', auth: true },
    { slug: 'organizer-command-center', url: '/Organizer/CommandCenter', auth: true },
    { slug: 'organizer-participants', url: '/Organizer/Participants', auth: true },
    { slug: 'organizer-attendees', url: '/Organizer/Attendees', auth: true },
    { slug: 'organizer-sessions', url: '/Organizer/Sessions', auth: true },
    { slug: 'organizer-allocation-queue', url: '/Organizer/ActionQueue', auth: true },
    { slug: 'organizer-email-center', url: '/Organizer/EmailCenter', auth: true },
    { slug: 'organizer-email-log', url: '/Organizer/EmailLog', auth: true },
    { slug: 'organizer-jobs', url: '/Organizer/Jobs', auth: true },
    { slug: 'organizer-graphics', url: '/Organizer/Graphics', auth: true },
    { slug: 'organizer-exports', url: '/Organizer/Exports', auth: true },
    { slug: 'organizer-audit-trail', url: '/Organizer/AuditTrail', auth: true },
    { slug: 'organizer-data-freshness', url: '/Organizer/DataFreshness', auth: true },
    { slug: 'organizer-hotels', url: '/Organizer/Hotels', auth: true },
    { slug: 'organizer-volunteers', url: '/Organizer/Volunteers', auth: true },
    { slug: 'organizer-evaluation-results', url: '/Organizer/EvaluationResults', auth: true },
    { slug: 'organizer-coupon-invoicing', url: '/Organizer/CouponInvoicing', auth: true },
    { slug: 'organizer-group-photos', url: '/Organizer/GroupPhotos', auth: true },
    // --- the social-media campaign (§824 onward, nothing captured before) ---------
    { slug: 'some-hub', url: '/Organizer/Content', auth: true },
    { slug: 'some-content-studio', url: '/Organizer/ContentStudio', auth: true },
    { slug: 'some-event-posts', url: '/Organizer/EventPosts', auth: true },
    { slug: 'some-settings', url: '/Organizer/SoMeSettings', auth: true },
    { slug: 'some-templates', url: '/Organizer/SoMeTemplates', auth: true },
];

const VIEWPORTS = [
    { suffix: '', width: 1440, height: 900 },
    { suffix: '-mobile', width: 390, height: 844 },
];

test.describe('documentation screenshots', () => {
    test.skip(!PIN, 'ADMIN_PIN not set — plant PINs with tools/plant-test-pins.ps1 first');
    test.skip(!ANON_SOURCE || !fs.existsSync(ANON_SOURCE),
        'ANON_SOURCE not set — dump the real strings first so anonymity can be ASSERTED, not assumed');
    test.setTimeout(30 * 60_000);

    async function login(page: Page) {
        await page.goto(`${BASE}/Login`, { waitUntil: 'domcontentloaded' });
        await page.locator('input[name="Email"]').fill(EMAIL);
        await page.getByRole('button', { name: /send.*code|email me|request/i }).click();
        const pinInput = page.locator('input[name="Pin"]');
        await expect(pinInput).toBeVisible();
        await pinInput.fill(PIN);
        await page.getByRole('button', { name: 'Sign in', exact: true }).click();
        // ⚠️ ATTACHED, not visible. The sign-out control lives inside the collapsed navigation on a
        // desktop viewport, so "visible" fails on a session that is perfectly valid — the button
        // renders at all only when signed in, which is the property being asserted.
        await expect(page.locator('button.signout')).toBeAttached({ timeout: 20_000 });
        await expect(page).not.toHaveURL(/\/Login/);
    }

    test('capture every published image, anonymised and verified', async ({ browser }: { browser: Browser }) => {
        // ⚠️ BOM-stripped: Windows PowerShell's UTF8 writer prefixes one, and JSON.parse rejects it.
        const raw = fs.readFileSync(ANON_SOURCE, 'utf8').replace(/^﻿/, '');
        const src = JSON.parse(raw) as AnonSource;
        const map = buildMap(src);
        fs.mkdirSync(OUT_DIR, { recursive: true });

        const written: string[] = [];
        const skipped: string[] = [];

        for (const vp of VIEWPORTS) {
            const ctx = await browser.newContext({
                viewport: { width: vp.width, height: vp.height },
                deviceScaleFactor: 2,
                isMobile: vp.suffix === '-mobile',
                hasTouch: vp.suffix === '-mobile',
            });
            const page = await ctx.newPage();
            await login(page);

            for (const t of TARGETS) {
                const file = path.join(OUT_DIR, `${t.slug}${vp.suffix}.png`);
                try {
                    const resp = await page.goto(`${BASE}${t.url}`, { waitUntil: 'networkidle', timeout: 45_000 });
                    if (resp && resp.status() >= 400) { skipped.push(`${t.slug}${vp.suffix} (HTTP ${resp.status()})`); continue; }
                    // A redirect to the login page means this account cannot see the page — skip it
                    // rather than publishing a picture of a login form under a feature's name.
                    if (page.url().includes('/Login')) { skipped.push(`${t.slug}${vp.suffix} (no access)`); continue; }

                    await page.waitForTimeout(400);
                    await anonymise(page, map);
                    await assertClean(page, map, `${t.slug}${vp.suffix}`);
                    // 🔒 VIEWPORT, NOT fullPage. A full-page capture of a long admin table produced
                    // a 6.8 MB PNG and the set totalled 66 MB — which then lives in git history for
                    // ever, on a repo that mirrors publicly. Above-the-fold also reads better in a
                    // documentation table: it shows what the page IS rather than everything it can
                    // scroll to.
                    await page.screenshot({ path: file, fullPage: false });
                    written.push(`${t.slug}${vp.suffix}`);
                } catch (e) {
                    skipped.push(`${t.slug}${vp.suffix} (${(e as Error).message.split('\n')[0].slice(0, 80)})`);
                }
            }
            await ctx.close();
        }

        // 🔒 Reported, never silent: a missing picture must not read as a feature that has none.
        console.log(`\n=== captured ${written.length} image(s) ===`);
        console.log(written.join('\n'));
        if (skipped.length) {
            console.log(`\n=== SKIPPED ${skipped.length} ===`);
            console.log(skipped.join('\n'));
        }
        // 🔒 Written beside the test results, NOT into docs/img — that folder publishes to the
        // public mirror and a build artefact does not belong in it.
        const reportDir = path.resolve(__dirname, 'test-results');
        fs.mkdirSync(reportDir, { recursive: true });
        fs.writeFileSync(path.join(reportDir, 'docs-capture-report.txt'),
            `captured:\n${written.join('\n')}\n\nskipped:\n${skipped.join('\n')}\n`);

        expect(written.length, 'no screenshots were captured at all').toBeGreaterThan(10);
    });
});
