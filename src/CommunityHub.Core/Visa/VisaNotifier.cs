using System.Net;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Visa;

/// <summary>§1383 — where the "a visa application was sent" notice goes (config section <c>Visa</c>).</summary>
public sealed class VisaNotifyOptions
{
    public const string SectionName = "Visa";

    /// <summary>The organizers' mailbox. Blank turns the notice off.</summary>
    public string NotifyEmailTo { get; set; } = "info@expertslive.dk";
}

/// <summary>
/// §1383 — tells the organizers' mailbox that an applicant sent (or changed) their visa application.
/// </summary>
/// <remarks>
/// <para>Operator 2026-11-07 (reported as a bug): <i>"when someone submits a visa application, a mail must
/// be sent to info@expertslive.dk so the organizers know it is filled out"</i>. This reverses §1374's
/// "no e-mail" for the ORGANIZER side only; the applicant still gets no mail.</para>
/// <para>🔒 <b>Ring-exempt</b>, like <c>feedback-intake</c>: the recipient is a MAILBOX, not a participant in
/// a rollout ring, so a ring could only ever drop the notice. The kill switch and the DEV redirect still
/// apply (they live in the sender).</para>
/// <para>🔒 <b>No passport-grade data in the mail.</b> Name, e-mail, role and a link to the organizer page;
/// for an update, the NAMES of the fields that changed, never their values. The answers stay in the hub.</para>
/// <para>Best-effort: a mail failure is logged and never fails the applicant's save.</para>
/// </remarks>
public sealed class VisaNotifier
{
    /// <summary>The registry key (<see cref="EmailTemplateCatalog"/>) and template file name.</summary>
    public const string MailKey = "visa-application-submitted";

    private readonly CommunityHubDbContext _db;
    private readonly EmailTemplateProvider _templates;
    private readonly IEmailSender _email;
    private readonly IEmailContextAccessor? _ctx;
    private readonly VisaNotifyOptions _options;
    private readonly ILogger<VisaNotifier>? _log;

    public VisaNotifier(
        CommunityHubDbContext db, EmailTemplateProvider templates, IEmailSender email,
        IEmailContextAccessor? ctx = null, VisaNotifyOptions? options = null, ILogger<VisaNotifier>? log = null)
    {
        _db = db;
        _templates = templates;
        _email = email;
        _ctx = ctx;
        _options = options ?? new VisaNotifyOptions();
        _log = log;
    }

    /// <summary>
    /// Send the notice. <paramref name="changedFields"/> empty ⇒ the first submit ("submitted");
    /// otherwise an edit of an already-sent application ("updated", naming the changed fields).
    /// Returns true when a mail was handed to the sender.
    /// </summary>
    public async Task<bool> NotifyAsync(int eventId, int participantId, IReadOnlyList<string> changedFields, CancellationToken ct = default)
    {
        var to = _options.NotifyEmailTo?.Trim();
        if (string.IsNullOrWhiteSpace(to)) return false;

        try
        {
            var person = await _db.Participants.AsNoTracking()
                .Where(p => p.Id == participantId && p.EventId == eventId)
                .Select(p => new { p.FullName, p.Email, p.Role })
                .FirstOrDefaultAsync(ct);
            if (person is null) return false;
            var eventName = await _db.Events.AsNoTracking().Where(e => e.Id == eventId)
                .Select(e => e.DisplayName).FirstOrDefaultAsync(ct);

            var updated = changedFields.Count > 0;
            using var _ = _ctx?.Set(new EmailContext(MailKey, eventId, null, null, TemplateName: MailKey, RingExempt: true));
            var t = _templates.NewTokenSet();
            if (!string.IsNullOrWhiteSpace(eventName)) t["eventDisplayName"] = eventName;
            t["state"] = updated ? "updated" : "submitted";
            t["applicantName"] = string.IsNullOrWhiteSpace(person.FullName) ? person.Email : person.FullName.Trim();
            t["applicantEmail"] = person.Email;
            t["applicantRole"] = person.Role.ToString();
            t["participantId"] = participantId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            t["changesHtml"] = updated
                ? "<p style=\"margin:0 0 8px;\">Changed since the last version:</p><ul style=\"margin:0 0 16px;\">"
                  + string.Concat(changedFields.Select(f => $"<li>{WebUtility.HtmlEncode(f)}</li>")) + "</ul>"
                : "<p style=\"margin:0 0 16px;\">This is the first time they sent it. Copy it into the invitation on "
                  + "nyidanmark.dk, then enter the application ID on the organizer page.</p>";
            var r = _templates.Render(MailKey, t);
            EmailReplyTo? replyTo = string.IsNullOrWhiteSpace(person.Email) ? null : new EmailReplyTo(person.Email, person.FullName);
            await _email.SendAsync(to, r.Subject, r.HtmlBody, replyTo, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.LogWarning(ex, "§1383 visa notice for participant {Id} to {To} failed (the application is saved).", participantId, to);
            return false;
        }
    }
}
