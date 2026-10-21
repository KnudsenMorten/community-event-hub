using System.Text.Json.Serialization;
using CommunityHub.Core.Evaluation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CommunityHub.Api;

/// <summary>
/// §1259 — the two endpoints the WEB KIOSK needs and a built unit does not.
/// </summary>
/// <remarks>
/// <para>🔒 <b>The device contract is untouched.</b> Votes do NOT come here: the kiosk posts them
/// to <c>/evaluation/v1/ingest/responses</c> with the same headers, the same record shape and the
/// same idempotency key as a built box, so attribution, de-duplication, scoring and the reports
/// have exactly one path through them. What a browser needs beyond that is only:</para>
/// <list type="number">
///   <item><b>pair</b> — receive its device key without a human typing a 43-character secret on a
///   tablet, and without the key ever appearing in a URL;</item>
///   <item><b>session</b> — show the attendee which session they are rating.</item>
/// </list>
///
/// <para>🔒 <see cref="AllowAnonymousAttribute"/> for the same reason as the ingest controller: the
/// app's fallback policy would otherwise redirect an iPad to a login page. Pairing authenticates
/// with the one-time code; everything after it authenticates with the device key headers.</para>
/// </remarks>
[ApiController]
[AllowAnonymous]
[Route("evaluation/v1/kiosk")]
public sealed class EvaluationKioskController : ControllerBase
{
    private readonly EvaluationKioskService _kiosk;
    private readonly EvaluationIngestService _ingest;
    private readonly EvaluationProvisioningService _provisioning;
    private readonly ILogger<EvaluationKioskController> _log;
    private readonly KioskPinGuard? _pinGuard;
    private readonly IntuneKioskDirectory? _intune;
    private readonly EvaluationKioskOptions _kioskOptions;

    public EvaluationKioskController(
        EvaluationKioskService kiosk, EvaluationIngestService ingest,
        EvaluationProvisioningService provisioning,
        ILogger<EvaluationKioskController> log,
        KioskPinGuard? pinGuard = null,
        IntuneKioskDirectory? intune = null,
        Microsoft.Extensions.Options.IOptions<EvaluationKioskOptions>? kioskOptions = null)
    {
        _kiosk = kiosk;
        _ingest = ingest;
        _provisioning = provisioning;
        _log = log;
        _pinGuard = pinGuard;
        _intune = intune;
        _kioskOptions = kioskOptions?.Value ?? new EvaluationKioskOptions();
    }

    public sealed class PinRequest
    {
        [JsonPropertyName("pin")] public string? Pin { get; set; }
        /// <summary>For <c>managed-enrol</c>: the Intune device name tapped.</summary>
        [JsonPropertyName("name")] public string? Name { get; set; }
    }

    /// <summary>§1303 — the PIN verdict as an HTTP answer, or null when the PIN is right.</summary>
    private IActionResult? PinRefusal(string? pin, bool requireConfigured)
    {
        var client = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var verdict = (_pinGuard ?? new KioskPinGuard(TimeProvider.System)).Check(_kioskOptions.Pin, pin, client);
        switch (verdict)
        {
            case KioskPinGuard.Verdict.Ok:
                return null;
            case KioskPinGuard.Verdict.NotConfigured:
                return requireConfigured
                    ? StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "pin_not_configured" })
                    : null;
            case KioskPinGuard.Verdict.LockedOut:
                _log.LogWarning("Evaluation kiosk: organizer PIN locked out for {Client}.", client);
                return StatusCode(StatusCodes.Status429TooManyRequests, new { error = "pin_locked_out" });
            default:
                _log.LogWarning("Evaluation kiosk: wrong organizer PIN from {Client}.", client);
                return StatusCode(StatusCodes.Status403Forbidden, new { error = "pin_wrong" });
        }
    }

    // ---- POST /evaluation/v1/kiosk/unlock ----------------------------------------------------

    /// <summary>
    /// §1303 — the organizer PIN opens the kiosk's setup screen (operator: <i>"none must be able to
    /// modify except organizers with pin code"</i>) and, in the same answer, lists the Intune tablets
    /// for one-tap enrolment. With no PIN configured, setup stays open as before (DEV / tests).
    /// </summary>
    [HttpPost("unlock")]
    public async Task<IActionResult> UnlockAsync([FromBody] PinRequest? body, CancellationToken ct)
    {
        if (PinRefusal(body?.Pin, requireConfigured: false) is { } refusal) return refusal;

        var pinConfigured = !string.IsNullOrWhiteSpace(_kioskOptions.Pin);
        var eventId = await _kiosk.ActiveEventIdAsync(ct);
        IReadOnlyList<string>? names = pinConfigured && _intune is not null && eventId is not null
            ? await _intune.ListAsync(_kioskOptions.IntuneNamePrefix, ct)
            : null;

        var enrolled = names is null || eventId is null
            ? new Dictionary<string, string?>()
            : await _kiosk.EnrolledRoomsAsync(eventId.Value, names, ct);

        return Ok(new
        {
            managed = names is not null,
            devices = (names ?? Array.Empty<string>()).Select(n => new
            {
                name = n,
                enrolled = enrolled.ContainsKey(n),
                room = enrolled.TryGetValue(n, out var room) ? room : null,
            }),
        });
    }

    // ---- POST /evaluation/v1/kiosk/managed-enrol ---------------------------------------------

    /// <summary>
    /// §1303 — enrol THIS tablet as the tapped Intune device, approved on the spot (operator:
    /// <i>"then it should auto-approve it"</i>). Needs the PIN and a name Intune actually has.
    /// </summary>
    [HttpPost("managed-enrol")]
    public async Task<IActionResult> ManagedEnrolAsync([FromBody] PinRequest? body, CancellationToken ct)
    {
        if (PinRefusal(body?.Pin, requireConfigured: true) is { } refusal) return refusal;

        var name = (body?.Name ?? string.Empty).Trim();
        if (name.Length is < 3 or > 64) return BadRequest(new { error = "name_required" });

        var eventId = await _kiosk.ActiveEventIdAsync(ct);
        if (eventId is null) return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "no_active_event" });

        // 🔒 Only a name Intune has: the PIN proves an organizer, the directory proves the tablet.
        var names = _intune is null ? null : await _intune.ListAsync(_kioskOptions.IntuneNamePrefix, ct);
        if (names is null) return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "intune_unavailable" });
        var match = names.FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        if (match is null) return NotFound(new { error = "not_an_intune_device" });

        var paired = await _kiosk.EnrolManagedAsync(eventId.Value, match, ct);
        _log.LogInformation("Evaluation kiosk: {Name} enrolled as a managed tablet ({Room}).",
            paired.SerialNumber, paired.RoomName ?? "no room linked");

        return Ok(new
        {
            serialNumber = paired.SerialNumber,
            deviceKey = paired.DeviceKey,
            label = paired.Label,
            room = paired.RoomName,
        });
    }

    /// <summary>
    /// §1265 — identifies the running build. The kiosk page carries the value it was rendered with
    /// and compares it to <c>GET build</c>, so a stand picks up a deploy by itself instead of at the
    /// 03:00 reload. The assembly's module version id changes on every compile, needs no config and
    /// says nothing about the environment.
    /// </summary>
    public static readonly string BuildId =
        typeof(EvaluationKioskController).Assembly.ManifestModule.ModuleVersionId.ToString("N");

    // ---- GET /evaluation/v1/kiosk/build -----------------------------------------------------

    /// <summary>§1265 — the build now running. Anonymous: it grants nothing and names nothing.</summary>
    [HttpGet("build")]
    public IActionResult Build()
    {
        // 🔒 Never cached anywhere on the way: a stale answer here is exactly the bug it fixes.
        Response.Headers.CacheControl = "no-store";
        return Ok(new { build = BuildId });
    }

    public sealed class PairRequest
    {
        [JsonPropertyName("code")] public string? Code { get; set; }
    }

    // ---- POST /evaluation/v1/kiosk/pair -------------------------------------------------------

    /// <summary>
    /// Redeem a one-time pairing code for this device's identity + a fresh key.
    /// </summary>
    /// <remarks>
    /// ⚠️ One deliberate vagueness: an unknown, expired and already-used code all answer the same
    /// 404. The iPad's operator is standing next to the organiser who issued it, so a precise
    /// reason helps nobody who could not simply ask — while a distinct "expired" answer tells an
    /// attacker their guess was once a real code.
    /// </remarks>
    [HttpPost("pair")]
    public async Task<IActionResult> PairAsync([FromBody] PairRequest? body, CancellationToken ct)
    {
        var paired = await _kiosk.RedeemAsync(body?.Code, ct);
        if (paired is null)
        {
            _log.LogWarning("Evaluation kiosk: pairing refused (unknown, expired or used code).");
            return NotFound(new { error = "pairing_code_not_valid" });
        }

        _log.LogInformation(
            "Evaluation kiosk: device {Serial} paired ({Room}).",
            paired.SerialNumber, paired.RoomName ?? "no room linked");

        return Ok(new
        {
            serialNumber = paired.SerialNumber,
            deviceKey = paired.DeviceKey,
            label = paired.Label,
            room = paired.RoomName,
        });
    }

    public sealed class EnrolRequest
    {
        [JsonPropertyName("token")] public string? Token { get; set; }
    }

    public sealed class JoinRequest
    {
        /// <summary>The identity the browser generated for itself and will keep.</summary>
        [JsonPropertyName("serialNumber")] public string? SerialNumber { get; set; }
        /// <summary>What the person at the iPad called it, e.g. "Room 17 stand".</summary>
        [JsonPropertyName("name")] public string? Name { get; set; }
    }

    // ---- POST /evaluation/v1/kiosk/join -------------------------------------------------------

    /// <summary>
    /// §1259d — an iPad asking to join, and then polling the same endpoint until an organiser has
    /// approved it. Approval hands back the device key; nothing before it grants anything at all.
    /// </summary>
    /// <remarks>
    /// <para>🔑 <b>This is the flow the operator asked for</b> (2026-09-20: *"i thought i go to an
    /// easy to remember url in the browser and choose enroll device, which goes into a unapproved
    /// queue backend, where i can approve"*), and it is the flow the BUILT UNITS already use —
    /// <see cref="EvaluationProvisioningService.RequestAsync"/>, idempotent on the identifier, with
    /// the existing Approve / Reject queue on <c>/Organizer/EvaluationDevices</c>. The iPad simply
    /// joins that queue instead of a second one.</para>
    ///
    /// <para>🔒 <b>No bootstrap secret here, unlike the hardware endpoint — deliberately.</b> These
    /// tablets have no mail and no keyboard worth typing a secret on, which is the whole reason this
    /// exists. What makes it safe is not a header, it is that <b>reaching it grants NOTHING</b>: the
    /// caller gets a row in a queue, and a human decides. The cap below is the only other guard
    /// needed — it stops the queue being buried, which is the realistic abuse, rather than pretending
    /// an anonymous endpoint can be authenticated.</para>
    /// </remarks>
    [HttpPost("join")]
    public async Task<IActionResult> JoinAsync([FromBody] JoinRequest? body, CancellationToken ct)
    {
        var serial = (body?.SerialNumber ?? string.Empty).Trim();
        if (serial.Length is < 6 or > 64) return BadRequest(new { error = "serial_number_required" });

        var eventId = await _kiosk.ActiveEventIdAsync(ct);
        if (eventId is null) return StatusCode(StatusCodes.Status503ServiceUnavailable,
            new { error = "no_active_event" });

        // 🔒 The queue is a human's inbox, so it is bounded. A NEW identifier is refused once the
        // pending list is full; an identifier already in the queue always gets through, because
        // that is a device polling for its answer and must never be locked out by the crowd.
        if (!await _kiosk.MayJoinAsync(eventId.Value, serial, ct))
        {
            _log.LogWarning("Evaluation kiosk: join refused — the pending queue is full.");
            return StatusCode(StatusCodes.Status429TooManyRequests, new { error = "queue_full" });
        }

        var result = await _provisioning.RequestAsync(
            eventId.Value, serial, firmwareVersion: "web-kiosk/1", note: body?.Name, ct);

        switch (result.Outcome)
        {
            case EvaluationProvisioningService.Outcome.Approved:
                await _kiosk.MarkAsKioskAsync(result.Device!.Id, ct);
                return Ok(new
                {
                    status = "approved",
                    serialNumber = result.Device!.SerialNumber,
                    deviceKey = result.DeviceKey,
                    room = await _kiosk.RoomNameAsync(result.Device, ct),
                    // 🔴 §1275 — the device's reconnect link, issued AT approval. On an iPad a page
                    // added to the Home Screen gets its OWN storage, so the identity Safari saved is
                    // invisible to it and it enrolled again as a second device (operator 2026-09-21:
                    // "now i have same device 2 times - with different serial numbers"). iOS keeps
                    // the ADDRESS when a page is added, so the page puts this in the address bar and
                    // the Home Screen app reconnects as the same device.
                    enrolToken = await _kiosk.IssueEnrolmentTokenAsync(result.Device.Id, ct),
                });

            case EvaluationProvisioningService.Outcome.AlreadyProvisioned:
                // The key was handed over once already and cannot be shown again. An iPad that lost
                // it needs an organiser to re-pair or re-issue its link — say so plainly.
                return Ok(new { status = "already_provisioned" });

            case EvaluationProvisioningService.Outcome.Rejected:
                return Ok(new { status = "rejected" });

            default:
                return Ok(new { status = "pending" });
        }
    }

    // ---- POST /evaluation/v1/kiosk/enrol ------------------------------------------------------

    /// <summary>
    /// §1259c — exchange this device's LONG-LIVED enrolment token (the secret in its bookmarked
    /// URL) for a device key. Repeatable by design: this is the path that heals a kiosk whose
    /// browser storage was evicted, wiped or replaced, with nobody typing anything.
    /// </summary>
    /// <remarks>
    /// 🔒 Same deliberate vagueness as pairing: unknown, revoked and retired all answer 404. And
    /// the same 60-second reality check — this grants the ability to be ONE kiosk in ONE room,
    /// whose only power is adding ratings; it reads nothing and cannot reach the hub.
    /// </remarks>
    [HttpPost("enrol")]
    public async Task<IActionResult> EnrolAsync([FromBody] EnrolRequest? body, CancellationToken ct)
    {
        var device = await _kiosk.EnrolAsync(body?.Token, ct);
        if (device is null) return NotFound(new { error = "enrolment_token_not_valid" });

        _log.LogInformation(
            "Evaluation kiosk: device {Serial} enrolled from its link ({Room}).",
            device.SerialNumber, device.RoomName ?? "no room linked");

        return Ok(new
        {
            serialNumber = device.SerialNumber,
            deviceKey = device.DeviceKey,
            label = device.Label,
            room = device.RoomName,
        });
    }

    // ---- GET /evaluation/v1/kiosk/session -----------------------------------------------------

    /// <summary>
    /// What is running in this device's room right now — resolved by the SAME attribution a press
    /// gets, so the screen can never name one session while the vote lands on another.
    /// </summary>
    [HttpGet("session")]
    public async Task<IActionResult> SessionAsync(CancellationToken ct)
    {
        var serial = Request.Headers[EvaluationIngestController.SerialNumberHeader].ToString();
        var key = Request.Headers[EvaluationIngestController.DeviceKeyHeader].ToString();

        var (device, failure) = await _ingest.AuthenticateAsync(serial, key, ct);
        // §1303 — operator: "when i delete a device it should go to enrolment page so i can
        // reonboard it". A DELETED or revoked device is not a key problem that self-healing can fix:
        // 410 tells the page to forget itself and open setup. A wrong key stays 401 (heal silently).
        if (device is null && failure == EvaluationIngestService.AuthFailure.UnknownDevice)
            return StatusCode(StatusCodes.Status410Gone, new { error = "device_removed" });
        if (device is null) return Unauthorized(new { error = "device_not_authenticated" });

        var session = await _kiosk.CurrentSessionAsync(device, ct);
        // §1262 — only when nothing is running: an idle screen counts down to the next window.
        var next = session is null ? await _kiosk.NextSessionAsync(device, ct) : null;
        var policy = await _kiosk.CapturePolicyAsync(device, ct);
        var symbols = await _kiosk.RatingSymbolsAsync(device, ct);

        return Ok(new
        {
            // The ROOM NAME, read live — not the device label, and not whatever the iPad cached at
            // approval (a room linked afterwards would otherwise never appear on the screen).
            room = await _kiosk.RoomNameAsync(device, ct),
            // §1354 — the capture policy (int) and what it means for this page: false = rating only.
            // The page stamps each vote with the policy it was asked under.
            policy = (int)policy,
            followUps = FeedbackCapturePolicies.HasFollowUps(policy),
            // §1355 — the symbols on the rating buttons (flags: 1 faces, 2 stars, 4 points; one or two).
            symbols = (int)symbols,
            // §1269 — for the kiosk's troubleshooting line: the name an organizer gave it (else the
            // serial), so the screen and the devices page call the device the same thing.
            device = new
            {
                name = CommunityHub.Pages.Organizer.EvaluationDevicesModel.DisplayName(device),
                serial = device.SerialNumber,
            },
            session = session is null ? null : new
            {
                id = session.Id,
                title = session.Title,
                speakers = session.Speakers,
                endsAt = session.EndsAt,
                closesAt = session.ClosesAt,   // §1270 — end + 30 min grace
            },
            next = next is null ? null : new
            {
                title = next.Title,
                opensAt = next.OpensAt,
                closesAt = next.ClosesAt,
            },
        });
    }
}
