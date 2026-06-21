namespace CommunityHub.Core.Settings;

/// <summary>
/// The release RING of a resource (and of a feature's "released-to" level) — the
/// progressive-rollout access level that, together with the hard kill switch,
/// decides whether an advanced feature is active for a given person/company
/// (REQUIREMENTS §23).
///
/// LOWER ring = EARLIER access. A feature is "released to" a ring; a resource
/// whose <b>effective ring</b> is at or below (≤) the released ring sees the
/// feature. A feature released to <see cref="Broad"/> (the default) is therefore
/// visible to EVERYONE (every resource's effective ring is ≤ Broad), so the
/// default behaviour is unchanged: nothing ring-assigned ⇒ everyone sees it.
///
/// This REPLACES the older dev/test/prod stage + <c>#Test=On</c> idea: a person's
/// RING is their access level (assigning Ring0/Ring1 IS the "always see test"
/// override). The SAME ring definitions apply in dev and prod — the resolver is
/// not environment-specific.
/// </summary>
public enum Ring
{
    /// <summary>Ring 0 — dev/earliest. Sees features released to ANY ring (0–3).</summary>
    Ring0 = 0,

    /// <summary>Ring 1 — internal test. Sees features released to ring 1, 2 or 3.</summary>
    Ring1 = 1,

    /// <summary>Ring 2 — design-partner test. Sees features released to ring 2 or 3.</summary>
    Ring2 = 2,

    /// <summary>
    /// Ring 3 — broad / general availability. The DEFAULT for every resource and
    /// the default "released-to" ring for a feature. A Ring3 resource sees ONLY
    /// fully-released (Ring3) features; a Ring3-released feature is visible to all.
    /// </summary>
    Broad = 3,
}

/// <summary>
/// Friendly names + the effective-ring resolution rule for <see cref="Ring"/>.
/// </summary>
public static class Rings
{
    /// <summary>The default ring for an unassigned resource and a feature's default released-to ring.</summary>
    public const Ring Default = Ring.Broad;

    /// <summary>All rings in order, lowest (earliest) first.</summary>
    public static readonly IReadOnlyList<Ring> All = new[]
    {
        Ring.Ring0, Ring.Ring1, Ring.Ring2, Ring.Broad,
    };

    /// <summary>
    /// The EFFECTIVE ring of a sponsor CONTACT: the contact's own ring SUPERSEDES
    /// the company ring, which is the default for the company's contacts; if
    /// neither is set the platform default (<see cref="Ring.Broad"/>) applies.
    /// <c>effectiveRing = contact.Ring ?? company.Ring ?? Broad</c>.
    /// </summary>
    public static Ring Effective(Ring? contactRing, Ring? companyRing) =>
        contactRing ?? companyRing ?? Default;

    /// <summary>
    /// The effective ring of a NON-sponsor resource (speaker / volunteer /
    /// attendee), which has no company default: its own ring or the platform
    /// default.
    /// </summary>
    public static Ring Effective(Ring? resourceRing) => resourceRing ?? Default;

    /// <summary>
    /// Is a feature RELEASED to <paramref name="releasedTo"/> active for a resource
    /// whose effective ring is <paramref name="effectiveRing"/>? Active iff the
    /// resource's ring is at or below (earlier than) the released ring —
    /// <c>effectiveRing &lt;= releasedTo</c>. (Kill-switch is a separate gate.)
    /// </summary>
    public static bool IsActiveForRing(Ring effectiveRing, Ring releasedTo) =>
        (int)effectiveRing <= (int)releasedTo;

    /// <summary>A short stable label ("Ring 0" … "Ring 3 (broad)") for the admin GUI / logs.</summary>
    public static string Label(Ring ring) => ring switch
    {
        Ring.Ring0 => "Ring 0 (dev)",
        Ring.Ring1 => "Ring 1 (test - internal)",
        Ring.Ring2 => "Ring 2 (test - design partner)",
        Ring.Broad => "Ring 3 (broad)",
        _ => ring.ToString(),
    };
}
