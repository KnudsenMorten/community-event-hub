namespace CommunityHub.Core.Domain;

/// <summary>
/// §1080 — WHO a campaign goes to. The twelve the operator listed, verbatim (2026-08-12).
/// </summary>
/// <remarks>
/// 🔑 An ENUM, not a query an organizer types. Each value has exactly one resolver, so "All
/// attendees" means the same set on every campaign, in the preview and in the send — and a change
/// to a definition is a change in one place with tests around it.
/// </remarks>
public enum MailAudience
{
    AllParticipants = 0,
    AllAttendees = 1,
    TwoDayTicketHolders = 2,
    OneDayTicketHolders = 3,
    SponsorsAndExhibitors = 4,
    ExhibitorsOnly = 5,
    Volunteers = 6,
    Speakers = 7,
    MasterClassSpeakers = 8,
    Media = 9,
    EventPartners = 10,

    /// <summary>
    /// 🔴 People from PREVIOUS editions who are not attending this one — the imported list minus
    /// this edition's attendees. The only audience that reaches outside the hub's own people, and
    /// therefore the only one the ring gate would refuse.
    /// </summary>
    PreviousAttendeesNotThisEdition = 11,
}

/// <summary>Where a campaign is in its life.</summary>
public enum MailCampaignState
{
    Draft = 0,
    Scheduled = 1,
    Sending = 2,
    Paused = 3,
    Sent = 4,
}

/// <summary>
/// §1080 — one mass mailing: a template, an audience, and the deliberate acts that let it send.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-12: <i>"i want the ability to mass mail based on individual templates,
/// which must be linked to one of the following …"</i></para>
///
/// <para>🔴 <b>A campaign is the one thing in the hub that can write to thousands of people at
/// once</b>, and one of its audiences is people who are not participants at all — whom the ring gate
/// exists to refuse. Three things stand in front of a send, and all three were his decision
/// (2026-08-12): its own feature switch, an <b>acknowledged dry-run</b> (see
/// <see cref="DryRunAcknowledgedAt"/>), and suppression checked per recipient at send time.</para>
/// </remarks>
public class MailCampaign
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>What an organizer calls it — shown in the list and in the log.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The template key this campaign renders (the same store the rest of the hub uses).</summary>
    public string TemplateKey { get; set; } = string.Empty;

    public MailAudience Audience { get; set; }

    public MailCampaignState State { get; set; } = MailCampaignState.Draft;

    /// <summary>When it should start sending. Null ⇒ it waits for a person.</summary>
    public DateTimeOffset? ScheduledFor { get; set; }

    /// <summary>
    /// How many go out per tick, and how long the job waits between ticks.
    /// </summary>
    /// <remarks>
    /// 🔑 His words: <i>"mass mail in batches so ip is not blocked"</i>. A shared sending IP is a
    /// reputation shared with everyone else on it; a thousand messages in one minute is what gets it
    /// throttled or listed. Defaults are deliberately timid — raising them is a decision, lowering
    /// them never hurts.
    /// </remarks>
    public int BatchSize { get; set; } = 50;
    public int BatchIntervalMinutes { get; set; } = 15;

    /// <summary>
    /// 🔴 §1080b — whether THIS campaign's mail carries an unsubscribe link. <b>Default false.</b>
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-13, correcting the first build: <i>"it can only be done to mass emails
    /// functionality … and it must be linked to per template if it should be included. only one
    /// template must include the unsubscribe functionality and that is the group All previous eldk
    /// attendees except eldk27 participants"</i>.</para>
    ///
    /// <para>🔑 <b>Why it is not on everything.</b> An unsubscribe link on a mailing to this
    /// edition's own attendees, speakers or sponsors invites them to opt out of the operational mail
    /// they NEED — their ticket, their session, their booth. The people it is for are the ones with
    /// no current relationship to this edition: the previous-attendees list, where the existing
    /// customer relationship is the basis and the opt-out is its condition.</para>
    ///
    /// <para>🔒 <b>It is REQUIRED for that audience</b> — see
    /// <see cref="MailAudienceResolver.ReachesOutsideTheHub"/>; a campaign to previous attendees
    /// without it is refused, because that is the one where the basis depends on it.</para>
    /// </remarks>
    public bool IncludeUnsubscribeLink { get; set; }

    /// <summary>
    /// 🔴 The dry-run acknowledgement: an organizer has SEEN the recipient count and a sample of the
    /// addresses, and confirmed. Null ⇒ this campaign cannot send.
    /// </summary>
    /// <remarks>
    /// ⚠️ It is deliberately invalidated whenever the audience or the template changes — an
    /// acknowledgement of a different mailing is not an acknowledgement of this one.
    /// </remarks>
    public DateTimeOffset? DryRunAcknowledgedAt { get; set; }
    public string? DryRunAcknowledgedByEmail { get; set; }
    public int DryRunRecipientCount { get; set; }

    // ---- progress ----------------------------------------------------------

    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? LastBatchAt { get; set; }
    public int SentCount { get; set; }
    public int FailedCount { get; set; }
    public int SuppressedCount { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? CreatedByEmail { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>True when a person has approved exactly this mailing.</summary>
    public bool MaySend =>
        DryRunAcknowledgedAt is not null
        && !string.IsNullOrWhiteSpace(TemplateKey)
        && State is MailCampaignState.Draft or MailCampaignState.Scheduled
                 or MailCampaignState.Sending or MailCampaignState.Paused;
}

/// <summary>
/// §1080 — an address from the operator's imported list: somebody who attended a PREVIOUS edition.
/// </summary>
/// <remarks>
/// <para>🔒 <b>A separate table, never <see cref="Participant"/>.</b> Participants are this
/// edition's own people and part of that data is a Zoho mirror the sync rewrites; dropping eight
/// thousand strangers into it would corrupt every count in the hub and put them in front of
/// role-gated pages.</para>
///
/// <para>⚠️ <b>Legal basis (his decision, 2026-08-12): the existing-customer relationship</b> — they
/// attended a previous ELDK, so we may write to them about the next one. That basis REQUIRES a
/// working unsubscribe in every mail; it is not a nicety here, it is the condition.</para>
/// </remarks>
/// <summary>What happened to one recipient of one campaign.</summary>
public enum MailCampaignRecipientState
{
    Pending = 0,
    Sent = 1,
    Failed = 2,

    /// <summary>Skipped because the address was suppressed AT SEND TIME.</summary>
    Suppressed = 3,
}

/// <summary>
/// §1080 stage 4 — one row per person per campaign: the ledger that makes a batched send
/// <b>resumable</b> and stops anyone being mailed twice.
/// </summary>
/// <remarks>
/// <para>🔴 <b>Without this table a batch job cannot be interrupted.</b> A restart mid-campaign
/// would either re-send to everyone already mailed or skip whoever was in flight — and with
/// thousands of recipients both are discovered by the recipients rather than by us.</para>
///
/// <para>🔑 Rows are written when the campaign STARTS (the audience frozen at that moment), and each
/// batch flips a slice of them. It is also what makes the progress figures honest: counted from
/// rows, not accumulated in a field that drifts.</para>
/// </remarks>
public class MailCampaignRecipient
{
    public int Id { get; set; }

    public int MailCampaignId { get; set; }
    public MailCampaign Campaign { get; set; } = null!;

    /// <summary>Lower-cased — the suppression key.</summary>
    public string Email { get; set; } = string.Empty;
    public string? Name { get; set; }

    public MailCampaignRecipientState State { get; set; } = MailCampaignRecipientState.Pending;
    public DateTimeOffset? SentAt { get; set; }
    public string? Error { get; set; }
}

public class ExternalRecipient
{
    public int Id { get; set; }

    /// <summary>The edition this list was imported FOR (not the one they attended).</summary>
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    public string Email { get; set; } = string.Empty;
    public string? FullName { get; set; }
    public string? CompanyName { get; set; }

    /// <summary>Which edition they came from, when the import knows it ("ELDK26").</summary>
    public string? SourceEdition { get; set; }

    /// <summary>The file this row came from, so an import can be explained months later.</summary>
    public string? ImportBatch { get; set; }

    public DateTimeOffset ImportedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? ImportedByEmail { get; set; }
}

/// <summary>§1080 — why an address must not be written to again.</summary>
public enum MailSuppressionReason
{
    /// <summary>They asked to stop — the tick box or the link in a mail.</summary>
    Unsubscribed = 0,

    /// <summary>The address does not exist / permanently rejected (Brevo webhook).</summary>
    HardBounce = 1,

    /// <summary>They marked it as spam. 🔴 The most expensive signal a sender can ignore.</summary>
    Complaint = 2,

    /// <summary>An organizer suppressed it by hand.</summary>
    Manual = 3,
}

/// <summary>
/// §1080 — one address that campaigns must skip, and why.
/// </summary>
/// <remarks>
/// 🔴 <b>Checked at SEND time, never at audience time.</b> A campaign scheduled on Monday must
/// respect an unsubscribe that arrives on Tuesday — resolving the audience once and mailing it later
/// is exactly how somebody who opted out receives the next mailing anyway.
/// </remarks>
public class MailSuppression
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>Lower-cased and trimmed — the comparison key, not what they typed.</summary>
    public string Email { get; set; } = string.Empty;

    public MailSuppressionReason Reason { get; set; }

    /// <summary>What the bounce actually said, when a webhook supplied one.</summary>
    public string? Detail { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Normalise an address for comparison. One definition, used by every caller.</summary>
    public static string Normalise(string? raw) => (raw ?? string.Empty).Trim().ToLowerInvariant();
}
