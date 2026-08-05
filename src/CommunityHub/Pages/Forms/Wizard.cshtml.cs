using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Forms;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Forms;

/// <summary>
/// The generic in-wizard STEPPER host (REQUIREMENTS §148). A true one-step-at-a-time
/// wizard: it asks the role's existing wizard SERVICE for the ordered, entitlement-gated,
/// done-marked step plan (<see cref="SpeakerWizardService"/> for speakers, otherwise
/// <see cref="RoleWizardService"/>), picks the current step (from <c>?step=</c> or the first
/// incomplete one), and renders THAT step's fields INLINE — the step's own
/// <see cref="IWizardStepHandler"/> loads its model and the host drops the handler's
/// fields-partial inside ONE <c>&lt;form method="post"&gt;</c> with a progress bar +
/// Previous / Save&amp;next / Finish.
///
/// <para>On POST the plan is REBUILT (stateless — re-reads data, so refresh / re-entry is
/// always correct), then the step's handler runs the form's REAL validate + persist +
/// side-effects via its shared service: <see cref="WizardStepOutcome.Advance"/> → PRG to the
/// next incomplete step (or the hub when done); <see cref="WizardStepOutcome.Invalid"/> →
/// re-render the SAME step with field errors; <see cref="WizardStepOutcome.NotRelevant"/> →
/// skip forward. The host owns NO form logic.</para>
///
/// <para>Sponsors keep their bespoke <c>/Sponsor/GetStarted</c> (their steps embed sections of
/// the single Company Details page and share files), so they are redirected out of here.</para>
/// </summary>
[Authorize]
public class WizardModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly SpeakerWizardService _speaker;
    private readonly RoleWizardService _role;
    private readonly SponsorWizardService? _sponsor;
    private readonly AttendeeWizardService? _attendee;   // §326ak
    private readonly Core.Reminders.PartyRsvpService? _partyRsvp;
    private readonly Core.Email.CalendarInviteEmailService? _calendarInvite;
    private readonly ILogger<WizardModel>? _log;
    private readonly Core.Audit.IAuditTrail? _audit;   // §728
    private readonly Core.Reminders.GetStartedCompletionNotifier? _completionNotice;   // §720
    private readonly Dictionary<string, IWizardStepHandler> _handlers;

    public WizardModel(
        ICurrentParticipantAccessor participant,
        SpeakerWizardService speaker,
        RoleWizardService role,
        IEnumerable<IWizardStepHandler> handlers,
        // Optional + last so non-sponsor unit tests need not construct its heavy deps; the DI
        // container always injects the registered service in production (§285).
        SponsorWizardService? sponsor = null,
        // §316: the party step's "e-mail me a calendar invite" (same optional-DI pattern).
        Core.Reminders.PartyRsvpService? partyRsvp = null,
        Core.Email.CalendarInviteEmailService? calendarInvite = null,
        ILogger<WizardModel>? log = null,
        // §326ak (operator 2026-07-25 BUG: "get started is empty" as a 2-day attendee):
        // attendees DO have a wizard (§207: Master Class + Party) — this host just never
        // asked for it. Optional + last so existing unit tests keep compiling.
        AttendeeWizardService? attendee = null,
        // §728 — optional + last, so every existing unit test that constructs this page model keeps
        // compiling and simply records nothing.
        Core.Audit.IAuditTrail? audit = null,
        // §720 — the "someone finished Get Started" notice. Optional for the same reason.
        Core.Reminders.GetStartedCompletionNotifier? completionNotice = null)
    {
        _completionNotice = completionNotice;
        _participant = participant;
        _speaker = speaker;
        _role = role;
        _sponsor = sponsor;
        _attendee = attendee;
        _partyRsvp = partyRsvp;
        _calendarInvite = calendarInvite;
        _log = log;
        _audit = audit;

        // Key every discovered handler by its stable Key. In DEBUG a duplicate key is a
        // wiring bug (two handlers claim the same step) — fail loudly; in release last wins.
        _handlers = new(StringComparer.Ordinal);
        foreach (var h in handlers)
        {
#if DEBUG
            if (_handlers.ContainsKey(h.Key))
                throw new InvalidOperationException(
                    $"Duplicate IWizardStepHandler.Key '{h.Key}' ({h.GetType().FullName}). Each wizard step key must be unique.");
#endif
            _handlers[h.Key] = h;
        }
    }

    /// <summary>One normalized step (across the speaker / role wizard view shapes).</summary>
    public sealed record PlanStep(string Key, string Route, bool Done);

    /// <summary>The normalized, ordered, gated, done-marked plan the host renders against.</summary>
    public sealed class WizardPlan
    {
        public required string ResxPrefix { get; init; }   // "SpeakerWiz" | "RoleWiz" → Step.<key> labels
        public required IReadOnlyList<PlanStep> Steps { get; init; }
        public int EntitledCount => Steps.Count;
        public int DoneCount => Steps.Count(s => s.Done);
        public int Percent => EntitledCount == 0 ? 0 : (int)Math.Round(100.0 * DoneCount / EntitledCount);
        public bool AllDone => EntitledCount > 0 && DoneCount >= EntitledCount;
        public PlanStep? NextStep => Steps.FirstOrDefault(s => !s.Done);
        public int IndexOf(string key)
        {
            for (var i = 0; i < Steps.Count; i++)
                if (string.Equals(Steps[i].Key, key, StringComparison.Ordinal)) return i;
            return -1;
        }
    }

    // ----- render state (populated by OnGet / OnPost-invalid) -------------
    public bool AccessDenied { get; private set; }
    public bool NothingToDo { get; private set; }
    public bool ShowAllDone { get; private set; }
    /// <summary>§297: every step is already done, but we still render the editable wizard (landed on
    /// the first step) so the person can review/change any answer — never a dead-end at 100%.</summary>
    public bool AllComplete { get; private set; }
    public WizardPlan? Plan { get; private set; }
    public int CurrentIndex { get; private set; }
    public string FullName { get; private set; } = string.Empty;

    public PlanStep? CurrentStep => Plan is not null && CurrentIndex >= 0 && CurrentIndex < Plan.Steps.Count
        ? Plan.Steps[CurrentIndex] : null;

    /// <summary>The handler for the current step, or null when no inline handler is registered yet
    /// (during rollout): the view then falls back to a link to the step's standalone page.</summary>
    public IWizardStepHandler? CurrentHandler { get; private set; }

    /// <summary>1-based position of the current step (for "Step X of N").</summary>
    public int CurrentStepNumber => CurrentIndex + 1;
    public bool IsFirstStep => CurrentIndex <= 0;
    public bool IsLastStep => Plan is not null && CurrentIndex == Plan.Steps.Count - 1;

    public async Task<IActionResult> OnGetAsync(string? step, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        // §285: sponsors now run through the SAME inline wizard as every other role (their
        // plan comes from SponsorWizardService in BuildPlanAsync). No more redirect to the
        // card-stepper / Company Details deep-links.
        FullName = me.FullName;
        Plan = await BuildPlanAsync(me, ct);
        // A null plan means the role simply has no generic wizard (e.g. Attendee) — that is NOT a
        // denial, so show the neutral "nothing to set up" copy, not the speaker-only access-denied
        // string (which misleadingly told attendees they "do not have a speaker profile").
        if (Plan is null) { NothingToDo = true; return Page(); }
        if (Plan.EntitledCount == 0) { NothingToDo = true; return Page(); }

        // Current step: an in-plan ?step= wins (so completed steps are revisitable);
        // otherwise the first incomplete step; otherwise everything is done.
        var idx = !string.IsNullOrEmpty(step) ? Plan.IndexOf(step) : -1;
        if (idx < 0) idx = Plan.NextStep is { } n ? Plan.IndexOf(n.Key) : -1;
        if (idx < 0)
        {
            // §297: all steps complete. DON'T dead-end on a "go to hub" card — it was very confusing
            // how to edit once you hit 100%. Land on the FIRST step (fully editable) with the step
            // rail + Prev/Next so ANY answer can be reviewed/changed, plus an "all done" banner.
            AllComplete = true;
            idx = 0;
        }

        // §680 — a FIRST-RUN participant lands on the WELCOME step.
        //
        // 🔒 Without this the step would be built and never seen by anyone. The landing rule above
        // picks the first INCOMPLETE step, and the welcome is marked Done: true (it asks nothing,
        // so it must never hold the progress bar below 100% — the §400 deadlines precedent). At the
        // END of the plan those two facts sit together fine: you walk INTO the deadlines step in
        // sequence. At the START they cancel each other out, and "step 1" would be skipped straight
        // past on the very first visit — the one visit it exists for.
        //
        // "First run" = nothing has been answered yet: no step is Done except the informational ones
        // that are Done by construction. A returning participant who has completed even one real
        // step resumes where they left off and is not re-welcomed; they can still reach the step any
        // time from the rail. An explicit ?step= always wins — this only decides where an unqualified
        // /Forms/Wizard lands.
        if (string.IsNullOrEmpty(step) && IsFirstRun(Plan)) idx = 0;

        CurrentIndex = idx;
        CurrentHandler = ResolveHandler(Plan.Steps[idx].Key);
        if (CurrentHandler is not null)
            await CurrentHandler.LoadAsync(BuildContext(me, ct));

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        FullName = me.FullName;

        // Stateless: rebuild the plan from current data so re-entry / refresh is always correct.
        Plan = await BuildPlanAsync(me, ct);
        // Null plan = role has no generic wizard (not a denial) → neutral "nothing to do" copy.
        if (Plan is null) { NothingToDo = true; return Page(); }
        if (Plan.EntitledCount == 0) return RedirectToPage("/Index");   // nothing left to do

        var stepKey = Request.Form["__step"].ToString();
        var dir = Request.Form["__dir"].ToString();
        var idx = string.IsNullOrEmpty(stepKey) ? -1 : Plan.IndexOf(stepKey);

        // Unknown / stale step → restart at the first incomplete step (or hub).
        if (idx < 0) return RedirectToStepOrHub(Plan.NextStep);

        // Previous: no save, just move back one step (completed steps stay revisitable).
        if (dir == "prev")
            return RedirectToStep(Plan.Steps[Math.Max(0, idx - 1)].Key);

        // Next / Save.
        var handler = ResolveHandler(stepKey);
        if (handler is null)
            // No inline handler yet (rollout) → behave as "skip for now": advance.
            return AdvanceFrom(idx);

        var outcome = await handler.SaveAsync(BuildContext(me, ct));

        // §393 — SAVE & EXIT (operator 2026-07-26: "when i hit for example master class selection in
        // the main menu, it take me to step 1, then step 2 then step 3 (party). it seems not
        // relevant to go through all steps … otherwise we should have a save button next to the
        // Save & Next").
        //
        // Deliberately NOT solved by trimming steps from the plan: the wizard is also the genuine
        // first-run onboarding, where Party DOES belong. The problem is only that arriving from a
        // MENU ITEM means you wanted one step, not the tour. So the step list is untouched and the
        // person gets a way out that still SAVES.
        //
        // Placed after SaveAsync and gated on a successful outcome, so "exit" can never be a way to
        // skip validation: an Invalid step falls through and re-renders with its errors exactly as
        // "next" does.
        if (dir == "exit" && outcome is WizardStepOutcome.Advance or WizardStepOutcome.NotRelevant)
        {
            return RedirectToPage("/Index");
        }

        // 🔒 §728 — NAME what the person just did. Operator 2026-07-31: *"i would like also to see
        // things like selected master class, cancelled master class, signed up for waitlist, party
        // sign-up, etc"*.
        //
        // Every wizard step posts to THIS one handler, so the auto-captured trail recorded 302 rows
        // all reading `POST /Forms/Wizard` — choosing a Master Class, joining a waitlist, the party
        // RSVP, the profile and the Code of Conduct, indistinguishable. The step key is only known
        // at RUNTIME, so an [Audit] attribute cannot express it; the row has to be written here.
        //
        // Recorded only on a SUCCESSFUL save: an Invalid outcome is a failed attempt, and the
        // generic capture still records it (the suppression flag is set inside RecordStepAsync,
        // after the write, so a throw leaves the safety net in place).
        if (outcome is WizardStepOutcome.Advance or WizardStepOutcome.NotRelevant)
        {
            await RecordStepAuditAsync(me, stepKey, outcome, ct);

            // §720 (operator 2026-07-31: *"Fire at 100%"*) — tell the operator the moment someone
            // finishes, so he can ask them about the experience while it is fresh.
            //
            // 🔒 The plan is REBUILT here on purpose. `Plan` above was computed BEFORE the handler
            // saved, so it still shows the step that was just completed as outstanding — using it
            // would mean the notice never fires on the save that actually finishes the wizard, and
            // then fires on the NEXT unrelated visit instead. The wizard services are stateless
            // reads, so rebuilding is the cheap, correct way to ask "are they done NOW?".
            if (_completionNotice is not null)
            {
                var after = await BuildPlanAsync(me, ct);
                if (after is { AllDone: true })
                {
                    await _completionNotice.NotifyIfNewlyCompleteAsync(
                        me.EventId, me.ParticipantId, allDone: true, ct);
                }
            }
        }

        // 🔑 §783.4/§783.5 — SAVE AND STAY: the step's own action button.
        //
        // Operator 2026-08-03 on the Logos step: *"i am missing an upload button … problem is that
        // the system hangs for 6 seconds when you click next so it is not user friendly. I prefer a
        // buttn and it must show that it is uploading"*; and on Booth materials: *"i nded a button to
        // SET booth videos URLs and a button to upload the booth collateral"*.
        //
        // 🔒 It is a DIRECTION, not a new handler. The wizard host owns the single form (§304b — a
        // nested form would not post at all), and every step already saves through the one code path
        // above with its validation, its §728 audit row and its completion notice. A second upload
        // route would be a second place for those to be forgotten. So the button reuses all of it and
        // only changes where it LANDS — back on this step, with the work visibly done, instead of
        // silently three seconds into the next one. §473 previously answered this same complaint with
        // a paragraph explaining that no button exists; that explained the surprise without removing
        // it.
        if (dir == "stay" && outcome is WizardStepOutcome.Advance or WizardStepOutcome.NotRelevant)
        {
            TempData["WizardStepSaved"] = Request.Form["__stayMsg"].ToString() is { Length: > 0 } m
                ? m
                : "Saved.";
            return RedirectToStep(stepKey);
        }

        switch (outcome)
        {
            case WizardStepOutcome.Advance:
                // Move FORWARD to the next step IN SEQUENCE — NOT the first-incomplete step.
                // The first-incomplete can be THIS step again when "done" needs more than this
                // save provides (e.g. Profile is "done" only once a phone is present, yet you
                // may Save & next without one) — which made Save & next silently loop back to
                // the same step ("nothing happens"). Completed/skipped steps stay revisitable
                // via the breadcrumb; the last step's Finish lands on the hub.
                return AdvanceFrom(idx);

            case WizardStepOutcome.NotRelevant:
                return AdvanceFrom(idx);

            case WizardStepOutcome.Invalid:
            default:
                // Re-render the SAME step inline with the posted values + ModelState errors.
                CurrentIndex = idx;
                CurrentHandler = handler;   // Model already holds the posted values from SaveAsync
                return Page();
        }
    }

    /// <summary>
    /// §316: the party step's "e-mail me a calendar invite" — the SAME §193/§206 invite the
    /// standalone /Party page sends (stable UID ⇒ re-send updates the same entry; honors the
    /// calendar/override e-mail), but returned to the wizard's party step so the flow is not
    /// interrupted. Fail-soft with a flash message.
    /// </summary>
    public async Task<IActionResult> OnPostPartyInviteAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (_partyRsvp is null || _calendarInvite is null) return RedirectToPage(new { step = "party" });

        var party = await _partyRsvp.GetActivePartyAsync(ct);
        if (party is null) return RedirectToPage(new { step = "party" });

        var (startUtc, endUtc) = Core.Reminders.PartyRsvpService.WindowUtc(party);
        try
        {
            // §252: alongside the attached .ics, the mail offers open-in-browser links
            // (Google + Outlook compose) — same content as the /Party page's invite.
            var summary = $"{party.EventName} — Party";
            var details = $"Join us for the {party.EventName} party — {party.Location}.";
            var googleUrl = System.Net.WebUtility.HtmlEncode(Core.Email.CalendarLinkBuilder.GoogleUrl(
                summary, startUtc, endUtc, details, party.Location));
            var outlookUrl = System.Net.WebUtility.HtmlEncode(Core.Email.CalendarLinkBuilder.OutlookUrl(
                summary, startUtc, endUtc, details, party.Location));
            var sent = await _calendarInvite.SendItemInviteAsync(
                me.ParticipantId,
                uid: $"party-{party.EventId}@eventhub",
                summary: summary,
                description: details,
                location: party.Location,
                start: startUtc,
                end: endUtc,
                allDay: false,
                fileName: "party.ics",
                introHtml: "Here is your calendar invitation for the party. Prefer to add it online? "
                    + $"Open it in <a href=\"{googleUrl}\">Google Calendar</a> or "
                    + $"<a href=\"{outlookUrl}\">Outlook</a>.",
                ct: ct);
            TempData["PartyInviteMessage"] = sent
                ? sent.Confirmation()
                : "Calendar invitations are turned off for this event.";
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Wizard party calendar invite failed for participant {Pid}.", me.ParticipantId);
            TempData["PartyInviteMessage"] = "We couldn't send the invite just now — please try again later.";
        }
        return RedirectToPage(new { step = "party" });
    }

    /// <summary>
    /// §370 — give up the confirmed Master Class seat from the inline wizard step.
    ///
    /// <para>The control used to live only on <c>/Attendee</c>, which §365 removed from the nav —
    /// leaving an attendee no reachable way to cancel. Delegates to the SAME
    /// <c>MasterClassSignupService.RemoveAsync</c> the old page used, so the freed seat still
    /// promotes the next person on the waitlist exactly as before.</para>
    /// </summary>
    /// <summary>
    /// §734 — remove ONE speaker from the sponsor's session (operator 2026-07-31:
    /// <i>"need option to remove a speaker here"</i> … <i>"remove from sesison only"</i>).
    /// </summary>
    /// <remarks>
    /// Sponsor-only by construction: the service resolves the session from the ACTOR's own
    /// <c>SponsorCompanyId</c>, so a posted e-mail can only ever remove a speaker from the caller's
    /// own session — there is no session id on the wire to tamper with.
    /// </remarks>
    [CommunityHub.Audit.Audit("Removed a speaker from the sponsor session",
        Action = "speaker.remove", TargetType = "SponsorSessionSpeaker")]
    public async Task<IActionResult> OnPostRemoveSpeakerAsync(
        string speakerEmail,
        [FromServices] CommunityHub.Forms.Steps.SponsorSessionFormService sessions,
        CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Sponsor) return Forbid();

        var removed = await sessions.RemoveSpeakerAsync(me.EventId, me.ParticipantId, speakerEmail, ct);
        TempData["SponsorSessionMessage"] = removed is null
            ? "That speaker was not found on your session."
            : $"{removed} was removed from your session. We have told the organizers so Zoho can be "
              + "tidied up.";

        return RedirectToPage(new { step = "session" });
    }

    /// <summary>
    /// §783.3 — remove one of the sponsor's e-conomic contacts from the Contacts step.
    /// </summary>
    /// <remarks>
    /// Operator 2026-08-03: <i>"I am missing ability to remove contacts in the get started. I can
    /// only ADD"</i>. Sponsor-only by construction: the service resolves the e-conomic customer from
    /// the ACTOR's own <c>SponsorCompanyId</c>, so a posted contact number can only ever delete from
    /// the caller's own record. The service refuses the LAST event coordinator and says why.
    /// </remarks>
    [CommunityHub.Audit.Audit("Removed a sponsor contact",
        Action = "sponsor.contact.remove", TargetType = "SponsorContact")]
    public async Task<IActionResult> OnPostRemoveContactAsync(
        int contactNumber,
        [FromServices] CommunityHub.Forms.Steps.SponsorContactsFormService contacts,
        CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Sponsor) return Forbid();

        TempData["SponsorContactsMessage"] =
            await contacts.RemoveContactAsync(me.ParticipantId, contactNumber, ct);

        return RedirectToPage(new { step = "contacts" });
    }

    // §728 — this one already stood out in the trail (it has its own handler, so it read
    // `POST /Forms/Wizard [MasterClassGiveUp]`). Naming it gives it a STABLE code an organizer can
    // filter on, matching the wizard-step rows beside it.
    [CommunityHub.Audit.Audit("Gave up their Master Class seat",
        Action = Core.Audit.AuditActions.MasterClassCancel, TargetType = "MasterClass")]
    public async Task<IActionResult> OnPostMasterClassGiveUpAsync(
        [FromServices] Core.Reminders.MasterClassSignupService signups,
        [FromServices] Core.Email.MasterClassEmailService mcEmail,
        [FromServices] Core.Email.MasterClassPromotionEmailService promo,
        CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        var attendee = await signups.ResolveByEmailAsync(me.EventId, me.Email, ct);
        if (attendee is null) return RedirectToPage(new { step = "masterclass" });

        var mine = await signups.GetForAttendeeAsync(me.EventId, attendee.Id, ct);
        var confirmed = mine.FirstOrDefault(
            s => s.Status == Core.Domain.MasterClassSignupStatus.Confirmed);
        if (confirmed is null) return RedirectToPage(new { step = "masterclass" });

        // §387 (operator 2026-07-26: "i still did NOT get any email when i was moved up and got a
        // seat from the waitlist"). RemoveAsync RETURNS the promotion it caused — and this handler
        // used to THROW THAT RETURN VALUE AWAY. The seat moved in the database, which is why the
        // promotion looked like it worked, but the person who got it was never told.
        //
        // Every other give-up surface (/Attendee, /Attendee/Waitlist, /MyMasterClass) sends this
        // mail; the wizard step did not — and §370 had just made the wizard THE place attendees
        // cancel from, so the one surface that was missing it became the only one being used.
        var promotion = await signups.RemoveAsync(me.EventId, attendee.Id, confirmed.SessionId, ct);

        if (promotion?.PromotedSignupId is int promotedId)
        {
            // ONE mail, not two (operator: "I dont need to have 2 emails"): the promotion mail
            // itself names the released seat via §386's ReleasedTitle, so the promoted attendee gets
            // "you moved up AND your old seat was released" in a single message.
            try { await promo.SendPromotionAsync(promotedId, BaseUrlFor(), ct, promotion.ReleasedTitle); }
            catch { /* the promotion stands even if the mail fails; the job retries */ }
        }

        try
        {
            // …and the person who GAVE UP the seat gets their own cancellation confirmation.
            await mcEmail.SendCancelledAsync(
                me.EventId, attendee.Email, attendee.FirstName, attendee.LastName,
                confirmed.Title, BaseUrlFor(), attendee.Id, ct);
        }
        catch { /* the cancellation stands even if the mail fails */ }

        TempData["MasterClassInviteMessage"] =
            "Your Master Class seat was given up. You can choose another one below while seats last.";
        return RedirectToPage(new { step = "masterclass" });
    }

    /// <summary>
    /// §400 — e-mail a calendar invitation for ONE dated task from the deadlines step.
    ///
    /// <para>Reuses the SAME <c>task-{id}@{host}</c> UID the hub's "Send Reminder to My Calendar"
    /// button uses, so pressing either one updates the same calendar entry instead of creating a
    /// second copy of the same deadline.</para>
    /// </summary>
    public async Task<IActionResult> OnPostTaskReminderAsync(
        int taskId,
        [FromServices] Core.Email.CalendarInviteEmailService calendar,
        [FromServices] CommunityHub.Forms.Steps.DeadlinesFormService deadlines,
        CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        var sent = await SendTaskInviteAsync(calendar, deadlines, me, taskId, ct);
        TempData["CalendarInviteMessage"] = sent
            ? sent.Confirmation("Reminder")
            : "We couldn't send that reminder just now — please try again.";

        return RedirectToPage(new { step = "deadlines" });
    }

    /// <summary>
    /// §400 — one button for every dated, still-open task (operator: <i>"Send Calendar invites for
    /// all tasks (one button)"</i>). Each is a separate invitation with its own stable UID, so a
    /// re-press updates rather than duplicates.
    /// </summary>
    public async Task<IActionResult> OnPostAllTaskRemindersAsync(
        [FromServices] Core.Email.CalendarInviteEmailService calendar,
        [FromServices] CommunityHub.Forms.Steps.DeadlinesFormService deadlines,
        CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        var ids = await deadlines.DatedPendingTaskIdsAsync(me.EventId, me.ParticipantId, ct);
        var sent = 0;
        foreach (var id in ids)
        {
            // One failure must not abandon the rest of the list.
            if (await SendTaskInviteAsync(calendar, deadlines, me, id, ct)) sent++;
        }

        TempData["CalendarInviteMessage"] = ids.Count == 0
            ? "You have no dated deadlines to add yet."
            : sent == ids.Count
                ? $"Sent {sent} calendar invitation(s) — check your inbox."
                : $"Sent {sent} of {ids.Count} invitations; please try the rest again shortly.";

        return RedirectToPage(new { step = "deadlines" });
    }

    /// <summary>Shared by both handlers so the single and the bulk path cannot diverge.</summary>
    private async Task<Core.Email.CalendarInviteResult> SendTaskInviteAsync(
        Core.Email.CalendarInviteEmailService calendar,
        CommunityHub.Forms.Steps.DeadlinesFormService deadlines,
        CurrentParticipant me, int taskId, CancellationToken ct)
    {
        // Resolved through the step's own service, which applies the checklist's visibility rule —
        // so this can never invite someone to a task that is not theirs, and never refuse one the
        // step just listed.
        var task = await deadlines.DatedTaskAsync(me.EventId, me.ParticipantId, taskId, ct);
        if (task is null) return Core.Email.CalendarInviteResult.NotSent;

        var start = new DateTimeOffset(task.DueDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var description = string.IsNullOrWhiteSpace(task.Description)
            ? "Deadline from your Event Hub. Open the hub to update this item."
            : Core.Email.TaskMarkup.ToPlainText(task.Description);

        try
        {
            return await calendar.SendItemInviteAsync(
                me.ParticipantId,
                uid: $"task-{task.Id}@{Request.Host.Host}",
                summary: task.Title,
                description: description,
                location: null,
                start: start,
                end: start.AddDays(1),
                allDay: true,
                fileName: "reminder.ics",
                introHtml: $"Here is a reminder for <strong>{System.Net.WebUtility.HtmlEncode(task.Title)}</strong>, due {task.DueDate:d MMM yyyy}.",
                ct: ct);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Wizard task reminder failed for task {TaskId}.", taskId);
            return Core.Email.CalendarInviteResult.NotSent;
        }
    }

    private string BaseUrlFor() => $"{Request.Scheme}://{Request.Host}";

    /// <summary>
    /// §352 — e-mail the attendee a calendar invitation for their CONFIRMED Master Class from the
    /// inline wizard step (operator 2026-07-26 asked for the button on BOTH steps).
    ///
    /// <para>Reuses <c>MasterClassDayWindowUtc()</c> and <c>MasterClassInviteUid()</c> — the SAME
    /// window and UID the confirmation mail's invite uses — so the two can never disagree and a
    /// re-send UPDATES the existing calendar entry instead of duplicating it (§341-1).</para>
    /// </summary>
    public async Task<IActionResult> OnPostMasterClassInviteAsync(
        [FromServices] Core.Reminders.MasterClassSignupService signups,
        [FromServices] Core.Email.MasterClassEmailService mcEmail,
        CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (_calendarInvite is null) return RedirectToPage(new { step = "masterclass" });

        var attendee = await signups.ResolveByEmailAsync(me.EventId, me.Email, ct);
        if (attendee is null) return RedirectToPage(new { step = "masterclass" });

        var mine = await signups.GetForAttendeeAsync(me.EventId, attendee.Id, ct);
        var confirmed = mine.FirstOrDefault(
            s => s.Status == Core.Domain.MasterClassSignupStatus.Confirmed);
        if (confirmed is null)
        {
            TempData["MasterClassInviteMessage"] =
                "You need a confirmed Master Class seat before we can send the invite.";
            return RedirectToPage(new { step = "masterclass" });
        }

        try
        {
            var (startUtc, endUtc) = mcEmail.MasterClassDayWindowUtc();
            var sent = await _calendarInvite.SendItemInviteAsync(
                me.ParticipantId,
                uid: Core.Email.MasterClassEmailService.MasterClassInviteUid(
                    confirmed.SessionId, Request.Host.Host),
                summary: confirmed.Title,
                description: "Your Master Class. Registration & breakfast open at 07:00 — come "
                    + "early so we can check everyone in; the class itself runs 09:00–16:00.",
                location: string.Empty,
                start: startUtc,
                end: endUtc,
                allDay: false,
                fileName: "master-class.ics",
                introHtml: "Here is your calendar invitation for your Master Class.",
                ct: ct);

            TempData["MasterClassInviteMessage"] = sent
                ? sent.Confirmation()
                : "Calendar invitations are turned off for this event.";
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex,
                "Wizard Master Class calendar invite failed for participant {Pid}.", me.ParticipantId);
            TempData["MasterClassInviteMessage"] =
                "We couldn't send the invite just now — please try again later.";
        }
        return RedirectToPage(new { step = "masterclass" });
    }

    /// <summary>§322n: the hotel step's "Email me a calendar invite" — the standard §193
    /// invitation for the saved hotel dates; back to the hotel step with a flash.
    /// §424: saves the step first (see <see cref="SaveStepBeforeInviteAsync"/>).</summary>
    public async Task<IActionResult> OnPostHotelInviteAsync(
        [FromServices] CommunityHub.Forms.Steps.HotelFormService hotelForm, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        await SaveStepBeforeInviteAsync(me, "hotel", ct);
        var (_, message) = await hotelForm.SendInviteEmailAsync(me.EventId, me.ParticipantId, ct);
        TempData["HotelInviteMessage"] = message;
        return RedirectToPage(new { step = "hotel" });
    }

    /// <summary>§322n: the dinner step's "Email me a calendar invite" — the standard §193
    /// invitation for the saved RSVP=Yes; back to the dinner step with a flash.
    /// §424: saves the step first (see <see cref="SaveStepBeforeInviteAsync"/>).</summary>
    public async Task<IActionResult> OnPostDinnerInviteAsync(
        [FromServices] CommunityHub.Forms.Steps.DinnerFormService dinnerForm, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        await SaveStepBeforeInviteAsync(me, "dinner", ct);
        var (_, message) = await dinnerForm.SendInviteEmailAsync(me.EventId, me.ParticipantId, ct);
        TempData["DinnerInviteMessage"] = message;
        return RedirectToPage(new { step = "dinner" });
    }

    /// <summary>
    /// §779 — the Signal step's "send me the join links"; back to the Signal step with a flash.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>No save-first here, deliberately</b> — unlike the hotel and dinner invites above. The
    /// Signal step has NO posted fields (joining is external), so there is nothing a §424-style save
    /// could persist, and the links are read from config rather than from anything the user typed.
    /// </remarks>
    public async Task<IActionResult> OnPostMailSignalLinksAsync(
        [FromServices] CommunityHub.Forms.Steps.SignalFormService signalForm, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        var (_, message) = await signalForm.SendLinksEmailAsync(me.EventId, me.ParticipantId, me.Role, ct);
        TempData["SignalLinksMessage"] = message;
        return RedirectToPage(new { step = "signal" });
    }

    /// <summary>
    /// §424 — persist the step the invite button lives on, BEFORE sending the invite.
    ///
    /// <para><b>Why.</b> The operator reported the buttons missing entirely (2026-07-27: <i>"i am
    /// missing the button to send calendar invite for Appreciation Dinner + Hotel in the get
    /// started wizard for all roles if they choose yes"</i>). They existed, but rendered only when
    /// the LOADED model already said Yes — and a submit button with <c>asp-page-handler</c>
    /// bypasses <see cref="OnPostAsync"/> entirely, so the Yes you just clicked was never saved.
    /// Between those two facts the button was invisible until you had saved, navigated away, and
    /// come back; and had it been visible, pressing it would have sent nothing, because
    /// <c>SendInviteEmailAsync</c> re-derives from the database and would have found no RSVP.</para>
    ///
    /// <para>Saving first makes the button mean what it says the moment you press it. Failures are
    /// deliberately swallowed: the send that follows re-reads the database and reports honestly
    /// ("RSVP Yes and save first — then the invite has something to contain"), so a rejected save
    /// surfaces as an accurate message rather than a lost keystroke.</para>
    /// </summary>
    private async Task SaveStepBeforeInviteAsync(CurrentParticipant me, string stepKey, CancellationToken ct)
    {
        try
        {
            Plan = await BuildPlanAsync(me, ct);
            if (Plan is null || Plan.IndexOf(stepKey) < 0) return;

            var handler = ResolveHandler(stepKey);
            if (handler is not null) await handler.SaveAsync(BuildContext(me, ct));
        }
        catch
        {
            // Never let a save problem swallow the invite request itself — the send below
            // reads the persisted state and says what it actually found.
        }
        finally
        {
            // The invite handlers redirect, so nothing here should leak into a rendered page.
            ModelState.Clear();
        }
    }

    // ----- helpers --------------------------------------------------------

    /// <summary>
    /// §728 — write ONE named audit row for the step that was just saved, and suppress the generic
    /// <c>POST /Forms/Wizard</c> capture for this request.
    /// </summary>
    /// <remarks>
    /// <para>Best-effort by design: <c>IAuditTrail.RecordAsync</c> swallows its own write errors,
    /// and this method never throws into the save it is describing — an audit problem must not turn
    /// a successful step into a failed one.</para>
    ///
    /// <para>The SUMMARY is what he will actually read, so it names the step in the same words the
    /// wizard rail uses rather than the internal key. The KEY still goes in <c>TargetId</c>, which
    /// is what makes the trail filterable.</para>
    /// </remarks>
    private async Task RecordStepAuditAsync(
        CurrentParticipant me, string stepKey, WizardStepOutcome outcome, CancellationToken ct)
    {
        if (_audit is null) return;

        var label = StepLabel(stepKey);
        await _audit.RecordAsync(new Core.Domain.AuditEntry
        {
            EventId = me.EventId,
            OccurredUtc = DateTimeOffset.UtcNow,
            Category = Core.Domain.AuditCategory.UserAction,
            Action = Core.Audit.AuditActions.WizardStepSaved,
            ActorParticipantId = me.ParticipantId,
            ActorEmail = me.Email,
            ActorRole = me.Role.ToString(),
            TargetType = "WizardStep",
            TargetId = stepKey,
            Summary = outcome == WizardStepOutcome.NotRelevant
                ? $"Get Started — skipped “{label}” (not applicable)"
                : $"Get Started — saved “{label}”",
            Outcome = Core.Domain.AuditOutcome.Success,
            Source = Core.Domain.AuditSource.Web,
            HttpMethod = "POST",
            Path = Request.Path.Value,
        }, ct);

        // Set AFTER the write: if RecordAsync somehow does not run, the generic capture still
        // records the action rather than the request vanishing from the trail entirely.
        HttpContext.Items[CommunityHub.Audit.AuditPageFilter.SuppressGenericKey] = true;
    }

    /// <summary>
    /// §728 — the human name for a step key, matching what the wizard rail shows. Kept here rather
    /// than reading the resx: the audit trail is read by an organizer in one language, and a
    /// localized summary would make the SAME action read differently row to row.
    /// </summary>
    private static string StepLabel(string stepKey) => stepKey switch
    {
        "welcome"              => "Welcome",
        "masterclass"          => "Master Class selection",
        "masterclass-waitlist" => "Master Class waitlist",
        "party"                => "Party sign-up",
        "profile"              => "Your profile",
        "accept"               => "Code of Conduct & Privacy",
        "calendar"             => "Calendar e-mail",
        "details"              => "Speaker details",
        "availability"         => "Volunteer availability",
        "hotel"                => "Hotel",
        "dinner"               => "Appreciation dinner",
        "lunch"                => "Lunch",
        "swag"                 => "Swag & gift",
        "travel"               => "Travel reimbursement",
        "signal"               => "Signal groups",
        "deadlines"            => "Tasks & deadlines",
        "company"              => "Company details",
        "contacts"             => "Company contacts",
        "logos"                => "Logos & artwork",
        "booth-materials"      => "Booth materials",
        "booth-checkin"        => "Booth check-in",
        _                      => stepKey,
    };

    private WizardStepContext BuildContext(CurrentParticipant me, CancellationToken ct) =>
        new(me.EventId, me.ParticipantId, me.Role, me.Email, me.FullName, this,
            // The host owns binding: run model binding for THIS request (flat, no prefix) into
            // the handler's concrete form model. Uses the public non-generic overload so handlers
            // never need to derive from PageModel.
            model => TryUpdateModelAsync(model, model.GetType(), name: string.Empty),
            ct);

    /// <summary>
    /// §680 — steps that are INFORMATIONAL: they ask nothing, store nothing, and are therefore
    /// marked <c>Done: true</c> by every wizard service. They are not evidence that the
    /// participant has answered anything, which is what <see cref="IsFirstRun"/> needs to know.
    /// </summary>
    private static readonly HashSet<string> InformationalSteps =
        new(StringComparer.Ordinal) { Core.Content.WelcomeCopyStore.StepKey, "deadlines" };

    /// <summary>
    /// Is this the participant's first run — the welcome step is step 1 and nothing REAL has been
    /// completed yet? Static and plan-only so it is reachable from a unit test; the rule that
    /// decides whether anybody ever sees the welcome must not live only in a page's control flow.
    /// </summary>
    public static bool IsFirstRun(WizardPlan? plan) =>
        plan is { Steps.Count: > 0 }
        && plan.Steps[0].Key == Core.Content.WelcomeCopyStore.StepKey
        && !plan.Steps.Any(s => s.Done && !InformationalSteps.Contains(s.Key));

    private IWizardStepHandler? ResolveHandler(string key) =>
        _handlers.TryGetValue(key, out var h) ? h : null;

    private IActionResult AdvanceFrom(int idx)
    {
        var next = Plan!.Steps.Skip(idx + 1).FirstOrDefault();
        return next is null ? RedirectToPage("/Index") : RedirectToStep(next.Key);
    }

    private IActionResult RedirectToStep(string key) => RedirectToPage(new { step = key });

    // §285: a sponsor step's section link (used by the handler-less fallback in the view) — the
    // party step opens /Party (group reservation); every other step opens its section on the
    // shared Company Details page with the fragment so it lands at the right place.
    private static string SponsorStepRoute(string anchor) => anchor switch
    {
        "party" => "/Party",
        "deadlines" => "/Tasks",   // §400 — not a Company Details section; it IS the task list
        // §680 — the welcome has no Company Details section to deep-link to; it exists only
        // inside the wizard. Without this it would fall through to
        // "/Sponsor/CompanyDetails#welcome", an anchor that does not exist on that page.
        Core.Content.WelcomeCopyStore.StepKey => Core.Content.WelcomeCopyStore.StepRoute,
        _ => "/Sponsor/CompanyDetails#" + anchor,
    };

    private IActionResult RedirectToStepOrHub(PlanStep? step) =>
        step is null ? RedirectToPage("/Index") : RedirectToStep(step.Key);

    private async Task<WizardPlan?> BuildPlanAsync(CurrentParticipant me, CancellationToken ct)
    {
        if (me.Role == ParticipantRole.Sponsor)
        {
            // §285: sponsors run through the shared inline wizard. Steps with an inline handler
            // render their fields in-place; steps without one fall back to their Company-Details
            // section link (the party step → /Party). Onboarding-only steps per §286.
            if (_sponsor is null) return null;   // heavy service unavailable (unit-test path)
            var sv = await _sponsor.BuildAsync(me.EventId, me.ParticipantId, ct);
            if (sv is null) return null;
            return new WizardPlan
            {
                ResxPrefix = "SponsorWiz",
                Steps = sv.Steps.Select(s => new PlanStep(s.Key, SponsorStepRoute(s.Anchor), s.Done ?? false)).ToList(),
            };
        }

        if (me.Role == ParticipantRole.Speaker)
        {
            var v = await _speaker.BuildAsync(me.EventId, me.ParticipantId, ct);
            return new WizardPlan
            {
                ResxPrefix = "SpeakerWiz",
                Steps = v.Steps.Select(s => new PlanStep(s.Key, s.Route, s.Done)).ToList(),
            };
        }

        if (RoleWizardService.Handles(me.Role))
        {
            var v = await _role.BuildAsync(me.EventId, me.ParticipantId, ct);
            return new WizardPlan
            {
                ResxPrefix = "RoleWiz",
                Steps = v.Steps.Select(s => new PlanStep(s.Key, s.Route, s.Done)).ToList(),
            };
        }

        // §326ak (operator 2026-07-25 BUG — "get started is empty" as a 2-day attendee):
        // attendees DO have a wizard (§207/§208: Master Class + Party for a 2-day holder,
        // Party alone for 1-day). The nav sends EVERY role to /Forms/Wizard (§285), so
        // returning null here rendered "There are no get-started steps for you right now."
        // AttendeeWizardService returns a RoleWizardView, so it uses the RoleWiz labels.
        if (me.Role == ParticipantRole.Attendee && _attendee is not null)
        {
            var av = await _attendee.BuildAsync(me.EventId, me.ParticipantId, ct);
            return new WizardPlan
            {
                ResxPrefix = "RoleWiz",
                Steps = av.Steps.Select(s => new PlanStep(s.Key, s.Route, s.Done)).ToList(),
            };
        }

        return null;   // role genuinely has no inline wizard
    }
}
