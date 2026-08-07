using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Settings;

/// <summary>
/// §940 — <b>RING 1 IMPLIES TEST DATA.</b> Operator 2026-08-07: <i>"when a person, nomatter the role
/// becomes a ring 1 user (test), you must set the flag IsTestUser = true"</i>.
/// </summary>
/// <remarks>
/// <para>🔴 <b>The bug this closes.</b> Before this rule, <see cref="Participant.IsTestUser"/> was set
/// by NOTHING in the application — only by the SQL seeds (<c>tools/seed-demo.sql</c>,
/// <c>Sync-CehParity.ps1</c>) and by the scenario seeder, all of which pick their rows by the
/// <c>test-*@</c> NAMING CONVENTION. A role-simulation account named anything else was a Ring-1
/// account that the rest of the platform read as a real person. Measured on PROD: participant 116
/// (<c>mlh-volunteer@</c>, Volunteer, Ring 1, Active) carried <c>IsTestUser = False</c>.</para>
///
/// <para>⚠️ <b>What that costs.</b> <see cref="Integrations.TestDataScope"/> (§905) reads this flag to
/// decide who may appear in SoMe subjects, tier lists and graphics. An unflagged Ring-1 account is a
/// real participant to every one of those — so a simulation account can be announced on the company
/// page.</para>
///
/// <para>🔑 <b>ONE-WAY, deliberately.</b> Moving OFF Ring 1 does <b>not</b> clear the flag (§940 asked
/// for that to be a decision, not an accident). Clearing it automatically would silently promote rows
/// that were created as test data into the real exports and the public surfaces — the exact failure
/// this rule exists to prevent, only in the dangerous direction. An organizer who genuinely converts a
/// test account into a real person clears the flag by hand.</para>
///
/// <para>🔑 <b>Ring 1 only</b> — his words were "ring 1 user (test)", and the platform already labels
/// Ring 1 "Test" / Ring 2 "Design" (<see cref="Rings.RingFlagLabel"/>). Ring 0 (dev) is not swept in
/// here without him saying so; it is captured as an open question instead.</para>
/// </remarks>
public static class TestUserRule
{
    /// <summary>The ring that means "this person is an internal test account".</summary>
    public const Ring TestRing = Ring.Ring1;

    /// <summary>Does this EFFECTIVE ring make its holder test data?</summary>
    public static bool ImpliesTestUser(Ring effectiveRing) => effectiveRing == TestRing;

    /// <summary>
    /// Assign <paramref name="ring"/> as <paramref name="p"/>'s OWN ring and apply §940 in the same
    /// step — the call every ring-assignment entry point makes instead of writing
    /// <see cref="Participant.Ring"/> directly.
    /// </summary>
    /// <returns>
    /// True when the row actually changed. 🔑 That includes the <b>repair</b> case: a participant
    /// already sitting on Ring 1 with the flag missing is reported as changed, so re-assigning the
    /// ring they already have fixes them rather than short-circuiting on "no ring change".
    /// </returns>
    public static bool AssignRing(Participant p, Ring ring)
    {
        var changed = false;
        if (p.Ring != ring)
        {
            p.Ring = ring;
            changed = true;
        }

        if (ApplyEffectiveRing(p, ring)) changed = true;
        return changed;
    }

    /// <summary>
    /// Apply §940 from an <b>effective</b> ring WITHOUT touching the stored ring — the sponsor-company
    /// path. A company's default ring is inherited by every contact still on the platform default
    /// (<see cref="RingResolver.EffectiveForContact"/>), so setting a company to Ring 1 really does
    /// make those contacts Ring-1 people, even though their own column never changes.
    /// </summary>
    /// <returns>True when the flag was set (it was previously false).</returns>
    public static bool ApplyEffectiveRing(Participant p, Ring effectiveRing)
    {
        if (!ImpliesTestUser(effectiveRing) || p.IsTestUser) return false;
        p.IsTestUser = true;
        return true;
    }
}
