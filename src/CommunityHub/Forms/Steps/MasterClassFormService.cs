using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace CommunityHub.Forms.Steps;

/// <summary>
/// §352 — the render/bind model for the INLINE Master Class selection wizard step.
/// </summary>
public sealed class MasterClassFormModel
{
    /// <summary>The session the attendee picked in this POST (0 = nothing chosen yet).</summary>
    public int SessionId { get; set; }

    /// <summary>
    /// The attendee's explicit consent to give up a held seat when choosing a class that is
    /// FULL (joining its waitlist). Mirrors the <c>/Attendee</c> page's consent checkbox —
    /// the service refuses the switch without it, so the step must carry it too.
    /// </summary>
    public bool AutoSwitchConsent { get; set; }

    // ---- render-only state (never bound from the POST) ----------------------

    /// <summary>True when the signed-in participant has no 2-day attendee row (step not applicable).</summary>
    public bool NotEligible { get; private set; }

    /// <summary>The choosable classes with live seat counts.</summary>
    public IReadOnlyList<MasterClassSignupService.McOption> Options { get; private set; }
        = Array.Empty<MasterClassSignupService.McOption>();

    /// <summary>The attendee's confirmed seat, if any.</summary>
    public MasterClassSignupService.MySignup? Confirmed { get; private set; }

    /// <summary>The attendee's waitlist place / held offer, if any.</summary>
    public MasterClassSignupService.MySignup? Pending { get; private set; }

    internal void Fill(
        bool notEligible,
        IReadOnlyList<MasterClassSignupService.McOption> options,
        MasterClassSignupService.MySignup? confirmed,
        MasterClassSignupService.MySignup? pending)
    {
        NotEligible = notEligible;
        Options = options;
        Confirmed = confirmed;
        Pending = pending;
    }
}

/// <summary>
/// §352 — load/save for the inline Master Class selection step.
///
/// <para><b>Delegates every decision to <see cref="MasterClassSignupService"/></b> — the SAME
/// service <c>/Attendee</c> uses. That is the property that makes this safe: capacity, waitlist
/// promotion, the one-confirmed-seat rule and the auto-switch consent all keep ONE
/// implementation, so the inline step and the standing page cannot drift. It is exactly why the
/// party step (<see cref="PartyFormService"/>) and <c>/Party</c> never diverged.</para>
///
/// <para>The step exists because a LINK-OUT step can never complete: the operator reported that
/// the old "Open: Select your Master Class" button navigated away from Get Started, so the
/// attendee never reached step 2.</para>
/// </summary>
public sealed class MasterClassFormService : IWizardFormService
{
    private readonly MasterClassSignupService _signups;
    private readonly CommunityHub.Core.Email.MasterClassEmailService _email;

    public MasterClassFormService(
        MasterClassSignupService signups,
        CommunityHub.Core.Email.MasterClassEmailService email)
    {
        _signups = signups;
        _email = email;
    }

    /// <summary>Resolve the attendee row for the signed-in participant's e-mail, or null.</summary>
    private Task<Attendee?> ResolveAsync(int eventId, string? email, CancellationToken ct) =>
        _signups.ResolveByEmailAsync(eventId, email, ct);

    public async Task<MasterClassFormModel> LoadAsync(
        int eventId, string? email, CancellationToken ct)
    {
        var model = new MasterClassFormModel();
        var a = await ResolveAsync(eventId, email, ct);
        if (a is null || a.TicketStatus != TicketStatus.TwoDay)
        {
            model.Fill(notEligible: true, Array.Empty<MasterClassSignupService.McOption>(), null, null);
            return model;
        }

        var mine = await _signups.GetForAttendeeAsync(eventId, a.Id, ct);
        model.Fill(
            notEligible: false,
            // Â§972 â a TEST Master Class is never offered to an attendee for selection.
            await _signups.ListMasterClassesAsync(eventId, ct, excludeTestSessions: true),
            mine.FirstOrDefault(s => s.Status == MasterClassSignupStatus.Confirmed),
            mine.FirstOrDefault(s =>
                s.Status is MasterClassSignupStatus.Waitlisted or MasterClassSignupStatus.Offered));
        return model;
    }

    /// <summary>
    /// Save the chosen class. Returns <see cref="WizardStepOutcome.Invalid"/> with a model error
    /// when the service refuses (full class without consent, not eligible, unknown session) — the
    /// host then re-renders this step, so the attendee stays in the wizard instead of being
    /// bounced out, which was the whole point of embedding it.
    /// </summary>
    public async Task<WizardStepOutcome> SaveAsync(
        MasterClassFormModel model, int eventId, string? email, string baseUrl,
        ModelStateDictionary modelState, CancellationToken ct)
    {
        var a = await ResolveAsync(eventId, email, ct);
        if (a is null || a.TicketStatus != TicketStatus.TwoDay)
        {
            // Not applicable to this participant — never block the wizard on it.
            return WizardStepOutcome.NotRelevant;
        }

        if (model.SessionId <= 0)
        {
            // §413 (operator 2026-07-27: "when i click 'Save & Exit' nothing happens … it fails
            // even though the user already have selected a master class").
            //
            // Posting no radio is NOT the same as having no seat. Someone who already holds a
            // CONFIRMED place and presses Save & next / Save & exit is saying "nothing to change" —
            // and this step is data-backed, so their seat is already persisted. Demanding a
            // selection there made a COMPLETED step impossible to leave: it re-rendered with
            // "Please choose a Master Class" directly above "✓ You're confirmed for Test Master
            // Class" — the screen contradicting itself, which is exactly what he screenshotted.
            //
            // So: an existing confirmed seat ⇒ ADVANCE (a no-op save). Only someone with NO seat is
            // asked to choose, which is the case the message was written for.
            await Refill(model, eventId, a.Id, ct);
            if (model.Confirmed is not null) return WizardStepOutcome.Advance;

            modelState.AddModelError(
                nameof(MasterClassFormModel.SessionId),
                "Please choose a Master Class.");
            return WizardStepOutcome.Invalid;
        }

        var r = await _signups.SignUpAsync(eventId, a.Id, model.SessionId, model.AutoSwitchConsent, ct);
        if (!r.Ok)
        {
            modelState.AddModelError(
                nameof(MasterClassFormModel.SessionId),
                r.Error ?? "That Master Class could not be selected.");
            await Refill(model, eventId, a.Id, ct);
            return WizardStepOutcome.Invalid;
        }

        // Mail on the SAME terms as /Attendee: a confirmed seat gets the confirmation, a
        // waitlisted one gets the waitlist mail. A mail failure must never undo the signup —
        // the seat is already held, so swallow and let the organizer resend if needed.
        if (r.Signup is { } s)
        {
            var sid = await _signups.SignupIdAsync(eventId, a.Id, s.SessionId, ct);
            if (sid is int signupId)
            {
                try
                {
                    if (s.Status == MasterClassSignupStatus.Confirmed)
                        await _email.SendConfirmedAsync(signupId, baseUrl, ct);
                    else if (s.Status == MasterClassSignupStatus.Waitlisted)
                        await _email.SendWaitlistedAsync(signupId, baseUrl, s.WaitlistPosition, ct);
                }
                catch { /* the signup stands even if the mail fails */ }
            }
        }

        await Refill(model, eventId, a.Id, ct);
        return WizardStepOutcome.Advance;
    }

    private async Task Refill(
        MasterClassFormModel model, int eventId, int attendeeId, CancellationToken ct)
    {
        var mine = await _signups.GetForAttendeeAsync(eventId, attendeeId, ct);
        model.Fill(
            notEligible: false,
            // Â§972 â a TEST Master Class is never offered to an attendee for selection.
            await _signups.ListMasterClassesAsync(eventId, ct, excludeTestSessions: true),
            mine.FirstOrDefault(x => x.Status == MasterClassSignupStatus.Confirmed),
            mine.FirstOrDefault(x =>
                x.Status is MasterClassSignupStatus.Waitlisted or MasterClassSignupStatus.Offered));
    }
}
