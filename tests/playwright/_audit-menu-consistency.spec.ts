import { test, expect, Page } from '@playwright/test';
import { BASE, USERS, PINS, login, pageHealth, collectPageErrors, onLoginPage } from './support/hub';

/**
 * OPERATOR-REQUESTED CRITICAL GUI AUDIT (2026-07-24, overnight):
 * "do a complete gui validation using all the test accounts … and be critical and
 *  detect errors or issues, like … register links spread out under 3 menu items."
 *
 * One REAL PIN login per role (PIN budget: 5 requests/hour/account — this spec
 * spends exactly one send-code + one planted row per role), then IN THE SAME
 * SESSION: capture the full primary-nav structure, assert the §301c consistency
 * rules, and sweep every internal page reachable from the nav checking health +
 * console errors. The structure JSON is logged for human review.
 *
 * Consistency rules asserted:
 *  R1 every visible "(Register)"/"(Claim)" item lives inside the "Register/Update" fold-out
 *     (§319: item labels no longer carry "(Register)" — the menu name says it; "(Claim)"
 *     remains on Travel Reimbursement)
 *  R2 no href appears twice anywhere in the primary nav
 *  R3 "My Tasks" (when present) is a PLAIN link, not a fold-out
 *  (R4 retired by §319 — items inside Register/Update carry NO "(Register)" suffix)
 */

type NavLink = { label: string; href: string };
type NavNode =
    | { type: 'link'; label: string; href: string }
    | { type: 'section'; label: string; items: NavLink[]; subs: { label: string; items: NavLink[] }[] };

async function extractNav(page: Page): Promise<NavNode[]> {
    return page.evaluate(() => {
        const nav = document.querySelector('nav.primary');
        if (!nav) return [] as any[];
        const out: any[] = [];
        const linkOf = (a: Element) => ({
            label: (a.textContent ?? '').replace(/\s+/g, ' ').trim(),
            href: a.getAttribute('href') ?? '',
        });
        for (const el of Array.from(nav.children)) {
            if (el.tagName === 'A') {
                out.push({ type: 'link', ...linkOf(el) });
            } else if (el.tagName === 'DETAILS') {
                const label = (el.querySelector(':scope > summary .nav-section-label')?.textContent ?? '').trim();
                const box = el.querySelector(':scope > .nav-section-items');
                const items = box ? Array.from(box.querySelectorAll(':scope > a')).map(linkOf) : [];
                const subs = box ? Array.from(box.querySelectorAll(':scope > details')).map(d => ({
                    label: (d.querySelector(':scope > summary .nav-section-label')?.textContent ?? '').trim(),
                    items: Array.from(d.querySelectorAll('a')).map(linkOf),
                })) : [];
                out.push({ type: 'section', label, items, subs });
            }
        }
        return out;
    });
}

function allLinks(nav: NavNode[]): { label: string; href: string; where: string }[] {
    const all: { label: string; href: string; where: string }[] = [];
    for (const n of nav) {
        if (n.type === 'link') all.push({ label: n.label, href: n.href, where: '(top level)' });
        else {
            for (const i of n.items) all.push({ label: i.label, href: i.href, where: n.label });
            for (const s of n.subs) for (const i of s.items) all.push({ label: i.label, href: i.href, where: `${n.label} > ${s.label}` });
        }
    }
    return all;
}

const ROLES: { role: string; email: string; pin: string }[] = [
    { role: 'organizer', email: USERS.organizer, pin: PINS.organizer },
    { role: 'speaker',   email: USERS.speaker,   pin: PINS.speaker },
    { role: 'volunteer', email: USERS.volunteer, pin: PINS.volunteer },
    { role: 'sponsor',   email: USERS.sponsor,   pin: PINS.sponsor },
    { role: 'attendee',  email: USERS.attendee,  pin: PINS.attendee },
];

for (const { role, email, pin } of ROLES) {
    test.describe(`menu audit: ${role}`, () => {
        test.skip(!email || !pin, `no credentials for ${role}`);
        test.use({ viewport: { width: 1440, height: 900 } });   // desktop — matches the operator's screenshots
        test.setTimeout(600_000);

        test(`${role}: nav consistency + full page sweep`, async ({ page }) => {
            const consoleErrors = collectPageErrors(page);
            await login(page, email, pin);

            const nav = await extractNav(page);
            console.log(`\n===== NAV STRUCTURE [${role}] =====\n` + JSON.stringify(nav, null, 1));
            await page.screenshot({ path: `audit-out/nav-${role}.png`, fullPage: false });
            expect(nav.length, 'primary nav must render').toBeGreaterThan(0);

            const links = allLinks(nav);
            const problems: string[] = [];

            // R1: every "(Register)"/"(Claim)" item must live inside the Register fold-out.
            // DELIBERATE exceptions (operator rulings, §297): the attendee Party is ONE
            // prominent top-level entry; the exhibitor-sponsor Party sits under
            // "Exhibitor & Booth Details".
            for (const l of links) {
                const partyException =
                    /^party/i.test(l.label)
                    && ((role === 'attendee' && l.where === '(top level)')
                        || (role === 'sponsor' && /exhibitor/i.test(l.where)));
                if (/\((register|claim)\)/i.test(l.label) && !/^register/i.test(l.where) && !partyException) {
                    problems.push(`R1 [${role}]: "${l.label}" is under "${l.where}", expected the "Register/Update" menu`);
                }
            }
            // R2: no duplicate hrefs in the primary nav.
            // §322k exception: /Sessions/Slides is DELIBERATELY duplicated for speakers
            // (Speaker Info → Preparing My Session AND Event Info — operator ruling).
            const seen = new Map<string, string>();
            for (const l of links) {
                const key = l.href.toLowerCase();
                if (key === '/sessions/slides') continue;
                if (seen.has(key)) problems.push(`R2 [${role}]: "${l.href}" appears both in "${seen.get(key)}" and "${l.where}"`);
                else seen.set(key, l.where);
            }
            // R3: "My Tasks" must be a plain link, not a fold-out
            for (const n of nav) {
                if (n.type === 'section' && /^my tasks$/i.test(n.label)) {
                    problems.push(`R3 [${role}]: "My Tasks" renders as a fold-out with ${n.items.length} items — expected a plain link`);
                }
            }
            // (R4 retired by §319: the menu is named "Register/Update", so its items carry
            //  no "(Register)" suffix any more.)

            // Full internal-page sweep in this session: health + console errors.
            const sweepFailures: string[] = [];
            const internal = [...new Set(links.map(l => l.href))]
                .filter(h => h.startsWith('/') && !h.startsWith('//'));
            internal.push('/Contributors');
            for (const path of internal) {
                const before = consoleErrors.length;
                const resp = await page.goto(`${BASE}${path}`, { waitUntil: 'domcontentloaded' });
                if (!resp || resp.status() !== 200) { sweepFailures.push(`${path}: HTTP ${resp?.status()}`); continue; }
                if (onLoginPage(page)) { sweepFailures.push(`${path}: bounced to login`); continue; }
                // /Party is a DELIBERATE standalone invite-style page (Layout = null, own
                // skeleton) — skip the shared-layout landmark checks there (§234 note:
                // whether it should adopt the hub chrome is an operator design call).
                if (path.toLowerCase() !== '/party') {
                    const health = await pageHealth(page);
                    for (const h of health) sweepFailures.push(`${path}: ${h}`);
                }
                for (const e of consoleErrors.slice(before)) sweepFailures.push(`${path}: ${e}`);
            }
            // /Contributors: titles removed (operator 2026-07-24)
            await page.goto(`${BASE}/Contributors`, { waitUntil: 'domcontentloaded' });
            const contribText = await page.locator('main').innerText();
            if (/Microsoft MVP|Software Central/i.test(contribText)) {
                problems.push(`[${role}] /Contributors still shows titles (expected names + LinkedIn only)`);
            }

            console.log(`\n===== SWEEP [${role}] ${internal.length} pages =====`);
            if (sweepFailures.length) console.log('SWEEP FINDINGS:\n' + sweepFailures.join('\n'));
            if (problems.length) console.log('CONSISTENCY FINDINGS:\n' + problems.join('\n'));

            expect(problems.concat(sweepFailures), '\n' + problems.concat(sweepFailures).join('\n')).toEqual([]);
        });
    });
}
