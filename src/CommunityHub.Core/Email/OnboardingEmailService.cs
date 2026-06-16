using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Email;

/// <summary>
/// Auto-sends a person's persona onboarding email SET when they are activated
/// (requirement 10a-1). NO approval gate. Idempotent: each onboarding email is
/// recorded in the <see cref="SentReminder"/> ledger keyed on the IDENTITY
/// address + <see cref="OnboardingEmailSets.ReminderType"/> + the per-step
/// occasion, so a second activation pass (or a re-activation) never re-sends an
/// email already sent to that person. Routes to the effective address + the
/// secondary-email CC via <see cref="ParticipantEmailService"/>.
/// </summary>
public sealed class OnboardingEmailService
{
    private readonly CommunityHubDbContext _db;
    private readonly ParticipantEmailService _participantEmail;
    private readonly TimeProvider _clock;

    public OnboardingEmailService(
        CommunityHubDbContext db,
        ParticipantEmailService participantEmail,
        TimeProvider clock)
    {
        _db = db;
        _participantEmail = participantEmail;
        _clock = clock;
    }

    /// <summary>
    /// Send the not-yet-sent onboarding emails for one participant's persona.
    /// Returns the number actually sent (0 if the persona has no set, the person
    /// is not active, or every email was already sent). Safe to call repeatedly.
    /// </summary>
    public async Task<int> SendOnboardingSetAsync(
        int participantId, CancellationToken ct = default)
    {
        var p = await _db.Participants
            .FirstOrDefaultAsync(x => x.Id == participantId, ct);
        if (p is null
            || !p.IsActive
            || p.LifecycleState != ParticipantLifecycleState.Active)
        {
            return 0;
        }

        var persona = OnboardingEmailSets.PersonaFor(p.Role);
        var set = OnboardingEmailSets.For(persona);
        if (set.Count == 0) return 0;

        // Load the onboarding occasions already sent to this identity once.
        var alreadySent = (await _db.SentReminders
            .Where(s => s.EventId == p.EventId
                        && s.RecipientEmail == p.Email
                        && s.ReminderType == OnboardingEmailSets.ReminderType)
            .Select(s => s.OccasionKey)
            .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        var sent = 0;
        foreach (var email in set)
        {
            var occasionKey = $"onboarding:{email.StepKey}";
            if (alreadySent.Contains(occasionKey)) continue;

            var addressed = await _participantEmail.SendTemplateToParticipantAsync(
                p.EventId, p.Id, email.TemplateName,
                category: OnboardingEmailSets.ReminderType, extraTokens: null, ct);
            if (addressed is null) continue;   // participant vanished mid-loop

            _db.SentReminders.Add(new SentReminder
            {
                EventId = p.EventId,
                RecipientEmail = p.Email,        // idempotency keys on identity
                ReminderType = OnboardingEmailSets.ReminderType,
                OccasionKey = occasionKey,
                SentAt = _clock.GetUtcNow(),
            });
            await _db.SaveChangesAsync(ct);
            alreadySent.Add(occasionKey);
            sent++;
        }

        return sent;
    }
}
