using System.Security.Cryptography;
using System.Text;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain.Evaluation;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Evaluation;

/// <summary>
/// §743 C4/C4a — the device-facing ingest path: authenticate a unit, store its button presses
/// idempotently, and record its heartbeat.
/// </summary>
/// <remarks>
/// <para>The contract this implements is <c>docs/internal-session-eval-device-api-contract.md</c>,
/// which is the document the vendor builds their firmware against. <b>Where this code and that
/// document disagree, the document wins</b> — their Phase 2 firmware is written from it, and we
/// cannot revise 25 fielded units as easily as we can revise a service.</para>
///
/// <para>🔒 <b>Per-record results, never a blanket success.</b> The device clears from its cache
/// only what we say we accepted. A 200 covering a partially-stored batch would make it discard
/// records we never kept — silent, permanent data loss on the exact path that exists to recover
/// data.</para>
///
/// <para>🔒 <b>Late is normal, not an error.</b> Units cache offline and flush when cellular
/// returns, so a record's age says nothing about its validity. Attribution uses the collection
/// timestamp regardless of arrival; the only hard stop is the event's ingest cut-off.</para>
/// </remarks>
public sealed class EvaluationIngestService
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public EvaluationIngestService(CommunityHubDbContext db, TimeProvider? clock = null)
    {
        _db = db;
        _clock = clock ?? TimeProvider.System;
    }

    // -------------------------------------------------------------------------
    // KEYS
    // -------------------------------------------------------------------------

    /// <summary>
    /// Hash a device key for storage — PBKDF2-SHA256, salt:hash, the same treatment
    /// <c>PinService.HashPin</c> gives a login PIN. We never store the key itself, so a database
    /// leak yields no working device credential.
    /// </summary>
    public static string HashKey(string key)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password: Encoding.UTF8.GetBytes(key),
            salt: salt, iterations: 100_000,
            hashAlgorithm: HashAlgorithmName.SHA256, outputLength: 32);
        return $"{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    /// <summary>Constant-time verify against a stored salt:hash, so a wrong key cannot be found by timing.</summary>
    public static bool VerifyKey(string candidate, string? storedHash)
    {
        if (string.IsNullOrWhiteSpace(storedHash)) return false;
        var parts = storedHash.Split(':', 2);
        if (parts.Length != 2) return false;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[0]);
            expected = Convert.FromBase64String(parts[1]);
        }
        catch (FormatException) { return false; }

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            password: Encoding.UTF8.GetBytes(candidate ?? string.Empty),
            salt: salt, iterations: 100_000,
            hashAlgorithm: HashAlgorithmName.SHA256, outputLength: 32);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Generate a device key: 32 bytes of CSPRNG output, base64url, safe in a header.</summary>
    public static string NewKey() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    // -------------------------------------------------------------------------
    // AUTH
    // -------------------------------------------------------------------------

    /// <summary>Why a request was refused. Maps to the HTTP status the contract promises.</summary>
    public enum AuthFailure { None, UnknownDevice, BadKey }

    /// <summary>
    /// Resolve + authenticate a unit from its identifier and presented key.
    /// </summary>
    /// <remarks>
    /// 🔒 <b>Both the CURRENT and the PREVIOUS key are accepted.</b> That is what makes a staggered
    /// OTA key rotation safe: a unit still running the old firmware keeps authenticating until the
    /// rollout finishes. With one key, rotating it partially bricks a fleet that has no screen to
    /// explain itself.
    ///
    /// <para>An inactive (revoked) device is treated as UNKNOWN on purpose — revocation must not be
    /// distinguishable from "never registered" by anyone holding a stolen key.</para>
    /// </remarks>
    public async Task<(EvaluationDevice? Device, AuthFailure Failure)> AuthenticateAsync(
        string? serialNumber, string? presentedKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(serialNumber)) return (null, AuthFailure.UnknownDevice);

        // Matched EXACTLY — the identifier is opaque (MAC or serial, §743 item 10).
        var device = await _db.EvaluationDevices
            .FirstOrDefaultAsync(d => d.SerialNumber == serialNumber && d.IsActive, ct);
        if (device is null) return (null, AuthFailure.UnknownDevice);

        if (string.IsNullOrWhiteSpace(presentedKey)) return (null, AuthFailure.BadKey);

        var ok = VerifyKey(presentedKey, device.KeyHash)
                 || VerifyKey(presentedKey, device.PreviousKeyHash);

        return ok ? (device, AuthFailure.None) : (null, AuthFailure.BadKey);
    }

    // -------------------------------------------------------------------------
    // INGEST
    // -------------------------------------------------------------------------

    /// <summary>One record as the device sends it.</summary>
    public sealed record IncomingRecord(
        string? DeviceRecordId, int Rating, DateTimeOffset? CollectionTimestamp);

    /// <summary>Per-record outcome. The device clears its cache from this, so it is load-bearing.</summary>
    public sealed record RecordResult(string? DeviceRecordId, string Status, string? Reason = null);

    public static class RecordStatus
    {
        public const string Accepted = "accepted";
        /// <summary>Already stored under this id. A SUCCESS — the idempotent replay path.</summary>
        public const string Duplicate = "duplicate";
        /// <summary>Permanently refused. Retrying cannot change the outcome, so the device drops it.</summary>
        public const string Rejected = "rejected";
    }

    public static class RejectReason
    {
        public const string EventClosed = "event_closed";
        public const string InvalidRating = "invalid_rating";
        public const string InvalidTimestamp = "invalid_timestamp";
        public const string Malformed = "malformed";
    }

    /// <summary>
    /// Store a batch. Returns one result per record, in the order received.
    /// </summary>
    /// <remarks>
    /// <para>🔒 <b>Idempotency is enforced by a UNIQUE INDEX</b> on (SerialNumber, DeviceRecordId), not
    /// merely by the pre-read below. The pre-read collapses the common case (a device re-flushing a
    /// batch we already have) into a cheap "duplicate"; the index is what holds when a retry arrives
    /// while the first request is still committing. A read-then-write check alone would let that
    /// race double-count, and double-counting is invisible in a rating distribution.</para>
    ///
    /// <para>A batch containing the SAME record id twice is handled too — the second occurrence sees
    /// the first in the in-batch set and reports duplicate, rather than failing the whole save.</para>
    /// </remarks>
    public async Task<IReadOnlyList<RecordResult>> IngestAsync(
        EvaluationDevice device,
        IReadOnlyList<IncomingRecord> records,
        string? firmwareVersion,
        DateTimeOffset? clockSyncedAt,
        bool eventClosed,
        CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var results = new List<RecordResult>(records.Count);

        // One query for the whole batch rather than one per record.
        var incomingIds = records
            .Select(r => r.DeviceRecordId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct()
            .ToList();

        var known = incomingIds.Count == 0
            ? new HashSet<string>(StringComparer.Ordinal)
            : (await _db.EvaluationResponses
                .Where(r => r.SerialNumber == device.SerialNumber
                            && r.DeviceRecordId != null
                            && incomingIds.Contains(r.DeviceRecordId))
                .Select(r => r.DeviceRecordId!)
                .ToListAsync(ct))
              .ToHashSet(StringComparer.Ordinal);

        var seenInBatch = new HashSet<string>(StringComparer.Ordinal);
        var toAdd = new List<EvaluationResponse>();

        // §743 C3 — the candidate sessions for THIS device's room, loaded once for the batch.
        // An unlinked device has none, and its presses stay venue-and-date only (§5.3) until an
        // organiser links it — at which point the sync's backfill attributes them retrospectively.
        var roomSessions = device.RoomId is int roomId
            ? await _db.EvaluationSessions
                .Where(s => s.EventId == device.EventId && s.RoomId == roomId)
                .ToListAsync(ct)
            : new List<Domain.Evaluation.EvaluationSession>();

        foreach (var rec in records)
        {
            if (string.IsNullOrWhiteSpace(rec.DeviceRecordId))
            {
                // No idempotency key ⇒ we could never deduplicate it. Refusing is safer than
                // storing something a retry would silently duplicate.
                results.Add(new RecordResult(rec.DeviceRecordId, RecordStatus.Rejected, RejectReason.Malformed));
                continue;
            }

            // 🔒 The event's ingest cut-off. A unit found in a drawer weeks later must not
            // retroactively change a published result. Logged as a rejection, never dropped silently.
            if (eventClosed)
            {
                results.Add(new RecordResult(rec.DeviceRecordId, RecordStatus.Rejected, RejectReason.EventClosed));
                continue;
            }

            if (rec.Rating is < 1 or > 4)
            {
                results.Add(new RecordResult(rec.DeviceRecordId, RecordStatus.Rejected, RejectReason.InvalidRating));
                continue;
            }

            if (rec.CollectionTimestamp is not DateTimeOffset collected)
            {
                results.Add(new RecordResult(rec.DeviceRecordId, RecordStatus.Rejected, RejectReason.InvalidTimestamp));
                continue;
            }

            if (known.Contains(rec.DeviceRecordId) || !seenInBatch.Add(rec.DeviceRecordId))
            {
                results.Add(new RecordResult(rec.DeviceRecordId, RecordStatus.Duplicate));
                continue;
            }

            toAdd.Add(new EvaluationResponse
            {
                EventId = device.EventId,
                SerialNumber = device.SerialNumber,
                // §743 §5.3 — the session running in THIS device's room at the moment of the
                // press. Resolved from the collection timestamp, never the arrival time, so a
                // batch flushed hours late still lands on the session it was collected during.
                // NULL stays a legitimate outcome (no room linked, or genuinely outside every
                // window): the response is kept, counts toward the event pooled score, and the
                // sync's backfill can attribute it later.
                SessionId = EvaluationAttribution.Resolve(roomSessions, collected)?.Id,
                Rating = rec.Rating,
                CollectionTimestamp = collected.ToUniversalTime(),
                ReceivedTimestamp = now,
                DeviceRecordId = rec.DeviceRecordId,
                Source = EvaluationResponseSources.Device,
                FirmwareVersion = firmwareVersion,
                ClockSyncedAt = clockSyncedAt,
                // §5.5 — a collection time in the FUTURE beyond tolerance cannot be legitimate.
                // Flagged, still counted: flagging is not filtering, and the flag is what lets an
                // implausible result be traced to a drifting unit rather than blamed on a speaker.
                TimestampSuspect = collected > now.AddMinutes(SkewToleranceMinutes),
            });

            results.Add(new RecordResult(rec.DeviceRecordId, RecordStatus.Accepted));
        }

        if (toAdd.Count > 0)
        {
            _db.EvaluationResponses.AddRange(toAdd);
            await _db.SaveChangesAsync(ct);
        }

        // The unit is alive if it is uploading, even when it never sends a heartbeat.
        await TouchAsync(device, firmwareVersion, clockSyncedAt, ct);

        return results;
    }

    /// <summary>
    /// §5.5 — how far ahead a device clock may be before its records are marked suspect.
    /// ❓ The real tolerance is still open with the vendor (§743 item 12), so this is a placeholder
    /// that must become configuration before the fleet ships, not a constant to inherit silently.
    /// </summary>
    public const int SkewToleranceMinutes = 5;

    // -------------------------------------------------------------------------
    // HEARTBEAT
    // -------------------------------------------------------------------------

    /// <summary>
    /// Record a check-in. Because the units sleep and duty-cycle, this is the ONLY thing that
    /// distinguishes "asleep and healthy" from "dead" — liveness can never be inferred from a
    /// connection.
    /// </summary>
    public async Task HeartbeatAsync(
        EvaluationDevice device, int? batteryPercent, int? signalQuality,
        int? cachedRecordCount, string? firmwareVersion, DateTimeOffset? clockSyncedAt,
        CancellationToken ct = default)
    {
        device.LastBatteryPercent = batteryPercent;
        device.LastSignalQuality = signalQuality;
        device.CachedRecordCount = cachedRecordCount;
        await TouchAsync(device, firmwareVersion, clockSyncedAt, ct);
    }

    private async Task TouchAsync(
        EvaluationDevice device, string? firmwareVersion, DateTimeOffset? clockSyncedAt,
        CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        device.LastSeenAt = now;
        device.UpdatedAt = now;
        if (!string.IsNullOrWhiteSpace(firmwareVersion)) device.FirmwareVersion = firmwareVersion;
        if (clockSyncedAt is not null) device.ClockSyncedAt = clockSyncedAt;
        await _db.SaveChangesAsync(ct);
    }
}
