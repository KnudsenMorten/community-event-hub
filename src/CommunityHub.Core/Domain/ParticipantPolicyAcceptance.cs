namespace CommunityHub.Core.Domain;

/// <summary>
/// A participant's recorded acceptance of the event policies — the Code of Conduct
/// and the Privacy Policy — captured by the "I accept" Get-Started wizard step
/// (REQUIREMENTS §119). One row per (edition, participant): a persisted record of
/// WHO accepted and WHEN (not a transient tick), so the acceptance is auditable.
/// The exact policy URLs in force at acceptance time are stored alongside so a later
/// URL change does not rewrite what the person actually agreed to.
/// </summary>
public class ParticipantPolicyAcceptance
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    public int ParticipantId { get; set; }
    public Participant Participant { get; set; } = null!;

    /// <summary>The login email of the participant who accepted (who).</summary>
    public string AcceptedByEmail { get; set; } = string.Empty;

    /// <summary>When the acceptance was recorded (when).</summary>
    public DateTimeOffset AcceptedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The Code of Conduct URL shown at acceptance time.</summary>
    public string? CodeOfConductUrl { get; set; }

    /// <summary>The Privacy Policy URL shown at acceptance time.</summary>
    public string? PrivacyPolicyUrl { get; set; }
}
