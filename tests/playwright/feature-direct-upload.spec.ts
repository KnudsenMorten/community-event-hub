import { test, expect, BASE, USERS, PINS, login, narrowOnly, Page } from './support/hub';

/**
 * ⚠️ NOT CURRENTLY RUNNABLE — the shared PIN `login()` helper fails against prod (2026-07-28).
 * These specs have NEVER passed. Do not read their presence as coverage.
 *
 * What is known, so nobody re-derives it:
 *   • The server flow is FINE — a plain HTTP drive of /Login with a planted PIN creates a
 *     real-time LoginPin row and proceeds. The PIN, the planter and the hash all verify.
 *   • In the BROWSER, step 1 never reaches the server: no real-time LoginPin row is ever created
 *     by a Playwright run. `Model.Email` therefore stays empty, step 2 posts an empty hidden
 *     Email, the participant lookup misses, and the page reports "Invalid email or code" with
 *     FailedAttempts untouched on every row. That last detail is the tell — the app never got as
 *     far as comparing a PIN.
 *   • Ruled out: PIN validity, the hash, the 1000/hour rate limit, duplicate participant rows,
 *     the one-day-ticket gate (§361), event-id mismatch, and stale/expired plants.
 *   • Next step if resumed: log the actual POST body of step 1 from the browser and compare it
 *     with the working HTTP request. The difference is in what is sent, not in what is stored.
 *
 * Until that is fixed, the manual test plan (docs/TEST-PLAN-2026-07-28.md) is the real coverage.
 */

/**
 * §494 — DIRECT-TO-STORAGE uploads, through a real PIN login and the real form.
 *
 * The file goes browser → SharePoint and never touches the hub, so this is the one
 * behaviour NO server-side test can cover: it lives in the browser, in a chunked PUT to a
 * pre-authenticated Graph URL. It is also the behaviour that would fail silently — the form
 * falls back to the old server-proxied post, so a broken direct path looks like a working
 * upload unless something checks WHICH road was taken.
 *
 * These tests therefore assert the ROUTE, not just the outcome:
 *   • the begin/complete calls actually happen (network interception), and
 *   • the bytes go to a NON-hub origin (proving the app was bypassed).
 *
 * ⚠️ SIDE EFFECTS: this uploads a real file to the sponsor's real collateral folder on the
 * target environment, and completion sends the sponsor-upload notification. Files are named
 * so they are obvious to delete. Kept to ONE small file per run for that reason.
 */

/** A tiny but genuinely valid PDF — a real file, so Graph/SharePoint treat it normally. */
const TINY_PDF = Buffer.from(
    '%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n' +
    '2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n' +
    '3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 200 200]>>endobj\n' +
    'trailer<</Root 1 0 R>>\n%%EOF\n', 'utf8');

function testFileName() {
    // Stamped so a failed run leaves something identifiable rather than a mystery file.
    return `CEH-GUI-TEST-${Date.now()}.pdf`;
}

test.describe('@gui §494 Direct-to-storage uploads', () => {
    test.skip(!PINS.sponsor || !USERS.sponsor, 'SPONSOR_PIN/SPONSOR_EMAIL not set');
    narrowOnly();
    test.beforeEach(async ({ page }) => login(page, USERS.sponsor, PINS.sponsor));

    test('collateral upload goes DIRECT to storage, bypassing the hub', async ({ page }) => {
        const calls: string[] = [];
        const putOrigins: string[] = [];

        page.on('request', req => {
            const u = req.url();
            if (u.includes('/sponsor/uploads/')) calls.push(`${req.method()} ${new URL(u).pathname}`);
            // The actual bytes: a PUT to somewhere that is NOT us.
            if (req.method() === 'PUT') putOrigins.push(new URL(u).origin);
        });

        await page.goto(`${BASE}/Sponsor/CompanyDetails`, { waitUntil: 'domcontentloaded' });

        const form = page.locator('form[data-direct-upload="collateral"]');
        if (await form.count() === 0) {
            test.skip(true, 'Collateral upload not offered (folder not configured, or cap reached)');
        }

        const name = testFileName();
        await form.locator('input[type=file]').setInputFiles({
            name, mimeType: 'application/pdf', buffer: TINY_PDF,
        });
        await form.locator('button[type=submit]').click();

        // The page reloads on success; the uploaded name appears in the materials list.
        await page.waitForLoadState('load', { timeout: 120_000 });

        // 1. Both halves of the handshake ran.
        expect(calls.join(' | '), 'begin + complete should both be called')
            .toContain('/sponsor/uploads/collateral/begin');
        expect(calls.join(' | ')).toContain('/sponsor/uploads/collateral/complete');

        // 2. THE POINT: the bytes went somewhere that is not the hub. If this fails while the
        //    upload still "works", the JS fell back to the server-proxied post — which is the
        //    silent failure this test exists to catch.
        const hubOrigin = new URL(BASE).origin;
        expect(putOrigins.length, 'the browser should PUT the file itself').toBeGreaterThan(0);
        expect(putOrigins.every(o => o !== hubOrigin),
            `file bytes must NOT be PUT to the hub (${hubOrigin}); saw ${putOrigins.join(', ')}`).toBe(true);

        // 3. And it is actually recorded.
        await expect(page.getByText(name, { exact: false }).first()).toBeVisible({ timeout: 30_000 });
    });

    test('a LOGO upload goes direct too, and is versioned server-side', async ({ page }) => {
        // The logo kinds share the generic script and the {kind} route, so this proves the
        // generalisation actually generalised — not just that collateral still works.
        const putOrigins: string[] = [];
        page.on('request', req => {
            if (req.method() === 'PUT') putOrigins.push(new URL(req.url()).origin);
        });

        await page.goto(`${BASE}/Sponsor/CompanyDetails`, { waitUntil: 'domcontentloaded' });

        // SoMe logo takes a PNG. A 1×1 PNG is a genuinely valid image.
        const PNG = Buffer.from(
            'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==',
            'base64');

        const form = page.locator('form[data-direct-upload="some"]');
        if (await form.count() === 0) test.skip(true, 'SoMe logo upload not offered');

        await form.locator('input[type=file]').setInputFiles({
            name: `CEH-GUI-TEST-${Date.now()}.png`, mimeType: 'image/png', buffer: PNG,
        });
        await form.locator('button[type=submit]').click();
        await page.waitForLoadState('load', { timeout: 120_000 });

        const hubOrigin = new URL(BASE).origin;
        expect(putOrigins.length, 'the browser should PUT the logo itself').toBeGreaterThan(0);
        expect(putOrigins.every(o => o !== hubOrigin), 'logo bytes must bypass the hub').toBe(true);

        // §494b: the SERVER names it — the client's name is not what lands.
        await expect(page.getByText(/SoMeBrandingLogo_.*_v\d+\.png/i).first(),
            'the stored name should be the server-side versioned one').toBeVisible({ timeout: 30_000 });
    });

    test('an oversized file is refused BEFORE any upload URL is issued', async ({ page }) => {
        // The size gate must bite at `begin`. If it only bit later, a client could obtain a
        // write URL and then push whatever it liked through it.
        await page.goto(`${BASE}/Sponsor/CompanyDetails`, { waitUntil: 'domcontentloaded' });

        const res = await page.evaluate(async () => {
            const r = await fetch('/sponsor/uploads/collateral/begin', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                // 2 GB — far over the 25 MB collateral cap.
                body: JSON.stringify({ fileName: 'huge.pdf', sizeBytes: 2 * 1024 * 1024 * 1024 }),
            });
            return { status: r.status, body: await r.text() };
        });

        expect(res.status, 'oversized begin must be refused').toBe(400);
        expect(res.body.toLowerCase()).toContain('too large');
        expect(res.body, 'a refusal must never leak an upload URL').not.toContain('uploadUrl');
    });

    test('an unsupported file type is refused BEFORE any upload URL is issued', async ({ page }) => {
        await page.goto(`${BASE}/Sponsor/CompanyDetails`, { waitUntil: 'domcontentloaded' });

        const res = await page.evaluate(async () => {
            const r = await fetch('/sponsor/uploads/collateral/begin', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ fileName: 'payload.exe', sizeBytes: 1024 }),
            });
            return { status: r.status, body: await r.text() };
        });

        expect(res.status).toBe(400);
        expect(res.body).not.toContain('uploadUrl');
    });

    test('completing a path OUTSIDE the kind folder is refused', async ({ page }) => {
        // The check that stops a caller recording a file they do not own, or attaching one
        // kind's file to another kind.
        await page.goto(`${BASE}/Sponsor/CompanyDetails`, { waitUntil: 'domcontentloaded' });

        const res = await page.evaluate(async () => {
            const r = await fetch('/sponsor/uploads/collateral/complete', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ path: 'Some/Other/Folder/notmine.pdf', fileName: 'notmine.pdf' }),
            });
            return r.status;
        });

        // 403 (outside the folder) — never 200.
        expect(res, 'a foreign path must not be recorded').not.toBe(200);
    });

    test('an unknown sponsor upload kind is not routable', async ({ page }) => {
        // The route constraint (some|print|zoho|wall) — a stray kind must 404 rather than
        // reach the handler and be interpreted.
        await page.goto(`${BASE}/Sponsor/CompanyDetails`, { waitUntil: 'domcontentloaded' });

        const status = await page.evaluate(async () => {
            const r = await fetch('/sponsor/uploads/bogus/begin', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ fileName: 'x.png', sizeBytes: 10 }),
            });
            return r.status;
        });

        expect(status).toBe(404);
    });
});

test.describe('@gui §494c Speaker deck direct upload', () => {
    test.skip(!PINS.speaker || !USERS.speaker, 'SPEAKER_PIN/SPEAKER_EMAIL not set');
    narrowOnly();
    test.beforeEach(async ({ page }) => login(page, USERS.speaker, PINS.speaker));

    test('a deck cannot be started for a session the speaker does not own', async ({ page }) => {
        // The ownership gate lives at BEGIN, before any write URL exists. Session id 0 can
        // never belong to anyone, so this must be refused rather than 500.
        await page.goto(`${BASE}/Speaker`, { waitUntil: 'domcontentloaded' });

        const res = await page.evaluate(async () => {
            const r = await fetch('/speaker/decks/begin', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ sessionId: 0, kind: 'final', fileName: 'deck.pptx', sizeBytes: 1024 }),
            });
            return { status: r.status, body: await r.text() };
        });

        expect(res.status).toBe(400);
        expect(res.body).not.toContain('uploadUrl');
    });

    test('FULL SIMULATION: a real deck is uploaded direct to storage and recorded', async ({ page }) => {
        // Decks are the biggest files in the product and the §455 incident that started all
        // of this. This walks the exact three phases the browser performs — begin, chunked
        // PUT, complete — against a session the speaker genuinely owns.
        await page.goto(`${BASE}/Speaker`, { waitUntil: 'domcontentloaded' });

        // Discover a session this speaker actually owns, from the page itself rather than
        // hard-coding an id that would rot.
        const sessionId = await page.evaluate(() => {
            const el = document.querySelector('select[name=sessionId] option[value]:not([value=""])')
                ?? document.querySelector('input[name=sessionId][value]');
            const v = el?.getAttribute('value');
            return v ? parseInt(v, 10) : 0;
        });
        if (!sessionId) test.skip(true, 'No session found for this speaker on the upload page');

        const result = await page.evaluate(async (sid) => {
            // A small but real PDF (the deck allow-list accepts pdf/pptx/zip).
            const bytes = new TextEncoder().encode(
                '%PDF-1.4\n1 0 obj<</Type/Catalog>>endobj\ntrailer<</Root 1 0 R>>\n%%EOF\n');

            const begin = await fetch('/speaker/decks/begin', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    sessionId: sid, kind: 'preview',
                    fileName: 'CEH-GUI-TEST-deck.pdf', sizeBytes: bytes.length,
                }),
            });
            if (!begin.ok) return { phase: 'begin', status: begin.status, body: await begin.text() };
            const { uploadUrl, path } = await begin.json();

            // The bytes go straight to storage — exactly one chunk at this size.
            const put = await fetch(uploadUrl, {
                method: 'PUT',
                headers: { 'Content-Range': `bytes 0-${bytes.length - 1}/${bytes.length}` },
                body: bytes,
            });
            if (!put.ok && put.status !== 202) {
                return { phase: 'put', status: put.status, uploadOrigin: new URL(uploadUrl).origin };
            }

            const done = await fetch('/speaker/decks/complete', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ sessionId: sid, kind: 'preview', path }),
            });
            return {
                phase: 'complete', status: done.status, body: await done.text(),
                uploadOrigin: new URL(uploadUrl).origin, path,
            };
        }, sessionId);

        expect(result.phase, `failed at ${result.phase}: ${JSON.stringify(result)}`).toBe('complete');
        expect(result.status, 'complete should succeed').toBe(200);

        // The whole point: the write URL was NOT us.
        expect(result.uploadOrigin, 'deck bytes must bypass the hub')
            .not.toBe(new URL(BASE).origin);

        // And the server chose a versioned name under its own folder.
        expect(result.path).toMatch(new RegExp(`${sessionId} - .*_v\\d+\\.pdf$`));
    });

    test('an unsupported deck type is refused', async ({ page }) => {
        await page.goto(`${BASE}/Speaker`, { waitUntil: 'domcontentloaded' });

        const res = await page.evaluate(async () => {
            const r = await fetch('/speaker/decks/begin', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ sessionId: 1, kind: 'final', fileName: 'deck.exe', sizeBytes: 1024 }),
            });
            return { status: r.status, body: await r.text() };
        });

        expect(res.status).toBe(400);
        expect(res.body).not.toContain('uploadUrl');
    });
});
