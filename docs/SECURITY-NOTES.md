# Community Event Hub (CEH / ELDK27) — Security notes (deferred audit items)

Deferred findings from a read-only security audit. These are **not fixed in code** in this
release — most are **operator / ops actions** (token rotation, KV wiring, secret rotation) or
lower-risk hardening to schedule. No CRITICAL findings; no committed secrets were found.

For *what the system does* see [`DESIGN.md`](DESIGN.md); for backlog see
[`REQUIREMENTS.md`](REQUIREMENTS.md). This file = audit items the operator must action.

Severity legend: **HIGH · MEDIUM · LOW · INFO**.

---

## HIGH — Re-issue the Zoho refresh token with least privilege (ops)

The current Zoho refresh token carries **unused write/delete scopes** the mirror never uses:
`order.CREATE/UPDATE/DELETE`, `attendee.UPDATE/DELETE`, `webhook.*`, and bookings write. The
mirror is **read-only for orders and attendees**, so these grants are pure blast-radius if the
token leaks.

**Action — rotate now.** Re-issue the refresh token scoped to:
- `*.READ` for **orders**, **attendees**, **bookings** (read-only mirror).
- Only the writes actually used: **sponsor / exhibitor / speaker / agenda / booth-member**
  create + update.
- Drop entirely: `order.CREATE/UPDATE/DELETE`, `attendee.UPDATE/DELETE`, `webhook.*`,
  bookings write.

Update the stored token in Key Vault and verify the sync jobs (sponsor/exhibitor/speaker/agenda)
still succeed with the reduced scope.

---

## MEDIUM — Encrypt the DataProtection key ring at rest

`Program.cs` `AddDataProtection()` uses `PersistKeysToDbContext(...)` with **no
`ProtectKeysWith…`**. The key ring is stored in the database in plaintext, so a DB read alone
could be used to **forge auth cookies / magic-link tokens**.

**Mitigated today** by AAD-only SQL (no SQL logins; DB access requires a directory identity), but
this is defence-in-depth, not encryption-at-rest.

**Action.** Wrap the key ring with `ProtectKeysWithAzureKeyVault(keyIdentifier, credential)`
(reuse the existing managed identity / KV the app already uses). After wiring, confirm existing
sessions/magic-links still validate or plan a rotation window (changing protection can invalidate
previously issued tokens).

---

## MEDIUM — Webhook secret appears in URL (captured by App Insights)

The Zoho webhook secret is delivered in the **query string**, so it lands in **Application
Insights** request telemetry.

**Partially mitigated this release** by the `WebhookSsrf` redaction (the secret is scrubbed from
logged/telemetry URLs). Residual risk remains for any path not covered by redaction.

**Action.**
- **Rotate the webhook secret** (it was previously loggable).
- Prefer **header-based delivery** if/when Zoho supports it, instead of `?secret=` in the URL.
- Confirm the redaction covers all telemetry sinks (request, dependency, exception).

---

## LOW — Markdown content-hub renderer has no HTML sanitizer

`ContentMarkdownRenderer` passes **raw HTML through Markdig** without `DisableHtml()` or an
HTML sanitizer, so embedded `<script>`/HTML in content would render.

**Why low.** Content is **operator-authored**, **commit-gated**, and restricted to an
**allowlist of slugs** — it is not reachable by end users today.

**Action.** If untrusted content could *ever* reach config/content (e.g. a future
contributor/editor flow), add a sanitizer (e.g. HtmlSanitizer / Ganss.Xss) or call
`DisableHtml()` on the pipeline. Revisit before opening content authoring beyond operators.

---

## LOW — Magic-link tokens: long TTL, multi-use, long session

Magic-link tokens are **14-day, multi-use** and mint a **365-day session**.

**Action (hardening).** Consider:
- Shorter TTL and/or **single-use** tokens for **Organizer + Sponsor** roles (higher privilege).
- A tighter **session lifetime** for those roles.

Balance against the attendee UX (long-lived convenience links are intentional for low-privilege
attendees).

---

## LOW — Sponsor-leads CSV/JSON API accepts the key in `?key=` query string

The sponsor-leads export endpoint accepts the API key via **`?key=` query string**, which is
**loggable** (server logs, App Insights, proxies, browser history).

**Action.**
- Prefer an **HTTP header** (e.g. `X-Api-Key` / `Authorization`) over the query string.
- Verify the key comparison is **constant-time** (no early-exit string compare).

---

## INFO — No FallbackPolicy (fixed this release)

The app previously had **no authorization `FallbackPolicy`**, meaning endpoints without an
explicit `[Authorize]`/policy defaulted to anonymous. **Fixed this release** via the
**AuthzBackstop** (a fallback policy requiring authentication unless explicitly allowed). Recorded
here for completeness; no further action required beyond keeping the backstop in place and
auditing any new `[AllowAnonymous]` usage.
