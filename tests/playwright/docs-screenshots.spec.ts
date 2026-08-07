import { test, expect, Page, Browser } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';

/**
 * §931.3 / §952 / §957 — THE DOCUMENTATION SCREENSHOT HARNESS.
 *
 * Captures every published image in docs/img/ headlessly, desktop AND mobile, from a real
 * PIN login — ONE AUTHENTICATED SESSION PER ROLE.
 *
 * 🔴 THE DEFECT THIS EXISTS TO PREVENT (§952). The harness used to sign in ONCE, as an organizer,
 * and then visit every role's pages. A role-gated page answers an organizer with **HTTP 200 and a
 * polite refusal**, so the capture succeeded, the file was written, and a picture of the access gate
 * was published as the illustration of the feature: *"This page is for sponsors only"* went to the
 * public mirror as the sponsor experience. Every bad shot was a *successful page load*, which is
 * exactly why nothing ever failed. ⇒ **A SUCCESSFUL PAGE LOAD IS NOT A SUCCESSFUL SCREENSHOT.**
 * Two things fix it, and both must stay: per-role sessions (`ROLE_ACCOUNTS` × `TARGETS[].role`),
 * and `refusalReason()` asserted BEFORE `page.screenshot`.
 *
 * 🔒 TARGET IS **PROD** (operator 2026-08-07: *"we dont use dev"* / *"we use only prod"*).
 * That is a reversal of this file's original "DEV ONLY" header, and it is deliberate — see §952a.
 * Planting PIN rows in the PROD database is an explicitly authorised, NAMED exception to the
 * read-only-PROD rule (§957 decision 2), scoped to exactly this and nothing else. It sends no PIN
 * e-mail, which is the operator's other standing constraint (2026-08-02: *"i get tons of pin sign
 * in right now"*).
 *
 * 🔴 DISCLOSURE MODEL — DIM THE PEOPLE, REPLACE THE CONTACT DATA (§952c + §957 decision 1).
 * These images publish to a PUBLIC GitHub mirror and now render REAL production data, so the split
 * below is the whole safety model. It is NOT arbitrary and it must not be "tidied" in either
 * direction:
 *
 *   names · faces · companies  →  DIMMED, real and legible   (`dimPeople`)
 *   e-mail addresses · phones  →  REPLACED with placeholders (`redactContacts`)
 *
 * ⚠️ Operator, verbatim: *"i want you to dim pictures + names, but show them dimmed so they can be
 * read, same with faces"*. So a future session must NOT restore the name anonymiser thinking it is
 * fixing a leak — the real names are INTENDED. Equally it must not weaken the dimming thinking it
 * is decorative. The reason the two halves differ: dimming is about visual EMPHASIS (this is sample
 * data, not the subject of the picture), whereas **a dimmed e-mail address is still a harvestable
 * e-mail address**. Contact data is a disclosure question, not an emphasis one.
 *
 * ⚠️ `assertNoContactData()` therefore did not die with the anonymiser — it NARROWED. It no longer
 * fails on a surviving name; it fails on a surviving address or phone number, so the class of data
 * that still must never appear stays ASSERTED rather than trusted.
 *
 * ⚠️ The replacement map is EXACT, not guessed: dumped read-only from the target database
 * (scratchpad/anon-source.json), so "did I remember every name?" is never answered from memory.
 *
 *   pwsh -File tools/plant-docs-pins.ps1 -Env prod            # -> scratchpad/docs-pins.json
 *   $env:DOCS_PINS='<path>'; $env:ANON_SOURCE='<path to anon-source.json>'
 *   npx playwright test docs-screenshots --project=docs
 */

const BASE = process.env.DOCS_BASE ?? 'https://eldk27.eventhub.expertslive.dk';
const ANON_SOURCE = process.env.ANON_SOURCE ?? '';
const OUT_DIR = path.resolve(__dirname, '../../docs/img');

/**
 * 🔒 THE ACCOUNTS ARE NOT IN THIS FILE, AND THAT IS THE POINT. `tests/` publishes to the public
 * mirror (it is not on the publish denylist), so the five addresses hardcoded here on 2026-08-07
 * were personal e-mail addresses on their way to a public GitHub repo. They now live in
 * `config/docs-screenshot-accounts.json`, and `config/*` IS denylisted.
 */
const ACCOUNTS_FILE = process.env.DOCS_ACCOUNTS
    ?? path.resolve(__dirname, '../../config/docs-screenshot-accounts.json');
/** `{ "<role>": "<planted pin>" }` — written by tools/plant-docs-pins.ps1. */
const PINS_FILE = process.env.DOCS_PINS ?? '';

type RoleAccount = { email: string; role: number };

function readJson<T>(file: string): T {
    // ⚠️ BOM-stripped: Windows PowerShell's UTF8 writer prefixes one, and JSON.parse rejects it.
    return JSON.parse(fs.readFileSync(file, 'utf8').replace(/^﻿/, '')) as T;
}

/**
 * §952 — WHICH ACCOUNT SEES WHICH PAGE. Operator 2026-08-07, after finding published screenshots of
 * *"We couldn't match your ticket yet"* and *"This page is for sponsors only"* on the public site:
 * *"use my test accounts (MOK) for each role … so you always login as these users with pin code"*.
 */
function loadAccounts(): Record<string, RoleAccount> {
    return readJson<{ accounts: Record<string, RoleAccount> }>(ACCOUNTS_FILE).accounts;
}

/** Neutral stand-ins for the data that is REPLACED rather than dimmed. */
const FAKE_EMAIL = (i: number) => `person${i + 1}@example.org`;
const FAKE_PHONE = '+45 00 00 00 00';

/**
 * 🔒 Words that are PRODUCT VOCABULARY and must never be treated as somebody's name, however they
 * appear in a fixture account. The roles are here because the test participants are named after
 * them; the rest are the nouns this UI is built out of.
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
    // 🔴 Added 2026-08-07 after the first PROD run greyed out the NAVIGATION. The live database
    // holds contact rows whose FullName *is* a department ("Info", "Finance", "Marketing") and
    // fixture rows labelled "Test Silver" / "Test Masterclass Speaker" — so the harness learned
    // "Silver", "Masterclass" and "Ticket" as surnames and dimmed the ticket banner, the tier
    // badges and the "Event Info" menu. Same bug as the old "Organizer Area" → "Danforth Area";
    // dimming just makes it quieter, not righter.
    'ticket', 'tickets', 'silver', 'gold', 'bronze', 'platinum', 'masterclass', 'masterclasses',
    'finance', 'marketing', 'active', 'inactive', 'approved', 'member', 'members', 'reassigned',
    'purchaser', 'firstname', 'lastname', 'community', 'guest', 'link', 'day', 'days',
]);

/**
 * Is this string plausibly a PERSON'S name, as opposed to a descriptive fixture label?
 *
 * 🔴 Why this exists. The live database carries rows whose FullName is a whole sentence —
 * `Test-Attendee-1day (test-attendee-1day@expertslive.dk) Attendee 1-day ticket`. Splitting that
 * into tokens teaches the dimmer that "ticket", "1day" and "Attendee" are somebody's surname, and
 * the result is a greyed-out ticket banner on every public page. A real person's name is a few
 * plain words: no address, no parenthetical, no comma-separated description.
 *
 * ⚠️ Such a row is still dimmed AS A WHOLE STRING — it just does not get to donate vocabulary.
 * That is the right trade: these labels are self-evidently test data, whereas the nav bar is the
 * product.
 */
function isPersonalName(s: string): boolean {
    if (/[@()]/.test(s) || s.includes(',')) return false;
    const words = s.trim().split(/\s+/);
    return words.length > 0 && words.length <= 4;
}

type AnonSource = { people: string[]; emails: string[]; companies: string[] };
/**
 * `dim` — real strings that STAY, rendered dimmed (names, name tokens, companies).
 * `emailPairs` / `realEmails` — real strings that are REPLACED, and then asserted absent.
 */
type AnonMap = { dim: string[]; emailPairs: [string, string][]; realEmails: string[] };

/** Deterministic: same input list ⇒ same output, so re-running does not reshuffle the docs. */
function buildMap(src: AnonSource): AnonMap {
    const dim: string[] = [];

    const people = [...new Set(src.people)].sort();
    // ⚠️ A single-word name that IS product vocabulary is skipped outright. The live database has
    // contact rows literally called "Info", "Finance" and "Marketing"; dimming those greys out the
    // "Event Info" menu across every screenshot, and the words identify nobody. A generic
    // department label showing undimmed is the cheaper mistake by a wide margin.
    const isStoplisted = (s: string) => STOPLIST.has(s.trim().toLowerCase());
    for (const real of people) if (!isStoplisted(real)) dim.push(real);
    for (const real of [...new Set(src.companies)].sort()) if (!isStoplisted(real)) dim.push(real);

    // ⚠️ Individual name TOKENS after the full names, because a page may print only a first or
    // last name ("Hi Kent,"). Restricted to ≥4 characters so short common words are untouched.
    //
    // 🔴 AND STOPLISTED, which is not belt-and-braces — it is a bug this caught back when tokens
    // were REWRITTEN rather than dimmed. Role fixtures are named things like "ELDK Organizer
    // (wiztest)", so a naive token pass learned "Organizer" as a surname and rewrote the
    // NAVIGATION: "Organizer Area" became "Danforth Area". Dimming makes the failure quieter but
    // no less wrong — a greyed-out nav bar is a bad screenshot too.
    for (const real of people) {
        if (!isPersonalName(real)) continue;   // fixture labels donate no vocabulary — see above
        for (const token of real.split(/\s+/)) {
            const bare = token.replace(/[^A-Za-zÀ-ÿ]/g, '');
            if (bare.length < 4) continue;
            if (STOPLIST.has(bare.toLowerCase())) continue;
            dim.push(bare);
        }
    }

    const emailPairs: [string, string][] = [];
    const realEmails: string[] = [];
    [...new Set(src.emails)].sort().forEach((real, i) => {
        emailPairs.push([real, FAKE_EMAIL(i)]);
        realEmails.push(real);
    });

    // Longest first: "Morten Knudsen" must be consumed before the bare token "Morten", or the
    // full name ends up as two separately-wrapped tokens with an undimmed space between them.
    const uniqueDim = [...new Set(dim)].sort((a, b) => b.length - a.length);
    emailPairs.sort((a, b) => b[0].length - a[0].length);
    return { dim: uniqueDim, emailPairs, realEmails };
}

/**
 * 🔴 REPLACE — e-mail addresses and phone numbers only (§957 decision 1). Runs in the page.
 * These are contact data, not emphasis: a dimmed address is still a harvestable address.
 */
async function redactContacts(page: Page, map: AnonMap) {
    await page.evaluate((m: AnonMap) => {
        const esc = (s: string) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
        const rules = m.emailPairs.map(([from, to]) => ({ re: new RegExp(esc(from), 'gi'), to }));

        const swap = (s: string) => {
            // The EXACT pairs first (deterministic person1@…, person2@… across re-runs), then a
            // generic sweep as the backstop for any address the dump did not know about.
            let out = s;
            for (const r of rules) out = out.replace(r.re, r.to);
            out = out.replace(/[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}/g, 'person@example.org');
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
            // `value` on a live input is a PROPERTY, not the attribute — the attribute pass above
            // does not touch what is actually rendered in the box.
            if (el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement) {
                const n = swap(el.value); if (n !== el.value) el.value = n;
            }
        }
    }, map);
}

/**
 * 🔴 DIM — names, companies and faces stay REAL and legible, rendered as sample data (§952c).
 * Applied immediately before `page.screenshot`, so the treatment is IN THE PIXELS and cannot be
 * undone by a viewer. Operator: *"dim pictures + names … so they can be read, same with faces"*.
 */
async function dimPeople(page: Page, map: AnonMap) {
    await page.evaluate((names: string[]) => {
        const style = document.createElement('style');
        style.textContent =
            `.ceh-dim-name{opacity:.45!important;filter:grayscale(.6);}` +
            `.ceh-dim-face{opacity:.5!important;filter:grayscale(.45)!important;}`;
        document.head.appendChild(style);

        // 🔒 Guard: an empty list would compile to /()/ and match at every position, wrapping the
        // entire page. A dump that returned no people is a broken dump, not a clean page.
        if (names.length) {
            const esc = (s: string) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
            const re = new RegExp('(' + names.map(esc).join('|') + ')', 'gi');

            const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT, {
                acceptNode: (n: Node) => {
                    const p = (n as Text).parentElement;
                    if (!p) return NodeFilter.FILTER_REJECT;
                    if (['SCRIPT', 'STYLE', 'NOSCRIPT', 'TITLE'].includes(p.tagName)) return NodeFilter.FILTER_REJECT;
                    if (p.closest('.ceh-dim-name')) return NodeFilter.FILTER_REJECT;
                    return NodeFilter.FILTER_ACCEPT;
                },
            });
            const texts: Text[] = [];
            while (walker.nextNode()) texts.push(walker.currentNode as Text);

            for (const t of texts) {
                const v = t.nodeValue ?? '';
                re.lastIndex = 0;
                if (!re.test(v)) continue;
                re.lastIndex = 0;
                const frag = document.createDocumentFragment();
                let last = 0;
                let m: RegExpExecArray | null;
                while ((m = re.exec(v)) !== null) {
                    if (m[0].length === 0) { re.lastIndex++; continue; }
                    if (m.index > last) frag.appendChild(document.createTextNode(v.slice(last, m.index)));
                    const span = document.createElement('span');
                    span.className = 'ceh-dim-name';
                    span.textContent = m[0];
                    frag.appendChild(span);
                    last = m.index + m[0].length;
                }
                if (last < v.length) frag.appendChild(document.createTextNode(v.slice(last)));
                t.parentNode?.replaceChild(frag, t);
            }
        }

        // 🔒 Faces. Every raster image that is not obviously chrome is dimmed — including the
        // promotion graphics, which COMPOSE a speaker's own photo into the artwork, so dimming the
        // whole graphic is the only total treatment available.
        for (const img of Array.from(document.images)) {
            const src = img.currentSrc || img.src || '';
            if (/\.(svg)(\?|$)/i.test(src)) continue;            // icons/logos stay
            if (img.width < 24 || img.height < 24) continue;      // sprites stay
            img.classList.add('ceh-dim-face');
        }
        // ⚠️ A photo can also sit in a CSS background, where `document.images` never sees it.
        // Bounded to ≤400px square: avatars and portraits are small, and an unbounded sweep would
        // grey out full-width hero banners, which are event branding rather than anybody's face.
        for (const el of Array.from(document.querySelectorAll<HTMLElement>('*'))) {
            const bg = getComputedStyle(el).backgroundImage;
            if (!bg || !bg.includes('url(')) continue;
            if (/\.svg|data:image\/svg/i.test(bg)) continue;
            const r = el.getBoundingClientRect();
            if (r.width < 24 || r.height < 24 || r.width > 400 || r.height > 400) continue;
            el.classList.add('ceh-dim-face');
        }
    }, map.dim);
}

/**
 * 🔴 THE REMAINING GUARANTEE (§957). Names are now intended to survive; contact data is not.
 * Fails the run rather than writing a PNG with a real address or phone number in it.
 */
async function assertNoContactData(page: Page, map: AnonMap, where: string) {
    const text = await page.evaluate(() => {
        // Rendered input values and placeholders are VISIBLE, and innerText does not include them.
        const parts = [document.body.innerText ?? ''];
        for (const el of Array.from(document.querySelectorAll<HTMLInputElement>('input,textarea'))) {
            parts.push(el.value ?? '', el.placeholder ?? '');
        }
        return parts.join('\n');
    });
    const hay = text.toLowerCase();

    const leaked = map.realEmails.filter(s => s.length >= 4 && hay.includes(s.toLowerCase()));
    expect(leaked, `a real e-mail address survived redaction on ${where}: ${leaked.join(', ')}`).toEqual([]);

    // Belt-and-braces: an address the dump never knew about is still an address.
    const stray = (text.match(/[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}/g) ?? [])
        .filter(a => !/@example\.org$/i.test(a));
    expect(stray, `an unredacted e-mail address is visible on ${where}: ${stray.join(', ')}`).toEqual([]);

    const phones = (text.match(/\+\d[\d\s().-]{7,}\d/g) ?? [])
        .map(p => p.trim())
        .filter(p => p !== FAKE_PHONE);
    expect(phones, `an unredacted phone number is visible on ${where}: ${phones.join(', ')}`).toEqual([]);
}

/**
 * 🔒 §952 — TEXT THAT MEANS "YOU ARE NOT ALLOWED HERE, OR THERE IS NOTHING FOR YOU".
 * A page containing any of these is NOT a picture of the feature and must never be written.
 * Taken from the two the operator caught plus the sibling gates that render the same way.
 */
const REFUSALS: RegExp[] = [
    /this page is for [a-z ]+ only/i,
    /we couldn.?t match your ticket/i,
    /you are not a [a-z]+/i,
    /access denied|not authoris|not authoriz|forbidden/i,
    /sign in to continue|please sign in/i,
];

/**
 * ⚠️ WEAK SIGNALS — true of a gate, but ALSO true of perfectly good pages. *"Back to hub"* is one
 * of the two things the operator caught, but it is an ordinary navigation link that a real feature
 * page may legitimately carry, so matching it outright would fail valid captures. It counts only on
 * a THIN page: a refusal is a sentence and a link, a feature is a screenful.
 */
const WEAK_REFUSALS: RegExp[] = [/back to hub/i];
const THIN_PAGE_CHARS = 600;

/**
 * Returns why this page must not be photographed, or null if it is a picture of the feature.
 * 🔴 Called BEFORE `page.screenshot`. Without it the run is not trustworthy however green it looks.
 */
async function refusalReason(page: Page, role: string | undefined): Promise<string | null> {
    const text = await page.evaluate(() => document.body.innerText ?? '');
    for (const re of REFUSALS) {
        const m = text.match(re);
        if (m) return `refusal: "${m[0].slice(0, 60)}"`;
    }
    const body = text.replace(/\s+/g, ' ').trim();
    if (body.length < THIN_PAGE_CHARS) {
        for (const re of WEAK_REFUSALS) {
            const m = text.match(re);
            if (m) return `refusal: "${m[0]}" on a ${body.length}-char page`;
        }
    }
    // 🔴 The symptom the operator actually reported: the ORGANIZER nav bar sitting above a page
    // meant for an attendee or a sponsor. Per-role sessions should make this impossible — which is
    // precisely why it is worth asserting, because if it appears the role mapping has broken.
    if (role && role !== 'organizer' && /organizer area/i.test(text)) {
        return `organizer nav rendered on a ${role} page — wrong session`;
    }
    return null;
}

/**
 * Every feature area worth a picture. `role` names the account that must be signed in for the shot
 * to be a picture of the FEATURE rather than of the gate. Public pages carry no role.
 */
const TARGETS: { slug: string; url: string; auth: boolean; role?: string }[] = [
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
    // The 'moment you sign in' shots are a REGULAR participant, not an organizer — an organizer's
    // hub is a different page and would misrepresent what a speaker or volunteer actually lands on.
    { slug: 'hub-home', url: '/', auth: true, role: 'speaker' },
    { slug: 'speaker-hub', url: '/Speaker', auth: true, role: 'speaker' },
    { slug: 'speaker-tasks', url: '/Speaker/Tasks', auth: true, role: 'speaker' },
    { slug: 'speaker-graphics', url: '/Speaker/Graphics', auth: true, role: 'speaker' },
    { slug: 'speaker-readiness', url: '/Speaker/Readiness', auth: true, role: 'speaker' },
    { slug: 'speaker-announcements', url: '/Speaker/Announcements', auth: true, role: 'speaker' },
    { slug: 'sponsor-portal', url: '/Sponsor', auth: true, role: 'sponsor' },
    { slug: 'sponsor-our-booth', url: '/Sponsor/Booth', auth: true, role: 'sponsor' },
    { slug: 'sponsor-deliverables', url: '/Sponsor/Deliverables', auth: true, role: 'sponsor' },
    { slug: 'sponsor-announcements', url: '/Sponsor/Announcements', auth: true, role: 'sponsor' },
    { slug: 'volunteer-schedule', url: '/Volunteer/MySchedule', auth: true, role: 'volunteer' },
    { slug: 'volunteer-signup', url: '/Volunteer/Signup', auth: true, role: 'volunteer' },
    { slug: 'attendee-my-event', url: '/Attendee', auth: true, role: 'attendee' },
    { slug: 'unified-task-checklist', url: '/Tasks', auth: true, role: 'organizer' },
    { slug: 'profile', url: '/Profile', auth: true, role: 'speaker' },
    // --- organizer ---------------------------------------------------------------
    { slug: 'organizer-dashboard', url: '/Organizer/Dashboard', auth: true, role: 'organizer' },
    { slug: 'organizer-command-center', url: '/Organizer/CommandCenter', auth: true, role: 'organizer' },
    { slug: 'organizer-participants', url: '/Organizer/Participants', auth: true, role: 'organizer' },
    { slug: 'organizer-attendees', url: '/Organizer/Attendees', auth: true, role: 'organizer' },
    { slug: 'organizer-sessions', url: '/Organizer/Sessions', auth: true, role: 'organizer' },
    { slug: 'organizer-allocation-queue', url: '/Organizer/ActionQueue', auth: true, role: 'organizer' },
    { slug: 'organizer-email-center', url: '/Organizer/EmailCenter', auth: true, role: 'organizer' },
    { slug: 'organizer-email-log', url: '/Organizer/EmailLog', auth: true, role: 'organizer' },
    { slug: 'organizer-jobs', url: '/Organizer/Jobs', auth: true, role: 'organizer' },
    { slug: 'organizer-graphics', url: '/Organizer/Graphics', auth: true, role: 'organizer' },
    { slug: 'organizer-exports', url: '/Organizer/Exports', auth: true, role: 'organizer' },
    { slug: 'organizer-audit-trail', url: '/Organizer/AuditTrail', auth: true, role: 'organizer' },
    { slug: 'organizer-data-freshness', url: '/Organizer/DataFreshness', auth: true, role: 'organizer' },
    { slug: 'organizer-hotels', url: '/Organizer/Hotels', auth: true, role: 'organizer' },
    { slug: 'organizer-volunteers', url: '/Organizer/Volunteers', auth: true, role: 'organizer' },
    { slug: 'organizer-evaluation-results', url: '/Organizer/EvaluationResults', auth: true, role: 'organizer' },
    { slug: 'organizer-coupon-invoicing', url: '/Organizer/CouponInvoicing', auth: true, role: 'organizer' },
    { slug: 'organizer-group-photos', url: '/Organizer/GroupPhotos', auth: true, role: 'organizer' },
    // --- the social-media campaign (§824 onward, nothing captured before) ---------
    { slug: 'some-hub', url: '/Organizer/Content', auth: true, role: 'organizer' },
    { slug: 'some-content-studio', url: '/Organizer/ContentStudio', auth: true, role: 'organizer' },
    { slug: 'some-event-posts', url: '/Organizer/EventPosts', auth: true, role: 'organizer' },
    { slug: 'some-settings', url: '/Organizer/SoMeSettings', auth: true, role: 'organizer' },
    { slug: 'some-templates', url: '/Organizer/SoMeTemplates', auth: true, role: 'organizer' },
];

const VIEWPORTS = [
    { suffix: '', width: 1440, height: 900 },
    { suffix: '-mobile', width: 390, height: 844 },
];

test.describe('documentation screenshots', () => {
    test.skip(!PINS_FILE || !fs.existsSync(PINS_FILE),
        'DOCS_PINS not set — plant one PIN per role with tools/plant-docs-pins.ps1 first');
    test.skip(!ANON_SOURCE || !fs.existsSync(ANON_SOURCE),
        'ANON_SOURCE not set — dump the real strings first so redaction can be ASSERTED, not assumed');
    test.setTimeout(45 * 60_000);

    async function login(page: Page, email: string, pin: string) {
        await page.goto(`${BASE}/Login`, { waitUntil: 'domcontentloaded' });
        await page.locator('input[name="Email"]').fill(email);
        await page.getByRole('button', { name: /send.*code|email me|request/i }).click();
        const pinInput = page.locator('input[name="Pin"]');
        await expect(pinInput).toBeVisible();
        await pinInput.fill(pin);
        await page.getByRole('button', { name: 'Sign in', exact: true }).click();
        // ⚠️ ATTACHED, not visible. The sign-out control lives inside the collapsed navigation on a
        // desktop viewport, so "visible" fails on a session that is perfectly valid — the button
        // renders at all only when signed in, which is the property being asserted.
        await expect(page.locator('button.signout')).toBeAttached({ timeout: 20_000 });
        await expect(page).not.toHaveURL(/\/Login/);
    }

    test('capture every published image, per role, refusals asserted', async ({ browser }: { browser: Browser }) => {
        const accounts = loadAccounts();
        const pins = readJson<Record<string, string>>(PINS_FILE);
        const src = readJson<AnonSource>(ANON_SOURCE);
        const map = buildMap(src);
        fs.mkdirSync(OUT_DIR, { recursive: true });

        // 🔴 A static guard, checked before a single browser opens: an authenticated target with no
        // role would silently never be captured under a per-role loop, and a missing picture reads
        // as a feature that has none.
        const orphans = TARGETS.filter(t => t.auth && (!t.role || !accounts[t.role])).map(t => t.slug);
        expect(orphans, `authenticated targets with no usable role account: ${orphans.join(', ')}`).toEqual([]);

        // 🔴 §957 — SPONSOR LEAD DATA IS COMMERCIAL DATA BELONGING TO A SPONSOR, NOT TO US.
        // The dim-the-people treatment is explicitly NOT authorised for it: a dimmed lead is still
        // a legible list of who visited someone's booth, published on a public mirror. No leads
        // page is in TARGETS today, and this guard is what stops one being added later by someone
        // who reasonably assumes the dimming covers everything. If a leads picture is ever wanted,
        // it needs its own decision and its own replacement pass — not this line deleted.
        const leadPages = TARGETS.filter(t => /lead/i.test(t.url)).map(t => t.slug);
        expect(leadPages, `sponsor lead data must not be photographed (§957): ${leadPages.join(', ')}`).toEqual([]);

        const written: string[] = [];
        const skipped: string[] = [];
        const refused: string[] = [];

        async function capture(page: Page, t: typeof TARGETS[number], suffix: string, role?: string) {
            const name = `${t.slug}${suffix}`;
            const file = path.join(OUT_DIR, `${name}.png`);
            try {
                const resp = await page.goto(`${BASE}${t.url}`, { waitUntil: 'networkidle', timeout: 45_000 });
                if (resp && resp.status() >= 400) { skipped.push(`${name} (HTTP ${resp.status()})`); return; }
                // A redirect to the login page means this account cannot see the page — skip it
                // rather than publishing a picture of a login form under a feature's name.
                if (page.url().includes('/Login') && t.url !== '/Login') { skipped.push(`${name} (no access)`); return; }

                await page.waitForTimeout(400);

                // 🔴 BEFORE the pixels are touched: is this the feature, or the gate? Checked on the
                // raw page, because dimming and redaction would only make a refusal harder to read.
                const why = await refusalReason(page, role);
                if (why) { refused.push(`${name} — ${why}`); return; }

                await redactContacts(page, map);
                await assertNoContactData(page, map, name);
                await dimPeople(page, map);

                // 🔒 VIEWPORT, NOT fullPage. A full-page capture of a long admin table produced
                // a 6.8 MB PNG and the set totalled 66 MB — which then lives in git history for
                // ever, on a repo that mirrors publicly. Above-the-fold also reads better in a
                // documentation table: it shows what the page IS rather than everything it can
                // scroll to.
                await page.screenshot({ path: file, fullPage: false });
                written.push(name);
            } catch (e) {
                skipped.push(`${name} (${(e as Error).message.split('\n')[0].slice(0, 80)})`);
            }
        }

        for (const vp of VIEWPORTS) {
            const ctxOpts = {
                viewport: { width: vp.width, height: vp.height },
                deviceScaleFactor: 2,
                isMobile: vp.suffix === '-mobile',
                hasTouch: vp.suffix === '-mobile',
            };

            // --- public pages: no session at all, so they are photographed as a visitor sees them.
            const anon = await browser.newContext(ctxOpts);
            const anonPage = await anon.newPage();
            for (const t of TARGETS.filter(x => !x.auth)) await capture(anonPage, t, vp.suffix);
            await anon.close();

            // --- 🔴 ONE BROWSER CONTEXT PER ROLE (§952). Not one session reused across roles:
            // that is the entire defect. A fresh context also guarantees no cookie survives from
            // the previous role, which a shared context with a re-login would not.
            for (const [role, acct] of Object.entries(accounts)) {
                const mine = TARGETS.filter(x => x.auth && x.role === role);
                if (!mine.length) continue;

                const pin = pins[role];
                if (!pin) {
                    for (const t of mine) skipped.push(`${t.slug}${vp.suffix} (no PIN planted for ${role})`);
                    continue;
                }

                const ctx = await browser.newContext(ctxOpts);
                const page = await ctx.newPage();
                try {
                    await login(page, acct.email, pin);
                } catch (e) {
                    // ⚠️ Loud, not silent. A failed login means a whole role's chapter has no
                    // pictures, and the most common cause is a PIN that expired mid-run — plant
                    // with a generous -RankMinutes (see tools/plant-docs-pins.ps1).
                    for (const t of mine) {
                        skipped.push(`${t.slug}${vp.suffix} (${role} login failed: ${(e as Error).message.split('\n')[0].slice(0, 60)})`);
                    }
                    await ctx.close();
                    continue;
                }
                for (const t of mine) await capture(page, t, vp.suffix, role);
                await ctx.close();
            }
        }

        // 🔒 Reported, never silent: a missing picture must not read as a feature that has none.
        console.log(`\n=== captured ${written.length} image(s) ===`);
        console.log(written.join('\n'));
        if (skipped.length) {
            console.log(`\n=== SKIPPED ${skipped.length} ===`);
            console.log(skipped.join('\n'));
        }
        if (refused.length) {
            console.log(`\n=== REFUSED ${refused.length} (NOT written) ===`);
            console.log(refused.join('\n'));
        }
        // 🔒 Written beside the test results, NOT into docs/img — that folder publishes to the
        // public mirror and a build artefact does not belong in it.
        const reportDir = path.resolve(__dirname, 'test-results');
        fs.mkdirSync(reportDir, { recursive: true });
        fs.writeFileSync(path.join(reportDir, 'docs-capture-report.txt'),
            `captured:\n${written.join('\n')}\n\nskipped:\n${skipped.join('\n')}\n\nrefused:\n${refused.join('\n')}\n`);

        // 🔴 A REFUSAL FAILS THE RUN. Not writing the file is necessary but not sufficient: a
        // refusal means a page the operator expects to see documented has no picture AND the role
        // mapping is wrong. Publishing a set with a silent hole is how this defect survived once.
        expect(refused, `pages answered with a refusal instead of the feature:\n${refused.join('\n')}`).toEqual([]);
        expect(written.length, 'no screenshots were captured at all').toBeGreaterThan(10);
    });
});
