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
    /// <remarks>
    /// <para>§1076 — <b>the organizer mailbox, not the operator's personal one.</b> Operator
    /// 2026-08-11: <i>"change the logistics mail to go to info@expertslive.dk"</i>, applying his
    /// general rule: <b>ops alerts → the operator; event-related action mail → info@</b>. Logistics
    /// reports (venue, hotel, catering, furniture) are event actions someone must act on, so they
    /// belong where the team can see them rather than in one person's inbox.</para>
    ///
    /// <para>⚠️ This is not a small blast radius: <see cref="ApprovedForRealRecipients"/> is FALSE,
    /// so <b>every</b> logistics notification is routed here — none reach a venue or supplier yet.
    /// Changing this address therefore moves the whole logistics stream at once.</para>
    ///
    /// <para>🔒 Verified 2026-08-11 that NEITHER PROD host sets a <c>LogisticsMail__*</c> app
    /// setting, so this default is what actually runs — the change has effect rather than being
    /// silently overridden.</para>
    /// </remarks>
    public string ReviewMailbox { get; set; } = DefaultReviewMailbox;

    /// <summary>
    /// The shipped review mailbox. 🔑 A named constant so the TESTS assert the RULE ("everything goes
    /// to the review mailbox until approved") rather than a literal address — three tests had the old
    /// address typed into them and failed on a recipient change that broke no behaviour at all.
    /// </summary>
    public const string DefaultReviewMailbox = "info@expertslive.dk";

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
