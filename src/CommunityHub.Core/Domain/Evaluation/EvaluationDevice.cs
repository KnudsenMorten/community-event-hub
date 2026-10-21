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

    /// <summary>
    /// §1349 — the SAME physical room's pre-day (master-class) record. CEH keeps the pre-day as
    /// its own room ("Room-16-Floor 1-Max 32-MC" next to "Room-16-Floor 1-Max 44"), so a unit
    /// standing in room 16 needs both links. NULL when the room hosts no master class.
    /// </summary>
    /// <remarks>
    /// 🔑 An explicit link, not a name rule: "Room-18-19-…-MC" and "Room-5-7-…-MC" span two
    /// main-day rooms. The pre-day and the main days never overlap, so the press time decides
    /// which room's session it lands on (<see cref="LinkedRoomIds"/>).
    /// </remarks>
    public int? MasterClassRoomId { get; set; }
    public EvaluationRoom? MasterClassRoom { get; set; }

    /// <summary>
    /// §1349 — the two DIRECT links (main day + pre-day). Superseded by <see cref="PhysicalRoomId"/>
    /// (§1350) and used only when no physical room is set; resolve through
    /// <c>EvaluationRoomMap.RoomIdsAsync</c>, never this alone.
    /// </summary>
    public IReadOnlyList<int> LinkedRoomIds() =>
        new[] { RoomId, MasterClassRoomId }.OfType<int>().Distinct().ToList();

    /// <summary>
    /// §1350 — the PHYSICAL room this unit stands in. When set it wins over <see cref="RoomId"/> /
    /// <see cref="MasterClassRoomId"/>: the unit collects for every schedule room mapped to it.
    /// </summary>
    public int? PhysicalRoomId { get; set; }
    public EvaluationPhysicalRoom? PhysicalRoom { get; set; }

    /// <summary>
    /// §1354 — this kiosk's own capture policy, overriding the edition's default kiosk policy, so two
    /// tablets can run different policies side by side (*"so we can test what works"*). Null = default.
    /// </summary>
    public CommunityHub.Core.Evaluation.FeedbackCapturePolicy? CapturePolicy { get; set; }

    /// <summary>§1355 — this kiosk's own rating-button symbols (null = the edition's kiosk default).</summary>
    public CommunityHub.Core.Evaluation.RatingSymbols? RatingSymbols { get; set; }

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

    // ---- §1259 — the WEB KIOSK option (an iPad instead of a built box) ----------------------
    //
    // 🔒 ADDITIVE, and the hardware design is untouched. Operator 2026-09-20: *"i would like to
    // build a web based portal that i can run on 20 ipads instead of my other physical session
    // eval boxes"* · *"make this a new option"* · *"dont remove the old session eval design"*.
    // ⇒ A kiosk is just another EvaluationDevice: same opaque identifier, same per-device key,
    // same room link, same /evaluation/v1/ingest contract, same time attribution, same score and
    // the same reports. Nothing about the built units changes, and a room can run either kind.

    /// <summary>
    /// §1259 — true when this "device" is an iPad running the web kiosk rather than a built unit.
    /// </summary>
    /// <remarks>
    /// Recorded for two reasons only: the response's <c>Source</c> (diagnostics, never
    /// segmentation of the headline figure — Part 1, "the two channels are one instrument"), and
    /// the health board, where a kiosk has no battery or signal to report and would otherwise read
    /// as a unit failing to send telemetry.
    /// </remarks>
    public bool IsKiosk { get; set; }

    /// <summary>
    /// §1259 — the hash of a ONE-TIME pairing code an organiser issues so an iPad can fetch its
    /// device key without anyone typing a 43-character secret on a tablet.
    /// </summary>
    /// <remarks>
    /// 🔒 Hashed with the same PBKDF2 helper as the device key: it is a credential for the few
    /// minutes it lives, and the thing it hands over is the real key. Single use
    /// (<see cref="PairingCodeUsedAt"/>) and short-lived (<see cref="PairingCodeExpiresAt"/>), so a
    /// code read off a shoulder at the venue is worthless within the hour — and re-pairing is one
    /// click, which is what makes a strict expiry affordable.
    /// </remarks>
    public string? PairingCodeHash { get; set; }

    /// <summary>When the outstanding pairing code stops working. Null when none is outstanding.</summary>
    public DateTimeOffset? PairingCodeExpiresAt { get; set; }

    /// <summary>When the outstanding pairing code was redeemed. Non-null ⇒ it cannot be used again.</summary>
    public DateTimeOffset? PairingCodeUsedAt { get; set; }

    /// <summary>When this kiosk last loaded its screen (the browser equivalent of a heartbeat).</summary>
    public DateTimeOffset? KioskLastSeenAt { get; set; }

    /// <summary>
    /// §1259c — the hash of this device's LONG-LIVED enrolment token: the secret inside the URL an
    /// iPad keeps on its Home Screen, exchanged for a device key whenever the browser has lost one.
    /// </summary>
    /// <remarks>
    /// <para>🔑 <b>This is what makes the kiosk self-healing and removes any "daily login".</b>
    /// Safari can evict script-written storage, an iPad can be wiped, a replacement can arrive
    /// mid-event — in every case the bookmark still carries enough to become this device again,
    /// with nobody typing anything.</para>
    ///
    /// <para>⚠️ <b>A deliberate exception to "a credential is never in a URL", and the reasons it is
    /// acceptable HERE:</b> it grants exactly one capability — to be this one kiosk, whose only
    /// power is to add ratings to the room it is linked to. It reads nothing, it cannot reach the
    /// hub, and it is revocable per device in one click. The alternative — a human typing a code on
    /// 20 tablets after every storage eviction, during an event — is the failure we are actually
    /// trying to avoid. The page strips the parameter from the address bar the moment it is used,
    /// so it does not sit on screen or in the back stack.</para>
    ///
    /// <para>🔒 Revoke by rotating: issuing a new enrolment link invalidates the old one, and
    /// clearing it stops the device re-enrolling at all.</para>
    /// </remarks>
    public string? KioskEnrolmentTokenHash { get; set; }

    /// <summary>When the current enrolment token was issued, for the audit trail on the device row.</summary>
    public DateTimeOffset? KioskEnrolmentIssuedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
