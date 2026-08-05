using System.Text.Json.Serialization;
using CommunityHub.Core.Data;
using CommunityHub.Core.Evaluation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Api;

/// <summary>
/// §743 C4a — the ONLY two endpoints the feedback devices ever call.
/// </summary>
/// <remarks>
/// <para>Implements <c>docs/internal-session-eval-device-api-contract.md</c>. That document is what
/// the vendor writes firmware against, and 25 fielded units cannot be revised as easily as this
/// service — <b>where the two disagree, the document wins.</b></para>
///
/// <para>🔒 <b><see cref="AllowAnonymousAttribute"/> is REQUIRED, not incidental.</b> The app has a
/// fail-closed authorization fallback policy, so without this the cookie requirement would run and
/// every device request would be redirected to a login page — the units have no browser, no screen
/// and no way to report why. These endpoints authenticate themselves with a per-device key over TLS,
/// exactly as <c>SponsorLeadsController</c> does with its per-sponsor API key.</para>
///
/// <para>🔒 <b>The route is FIXED for the life of these units.</b> Firmware ships against
/// <c>/evaluation/v1/ingest/...</c>; changing it means an OTA rollout across the fleet. The service
/// name precedes the version so Session Evaluation can later be versioned, rate-limited and routed
/// to its own infrastructure without disturbing any other API — the extraction becomes a routing
/// change rather than a rewrite.</para>
/// </remarks>
[ApiController]
[AllowAnonymous]
[Route("evaluation/v1/ingest")]
public sealed class EvaluationIngestController : ControllerBase
{
    /// <summary>The device's identity. Opaque — MAC or serial, never parsed (§743 item 10).</summary>
    public const string SerialNumberHeader = "X-Device-Serial-Number";

    /// <summary>
    /// The per-device pre-shared key. 🔒 A HEADER, never a query string: URLs land in access logs,
    /// proxies and crash dumps, and a credential must not.
    /// </summary>
    public const string DeviceKeyHeader = "X-Device-Key";

    private readonly CommunityHubDbContext _db;
    private readonly EvaluationIngestService _ingest;
    private readonly EvaluationProvisioningService _provisioning;
    private readonly ILogger<EvaluationIngestController> _log;

    public EvaluationIngestController(
        CommunityHubDbContext db, EvaluationIngestService ingest,
        EvaluationProvisioningService provisioning,
        ILogger<EvaluationIngestController> log)
    {
        _db = db;
        _ingest = ingest;
        _provisioning = provisioning;
        _log = log;
    }

    // ---- wire shapes (the contract's JSON) --------------------------------------------------

    public sealed class RecordDto
    {
        [JsonPropertyName("deviceRecordId")] public string? DeviceRecordId { get; set; }
        [JsonPropertyName("rating")] public int Rating { get; set; }
        [JsonPropertyName("collectionTimestamp")] public DateTimeOffset? CollectionTimestamp { get; set; }
    }

    public sealed class ResponsesRequest
    {
        [JsonPropertyName("serialNumber")] public string? SerialNumber { get; set; }
        [JsonPropertyName("firmwareVersion")] public string? FirmwareVersion { get; set; }
        [JsonPropertyName("clockSyncedAt")] public DateTimeOffset? ClockSyncedAt { get; set; }
        [JsonPropertyName("records")] public List<RecordDto>? Records { get; set; }
    }

    public sealed class HeartbeatRequest
    {
        [JsonPropertyName("serialNumber")] public string? SerialNumber { get; set; }
        [JsonPropertyName("firmwareVersion")] public string? FirmwareVersion { get; set; }
        [JsonPropertyName("batteryPercent")] public int? BatteryPercent { get; set; }
        [JsonPropertyName("signalQuality")] public int? SignalQuality { get; set; }
        [JsonPropertyName("cachedRecordCount")] public int? CachedRecordCount { get; set; }
        [JsonPropertyName("clockSyncedAt")] public DateTimeOffset? ClockSyncedAt { get; set; }
    }

    /// <summary>Max records per batch. Larger ⇒ 413, per the contract; the device splits and retries.</summary>
    public const int MaxBatchSize = 500;

    // ---- POST /evaluation/v1/ingest/responses -----------------------------------------------

    [HttpPost("responses")]
    public async Task<IActionResult> PostResponsesAsync(
        [FromBody] ResponsesRequest? body, CancellationToken ct)
    {
        if (body is null) return BadRequest(new { error = "malformed" });

        // The header is the authenticated identity. A body serialNumber that disagrees with it is a
        // firmware bug worth surfacing loudly rather than quietly preferring one of the two.
        var headerId = Request.Headers[SerialNumberHeader].ToString();
        var key = Request.Headers[DeviceKeyHeader].ToString();

        var (device, failure) = await _ingest.AuthenticateAsync(headerId, key, ct);
        if (device is null) return AuthProblem(failure, headerId);

        if (!string.IsNullOrWhiteSpace(body.SerialNumber)
            && !string.Equals(body.SerialNumber, device.SerialNumber, StringComparison.Ordinal))
        {
            _log.LogWarning(
                "Evaluation ingest: body serialNumber '{BodyId}' does not match authenticated '{AuthId}'.",
                body.SerialNumber, device.SerialNumber);
            return BadRequest(new { error = "device_id_mismatch" });
        }

        var records = body.Records ?? new List<RecordDto>();
        if (records.Count > MaxBatchSize)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge,
                new { error = "batch_too_large", maxBatchSize = MaxBatchSize });
        }

        var eventClosed = await IsEventClosedAsync(device.EventId, ct);

        var results = await _ingest.IngestAsync(
            device,
            records.Select(r => new EvaluationIngestService.IncomingRecord(
                r.DeviceRecordId, r.Rating, r.CollectionTimestamp)).ToList(),
            body.FirmwareVersion, body.ClockSyncedAt, eventClosed, ct);

        return Ok(new
        {
            results = results.Select(r => new
            {
                deviceRecordId = r.DeviceRecordId,
                status = r.Status,
                reason = r.Reason,
            }),
        });
    }

    // ---- POST /evaluation/v1/ingest/heartbeat ------------------------------------------------

    [HttpPost("heartbeat")]
    public async Task<IActionResult> PostHeartbeatAsync(
        [FromBody] HeartbeatRequest? body, CancellationToken ct)
    {
        var headerId = Request.Headers[SerialNumberHeader].ToString();
        var key = Request.Headers[DeviceKeyHeader].ToString();

        var (device, failure) = await _ingest.AuthenticateAsync(headerId, key, ct);
        if (device is null) return AuthProblem(failure, headerId);

        await _ingest.HeartbeatAsync(
            device, body?.BatteryPercent, body?.SignalQuality, body?.CachedRecordCount,
            body?.FirmwareVersion, body?.ClockSyncedAt, ct);

        // 🔑 §753 — the health directive rides back on the HEARTBEAT as well as on /provision.
        // Without it, a change to a device's telemetry switch would only reach the unit at its next
        // hourly provisioning check, so a unit spamming telemetry mid-event could not be quietened
        // for an hour. Same service in both places, so the two answers cannot disagree.
        var health = await _provisioning.HealthDirectiveAsync(device, ct);

        return Ok(new
        {
            status = "ok",
            health = new
            {
                sendHealth = health.SendHealth,
                intervalSeconds = health.IntervalSeconds,
                nextCheckAfter = health.NextCheckAfter,
            },
        });
    }

    // ---- helpers -----------------------------------------------------------------------------

    /// <summary>
    /// 🔒 The contract promises 401 for a bad key and 403 for an unknown/unpaired device, and tells
    /// the firmware to back off ≥ 1 hour on BOTH — the fix is a human registering a unit, so
    /// hammering achieves nothing. Critically, the device must KEEP its cache on either.
    /// </summary>
    private IActionResult AuthProblem(EvaluationIngestService.AuthFailure failure, string serialNumber)
    {
        _log.LogWarning(
            "Evaluation ingest refused ({Failure}) for device '{SerialNumber}'.", failure, serialNumber);

        return failure == EvaluationIngestService.AuthFailure.BadKey
            ? Unauthorized(new { error = "invalid_key" })
            : StatusCode(StatusCodes.Status403Forbidden, new { error = "unknown_device" });
    }

    /// <summary>
    /// The event's ingest cut-off: records are accepted until the end of the event and rejected
    /// afterwards (§743 item 5). A device offline for a day can still flush on the final day; a unit
    /// discovered weeks later cannot retroactively change a published result.
    /// </summary>
    private async Task<bool> IsEventClosedAsync(int eventId, CancellationToken ct)
    {
        var end = await _db.Events
            .Where(e => e.Id == eventId)
            .Select(e => (DateOnly?)e.EndDate)
            .FirstOrDefaultAsync(ct);

        if (end is null) return false;   // no end date configured ⇒ never refuse on this basis

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return today > end.Value;
    }
}
