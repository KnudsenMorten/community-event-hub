# CommunityHub — Design Notes: Emails, Sponsor Tasks, Sessionize

These three areas were asked about but are **not built**. This documents the
intended design so they can be built deliberately — they each need input that
should not be guessed.

---

## 1. Email matrix — what exists and what is proposed

### Built today
| Email | Trigger | Recipients | Rendering |
|---|---|---|---|
| PIN sign-in code | Login request | The person signing in (any role) | Inline HTML |
| Task-deadline reminder | `ReminderJob`, 14/7/3/1 days before due | Whoever a dated task is assigned to | `task-deadline-reminder.html` |
| Attendee: missing booking | `AttendeeReconcileJob` | 2-day-ticket holders with no Master Class | Inline HTML |
| Attendee: missing ticket | `AttendeeReconcileJob` | Master Class booked, no 2-day ticket | Inline HTML |
| Attendee: duplicate booking | `AttendeeReconcileJob` | >1 Master Class booking | Inline HTML |

### Template files that exist but are NOT wired to any sender
`incomplete-form-chaser.html`, `speaker-deadline-reminder.html`,
`speaker-pending-tasks.html`, `sponsor-overdue.html`.

### Proposed full matrix (NOT built — needs sign-off on content)
| Email | Trigger | Roles | Notes |
|---|---|---|---|
| **Welcome** | On participant creation / import | All roles | Role-aware body: a speaker sees deadlines, a volunteer sees the sign-up wizard link, a sponsor sees the sponsor area. One template, role token. |
| Incomplete-form chaser | `ReminderJob`, weekly until lock date | Speaker, Volunteer | Uses `incomplete-form-chaser.html`. Fires if hotel/dinner/volunteer form is unsubmitted. |
| Speaker deadline reminder | `ReminderJob` milestones | Speaker, MasterclassSpeaker | Uses `speaker-deadline-reminder.html`. |
| Sponsor overdue | `ReminderJob` | Sponsor | Uses `sponsor-overdue.html`. Fires on overdue sponsor tasks. |
| Event-week info | Manual / scheduled, once | All active | Practical info before the event. |

**Open decisions before building:**
- Exact wording of the welcome email per role.
- Whether the welcome email is automatic on import or organizer-triggered.
- Cadence for the incomplete-form chaser (weekly is the assumed default).

All proposed emails route through `EmailTemplateProvider` + `ReminderEngine`,
so they dedup via the `SentReminder` ledger like the task reminder already does.

---

## 2. Sponsor tasks — generic JSON control

### Goal
An organizer should be able to change *what tasks a sponsor gets* by editing
JSON, with no code change — the same philosophy as the rest of the config.

### Where it lives
`sponsor.<edition>.json` (already exists, with the category-classification
rules). The **task-set definitions** are added to it. Proposed shape:

```json
{
  "taskSets": {
    "allSponsors": [
      { "title": "Upload vector logo", "offsetFromOrderDays": 7 },
      { "title": "Provide company description", "offsetFromOrderDays": 7 },
      { "title": "Backstage onboarding", "offsetFromEventDays": -30 },
      { "title": "Attendee-bag insert shipment", "offsetFromEventDays": -14 }
    ],
    "boothPlatinum": [
      { "title": "Booth wall artwork (6m)", "offsetFromEventDays": -21 },
      { "title": "Pre-keynote video - 3 images", "offsetFromEventDays": -21 }
    ],
    "boothDiamond": [
      { "title": "Booth wall artwork (5m)", "offsetFromEventDays": -21 },
      { "title": "Pre-keynote video - 1 image", "offsetFromEventDays": -21 }
    ],
    "boothGold": [
      { "title": "Booth wall artwork (4m)", "offsetFromEventDays": -21 }
    ]
  }
}
```

### How the job uses it
`WooCommercePullJob` already classifies each ordered product (kind + tier).
The change: instead of creating one generic task, it looks up the matching
task set(s) — always `allSponsors`, plus the tier set for a booth — and creates
each task, computing the due date from `offsetFromOrderDays` (order date + N)
or `offsetFromEventDays` (event date + N, N negative = before).

Idempotency stays: each task's `SourceKey` becomes
`woo:{orderId}:{productId}:{taskTitle-slug}` so re-runs never duplicate.

### To build
A small `SponsorTaskConfig` loader in `CommunityHub.Core/Config`, and the
expansion loop in `WooCommercePullJob`. The JSON shape above is a proposal —
confirm the task titles and offsets against the real sponsor onboarding before
building.

---

## 3. Sessionize speaker import

### Goal
An organizer clicks **"Import from Sessionize"** and every accepted speaker
becomes a `Participant` row (role `Speaker`), ready to sign in.

### How
Sessionize exposes a public read-only API per event (an "API endpoint" GUID
the organizer creates in Sessionize). It returns speakers as JSON — name,
email, bio, sessions.

Proposed: an organizer-only page with the Sessionize endpoint URL (stored in
`integrations.<edition>.json` as a `sessionize` block) and an Import button.
On click, a `SessionizeClient` in `CommunityHub.Core/Integrations` fetches the
speaker list and upserts `Participant` rows:
- Match on email within the edition (the existing unique key).
- New email → new `Participant`, role `Speaker`, `IsActive = true`.
- Existing email → update name; do **not** silently change role.
- Never delete: a speaker removed in Sessionize is deactivated by an organizer
  via the Participants page, not auto-removed (same principle as attendee
  reconciliation — no destructive automatic action).

### Open decisions before building
- Should MasterclassSpeakers be distinguished on import, or all imported as
  `Speaker` and reclassified manually? (Sessionize may not carry that.)
- Should import optionally send the welcome email to new speakers?
- The Sessionize API endpoint GUID — needs to be provided.

### To build
`SessionizeClient` (HTTP + JSON parse), a `sessionize` config block, an
organizer import page, and the upsert logic. Self-contained; does not touch
the other integrations.

---

## Summary — build order suggestion

1. **Sponsor-task JSON** — extends an existing job, well-understood, high value.
2. **Welcome email + wire the orphan templates** — needs wording sign-off.
3. **Sessionize import** — new integration, needs the API endpoint and the
   MasterclassSpeaker decision.
