using System.Text;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CommunityHub.Core.Email;

/// <summary>
/// §203 — emails the organizer ops mailbox (<c>info@expertslive.dk</c>) a DIGEST
/// whenever there is something waiting for an organizer to ACT on: people sitting
/// in the pre-selection queue (prospective volunteers / speakers / media), new
/// Sessionize-synced speakers needing review, and new volunteers — each with a
/// DIRECT link to the relevant queue page so the organizer can go approve.
///
/// Design (matches the operator brief):
///  - DIGEST, not per-item: ONE batched mail listing the categories + counts.
///  - Sends NOTHING when there is nothing pending (zero total ⇒ no mail).
///  - Delivered through <see cref="EngineAlertSender"/> so it is RING-EXEMPT (an
///    ops address is not a ring-gated participant and would otherwise be dropped).
///  - THROTTLED per edition ("pending-approvals-{eventId}") so a daily job — or a
///    burst of triggers — can send at most one within the alert window.
///  - The CALLER gates it behind the outbound-email / digest feature flags (the
///    reminder job only invokes this inside its existing digest-enabled branch),
///    so there is no second gate here.
///
/// Pure + constructor-injected (db + alert sender + branding options for the hub
/// base URL), so it is unit-testable on the EF Core InMemory provider.
/// </summary>
public sealed class PendingApprovalsDigestService
{
    /// <summary>The ops mailbox the pending-approvals digest is sent to (§203).</summary>
    public const string Recipient = "info@expertslive.dk";

    private readonly CommunityHubDbContext _db;
    private readonly EngineAlertSender _alerts;
    private readonly string _hubUrl;

    public PendingApprovalsDigestService(
        CommunityHubDbContext db,
        EngineAlertSender alerts,
        IOptions<EmailTemplateOptions>? branding = null)
    {
        _db = db;
        _alerts = alerts;
        _hubUrl = (branding?.Value.HubUrl ?? string.Empty).TrimEnd('/');
    }

    /// <summary>
    /// The pending-action counts for an edition. A category is "pending" when its
    /// count is &gt; 0; <see cref="Total"/> drives the send/no-send decision.
    /// </summary>
    public sealed record PendingCounts(
        int QueueTotal,
        int Speakers,
        int Volunteers,
        int Media)
    {
        /// <summary>Total rows awaiting organizer review across the queue.</summary>
        public int Total => QueueTotal;
        public bool Any => QueueTotal > 0;
    }

    /// <summary>
    /// Count everything awaiting an organizer's review for an edition: the whole
    /// pre-selection queue (not-yet-Active, non-sponsor) plus the per-source
    /// breakdown (Sessionize speakers / volunteer-form volunteers / media team).
    /// Pure read — never sends or writes.
    /// </summary>
    public async Task<PendingCounts> CountPendingAsync(int eventId, CancellationToken ct = default)
    {
        // Mirror PreselectionQueueService.GetQueueAsync's scope: rows not yet fully
        // activated, excluding sponsors (managed in the sponsor area).
        var pending = _db.Participants.Where(p =>
            p.EventId == eventId
            && p.LifecycleState != ParticipantLifecycleState.Active
            && p.Role != ParticipantRole.Sponsor);

        var queueTotal = await pending.CountAsync(ct);
        var speakers = await pending.CountAsync(p => p.QueueSource == ParticipantQueueSource.SessionizeSync, ct);
        var volunteers = await pending.CountAsync(p => p.QueueSource == ParticipantQueueSource.VolunteerInterestForm, ct);
        var media = await pending.CountAsync(p => p.QueueSource == ParticipantQueueSource.MediaTeamSignup, ct);

        return new PendingCounts(queueTotal, speakers, volunteers, media);
    }

    /// <summary>
    /// Compute the pending counts and, if anything is pending, send ONE batched ops
    /// digest. Returns true when a mail was sent, false when there was nothing
    /// pending (or the throttle suppressed it). Never throws — delivery is
    /// best-effort via <see cref="EngineAlertSender"/>.
    /// </summary>
    public async Task<bool> SendPendingDigestAsync(int eventId, CancellationToken ct = default)
    {
        var counts = await CountPendingAsync(eventId, ct);
        if (!counts.Any) return false; // nothing pending ⇒ send nothing

        var (subject, body) = BuildDigest(counts);
        await _alerts.AlertAsync(
            subject, body, ct,
            throttleKey: $"pending-approvals-{eventId}",
            recipient: Recipient);
        return true;
    }

    /// <summary>
    /// Build the digest subject + HTML body from the counts. Public so a test can
    /// assert the categories, counts and direct queue links without sending.
    /// </summary>
    public (string Subject, string Html) BuildDigest(PendingCounts counts)
    {
        var subject = $"Pending organizer action: {counts.QueueTotal} awaiting review";

        var queueLink = QueueLink(null);
        var sb = new StringBuilder();
        sb.Append("<p>There ")
          .Append(counts.QueueTotal == 1 ? "is" : "are")
          .Append(' ').Append("<strong>").Append(counts.QueueTotal).Append("</strong> ")
          .Append(counts.QueueTotal == 1 ? "person" : "people")
          .Append(" waiting in the pre-selection queue for an organizer to review and approve.</p>");

        sb.Append("<ul>");
        sb.Append(Line("Pre-selection queue (all prospective volunteers, speakers &amp; media)",
            counts.QueueTotal, queueLink));
        if (counts.Speakers > 0)
            sb.Append(Line("New Sessionize-synced speakers needing review",
                counts.Speakers, QueueLink(ParticipantQueueSource.SessionizeSync)));
        if (counts.Volunteers > 0)
            sb.Append(Line("New volunteers",
                counts.Volunteers, QueueLink(ParticipantQueueSource.VolunteerInterestForm)));
        if (counts.Media > 0)
            sb.Append(Line("New media / crew sign-ups",
                counts.Media, QueueLink(ParticipantQueueSource.MediaTeamSignup)));
        sb.Append("</ul>");

        // §500 (operator 2026-07-28): "the reason why a speaker is inactive by default is due to we
        // as organizers have to tell ceh the speakercategory and ring… include this vital info in
        // the email that goes to organisers when new speakers land in pending queue".
        //
        // The digest said WHAT was waiting but never WHY it was held or WHAT unblocks it — so an
        // inactive speaker reads as a fault rather than a decision nobody has taken yet. It is the
        // missing CATEGORY and RING that hold them, and nothing said so at the moment an organizer
        // is actually looking at the queue. Shown only when there ARE speakers waiting, so the
        // explanation never becomes boilerplate people learn to skip.
        if (counts.Speakers > 0)
        {
            sb.Append("<p style=\"background:#eef6ff;border:1px solid #d3e6fb;border-radius:8px;")
              .Append("padding:12px 14px;margin:14px 0;\">")
              .Append("<strong>Why these speakers are inactive:</strong> a Sessionize-synced speaker ")
              .Append("stays inactive until an organizer sets their <strong>speaker category</strong> ")
              .Append("and <strong>ring</strong> &mdash; CEH cannot infer either. Until both are set ")
              .Append("they are held from the Zoho flow, get no reminders and cannot sign in. ")
              .Append("Setting the two activates the speaker automatically.")
              .Append("</p>");
        }

        sb.Append("<p><a href=\"").Append(queueLink).Append("\">Open the pre-selection queue &rarr;</a></p>");

        return (subject, sb.ToString());
    }

    private static string Line(string label, int count, string link) =>
        $"<li>{label}: <strong>{count}</strong> &mdash; <a href=\"{link}\">review</a></li>";

    /// <summary>The absolute (or root-relative when no hub URL is configured) link
    /// to the pre-selection queue, optionally filtered to one inbound source.</summary>
    private string QueueLink(ParticipantQueueSource? source)
    {
        var path = "/Organizer/PreselectionQueue";
        if (source is not null) path += $"?SourceFilter={source.Value}";
        return string.IsNullOrEmpty(_hubUrl) ? path : _hubUrl + path;
    }
}
