# Email templates

Beautiful, manageable email - without fighting email clients.

## The layout system

- **`_layout.html`** - the branded shell every email is rendered into: header
  with the community logo and brand color, a clean white body card, a footer.
  Built with email-safe **table layout + inline styles** (Outlook does not
  support modern CSS / flexbox / grid / `<style>` blocks - this is why email
  HTML is not HTML5; table-based inline-styled HTML is how every polished
  email is built). Edit this ONE file to restyle every email.
- **Content templates** (`speaker-pending-tasks.html`, etc.) - each holds only
  the *content* of one email. The app renders the content, then drops it into
  `_layout.html` at `{{bodyContent}}`. Restyling the chrome is one file;
  editing wording is a small content file.
- The layout is themed from config: `{{brandColor}}` and `{{logoUrl}}` come
  from `event.<edition>.json`. Change the brand once - every email updates.

## File format

- First line is `Subject: ...` - everything after is the content HTML.
- Placeholder tokens are `{{token}}`, filled by the app at send time.
- Content templates contain NO `<html>`/`<body>` - only the inner content.

## Common tokens (every template)

Every branded email auto-receives the four branding tokens plus the per-recipient
basics. (There is no `{{fullName}}` and no `{{senderName}}` — no code fills them.)

| Token | Filled with |
|-------|-------------|
| `{{firstName}}` | Recipient first name (falls back to "there") |
| `{{communityName}}` | From `event.<edition>.json` |
| `{{eventDisplayName}}` | e.g. ELDK27 Crew Hub |
| `{{roleName}}` | Friendly role noun (e.g. "speaker") |
| `{{hubUrl}}` | Link to the hub |
| `{{brandColor}}` / `{{logoUrl}}` | Brand theming (used by `_layout.html`) |
| `{{supportEmail}}` | Configured support address |

## Template-specific tokens

- **Deadline reminders** (`speaker-deadline-reminder`, `task-deadline-reminder`):
  `{{taskTitle}}`, `{{dueDate}}`, `{{state}}`, `{{taskLink}}`.
- **List digests** (`speaker-pending-tasks`, `sponsor-overdue`):
  `{{taskListHtml}}` - a pre-rendered list of the person's open/overdue tasks.
- **Sponsor list digest** also has `{{sponsorCompany}}`.
- **Incomplete-form chaser**: `{{formName}}`, `{{formDeadline}}`.

> The full, code-verified token set per template (and which sender fills each)
> lives in the internal `templates/emails/README.md`. Booth tokens
> (`{{boothTier}}` etc.) are a future idea — not wired today, so they resolve
> blank.

## Rules

- A template uses only its listed tokens; an unknown token is left blank and
  logged - never a crash.
- Keep to table layout + inline styles. No `<style>` blocks, no flexbox/grid.
- Images must be hosted at a public URL (email cannot embed local files) -
  see `templates/assets/` for the logo source; upload it somewhere public and
  set `logoUrl` to that URL.
- Per-edition overrides may live in `templates/emails/<edition>/`.
