using CommunityHub.Core.Evaluation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace CommunityHub.Api;

/// <summary>
/// §747 C8 — the outbound report pull. The one endpoint external systems call to collect a session's
/// generated PDF.
/// </summary>
/// <remarks>
/// <para>The brief: <i>"Endpoint from which external systems can pull the generated PDF reports once
/// they are ready. Report versions are superseded when a session is recomputed after late data, so
/// consumers must be able to detect that a newer version exists."</i> The route and the access model
/// are specified there and are NOT ours to reinvent —
/// <c>GET /evaluation/v1/events/{eventId}/sessions/{sessionId}/report</c>, authorised by a
/// <b>service credential</b>.</para>
///
/// <para>🔒 <b><see cref="AllowAnonymousAttribute"/> is REQUIRED, not incidental.</b> The app's
/// authorization fallback is fail-closed, so without this a service client would be redirected to
/// <c>/Login</c> and would see an HTML login page with status 200 — the worst possible failure for a
/// machine consumer, because it looks like success. <c>AuthorizationFallbackTests</c> pins this.</para>
///
/// <para>🔒 <b>Reports are generated per request and never stored</b> (§8, §743.14), so supersession
/// is signalled by a version derived from the report's INPUTS —
/// <see cref="EvaluationReportBuilder.DeriveVersion"/> explains why that, and not a file hash or a
/// timestamp of "now", is the honest answer.</para>
///
/// <para>Two ways to use it, both standard HTTP so a consumer needs no bespoke client:</para>
/// <list type="bullet">
///   <item><c>GET</c> with <c>If-None-Match: "&lt;version&gt;"</c> ⇒ <b>304</b> while nothing has
///   changed, the PDF when it has.</item>
///   <item><c>HEAD</c> ⇒ headers only. The version without generating a PDF, for a consumer that
///   polls often.</item>
/// </list>
/// </remarks>
[ApiController]
[AllowAnonymous]
[Route("evaluation/v1/events/{eventId:int}/sessions/{sessionId:int}")]
public sealed class EvaluationReportController : ControllerBase
{
    /// <summary>
    /// The service credential. 🔒 A HEADER, never a query string — URLs land in access logs, proxy
    /// caches and crash dumps, and this key opens verbatim attendee comments.
    /// </summary>
    public const string ApiKeyHeader = "X-Evaluation-Api-Key";

    /// <summary>The version identifier, echoed outside the ETag for consumers that ignore caching headers.</summary>
    public const string VersionHeader = "X-Report-Version";

    private readonly EvaluationApiClientService _clients;
    private readonly EvaluationReportBuilder _builder;
    private readonly EvaluationReportService _reports;
    private readonly ILogger<EvaluationReportController> _log;

    public EvaluationReportController(
        EvaluationApiClientService clients, EvaluationReportBuilder builder,
        EvaluationReportService reports, ILogger<EvaluationReportController> log)
    {
        _clients = clients;
        _builder = builder;
        _reports = reports;
        _log = log;
    }

    [HttpGet("report")]
    [HttpHead("report")]
    public async Task<IActionResult> GetReportAsync(int eventId, int sessionId, CancellationToken ct)
    {
        var presented = ReadKey();

        var client = await _clients.AuthenticateAsync(eventId, presented, ct);
        if (client is null)
        {
            // 🔒 ONE status for every refusal — bad key, revoked credential, and a valid key aimed at
            // another event are indistinguishable from outside. Distinguishing them would let a
            // holder of one event's key enumerate which other events exist.
            _log.LogWarning(
                "Evaluation report pull refused for event {EventId}, session {SessionId}.",
                eventId, sessionId);
            return Unauthorized(new { error = "invalid_credential" });
        }

        var version = await _builder.VersionAsync(eventId, sessionId, ct);
        if (version is null) return NotFound(new { error = "unknown_session" });

        var etag = new EntityTagHeaderValue($"\"{version}\"");
        Response.Headers[VersionHeader] = version;
        Response.Headers[HeaderNames.ETag] = etag.ToString();

        // 🔑 The whole point of the version: a consumer that already holds this one is told so
        // WITHOUT us rendering a PDF. Polling is therefore cheap enough to do often.
        if (RequestMatchesVersion(etag)) return StatusCode(StatusCodes.Status304NotModified);

        // HEAD asks whether a newer version exists, not for the document. Generating a PDF to throw
        // the body away would make the cheap check the expensive one.
        if (HttpMethods.IsHead(Request.Method)) return Ok();

        var report = await _builder.BuildAsync(eventId, sessionId, ct);
        if (report is null) return NotFound(new { error = "unknown_session" });

        var pdf = _reports.Render(report.Data);

        var safeTitle = string.Join("_", report.Data.SessionTitle.Split(Path.GetInvalidFileNameChars()));
        return File(pdf, "application/pdf", $"evaluation-{sessionId}-{safeTitle}.pdf");
    }

    /// <summary>
    /// Accepts the dedicated header or a bearer token, matching how
    /// <see cref="SponsorLeadsController"/> already takes a per-sponsor key — a consumer's HTTP
    /// client usually has first-class support for one or the other, rarely both.
    /// </summary>
    private string? ReadKey()
    {
        if (Request.Headers.TryGetValue(ApiKeyHeader, out var custom) && custom.Count > 0
            && !string.IsNullOrWhiteSpace(custom[0]))
        {
            return custom[0];
        }

        if (Request.Headers.TryGetValue(HeaderNames.Authorization, out var auth) && auth.Count > 0)
        {
            var v = auth[0];
            if (!string.IsNullOrWhiteSpace(v)
                && v.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return v["Bearer ".Length..].Trim();
            }
        }

        return null;
    }

    /// <summary>
    /// <c>If-None-Match</c>, per RFC 9110: <c>*</c> matches anything we hold, otherwise any listed
    /// tag. Compared weakly, because our tag describes the report's data rather than its bytes — two
    /// renders of the same version differ only by the printed generation time.
    /// </summary>
    private bool RequestMatchesVersion(EntityTagHeaderValue current)
    {
        var candidates = Request.GetTypedHeaders().IfNoneMatch;
        if (candidates is null || candidates.Count == 0) return false;

        return candidates.Any(c =>
            c.Tag == "*" || c.Compare(current, useStrongComparison: false));
    }
}
