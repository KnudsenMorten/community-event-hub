using CommunityHub.Core.Data;
using CommunityHub.Core.Domain.Evaluation;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Evaluation;

/// <summary>
/// §753 — device self-service onboarding: a unit asks, a human approves, the unit collects its key.
/// </summary>
/// <remarks>
/// <para>🔴 <b>This replaces "the organiser pre-types 25 identifiers"</b> (brief C1). The security
/// guarantee is unchanged and is worth restating because the change looks like a loosening and is
/// not: <b>a request in the queue has no key, no upload URL, and cannot post a single response</b>.
/// Self-<i>request</i> is not self-registration. What we gave up is hand-transcription — which was
/// itself a hazard, since a mistyped MAC creates a unit that can never authenticate and presents as
/// a firmware fault.</para>
///
/// <para>🔒 <b>The bootstrap secret is checked by the CALLER (the controller), not here.</b> This
/// service is about the queue; the endpoint is about who may reach it. Keeping them apart means the
/// approval path — which runs as a signed-in organiser and has no secret — does not have to pretend
/// to hold one.</para>
/// </remarks>
public sealed class EvaluationProvisioningService
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public EvaluationProvisioningService(CommunityHubDbContext db, TimeProvider? clock = null)
    {
        _db = db;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>What the endpoint should tell the device.</summary>
    public enum Outcome
    {
        /// <summary>Queued (or re-queued) and waiting for a human. HTTP 202.</summary>
        Pending = 0,

        /// <summary>Approved and the key is being handed over, once. HTTP 200.</summary>
        Approved = 1,

        /// <summary>Approved earlier and the key already collected. HTTP 200, no key.</summary>
        AlreadyProvisioned = 2,

        /// <summary>Refused by a human, or the device was later revoked. HTTP 403.</summary>
        Rejected = 3,
    }

    /// <summary>
    /// The provisioning answer. <paramref name="DeviceKey"/> is non-null ONLY on the single call that
    /// transitions an approved request into a live device.
    /// </summary>
    public sealed record Result(
        Outcome Outcome,
        string? DeviceKey,
        EvaluationDevice? Device,
        EvaluationDeviceProvisionRequest Request);

    /// <summary>
    /// A device asking to be onboarded, or an already-onboarded device re-checking its state.
    /// </summary>
    /// <remarks>
    /// 🔑 <b>Idempotent on <c>serialNumber</c>.</b> A unit polls on a long backoff until approved; every
    /// poll refreshes the same row rather than creating another. Without that, a fortnight of hourly
    /// polling would bury the other 24 units in the approval queue under one box's history.
    /// </remarks>
    public async Task<Result> RequestAsync(
        int eventId, string serialNumber, string? firmwareVersion,
        string? note, CancellationToken ct = default)
    {
        var id = (serialNumber ?? string.Empty).Trim();
        if (id.Length == 0) throw new ArgumentException("serialNumber is required.", nameof(serialNumber));

        var now = _clock.GetUtcNow();

        var row = await _db.EvaluationDeviceProvisionRequests
            .FirstOrDefaultAsync(r => r.EventId == eventId && r.SerialNumber == id, ct);

        if (row is null)
        {
            row = new EvaluationDeviceProvisionRequest
            {
                EventId = eventId,
                SerialNumber = id,
                RequestedAt = now,
                CreatedAt = now,
                Status = EvaluationProvisionStatus.Pending,
            };
            _db.EvaluationDeviceProvisionRequests.Add(row);
        }

        // 🔑 §753.2 (operator: *"i dont get it why we have both deviceid + serial number. seems
        // redundant"* → *"apply SerialNumber everywhere"*). There is now ONE identifier and it is the
        // number printed on the enclosure. The old deviceId/serialNumber pair is gone: two names for
        // one thing is exactly what he objected to, and the second existed only for a hardware case
        // the vendor has never confirmed.
        //
        // ⚠️ The consequence, stated where it is decided: the PRINTED LABEL IS NOW LOAD-BEARING.
        // §8.4 of the contract stops being a nice-to-have — if a unit ships without a readable
        // serial, it has no identity anyone can match to a room.
        if (!string.IsNullOrWhiteSpace(firmwareVersion)) row.FirmwareVersion = firmwareVersion.Trim();
        if (!string.IsNullOrWhiteSpace(note)) row.Note = note.Trim();
        row.LastRequestedAt = now;
        row.RequestCount += 1;
        row.UpdatedAt = now;

        if (row.Status == EvaluationProvisionStatus.Rejected)
        {
            await _db.SaveChangesAsync(ct);
            return new Result(Outcome.Rejected, null, null, row);
        }

        if (row.Status == EvaluationProvisionStatus.Pending)
        {
            await _db.SaveChangesAsync(ct);
            return new Result(Outcome.Pending, null, null, row);
        }

        // ---- Approved ------------------------------------------------------------------------
        var device = row.EvaluationDeviceId is int devId
            ? await _db.EvaluationDevices.FirstOrDefaultAsync(d => d.Id == devId, ct)
            : null;

        // 🔒 A device REVOKED after approval must stop being provisionable, or revocation would be
        // undone by the unit's next hourly check — the control would leak away exactly when used.
        if (device is not null && !device.IsActive)
        {
            await _db.SaveChangesAsync(ct);
            return new Result(Outcome.Rejected, null, device, row);
        }

        if (row.KeyIssuedAt is not null && device is not null)
        {
            await _db.SaveChangesAsync(ct);
            return new Result(Outcome.AlreadyProvisioned, null, device, row);
        }

        // First collection after approval: mint the device and hand the key over ONCE.
        var key = EvaluationIngestService.NewKey();

        if (device is null)
        {
            device = new EvaluationDevice
            {
                EventId = eventId,
                SerialNumber = row.SerialNumber,
                Label = $"Serial {row.SerialNumber}",
                FirmwareVersion = row.FirmwareVersion,
                IsActive = true,
                HealthTelemetryEnabled = true,
                CreatedAt = now,
                UpdatedAt = now,
                KeyHash = EvaluationIngestService.HashKey(key),
            };
            _db.EvaluationDevices.Add(device);
            await _db.SaveChangesAsync(ct);       // materialise the id for the link below
            row.EvaluationDeviceId = device.Id;
        }
        else
        {
            device.KeyHash = EvaluationIngestService.HashKey(key);
            device.UpdatedAt = now;
        }

        row.KeyIssuedAt = now;
        row.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        return new Result(Outcome.Approved, key, device, row);
    }

    /// <summary>
    /// An organiser approving a queued request. The device row is NOT created here.
    /// </summary>
    /// <remarks>
    /// 🔑 <b>The device is minted when the unit next calls in, not at approval.</b> The key can only
    /// be shown once, and the only moment it is useful is the moment the device is listening. Minting
    /// at approval would mean generating a credential into an empty room and hoping the unit asks
    /// before anyone reads the screen.
    /// </remarks>
    public async Task<bool> ApproveAsync(int requestId, string byEmail, CancellationToken ct = default)
    {
        var row = await _db.EvaluationDeviceProvisionRequests
            .FirstOrDefaultAsync(r => r.Id == requestId, ct);
        if (row is null || row.Status == EvaluationProvisionStatus.Approved) return false;

        var now = _clock.GetUtcNow();
        row.Status = EvaluationProvisionStatus.Approved;
        row.DecidedAt = now;
        row.DecidedByEmail = byEmail;
        row.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Refuse a request. 🔒 The row is KEPT — a rejected identifier that keeps asking is worth
    /// seeing, and re-approving should be a deliberate act rather than a side effect of a delete.
    /// </summary>
    public async Task<bool> RejectAsync(int requestId, string byEmail, CancellationToken ct = default)
    {
        var row = await _db.EvaluationDeviceProvisionRequests
            .FirstOrDefaultAsync(r => r.Id == requestId, ct);
        if (row is null) return false;

        var now = _clock.GetUtcNow();
        row.Status = EvaluationProvisionStatus.Rejected;
        row.DecidedAt = now;
        row.DecidedByEmail = byEmail;
        row.UpdatedAt = now;

        // A device already minted for this request is revoked too — otherwise "reject" would leave a
        // working credential behind, which is the opposite of what the word means.
        if (row.EvaluationDeviceId is int devId)
        {
            var device = await _db.EvaluationDevices.FirstOrDefaultAsync(d => d.Id == devId, ct);
            if (device is not null) { device.IsActive = false; device.UpdatedAt = now; }
        }

        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// The health block the device is told to obey — used by BOTH the provisioning response and the
    /// heartbeat response, so the two can never disagree.
    /// </summary>
    public async Task<TelemetryDirective> HealthDirectiveAsync(
        EvaluationDevice device, CancellationToken ct = default)
    {
        var windows = await _db.EvaluationTelemetryWindows
            .Where(w => w.EventId == device.EventId
                        && (w.EvaluationDeviceId == null || w.EvaluationDeviceId == device.Id))
            .ToListAsync(ct);

        var decision = EvaluationTelemetryPolicy.Evaluate(device, windows, _clock.GetUtcNow());
        return new TelemetryDirective(decision.Enabled, DefaultIntervalSeconds, DefaultRecheckSeconds);
    }

    /// <summary>
    /// 🔑 SERVER-CONTROLLED, deliberately (contract v2 §8.2). The operator asked whether telemetry
    /// should be every 1 or every 5 minutes; making it a value the device reads means neither side
    /// has to commit — 5 minutes now, 1 minute on the event day, without a firmware release.
    /// 5 min × 25 devices costs ~0.3 MB across the event, comfortably inside a 500 MB IoT SIM plan,
    /// where always-on 1-minute telemetry would be ~2.6 GB over the plan's 10-year life.
    /// </summary>
    public const int DefaultIntervalSeconds = 300;

    /// <summary>How often an approved unit re-checks its state. One hour: a human has to click.</summary>
    public const int DefaultRecheckSeconds = 3600;

    /// <summary>The <c>health</c> block in the wire contract.</summary>
    public sealed record TelemetryDirective(bool SendHealth, int IntervalSeconds, int NextCheckAfter);
}
