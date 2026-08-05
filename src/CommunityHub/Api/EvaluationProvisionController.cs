using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using CommunityHub.Core.Data;
using CommunityHub.Core.Evaluation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Api;

/// <summary>
/// §753 C4b — <c>POST /evaluation/v1/provision</c>: a device asks to be onboarded, and collects its
/// credentials once an organiser has approved it.
/// </summary>
/// <remarks>
/// <para>🔴 <b>The only endpoint in the system that accepts an unauthenticated caller.</b> A virgin
/// unit has no device key — that is the entire point — so the usual
/// <c>X-Device-Serial-Number</c>/<c>X-Device-Key</c> pair cannot apply. What guards it instead is a
/// <b>fleet-wide bootstrap secret</b> plus the fact that <b>reaching it grants nothing</b>: a caller
/// who presents the secret gets a row in a queue, and a human decides everything after that.</para>
///
/// <para>🔒 <b>The secret is compared in constant time.</b> A naive <c>==</c> on a shared secret over
/// an internet-facing endpoint leaks its length and prefix to a patient attacker through timing. The
/// cost of doing it properly is one method call.</para>
///
/// <para>🔒 <b>Anonymous by necessity, and that is a deliberate exception</b> to the fail-closed
/// <c>FallbackPolicy</c> (every endpoint without explicit metadata is private). It is called out here
/// so a future reader does not "fix" it, and it is pinned by a test in the authorization suite.</para>
/// </remarks>
[ApiController]
[AllowAnonymous]
[Route("evaluation/v1/provision")]
public sealed class EvaluationProvisionController : ControllerBase
{
    /// <summary>
    /// The fleet bootstrap secret header. 🔒 A HEADER, never a query string — same reasoning as the
    /// device key: URLs land in access logs, proxies and crash dumps.
    /// </summary>
    public const string ProvisionSecretHeader = "X-Provision-Secret";

    /// <summary>Configuration key holding the expected secret.</summary>
    public const string SecretConfigKey = "Evaluation:ProvisionSecret";

    private readonly CommunityHubDbContext _db;
    private readonly EvaluationProvisioningService _provisioning;
    private readonly IConfiguration _config;
    private readonly ILogger<EvaluationProvisionController> _log;

    public EvaluationProvisionController(
        CommunityHubDbContext db, EvaluationProvisioningService provisioning,
        IConfiguration config, ILogger<EvaluationProvisionController> log)
    {
        _db = db;
        _provisioning = provisioning;
        _config = config;
        _log = log;
    }

    // ---- wire shapes -----------------------------------------------------------------------

    /// <remarks>
    /// 🔑 §753.2 — ONE identifier: <c>serialNumber</c>, the number printed on the enclosure.
    /// Chosen over <c>serial</c> (which reads as the UART to a firmware engineer) and over
    /// <c>serialId</c> (whose <c>Id</c> suffix implies a key WE generate — the vendor stamps this
    /// one at manufacture). It matches what is on the label: S/N.
    /// </remarks>
    public sealed class ProvisionRequest
    {
        [JsonPropertyName("serialNumber")] public string? SerialNumber { get; set; }
        [JsonPropertyName("firmwareVersion")] public string? FirmwareVersion { get; set; }
        [JsonPropertyName("note")] public string? Note { get; set; }
    }

    // ---- POST /evaluation/v1/provision ------------------------------------------------------

    [HttpPost]
    public async Task<IActionResult> PostAsync(
        [FromBody] ProvisionRequest? body, CancellationToken ct)
    {
        var expected = _config[SecretConfigKey];
        if (string.IsNullOrWhiteSpace(expected))
        {
            // 🔒 FAIL CLOSED. An unset secret must refuse everything, never wave everyone through —
            // the one configuration mistake here would otherwise open the only anonymous write
            // endpoint we have. Logged loudly because it is silent from the device's side.
            _log.LogError(
                "Evaluation provisioning is not configured ({Key} is unset) — refusing all requests.",
                SecretConfigKey);
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "provisioning_unavailable" });
        }

        var presented = Request.Headers[ProvisionSecretHeader].ToString();
        if (!FixedTimeEquals(presented, expected))
        {
            _log.LogWarning(
                "Provisioning refused: bad or missing bootstrap secret (device '{SerialNumber}').",
                body?.SerialNumber);
            return Unauthorized(new { error = "invalid_provision_secret" });
        }

        var serialNumber = (body?.SerialNumber ?? string.Empty).Trim();
        if (serialNumber.Length == 0)
        {
            return BadRequest(new { error = "serialNumber_required" });
        }

        var eventId = await _db.Events
            .Where(e => e.IsActive)
            .OrderBy(e => e.Id)
            .Select(e => (int?)e.Id)
            .FirstOrDefaultAsync(ct);
        if (eventId is null) return StatusCode(StatusCodes.Status503ServiceUnavailable,
            new { error = "no_active_event" });

        var result = await _provisioning.RequestAsync(
            eventId.Value, serialNumber, body?.FirmwareVersion, body?.Note, ct);

        switch (result.Outcome)
        {
            case EvaluationProvisioningService.Outcome.Pending:
                _log.LogInformation(
                    "Provisioning: '{SerialNumber}' (serial '{Serial}') is awaiting approval — ask #{N}.",
                    serialNumber, result.Request.SerialNumber, result.Request.RequestCount);
                return Accepted(new { status = "pending" });

            case EvaluationProvisioningService.Outcome.Rejected:
                return StatusCode(StatusCodes.Status403Forbidden, new { status = "rejected" });

            case EvaluationProvisioningService.Outcome.Approved:
            {
                var health = await _provisioning.HealthDirectiveAsync(result.Device!, ct);
                _log.LogInformation(
                    "Provisioning: '{SerialNumber}' approved and key issued (device {Id}).",
                    serialNumber, result.Device!.Id);
                return Ok(new
                {
                    status = "approved",
                    serialNumber = result.Device.SerialNumber,
                    deviceKey = result.DeviceKey,      // 🔒 the ONLY response that ever carries this
                    uploadUrl = AbsoluteUrl("/evaluation/v1/ingest/responses"),
                    heartbeatUrl = AbsoluteUrl("/evaluation/v1/ingest/heartbeat"),
                    health = new
                    {
                        sendHealth = health.SendHealth,
                        intervalSeconds = health.IntervalSeconds,
                        nextCheckAfter = health.NextCheckAfter,
                    },
                });
            }

            default:
            {
                // AlreadyProvisioned — the hourly state refresh. No key, ever again.
                var health = await _provisioning.HealthDirectiveAsync(result.Device!, ct);
                return Ok(new
                {
                    status = "already_provisioned",
                    health = new
                    {
                        sendHealth = health.SendHealth,
                        intervalSeconds = health.IntervalSeconds,
                        nextCheckAfter = health.NextCheckAfter,
                    },
                });
            }
        }
    }

    /// <summary>
    /// 🔑 Built from the REQUEST, not from configuration. A unit provisioned against the test host
    /// must be handed test URLs and a unit provisioned against production must be handed production
    /// URLs — deriving them from where the call actually arrived makes that automatic, and removes
    /// the class of bug where a DEV device is told to upload into PROD.
    /// </summary>
    private string AbsoluteUrl(string path) =>
        $"{Request.Scheme}://{Request.Host}{path}";

    /// <summary>Constant-time comparison — never <c>==</c> on a secret (see the class remarks).</summary>
    private static bool FixedTimeEquals(string presented, string expected)
    {
        var a = Encoding.UTF8.GetBytes(presented ?? string.Empty);
        var b = Encoding.UTF8.GetBytes(expected ?? string.Empty);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
