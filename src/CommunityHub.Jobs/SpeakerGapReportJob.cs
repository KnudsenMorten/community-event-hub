using CommunityHub.Core.Data;
using CommunityHub.Core.Integrations;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// §575/§586/§623 — mails the organizers what CEH holds that Zoho Backstage does NOT, per speaker.
/// </summary>
/// <remarks>
/// <para>Operator 2026-07-28: *"i am also missing a speaker mail, which shows the gaps between ceh
/// and zoho. for example per larsen has set country + skills"*, and once the API limits were
/// established: *"once it exist in zoho (session, speaker) we must send mail to info@expertslive.dk
/// with things that must be added manually by org team"*.</para>
///
/// <para><b>A mail is the ONLY option here, not a fallback.</b> The v3 speakers API is create-only —
/// PUT, PATCH and POST to <c>/speakers/{id}</c> all answer <c>404 "Please provide valid method"</c>
/// (§584). Nothing can be pushed, so the organizers add it by hand and this tells them exactly what.</para>
///
/// <para><b>Cadence: daily.</b> A speaker fills in their profile once; the gaps change slowly. The
/// report is hash-deduped anyway, so a faster cadence would find the same set and send nothing —
/// spending API calls to discover a value that has not moved.</para>
/// </remarks>
public sealed class SpeakerGapReportJob
{
    private readonly CommunityHubDbContext _db;
    private readonly SpeakerZohoGapReporter _reporter;
    private readonly ILogger<SpeakerGapReportJob> _log;

    public SpeakerGapReportJob(
        CommunityHubDbContext db, SpeakerZohoGapReporter reporter, ILogger<SpeakerGapReportJob> log)
    {
        _db = db; _reporter = reporter; _log = log;
    }

    [Function("SpeakerGapReportJob")]
    public async Task Run(
        // Daily at 06:20 UTC — after the nightly jobs, before the organizers start their day.
        [TimerTrigger("0 20 6 * * *")] TimerInfo timer,
        CancellationToken ct)
    {
        var eventId = await _db.Events.Where(e => e.IsActive).Select(e => (int?)e.Id)
            .FirstOrDefaultAsync(ct);
        if (eventId is null) { _log.LogWarning("SpeakerGapReportJob: no active event."); return; }

        var r = await _reporter.RunAsync(eventId.Value, ct);

        // One honest outcome line per run (§570). "Source unavailable" is reported as itself, never
        // as "no gaps" — an unreadable Zoho is not evidence that everything is fine.
        if (!r.SourceAvailable)
        {
            _log.LogWarning(
                "SpeakerGapReportJob: Zoho speakers unavailable ({Reason}) — nothing compared.",
                r.UnavailableReason);
            return;
        }

        _log.LogInformation(
            "SpeakerGapReportJob: compared {Compared}, {WithGaps} with gaps, mailed {Mailed}.",
            r.Compared, r.WithGaps, r.Mailed);
    }
}
