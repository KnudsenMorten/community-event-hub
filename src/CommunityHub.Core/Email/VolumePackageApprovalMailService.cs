using System.Net;
using System.Text;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CommunityHub.Core.Email;

/// <summary>
/// §1077 stage 2 — THE ONE MAIL THIS FEATURE SENDS: <i>"mail an organizer to approve"</i> when a
/// company reaches ten attendees, carrying the suggested approver for them to confirm or overrule.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-11: <i>"Suggest the purchaser, who bought the most tickets in the 3 checks
/// above … mail an organizer to approve; organizer may overrule"</i>.</para>
///
/// <para>🔴 <b>It mails the ORGANIZER MAILBOX, never the company.</b> Nothing in stage 2 reaches an
/// attendee, a purchaser or a coordinator — the suggested approver is NAMED in a mail to
/// <c>info@</c> and is not written to. That is the whole difference between stage 2 and stage 3, and
/// it is why stage 2 could be built on the strength of "who do we ask?" rather than needing the
/// outbound-mail conversation stage 3 needs.</para>
///
/// <para>🔒 <b>Recipient per §1075's rule</b> (<i>"ops-alerts goes to [the operator] and any other
/// event-related action mails goes to info@expertslive.dk"</i>): this is an event action — someone
/// has to decide whether a company gets a keynote mention — so it goes to <c>info@</c>, read by
/// several people, rather than to one person who might be on holiday.</para>
///
/// <para>⚠️ <b>Why <see cref="EngineAlertSender"/> and not a plain send.</b> The transport ring-gate
/// FAILS CLOSED for a recipient who is not an imported participant, and <c>info@</c> is not one — a
/// direct send would be dropped as "RING-DROP (unknown recipient)" and the feature would look
/// built and be silent. This sender is ring-exempt by construction (kill switch and redirect still
/// apply), which is exactly the §879 organizer-review path.</para>
///
/// <para>🔑 <b>One mail per pass, listing every company waiting</b> — not one mail per company. A
/// morning where four companies cross the line is one decision session for the organizer, and four
/// separate mails would be four chances to action three of them.</para>
/// </remarks>
public sealed class VolumePackageApprovalMailService
{
    /// <summary>
    /// The event-actions mailbox (§1075). Same constant reasoning as
    /// <see cref="OrganizerReviewMailService.Recipient"/>; kept as its own name so a change to one
    /// stream is a decision about that stream.
    /// </summary>
    public const string Recipient = "info@expertslive.dk";

    /// <summary>
    /// The kill switch for stage 2's automatic mail. 🔴 <b>Default OFF</b>: the operator's standing
    /// instruction on §1077 was that outbound mail is the part he wants to approve first, so the
    /// code ships able to send and switched off, rather than shipping later.
    /// ⚠️ §1018c is the warning attached to this pattern — a feature switched off by default and
    /// forgotten never runs at all — so the organizer page STATES the switch's position instead of
    /// leaving it discoverable only in settings.
    /// </summary>
    public const string FeatureKey = "volume-package-approval-mail";

    private readonly CommunityHubDbContext _db;
    private readonly VolumePackageSweep _sweep;
    private readonly EngineAlertSender _alerts;
    private readonly TimeProvider _clock;
    private readonly string _hubUrl;

    public VolumePackageApprovalMailService(
        CommunityHubDbContext db,
        VolumePackageSweep sweep,
        EngineAlertSender alerts,
        TimeProvider? clock = null,
        IOptions<EmailTemplateOptions>? branding = null)
    {
        _db = db;
        _sweep = sweep;
        _alerts = alerts;
        _clock = clock ?? TimeProvider.System;
        _hubUrl = (branding?.Value.HubUrl ?? string.Empty).TrimEnd('/');
    }

    /// <summary>What one pass did.</summary>
    public sealed record ApprovalMailResult(int Companies, bool Sent)
    {
        public static readonly ApprovalMailResult Nothing = new(0, false);
        public override string ToString() =>
            Sent ? $"asked about {Companies} company/companies" : "nothing to ask about";
    }

    /// <summary>
    /// The companies WAITING for a decision: qualifying today, benefits not yet approved, not
    /// declined, and not already asked about.
    /// </summary>
    /// <remarks>
    /// 🔑 <b>Reads <see cref="VolumePackageCompany.QualifiedNow"/> rather than recomputing.</b> The
    /// caller has just run the sweep, so this is the same answer one query later; recomputing here
    /// would let the mail disagree with the page over a ticket cancelled in between, and the mail is
    /// the one that cannot be corrected after the fact.
    /// </remarks>
    public async Task<IReadOnlyList<VolumePackageCompany>> PendingAsync(
        int eventId, CancellationToken ct = default) =>
        await _db.VolumePackageCompanies
            .Where(c => c.EventId == eventId
                        && c.QualifiedNow
                        && c.BenefitsApprovedAt == null
                        && c.DeclinedAt == null
                        && c.ApprovalRequestedAt == null)
            .OrderBy(c => c.CustomName)
            .ToListAsync(ct);

    /// <summary>
    /// Ask the organizer mailbox about every company that is waiting. Stamps
    /// <see cref="VolumePackageCompany.ApprovalRequestedAt"/> on the ones included, so tomorrow's
    /// pass does not ask again.
    /// </summary>
    /// <param name="devSilent">
    /// §752.9 — true when called from the JOB. On DEV the companies are test data, so the mail asks a
    /// human to make a decision that does not exist. A manual send from the page passes false: an
    /// organizer who clicked the button on DEV is testing the mail and should receive it.
    /// </param>
    public async Task<ApprovalMailResult> SendPendingAsync(
        int eventId, bool devSilent = true, CancellationToken ct = default)
    {
        var pending = await PendingAsync(eventId, ct);
        if (pending.Count == 0) return ApprovalMailResult.Nothing;

        return await SendForAsync(pending, devSilent, ct);
    }

    /// <summary>
    /// Ask about ONE company, whatever its request stamp says — the organizer page's deliberate
    /// "ask again" button. 🔒 Still refuses a company that is not qualifying or has been approved or
    /// declined: those are not questions, and a mail asking one would be answered with confusion.
    /// </summary>
    public async Task<ApprovalMailResult> SendForCompanyAsync(
        int companyId, CancellationToken ct = default)
    {
        var company = await _db.VolumePackageCompanies
            .FirstOrDefaultAsync(c => c.Id == companyId, ct);

        if (company is null || !company.QualifiedNow
            || company.BenefitsApprovedAt is not null || company.DeclinedAt is not null)
            return ApprovalMailResult.Nothing;

        return await SendForAsync(new[] { company }, devSilent: false, ct);
    }

    private async Task<ApprovalMailResult> SendForAsync(
        IReadOnlyList<VolumePackageCompany> companies, bool devSilent, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();

        var sections = new List<string>();
        foreach (var c in companies)
        {
            // The suggestion is computed per company at SEND time, not stored: it is derived from
            // orders that keep arriving, and a stale suggestion in a mail is a name the organizer
            // would have to check anyway.
            var suggestion = await _sweep.SuggestApproverAsync(c.Id, ct);
            sections.Add(BuildCompanySection(c, suggestion));
        }

        var subject = companies.Count == 1
            ? $"ACTION: {companies[0].CustomName} qualifies for the volume package — who approves it?"
            : $"ACTION: {companies.Count} companies qualify for the volume package — who approves them?";

        await _alerts.AlertAsync(
            subject,
            BuildHtml(sections),
            ct,
            // 🔒 NO throttle key — the dedupe is the durable ApprovalRequestedAt stamp, per §879's
            // reasoning: a second, invisible in-memory window would decide the real spacing while
            // the page and the data said something else.
            throttleKey: null,
            recipient: Recipient,
            devSilent: devSilent);

        foreach (var c in companies) c.ApprovalRequestedAt = now;
        await _db.SaveChangesAsync(ct);

        return new ApprovalMailResult(companies.Count, true);
    }

    private string BuildCompanySection(
        VolumePackageCompany c, (string Email, string? Name, int Tickets)? suggestion)
    {
        var sb = new StringBuilder();
        sb.Append("<div style=\"margin:0 0 22px;padding:14px;border:1px solid #e5e7eb;border-radius:8px;\">");
        sb.Append($"<div style=\"font-size:17px;font-weight:700;\">{E(c.CustomName)}</div>");
        sb.Append("<div style=\"margin:6px 0 10px;color:#374151;\">")
          .Append($"<b>{c.LastQualifiedCount}</b> attendees counted — the threshold is ")
          .Append($"{VolumePackageQualificationService.Threshold}.</div>");

        var domains = string.Join(", ", c.DomainList);
        if (domains.Length > 0)
            sb.Append($"<div style=\"color:#6b7280;font-size:13px;\">Domains: {E(domains)}</div>");

        if (suggestion is { } s)
        {
            var who = string.IsNullOrWhiteSpace(s.Name) ? s.Email : $"{s.Name} ({s.Email})";
            sb.Append("<p style=\"margin:12px 0 0;\"><b>Suggested approver:</b> ")
              .Append(E(who))
              .Append($" — brought {s.Tickets} attendee(s), more than any other buyer for this company.</p>");
        }
        else
        {
            // ⚠️ The honest null, same as the page's. A company that qualified purely on shared
            // e-mail domains — freelancers who each paid for themselves — has NO purchaser, and
            // nominating one would put a stranger's name in front of an organizer as a
            // recommendation to approve.
            sb.Append("<p style=\"margin:12px 0 0;\"><b>No suggested approver:</b> nobody bought "
                    + "tickets for this company — the people were found by their e-mail domain. "
                    + "Pick the contact by hand.</p>");
        }

        sb.Append("</div>");
        return sb.ToString();
    }

    private string BuildHtml(IReadOnlyList<string> sections)
    {
        var link = string.IsNullOrEmpty(_hubUrl)
            ? "/Organizer/VolumePackage"
            : $"{_hubUrl}/Organizer/VolumePackage";

        var sb = new StringBuilder();
        sb.Append("<p>These companies have reached the volume-package threshold. Someone has to "
                + "confirm who we deal with before anything is offered to them.</p>");
        foreach (var s in sections) sb.Append(s);
        sb.Append($"<p><a href=\"{E(link)}\" style=\"font-weight:600;\">Approve or choose someone "
                + "else on the Volume Package page</a></p>");
        // 🔑 Said in the mail as well as on the page: a reader who adds up the per-check figures and
        // finds them larger than the total reports a bug otherwise, and the honest answer then
        // arrives after the report instead of before it.
        sb.Append("<p style=\"color:#6b7280;font-size:13px;\">A person is counted once even when "
                + "several routes find them (an order, a coupon and their own e-mail domain), so the "
                + "per-route figures on the page do not add up to the total.</p>");
        sb.Append("<p style=\"color:#6b7280;font-size:13px;\">Nobody at these companies has been "
                + "contacted. This mail asks the organizers, not the company.</p>");
        return sb.ToString();
    }

    private static string E(string? raw) => WebUtility.HtmlEncode(raw ?? string.Empty);
}
