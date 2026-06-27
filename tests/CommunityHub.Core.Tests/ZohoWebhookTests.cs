using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Host-free unit tests for the §128 Zoho Backstage order-change webhook helpers:
/// shared-secret authorization (constant-time, header OR query) and the defensive
/// order-id / action payload parse.
/// </summary>
public class ZohoWebhookTests
{
    // ---- Shared-secret authorization ----------------------------------------

    [Fact]
    public void Authorized_when_query_token_matches()
        => Assert.True(ZohoWebhook.IsAuthorized("s3cret", providedHeader: null, providedQuery: "s3cret"));

    [Fact]
    public void Authorized_when_header_matches()
        => Assert.True(ZohoWebhook.IsAuthorized("s3cret", providedHeader: "s3cret", providedQuery: null));

    [Fact]
    public void Rejected_when_secret_wrong()
        => Assert.False(ZohoWebhook.IsAuthorized("s3cret", providedHeader: "nope", providedQuery: "nope"));

    [Fact]
    public void Rejected_when_nothing_provided()
        => Assert.False(ZohoWebhook.IsAuthorized("s3cret", providedHeader: null, providedQuery: null));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Rejected_when_expected_secret_unconfigured(string? expected)
        => Assert.False(ZohoWebhook.IsAuthorized(expected, providedHeader: "anything", providedQuery: "anything"));

    [Fact]
    public void Secret_match_is_case_sensitive()
        => Assert.False(ZohoWebhook.IsAuthorized("Secret", providedHeader: null, providedQuery: "secret"));

    // ---- Payload parsing -----------------------------------------------------

    [Fact]
    public void Parses_top_level_order_id_and_action()
    {
        var p = ZohoWebhook.ParsePayload("""{"action":"order.update","order_id":"O-123"}""");
        Assert.Equal("O-123", p.OrderId);
        Assert.Equal("order.update", p.Action);
        Assert.False(p.IsCancellation);
    }

    [Fact]
    public void Parses_numeric_order_id()
    {
        var p = ZohoWebhook.ParsePayload("""{"event":"EventOrder.Create","id":987654}""");
        Assert.Equal("987654", p.OrderId);
    }

    [Fact]
    public void Parses_order_id_from_nested_envelope()
    {
        var p = ZohoWebhook.ParsePayload("""{"event_type":"order.cancel","data":{"order_id":"O-9"}}""");
        Assert.Equal("O-9", p.OrderId);
        Assert.True(p.IsCancellation);
    }

    [Theory]
    [InlineData("order.cancel", true)]
    [InlineData("EventOrder.Delete", true)]
    [InlineData("order.refund", true)]
    [InlineData("order.update", false)]
    [InlineData("attendee.create", false)]
    public void Cancellation_hint_is_detected_from_action(string action, bool expected)
    {
        var p = ZohoWebhook.ParsePayload($$"""{"action":"{{action}}","order_id":"O-1"}""");
        Assert.Equal(expected, p.IsCancellation);
    }

    [Fact]
    public void Order_specific_key_wins_over_generic_id()
    {
        // Attendee envelope: "id" is the attendee id, "order_id" is what we want.
        var p = ZohoWebhook.ParsePayload("""{"id":"attendee-5","order_id":"O-77"}""");
        Assert.Equal("O-77", p.OrderId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"foo":"bar"}""")]
    public void Returns_null_order_id_when_absent_or_malformed(string body)
    {
        var p = ZohoWebhook.ParsePayload(body);
        Assert.Null(p.OrderId);
    }
}
