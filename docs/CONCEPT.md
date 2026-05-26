# Community Event Hub — Concept & Vision

Source-of-truth design document for the **Community Event Hub** (CEH). This is the "what we want and why" doc — the architecture sketch, role-by-role hub spec, organizer admin areas, and automation scope all live here.

The README at the repo root has the install + run instructions. This file is the spec.

---

## Goals

- Build an **open-source community event platform** that other communities can re-use — to help scale the Microsoft (and adjacent) community by automating many manual tasks. Public repo: <https://github.com/KnudsenMorten/community-event-hub>.
- Provide a central hub for **speakers / volunteers / sponsors** to collect data and tasks — and let them see and manage their own submissions (self-service).
- **Centralize tasks** — one overview, not five.
- **Better change management** — hotel changes, speaker changes, etc. flow through the hub instead of email threads.
- **More automation** — fewer manual touches per participant.
- **Sync data to subsystems** — Zoho Backstage / ERP / Webshop, two-way where it makes sense.
- **Automate deliverables to partners** — hotel rooming list, Bella catering, swag / polo orders.
- **Automate invoicing** — faster money in the bank.

---

## Problems to solve

(vs. how organizing a conference usually works today)

- Move out of spreadsheets into a database — automation vs. manual.
- Avoid static forms — they generate endless follow-up and manual merges in Excel.
- Simplify the system landscape — drop Microsoft Planner with tenant integration for sponsors.
- Avoid manual follow-up by validating data at creation time (e.g. company tax ID).
- Separate collection of info away from Sessionize / generic forms — only collect from the selected people.
- Provide more self-service so we don't have to update on people's behalf (avoids human mistake).
- Minimize emails — only send for overdue tasks.

---

## Architecture

### Open-source public solution

- Lives on GitHub as [`KnudsenMorten/community-event-hub`](https://github.com/KnudsenMorten/community-event-hub).
- Each community (e.g. ELDK) clones / forks the public repo and customizes per event.

### Design

- Repo is 100% managed through GitHub CI/CD actions + Visual Studio Code with Claude AI integration.
- Per-event customization via JSON file (community name, hostname, deadlines, role rules).

### Deployment

- Automatic deployment to Azure of all components (dev + prod).
- Components: **Azure SQL, Azure App Service, Azure Functions, Application Insights**.
- Separate dev + prod instances per event (e.g. `rg-eldk27hub-dev`, `rg-eldk27hub-prod`).
- DNS:
  - dev: `dev.eldk27.eventhub.expertslive.dk`
  - prod: `eldk27.eventhub.expertslive.dk`

### Security — login

- Login via email + PIN code (PIN valid for 15 minutes).
- URL support with PIN auto-login (valid 7 days).

### Integration

- iframe-embedded inside Zoho Backstage.

### Cost

- Estimated platform cost per instance: **~€25 / month** (~€50 / month for dev + prod combined).

![Architecture overview](img/image1.png)
![Architecture detail](img/image2.png)

---

## End-user hubs (interfaces)

### Welcome (one-time only)

A first-time **Welcome** page is shown once per participant per edition.

![Welcome landing page](img/image3.png)

### Speaker Hub

- Tasks for speakers + overdue-only reminders — join Signal channel, deliver preview / final presentation, submit info, join Zoho Backstage.
- Collect + maintain info:
  - **Hotel** (incl. calendar invite)
  - **Appreciation dinner** (incl. calendar invite)
  - **Swag** incl. polo
  - **Travel reimbursement claim**
  - **Lunch participation** (pre-day)
  - **Speaker info** — accreditation, country, first-time speaker

![Speaker hub](img/image4.png)
![Speaker hub – tasks](img/image5.png)
![Speaker hub – details](img/image6.png)

### Volunteers Hub

- Volunteer interest sign-up (unconfirmed participant) — collect availability.
- **Confirmed-volunteers only** view: congrats mail on selection, then:
  - **Hotel** (incl. calendar invite)
  - **Appreciation dinner** (incl. calendar invite)
  - **Swag** incl. polo
  - **Lunch participation** (setup day, pre-day)
  - **Tasks assigned** with sync-to-calendar option (button)

![Volunteer hub](img/image7.png)
![Volunteer hub – tasks](img/image8.png)
![Volunteer hub – details](img/image9.png)

### Sponsors Hub

- Tasks + overdue-only reminders.
- Register company info — automatic upload to Zoho Backstage (exhibitors only).

![Sponsor hub](img/image10.png)

### Attendees Hub

- Pre-day attendees only:
  - See confirmed master class.
  - Remove master class booking(s).
  - Check if a booking was made with a different email.

![Attendee hub](img/image11.png)

---

## Organizers Hub (event management)

![Organizer hub](img/image12.png)

### Speakers management

- Import speakers from Sessionize Excel → into Hub.
- Import from Sessionize Excel → into Zoho Backstage.
- Set speaker participation (pre-day / main-day) — import OR grid multi-select.
- Activate / deactivate speakers.
- Dashboard — overview of pending tasks.
- Send reminders covering overdue tasks.
- Add / update / delete tasks for speakers, incl. deadlines.

### Sponsors management

- Dashboard — overview of pending tasks.
- Add (and link) extra event coordinator with automatic sync to ERP / Webshop / Hub.
- Delete event coordinator (automatic across ERP / Webshop / Hub).
- Set default signer / event coordinator.
- Add / update / delete tasks for sponsors with deadlines — targeting `exhibitors-all`, `sponsors-all`, or `exhibitor-{gold,diamond,platinum}`.

![Sponsor management](img/image13.png)

### Volunteer management

- Import tasks from Excel and assign to volunteers.
- Activate / deactivate volunteers.
- Dashboard — pending / missing submissions.

### Organizer tasks management

- Import / assign tasks to the ELDK leads.

### Hotel management

- Send Excel rooming list to the hotel.
- Import Excel with confirmation IDs.
- Send email with updated calendar invite carrying the hotel confirmation.
- Dashboard — pending / missing submissions.

![Hotel management](img/image14.png)

### Travel reimbursement management

- Overview of claims.
- Register payout + send confirmation email to speaker.

![Travel reimbursement management](img/image15.png)

### Swag management

- Excel overview for ordering: polo, awards, jackets, etc.
- Dashboard — pending / missing submissions.

![Swag management](img/image16.png)

### Bella group event management

- Lunch overview pre-day / main-day.
- Appreciation dinner overview incl. allergies.
- Tasks.
- Book furniture — via API (planned).
- Exhibitor booth overview.

### Group photos management

- Register company with contact details.
- Create / update calendar invite + send to the lead incl. internal participants.

### App game sponsor participation management

- Register participating sponsor (gift).
- Send reminder to sponsor to bring gift to event.

---

## Automation (scripts)

![Automation pipeline](img/image17.png)

### Sponsor automation

- Automatic create / update of sponsors + exhibitors in **Zoho Backstage** from sponsor webshop orders.
- Automatic create sponsors in **e-conomic ERP** + sync to sponsor webshop.
- Automatic API integration to validate company tax ID when a new sponsor is created.
- Ability to create / update customers + contacts via webhook / API.
- Automatic create contacts + roles in ERP (e-conomic) + sync to sponsor webshop.
- Automatic set default signer / event coordinator in sponsor webshop.
- Automatic create ERP (e-conomic) orders from sponsor webshop.
- Automatic currency check on order creation (today's FX).

![Sponsor automation](img/image18.png)

### Attendee automation

- Send info if no master class has been chosen.
- Send info if more than one master class has been booked — with ability to remove booking(s).

### Sponsor sync — to-do

- Orders only — create sponsor companies in the hub via script.
- Orders only — create contacts in the hub via script and link to sponsor company.
- Tasks are assigned to the sponsor **company** (not the individual contact). All contacts of a sponsor company see all tasks for that company.
