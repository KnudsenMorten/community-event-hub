using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Email;

/// <summary>
/// Consumes the onboarding flip-to-0 hand-off (requirement 10a-6). The
/// onboarding-lifecycle (#39) raises an <see cref="OrganizerActionItem"/> of type
/// <see cref="OrganizerActionItemService.TypeOnboardingStepReset"/> when an
/// organizer re-opens a wizard step. This service drains those OPEN items: it
/// sends the person a branded email pointing them at the wizard to complete that
/// step, then marks the action item RESOLVED so it is consumed exactly once. A
/// re-run finds nothing open (idempotent). Routes to effective address +
/// secondary CC via <see cref="ParticipantEmailService"/>.
/// </summary>
public sealed class OnboardingStepResetEmailService
{
    private const string TemplateName = "onboarding-step-reset";
    private const string Category = "onboarding-step-reset";

    private readonly CommunityHubDbContext _db;
    private readonly ParticipantEmailService _participantEmail;
    private readonly OrganizerActionItemService _actions;

    public OnboardingStepResetEmailService(
        CommunityHubDbContext db,
        ParticipantEmailService participantEmail,
        OrganizerActionItemService actions)
    {
        _db = db;
        _participantEmail = participantEmail;
        _actions = actions;
    }

    /// <summary>
    /// Send the reminder email for every open <c>onboarding-step-reset</c> action
    /// item in the edition, resolving each as it is sent. Returns the number of
    /// items consumed (emailed + resolved). An item whose participant has no
    /// resolvable address is still resolved (it cannot be retried by mail), so the
    /// queue does not wedge.
    /// </summary>
    public async Task<int> SendPendingAsync(int eventId, CancellationToken ct = default)
    {
        var open = await _db.OrganizerActionItems
            .Where(a => a.EventId == eventId
                        && a.Type == OrganizerActionItemService.TypeOnboardingStepReset
                        && a.ResolvedAt == null
                        && a.ParticipantId != null)
            .ToListAsync(ct);

        var consumed = 0;
        foreach (var item in open)
        {
            var stepLabel = ExtractStepLabel(item.Summary);
            await _participantEmail.SendTemplateToParticipantAsync(
                eventId, item.ParticipantId!.Value, TemplateName,
                category: Category,
                extraTokens: new Dictionary<string, string> { ["stepLabel"] = stepLabel },
                ct);

            // Consume it (idempotent: ResolveAsync no-ops if already resolved).
            await _actions.ResolveAsync(
                eventId, item.Id,
                notes: "Reminder email sent to the participant.", ct);
            consumed++;
        }

        return consumed;
    }

    /// <summary>
    /// Pull the human step label out of the action summary, which is
    /// <c>"Onboarding step re-opened: {label} — please remind ..."</c>. Falls back
    /// to a generic phrase if the shape ever changes.
    /// </summary>
    public static string ExtractStepLabel(string summary)
    {
        if (string.IsNullOrWhiteSpace(summary)) return "an onboarding step";
        var colon = summary.IndexOf(": ", StringComparison.Ordinal);
        if (colon < 0) return "an onboarding step";
        var rest = summary[(colon + 2)..];
        var dash = rest.IndexOf('—');               // em dash used in the summary
        if (dash < 0) dash = rest.IndexOf(" - ", StringComparison.Ordinal);
        var label = (dash < 0 ? rest : rest[..dash]).Trim();
        return label.Length == 0 ? "an onboarding step" : label;
    }
}
