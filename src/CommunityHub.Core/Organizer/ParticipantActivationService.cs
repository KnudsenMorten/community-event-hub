using CommunityHub.Core.Email;

namespace CommunityHub.Core.Organizer;

/// <summary>
/// Orchestrates the activation hand-off (requirement 10a-1): advance the queue rows to
/// <c>Active</c> via <see cref="PreselectionQueueService"/>, then send each newly-activated
/// person their WELCOME. Kept separate from <see cref="PreselectionQueueService"/> so the pure
/// queue state-machine stays free of email dependencies and is unit-testable on its own.
///
/// <para>🔒 §707.21 — ONE MAIL, AND IT IS THE WELCOME. Operator 2026-07-30: *"this is a failure.
/// speaker must only get one mail - welcome speaker mail … it should be the welcome. the concept of
/// onboarding-getting-started could be legacy until we standardized on the welcome word … it must be
/// consistent"*.</para>
///
/// <para>This used to send the persona ONBOARDING SET (<c>onboarding-getting-started</c>), which was
/// a second, parallel welcome concept: ONE identical mail shared by all five personas, beside the
/// per-role <c>welcome-*</c> family. A speaker activated from the queue could therefore receive both.
/// The PROD ledger settled which is live — <c>welcome</c>: 28 sends, still running (latest
/// 2026-07-28); <c>onboarding</c>: 4 sends, none since 2026-06-29, and the only recent
/// <c>onboarding-getting-started</c> was a MANUAL resend. So the onboarding set is legacy from before
/// "welcome" was standardised on.</para>
///
/// <para>The welcome send is idempotent on its own ledger, so activating someone who was already
/// welcomed sends nothing — which is precisely the "only one mail" rule.</para>
///
/// <para>REQUIREMENTS §201: the "… — added to your calendar" welcome (a calendar-attach e-mail) used
/// to be sent here on activation. The operator did not ask for it and the "Add to my calendar"
/// feature was removed, so activation never sends a calendar invite.</para>
/// </summary>
public sealed class ParticipantActivationService
{
    private readonly PreselectionQueueService _queue;
    private readonly Reminders.WelcomeEmailService _welcome;

    public ParticipantActivationService(
        PreselectionQueueService queue,
        Reminders.WelcomeEmailService welcome)
    {
        _queue = queue;
        _welcome = welcome;
    }

    /// <summary>Result of an activate-and-onboard call.</summary>
    /// <param name="Queue">The raw queue advance result (matched/changed/activated ids).</param>
    /// <param name="OnboardingEmailsSent">
    /// How many WELCOME mails actually went out across the activated people (§707.21 — it was the
    /// onboarding set; the name is kept so the queue page's wording and tests do not churn).
    /// </param>
    public sealed record ActivationResult(
        PreselectionQueueService.QueueResult Queue,
        int OnboardingEmailsSent);

    /// <summary>
    /// Activate the selected queue rows and send each newly-activated person their WELCOME.
    /// People already Active are not re-welcomed (the queue only lists ids that newly reached
    /// Active), and the welcome is idempotent on its own ledger, so this is safe to call repeatedly
    /// and a person can never be welcomed twice.
    /// </summary>
    public async Task<ActivationResult> ActivateAndOnboardAsync(
        int eventId, IEnumerable<int> participantIds, CancellationToken ct = default)
    {
        var result = await _queue.ActivateAsync(eventId, participantIds, ct);

        var emailsSent = 0;
        foreach (var id in result.ActivatedIds)
        {
            // §707.21 — the role-appropriate welcome (welcome-speaker, welcome-sponsor, …), NOT the
            // retired shared onboarding mail. Idempotent: already welcomed ⇒ nothing sent.
            if (await _welcome.SendWelcomeAsync(id, ct)) emailsSent++;
        }

        return new ActivationResult(result, emailsSent);
    }
}
