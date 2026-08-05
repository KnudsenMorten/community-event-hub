namespace CommunityHub.Core.Domain.Evaluation;

/// <summary>
/// §743 C1/C4a — one physical feedback device (four buttons, LTE, battery), paired to exactly one
/// event. This is the row the ingest API authenticates against.
/// </summary>
/// <remarks>
/// <para>🔒 <b><see cref="SerialNumber"/> is OPAQUE.</b> The vendor has not yet fixed whether the unit's
/// identity is its MAC address or its serial number (§743 open item 10), and it does not need to be
/// fixed for us to build. We store it, index it, match it EXACTLY, and display it as given — we
/// never parse it, never validate its shape, and never derive anything from its structure. A MAC and
/// a serial differ in length, character set and separators, so any format assumption written into a
/// validator, a column width or a UI mask has to be undone later.</para>
///
/// <para>🔒 <b>Pre-registration is the security model.</b> An organiser enters the identifier and
/// pairs it to the event BEFORE the unit is used; ingest rejects any identifier it does not already
/// know (§743 item 35). There is no self-registration and no pending-approval queue.</para>
///
/// <para>🔒 <b>Two keys, deliberately.</b> <see cref="KeyHash"/> and
/// <see cref="PreviousKeyHash"/> are both accepted. That is what makes an OTA key rotation safe:
/// units update in a staggered rollout, and a device still running the old firmware keeps
/// authenticating until the rollout finishes. With a single key, rotating it partially bricks the
/// fleet — and these units have no screen to tell anyone why.</para>
///
/// <para><b>Liveness is LAST-SEEN, never a live socket.</b> The units are battery-powered and
/// duty-cycle aggressively, so absence of a connection is normal operation, not a fault. Everything
/// here after the key is telemetry from the heartbeat.</para>
/// </remarks>
public class EvaluationDevice
{
    public int Id { get; set; }

    /// <summary>The edition this unit is paired to. A device belongs to ONE event at a time.</summary>
    /// <remarks>
    /// §743 reuses the CEH <see cref="Event"/> as the event identity while Session Evaluation lives
    /// inside ELDK27. That is the ONE deliberate coupling — "event identity is a parameter, not an
    /// assumption" — and on extraction it becomes the eval system's own Event row, not a rewrite.
    /// </remarks>
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>The unit's hardware identifier — MAC or serial. Opaque; never parsed.</summary>
    public string SerialNumber { get; set; } = string.Empty;

    /// <summary>
    /// PBKDF2 salt:hash of the device's pre-shared key — never the key itself, so a database leak
    /// yields no working device credential. Same treatment
    /// <see cref="Auth.PinService.HashPin"/> gives a login PIN.
    /// </summary>
    public string KeyHash { get; set; } = string.Empty;

    /// <summary>The PREVIOUS key, still accepted during a staggered OTA rotation. Null when unused.</summary>
    public string? PreviousKeyHash { get; set; }

    /// <summary>
    /// Revocation, and it must not need a deploy: false ⇒ every request from this unit is rejected.
    /// A lost unit, or one whose enclosure has been opened, is disabled here.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Human label for the venue ("Room 1 unit, by the door"). Never used for matching.</summary>
    public string? Label { get; set; }

    /// <summary>
    /// §753 — the HARD health-telemetry switch for this unit. False ⇒ never sends, whatever the
    /// schedule says.
    /// </summary>
    /// <remarks>
    /// 🔒 <b>Hard beats soft, always.</b> This is the switch an operator reaches for when a specific
    /// unit is misbehaving — a firmware bug spamming telemetry, or a unit pulled from the floor. A
    /// schedule that could re-enable it would make the control useless precisely when it is needed,
    /// so <see cref="EvaluationTelemetryWindow"/> can only ever narrow this, never widen it.
    /// </remarks>
    public bool HealthTelemetryEnabled { get; set; } = true;

    /// <summary>
    /// §743 C2/C3 — the room this unit stands in. NULL until an organiser links it, and an unlinked
    /// device's presses cannot be attributed to any session.
    /// </summary>
    /// <remarks>
    /// 🔑 <b>The link is to the ROOM, never to a session.</b> That is what makes a session moving to
    /// another room re-point at that room's device automatically, with no device reconfiguration —
    /// nobody has to touch hardware because a schedule changed. It is also why the sync never moves
    /// a device by itself: the unit is a physical object in a physical room, and only a person knows
    /// where it actually is.
    /// </remarks>
    public int? RoomId { get; set; }
    public EvaluationRoom? Room { get; set; }

    // ---- Heartbeat telemetry (C1 health board) ---------------------------------------------

    /// <summary>Last contact of ANY kind. Drives online / late / silent, and the missed-heartbeat alert.</summary>
    public DateTimeOffset? LastSeenAt { get; set; }

    public int? LastBatteryPercent { get; set; }
    public int? LastSignalQuality { get; set; }
    public string? FirmwareVersion { get; set; }

    /// <summary>
    /// 🔑 The most operationally important field on this row. A device that is online and
    /// heartbeating but NOT draining its buffer is failing silently, and nothing else on the health
    /// board would reveal it. Alert threshold ≥ 50 (configurable).
    /// </summary>
    public int? CachedRecordCount { get; set; }

    /// <summary>The device's last successful clock sync, as reported. Feeds the §5.5 skew check.</summary>
    public DateTimeOffset? ClockSyncedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
