using CommunityHub.Core.Auth;
using CommunityHub.Core.Domain;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// The acting-as session contract (parsed by <see cref="ActingAsClaims.Parse"/>,
/// which the web-layer CurrentParticipant delegates to). An impersonation /
/// secretary sign-in keeps the TARGET as the signed-in identity and adds marker
/// claims naming who is really acting; absence of the kind marker = a normal
/// session. This is the crux of impersonation scope + the "no nested
/// impersonation" guard, so it is asserted directly on the contract.
/// </summary>
public sealed class ActingAsClaimsTests
{
    [Fact]
    public void No_kind_marker_is_a_normal_session()
    {
        Assert.Null(ActingAsClaims.Parse(kind: null, actorParticipantId: null, actorLabel: null));
        Assert.Null(ActingAsClaims.Parse(kind: "garbage", actorParticipantId: "10", actorLabel: "x"));
    }

    [Fact]
    public void Organizer_marker_carries_the_acting_organizer()
    {
        var ctx = ActingAsClaims.Parse(
            ImpersonationActorKind.Organizer.ToString(), "10", "Org (org@example.test)");
        Assert.NotNull(ctx);
        Assert.Equal(ImpersonationActorKind.Organizer, ctx!.Kind);
        Assert.Equal(10, ctx.ActorParticipantId);
        Assert.Equal("Org (org@example.test)", ctx.ActorLabel);
    }

    [Fact]
    public void Secretary_marker_has_no_actor_participant()
    {
        var ctx = ActingAsClaims.Parse(
            ImpersonationActorKind.SecretaryToken.ToString(), actorParticipantId: null,
            actorLabel: "Secretary for VP");
        Assert.NotNull(ctx);
        Assert.Equal(ImpersonationActorKind.SecretaryToken, ctx!.Kind);
        Assert.Null(ctx.ActorParticipantId);
    }

    [Fact]
    public void Blank_label_falls_back_to_a_placeholder()
    {
        var ctx = ActingAsClaims.Parse(
            ImpersonationActorKind.Organizer.ToString(), "5", actorLabel: "   ");
        Assert.NotNull(ctx);
        Assert.Equal("(unknown)", ctx!.ActorLabel);
    }
}
