# CEH — complete outbound e-mail inventory (subject + full text)

Generated 2026-07-26 from the template files, resolving the layer precedence CEH uses at send time.

**Three layers, highest first:** (1) a per-edition **DB override** saved on `/Organizer/EmailTemplates` — badged *Edited* there, and it BEATS both files; (2) `config/email-templates/` (private overlay, 7 templates); (3) `templates/emails/` (shipped default). This document shows layers 2-3. **If a template has a DB override in an edition, that edition sends the override, not what is below.**

Tokens like `{{firstName}}` are substituted at send time. Every body is wrapped in `_layout.html` (logo header + support footer), and `[ELDK27]` is appended to every subject at the send chokepoint (§180).

| # | Template | Layer that wins |
|---|---|---|
| 1 | `app-game-gift-reminder` | shipped |
| 3 | `getstarted-deadline-reminder` | shipped |
| 4 | `getstarted-digest` | shipped |
| 5 | `group-photo-invite` | shipped |
| 6 | `hotel-cutoff-reminder` | shipped |
| 8 | `masterclass-cancelled` | shipped |
| 9 | `masterclass-cancelled-ticket` | shipped |
| 10 | `masterclass-confirmed` | **private overlay** |
| 11 | `masterclass-month-reminder` | shipped |
| 12 | `masterclass-offer` | shipped |
| 13 | `masterclass-promoted` | shipped |
| 14 | `masterclass-reassignment` | shipped |
| 15 | `masterclass-selection-invite` | **private overlay** |
| 16 | `masterclass-waitlisted` | shipped |
| 17 | `onboarding-getting-started` | shipped |
| 18 | `onboarding-step-reset` | shipped |
| 19 | `pending-master-class-selection` | shipped |
| 20 | `pin-signin` | shipped |
| 21 | `session-evaluation-results` | shipped |
| 22 | `session-time-location-changed` | shipped |
| 23 | `speaker-graphics-ready` | shipped |
| 24 | `speaker-question-digest` | shipped |
| 25 | `sponsor-leads-digest` | shipped |
| 26 | `task-deadline-reminder` | shipped |
| 27 | `task-manual-reminder` | shipped |
| 28 | `travel-reimbursement-paid` | shipped |
| 29 | `volunteer-help-raised` | shipped |
| 30 | `welcome` | shipped |
| 31 | `welcome-attendee-1day` | shipped |
| 32 | `welcome-eventpartner` | **private overlay** |
| 33 | `welcome-media` | **private overlay** |
| 34 | `welcome-speaker` | **private overlay** |
| 35 | `welcome-sponsor` | **private overlay** |
| 36 | `welcome-volunteer` | **private overlay** |

---

## 1. `app-game-gift-reminder`

*Source: `templates/emails/app-game-gift-reminder.html`*

**Subject:** {{eventDisplayName}}: please remember the app game gift &mdash; {{companyName}}

```text
Hi {{firstName}},
{{companyName}} takes part in the attendee app game at {{eventDisplayName}}{{eventCodeParens}} — thank you! This is a friendly reminder to bring the gift you committed:
{{giftDescription}}
Hand it to the organizer team at the info desk when you arrive for booth build-up.
The gift is raffled among attendees who complete the app game — winners are announced from the stage, with your company name on the slide.
Questions? Reply to this mail or contact {{supportEmail}}.
```

## 3. `getstarted-deadline-reminder`

*Source: `templates/emails/getstarted-deadline-reminder.html`*

**Subject:** Action needed by {{deadlineDate}}: complete your Get Started — {{eventDisplayName}}

```text
Hi {{firstName}},
A friendly heads-up: the deadline for completing your Get Started flow for {{eventDisplayName}}{{eventCodeParens}} is {{deadlineDate}} — and you still have {{openStepCount}} open step(s):
{{openStepsHtml}}
Each step takes a minute or two — completing them before the deadline lets us lock in hotels, dinner seats and catering counts:
Complete Get Started
Opens your Get Started flow — you'll be signed in automatically.
Questions? Email {{supportEmail}}.
See you at {{eventDisplayName}}{{eventCodeParens}}.
```

## 4. `getstarted-digest`

*Source: `templates/emails/getstarted-digest.html`*

**Subject:** {{eventDisplayName}} — a few Get Started steps are still waiting for you

```text
Hi {{firstName}},
You're almost set for {{eventDisplayName}}{{eventCodeParens}} — your Get Started flow just has {{openStepCount}} open step(s) left:
{{openStepsHtml}}
Each step takes a minute or two — pick up right where you left off:
Continue Get Started
Opens your Get Started flow — you'll be signed in automatically.
Questions? Email {{supportEmail}}.
See you at {{eventDisplayName}}{{eventCodeParens}}.
```

## 5. `group-photo-invite`

*Source: `templates/emails/group-photo-invite.html`*

**Subject:** {{eventDisplayName}}: your group photo session &mdash; {{companyName}}

```text
Hi {{contactName}},
Your group photo session for {{companyName}} at {{eventDisplayName}}{{eventCodeParens}} is scheduled:
{{slotTime}}
{{location}}
A calendar invitation is attached — accept it and the slot lands in your calendar. If we move the slot, the same invitation updates automatically.
Please gather your colleagues a few minutes early so the session stays on schedule.
Questions? Reply to this mail or contact {{supportEmail}}.
```

## 6. `hotel-cutoff-reminder`

*Source: `templates/emails/hotel-cutoff-reminder.html`*

**Subject:** {{eventDisplayName}}: hotel release deadline in {{daysLeft}} day(s) — {{hotelName}}

```text
Hi {{firstName}},
A hotel release deadline is coming up. After it passes, rooms you have
not released start costing money.
{{hotelName}}
{{cutoffLabel}}
Deadline: {{cutoffDate}} ({{daysLeft}} day(s) away){{releaseText}}
Where this hotel stands right now:
{{positionHtml}}
These numbers count only ACTIVE participants who asked for a room. Check them against the
contract before you release anything — the hub records the deadline, it never releases
rooms for you.
Open the allotment board
(the button signs you in automatically — no password needed)
```

## 8. `masterclass-cancelled`

*Source: `templates/emails/masterclass-cancelled.html`*

**Subject:** Master Class cancelled: {{masterClassTitle}}

```text
Hi {{firstName}},
Your Master Class seat for {{masterClassTitle}} at {{eventDisplayName}}{{eventCodeParens}} has been cancelled.
You can sign up for another Master Class on your self-service page (subject to availability):
Choose another Master Class
(the button signs you in automatically — no password needed)
```

## 9. `masterclass-cancelled-ticket`

*Source: `templates/emails/masterclass-cancelled-ticket.html`*

**Subject:** Your ticket for {{eventDisplayName}} was cancelled

```text
Hi {{firstName}},
Your 2-day ticket with Master Class for {{eventDisplayName}}{{eventCodeParens}} has been cancelled.
That means:
- your reserved Master Class seat has been released (it may be offered to the waitlist), and
- your Event Hub sign-in has been closed.
If your ticket is re-purchased or re-assigned, everything is restored automatically — the new holder receives their own email.
If this cancellation is unexpected, just reply to this email or contact us and we'll sort it out.
```

## 10. `masterclass-confirmed`

*Source: `config/email-templates/masterclass-confirmed.html`*

**Subject:** Your Master Class seat is confirmed – {{masterClassTitle}} — important details inside

```text
Dear {{firstName}},
Great news — your seat for the Master Class {{masterClassTitle}}
at {{eventDisplayName}} is confirmed! 🎉
🚪 Registration & breakfast from 07:00 — come early so we can check everyone in; your Master Class runs 09:00–16:00.
We've set up a dedicated Master Class page for you and the other
attendees of this class, where your speaker shares everything you need to get the
most out of it:
- What to expect
- Whether to bring a laptop
- Anything to prepare or install in advance
- Other practical details
- A shared Q&A where you can ask the speaker a question and see
what the other attendees have asked
Open my Master Class page
(the button signs you in automatically — no password needed)
Manage your seat & waitlists
Plans changed? You can give up your seat (so someone on the waitlist can take it)
or join the waitlist for another Master Class any time:
Manage my seat & waitlist
We look forward to seeing you at {{eventDisplayName}}!
Best regards,
Morten K, Kent, Martin, Morten L
Experts Live Denmark Organizer-team
```

## 11. `masterclass-month-reminder`

*Source: `templates/emails/masterclass-month-reminder.html`*

**Subject:** Coming up: {{masterClassTitle}}

```text
Hi {{firstName}},
Your Master Class {{masterClassTitle}} is coming up — here's the calendar entry you asked us to send (attached).
Questions? Email {{supportEmail}}.
```

## 12. `masterclass-offer`

*Source: `templates/emails/masterclass-offer.html`*

**Subject:** You've been moved into {{masterClassTitle}}

```text
Hi {{firstName}},
A seat opened up and you've been moved into the Master Class {{masterClassTitle}}.
Your previous Master Class seat has been released. You don't need to do anything — this is just to let you know you now have one confirmed Master Class.
Review my Master Class
(the button signs you in automatically — no password needed)
Questions? Email {{supportEmail}}.
```

## 13. `masterclass-promoted`

*Source: `templates/emails/masterclass-promoted.html`*

**Subject:** You've been moved into {{masterClassTitle}}

```text
Hi {{firstName}},
A seat opened up and you've been moved into the Master Class {{masterClassTitle}}.
Your previous Master Class seat has been released. You don't need to do anything — this is just to let you know you now have one confirmed Master Class. You can review or manage it on your self-service page:
Manage my seat
(the button signs you in automatically — no password needed)
Questions? Email {{supportEmail}}.
```

## 14. `masterclass-reassignment`

*Source: `templates/emails/masterclass-reassignment.html`*

**Subject:** Action required: Validate your Master Class for {{eventDisplayName}} (ticket re-assignment)

```text
Dear {{firstName}},
You have been re-assigned a 2-day ticket with Master Class for {{eventDisplayName}}{{eventCodeParens}}.
{{heldMasterClass}}
You can log in to the Event Hub to:
- Cancel a Master Class
- Sign up for waitlists
- Ask questions to your Master Class speakers
- Review preparation instructions for your class
- Sync the event to your calendar
Review my Master Class
(the button signs you in automatically — no password needed)
Waitlist
Once a Master Class fills up, the waitlist option becomes available, allowing you to join the waitlist for one alternative Master Class.
- You cannot join a waitlist for a Master Class while seats are still available.
- By signing up for a waitlist, you accept that your current Master Class booking will be cancelled and replaced by the waitlisted class if a confirmed seat becomes available.
Questions? Email {{supportEmail}}.
```

## 15. `masterclass-selection-invite`

*Source: `config/email-templates/masterclass-selection-invite.html`*

**Subject:** Welcome to {{eventDisplayName}} — choose your Master Class &amp; join the Party

```text
Dear {{firstName}},
Welcome to {{eventDisplayName}}! Thank you for choosing a 2-day ticket
with Master Class — we can't wait to see you. You have your own Event Hub to get everything
ready, with two quick things to set up: pick your Master Class and let us
know about the Party.
🎉 You're invited to the Party
16:00–18:30 on 9 February 2027, in the expo / food area at
Bella Center. You can RSVP in the hub — it's a two-second yes/no.
Get started in the hub
Browse the Event Hub
Both buttons open your hub — you'll be signed in
automatically, and can choose your Master Class and RSVP to the Party.
Please note: your Master Class seat isn't confirmed yet. To secure it, just
choose your preferred Master Class. You can jump straight to the chooser here:
Choose my Master Class →
We recommend doing this as soon as possible. Space is limited, so if your preferred class is
already full, pick another available one to guarantee yourself a seat.
Waitlist
If a Master Class is full, you can join its waitlist. A few things to know:
- You can't join a waitlist while seats are still available.
- By joining a waitlist, you accept that your current booking will be cancelled
and replaced if a seat opens up.
Master Class self-service (Event Hub)
Once your seat is secured, log in to the Event Hub any time to:
- Cancel your seat
- Join waitlists
- Ask questions to the speakers
- Review prep instructions
- Sync the session to your calendar
Questions & Support
If you have any questions or need help, please write to the organizer mail at
info@expertslive.dk.
We look forward to seeing you at {{eventDisplayName}}!
Best regards,
Morten K, Kent, Martin, Morten L
Experts Live Denmark Organizer-team
```

## 16. `masterclass-waitlisted`

*Source: `templates/emails/masterclass-waitlisted.html`*

**Subject:** You're on the waitlist: {{masterClassTitle}}

```text
Hi {{firstName}},
You're on the waitlist for {{masterClassTitle}}. We'll email you if a seat opens up.
{{waitlistPositionBlock}}
{{waitlistTerms}}
You can leave the waitlist any time from your self-service page:
Manage my waitlist
(the button signs you in automatically — no password needed)
Questions? Email {{supportEmail}}.
```

## 17. `onboarding-getting-started`

*Source: `templates/emails/onboarding-getting-started.html`*

**Subject:** Getting started at {{eventDisplayName}}

```text
Hi {{firstName}},
You are now active in the {{communityName}} Event Hub for {{eventDisplayName}}{{eventCodeParens}} as a {{roleName}}. This is the first of a short set of getting-started messages for your role.
{{roleGuidance}}
Open your Event Hub
Opens your hub — you'll be signed in automatically.
Questions? Reply to this mail or contact {{supportEmail}}.
```

## 18. `onboarding-step-reset`

*Source: `templates/emails/onboarding-step-reset.html`*

**Subject:** Action needed: {{stepLabel}} for {{eventDisplayName}}

```text
Hi {{firstName}},
One of your onboarding steps for {{eventDisplayName}}{{eventCodeParens}} has been re-opened and needs your attention:
{{stepLabel}}
Please open the hub and complete this step in the onboarding wizard.
Complete this step
(the button signs you in automatically — no password needed)
Questions? Reply to this mail or contact {{supportEmail}}.
```

## 19. `pending-master-class-selection`

*Source: `templates/emails/pending-master-class-selection.html`*

**Subject:** {{eventDisplayName}}: select your Master Class

```text
Hi {{firstName}},
Great news — your 2-day ticket for {{eventDisplayName}}{{eventCodeParens}} includes a full-day Master Class on the pre-day. Our records show you haven't selected one yet.
Seats are first-come, first-served
Popular Master Classes fill up — pick yours soon to get your preferred topic. You can change it later while seats remain.
Select your Master Class
(the button signs you in automatically — no password needed)
Questions? Reply to this mail or contact {{supportEmail}}.
```

## 20. `pin-signin`

*Source: `templates/emails/pin-signin.html`*

**Subject:** Your sign-in code

```text
Hi {{firstName}},
Your sign-in code is:
{{pin}}
This code expires in {{expiryMinutes}} minutes and can be used once.
If you did not request this, you can ignore this email.
```

## 21. `session-evaluation-results`

*Source: `templates/emails/session-evaluation-results.html`*

**Subject:** Your session evaluation — {{sessionTitle}}

```text
Hi {{firstName}},
Thank you for speaking at {{eventDisplayName}}{{eventCodeParens}}! Here is the audience evaluation for your session {{sessionTitle}}:
{{resultsHtml}}
Questions? Email {{supportEmail}}.
The {{eventDisplayName}}{{eventCodeParens}} team
```

## 22. `session-time-location-changed`

*Source: `templates/emails/session-time-location-changed.html`*

**Subject:** Your session schedule changed: {{sessionTitle}}

```text
Hi {{firstName}},
The schedule for your session {{sessionTitle}} at {{eventDisplayName}}{{eventCodeParens}} has just changed. Here is what is different:
When
{{oldTime}}
{{newTime}}
Where
{{oldRoom}}
{{newRoom}}
No action is needed from you — we just want to make sure you have the latest time and location. Open your speaker hub any time for the current details and to add the updated session to your calendar.
Open my speaker hub
(the button signs you in automatically — no password needed)
Questions? Email {{supportEmail}}.
```

## 23. `speaker-graphics-ready`

*Source: `templates/emails/speaker-graphics-ready.html`*

**Subject:** {{eventDisplayName}}: your promo graphics are ready

```text
Hi {{firstName}},
Great news — your speaker promo graphics for {{eventDisplayName}}{{eventCodeParens}} are ready! Help us spread the word: share them on LinkedIn and let your network know you're speaking.
On the Help Promote page you'll find your ready-made graphics plus a one-tap LinkedIn post you can edit and share.
Open Help Promote
(the button signs you in automatically — no password needed)
Questions? Email {{supportEmail}}.
```

## 24. `speaker-question-digest`

*Source: `templates/emails/speaker-question-digest.html`*

**Subject:** {{eventDisplayName}}: {{openCount}} new audience {{openCountNoun}} for your sessions

```text
Hi {{firstName}},
Attendees have been sending in questions ahead of {{eventDisplayName}}{{eventCodeParens}}. You have {{openCount}} open {{openCountNoun}} waiting for a reply across {{sessionCount}} of your {{sessionCountNoun}}.
Open the hub to read each question and reply — your answer is shared with your co-speakers on the same session too, so you can prepare together.
Read & answer questions
(the button signs you in automatically — no password needed)
You receive this because there are unanswered audience questions on your sessions. Questions you have already answered are not counted. Need help? Contact {{supportEmail}}.
```

## 25. `sponsor-leads-digest`

*Source: `templates/emails/sponsor-leads-digest.html`*

**Subject:** {{eventDisplayName}}: {{leadCount}} new lead(s) for {{sponsorCompany}}

```text
New leads for {{sponsorCompany}}
{{leadCount}} new lead(s) were captured for your company at {{eventDisplayName}}{{eventCodeParens}} since your last update:
Who
Contact
Captured (UTC)
{{leadListHtml}}
The full list (including these) is always available through your leads feed — the CSV / JSON endpoints and PowerShell samples are on the Your Leads API page in the hub.
Open the hub
(the button signs you in automatically — no password needed)
You receive this because lead notifications are enabled for your company. Ask the organizer team ({{supportEmail}}) to change cadence or recipients.
```

## 26. `task-deadline-reminder`

*Source: `templates/emails/task-deadline-reminder.html`*

**Subject:** {{eventDisplayName}} task {{state}}: {{taskTitle}}

```text
Hi {{firstName}},
Your task is {{state}}:
{{taskTitle}}
Due: {{dueDate}}
{{taskLink}}
Open the hub
(the button signs you in automatically — no password needed)
```

## 27. `task-manual-reminder`

*Source: `templates/emails/task-manual-reminder.html`*

**Subject:** Reminder: {{taskTitle}}

```text
Hi {{firstName}},
Quick reminder from the {{eventCode}} team about the task {{taskTitle}} ({{dueText}}).
{{descriptionBlock}}
Sign in to your Event Hub to mark it Done or update progress.
Open the hub
(the button signs you in automatically — no password needed)
```

## 28. `travel-reimbursement-paid`

*Source: `templates/emails/travel-reimbursement-paid.html`*

**Subject:** Your travel reimbursement has been paid

```text
Hi {{firstName}},
Your {{eventCode}} travel reimbursement of EUR {{amount}} has been processed and transferred today.
{{notesBlock}}
Thank you for being part of {{communityName}}.
```

## 29. `volunteer-help-raised`

*Source: `templates/emails/volunteer-help-raised.html`*

**Subject:** Help needed: {{taskTitle}} ({{categoryName}})

```text
Hi {{firstName}},
A volunteer in your {{categoryName}} category has asked for help with a task.
Volunteer{{volunteerName}}
Task{{taskTitle}}
Category{{categoryName}}
They wrote:
{{helpMessage}}
Please open the hub to reply and mark it answered or resolved.
Open the hub
(the button signs you in automatically — no password needed)
Questions? Reply to this mail or contact {{supportEmail}}.
```

## 30. `welcome`

*Source: `templates/emails/welcome.html`*

**Subject:** Welcome to {{communityName}} - {{eventDisplayName}}

```text
Hi {{firstName}},
Welcome to {{communityName}}. You have been added to the hub for {{eventDisplayName}}{{eventCodeParens}} as a {{roleName}}.
The hub is where you will find everything relevant to your role:
{{roleGuidance}}
Your first step: open the hub and complete the tasks in the Get Started flow — it walks you through everything we need from you.
To sign in, go to the hub and enter your email - you will receive a one-time code. No password to remember.
Open the hub — Get Started
Browse the Event Hub
Both buttons open your hub — you'll be signed in automatically.
See you at {{eventDisplayName}}{{eventCodeParens}}.
```

## 31. `welcome-attendee-1day`

*Source: `templates/emails/welcome-attendee-1day.html`*

**Subject:** Welcome to {{eventDisplayName}}

```text
Hi {{firstName}}, welcome!
You have a ticket for {{eventDisplayName}}{{eventCodeParens}} — and your own Event Hub to go with it.
There's one quick thing to do: let us know whether you'll join us at the Party (16:00–18:30, in the expo / food area at Bella Center). Open the hub and tap Get Started — it's a two-second yes/no.
Open the Event Hub — Get Started
Browse the Event Hub
Both buttons open your hub — you'll be signed in automatically.
Questions? Email {{supportEmail}}.
See you at {{eventDisplayName}}{{eventCodeParens}}.
```

## 32. `welcome-eventpartner`

*Source: `config/email-templates/welcome-eventpartner.html`*

**Subject:** Welcome to {{eventDisplayName}}

```text
Dear {{firstName}},
Thank you for partnering with us on the logistics for
{{eventDisplayName}} (ELDK27) — your support is a key part of making
the event run smoothly! The event takes place on
9–10 February 2027 at Bella Center, Copenhagen.
Before we get into the logistics, if you'd like to get a feel for the atmosphere
from last conference, take a look at these videos:
- Highlights
- Attendee perspectives
- Volunteer perspectives
- Sponsor interviews
- Sponsor thank-you
- Full event recap (extended)
Program format for ELDK27
- Pre-day, 9 Feb 2027: Master Classes, Party, Appreciation Dinner
- Main day, 10 Feb 2027: Sessions, Ask the Experts, and panel discussions
Event Hub
This year we're introducing a new, self-built event hub platform that we'll use
to handle all event logistics across every role: sponsors, speakers, media, event
partners, volunteers, and attendees. In the hub you'll find your tasks, deadlines,
key dates and times, logistics information, and more.
The hub is built by Morten Knudsen, lead organizer of ELDK27, and we'll keep
refining it over the coming months with bug fixes and new features — so please bear
with us if you hit a small bug now and then :-) Drop an email to
mok@expertslive.dk if you find a bug or if
you have ideas on how to improve the platform. Thank You :-)
Your First Onboarding Steps
Please log in to the Event Hub and complete the tasks in the Get Started flow
first — it walks you through everything you need to set up, one short step at a time.
Afterwards you can change any of it under Event Logistics if needed.
Open the Event Hub — Get Started
Browse the Event Hub
(both buttons sign you in automatically — Get Started opens your personal task list, Browse opens the hub menu)
Questions & Support
If you have any questions or need help, don't hesitate to contact lead organizer
Morten Knudsen (mok@expertslive.dk) — or
write directly to the organizer mail at
info@expertslive.dk.
Best regards,
Morten K, Kent, Martin, Morten L
Experts Live Denmark Organizer-team
```

## 33. `welcome-media`

*Source: `config/email-templates/welcome-media.html`*

**Subject:** Welcome to {{eventDisplayName}}

```text
Dear {{firstName}},
Thank you for joining us to capture {{eventDisplayName}} (ELDK27)
on video and photo — your work is a huge part of how we share the event with the
community! The event takes place on
9–10 February 2027 at Bella Center, Copenhagen.
To get a feel for the style and atmosphere from last conference, take a look at these
videos:
- Highlights
- Attendee perspectives
- Volunteer perspectives
- Sponsor interviews
- Sponsor thank-you
- Full event recap (extended)
Program format for ELDK27
- Pre-day, 9 Feb 2027: Master Classes, Party, Appreciation Dinner
- Main day, 10 Feb 2027: Sessions, Ask the Experts, and panel discussions
Event Hub
This year we're introducing a new, self-built event hub platform that we'll use
to handle all event logistics across every role: sponsors, speakers, media, event
partners, volunteers, and attendees. In the hub you'll find your tasks, deadlines,
logistics information, and more. (Your shooting schedule is coordinated directly
with the organizer team, not in the hub.)
The hub is built by Morten Knudsen, lead organizer of ELDK27, and we'll keep
refining it over the coming months with bug fixes and new features — so please bear
with us if you hit a small bug now and then :-) Drop an email to
mok@expertslive.dk if you find a bug or if
you have ideas on how to improve the platform. Thank You :-)
If others on your team should have access to the event hub, send email to
info@expertslive.dk with name/email and
we'll add them.
Your First Onboarding Steps
Please log in to the Event Hub and complete the tasks in the Get Started flow
first — it walks you through everything you need to set up, one short step at a time.
Afterwards you can change any of it under Event Logistics if needed.
Open the Event Hub — Get Started
Browse the Event Hub
(both buttons sign you in automatically — Get Started opens your personal task list, Browse opens the hub menu)
Questions & Support
If you have any questions or need help, don't hesitate to contact lead organizer
Morten Knudsen (mok@expertslive.dk) — or
write directly to the organizer mail at
info@expertslive.dk.
Best regards,
Morten K, Kent, Martin, Morten L
Experts Live Denmark Organizer-team
```

## 34. `welcome-speaker`

*Source: `config/email-templates/welcome-speaker.html`*

**Subject:** Welcome to {{eventDisplayName}}

```text
Dear {{firstName}},
Once again, congratulations on being selected to speak at
{{eventDisplayName}} (ELDK27) — we're thrilled to have you on the
program! The event takes place on
9–10 February 2027 at Bella Center, Copenhagen.
Before we get into the logistics, if you'd like to relive the atmosphere from
last conference, take a look at these videos:
- Highlights
- Attendee perspectives
- Volunteer perspectives
- Sponsor interviews
- Sponsor thank-you
- Full event recap (extended)
Program format for ELDK27
- Pre-day, 9 Feb 2027: Master Classes, Party, Appreciation Dinner
- Main day, 10 Feb 2027: Sessions, Ask the Experts, and panel discussions
Event Hub
This year we're introducing a new, self-built event hub platform that we'll use
to handle all event logistics across every role: sponsors, speakers, media, event
partners, volunteers, and attendees. In the hub you'll find your tasks, deadlines,
session details, key dates and times, logistics information, and more.
The hub is built by Morten Knudsen, lead organizer of ELDK27, and we'll keep
refining it over the coming months with bug fixes and new features — so please bear
with us if you hit a small bug now and then :-) Drop an email to
mok@expertslive.dk if you find a bug or if
you have ideas on how to improve the platform. Thank You :-)
Your First Onboarding Steps
Please log in to the Event Hub and complete the tasks in the Get Started flow
first — it walks you through everything you need to set up, one short step at a time.
Afterwards you can change any of it under Event Logistics if needed.
Open the Event Hub — Get Started
Browse the Event Hub
(both buttons sign you in automatically — Get Started opens your personal task list, Browse opens the hub menu)
Questions & Support
If you have any questions or need help, don't hesitate to contact speaker lead
Kent Agerlund (kea@expertslive.dk) or lead
organizer Morten Knudsen (mok@expertslive.dk)
— or write directly to the organizer mail at
info@expertslive.dk.
Best regards,
Morten K, Kent, Martin, Morten L
Experts Live Denmark Organizer-team
```

## 35. `welcome-sponsor`

*Source: `config/email-templates/welcome-sponsor.html`*

**Subject:** Welcome to {{eventDisplayName}}

```text
Dear {{firstName}},
Thank you for supporting us as an exhibitor for
{{eventDisplayName}} (ELDK27). The event takes place on
9–10 February 2027 at Bella Center, Copenhagen. You are receiving
this email in your role as {{sponsorRole}}.
This year we've added a few exciting things to the event: a
multi-day expo, a pre-day party, and a
brand-new event hub platform.
Before we get into the logistics, if you'd like to relive the atmosphere from
last conference, take a look at these videos:
- Highlights
- Attendee perspectives
- Volunteer perspectives
- Sponsor interviews
- Sponsor thank-you
- Full event recap (extended)
Multi-day Expo & Party (included in your package)
- (New) Pre-day, 9 Feb 2027: Half-day expo, Party with attendees after master class ends
- Main day, 10 Feb 2027: Full-day expo
Event Hub
This year we're introducing a new, self-built event hub platform that we'll use
to handle all event logistics across every role: sponsors, speakers, media, event
partners, volunteers, and attendees. In the hub you'll find your tasks, deadlines,
links to your leads in Zoho Backstage, key dates and times, logistics information,
and more.
The hub is built by Morten Knudsen, lead organizer of ELDK27, and we'll keep
refining it over the coming months with bug fixes and new features — so please bear
with us if you hit a small bug now and then :-) Drop an email to
mok@expertslive.dk if you find a bug or if
you have ideas on how to improve the platform. Thank You :-)
Your First Onboarding Steps
To help us start building your sponsor profile, please log in to the Event Hub
and complete the tasks in the Get Started flow first — it walks you through everything you
need to set up. Afterwards you can change any of it under Event Logistics
if needed.
If others on your team should have access to the event hub, send email to
info@expertslive.dk with name/email and
we'll add them.
Open the Event Hub — Get Started
Browse the Event Hub
(both buttons sign you in automatically — Get Started opens your personal task list, Browse opens the hub menu)
Questions & Support
If you have any questions or need help, don't hesitate to contact your sponsor
lead and lead organizer, Morten Knudsen, at
mok@expertslive.dk (email/Teams) or on mobile
+45 40 178 179.
Best regards,
Morten K, Kent, Martin, Morten L
Experts Live Denmark Organizer-team
```

## 36. `welcome-volunteer`

*Source: `config/email-templates/welcome-volunteer.html`*

**Subject:** Welcome to {{eventDisplayName}}

```text
Dear {{firstName}},
Thank you so much for signing up to volunteer at
{{eventDisplayName}} (ELDK27) — the event simply wouldn't happen
without you! This mail confirms you as a selected volunteer. The event takes place on
9–10 February 2027 at Bella Center, Copenhagen.
Before we get into the logistics, if you'd like to relive the atmosphere from
last conference, take a look at these videos:
- Highlights
- Attendee perspectives
- Volunteer perspectives
- Sponsor interviews
- Sponsor thank-you
- Full event recap (extended)
Program format for ELDK27
- Move-in day, 5 Feb 2027: Organizers and event partner move from storage rooms
- Logistics, 6 Feb 2027: Organizer logistics day
- Packing-day, 7 Feb 2027: Packing day with volunteers, you can help
- Setup-day, 8 Feb 2027: Setup of Expo, Lounge, lamps and more
- Pre-day, 9 Feb 2027: Master Classes, Party, Appreciation Dinner
- Main day, 10 Feb 2027: Sessions, Ask the Experts, and panel discussions
Event Hub
This year we're introducing a new, self-built event hub platform that we'll use
to handle all event logistics across every role: sponsors, speakers, media, event
partners, volunteers, and attendees. In the hub you'll find your tasks, shifts,
deadlines, key dates and times, logistics information, and more.
The hub is built by Morten Knudsen, lead organizer of ELDK27, and we'll keep
refining it over the coming months with bug fixes and new features — so please bear
with us if you hit a small bug now and then :-) Drop an email to
mok@expertslive.dk if you find a bug or if
you have ideas on how to improve the platform. Thank You :-)
Your First Onboarding Steps
Please log in to the Event Hub and complete the tasks in the Get Started flow
first — it walks you through everything you need to set up, one short step at a time.
Afterwards you can change any of it under Event Logistics if needed.
Open the Event Hub — Get Started
Browse the Event Hub
(both buttons sign you in automatically — Get Started opens your personal task list, Browse opens the hub menu)
Questions & Support
If you have any questions or need help, don't hesitate to contact volunteer lead
Morten Leth Hedegaard (mlh@expertslive.dk)
or lead organizer Morten Knudsen
(mok@expertslive.dk) — or write directly to
the organizer mail at info@expertslive.dk.
Best regards,
Morten K, Kent, Martin, Morten L
Experts Live Denmark Organizer-team
```


