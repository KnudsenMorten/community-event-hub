using System.Net;
using System.Text;
using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Email;

/// <summary>
/// §1077.9 — THE POST-EVENT MAIL: thank the company, point them at the pictures, and invite them to
/// tag us.
/// </summary>
/// <remarks>
/// <para>The operator's own wording (2026-08-11): <i>"Thank you again for your amazing support at
/// #ELDK26 and for qualifying for the volume package benefits, including the group photo — it's
/// yours to use freely. You're also very welcome to include photos from the event, available
/// here … Feel free to tag us — when you do, you'll gain exposure to our approximately 30K followers
/// on LinkedIn … We hope for your support next year 😊"</i></para>
///
/// <para>🔒 <b>It is sent by a person, never by a job.</b> "The event is over" is not a date the hub
/// should infer — the pictures have to be uploaded and the link has to work first, and nothing here
/// can check that. An organizer presses the button when the gallery is live.</para>
///
/// <para>🔑 <b>The link and the profiles are CONFIG</b> (<see cref="PostEventConfig"/>): the picture
/// host is per edition and the five names change. A missing link means the paragraph is left out
/// rather than a mail promising pictures at a URL that 404s.</para>
///
/// <para>⚠️ <b>Ring-governed</b>, like every mail that reaches a customer.</para>
/// </remarks>
public sealed class VolumePackagePostEventMailService
{
    /// <summary>🔒 Default OFF. It reaches the company, so it gets its own switch.</summary>
    public const string FeatureKey = "volume-package-post-event-mail";

    private readonly CommunityHubDbContext _db;
    private readonly IEmailSender _email;
    private readonly IEmailContextAccessor _context;
    private readonly EventEditionConfigLoader _config;
    private readonly EventConfigOptions _configOptions;
    private readonly TimeProvider _clock;

    public VolumePackagePostEventMailService(
        CommunityHubDbContext db, IEmailSender email, IEmailContextAccessor context,
        EventEditionConfigLoader config, EventConfigOptions configOptions,
        TimeProvider? clock = null)
    {
        _db = db;
        _email = email;
        _context = context;
        _config = config;
        _configOptions = configOptions;
        _clock = clock ?? TimeProvider.System;
    }

    public sealed record PostEventResult(bool Sent, string? Recipient, string? Problem)
    {
        public static PostEventResult No(string problem) => new(false, null, problem);
    }

    /// <summary>
    /// Send it to one company's approver. The CALLER checks the feature switch, so the refusal can
    /// be explained on the page rather than guessed at.
    /// </summary>
    public async Task<PostEventResult> SendAsync(int companyId, CancellationToken ct = default)
    {
        var company = await _db.VolumePackageCompanies
            .FirstOrDefaultAsync(c => c.Id == companyId, ct);

        if (company is null) return PostEventResult.No("That company no longer exists.");

        if (string.IsNullOrWhiteSpace(company.ApproverEmail))
            return PostEventResult.No("There is no approver to write to.");

        // 🔴 A company that DECLINED took no benefits, so thanking them for "qualifying for the
        // volume package benefits, including the group photo" would be thanking them for something
        // they turned down.
        if (company.DeclinedAt is not null)
            return PostEventResult.No(
                "This company declined the volume package, so the thank-you would be thanking them "
                + "for something they turned down.");

        if (company.PostEventMailSentAt is not null)
            return PostEventResult.No(
                $"Already sent on {company.PostEventMailSentAt.Value.UtcDateTime:d MMM yyyy}. "
                + "Sending it twice reads as an automated mailing rather than a thank-you.");

        var ev = await _db.Events.FirstOrDefaultAsync(e => e.Id == company.EventId, ct);
        var cfg = LoadPostEvent();

        using var _ = _context.Set(new EmailContext(
            Category: "volume-package-post-event",
            EventId: company.EventId,
            RecipientName: company.ApproverName,
            FeatureKey: FeatureKey));

        await _email.SendAsync(
            company.ApproverEmail!,
            $"Thank you for your support at {ev?.DisplayName}",
            BuildHtml(company, ev?.DisplayName, cfg),
            ct);

        company.PostEventMailSentAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);

        return new PostEventResult(true, company.ApproverEmail, null);
    }

    /// <summary>The edition's post-event block, or an empty one when it is not configured.</summary>
    public PostEventConfig LoadPostEvent()
    {
        try
        {
            var path = _configOptions.EventConfigPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return new PostEventConfig();
            return _config.Load(path).PostEvent ?? new PostEventConfig();
        }
        catch
        {
            // ⚠️ A missing or malformed config must not stop a thank-you: the mail simply loses its
            // optional paragraphs rather than throwing at an organizer who pressed a button.
            return new PostEventConfig();
        }
    }

    private static string BuildHtml(VolumePackageCompany company, string? edition, PostEventConfig cfg)
    {
        var name = string.IsNullOrWhiteSpace(company.ApproverName)
            ? string.Empty
            : " " + E(company.ApproverName.Split(' ')[0]);
        var tag = string.IsNullOrWhiteSpace(edition) ? "the event" : E(edition);

        var sb = new StringBuilder();
        sb.Append($"<p>Dear{name},</p>");
        sb.Append($"<p>Thank you again for your amazing support at <strong>{tag}</strong> and for "
                + "qualifying for the volume package benefits, including the group photo — "
                + "it&rsquo;s yours to use freely.</p>");

        // 🔑 The pictures paragraph only appears when there is somewhere to send them.
        if (!string.IsNullOrWhiteSpace(cfg.PicturesUrl))
        {
            sb.Append("<p>You&rsquo;re also very welcome to include photos from the event, "
                    + $"available here:<br /><a href=\"{E(cfg.PicturesUrl)}\">{E(cfg.PicturesUrl)}</a></p>");
        }

        if (cfg.LinkedIn.Count > 0)
        {
            var reach = string.IsNullOrWhiteSpace(cfg.LinkedInFollowers)
                ? "you&rsquo;ll gain exposure on LinkedIn"
                : $"you&rsquo;ll gain exposure to our {E(cfg.LinkedInFollowers)} followers on LinkedIn";

            sb.Append($"<p>Feel free to tag us — when you do, {reach}.</p><ul>");
            foreach (var p in cfg.LinkedIn.Where(p => !string.IsNullOrWhiteSpace(p.Name)))
            {
                sb.Append(string.IsNullOrWhiteSpace(p.Url)
                    ? $"<li>{E(p.Name)}</li>"
                    : $"<li><a href=\"{E(p.Url)}\">{E(p.Name)}</a></li>");
            }
            sb.Append("</ul>");
        }

        sb.Append("<p>We hope for your support next year 😊</p>");
        return sb.ToString();
    }

    private static string E(string? raw) => WebUtility.HtmlEncode(raw ?? string.Empty);
}
