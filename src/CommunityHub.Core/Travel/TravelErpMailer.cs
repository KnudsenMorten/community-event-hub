using System.Text;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations.Erp;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Travel;

/// <summary>
/// §48 / §1166 — the travel claim to the ERP (e-conomic) inbox, with every file attached. Moved out of
/// the speaker form in §1387: the mail now goes when an organizer ACCEPTS the documents, not when the
/// speaker submits, and it is sent once (<see cref="TravelReimbursement.ErpSentAt"/>).
/// </summary>
/// <remarks>
/// ⚠️ The subject format is finance's contract — they filter and file on it (§1166) — so it is kept
/// byte for byte, including its long-standing spelling.
/// </remarks>
public sealed class TravelErpMailer
{
    /// <summary>§48 — the e-conomic auto-import inbox. Not a participant ⇒ the send is ring-exempt.</summary>
    public const string ErpInbox = "773bilag1685551@e-conomic.dk";

    private readonly CommunityHubDbContext _db;
    private readonly IEmailSender _email;
    private readonly IEmailContextAccessor? _ctx;

    public TravelErpMailer(CommunityHubDbContext db, IEmailSender email, IEmailContextAccessor? ctx = null)
    {
        _db = db;
        _email = email;
        _ctx = ctx;
    }

    /// <summary>§1166 — the finance subject with the VAT zone as a postfix.</summary>
    public static string ClaimSubject(string fullName, string momsZoneToken) =>
        $"Travel Rebursement - {fullName} - ELDK27 [MOMSZONE={momsZoneToken}]";

    /// <summary>
    /// §1387 — the organizer review, for finance (operator 2026-11-08: <i>"we need to include all req in the claim and
    /// the ones in question and the ones we accept/override"</i> · <i>"if i override and accept docs then eldk finance
    /// can see my overrides"</i>): the AI verdict, EVERY requirement with its status (met / missing / in question /
    /// accepted by organizer, with who, when and the note) and who accepted the claim. Empty when the claim was never
    /// reviewed (a pre-§1387 send) — no heading without content.
    /// </summary>
    public static string ReviewSection(TravelReimbursement row)
    {
        var checks = TravelReviewService.Checks(row);
        var accepted = row.ReviewState == TravelReviewState.Accepted;
        if (checks.Count == 0 && !accepted && row.AiVerdict == TravelAiVerdict.NotChecked) return "";

        string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
        string When(DateTimeOffset? d) => d is { } v ? v.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture) : "";
        var sb = new StringBuilder();
        sb.Append("<p style=\"font-size:14px;margin:16px 0 6px;\"><strong>Document review</strong></p>");
        sb.Append("<table style=\"border-collapse:collapse;font-size:14px;\">");
        sb.Append($"<tr><td style=\"padding:2px 12px 2px 0;\"><strong>AI check</strong></td><td>{Enc(TravelReviewService.EffectiveVerdictText(row.AiVerdict, checks))}"
                  + (string.IsNullOrWhiteSpace(row.AiSummary) ? "" : $" — {Enc(row.AiSummary)}") + "</td></tr>");
        if (accepted)
            sb.Append($"<tr><td style=\"padding:2px 12px 2px 0;\"><strong>Accepted by</strong></td><td>{Enc(row.ReviewedByEmail ?? "an organizer")}"
                      + (row.ReviewedAt is null ? "" : $", {When(row.ReviewedAt)}")
                      + (string.IsNullOrWhiteSpace(row.ReviewNote) ? "" : $" — {Enc(row.ReviewNote)}") + "</td></tr>");
        sb.Append("</table>");

        if (checks.Count > 0)
        {
            sb.Append("<table style=\"border-collapse:collapse;font-size:13px;margin-top:8px;\">");
            sb.Append("<tr><th style=\"text-align:left;padding:3px 10px 3px 0;border-bottom:1px solid #ccc;\">Requirement</th>"
                      + "<th style=\"text-align:left;padding:3px 10px 3px 0;border-bottom:1px solid #ccc;\">Status</th>"
                      + "<th style=\"text-align:left;padding:3px 0;border-bottom:1px solid #ccc;\">Details</th></tr>");
            foreach (var c in checks)
            {
                var colour = c.IsAccepted ? "#1d4ed8" : c.Ok switch { true => "#166534", false => "#991b1b", _ => "#92400e" };
                var details = new List<string>();
                if (!string.IsNullOrWhiteSpace(c.Note)) details.Add("AI: " + Enc(c.Note));
                if (c.IsAccepted)
                    details.Add($"Accepted by {Enc(c.AcceptedBy ?? "an organizer")}, {When(c.AcceptedAt)}"
                                + (string.IsNullOrWhiteSpace(c.AcceptNote) ? "" : $": {Enc(c.AcceptNote)}"));
                sb.Append($"<tr><td style=\"padding:3px 10px 3px 0;vertical-align:top;\">{Enc(c.Requirement)}</td>"
                          + $"<td style=\"padding:3px 10px 3px 0;vertical-align:top;color:{colour};font-weight:600;white-space:nowrap;\">"
                          + Enc(c.IsAccepted ? $"Accepted by organizer (was {(c.Ok == false ? "missing" : "in question")})" : c.StatusText) + "</td>"
                          + $"<td style=\"padding:3px 0;vertical-align:top;\">{(details.Count == 0 ? "—" : string.Join("<br />", details))}</td></tr>");
            }
            sb.Append("</table>");
        }
        return sb.ToString();
    }

    public async Task SendAsync(int eventId, int participantId, string fullName, string email,
        TravelReimbursement row, CancellationToken ct = default)
    {
        var (subject, html, attachments) = await RenderAsync(eventId, participantId, fullName, email, row, ct);
        using (_ctx?.Set(new EmailContext("travel-reimbursement-erp", eventId, participantId, fullName, RingExempt: true)))
        {
            if (attachments.Count > 0)
                await _email.SendWithAttachmentsAsync(ErpInbox, subject, html, attachments, ct);
            else
                await _email.SendAsync(ErpInbox, subject, html, ct);
        }
    }

    /// <summary>The finance mail exactly as <see cref="SendAsync"/> sends it — also used for the organizer's preview.</summary>
    public async Task<(string Subject, string Html, List<EmailAttachment> Attachments)> RenderAsync(int eventId, int participantId,
        string fullName, string email, TravelReimbursement row, CancellationToken ct = default)
    {
        var receipts = await _db.TravelReceipts
            .Where(r => r.EventId == eventId && r.ParticipantId == participantId)
            .OrderBy(r => r.UploadedAt)
            .ToListAsync(ct);
        var attachments = receipts
            .Where(r => r.Content is { Length: > 0 })
            .Select(r => new EmailAttachment(r.FileName, r.Content, r.ContentType))
            .ToList();

        var country = await _db.SpeakerProfiles
            .Where(s => s.EventId == eventId && s.ParticipantId == participantId)
            .Select(s => s.Country)
            .FirstOrDefaultAsync(ct);
        var zoneToken = VatZoneMapper.TokenFor(country);
        var subject = ClaimSubject(fullName, zoneToken);

        var amount = row.ClaimAmountEur?.ToString("0.##") ?? "(not specified)";
        string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
        var body = new StringBuilder();
        body.Append("<p style=\"font-size:14px;margin:0 0 10px;\">"
            + $"{Enc(fullName)}<br />{Enc(email)}<br />MOMSZONE={Enc(zoneToken)}</p>");
        body.Append("<p>A travel reimbursement request has been submitted via the Experts Live DK Event Hub.</p>");
        body.Append("<table style=\"border-collapse:collapse;font-size:14px;\">");
        body.Append($"<tr><td style=\"padding:2px 12px 2px 0;\"><strong>Name</strong></td><td>{Enc(fullName)}</td></tr>");
        body.Append($"<tr><td style=\"padding:2px 12px 2px 0;\"><strong>Email</strong></td><td>{Enc(email)}</td></tr>");
        body.Append($"<tr><td style=\"padding:2px 12px 2px 0;\"><strong>Origin city</strong></td><td>{Enc(row.OriginCity)}</td></tr>");
        body.Append($"<tr><td style=\"padding:2px 12px 2px 0;\"><strong>Amount claimed (EUR)</strong></td><td>{Enc(amount)}</td></tr>");
        body.Append($"<tr><td style=\"padding:2px 12px 2px 0;\"><strong>Explanation</strong></td><td>{Enc(row.Explanation)}</td></tr>");
        body.Append("</table>");
        body.Append($"<p style=\"font-size:14px;\">Attached file(s): {attachments.Count}</p>");
        body.Append(ReviewSection(row));
        return (subject, body.ToString(), attachments);
    }
}
