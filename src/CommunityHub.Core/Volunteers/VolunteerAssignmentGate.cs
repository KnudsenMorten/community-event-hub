using CommunityHub.Core.Data;
using CommunityHub.Core.Settings;

namespace CommunityHub.Core.Volunteers;

/// <summary>
/// §1295 — the ONE place that answers "does this volunteer get work?" (operator 2026-09-21:
/// <i>"i want to start onboarding and activation of volunteers. but i dont want volunteer task
/// assignment to happen. we just put ring gate on that functionality if not made"</i>).
/// </summary>
/// <remarks>
/// <para>🔴 <b>Why this is a service and not another inline check.</b> A ring gate on assignment
/// already existed — on TWO of the five write paths. <c>/Organizer/VolunteerStructure</c> checked
/// it in the PAGE and <c>VolunteerAllocationService.CommitAsync</c> in the service, while the
/// supervisor dashboard, the allocation-scenario commit and the CSV plan import wrote
/// <c>VolunteerTaskAssignment</c> rows with no check at all. A gate that lives in a page is a gate
/// the next call site does not inherit; this one is a constructor dependency of every service that
/// can create an assignment, so a new path cannot quietly skip it.</para>
///
/// <para><b>One key, both halves</b> (<see cref="FeatureKey"/>), because a volunteer notices both
/// and they are the same decision: may they be ASSIGNED, and may they SEE what they were assigned.
/// Settable per edition in <c>/Organizer/Settings</c> → Features → Volunteers: the on/off holds it
/// for everyone, the ring widens it (Ring 1 test volunteers → Ring 2 → Ring 3 everyone), which is
/// the "move things to next rings" he asked for.</para>
///
/// <para>🔑 It is deliberately NOT the existing <c>volunteer-tasks</c> / <c>volunteer-allocation</c>
/// keys. Those are Queue surfaces, so the Settings page renders them "not ring-scoped" with no ring
/// selector at all (§589), and re-classifying them would make <c>_HubGrid</c> hide the organizer's
/// own Volunteers tiles — they badge and gate those tiles, and both are catalog-default OFF. Those
/// two keep meaning "which organizer TOOLS are on"; this one means "who gets work".</para>
/// </remarks>
public sealed class VolunteerAssignmentGate
{
    /// <summary>
    /// The feature that governs volunteer task assignment and the volunteer's own assignments
    /// surface. UserImpact, so BOTH its on/off and its released ring apply.
    /// </summary>
    public const string FeatureKey = "volunteer-assignments";

    /// <summary>
    /// What a refused assignment says. It names the Settings control, because "out of ring" with no
    /// way to change it is the §694/§698 complaint — a control that does not state what it governs.
    /// </summary>
    public const string OutOfRingMessage =
        "Volunteer task assignment is not released to that volunteer yet, so nothing was assigned. "
        + "Turn on “Volunteer task assignment (who gets work)” in Settings → Features, "
        + "or raise its ring to include them.";

    private readonly FeatureGateService _gate;
    private readonly RingResolver _rings;

    public VolunteerAssignmentGate(FeatureGateService gate, RingResolver rings)
    {
        _gate = gate;
        _rings = rings;
    }

    /// <summary>
    /// Convenience construction over a DbContext — for tests and the few call sites that build
    /// their dependencies by hand. DI resolves the real constructor.
    /// </summary>
    public static VolunteerAssignmentGate For(CommunityHubDbContext db)
        => new(new FeatureGateService(db), new RingResolver(db));

    /// <summary>
    /// May <paramref name="participantId"/> be assigned to volunteer work in this edition? True
    /// only when the feature is ON and their effective ring is at or below its released ring.
    /// </summary>
    public Task<bool> MayAssignAsync(
        int eventId, int participantId, CancellationToken ct = default)
        => _gate.IsFeatureActiveForParticipantAsync(FeatureKey, eventId, participantId, _rings, ct);

    /// <summary>
    /// The assignable subset of <paramref name="participantIds"/> — the batch form for the importer,
    /// the scenario commit and the allocation queue, which decide over many targets at once. Reads
    /// the switch and the released ring ONCE, so a 127-task plan does not re-read them per row.
    /// </summary>
    public async Task<IReadOnlySet<int>> FilterAssignableAsync(
        int eventId, IEnumerable<int> participantIds, CancellationToken ct = default)
    {
        var ids = participantIds.Distinct().ToList();
        if (ids.Count == 0) return new HashSet<int>();

        // The kill switch first: off ⇒ nobody, whatever their ring.
        if (!await _gate.IsFeatureEnabledAsync(FeatureKey, eventId, ct)) return new HashSet<int>();

        var released = await _gate.GetReleasedRingAsync(FeatureKey, eventId, ct);
        var assignable = new HashSet<int>();
        foreach (var id in ids)
        {
            var ring = await _rings.GetEffectiveRingAsync(id, ct);
            if (Rings.IsActiveForRing(ring, released)) assignable.Add(id);
        }
        return assignable;
    }

    /// <summary>
    /// May this volunteer see their OWN assignments — the "My Schedule" page and the menu item that
    /// leads to it? The same switch and ring as <see cref="MayAssignAsync"/>: a volunteer who
    /// cannot be given work has nothing to read there, and a page that says so is a page that
    /// invites them to ask why it is empty.
    /// </summary>
    public Task<bool> MaySeeOwnAssignmentsAsync(
        int eventId, int participantId, CancellationToken ct = default)
        => MayAssignAsync(eventId, participantId, ct);
}
