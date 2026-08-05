using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Entitlements;

/// <summary>
/// §6.10 — whether the claim freeze is ENFORCED on this host.
/// </summary>
/// <remarks>
/// 🔒 <b>OFF by default, and the default is deliberate.</b> This is the only change in the §768/§769
/// series that takes something away from a speaker. It shipped with the switch off so it could go to
/// production without changing anybody's experience, and be turned on after the operator has
/// validated it — the same shape as <c>PhotoCleanup:DryRun</c>, and for the same reason: config, so
/// enabling it is an act with a diff rather than a redeploy.
/// </remarks>
public sealed class TravelClaimLockOptions
{
    public const string SectionName = "TravelClaim";

    /// <summary>False ⇒ a submitted claim is recorded but never frozen.</summary>
    public bool FreezeEnabled { get; set; }
}

/// <summary>
/// §6.10 / §768.10 D5 — the ONE place that answers "may this speaker still change their travel
/// claim?", and the one place that reopens it.
/// </summary>
/// <remarks>
/// <para>🔒 <b>Server-side, by rule.</b> The work order is explicit: <i>"Enforce server-side, not by
/// hiding a button. A stale browser tab or a direct POST must fail identically."</i> So every write
/// path — upload a receipt, delete a receipt, re-save the claim — asks THIS, and none of them decide
/// for themselves. A lock enforced at three call sites is a lock with three chances to be forgotten.</para>
///
/// <para>🔑 <b>The freeze is only safe because the reopen exists.</b> A speaker who submits after
/// uploading 3 of 5 receipts would otherwise be locked out with no recovery path — the exact
/// consequence work-order §6.10 asked to be flagged rather than assumed. The operator's answer
/// (§768.10 D5) was that an organizer CAN reopen, audit-logged.</para>
/// </remarks>
public sealed class TravelClaimLock
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;
    private readonly TravelClaimLockOptions _options;

    public TravelClaimLock(
        CommunityHubDbContext db, TimeProvider? clock = null, TravelClaimLockOptions? options = null)
    {
        _db = db;
        _clock = clock ?? TimeProvider.System;
        _options = options ?? new TravelClaimLockOptions();
    }

    /// <summary>Whether the freeze is being ENFORCED on this host.</summary>
    public bool Enforcing => _options.FreezeEnabled;

    /// <summary>The message a frozen claim shows — one wording, wherever the refusal happens.</summary>
    public const string FrozenMessage =
        "Your travel claim has been submitted, so it can no longer be changed. "
        + "If something is missing, ask the organizers to reopen it for you.";

    /// <summary>Is this speaker's claim frozen right now?</summary>
    /// <remarks>
    /// 🔒 <b>The switch is checked HERE, not at the three call sites.</b> Off ⇒ nobody is ever
    /// frozen, whatever the data says. Gating each caller instead would mean three chances to
    /// forget, and the one that got forgotten would be the one that locked a speaker out.
    ///
    /// <para>⚠️ <b>`SubmittedAt` is still stamped while the switch is off</b> (see
    /// <see cref="MarkSubmittedAsync"/>). The record of WHEN somebody submitted is true either way,
    /// and is worth having from the first day; only the ENFORCEMENT waits. The consequence to know:
    /// turning the switch on freezes everyone who has submitted since the deploy, immediately —
    /// which is the intended meaning of "submitted", not a surprise to design around.</para>
    /// </remarks>
    public async Task<bool> IsFrozenAsync(int eventId, int participantId, CancellationToken ct = default)
    {
        if (!_options.FreezeEnabled) return false;

        return await _db.TravelReimbursements
            .AsNoTracking()
            .AnyAsync(r => r.EventId == eventId
                           && r.ParticipantId == participantId
                           && r.SubmittedAt != null, ct);
    }

    /// <summary>
    /// Mark the claim submitted (idempotent — a second submit does not move the stamp).
    /// </summary>
    public async Task<bool> MarkSubmittedAsync(
        int eventId, int participantId, CancellationToken ct = default)
    {
        var row = await _db.TravelReimbursements
            .FirstOrDefaultAsync(r => r.EventId == eventId && r.ParticipantId == participantId, ct);
        if (row is null || row.SubmittedAt is not null) return false;

        row.SubmittedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// §768.10 D5 — an organizer returns a completed claim to the speaker so they can add what is
    /// missing and submit again. Audit-logged: who, when, and how many times it has happened.
    /// </summary>
    /// <returns>False when there was nothing to reopen (never submitted, or no claim at all).</returns>
    public async Task<bool> ReopenAsync(
        int eventId, int participantId, string? byEmail, CancellationToken ct = default)
    {
        var row = await _db.TravelReimbursements
            .FirstOrDefaultAsync(r => r.EventId == eventId && r.ParticipantId == participantId, ct);
        if (row is null || row.SubmittedAt is null) return false;

        var now = _clock.GetUtcNow();
        var submittedAt = row.SubmittedAt.Value;

        // 🔒 SubmittedAt is CLEARED, not kept alongside a "reopened" flag. The claim is genuinely
        // open again — every write path asks one question ("is SubmittedAt set?"), and a second
        // field that also had to be consulted is exactly how a lock ends up half-enforced.
        row.SubmittedAt = null;
        row.ReopenedAt = now;
        row.ReopenedByEmail = byEmail;
        row.ReopenCount++;

        _db.AuditEntries.Add(new AuditEntry
        {
            EventId = eventId,
            OccurredUtc = now,
            Category = AuditCategory.Admin,
            Action = "travel-claim.reopen",
            ActorEmail = byEmail ?? string.Empty,
            TargetType = "Participant",
            TargetId = participantId.ToString(),
            Summary = row.ReopenCount == 1
                ? "Reopened a submitted travel claim so the speaker can change it again."
                : $"Reopened a submitted travel claim (reopen #{row.ReopenCount}).",
            Detail = $"Submitted {submittedAt:u}; reopened after {(now - submittedAt).TotalHours:F1}h.",
            Outcome = AuditOutcome.Success,
            Source = AuditSource.Web,
        });

        await _db.SaveChangesAsync(ct);
        return true;
    }
}
