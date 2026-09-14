using System.Globalization;
using CommunityHub.Core.Integrations;

namespace CommunityHub.Core.Integrations.Erp;

/// <summary>
/// §1094 — the body of the fortnightly partner status mail: usage, cap remaining, and the link.
/// </summary>
/// <remarks>
/// <para>Pure, like every other composer here: it takes numbers and returns a subject and a body, so
/// it can be read and tested without a database, a mail server or a clock.</para>
///
/// <para>🔑 <b>Summary in the mail, detail on the page.</b> The report says how many and how many
/// left; it does NOT list who. Names and e-mail addresses stay behind the token, where access can be
/// withdrawn — a mail cannot be unsent, and this one is sent every fortnight to an address that may
/// well be a shared inbox.</para>
/// </remarks>
public static class CouponUsageReportComposer
{
    public static (string Subject, string Html) Build(
        string eventDisplayName,
        string customerLabel,
        int attendeeCount,
        MonitoredBilling billing,
        string monitorUrl)
    {
        ArgumentNullException.ThrowIfNull(billing);

        static string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);
        static string N(int v) => v.ToString(CultureInfo.InvariantCulture);
        static string Money(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture);
        // §1105 — the same breathing room as the claim invite; the two mails go to the same person.
        static string P(string inner) =>
            $"<p style=\"margin:0 0 22px;line-height:1.6;\">{inner}</p>";

        var subject = $"Your ticket usage for {eventDisplayName}";
        var sb = new System.Text.StringBuilder();

        // 🔴 §1105 — plain words. "Here is your fortnightly summary" made the reader stop on
        // "fortnightly" (operator 2026-08-20: *"we dont understand this wording"*), and a summary of
        // WHAT was not said until the next line. Say what it is, then how often it comes.
        sb.Append(P(
            $"Here is your ticket status for <strong>{Enc(eventDisplayName)}</strong> &mdash; "
            + "we send you this every 2 weeks."));
        sb.Append(P(
            $"<strong>{N(attendeeCount)}</strong> ticket(s) have been claimed so far in total."));

        // --- prepaid pools ---------------------------------------------------------------
        foreach (var p in billing.Pools)
        {
            var line =
                // §1116 — never a raw class id in a customer's status mail.
                $"<strong>{Enc(HumanLabel.TicketClassText(p.TicketClassLabel))}</strong>: {N(p.Purchased)} bought, "
                + $"{N(p.Claimed)} used, <strong>{N(p.Remaining)}</strong> left";

            // ⚠️ Oversubscription is stated, not hidden. It means tickets went out that nobody paid
            // for, and the partner is the one who can explain it.
            if (p.IsOversubscribed)
            {
                line += " — <strong>this is more than was bought</strong>, so we will be in touch";
            }
            else if (p.Closed)
            {
                line += " (this allocation is closed)";
            }

            sb.Append(P(line));
        }

        // --- ad-hoc coupons --------------------------------------------------------------
        foreach (var a in billing.AdHoc)
        {
            var line = $"<strong>{Enc(a.CouponName)}</strong>: {N(a.Claimed)} claimed";

            if (a.IsCapped)
            {
                line += $" of an agreed <strong>{N(a.CapTickets!.Value)}</strong>"
                        + $", <strong>{N(a.Remaining!.Value)}</strong> left";
                if (a.IsOverCap) line += " — <strong>above the agreed limit</strong>";
            }
            else
            {
                // 🔑 No invented ceiling. An uncapped agreement has no "left".
                line += " (no agreed limit)";
            }

            line += $". Invoiced so far: <strong>{Money(a.BilledSoFarDkk)} DKK</strong>";
            sb.Append(P(line));
        }

        // --- the page --------------------------------------------------------------------
        sb.Append(P(
            "You can see exactly who has signed up, and ask for more tickets, on your own page:"));
        sb.Append(P($"<a href=\"{Enc(monitorUrl)}\">{Enc(monitorUrl)}</a>"));

        if (billing.AdHoc.Any(a => a.IsCapped) || billing.Pools.Count > 0)
        {
            sb.Append(P(
                "<strong>Need more?</strong> Use the &ldquo;Need more tickets?&rdquo; box on that "
                + "page to tell us the new total — we will confirm by email."));
        }

        sb.Append(P(
            "<span style=\"color:#6b7280;font-size:13px;\">Please treat the link as confidential — "
            + "anyone who has it can see the names and e-mail addresses of the people who used your "
            + "code.</span>"));
        sb.Append(P("Best regards,<br/>Experts Live Denmark organizer-team"));

        return (subject, sb.ToString());
    }
}
