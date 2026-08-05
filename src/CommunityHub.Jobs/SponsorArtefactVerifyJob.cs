using CommunityHub.Core.Data;
using CommunityHub.Uploads;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// §598 — verifies that every artefact CEH believes was uploaded is still in SharePoint, and stamps
/// the ones a COMPLETE read confirms are gone so their task reverts to not-done.
/// </summary>
/// <remarks>
/// <para><b>Why it exists.</b> Operator 2026-07-28: *"i have manually deleted these files on
/// sharepoint, but ceh still remember them."* A stored path was treated as proof of upload forever,
/// so the Get Started "Logos &amp; artwork" step read DONE with nothing behind it and the reminder
/// engine believed it.</para>
///
/// <para><b>Why a JOB and not a page check.</b> §443 is explicit — never call a third-party system
/// while rendering (*"it must newer pull data from company manager (cm) in the admin interface"*);
/// doing so made five organizer pages take 6–8 s warm. Verification therefore runs on a timer and
/// the pages read the stamped result.</para>
///
/// <para><b>Cadence: daily.</b> Files do not vanish often, the check costs one Graph listing per
/// artefact kind, and a deletion is not urgent — the task reverting a few hours later is fine. An
/// aggressive cadence would spend API budget to detect something rare.</para>
///
/// <para>🔒 The safety rule lives in <see cref="SponsorArtefactVerifier"/>: a row is stamped ONLY
/// when the folder listing SUCCEEDED and simply lacked the file. Any throw is UNKNOWN, retried, then
/// logged loudly with nothing changed — because an empty/failed read is indistinguishable from
/// "everything was deleted", and acting on one would strip every sponsor at once.</para>
/// </remarks>
public sealed class SponsorArtefactVerifyJob
{
    private readonly CommunityHubDbContext _db;
    private readonly SponsorArtefactVerifier _verifier;
    private readonly ILogger<SponsorArtefactVerifyJob> _log;

    public SponsorArtefactVerifyJob(
        CommunityHubDbContext db, SponsorArtefactVerifier verifier,
        ILogger<SponsorArtefactVerifyJob> log)
    {
        _db = db; _verifier = verifier; _log = log;
    }

    [Function("SponsorArtefactVerifyJob")]
    public async Task Run(
        // §707.17 (operator 2026-07-30) — every 15 minutes; was daily at 05:10 UTC.
        //
        // 🔑 His reason, and it is the right one: this job is the ONLY thing that notices a file
        // deleted DIRECTLY in SharePoint. At a daily cadence the portal could show a sponsor task
        // as satisfied by an artefact that no longer exists, for up to 24 hours — *"otherwise will
        // the portal show wrong file if i deleted it manually"*. It is a read-and-compare against
        // SharePoint, so the added frequency costs listing calls, not writes.
        //
        // §869.3 — BASE TICK ONLY now. His 15 minutes became the §510 default interval instead of
        // a hard-coded cron, so the window he was reasoning about above is his to set.
        [TimerTrigger("0 */5 * * * *")] TimerInfo timer,
        CancellationToken ct)
    {
        var eventId = await _db.Events.Where(e => e.IsActive).Select(e => (int?)e.Id)
            .FirstOrDefaultAsync(ct);
        if (eventId is null)
        {
            _log.LogWarning("SponsorArtefactVerifyJob: no active event.");
            return;
        }

        var r = await _verifier.VerifyAsync(eventId.Value, ct);

        // One honest outcome line per run (§570 — a job that says nothing is indistinguishable from
        // a job that is broken). Note that "unreadable" is reported but is NOT a failure count: it
        // means we declined to guess.
        _log.LogInformation(
            "SponsorArtefactVerifyJob: checked {Checked}, marked missing {Missing}, restored {Restored}, "
            + "folders we could not read {Unreadable}{Note}.",
            r.Checked, r.MarkedMissing, r.Restored, r.FoldersUnreadable,
            string.IsNullOrWhiteSpace(r.Note) ? "" : $" — {r.Note}");
    }
}
