using CommunityHub.Core.Data;
using CommunityHub.Core.Evaluation;
using CommunityHub.Core.Integrations.Graphics;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Settings;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// §750 C7 — every 5 minutes, publish and notify the session reports that have gone quiet
/// (operator 2026-07-31: <i>"wire the job to run every 5 min"</i>).
/// </summary>
/// <remarks>
/// <para>🔑 <b>Why 5 minutes is the right cadence for a 30-minute debounce.</b> The tick is not what
/// decides when a report goes out — <see cref="EvaluationReportDebounceService.QuietMinutes"/> is.
/// The tick only decides how long AFTER a session settles we notice, so it is the upper bound on
/// lateness, not a second timing rule. 5 minutes keeps that bound small and each pass is cheap
/// (one version derivation per session, and almost always zero work).</para>
///
/// <para>🔒 <b>Overlapping passes are safe.</b> Publishing compares the derived version against the
/// stored one, so a session already published this cycle is skipped rather than re-mailed. That is
/// what makes a frequent tick harmless.</para>
///
/// <para>⚠️ <b>THREE switches must ALL be on before this sends anything</b>, and each of them can be
/// off while everything looks healthy — the <c>[[ceh-two-switch-trap]]</c> shape, with one extra.
/// So every early return REPORTS WHICH ONE, rather than returning quietly:</para>
/// <list type="number">
///   <item>the <c>session-eval-email</c> feature switch (ships <c>DefaultEnabled: false</c>);</item>
///   <item><c>Graphics:SharePoint:SessionEvalPdfFolderPath</c> — with no folder there is nowhere to
///         publish, and the publisher returns false for every session;</item>
///   <item>the mail's RING ROW, which is data and does not travel with a deploy. That one this job
///         cannot see — a send simply reaches nobody — which is why it is called out here for
///         whoever reads this file next.</item>
/// </list>
/// </remarks>
public sealed class EvaluationReportPublishJob
{
    private readonly CommunityHubDbContext _db;
    private readonly EvaluationReportDebounceService _debounce;
    private readonly SessionEvalPdfService _pdfStore;
    private readonly FeatureGateService _gate;
    private readonly ILogger<EvaluationReportPublishJob> _log;
    private readonly CommunityHub.Core.Diagnostics.JobActivityReporter? _activity;

    // §783.8 — the QR half: the artefact publisher, the token minter, the QR folder, and the hub
    // URL that gets ENCODED INTO PRINTED MATERIAL (hence config, never Request.Host).
    private readonly EvaluationArtifactPublishService _artifacts;
    private readonly CommunityHub.Core.Evaluation.EvaluationQrService _qrTokens;
    private readonly SessionEvalsQrService _qrStore;
    private readonly CommunityHub.Core.Email.EmailTemplateOptions _emailOptions;

    public EvaluationReportPublishJob(
        CommunityHubDbContext db,
        EvaluationReportDebounceService debounce,
        SessionEvalPdfService pdfStore,
        FeatureGateService gate,
        EvaluationArtifactPublishService artifacts,
        CommunityHub.Core.Evaluation.EvaluationQrService qrTokens,
        SessionEvalsQrService qrStore,
        Microsoft.Extensions.Options.IOptions<CommunityHub.Core.Email.EmailTemplateOptions> emailOptions,
        ILogger<EvaluationReportPublishJob> log,
        CommunityHub.Core.Diagnostics.JobActivityReporter? activity = null)
    {
        _db = db;
        _debounce = debounce;
        _pdfStore = pdfStore;
        _gate = gate;
        _artifacts = artifacts;
        _qrTokens = qrTokens;
        _qrStore = qrStore;
        _emailOptions = emailOptions.Value;
        _log = log;
        _activity = activity;
    }

    [Function("EvaluationReportPublishJob")]
    public async Task Run([TimerTrigger("0 */5 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        var eventId = await _db.Events
            .Where(e => e.IsActive).Select(e => (int?)e.Id).FirstOrDefaultAsync(ct);
        if (eventId is null)
        {
            _activity?.ReportInactive("No active edition, so there are no sessions to report on.");
            return;
        }

        // ===================================================================
        //  §783.8 — PUBLISH THE QR CODES FIRST, and OUTSIDE the e-mail gate.
        // ===================================================================
        //
        // 🔴 Operator 2026-08-03: *"system should automatically generate a qr code in the session
        // evaluation system once it is active"*, then *"qr code should be released once it is
        // ready"*. It was not happening at all:
        // `EvaluationArtifactPublishService.PublishQrCodesAsync` was written in §749.2, registered
        // in DI, given a test — and **never called by anything**. Verified against PROD SharePoint
        // on 2026-08-03: the QR folder held ZERO files, so the Organizer/Sessions column reading
        // "no QR" was telling the exact truth.
        //
        // 🔒 **Deliberately ABOVE the `session-eval-email` gate.** A QR is an INPUT to collecting
        // feedback — printed and put on a wall BEFORE the session runs — not a RESULT of it. The
        // switch below governs whether speakers are e-mailed their results, a different decision
        // entirely; leaving the QR behind it means the codes cannot be printed in time unless a
        // results-mail setting happens to be on.
        //
        // ⚠️ Tokens are ENSURED, never regenerated. `EnsureTokenAsync` is idempotent and hands back
        // an existing token untouched — the only safe behaviour once a code may already be printed,
        // because regenerating one silently kills a QR that is already on a wall.
        await PublishQrCodesAsync(eventId.Value, ct);

        // Switch 1 — the feature.
        if (!await _gate.IsFeatureEnabledAsync(
                EvaluationReportReadyMailService.FeatureKey, eventId.Value, ct))
        {
            _activity?.ReportInactive(
                "The 'session-eval-email' switch is off, so no evaluation reports are published or "
                + "sent. Turn it on in Settings when you want speakers to receive their results.");
            return;
        }

        // Switch 2 — somewhere to publish TO. Named explicitly: without this the pipeline runs
        // green and writes nothing, which is the failure that looks most like success.
        if (!_pdfStore.CanManage)
        {
            _activity?.ReportInactive(
                "No SharePoint folder is configured for evaluation PDFs "
                + "(Graphics:SharePoint:SessionEvalPdfFolderPath), so there is nowhere to publish a "
                + "report. Nothing was sent.");
            return;
        }

        var result = await _debounce.RunAsync(eventId.Value, ct);

        _activity?.ReportWork();

        if (result.Published > 0)
        {
            _log.LogInformation(
                "EvaluationReportPublishJob: published {Published} report(s), {Superseded} of them "
                + "superseding an earlier version.",
                result.Published, result.Superseded);
        }
    }

    /// <summary>
    /// §783.8 — mint any missing session tokens, then publish every session's QR PNG into the QR
    /// folder. Never throws into the timer: a QR problem must not stop the report pipeline below it.
    /// </summary>
    private async Task PublishQrCodesAsync(int eventId, CancellationToken ct)
    {
        try
        {
            // Somewhere to publish TO, and a URL to encode. Both are named, because a QR sweep that
            // runs green and writes nothing is the §768 failure that looks exactly like success.
            if (!_qrStore.CanManage)
            {
                _log.LogInformation(
                    "§783.8 QR publish skipped: no SharePoint folder is configured for the session "
                    + "evaluation QR codes, so there is nowhere to write them.");
                return;
            }

            var hubUrl = (_emailOptions.HubUrl ?? string.Empty).Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(hubUrl))
            {
                // 🔒 Never fall back to the request host: this URL is PRINTED. A code generated on a
                // staging hostname would carry it onto paper and nothing downstream would catch it.
                _log.LogWarning(
                    "§783.8 QR publish skipped: the hub URL is not configured, and a printed code "
                    + "must never encode a guessed hostname.");
                return;
            }

            // Ensure a token for every non-service session that lacks one. Idempotent: an existing
            // token is returned unchanged, so a code already on a wall keeps working.
            var needTokens = await _db.Sessions
                .Where(s => s.EventId == eventId
                            && !s.IsServiceSession
                            && (s.PublicToken == null || s.PublicToken == ""))
                .Select(s => s.Id)
                .ToListAsync(ct);

            foreach (var id in needTokens) await _qrTokens.EnsureTokenAsync(id, ct);

            var qr = await _artifacts.PublishQrCodesAsync(eventId, hubUrl, ct);

            if (qr.Published > 0 || needTokens.Count > 0)
            {
                _activity?.ReportWork();
                _log.LogInformation(
                    "§783.8 QR publish: {Published} code(s) written, {Skipped} failed, "
                    + "{Minted} new token(s) minted.",
                    qr.Published, qr.Skipped, needTokens.Count);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 🔒 Reported, never swallowed silently, and never allowed to abort the report pipeline
            // below — the two halves publish independently and one must not take the other down.
            _log.LogError(ex, "§783.8 QR publish failed for event {EventId}.", eventId);
        }
    }
}
