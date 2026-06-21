using CommunityHub.Core.Config;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Offline tests for the <c>ticketSale</c> block of
/// <see cref="EventEditionConfigLoader"/> — the config source behind the
/// site-wide topbar ticket banner (REQUIREMENTS §10). Pins the JSON parse + the
/// additive "absent block ⇒ null (fall back to literal)" contract.
/// </summary>
public sealed class TicketSaleConfigTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"ceh-ticketsale-{Guid.NewGuid():N}.json");

    private TicketSaleConfig? Load(string json)
    {
        File.WriteAllText(_path, json);
        return new EventEditionConfigLoader().Load(_path).TicketSale;
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void Missing_block_yields_null_so_the_layout_keeps_its_literal()
    {
        Assert.Null(Load("""{ "edition": { "code": "X" } }"""));
    }

    [Fact]
    public void Full_block_parses_all_fields()
    {
        var ts = Load("""
        {
          "ticketSale": {
            "enabled": true,
            "opensAtLocal": "2026-08-11T08:00:00",
            "ticketUrl": "https://tickets.test",
            "afterOpen": "hide"
          }
        }
        """);

        Assert.NotNull(ts);
        Assert.True(ts!.Enabled);
        Assert.Equal("2026-08-11T08:00:00", ts.OpensAtLocal);
        Assert.Equal("https://tickets.test", ts.TicketUrl);
        Assert.Equal("hide", ts.AfterOpen);
    }

    [Fact]
    public void Enabled_defaults_to_true_and_afterOpen_to_onsale_when_omitted()
    {
        var ts = Load("""
        {
          "ticketSale": { "opensAtLocal": "2026-08-11T08:00:00" }
        }
        """);

        Assert.NotNull(ts);
        Assert.True(ts!.Enabled);
        Assert.Equal("onsale", ts.AfterOpen);
        Assert.Equal(string.Empty, ts.TicketUrl);
    }
}
