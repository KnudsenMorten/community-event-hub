using CommunityHub.Core.Data;
using CommunityHub.Core.Integrations;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// §1060(l) — the DAILY run that asks the model whether each active session's description is a real
/// description, and stores the answer on the session row.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-11: <i>"you just have to do a daily rerun against ai when you check the
/// active sessions for this requirements. save it as an record on the session row."</i></para>
///
/// <para>🔑 <b>Nothing else calls the model.</b> <c>SoMeApprovalGate</c> READS the stored verdict, so
/// eligibility cannot flicker between sweeps — which matters because §1060 dropped the lead-time
/// window, so the moment a post is eligible it auto-approves and §889.1 publishes it.</para>
///
/// <para>🔒 <b>Deliberately NOT feature-gated.</b> A session whose description is a placeholder must
/// never be announced, and gating the sweep behind a switch would mean turning that switch off makes
/// every unjudged session pass (the gate treats <c>null</c> as "not asked" and does not block). The
/// kill switch that matters already exists one level down: with no model configured the judge
/// returns no verdict, the sweep writes nothing, and the deterministic rules still refuse every
/// certain placeholder.</para>
///
/// <para>Cadence: BASE TICK ONLY (§878) — the real interval is the operator's setting on
/// <c>/Organizer/Jobs</c>, defaulted to daily. Safe to run more often than that: an already-eligible
/// session with unchanged text is skipped without a call.</para>
/// </remarks>
public sealed class SoMeTextEligibilityJob
{
    private readonly CommunityHubDbContext _db;
    private readonly SoMeTextEligibilitySweep _sweep;
    private readonly SoMeTextEligibilityJudge _judge;
    private readonly ILogger<SoMeTextEligibilityJob> _log;

    public SoMeTextEligibilityJob(
        CommunityHubDbContext db, SoMeTextEligibilitySweep sweep,
        SoMeTextEligibilityJudge judge, ILogger<SoMeTextEligibilityJob> log)
    {
        _db = db; _sweep = sweep; _judge = judge; _log = log;
    }

    [Function("SoMeTextEligibilityJob")]
    public async Task Run([TimerTrigger("0 */5 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        var eventId = await _db.Events.Where(e => e.IsActive).Select(e => (int?)e.Id)
            .FirstOrDefaultAsync(ct);
        if (eventId is null) { _log.LogWarning("SoMeTextEligibilityJob: no active event."); return; }

        // INERT, and says so. An unconfigured model is a normal state on DEV, not a fault — but it
        // must be visible, or "no verdicts were ever written" looks like the sweep is broken rather
        // than like it was never able to run (§335: the only symptom is that nothing happens).
        if (!_judge.IsConfigured)
        {
            _log.LogInformation(
                "SoMeTextEligibilityJob: no OpenAI deployment configured — no verdicts formed. "
                + "The deterministic placeholder rules still apply, so a blank or \"TBD\" abstract "
                + "is still refused.");
            return;
        }

        var r = await _sweep.RunAsync(eventId.Value, ct);

        // ⚠️ Reported as its own outcome, never folded into "0 eligible": a sweep that could not
        // reach the model and a sweep that found nothing eligible are different facts.
        if (r.NoVerdict > 0)
        {
            _log.LogWarning(
                "SoMeTextEligibilityJob: {Result} — {NoVerdict} session(s) got NO verdict (the model "
                + "was unreachable or answered unusably); each keeps its previous one.",
                r, r.NoVerdict);
            return;
        }

        _log.LogInformation("SoMeTextEligibilityJob: {Result}.", r);
    }
}
