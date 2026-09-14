using System.Net;
using System.Text;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CommunityHub.Core.Email;

/// <summary>
/// §1077 stage 3 — THE FIRST MAIL THIS FEATURE SENDS TO SOMEBODY OUTSIDE THE ORGANIZING TEAM: the
/// invitation carrying the wizard link, to the approver an organizer confirmed in stage 2.
/// </summary>
/// <remarks>
/// <para>🔴 <b>This is the line the operator's opening instruction was drawn around</b> — <i>"this
/// change includes critical adjustment, so it is important that this change gets tested very
/// detailed in DEV, and then approved before going into PROD"</i>. Stages 1 and 2 stayed inside the
/// building (a page, and a mail to <c>info@</c>); this one reaches a customer's inbox. ⇒ It ships
/// behind <see cref="FeatureKey"/>, <b>default OFF</b>, and it is only ever sent by a deliberate
/// organizer click — there is no job that decides to send it.</para>
///
/// <para>🔒 <b>One recipient, and it is never guessed.</b> The mail goes to
/// <see cref="VolumePackageCompany.ApproverEmail"/>, which a human confirmed on the organizer page.
/// A company with no approver is refused rather than mailed at whoever the suggestion happened to
/// name — the difference between "we decided" and "the query returned a row".</para>
///
/// <para>⚠️ <b>It carries a link that is a credential.</b> So it names the company, says what the
/// link is for, and says plainly that it may be forwarded INSIDE the company but not published —
/// people handle a secret better when they are told it is one.</para>
/// </remarks>
public sealed class VolumePackageInviteMailService
{
    /// <summary>
    /// 🔒 Default OFF. Not a formality: this is the switch between "the feature exists" and "a
    /// customer receives mail from it".
    /// </summary>
    public const string FeatureKey = "volume-package-invite-mail";

    private readonly CommunityHubDbContext _db;
    private readonly VolumePackageWizardService _wizard;
    private readonly IEmailSender _email;
    private readonly IEmailContextAccessor _context;
    private readonly TimeProvider _clock;
    private readonly string _hubUrl;

    /// <summary>Optional: supplies the 1-day sentence. Null ⇒ the mail simply omits it.</summary>
    private readonly VolumePackageGroupPhotoService? _groupPhoto;

    public VolumePackageInviteMailService(
        CommunityHubDbContext db,
        VolumePackageWizardService wizard,
        IEmailSender email,
        IEmailContextAccessor context,
        TimeProvider? clock = null,
        IOptions<EmailTemplateOptions>? branding = null,
        VolumePackageGroupPhotoService? groupPhoto = null)
    {
        _groupPhoto = groupPhoto;
        _db = db;
        _wizard = wizard;
        _email = email;
        _context = context;
        _clock = clock ?? TimeProvider.System;
        _hubUrl = (branding?.Value.HubUrl ?? string.Empty).TrimEnd('/');
    }

    /// <summary>What one invitation attempt did, and why not when it did nothing.</summary>
    public sealed record InviteResult(bool Sent, string? Recipient, string? Problem)
    {
        public static InviteResult No(string problem) => new(false, null, problem);
    }

    /// <summary>
    /// Send the invitation for one company, minting a link if it has none.
    /// </summary>
    /// <remarks>
    /// 🔑 <b>The CALLER checks the feature switch.</b> Both call sites are organizer actions that
    /// must explain the refusal on the page ("the invitation mail is switched off"), and a service
    /// that silently returned "not sent" would make that message someone else's guess.
    /// </remarks>
    public async Task<InviteResult> SendAsync(int companyId, CancellationToken ct = default)
    {
        var company = await _db.VolumePackageCompanies
            .FirstOrDefaultAsync(c => c.Id == companyId, ct);

        if (company is null) return InviteResult.No("That company no longer exists.");

        if (string.IsNullOrWhiteSpace(company.ApproverEmail))
            return InviteResult.No(
                "There is no approver yet — approve the benefits first, so the invitation goes to a "
                + "person somebody chose rather than to whoever the suggestion named.");

        if (company.DeclinedAt is not null)
            return InviteResult.No(
                "This company has declined. Inviting them again would be asking a question they "
                + "already answered.");

        var now = _clock.GetUtcNow();
        if (!company.WizardTokenIsActive(now))
        {
            // No link, or a dead one: mint a fresh one rather than mailing a URL that 404s.
            await _wizard.IssueTokenAsync(company.Id, ct: ct);
            company = await _db.VolumePackageCompanies.FirstAsync(c => c.Id == companyId, ct);
        }

        var link = LinkFor(company);
        var subject = $"{company.CustomName}: your volume package at the event";

        // §1077.8 — say the day when their tickets already decide it. Computed from their own
        // attendees, so the sentence cannot contradict the plan the organizer will make.
        string? photoDay = null;
        if (_groupPhoto is not null)
        {
            var hint = await _groupPhoto.DayHintAsync(company.Id, ct);
            if (!hint.PreDayPossible && hint.MainDayPossible)
                photoDay = "Since all of your tickets are 1-day tickets, the photo would take place "
                         + "on the main day.";
        }

        // 🔒 RING-GOVERNED, deliberately. This reaches a real person at a customer, so it goes
        // through the ordinary participant mail path where the rings and the kill switch apply —
        // NOT through EngineAlertSender's ring-exempt channel, which exists for internal ops mail
        // that would otherwise be dropped. If the rings are not yet open, this must not send.
        using var _ = _context.Set(new EmailContext(
            Category: "volume-package-invite",
            EventId: company.EventId,
            RecipientName: company.ApproverName,
            FeatureKey: FeatureKey));

        await _email.SendAsync(company.ApproverEmail!, subject, BuildHtml(company, link, photoDay), ct);

        company.WizardInvitedAt = now;
        await _db.SaveChangesAsync(ct);

        return new InviteResult(true, company.ApproverEmail, null);
    }

    /// <summary>The wizard URL for a company, or an empty string when it has no live link.</summary>
    public string LinkFor(VolumePackageCompany company)
    {
        if (string.IsNullOrWhiteSpace(company.WizardToken)) return string.Empty;
        var root = string.IsNullOrEmpty(_hubUrl) ? string.Empty : _hubUrl;
        return $"{root}/volume-package/{company.WizardToken}";
    }

    /// <param name="photoDay">
    /// §1077.8 — the day their photo would fall on, when we already know it must be the main day
    /// because every one of their people holds a 1-day ticket. His own sentence to a customer:
    /// <i>"Since all of your tickets are 1-day tickets, this would take place on the main day,
    /// February 25."</i>
    /// </param>
    private static string BuildHtml(VolumePackageCompany company, string link, string? photoDay)
    {
        var sb = new StringBuilder();
        sb.Append($"<p>Hi{(string.IsNullOrWhiteSpace(company.ApproverName) ? string.Empty : " " + E(company.ApproverName.Split(' ')[0]))},</p>");
        // His wording (2026-08-11), kept close to the original because it is warmer than mine and it
        // is what his customers have heard before.
        sb.Append($"<p>Thank you for <strong>{E(company.CustomName)}</strong>&rsquo;s registration "
                + "for the event.</p>");
        sb.Append($"<p>As part of the fact that you have purchased ten or more tickets, your company "
                + "has qualified for the <strong>volume package benefits</strong>: a mention in the "
                + "keynote, an announcement on social media, and a group photo during the event — "
                + "all participants from your company are invited into the picture.</p>");
        sb.Append("<p>We will send the photo to you afterwards, and you are free to use it however "
                + "you like. Please consider this an offer — completely optional and 100% your "
                + "choice.</p>");

        // 🔴 "we preassign" · "it is not able for them to choose" (operator 2026-08-11). His 2026
        // copy said "several time slots to choose from (first come, first served)"; the hub assigns
        // the slot now, so that sentence would invite a reply nobody can grant.
        sb.Append("<p>Time slots are assigned by us — you will receive your company&rsquo;s time "
                + "with a calendar invitation you can forward to your colleagues.</p>");

        if (!string.IsNullOrWhiteSpace(photoDay))
        {
            sb.Append($"<p>{photoDay}</p>");
        }

        sb.Append("<p>The page below asks four short questions: which of the benefits you want, who "
                + "at your company coordinates the photo, your logo, and your LinkedIn page.</p>");
        sb.Append($"<p><a href=\"{E(link)}\" style=\"font-weight:600;\">Open your volume package page</a></p>");
        // ⚠️ The link IS the credential. Saying so is the cheapest protection it has.
        sb.Append("<p style=\"color:#6b7280;font-size:13px;\">The link is personal to your company — "
                + "pass it around your own team as you need, but please do not publish it. It stops "
                + "working after the event.</p>");
        sb.Append("<p style=\"color:#6b7280;font-size:13px;\">If you would rather not take part at "
                + "all, the same page has a “we do not want to participate” option, and we will stop "
                + "asking.</p>");
        return sb.ToString();
    }

    private static string E(string? raw) => WebUtility.HtmlEncode(raw ?? string.Empty);
}
