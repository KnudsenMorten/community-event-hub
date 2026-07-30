using CommunityHub.Core.Settings;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §514 — the outbound-email master ring is a CEILING on every ring-gated send:
/// <c>BrevoEmailSender</c> resolves the audience as <c>MIN(ring(outbound-email), ring(feature))</c>
/// and, in its own words, "never LOOSENS".
///
/// <para>Releasing a feature to Broad while the master sits at Ring 2 therefore does nothing, and
/// the page used to display the broader ring as if it applied. The operator found it by example:
/// *Speaker profile change alerts* at Ring 3 (broad) whose mail "will not arrive". These tests pin
/// the detection so the page can never silently overstate an audience again.</para>
/// </summary>
public sealed class TransportRingCapTests
{
    private static FeatureState State(FeatureDescriptor d, Ring released, Ring? transport) =>
        new(d, Enabled: true, IsPersisted: true, ReleasedToRing: released, OverrideRing: released,
            EffectiveGroup: d.Group, GroupRing: released, TransportRing: transport);

    /// <summary>A ring-scoped (user-impact) feature — the shape the transport clamps.</summary>
    private static FeatureDescriptor RingScoped() =>
        FeatureCatalog.All.First(d => d.IsRingScoped && d.Key != FeatureCatalog.OutboundEmailKey);

    /// <summary>An engine/backend feature — rings never gate it, so no cap can apply.</summary>
    private static FeatureDescriptor NotRingScoped() =>
        FeatureCatalog.All.First(d => !d.IsRingScoped);

    [Fact]
    public void A_feature_released_broader_than_the_master_is_reported_as_capped()
    {
        // The operator's exact case: feature Broad, master Ring 2.
        var s = State(RingScoped(), released: Ring.Broad, transport: Ring.Ring2);

        Assert.True(s.IsCappedByTransport);
        Assert.Equal(Ring.Ring2, s.EffectiveMailRing);
    }

    [Fact]
    public void A_feature_at_or_below_the_master_is_not_capped()
    {
        var equal = State(RingScoped(), released: Ring.Ring2, transport: Ring.Ring2);
        var below = State(RingScoped(), released: Ring.Ring1, transport: Ring.Ring2);

        Assert.False(equal.IsCappedByTransport);
        Assert.False(below.IsCappedByTransport);
        Assert.Equal(Ring.Ring2, equal.EffectiveMailRing);
        Assert.Equal(Ring.Ring1, below.EffectiveMailRing);
    }

    [Fact]
    public void A_broad_master_caps_nothing()
    {
        // The recommended end state: the master stops overruling deliberate per-feature decisions.
        var s = State(RingScoped(), released: Ring.Broad, transport: Ring.Broad);

        Assert.False(s.IsCappedByTransport);
        Assert.Equal(Ring.Broad, s.EffectiveMailRing);
    }

    [Fact]
    public void A_feature_that_is_not_ring_scoped_is_never_reported_as_capped()
    {
        // Rings do not gate an engine feature at all, so claiming a cap would be a new false
        // statement in place of the old one — the §326by mistake repeated.
        var s = State(NotRingScoped(), released: Ring.Broad, transport: Ring.Ring1);

        Assert.False(s.IsCappedByTransport);
    }

    [Fact]
    public void The_master_switch_never_warns_about_itself()
    {
        var master = FeatureCatalog.Find(FeatureCatalog.OutboundEmailKey);
        Assert.NotNull(master);

        var s = State(master!, released: Ring.Broad, transport: Ring.Ring1);
        Assert.False(s.IsCappedByTransport);
    }

    [Fact]
    public void With_no_transport_ring_known_nothing_is_claimed()
    {
        // Older call sites construct FeatureState without the transport ring. Absent information
        // must read as "no warning", never as a cap at ring 0.
        var s = State(RingScoped(), released: Ring.Broad, transport: null);

        Assert.False(s.IsCappedByTransport);
        Assert.Equal(Ring.Broad, s.EffectiveMailRing);
    }

    [Fact]
    public void The_cap_condition_matches_the_transports_own_condition()
    {
        // The transport applies the MIN when FeatureCatalog.Find(key)?.IsRingScoped == true.
        // If the page used a different test it would warn where no clamp happens, or stay silent
        // where one does. Assert the two agree across the whole catalog.
        foreach (var d in FeatureCatalog.All)
        {
            if (d.Key == FeatureCatalog.OutboundEmailKey) continue;

            var s = State(d, released: Ring.Broad, transport: Ring.Ring1);
            Assert.Equal(d.IsRingScoped, s.IsCappedByTransport);
        }
    }
}
