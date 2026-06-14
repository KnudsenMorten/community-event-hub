# Community Event Hub — Feature Catalog

*Delivered feature set, as of 2026-06-14.*

Community Event Hub (CEH) is the all-in-one workspace that runs a tech-community
conference end to end: one home for your organizers, speakers, volunteers,
sponsors and attendees. Everyone signs in to a single hub tailored to their role,
fills in the forms that apply to them, sees exactly the tasks they owe, and gets
gentle, well-timed reminders so nothing slips. Organizers get live dashboards,
exports and a one-stop email center; sponsors get their own lead pipeline; speakers
get their deadlines; attendees get a clean status view.

This catalog describes what the product does for each audience today. Features are
grouped by area — Platform, Sign-in, Crew, Self-service Forms, Tasks & Reminders,
Sessions & Surveys, Sponsors, Sponsor Leads, Attendees, Email, Organizer Hub, and
Hosting & Reliability.

---

## 1. Platform — built for every edition

- **One hub, every year, every community.** The same platform powers each edition
  and each community event. Launching a new edition is a matter of configuration —
  a new event and its settings — never a rebuild. The year lives only in your web
  address and the event's display name, so nothing has to be re-coded season to
  season.
- **Clean separation of product, code and your community.** The product is
  Community Event Hub; your community keeps its own name and branding throughout.
- **Everything about an edition is configuration.** Event details, sponsors,
  content, hotel, integrations and speaker deadlines are all settings you can edit,
  not code you have to change.
- **A sanitized public template.** A scrubbed template version of the platform is
  published openly, while your real configuration, logos and production settings
  stay private.

## 2. Sign-in & embedding — frictionless, no new passwords

- **One-time PIN by email — no new account to remember.** Crew sign in with just
  their email: the hub sends a 6-digit PIN that expires in 15 minutes and works
  once. No passwords to create, reset or forget. Sensible safeguards are built in
  (rate limiting, lockout after repeated wrong tries, and neutral messaging that
  never reveals whether an email is registered).
- **"Stay signed in" your way.** At login you choose how long to stay signed in —
  a day, a week (the default), a month, or until you sign out — and your session
  refreshes itself as you keep using the hub.
- **Magic-link login.** Invitation emails can carry a tap-to-sign-in link, so crew
  land straight in their hub without typing a PIN.
- **Ready for single sign-on.** The sign-in system is designed so a trusted
  identity provider can be added as a drop-in option in future, without disrupting
  the PIN experience.
- **Embeds safely in your event portal.** The hub can appear inside your existing
  conference platform (e.g. a Backstage portal) as a seamless embedded panel, with
  the security controls needed to keep that safe.

## 3. Crew profiles & roles — the right hub for each person

- **A complete crew profile.** Each person has one profile per edition: name,
  contact details, role, accreditation (MVP / Expert / RD / MS Employee), awards,
  clothing sizes, and status flags like verified and packed.
- **A tailored hub per role.** Every role — Organizer, Speaker, Masterclass
  Speaker, Volunteer, Sponsor, Speaker-Sponsor, Video, Photography, VIP, Attendee —
  sees a hub built around what that person actually needs to do.
- **A friendly one-time welcome.** New crew get a welcome page the first time they
  arrive, once per edition.
- **Activate or deactivate people in a click.** Organizers can filter crew by role
  and status and switch someone active or inactive; deactivated people can no
  longer sign in.
- **Link a sponsor contact to their company.** When editing someone, organizers can
  set or clear the sponsor company a contact belongs to, so that person sees exactly
  their company's sponsor tasks — and unlink them just as easily. *(✅ 2026-06-14)*

## 4. Self-service forms — crew fill in their own details

Each form is short, mobile-friendly, and wired so that completing it does the right
follow-up automatically.

- **Appreciation dinner.** RSVP with a calendar invite, and capture allergies for
  the dinner.
- **Hotel.** Book a room and receive a hotel calendar invite; this feeds the
  rooming list and the room-night forecast.
- **Lunch.** Sign up for pre-day and main-day lunch.
- **Speaker info.** Speakers see their imported session details at a glance.
- **Swag.** Choose polo, jacket and award preferences.
- **Travel.** Submit a reimbursement claim, which automatically creates the
  matching invoice/payout task.
- **Volunteer sign-up.** A single guided, multi-step wizard that sets up the right
  tasks for each volunteer (the older one-page form was retired so there is exactly
  one path — ✅ 2026-06-14).
- **Late-change alerts done right.** If someone edits their hotel, dinner or shift
  details after the change deadline, organizers are notified of the late change;
  edits before the deadline stay quiet — no inbox noise.

## 5. Tasks & reminders — nothing slips, no inbox spam

- **A personal to-do list for every person.** Each crew member sees only their own
  tasks, can tick them off, and the list fills itself from the forms they complete
  and the role they hold.
- **Speaker deadlines, scheduled for you.** Each speaker automatically gets a dated
  task for every key milestone, so the path from accepted to on-stage is laid out
  for them.
- **A reminder engine that's gentle and reliable.** Reminders go out on a sensible
  cadence per type — speaker milestones counting down (and a nudge if overdue), a
  weekly digest of what's still pending, weekly sponsor and form chasers, and a
  short series for general tasks. It never double-sends and quietly catches up if a
  day is missed.
- **Tuned entirely through settings.** Whether each reminder type is on, how often
  it goes, exactly what it says, and who receives it (including CC, BCC and
  escalation) are all configurable — no code changes.
- **Built to minimize email.** The guiding principle is to nudge only when
  something is actually overdue.

## 6. Sessions & surveys — from call-for-speakers to the schedule

- **Import your speakers from a spreadsheet.** Upload your Sessionize export and the
  hub reads the columns in any order, creates or updates speakers (matched on
  email, never overwriting roles), and clearly reports any rows it had to skip. No
  network dependency — just the file.
- **Imported speakers get welcomed automatically.** New speakers receive their
  welcome email on import, once.
- **Welcome people you add by hand, too.** When an organizer creates a participant
  manually, they can send the welcome email right then — or send it later from the
  edit screen — using the same branded template. It is sent only once per person, so
  there is no risk of a duplicate welcome. *(✅ 2026-06-14)*
- **Public, no-login surveys.** Run a beautiful 3-step survey at its own web
  address — pick a track, rank topics, set your level — with a live results
  dashboard anyone can view. Spam protection is built in and no sign-in is
  required.
- **Call-for-speakers demand survey.** Gather what your audience actually wants:
  weighted topic rankings, per-track breakdowns and a level distribution, all on a
  shareable results page that helps you shape the agenda.
- **Mobile-first and polished.** Surveys feature a clean hero, per-step imagery,
  friendly skill levels, clear ranking, and per-track deep links — designed for
  phones first.

## 7. Sponsors — managed as companies, with the right tasks

- **A sponsor is a company, not a single contact.** Every contact at a sponsor
  company sees that company's shared tasks, so nothing depends on one person.
- **Your company directory stays the source of truth.** Company and contact details,
  including who signs and who coordinates the event, come from your central company
  directory; the hub reads from it and never duplicates it.
- **Always the right public company name.** Sponsor-facing text shows the company's
  chosen public name, with a sensible fallback chain so a name always appears.
- **Booth tasks generated from what each sponsor bought.** Each booth product is
  recognized automatically and turned into the right set of tasks — shared booth
  basics plus the extras that come with each tier (Platinum / Diamond / Gold) — so
  new booth products need no setup.
- **A baseline checklist for every sponsor.** Logo upload, onboarding, company
  description, attendee-bag insert and shipment, and the app-game are set up for all
  sponsors.
- **Deadlines that make sense.** Most sponsor deadlines are anchored to the event
  date; logo and description deadlines are anchored to the first order — all
  configurable per edition.
- **No duplicate tasks, ever.** Tasks are de-duplicated across orders, so a company
  never sees the same item twice.
- **Clear, friendly task wording.** The sponsor task list is hand-curated for
  clarity, with narrative guidance rather than raw order data.
- **Automatic work stays invisible.** Things the platform handles behind the scenes
  (webshop-to-portal sync, ERP sync, currency checks, masterclass reconciliation)
  are never shown to sponsors as to-dos.
- **Reminders reach the right people.** Assigned tasks nudge the responsible contact
  with event coordinators copied; unassigned tasks go to the coordinators; signers
  are never bothered with task reminders.
- **Tidy buttons instead of long links.** Task instructions render link text as
  clean buttons, hiding long underlying URLs.
- **Per-task upload folders with change alerts.** Each task can have its own upload
  folder with a simple edit link, and the hub notices when files change — and now
  reliably sees every file in a folder, however many there are. *(✅ 2026-06-14)*
- **Sponsor details and sponsor tasks, side by side.** Sponsors get a details card
  (company info, linked contacts, who manages them) alongside their task list.
- **Full sponsor management for organizers.** Organizers can add, link or remove
  coordinators, set the default signer and coordinator, and create or edit tasks
  targeted at all exhibitors, all sponsors, or a specific tier.

## 8. Sponsor leads — capture, screen and route booth leads

- **A leads API for sponsors.** Sponsors can pull their own leads as JSON or CSV via
  a simple, secured endpoint, with ready-made script samples and a browser-friendly
  "Your Leads API" page.
- **Secure, revocable access per sponsor.** Each sponsor gets its own access key and
  token; keys are shown once and stored only as a secure hash, and access can be
  revoked instantly.
- **A real lead pipeline.** Leads live in a proper pipeline with a live grid to
  Reply, mark Processed, set Interest, or flag Ignore/Junk — nothing is ever hard-
  deleted.
- **Timely notifications.** Sponsors can opt into a daily digest or near-real-time
  alerts of new leads, with junk skipped and recipients defaulting to all the
  company's contacts.
- **Smart junk screening.** Each lead gets a 0–100 quality score and label, and only
  unmistakable test entries are auto-flagged as junk — operators stay in control.

## 9. Attendees & masterclass reconciliation — one clear picture

- **Tickets and masterclass seats reconciled automatically.** The hub compares
  two-day tickets against masterclass bookings and surfaces the mismatches — no
  booking, no ticket, or duplicate bookings — with branded chaser emails to sort
  them out.
- **Attendee status in the hub, bookings stay at the source.** Attendees are synced
  in for visibility, with deep links back to the booking system; the hub never tries
  to re-do seat reservations, capacity or waitlists.
- **People decide identity, not algorithms.** "Same person, two emails" cases are
  resolved by a human or by the attendee via a chaser — never auto-merged.
- **An attendee browser for organizers.** A clean, read-only view with tiles,
  search, filters and CSV export (correctly handling accented names). Corrections
  happen at the source system, keeping data trustworthy.

## 10. Email & notifications — on-brand, controllable, safe

- **Reliable, branded email delivery.** All mail is sent through a professional
  relay from your event sender address.
- **One branded template engine.** Every email uses a shared branded shell with
  per-type content and simple token substitution; templates are built to render
  correctly across email clients, including Outlook.
- **A library of ready templates.** Welcome notes, reminders, chasers, app-game and
  broadcast messages all come as branded templates.
- **Every email is on-brand — including invitations, task nudges and payout
  confirmations.** Sign-in invitations, manual task reminders and travel-reimbursement
  confirmations now use the same branded shell as the rest, so all mail looks
  consistent and adapts automatically to each community's branding. *(✅ 2026-06-14)*
- **An Email Center for organizers.** Preview any template safely, send a one-click
  test to yourself, and watch a delivery pulse with a filterable history of what's
  been sent.
- **Broadcast to the groups you choose.** Send one personalized message ("Hi
  {firstName}") to selected role groups (and optionally attendees), with a recipient
  preview; sending is resilient — a single failure never stops the batch.

## 11. Organizer hub — run the whole event from one place

- **A live dashboard.** See form completion, participants by role, tasks and
  overdues, sponsor completion, attendee mismatches and volunteer coverage at a
  glance, with live pipeline cards for leads and event prep.
- **Practical data grids.** Work through participants and hotel bookings (with inline
  active and check-in/out toggles and filters) and tasks (inline edit), each with
  CSV export.
- **Hotel management.** Export the rooming list, import confirmation IDs, send
  updated calendar invites, and track it all on a dashboard.
- **Group photos.** Register a company and contact, schedule a slot, and send
  calendar invites that update cleanly rather than duplicating.
- **App game.** Register a sponsor's gift and send the gift reminder.
- **Travel reimbursements.** See all claims, register payouts, and send confirmation
  emails.
- **Swag ordering.** Produce a multi-sheet vendor spreadsheet for polo, award and
  jacket orders.
- **Lunch and dinner overviews.** See pre- and main-day lunch numbers and the
  appreciation-dinner list with allergies.
- **A sponsor admin area.** Manage the sponsor task catalog, run the leads pipeline
  (issue, rotate or revoke keys, set notification preferences, action leads), and
  watch a sponsor status dashboard sorted overdue-first.
- **Built-in safety on file handling.** Organizer tools that read files are guarded
  against path-traversal.

## 12. Hosting & reliability — production-grade by design

- **Defined entirely as code.** The full environment (database, web app, scheduled
  jobs, storage, secret vault, logging and monitoring) is described as code for
  consistent, repeatable deployments.
- **Scheduled jobs that just work.** Background jobs handle reminders, order pulls,
  attendee reconciliation, portal sync, sponsor-lead delivery and upload-change
  watching on their own schedules, each individually switchable.
- **Scripted, safe deploys with rollback.** Releases build a versioned artifact,
  deploy, and health-check; a one-command rollback is always available.
- **Zero-downtime production releases.** Production deploys go to a staging slot,
  warm up, then swap in — so visitors never see downtime.
- **Separate environments, shared upstreams.** Development and production are fully
  separated, while shared upstream services are reused across both.
- **Resilient on a budget.** The platform absorbs database cold-starts gracefully
  and runs happily on cost-efficient, auto-pausing infrastructure.
- **Schema managed as code.** Database structure is versioned and applied in a
  controlled way.
- **A safe public-mirror workflow.** Publishing to the public template runs through a
  controlled, allow-listed process with a dry-run pre-flight, so only intended
  content is ever made public.
- **Strong delivery governance.** Protected branches, required reviews, automated
  checks that scan for secrets, and a consistent commit convention keep the codebase
  clean and safe.
- **Custom domains per environment.** Each environment binds its own verified custom
  domain with a managed certificate.
