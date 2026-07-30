using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace CommunityHub.Forms.Steps;

/// <summary>
/// §384 — the render/bind model for the WAITLIST wizard step (step 2 of the Master Class flow).
/// </summary>
public sealed class MasterClassWaitlistFormModel
{
    /// <summary>The FULL class the attendee wants to queue for (0 = none chosen).</summary>
    public int SessionId { get; set; }

    /// <summary>
    /// Consent to release a held seat if this waitlist place turns into a real one. Only meaningful
    /// when the attendee already holds a confirmed seat — the signup service refuses the switch
    /// without it, so the step must carry it.
    /// </summary>
    public bool AutoSwitchConsent { get; set; }

    // ---- render-only state (never bound from the POST) ----------------------

    /// <summary>True when this participant has no 2-day attendee row (step not applicable).</summary>
    public bool NotEligible { get; private set; }

    /// <summary>The FULL classes only — the entire point of this step.</summary>
    public IReadOnlyList<MasterClassSignupService.McOption> FullOptions { get; private set; }
        = Array.Empty<MasterClassSignupService.McOption>();

    /// <summary>The attendee's confirmed seat, if any (shown as the thing they might trade).</summary>
    public MasterClassSignupService.MySignup? Confirmed { get; private set; }

    /// <summary>The attendee's existing waitlist place / held offer, if any.</summary>
    public MasterClassSignupService.MySignup? Pending { get; private set; }

    internal void Fill(
        bool notEligible,
        IReadOnlyList<MasterClassSignupService.McOption> fullOptions,
        MasterClassSignupService.MySignup? confirmed,
        MasterClassSignupService.MySignup? pending)
    {
        NotEligible = notEligible;
        FullOptions = fullOptions;
        Confirmed = confirmed;
        Pending = pending;
    }
}

/// <summary>
/// §384 — load/save for the WAITLIST step (operator 2026-07-26: <i>"i suggest we make step 1 master
/// class selection and step 2 is waitlist selection for only full classes"</i>).
///
/// <para><b>Why a separate step rather than a smarter control.</b> Booking a seat and joining a
/// waitlist are not mutually exclusive — an attendee can hold a seat in one class and queue for
/// another, and <see cref="MasterClassSignupService"/> has always supported that. A single radio
/// group could express only one of them, so the UI misrepresented the rules. Two steps match the
/// domain; §377's grouped headings only made the wrong model easier to read.</para>
///
/// <para><b>Same engine, no second implementation.</b> Every decision still goes through
/// <see cref="MasterClassSignupService"/> — the same service <c>/Attendee</c>, the selection step
/// and <c>/Attendee/Waitlist</c> use. This step only narrows WHAT IS OFFERED (full classes) and
/// leaves capacity, promotion, the one-seat rule and the consent gate exactly where they live.</para>
///
/// <para><b>Never blocks the wizard.</b> A waitlist place is optional by nature, so choosing
/// nothing returns <see cref="WizardStepOutcome.Advance"/> rather than a validation error — unlike
/// the selection step, where "pick a Master Class" is the whole task.</para>
/// </summary>
public sealed class MasterClassWaitlistFormService : IWizardFormService
{
    private readonly MasterClassSignupService _signups;
    private readonly CommunityHub.Core.Email.MasterClassEmailService _email;

    public MasterClassWaitlistFormService(
        MasterClassSignupService signups,
        CommunityHub.Core.Email.MasterClassEmailService email)
    {
        _signups = signups;
        _email = email;
    }

    private Task<Attendee?> ResolveAsync(int eventId, string? email, CancellationToken ct) =>
        _signups.ResolveByEmailAsync(eventId, email, ct);

    public async Task<MasterClassWaitlistFormModel> LoadAsync(
        int eventId, string? email, CancellationToken ct)
    {
        var model = new MasterClassWaitlistFormModel();
        var a = await ResolveAsync(eventId, email, ct);
        if (a is null || a.TicketStatus != TicketStatus.TwoDay)
        {
            model.Fill(notEligible: true, Array.Empty<MasterClassSignupService.McOption>(), null, null);
            return model;
        }

        await RefillAsync(model, eventId, a.Id, ct);

        // §384c (operator 2026-07-26: "if it was selected in iteration 1, then it should be ticked
        // on iteration 2"). The consent IS on record — MasterClassSignup.AutoSwitchConsentAt — it
        // was just never read back, so a second walk through the wizard showed the box unticked and
        // read as "my answer was lost".
        //
        // Set on LOAD only, never in RefillAsync: refill also runs after a POST, where overwriting
        // with the stored value would throw away the tick the attendee just made on a page that is
        // being re-rendered because something else failed validation.
        model.AutoSwitchConsent = model.Pending?.AutoSwitchConsentAt is not null;
        return model;
    }

    public async Task<WizardStepOutcome> SaveAsync(
        MasterClassWaitlistFormModel model, int eventId, string? email, string baseUrl,
        ModelStateDictionary modelState, CancellationToken ct)
    {
        var a = await ResolveAsync(eventId, email, ct);
        if (a is null || a.TicketStatus != TicketStatus.TwoDay)
        {
            return WizardStepOutcome.NotRelevant;
        }

        // Choosing nothing is a legitimate answer here: not everyone wants a queue place.
        if (model.SessionId <= 0)
        {
            await RefillAsync(model, eventId, a.Id, ct);
            return WizardStepOutcome.Advance;
        }

        var r = await _signups.SignUpAsync(eventId, a.Id, model.SessionId, model.AutoSwitchConsent, ct);
        if (!r.Ok)
        {
            modelState.AddModelError(
                nameof(MasterClassWaitlistFormModel.SessionId),
                r.Error ?? "You could not be added to that waitlist.");
            await RefillAsync(model, eventId, a.Id, ct);
            return WizardStepOutcome.Invalid;
        }

        // Mail on the same terms as every other surface. A mail failure must never undo the
        // waitlist place — it is already recorded, so swallow and let an organizer resend.
        if (r.Signup is { } s)
        {
            var sid = await _signups.SignupIdAsync(eventId, a.Id, s.SessionId, ct);
            if (sid is int signupId)
            {
                try
                {
                    if (s.Status == MasterClassSignupStatus.Waitlisted)
                        await _email.SendWaitlistedAsync(signupId, baseUrl, s.WaitlistPosition, ct);
                    else if (s.Status == MasterClassSignupStatus.Confirmed)
                        await _email.SendConfirmedAsync(signupId, baseUrl, ct);
                }
                catch { /* the signup stands even if the mail fails */ }
            }
        }

        await RefillAsync(model, eventId, a.Id, ct);
        return WizardStepOutcome.Advance;
    }

    /// <summary>
    /// Reload the render state. Only FULL classes are offered — and the one the attendee already
    /// holds a seat in is excluded even if it is full, because queueing for your own class is not a
    /// thing.
    /// </summary>
    private async Task RefillAsync(
        MasterClassWaitlistFormModel model, int eventId, int attendeeId, CancellationToken ct)
    {
        var mine = await _signups.GetForAttendeeAsync(eventId, attendeeId, ct);
        var confirmed = mine.FirstOrDefault(x => x.Status == MasterClassSignupStatus.Confirmed);
        var all = await _signups.ListMasterClassesAsync(eventId, ct);

        var pending = mine.FirstOrDefault(x =>
            x.Status is MasterClassSignupStatus.Waitlisted or MasterClassSignupStatus.Offered);

        model.Fill(
            notEligible: false,
            all.Where(o => o.IsFull && o.SessionId != confirmed?.SessionId).ToList(),
            confirmed,
            pending);
    }
}
