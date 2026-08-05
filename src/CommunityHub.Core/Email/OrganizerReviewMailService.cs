using System.Text;
using CommunityHub.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CommunityHub.Core.Email;

/// <summary>
/// §879 — the two mails that tell the organizer somebody is waiting. <b>They are two, not one</b>,
/// and this class exists because they used to be one.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-05: <i>"i see the problem i have experiencing multiple times today, which
/// is speaker held in the queue. i need that to run every 10 min and be notified after 10 min.
/// volunteers awaiting review should go out weekly. we need to split these as they are very
/// different"</i>.</para>
///
/// <para>🔑 <b>Why one mail was wrong.</b> §203/§765 carried two unrelated populations at one
/// cadence: a <b>speaker stuck in the Zoho queue</b> — invisible, blocking, and the thing that cost
/// him hours on 2026-08-05 (§876) — and <b>volunteers awaiting review</b>, a housekeeping list
/// nobody needs before next week. A single cadence had to be wrong for one of them, and it was
/// wrong for the one that mattered: daily, so a held speaker could sit most of a day unmentioned.
/// They were never even one queue in the data (<c>PreselectionQueueService.QueueRows</c> filters
/// <c>Role == Volunteer</c>, so a speaker cannot appear in it — §756), which is the clue that
/// merging them was a presentation choice rather than a fact about the system.</para>
///
/// <para>🔒 <b>"Digest" is banned vocabulary</b> (§879.2, and §595 before it: <i>"hate that word
/// digest, dont use it and dont understand it"</i>). Both mails are named after what they DO. The
/// <c>digest-emails</c> FeatureKey is a live DB row and therefore UNCHANGED — per §595/§642 the key
/// stays and only the display moves, or the operator's switch silently detaches from its feature.</para>
///
/// <para>The speaker half's BODY is not built here: it is
/// <see cref="CommunityHub.Core.Organizer.SpeakerApprovalService.BuildPendingMailHtml"/>, the same
/// builder the import used, so the §877 one-click approve-all buttons are in the 10-minute mail too.
/// Two builders for one mail would have drifted the moment either changed.</para>
/// </remarks>
public sealed class OrganizerReviewMailService
{
    /// <summary>
    /// The ops mailbox both mails go to (§203, and §874: <i>"this mail must go to
    /// info@expertslive.dk"</i> — one person on holiday must not stall a speaker's Zoho flow).
    /// </summary>
    public const string Recipient = "info@expertslive.dk";

    private readonly CommunityHubDbContext _db;
    private readonly EngineAlertSender _alerts;
    private readonly string _hubUrl;
    private readonly CommunityHub.Core.Organizer.SpeakerApprovalService? _speakerApproval;

    public OrganizerReviewMailService(
        CommunityHubDbContext db,
        EngineAlertSender alerts,
        IOptions<EmailTemplateOptions>? branding = null,
        CommunityHub.Core.Organizer.SpeakerApprovalService? speakerApproval = null)
    {
        _db = db;
        _alerts = alerts;
        _hubUrl = (branding?.Value.HubUrl ?? string.Empty).TrimEnd('/');
        _speakerApproval = speakerApproval;
    }

    // ---------------------------------------------------------------------
    // Speakers held from the Zoho flow — every 10 minutes, once per CHANGE
    // ---------------------------------------------------------------------

    /// <summary>What a held-speaker pass found: how many, and the fingerprint of WHO.</summary>
    /// <param name="Hash">
    /// Null when nothing is held. Otherwise the identity of this exact set — see
    /// <see cref="HashOf"/>. The caller compares it with <c>JobRunState.LastContentHash</c> and mails
    /// only on a change, which is what lets the job run every 10 minutes without becoming noise.
    /// </param>
    public sealed record HeldSpeakers(int Count, string? Hash, string? Html)
    {
        public bool Any => Count > 0;
    }

    /// <summary>
    /// The speakers currently held from the Zoho flow, with the mail body already rendered.
    /// Pure read — never sends.
    /// </summary>
    public async Task<HeldSpeakers> HeldSpeakersAsync(int eventId, CancellationToken ct = default)
    {
        if (_speakerApproval is null) return new HeldSpeakers(0, null, null);

        var pending = await _speakerApproval.PendingAsync(eventId, ct);
        if (pending.Speakers.Count == 0) return new HeldSpeakers(0, null, null);

        var baseUrl = string.IsNullOrEmpty(_hubUrl) ? "https://localhost" : _hubUrl;
        var html = CommunityHub.Core.Organizer.SpeakerApprovalService
            .BuildPendingMailHtml(pending, baseUrl);

        return new HeldSpeakers(pending.Speakers.Count, HashOf(pending), html);
    }

    /// <summary>
    /// 🔑 §879 — the fingerprint of a held-speaker SET: <b>who</b> is held and <b>why</b>.
    /// </summary>
    /// <remarks>
    /// <para>The blockers are part of the identity on purpose. A speaker who goes from "no category;
    /// inactive" to "no category" alone has CHANGED — someone acted, and the remaining ask is
    /// different — so that is worth one mail. A pass where nothing moved produces the same string
    /// and stays silent.</para>
    ///
    /// <para>Ordered by participant id so the hash cannot flip on query-order alone, which would
    /// turn "mail on change" back into "mail every pass" in the least obvious way possible.</para>
    /// </remarks>
    public static string HashOf(CommunityHub.Core.Organizer.SpeakerApprovalService.PendingResult pending)
    {
        var payload = string.Join("\n", pending.Speakers
            .OrderBy(s => s.ParticipantId)
            .Select(s => $"{s.ParticipantId}|{string.Join(",", s.Blockers)}"));
        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(payload)))[..32];
    }

    /// <summary>
    /// Mail the held-speaker list to the ops mailbox. The CALLER decides whether anything changed
    /// (it owns the <c>JobRunState</c> hash); this just sends what it is handed.
    /// </summary>
    public async Task SendHeldSpeakersAsync(HeldSpeakers held, CancellationToken ct = default)
    {
        if (!held.Any || held.Html is null) return;

        await _alerts.AlertAsync(
            $"ACTION: {held.Count} speaker(s) held from the Zoho flow [ELDK27]",
            held.Html, ct,
            // 🔒 NO throttle key. The dedup for this mail is the durable content hash the job holds;
            // a second, invisible in-memory window would be the §765 two-switch trap all over again
            // — the operator would see "every 10 minutes" on the Jobs page while something else
            // decided the real spacing.
            throttleKey: null,
            recipient: Recipient,
            // §752.9 — DEV-silent: on DEV these are imported test speakers, so the mail asks a human
            // to do work that does not exist. In PROD it is a real queue with a real deadline.
            devSilent: true);
    }

    // ---------------------------------------------------------------------
    // Volunteers awaiting review — weekly
    // ---------------------------------------------------------------------

    /// <summary>
    /// How many volunteers are sitting in the pre-selection queue awaiting review.
    /// </summary>
    /// <remarks>
    /// 🔒 §759 — COUNTED FROM THE PAGE'S OWN QUERY, never a hand-copied WHERE clause. The two
    /// drifted once and he received <i>"10 awaiting review"</i> over a queue he opened and found
    /// EMPTY. A mail that names a queue the organizer finds empty teaches him to ignore the mail.
    /// </remarks>
    public Task<int> VolunteersAwaitingAsync(int eventId, CancellationToken ct = default) =>
        CommunityHub.Core.Organizer.PreselectionQueueService
            .QueueRows(_db.Participants, eventId)
            .CountAsync(ct);

    /// <summary>
    /// Mail the weekly volunteer-review list. Sends nothing when the queue is empty (§302).
    /// Returns true when a mail was sent.
    /// </summary>
    public async Task<bool> SendVolunteersAwaitingAsync(int count, CancellationToken ct = default)
    {
        if (count <= 0) return false;

        var link = string.IsNullOrEmpty(_hubUrl)
            ? "/Organizer/PreselectionQueue"
            : _hubUrl + "/Organizer/PreselectionQueue";

        var sb = new StringBuilder();
        sb.Append("<p>There ").Append(count == 1 ? "is" : "are")
          .Append(" <strong>").Append(count).Append("</strong> ")
          .Append(count == 1 ? "volunteer" : "volunteers")
          .Append(" waiting for an organizer to review.</p>");
        // 🔑 Says what this mail is NOT, because the split is the whole point: he must be able to
        // tell at a glance that a missing speaker is not hiding in here.
        sb.Append("<p style=\"color:#4b5563;font-size:13px;\">This is the weekly volunteer list only. ")
          .Append("Speakers held from the Zoho flow are a separate mail and arrive within 10 minutes ")
          .Append("of being held — they are never carried here.</p>");
        sb.Append("<p><a href=\"").Append(link).Append("\">Open the pre-selection queue &rarr;</a></p>");

        await _alerts.AlertAsync(
            $"Volunteers awaiting review: {count} [ELDK27]",
            sb.ToString(), ct,
            throttleKey: null,
            recipient: Recipient,
            // §752.9 — DEV-silent for the same reason as the speaker half: a queue of test records.
            devSilent: true);
        return true;
    }
}
