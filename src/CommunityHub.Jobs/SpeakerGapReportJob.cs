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
    /// <summary>
    /// §871 — the kill switch. Operator 2026-08-05: "it could be nice to have a button to DISABLE
    /// this one, as i expect us to do that soon".
    /// </summary>
    /// <remarks>
    /// ⚠️ §623 deliberately left this job UN-GATED, reasoning that a missing speaker detail must
    /// surface regardless of any feature switch. <b>A later instruction reverses that</b>, and the
    /// reasoning changed with the facts: most of the report is <c>Country</c>, which Backstage does
    /// not return at all (§871.1), so the mail can never stop asking for it. An alert that cannot be
    /// satisfied is one he learns to ignore, which is worse than no alert.
    /// </remarks>
    public const string FeatureKey = "speaker-gap-report";

    private readonly CommunityHubDbContext _db;
    private readonly SpeakerZohoGapReporter _reporter;
    private readonly CommunityHub.Core.Settings.FeatureGateService _gate;
    private readonly ILogger<SpeakerGapReportJob> _log;

    public SpeakerGapReportJob(
        CommunityHubDbContext db, SpeakerZohoGapReporter reporter,
        CommunityHub.Core.Settings.FeatureGateService gate, ILogger<SpeakerGapReportJob> log)
    {
        _db = db; _reporter = reporter; _gate = gate; _log = log;
    }

    [Function("SpeakerGapReportJob")]
    public async Task Run(
        // §878 — BASE TICK ONLY; the cadence is the operator's interval on /Organizer/Jobs.
        // 🔒 Safe to run often: the report is HASH-DEDUPED, so an unchanged gap set sends nothing.
        [TimerTrigger("0 */5 * * * *")] TimerInfo timer,
        CancellationToken ct)
    {
        var eventId = await _db.Events.Where(e => e.IsActive).Select(e => (int?)e.Id)
            .FirstOrDefaultAsync(ct);
        if (eventId is null) { _log.LogWarning("SpeakerGapReportJob: no active event."); return; }

        // §871 — his kill switch, checked before any Zoho call so switching it off costs nothing.
        if (!await _gate.IsFeatureEnabledAsync(FeatureKey, eventId.Value, ct))
        {
            _log.LogInformation("SpeakerGapReportJob: feature '{Key}' is off — no gap mail sent.", FeatureKey);
            return;
        }

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
