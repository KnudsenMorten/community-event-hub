# CommunityHub - Test Walkthrough

Four real test accounts are seeded by `scripts/seed-eldk27.sql`. Use them to
walk through the role-personalized experience once the app is running.

| Role      | Email                          | What to test |
|-----------|--------------------------------|--------------|
| Organizer | mok@expertslive.dk             | Dashboard, data grids, participant management, imports |
| Speaker   | mok@mortenknudsen.net          | Hub, hotel/dinner forms, task list |
| Volunteer | mortenknudsen1974@gmail.com    | Hub, volunteer sign-up wizard, forms |
| Sponsor   | mok@2linkit.net                | Sponsor area, complete/reopen a task |

(Two example.com rows - Masterclass Speaker, Attendee - are also seeded.)

## Prerequisites (these come BEFORE any walkthrough)

The app has never been compiled or run. In order, on a machine with the
.NET 8 SDK:

1. `dotnet build` the solution - fix compile errors first.
2. `dotnet ef migrations add InitialCreate` then `dotnet ef database update`
   - this creates the schema. The seed script cannot run before this.
3. Run `scripts/seed-eldk27.sql` against the database.
4. Configure Brevo SMTP secrets, or the PIN email cannot be sent (see below).
5. `dotnet run` the web project.

## Sign-in flow (every role)

1. Go to `/Login`.
2. Enter the role's email. A 6-digit PIN is emailed (15-minute expiry).
3. Enter the PIN. You land on the role-personalized hub.

NOTE: the PIN is sent by email via Brevo. If Brevo secrets are not set the
send fails and you cannot log in. For local testing without Brevo, the PIN
can be read from the application logs - `PinLoginService` logs the generated
code. Treat that as a local-only convenience, never a production path.

## What each role should see

### Organizer (mok@expertslive.dk)
- Hub shows the Organizer tools section.
- `/Organizer/Dashboard` - completion bars, task status, etc. (mostly zero
  until forms are filled - expected on a fresh seed).
- `/Organizer/DataGrid` - the seeded people; edit IsActive / hotel dates.
- `/Organizer/Participants`, `/Organizer/TasksTable`, the imports.

### Speaker (mok@mortenknudsen.net)
- Hub shows hotel form, dinner form, task list.
- Task list has one seeded task ("Submit session title and abstract").
- Fill the hotel and dinner forms; re-open to confirm they saved.

### Volunteer (mortenknudsen1974@gmail.com)
- Hub links to the volunteer sign-up wizard.
- Walk the 3 steps (shifts -> role/hours -> review); confirm it saves.

### Sponsor (mok@2linkit.net)
- Sponsor area shows one seeded task ("Upload company logo...").
- Mark it complete, then reopen it - both should work.
- This account has SponsorCompanyId 'test-2linkit', which is why the
  company-scoped sponsor page shows the task.

## Honest caveats

- This walkthrough is only possible AFTER the build + migration succeed.
  Until then it is a plan, not a test.
- The seed assumes EF's default table/column naming. It is correct against
  the model as designed, but can only be confirmed once the migration has
  generated the actual schema.
- Scheduled jobs (reminders, WooCommerce, Zoho, Backstage) are separate from
  this UI walkthrough and are gated by their own config flags.
