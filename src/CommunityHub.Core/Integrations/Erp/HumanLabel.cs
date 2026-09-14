namespace CommunityHub.Core.Integrations.Erp;

/// <summary>
/// 🔴 §1116 — the last line of defence against printing a MACHINE ID where a person reads a name.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-21, on an Arrow invoice reading <c>Ticket Class: 14880000003485482</c>:
/// *"stop using numbers like this - people dont understand a ticket class number - use the
/// displayname"* — then, on the mails: *"it was the same with customer id - noboddy knows a 13 digit
/// customer id - use names"*.</para>
///
/// <para>🔑 <b>Why a shared guard and not a fix at each call site.</b> Every ticket-class label in
/// this area is resolved through a fallback chain that ENDS IN THE ID (§1013b: live Backstage name →
/// the name stored on the pool → the id itself). That chain is right — a page showing an id is
/// better than a page showing a blank — but it means the id leaks into whatever renders the result,
/// and the renderers are an invoice, a claim invite, a usage report and an ops mail. Fixing the ones
/// he happened to see would leave the rest to be found by the next screenshot.</para>
///
/// <para>⚠️ <b>This is a net, not a name resolver.</b> The right answer is always a real display
/// name; this only decides what to do once every source of one has failed, and the choice is between
/// printing a number at a customer and printing nothing. It must never be used to justify not
/// resolving the name properly.</para>
/// </remarks>
public static class HumanLabel
{
    /// <summary>
    /// Is this "label" actually a raw system id — long, all digits, and meaningless to a reader?
    /// </summary>
    /// <remarks>
    /// <para>🔒 <b>Deliberately conservative: 10+ digits and nothing else.</b> Backstage ticket-class
    /// ids are 17 digits, and no plausible class NAME is ten digits long — so this catches what
    /// actually leaks here while leaving "2026" and "Track 12" alone.</para>
    ///
    /// <para>⚠️ <b>It does NOT catch an e-conomic customer number, which is 8–9 digits, and it is not
    /// meant to.</b> A customer always has <see cref="Customer"/>, which pairs the name with the
    /// number rather than choosing between them — there is no case where we must decide whether a
    /// customer value "looks like" an id. Widening the threshold to cover them would buy nothing and
    /// would start swallowing real labels.</para>
    /// </remarks>
    public static bool IsMachineId(string? value)
    {
        var s = (value ?? string.Empty).Trim();
        return s.Length >= 10 && s.All(char.IsAsciiDigit);
    }

    /// <summary>
    /// A ticket-class label fit to show a customer, or <paramref name="fallback"/> when all we have
    /// is an id.
    /// </summary>
    /// <remarks>
    /// The default fallback is the plain word "tickets": a sentence reading *"start claiming the 30
    /// pre-paid claims for tickets"* is slightly vague, where the same sentence with a 17-digit
    /// number in it is broken.
    /// </remarks>
    public static string TicketClassText(string? label, string fallback = "tickets")
    {
        var s = (label ?? string.Empty).Trim();
        return s.Length == 0 || IsMachineId(s) ? fallback : s;
    }

    /// <summary>
    /// A customer as a person reads them: <c>Name (12345678)</c>, or the bare number when no name is
    /// known.
    /// </summary>
    /// <remarks>
    /// 🔑 The number is KEPT, because it is what the operator searches on in e-conomic. It is simply
    /// not the identity, and it must never be the only thing on the line (§1109).
    /// </remarks>
    public static string Customer(string? name, int? customerNumber)
    {
        var no = customerNumber is { } n
            ? n.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;
        var nm = (name ?? string.Empty).Trim();

        if (nm.Length == 0) return no ?? "(no customer set)";
        return no is null ? nm : $"{nm} ({no})";
    }
}
