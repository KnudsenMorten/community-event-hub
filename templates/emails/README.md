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

## Supported variables — the source of truth

> Every token below is **resolved by code** (file:line cited). A token a template
> uses but no code fills resolves to an **empty string** (the renderer never
> crashes — `EmailTemplateRenderer.cs:67-73`). Do not add a `{{token}}` to a
> template unless a sender actually fills it.

### Branding tokens (auto-filled on EVERY branded email)

`EmailTemplateProvider.NewTokenSet()` (`EmailTemplateProvider.cs:63-69`) seeds
these from `event.<edition>.json`; the renderer also injects `bodyContent` +
`subject` into `_layout.html` (`EmailTemplateRenderer.cs:57-61`).

| Token | Filled with |
|-------|-------------|
| `{{brandColor}}` | Edition brand colour (theming `_layout.html`) |
| `{{logoUrl}}` | Public logo URL (theming `_layout.html`) |
| `{{supportEmail}}` | Configured support address |
| `{{hubUrl}}` | Link to the hub |
| `{{subject}}` / `{{bodyContent}}` | Filled by the renderer into the layout |

### Common per-recipient tokens

Most per-person templates go through `ParticipantEmailService` base tokens
(`ParticipantEmailService.cs:83-91`): `{{firstName}}`, `{{communityName}}`,
`{{eventDisplayName}}`, `{{roleName}}`. Other senders set the same names
themselves.

| Token | Filled with | Resolved at |
|-------|-------------|-------------|
| `{{firstName}}` | Recipient first name (falls back to "there") | `ParticipantEmailService.cs:84` and per-sender |
| `{{communityName}}` | Community name from config | `ParticipantEmailService.cs:85` |
| `{{eventDisplayName}}` | Edition display name, e.g. *ELDK27 Crew Hub* | `ParticipantEmailService.cs:86` |
| `{{roleName}}` | Friendly role noun (e.g. "speaker") | `ParticipantEmailService.cs:87` |

> There is **no `{{fullName}}` and no `{{senderName}}`** — no code fills them.
> They were documented here historically but resolve to blank; do not use them.

### Per-template tokens (the exact set each sender fills)

| Template | Sender (file:line) | Template-specific tokens |
|----------|--------------------|--------------------------|
| `welcome` | `WelcomeEmailService.cs:82-87` | `{{roleGuidance}}` |
| `welcome-login` *(DEV-only)* | `WelcomeWithLoginEmailService.cs:128-135` | `{{eventCode}}`, `{{roleLine}}`, `{{loginUrl}}` (magic-link `/Login/Magic?token=…`) |
| `invitation` | `SendInvitations.cshtml.cs:101-106` | `{{eventCode}}`, `{{magicLink}}` (note: `{{roleName}}` here is the raw enum) |
| `task-deadline-reminder` | `TaskReminderBuilder.cs:101-108` | `{{taskTitle}}`, `{{dueDate}}`, `{{state}}`, `{{taskLink}}` |
| `task-manual-reminder` | `SpeakerReminders.cshtml.cs:217-236` | `{{eventCode}}`, `{{taskTitle}}`, `{{dueText}}` *(raw HTML)*, `{{descriptionBlock}}` *(raw HTML)* |
| `travel-reimbursement-paid` | `TravelReimbursements.cshtml.cs:102-107` | `{{eventCode}}`, `{{amount}}`, `{{notesBlock}}` |
| `group-photo-invite` | `GroupPhotos.cshtml.cs:154-159` | `{{contactName}}`, `{{companyName}}`, `{{slotTime}}`, `{{location}}` |
| `app-game-gift-reminder` | `AppGame.cshtml.cs:171-176` | `{{companyName}}`, `{{giftDescription}}` |
| `volunteer-help-raised` | `VolunteerHelpNotificationService.cs:105-111` | `{{volunteerName}}`, `{{categoryName}}`, `{{taskTitle}}`, `{{helpMessage}}` |
| `speaker-question-digest` | `SpeakerQuestionDigestService.cs:153-161` | `{{openCount}}`, `{{sessionCount}}`, `{{openCountNoun}}`, `{{sessionCountNoun}}` |
| `onboarding-getting-started`, `onboarding-your-tasks` | `OnboardingEmailService.cs:68-70` | *(base tokens only)* |
| `onboarding-step-reset` | `OnboardingStepResetEmailService.cs:60` | `{{stepLabel}}` |
| `sponsor-leads-digest` | `SponsorLeadsJob.cs:135-139` | `{{sponsorCompany}}`, `{{leadCount}}`, `{{leadListHtml}}` |
| `attendee-missing-booking`, `attendee-missing-ticket` | `AttendeeReconcileJob.cs:202-211` | *(base tokens only)* |
| `attendee-duplicate-booking` | `AttendeeReconcileJob.cs:202-211` | `{{masterClassList}}` |
| `masterclass-selection-invite` | `MasterClassEmailService.cs` (`SendSelectionInviteAsync`) | `{{selectionUrl}}` |
| `masterclass-confirmed` | `MasterClassEmailService.cs` (`SendConfirmedAsync`) | `{{masterClassTitle}}`, `{{landingPageUrl}}`, `{{icsUrl}}`, `{{selfServiceUrl}}` |
| `masterclass-waitlisted` | `MasterClassEmailService.cs` (`SendWaitlistedAsync`) | `{{masterClassTitle}}`, `{{selfServiceUrl}}`, `{{waitlistTerms}}` *(raw HTML)* |
| `masterclass-cancelled` | `MasterClassEmailService.cs` (`SendCancelledAsync`) | `{{masterClassTitle}}`, `{{signupUrl}}` |
| `masterclass-reassignment` | `MasterClassEmailService.cs` (`SendReassignmentValidationAsync`) | `{{heldMasterClass}}` *(raw HTML)*, `{{selfServiceUrl}}` |
| `masterclass-offer` | `MasterClassPromotionEmailService.cs` (`SendPromotionAsync`, Offered) | `{{masterClassTitle}}`, `{{selfServiceUrl}}`, `{{offerDeadline}}` |
| `masterclass-promoted` | `MasterClassPromotionEmailService.cs` (`SendPromotionAsync`, Confirmed) | `{{masterClassTitle}}`, `{{selfServiceUrl}}` |
| `masterclass-month-reminder` | `MasterClassEmailService.cs` (`SendMonthReminderAsync`) | `{{masterClassTitle}}` *(+ .ics attachment)* |
| `pin-signin` | `PinLoginService.cs` (`RequestPinAsync`) | `{{subjectPrefix}}`, `{{pin}}`, `{{expiryMinutes}}` |
| `calendar-invite` | `CalendarInviteEmailService.cs` (`SendActivationInviteAsync`) | *(base tokens only; + .ics attachment)* |
| `session-evaluation-results` | `SessionEvaluationMailService.cs` (`EmailResultsToSpeakersAsync`) | `{{sessionTitle}}`, `{{resultsHtml}}` *(raw HTML)* |
| `broadcast` | `Broadcast.cshtml.cs:308-325` | `{{messageHtml}}` (see broadcast note below) |

### HTML-encoding contract — "encode at the seam"

`EmailTemplateRenderer` **HTML-encodes every token value** as it substitutes it
into the **HTML body**, so a name/title containing `<`, `&` or `"` renders as
readable text and can never break the markup. **Senders pass raw values — do not
pre-encode** (that would double-encode). The exception is tokens whose value is a
deliberately sender-built **HTML fragment**: these are identified by the
**`Html` / `Block` naming suffix** (e.g. `{{leadListHtml}}`, `{{taskListHtml}}`,
`{{messageHtml}}`, `{{descriptionBlock}}`, `{{notesBlock}}`, `{{resultsHtml}}`)
plus the explicit set `{{bodyContent}}` / `{{dueText}}` / `{{waitlistTerms}}` /
`{{heldMasterClass}}`, and pass through **verbatim** (a sender
building such a fragment still encodes its own untrusted leaf values). The
**`Subject:` header** is plain text and is **not** encoded. When adding a token:
name a free-text value plainly (it gets encoded for free) and a markup fragment
with the `…Html` / `…Block` suffix (so it is treated as raw).

### Organizer-authored broadcast — a SEPARATE `{Token}` syntax

The Organizer **Broadcast** page (`/Organizer/Broadcast`) lets an organizer type
a free subject + body. There, personalization uses **single-brace `{Token}`**
(not `{{token}}`), resolved by `BroadcastTemplates.Substitute`
(`BroadcastTemplate.cs:99-110`) before the text is poured into the branded
`broadcast.html` shell. The supported set is intentionally tiny
(`BroadcastTemplate.cs:34-39`):

| Token | Filled with |
|-------|-------------|
| `{FirstName}` | Recipient first name (falls back to "there") |
| `{EventName}` | Edition display name |

An unknown `{Token}` is left **verbatim** (a mistyped `{Foo}` survives). The page
shows an inline legend (meaning + example + click-to-insert) driven by
`BroadcastTemplates.TokenHelp`, so the GUI can never advertise a token the engine
does not substitute (guarded by `BroadcastTokenLegendTests`).

### Templates present but not wired to a live sender

`incomplete-form-chaser.html` (`{{formName}}`, `{{formDeadline}}`),
`sponsor-overdue.html`, `speaker-deadline-reminder.html` and
`speaker-pending-tasks.html` exist but have no live send path yet; they render
only via the Email Center **preview / test-send** with sample values
(`EmailCenter.cshtml.cs` `BuildSampleTokensAsync`). No booth tokens
(`boothTier`/`boothWallSize`/`boothWallSpecUrl`/`boothCoupon`) are wired — they
are a future idea, not a supported variable today.

## Rules

- A template uses only its listed tokens; an unknown token is left blank and
  logged - never a crash.
- Keep to table layout + inline styles. No `<style>` blocks, no flexbox/grid.
- Images must be hosted at a public URL (email cannot embed local files) -
  see `templates/assets/` for the logo source; upload it somewhere public and
  set `logoUrl` to that URL.
- Per-edition overrides may live in `templates/emails/<edition>/`.
