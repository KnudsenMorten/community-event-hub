/*
 * §1259c — the kiosk's offline shell.
 *
 * 🔴 THE FAILURE THIS EXISTS FOR: votes already survived a Wi-Fi outage (they are queued in
 * localStorage before the network is touched), but the PAGE did not. An iPad that reloaded — a
 * crash, a battery swap, someone closing the tab — while the venue Wi-Fi was down would land on
 * Safari's "cannot open page", and that stand collects nothing until a human notices. At a venue,
 * nobody notices quickly.
 *
 * ⇒ The shell is cached on first load and served from cache FIRST, every time. The kiosk therefore
 * starts, shows its four faces and keeps queueing votes with no network at all.
 *
 * 🔒 Cache-first is the right way round HERE and would be wrong almost anywhere else: a stand must
 * boot in a dead network more urgently than it must have today's markup. Freshness is handled
 * separately (§1265): the page compares the build it was rendered with against
 * /evaluation/v1/kiosk/build, and on a new build fetches `/eval?refresh=…` — which this worker
 * lets through to the network — stores it as the shell, and reloads while nobody is using it.
 */
const SHELL = 'ceh-eval-shell-v1';
const PAGE = '/eval';

self.addEventListener('install', (event) => {
    event.waitUntil(caches.open(SHELL).then((c) => c.add(PAGE)).then(() => self.skipWaiting()));
});

self.addEventListener('activate', (event) => {
    event.waitUntil(
        caches.keys()
            .then((keys) => Promise.all(keys.filter((k) => k !== SHELL).map((k) => caches.delete(k))))
            .then(() => self.clients.claim()));
});

self.addEventListener('fetch', (event) => {
    const req = event.request;
    if (req.method !== 'GET') { return; }                       // votes are POSTs — never intercepted
    const url = new URL(req.url);
    if (url.origin !== self.location.origin) { return; }
    if (url.pathname.startsWith('/evaluation/v1/')) { return; } // the API is always live-or-fail
    if (url.searchParams.has('refresh')) { return; }            // §1265 — the page asking for a NEW copy

    if (req.mode === 'navigate' || url.pathname === PAGE) {
        event.respondWith(
            caches.match(PAGE).then((hit) => {
                // Refresh the copy in the background so the next boot is current; if the network is
                // down this simply does nothing, which is the whole point.
                const fresh = fetch(PAGE).then((res) => {
                    if (res && res.ok) { caches.open(SHELL).then((c) => c.put(PAGE, res.clone())); }
                    return res;
                }).catch(() => null);
                return hit || fresh.then((r) => r || new Response(
                    '<h1>Offline</h1><p>This kiosk has not been online yet.</p>',
                    { headers: { 'Content-Type': 'text/html' } }));
            }));
    }
});
