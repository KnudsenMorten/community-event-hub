using CommunityHub.Core.Email;

namespace CommunityHub.Core.Organizer;

/// <summary>
/// Orchestrates the activation hand-off (requirement 10a-1): advance the queue
/// rows to <c>Active</c> via <see cref="PreselectionQueueService"/>, then
/// auto-send each newly-activated person's persona onboarding email set via
/// <see cref="OnboardingEmailService"/> — NO approval gate, idempotent. Kept
/// separate from <see cref="PreselectionQueueService"/> so the pure queue
/// state-machine stays free of email dependencies and is unit-testable on its own.
/// </summary>
public sealed class ParticipantActivationService
{
    private readonly PreselectionQueueService _queue;
    private readonly OnboardingEmailService _onboarding;
    private readonly CalendarInviteEmailService _calendarInvite;

    public ParticipantActivationService(
        PreselectionQueueService queue,
        OnboardingEmailService onboarding,
        CalendarInviteEmailService calendarInvite)
    {
        _queue = queue;
        _onboarding = onboarding;
        _calendarInvite = calendarInvite;
    }

    /// <summary>Result of an activate-and-onboard call.</summary>
    /// <param name="Queue">The raw queue advance result (matched/changed/activated ids).</param>
    /// <param name="OnboardingEmailsSent">Total onboarding emails sent across the activated people.</param>
    /// <param name="CalendarInvitesSent">Total .ics calendar invites sent across the activated people.</param>
    public sealed record ActivationResult(
        PreselectionQueueService.QueueResult Queue,
        int OnboardingEmailsSent,
        int CalendarInvitesSent);

    /// <summary>
    /// Activate the selected queue rows and, for each newly-activated person, send
    /// their persona onboarding set AND a one-shot .ics calendar invite (so the
    /// event lands in their calendar). People already Active are not re-onboarded
    /// (the queue only lists ids that newly reached Active); both sends are
    /// idempotent, so this is safe to call repeatedly. The calendar invite is
    /// skipped automatically when the edition has calendar sync disabled.
    /// </summary>
    public async Task<ActivationResult> ActivateAndOnboardAsync(
        int eventId, IEnumerable<int> participantIds, CancellationToken ct = default)
    {
        var result = await _queue.ActivateAsync(eventId, participantIds, ct);

        var emailsSent = 0;
        var invitesSent = 0;
        foreach (var id in result.ActivatedIds)
        {
            emailsSent += await _onboarding.SendOnboardingSetAsync(id, ct);
            if (await _calendarInvite.SendActivationInviteAsync(id, ct))
            {
                invitesSent++;
            }
        }

        return new ActivationResult(result, emailsSent, invitesSent);
    }
}
