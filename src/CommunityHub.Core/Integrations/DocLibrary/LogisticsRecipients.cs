namespace CommunityHub.Core.Integrations.DocLibrary;

/// <summary>
/// §6.4 / work-order §5.6 — who receives the LOGISTICS notification mails (the ones that carry the
/// generated Excel files).
/// </summary>
/// <remarks>
/// <para>🔒 <b>Operator 2026-08-02: <i>"the logistics mails should go to mok@expertslive.dk until I
/// approve them … I will add correct mails when report is excel is approved"</i>, and, asked to be
/// precise: <i>"it is only the excel notification mails where mok must receive … for the logistics
/// mails"</i>.</b> So this override is deliberately NARROW — it governs the §3.5 file notifications
/// and nothing else in CEH. Every other mail in the product keeps its own audience and its own
/// ring gate.</para>
///
/// <para>⚠️ <b>Why this exists as CODE and not as a note in a document.</b> The recipients in §5.6
/// are EXTERNAL — a venue mailbox, a hotel contact, a supplier. The first time the schedule runs
/// with the real addresses in it, a wrong or unreviewed spreadsheet reaches somebody outside the
/// organisation, and that cannot be recalled. So the redirect is enforced in the one place the
/// sender asks, and the real addresses stay unusable until he flips
/// <see cref="ApprovedForRealRecipients"/>.</para>
///
/// <para>🔑 <b>The real addresses are still configured, not deleted.</b> They are the §5.6
/// settings and they will be needed the moment the reports are approved; deleting them would make
/// approval a code change instead of a config change. What the switch controls is whether they are
/// USED.</para>
/// </remarks>
public sealed class LogisticsRecipients
{
    public const string SectionName = "LogisticsMail";

    /// <summary>Where everything goes until the reports are approved.</summary>
    public string ReviewMailbox { get; set; } = "mok@expertslive.dk";

    /// <summary>
    /// 🔴 FALSE until the operator has approved the generated spreadsheets. While false, EVERY
    /// logistics notification goes to <see cref="ReviewMailbox"/> and no external address is used.
    /// </summary>
    public bool ApprovedForRealRecipients { get; set; }

    /// <summary>§5.6 — the venue operations mailbox (food files, TV rental).</summary>
    public string VenueOperations { get; set; } = string.Empty;

    /// <summary>§5.6 — who reviews exhibitor-wall uploads.</summary>
    public string ExhibitorWallReview { get; set; } = string.Empty;

    /// <summary>§5.6 — the organizer who receives the furniture rental file.</summary>
    public string LogisticsOrganizer { get; set; } = string.Empty;

    /// <summary>§5.6 — the general event mailbox.</summary>
    public string EventMailbox { get; set; } = string.Empty;

    /// <summary>
    /// The address a logistics notification must actually be sent to.
    /// </summary>
    /// <param name="intended">
    /// The §5.6 recipient this mail is FOR — a venue mailbox, a hotel contact. Used only once
    /// approved.
    /// </param>
    /// <remarks>
    /// 🔒 <b>Every logistics send goes through here.</b> A sender that reads an address directly is
    /// a sender that can bypass the review gate, and the thing being bypassed is "an unreviewed
    /// spreadsheet reaches a hotel".
    /// </remarks>
    public string Resolve(string? intended)
    {
        if (!ApprovedForRealRecipients) return ReviewMailbox;

        var real = (intended ?? string.Empty).Trim();
        // Approved, but this particular recipient was never configured: the review mailbox is the
        // safe answer. Silently sending nowhere would hide a missing setting.
        return real.Length == 0 ? ReviewMailbox : real;
    }

    /// <summary>
    /// A one-line explanation for the log and the organizer page, so a redirected mail is never a
    /// mystery to whoever finds it.
    /// </summary>
    public string ExplainFor(string? intended) =>
        ApprovedForRealRecipients
            ? (string.IsNullOrWhiteSpace(intended)
                ? $"No recipient configured — sent to {ReviewMailbox}."
                : $"Sent to {intended}.")
            : $"Logistics reports are not approved yet, so this went to {ReviewMailbox} "
              + $"instead of {(string.IsNullOrWhiteSpace(intended) ? "its recipient" : intended)}.";
}
