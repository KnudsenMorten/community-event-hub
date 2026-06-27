using System.Net;
using CommunityHub.Core.Integrations.Graphics;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// SSRF guard for the Sessionize-supplied speaker picture URL (security hardening):
/// <see cref="HttpSpeakerPictureFetcher.IsBlockedAddress"/> must reject every
/// private/loopback/link-local/unique-local range so a crafted URL can't reach internal
/// services or the cloud metadata endpoint (169.254.169.254), and must allow ordinary
/// public addresses so real imports keep working.
/// </summary>
public sealed class HttpSpeakerPictureFetcherTests
{
    [Theory]
    // Loopback
    [InlineData("127.0.0.1")]
    [InlineData("127.10.20.30")]
    [InlineData("::1")]
    // Private RFC1918
    [InlineData("10.0.0.1")]
    [InlineData("10.255.255.255")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.0.1")]
    [InlineData("192.168.1.100")]
    // Link-local (incl. the cloud metadata endpoint)
    [InlineData("169.254.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("fe80::1")]
    // Unique-local IPv6
    [InlineData("fc00::1")]
    [InlineData("fd12:3456:789a::1")]
    // Unspecified / "this network"
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    // IPv4-mapped IPv6 form of the metadata address must also be caught
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::ffff:10.0.0.1")]
    public void IsBlockedAddress_RejectsPrivateAndLoopbackRanges(string ip)
    {
        Assert.True(HttpSpeakerPictureFetcher.IsBlockedAddress(IPAddress.Parse(ip)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")]   // example.com
    [InlineData("172.15.0.1")]      // just below the 172.16/12 block
    [InlineData("172.32.0.1")]      // just above the 172.16/12 block
    [InlineData("169.253.0.1")]     // just below the 169.254/16 block
    [InlineData("2606:2800:220:1::1")] // public IPv6
    public void IsBlockedAddress_AllowsPublicAddresses(string ip)
    {
        Assert.False(HttpSpeakerPictureFetcher.IsBlockedAddress(IPAddress.Parse(ip)));
    }
}
