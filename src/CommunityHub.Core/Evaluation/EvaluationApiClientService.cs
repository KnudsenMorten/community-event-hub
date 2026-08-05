using CommunityHub.Core.Data;
using CommunityHub.Core.Domain.Evaluation;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Evaluation;

/// <summary>
/// §747 C8 — issues, rotates and authenticates the SERVICE credentials external systems use to pull
/// reports.
/// </summary>
/// <remarks>
/// 🔒 <b>Key handling is not reimplemented here.</b> Hashing, verification and generation are
/// <see cref="EvaluationIngestService"/>'s static members, so machine credentials in this system have
/// exactly one implementation — PBKDF2-SHA256, salt:hash, constant-time compare.
/// </remarks>
public sealed class EvaluationApiClientService
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public EvaluationApiClientService(CommunityHubDbContext db, TimeProvider? clock = null)
    {
        _db = db;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Authenticate a presented key against the credentials for ONE event.
    /// </summary>
    /// <remarks>
    /// <para>🔒 <b>The event is part of authentication, not a filter applied afterwards.</b> A key
    /// issued for one edition never authorises another's reports, so the extraction constraint —
    /// authorise per event even with one event today — holds at the only place it can be
    /// bypassed.</para>
    ///
    /// <para>🔒 <b>Both the current and the previous key are accepted</b>, as with devices: a
    /// consumer is usually someone else's scheduled job, and rotating a credential must not require
    /// both sides to switch in the same second.</para>
    ///
    /// <para>Returns null for every failure — unknown, revoked, wrong event, bad key — because the
    /// caller must not be able to tell those apart.</para>
    /// </remarks>
    public async Task<EvaluationApiClient?> AuthenticateAsync(
        int eventId, string? presentedKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(presentedKey)) return null;

        // 🔑 There is no identifier on the wire — only a key — so every ACTIVE credential for this
        // event is a candidate. That is bounded (a handful of consumers), and it keeps the request
        // shape to a single header. PBKDF2 makes each check deliberately slow, so this must never be
        // let loose over an unbounded set: it is scoped by event and by IsActive for that reason.
        var candidates = await _db.EvaluationApiClients
            .Where(c => c.EventId == eventId && c.IsActive)
            .ToListAsync(ct);

        foreach (var c in candidates)
        {
            var ok = EvaluationIngestService.VerifyKey(presentedKey, c.KeyHash)
                     || EvaluationIngestService.VerifyKey(presentedKey, c.PreviousKeyHash);
            if (!ok) continue;

            c.LastUsedAt = _clock.GetUtcNow();
            await _db.SaveChangesAsync(ct);
            return c;
        }

        return null;
    }

    /// <summary>
    /// Issue a new credential. Returns the row and the PLAINTEXT key, which is the only time it
    /// exists — it is never stored and cannot be recovered, only rotated.
    /// </summary>
    public async Task<(EvaluationApiClient Client, string Key)> IssueAsync(
        int eventId, string name, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var key = EvaluationIngestService.NewKey();

        var client = new EvaluationApiClient
        {
            EventId = eventId,
            Name = name.Trim(),
            KeyHash = EvaluationIngestService.HashKey(key),
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.EvaluationApiClients.Add(client);
        await _db.SaveChangesAsync(ct);
        return (client, key);
    }

    /// <summary>
    /// Rotate a credential's key. The OLD key keeps working (it moves to
    /// <see cref="EvaluationApiClient.PreviousKeyHash"/>) until
    /// <see cref="RetirePreviousAsync"/> is called, so the consumer can switch over on its own
    /// schedule. Returns the new plaintext key, or null when the credential is not this event's.
    /// </summary>
    public async Task<string?> RotateAsync(int eventId, int id, CancellationToken ct = default)
    {
        var client = await _db.EvaluationApiClients
            .FirstOrDefaultAsync(c => c.Id == id && c.EventId == eventId, ct);
        if (client is null) return null;

        var key = EvaluationIngestService.NewKey();
        client.PreviousKeyHash = client.KeyHash;
        client.KeyHash = EvaluationIngestService.HashKey(key);
        client.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return key;
    }

    /// <summary>
    /// Close the rotation window: the previous key stops working. 🔑 This is the step that makes a
    /// rotation actually mean something — until it runs, a leaked old key is still valid.
    /// </summary>
    public async Task<bool> RetirePreviousAsync(int eventId, int id, CancellationToken ct = default)
    {
        var client = await _db.EvaluationApiClients
            .FirstOrDefaultAsync(c => c.Id == id && c.EventId == eventId, ct);
        if (client is null) return false;

        client.PreviousKeyHash = null;
        client.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Revoke or restore. Preferred over deletion: an audit should still show the credential existed
    /// and when it was last used.
    /// </summary>
    public async Task<bool> SetActiveAsync(
        int eventId, int id, bool active, CancellationToken ct = default)
    {
        var client = await _db.EvaluationApiClients
            .FirstOrDefaultAsync(c => c.Id == id && c.EventId == eventId, ct);
        if (client is null) return false;

        client.IsActive = active;
        client.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Every credential for the edition, newest first. Never exposes a key or a hash.</summary>
    public async Task<IReadOnlyList<EvaluationApiClient>> ListAsync(
        int eventId, CancellationToken ct = default) =>
        await _db.EvaluationApiClients
            .Where(c => c.EventId == eventId)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(ct);
}
