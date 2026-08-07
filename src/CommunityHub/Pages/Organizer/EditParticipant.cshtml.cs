using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

[Authorize]
public class EditParticipantModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly WelcomeEmailService _welcome;
    private readonly AttendeeOneDayWelcomeEmailService _attendeeWelcome;
    private readonly TimeProvider _clock;

    // §253 G11: reconciles the auto-seeded task set when the ROLE changes (prune the
    // old role's wizard/speakerdl tasks, seed the new role's). Optional so older test
    // constructions are unchanged; wired by DI at runtime.
    private readonly CommunityHub.Forms.RoleChangeTaskReconciler? _roleChange;

    // §707.15 — the ONE implementation of "make this person active again" (clears the organizer
    // tombstone, re-opens abandoned tasks, re-arms the welcome). Optional so existing test
    // constructions are unchanged; wired by DI at runtime.
    private readonly Core.Organizer.ParticipantDeactivationService? _reactivate;

    public EditParticipantModel(
        CommunityHubDbContext db, ICurrentParticipantAccessor participant,
        WelcomeEmailService welcome, AttendeeOneDayWelcomeEmailService attendeeWelcome,
        TimeProvider clock,
        CommunityHub.Forms.RoleChangeTaskReconciler? roleChange = null,
        Core.Organizer.ParticipantDeactivationService? reactivate = null)
    {
        _db = db;
        _participant = participant;
        _welcome = welcome;
        _attendeeWelcome = attendeeWelcome;
        _clock = clock;
        _roleChange = roleChange;
        _reactivate = reactivate;
    }

    public bool AccessDenied { get; private set; }
    public string? Message { get; private set; }
    public string? Error { get; private set; }
    public bool IsNew { get; private set; }

    /// <summary>True once this participant has already had a welcome email sent.</summary>
    public bool WelcomeAlreadySent { get; private set; }

    /// <summary>
    /// §761 — the speaker's own profile, shown READ-ONLY on this page so an organizer can copy the
    /// values straight into Zoho Backstage. Null for a non-speaker (or a speaker with no profile).
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-01: <i>"i would like to get all speaker details here including titel,
    /// description, country, etc so i can cut/paste it over if needed"</i>, then pointing at the
    /// <b>Speaker details</b> onboarding step: <i>"this is the info i mean"</i>.</para>
    ///
    /// <para>🔑 <b>Why it belongs here.</b> The Backstage speakers API is CREATE-ONLY, so when the
    /// §302b gap mail reports a field missing in Backstage he has to type it in by hand. Without
    /// this block he reads the field name in the mail and hunts for the value on a different CEH
    /// page, per speaker, per field.</para>
    ///
    /// <para>🔒 <b>READ-ONLY, deliberately.</b> He asked to COPY these, not to edit them — and they
    /// are already editable on the Speaker details step that owns them. A second editable copy would
    /// be two screens writing one profile, which is how a bio gets silently overwritten. Same reason
    /// the values render bare, with no surrounding quotes (§760): this is a paste target.</para>
    /// </remarks>
    public SpeakerProfile? Speaker { get; private set; }

    /// <summary>§761 — the derived Zoho "Skills" CSV, exactly as the gap mail reports it.</summary>
    public string? SpeakerSkills { get; private set; }

    /// <summary>§762 — true when this speaker's CURRENT country has been confirmed in Backstage.</summary>
    public bool CountryConfirmed { get; private set; }

    /// <summary>
    /// §762 — record that the speaker's country IS set in Zoho Backstage, so the gap mail stops
    /// asking. Stores the VALUE confirmed, not a flag, so a later country change re-asks.
    /// </summary>
    public async Task<IActionResult> OnPostConfirmCountryAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var profile = await _db.SpeakerProfiles
            .FirstOrDefaultAsync(sp => sp.ParticipantId == Id && sp.EventId == me.EventId, ct);
        if (profile is null || string.IsNullOrWhiteSpace(profile.Country))
            return RedirectToPage(new { Id, message = "No country is set in CEH, so there is nothing to confirm." });

        profile.CountryConfirmedInBackstage = profile.Country.Trim();
        profile.CountryConfirmedAt = _clock.GetUtcNow();
        profile.CountryConfirmedBy = me.Email;
        await _db.SaveChangesAsync(ct);

        return RedirectToPage(new
        {
            Id,
            message = $"Country '{profile.Country.Trim()}' confirmed as set in Backstage — "
                      + "it will stop appearing in the missing-details mail.",
        });
    }

    /// <summary>§762 — undo a confirmation (mis-clicked, or it turned out not to be set).</summary>
    public async Task<IActionResult> OnPostUnconfirmCountryAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var profile = await _db.SpeakerProfiles
            .FirstOrDefaultAsync(sp => sp.ParticipantId == Id && sp.EventId == me.EventId, ct);
        if (profile is not null)
        {
            profile.CountryConfirmedInBackstage = null;
            profile.CountryConfirmedAt = null;
            profile.CountryConfirmedBy = null;
            await _db.SaveChangesAsync(ct);
        }

        return RedirectToPage(new { Id, message = "Country confirmation removed — it will be reported again." });
    }

    [BindProperty(SupportsGet = true)] public int? Id { get; set; }
    [BindProperty(SupportsGet = true)] public string? message { get; set; }

    [BindProperty] public string Email { get; set; } = string.Empty;
    [BindProperty] public string FullName { get; set; } = string.Empty;

    /// <summary>
    /// §941 — first / last name, asked SEPARATELY on the pre-stage (create) form only.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-07: <i>"used to prestage a person and fill out the initial information
    /// like firstname, lastname, email, state (active/inactive) and link to a role"</i>.</para>
    ///
    /// <para>🔑 <b>No schema change.</b> The domain stores one <c>FullName</c>, and it stays that
    /// way — these two are composed into it on save. Splitting the column would ripple through every
    /// page, mail template and export that reads a name, which is exactly the "new subsystem" §941
    /// said this must not become.</para>
    ///
    /// <para>🔒 <b>Create only, and that is deliberate.</b> The EDIT form keeps the single Full name
    /// field. An existing row holds one string, so showing it as two boxes means guessing where to
    /// cut — and the guess is wrong for every "van der Berg", "de Jong" or mononym in the database.
    /// Typing a name in two halves is safe; splitting one that already exists is not.</para>
    /// </remarks>
    [BindProperty] public string? FirstName { get; set; }
    [BindProperty] public string? LastName { get; set; }
    [BindProperty] public string? Phone { get; set; }
    [BindProperty] public ParticipantRole Role { get; set; } = ParticipantRole.Speaker;
    [BindProperty] public bool IsActive { get; set; } = true;
    [BindProperty] public string? SponsorCompanyId { get; set; }

    /// <summary>
    /// When ticked on the create form, fire the welcome email to the new
    /// participant (idempotent via the SentReminder ledger). Defaults off so a
    /// bulk hand-add does not surprise anyone with mail.
    /// </summary>
    [BindProperty] public bool SendWelcome { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        if (Id is null)
        {
            IsNew = true;
            return Page();
        }

        if (!string.IsNullOrWhiteSpace(message)) Message = message;

        var p = await _db.Participants
            .FirstOrDefaultAsync(x => x.Id == Id && x.EventId == me.EventId, ct);
        if (p is null) { Error = $"Participant #{Id} not found."; IsNew = true; return Page(); }

        IsNew = false;
        Email = p.Email;
        FullName = p.FullName;
        Phone = p.Phone;
        Role = p.Role;
        IsActive = p.IsActive;
        SponsorCompanyId = p.SponsorCompanyId;

        WelcomeAlreadySent = await WelcomeSentAsync(p, ct);
        await LoadSpeakerDetailAsync(p, ct);
        return Page();
    }

    /// <summary>
    /// §761 — load the speaker profile for the copy/paste block. Speakers only; a blank panel on an
    /// attendee's page is noise, and a missing profile is not an error here.
    /// </summary>
    private async Task LoadSpeakerDetailAsync(Participant p, CancellationToken ct)
    {
        if (p.Role != ParticipantRole.Speaker) return;

        Speaker = await _db.SpeakerProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(sp => sp.ParticipantId == p.Id && sp.EventId == p.EventId, ct);

        // Derived, not stored — the same helper the §302b gap mail uses, so the page and the mail
        // never disagree about what CEH thinks this speaker's skills are.
        if (Speaker is not null)
        {
            SpeakerSkills = CommunityHub.Core.Integrations.ZohoFieldMap.SpeakerSkills(Speaker);
            // §762 — resolved by the SAME helper the reporter uses, so the button and the mail can
            // never disagree about whether this country still counts as confirmed.
            CountryConfirmed =
                CommunityHub.Core.Integrations.SpeakerZohoGapReporter.CountryIsConfirmed(Speaker);
        }
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var emailNorm = (Email ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(emailNorm) || !emailNorm.Contains('@'))
        {
            Error = "A valid email is required."; IsNew = (Id is null); return Page();
        }
        // §941 — the pre-stage form asks for first + last name; the domain stores one FullName.
        // Compose here rather than in the view, so a post that bypasses the form still lands in a
        // consistent shape. Only when FullName was not itself supplied, so the edit form is untouched.
        if (Id is null && string.IsNullOrWhiteSpace(FullName))
        {
            FullName = string.Join(' ', new[] { FirstName, LastName }
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!.Trim()));
        }

        if (string.IsNullOrWhiteSpace(FullName))
        {
            // Name the fields the organizer is actually looking at — on the create form there is no
            // box labelled "Full name" to go back and fill in.
            Error = Id is null
                ? "First name and last name are required."
                : "Full name is required.";
            IsNew = (Id is null); return Page();
        }

        Participant? p = Id is null
            ? null
            : await _db.Participants
                .FirstOrDefaultAsync(x => x.Id == Id && x.EventId == me.EventId, ct);

        if (p is null)
        {
            // Reject if email already taken in this edition.
            var clash = await _db.Participants.AnyAsync(
                x => x.EventId == me.EventId && x.Email == emailNorm, ct);
            if (clash)
            {
                Error = $"A participant with email {emailNorm} already exists in this edition.";
                IsNew = true; return Page();
            }

            p = new Participant
            {
                EventId = me.EventId,
                Email = emailNorm,
                FullName = FullName.Trim(),
                Phone = string.IsNullOrWhiteSpace(Phone) ? null : Phone.Trim(),
                Role = Role,
                IsActive = IsActive,
                // Organizer hand-add bypasses the pre-selection queue: the row is
                // activated immediately so the person can sign in right away.
                LifecycleState = ParticipantLifecycleState.Active,
                QueueSource = ParticipantQueueSource.Manual,
                SponsorCompanyId = string.IsNullOrWhiteSpace(SponsorCompanyId) ? null : SponsorCompanyId.Trim(),
                CreatedAt = _clock.GetUtcNow(),
            };
            _db.Participants.Add(p);
            await _db.SaveChangesAsync(ct);

            // Manual-create welcome hook: parity with the Sessionize import path,
            // which welcomes new speakers. Fires only when the organizer ticked
            // "Send welcome email" and the person is active. Idempotent via the
            // SentReminder ledger inside WelcomeEmailService.
            var note = "Created.";
            if (SendWelcome && p.IsActive)
            {
                try
                {
                    var sent = await _welcome.SendWelcomeAsync(p.Id, ct);
                    note = sent ? "Created and welcome email sent." : "Created (welcome already sent earlier).";
                }
                catch
                {
                    note = "Created, but the welcome email could not be sent (check email config).";
                }
            }
            return RedirectToPage("/Organizer/EditParticipant",
                new { Id = p.Id, message = note });
        }

        // Reject email change that collides with another row.
        if (!string.Equals(p.Email, emailNorm, StringComparison.OrdinalIgnoreCase))
        {
            var clash = await _db.Participants.AnyAsync(
                x => x.EventId == me.EventId && x.Email == emailNorm && x.Id != p.Id, ct);
            if (clash)
            {
                Error = $"Email {emailNorm} is already used by another participant in this edition.";
                IsNew = false; return Page();
            }
            p.Email = emailNorm;
        }

        var oldRole = p.Role;
        // 🔒 §707.15 — remember the BEFORE state: an inactive → active flip has to re-onboard.
        var wasActive = p.IsActive;
        p.FullName = FullName.Trim();
        p.Phone = string.IsNullOrWhiteSpace(Phone) ? null : Phone.Trim();
        p.Role = Role;
        p.IsActive = IsActive;
        p.SponsorCompanyId = string.IsNullOrWhiteSpace(SponsorCompanyId) ? null : SponsorCompanyId.Trim();
        await _db.SaveChangesAsync(ct);

        Message = "Saved.";

        // 🔒 §707.15 — INACTIVE → ACTIVE RE-ONBOARDS, FROM THIS FORM TOO. Operator 2026-07-30:
        // *"inactive to active must send email again"*, for ANY role.
        //
        // ⚠️ This checkbox wrote `p.IsActive` DIRECTLY and never went near
        // ParticipantDeactivationService — so the re-arm added there would have missed the page he
        // actually uses ("i primarily use the participant page"), and the person would have come
        // back silently with no welcome. Routing the transition through ReactivateAsync keeps ONE
        // implementation of "make this person active again": it clears the organizer tombstone,
        // re-opens the tasks abandoned at deactivation, and re-arms the welcome (plus the Master
        // Class selection invite for a 2-day attendee) so the reconcile jobs send it on their next
        // pass through the normal ring-gated path.
        if (!wasActive && IsActive && _reactivate is not null)
        {
            try
            {
                await _reactivate.ReactivateAsync(me.EventId, p.Id, me.Email, ct);
                Message = "Saved. Re-activated — their welcome will be sent again.";
            }
            catch (Exception ex)
            {
                // Never let the re-onboarding step lose the save the organizer just made.
                Message = $"Saved, but the welcome could not be re-armed: {ex.Message}";
            }
        }

        // §253 G11: a ROLE change is no longer a bare field write — prune the old
        // role's auto-seeded tasks (wizard-step mirrors + dated speakerdl: deadlines,
        // which otherwise keep firing due-day reminders) and seed + reconcile the new
        // role's steps. Runs AFTER the save so the wizards read the persisted role.
        if (oldRole != Role && _roleChange is not null)
        {
            try
            {
                var (removed, createdTasks) = await _roleChange.ReconcileAsync(
                    me.EventId, p.Id, oldRole, Role, ct);
                if (removed > 0 || createdTasks > 0)
                {
                    Message = $"Saved. Role changed {oldRole} → {Role}: "
                        + $"{removed} old-role task(s) removed, {createdTasks} new-role task(s) added.";
                }
            }
            catch
            {
                // The save itself succeeded; the nightly SpeakerDeadlineSeeder sweep +
                // page-load seeding self-heal the task set if this reconcile hiccups.
            }
        }
        IsNew = false;
        WelcomeAlreadySent = await WelcomeSentAsync(p, ct);
        return Page();
    }

    /// <summary>
    /// 🔒 §707.14 — RE-SEND A 2-DAY ATTENDEE'S REAL WELCOME (the Master Class selection invite).
    /// </summary>
    /// <remarks>
    /// Operator 2026-07-30: *"i primarily use the participant page, so it could be cool to have it
    /// there as well"* — so the same action the Attendees grid grew also lives here, beside the
    /// welcome button that does NOT do this.
    ///
    /// <para>🔑 Why a separate button: *Send/Resend welcome email* sends the GENERIC welcome and is
    /// idempotent (once sent it no-ops), and the reset-welcome path calls
    /// <c>AttendeeOneDayWelcomeEmailService</c>, retired by §299 OPEN-26 and hard-wired to return
    /// false. Neither reaches <c>masterclass-selection-invite</c>, which IS the 2-day welcome
    /// (§241).</para>
    ///
    /// <para>This page keys on a PARTICIPANT; the invite keys on the ATTENDEE (ticket) row, so the
    /// live 2-day ticket is resolved by email. The §707.14 deactivated-login guard still applies
    /// underneath — a re-send to a switched-off login is refused rather than delivering a
    /// magic-link button that cannot resolve.</para>
    /// </remarks>
    [CommunityHub.Audit.Audit("Re-send Master Class selection invite",
        Category = AuditCategory.Admin, TargetType = "Participant")]
    public async Task<IActionResult> OnPostResendSelectionInviteAsync(
        [FromServices] Core.Email.MasterClassEmailService mcEmail, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var p = Id is null ? null
            : await _db.Participants.FirstOrDefaultAsync(x => x.Id == Id && x.EventId == me.EventId, ct);
        if (p is null) { Error = "Participant not found."; IsNew = true; return Page(); }

        // The LIVE 2-day ticket for this address (never a cancelled one).
        var email = (p.Email ?? string.Empty).Trim().ToLowerInvariant();
        var attendeeId = await _db.Attendees
            .Where(a => a.EventId == me.EventId
                        && a.Email.ToLower() == email
                        && a.TicketStatus == TicketStatus.TwoDay
                        && a.MirrorState == MirrorState.Active)
            .Select(a => (int?)a.Id)
            .FirstOrDefaultAsync(ct);

        if (attendeeId is null)
        {
            Error = "No active 2-day ticket for this address — the selection invite only applies to "
                  + "a live 2-day holder.";
        }
        else
        {
            try
            {
                var baseUrl = $"{Request.Scheme}://{Request.Host}";
                var sent = await mcEmail.SendSelectionInviteAsync(attendeeId.Value, baseUrl, force: true, ct);
                if (sent) { Message = "Selection invite re-sent."; }
                else
                {
                    Error = "Not sent — most likely this login is deactivated, which would make the "
                          + "magic link in the mail fail. Re-activate it and try again.";
                }
            }
            catch (Exception ex)
            {
                Error = $"Could not re-send the selection invite: {ex.Message}";
            }
        }

        IsNew = false;
        Email = p.Email; FullName = p.FullName; Phone = p.Phone;
        Role = p.Role; IsActive = p.IsActive; SponsorCompanyId = p.SponsorCompanyId;
        return Page();
    }

    /// <summary>
    /// Manually send (or re-attempt) the welcome email to an existing
    /// participant. Idempotent: WelcomeEmailService no-ops if it has already
    /// been sent. Lets an organizer welcome someone who was added by hand or
    /// before the welcome hook existed.
    /// </summary>
    public async Task<IActionResult> OnPostSendWelcomeAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var p = Id is null ? null
            : await _db.Participants.FirstOrDefaultAsync(x => x.Id == Id && x.EventId == me.EventId, ct);
        if (p is null) { Error = "Participant not found."; IsNew = true; return Page(); }

        if (!p.IsActive)
        {
            Error = "Cannot send a welcome to a deactivated participant.";
        }
        else
        {
            try
            {
                // 🔴 §964 — THE RESEND BUTTON MUST ACTUALLY RESEND. It was labelled "Resend welcome
                // email" but called SendWelcomeAsync WITHOUT `force`, so the once-ever ledger check
                // short-circuited every click: nothing sent, and — because the send never reached the
                // transport — no EmailLog row either, so it was invisible in the Email Log too. The
                // helper text under the button even admitted it ("this will not send again"), which
                // made the control say two opposite things at once.
                //
                // 🔑 FORCE ONLY WHEN THIS REALLY IS A RE-SEND. A first send must keep the ledger
                // guard, so the ordinary path stays idempotent and an import cannot double-welcome.
                var alreadyWelcomed = await WelcomeSentAsync(p, ct);
                var sent = await _welcome.SendWelcomeAsync(p.Id, ct, force: alreadyWelcomed);

                if (sent)
                {
                    Message = alreadyWelcomed ? "Welcome email re-sent." : "Welcome email sent.";
                }
                else
                {
                    // ⚠️ Do NOT report this as "already sent" — with force on, that is no longer what
                    // a false means. §234: a ring-dropped or kill-switched send returns false ON
                    // PURPOSE so the ledger is not written and a later reconcile can retry. Naming
                    // the real candidates beats a cheerful message that is wrong, and the Email Log
                    // carries the recorded reason (§938 writes a Dropped row, not a failure).
                    Error = "Nothing was sent. The usual reasons are that this person is outside the "
                          + "released email rings, the email kill-switch is on, or this role does not "
                          + "use the generic welcome (attendees get the Master Class invite instead). "
                          + "Check the Email Log — a ring-drop is recorded there with its reason.";
                }
            }
            catch (Exception ex)
            {
                Error = $"Could not send the welcome email: {ex.Message}";
            }
        }

        // Re-hydrate the form.
        IsNew = false;
        Email = p.Email; FullName = p.FullName; Phone = p.Phone;
        Role = p.Role; IsActive = p.IsActive; SponsorCompanyId = p.SponsorCompanyId;
        WelcomeAlreadySent = await WelcomeSentAsync(p, ct);
        return Page();
    }

    /// <summary>
    /// §236 (operator 2026-07-07): RESET the welcome so it sends AGAIN — clears the
    /// once-ever SentReminder ledger row(s) and the provisioning stamp
    /// (<see cref="Participant.WelcomeWithLoginSentAt"/>), then immediately re-sends the
    /// role-appropriate welcome. Built for the operator's role-simulation testing on his
    /// Ring-1 accounts: reset → the fresh welcome (with its Get-Started magic link)
    /// arrives again, repeatably. Normal ring/kill-switch gating still applies to the send.
    /// </summary>
    public async Task<IActionResult> OnPostResetWelcomeAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var p = Id is null ? null
            : await _db.Participants.FirstOrDefaultAsync(x => x.Id == Id && x.EventId == me.EventId, ct);
        if (p is null) { Error = "Participant not found."; IsNew = true; return Page(); }

        if (!p.IsActive)
        {
            Error = "Cannot reset/send a welcome for a deactivated participant.";
        }
        else
        {
            // 1. RESET — make the welcome sendable again.
            var ledger = await WelcomeLedgerRows(p).ToListAsync(ct);
            _db.SentReminders.RemoveRange(ledger);
            p.WelcomeWithLoginSentAt = null;
            await _db.SaveChangesAsync(ct);

            // 2. RE-SEND immediately (role-appropriate path).
            try
            {
                bool sent;
                if (p.Role == ParticipantRole.Attendee)
                {
                    sent = await _attendeeWelcome.SendForProvisioningAsync(p.Id, ct);
                }
                else
                {
                    sent = await _welcome.SendWelcomeAsync(p.Id, ct);
                }
                Message = sent
                    ? "Welcome reset — a fresh welcome email is on its way."
                    : "Welcome reset — the immediate re-send was refused (no welcome for this role, or "
                      + "gated by ring/kill-switch); the reconcile job will also retry on its next run.";
            }
            catch (Exception ex)
            {
                Error = $"Welcome was reset, but the re-send failed: {ex.Message}";
            }
        }

        // Re-hydrate the form.
        IsNew = false;
        Email = p.Email; FullName = p.FullName; Phone = p.Phone;
        Role = p.Role; IsActive = p.IsActive; SponsorCompanyId = p.SponsorCompanyId;
        WelcomeAlreadySent = await WelcomeSentAsync(p, ct);
        return Page();
    }

    /// <summary>The §355 step checkboxes the organizer ticked.</summary>
    [BindProperty] public List<string> ResetSteps { get; set; } = new();

    /// <summary>
    /// §355 — reset this participant's GET STARTED, per step or in full (operator 2026-07-26:
    /// <i>"should we have a similar organizer interface for this reset functionality, so i can
    /// reset both per step + full reset"</i>). The attendee equivalent lives on
    /// <c>/Organizer/Attendees</c>; this is the speaker/volunteer/media side.
    ///
    /// <para>Guarded on <see cref="OrganizerAuth.IsRealOrganizer"/> (§337) — it deletes another
    /// person's answers and re-opens their tasks, so an ACTING-AS session must not be able to run
    /// it — and audited, because "who reset this and what did it clear" is exactly the question
    /// asked afterwards.</para>
    /// </summary>
    [CommunityHub.Audit.Audit("Reset participant Get Started",
        Category = AuditCategory.Admin, TargetType = nameof(Participant))]
    public async Task<IActionResult> OnPostResetOnboardingAsync(
        [FromServices] CommunityHub.Core.Organizer.ParticipantOnboardingResetService reset,
        CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var p = Id is null ? null
            : await _db.Participants.FirstOrDefaultAsync(x => x.Id == Id && x.EventId == me.EventId, ct);
        if (p is null) { Error = "Participant not found."; IsNew = true; return Page(); }

        var result = await reset.ResetAsync(me.EventId, p.Id, ResetSteps, ct);
        if (!result.Ok)
        {
            Error = result.Detail;
        }
        else
        {
            Message = $"Get Started reset ({string.Join(", ", result.StepsReset)}) — "
                      + $"{result.RowsRemoved} answer row(s) removed, {result.TasksReopened} task(s) re-opened.";
        }

        // Re-hydrate the form.
        IsNew = false;
        Email = p.Email; FullName = p.FullName; Phone = p.Phone;
        Role = p.Role; IsActive = p.IsActive; SponsorCompanyId = p.SponsorCompanyId;
        WelcomeAlreadySent = await WelcomeSentAsync(p, ct);
        return Page();
    }

    /// <summary>
    /// §340-G-1 — this participant's <c>welcome</c> ledger rows, found by ADDRESS **or** by the
    /// occasion key the send actually writes.
    ///
    /// <para>§326bf moved <c>WelcomeEmailService</c>'s idempotency to <c>welcome:{participantId}</c>
    /// because an address is not an identity: correcting a typo, a re-import that changes the case,
    /// or a Sessionize update leaves the stored row under the OLD address. Six sites on this page
    /// still looked that row up by address alone, and they failed in BOTH directions — the reset
    /// deleted nothing while reporting success, and the "welcome already sent" badge told the
    /// organizer it had never gone out, which is precisely the prompt to send it a second time.</para>
    ///
    /// <para>Written once and used by every site, so the badge and the reset can no longer disagree
    /// about whether a welcome exists.</para>
    /// </summary>
    private IQueryable<Core.Domain.SentReminder> WelcomeLedgerRows(Core.Domain.Participant p)
    {
        var occasionKey = $"welcome:{p.Id}";
        return _db.SentReminders
            .Where(s => s.EventId == p.EventId
                        && s.ReminderType == "welcome"
                        && (s.RecipientEmail == p.Email || s.OccasionKey == occasionKey));
    }

    /// <inheritdoc cref="WelcomeLedgerRows"/>
    private Task<bool> WelcomeSentAsync(Core.Domain.Participant p, CancellationToken ct) =>
        WelcomeLedgerRows(p).AnyAsync(ct);
}
