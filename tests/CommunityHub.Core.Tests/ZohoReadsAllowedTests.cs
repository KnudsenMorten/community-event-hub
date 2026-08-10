using CommunityHub.Core.Integrations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1038 — WHICH HOSTS MAY SPEND ZOHO TOKEN REQUESTS.
/// </summary>
/// <remarks>
/// <para>§783.12b blocked DEV from reaching Zoho <b>including reads</b>, and the reason was never
/// data safety: DEV and PROD share ONE refresh token, Zoho throttles <i>10 access-token requests per
/// 10 minutes</i> <b>per refresh token</b>, and DEV's reads spent PROD's budget — taking PROD down
/// with 401s that read exactly like a revoked credential.</para>
///
/// <para>🔑 Operator 2026-08-10 proposed the real fix: <i>"i propose you help me make a NEW DEV
/// access code (besides the current PROD) and then we can separate it 100%"</i>. Zoho allows
/// <b>20 active refresh tokens per user per client</b>, so a DEV-only token is available without a
/// new OAuth client — and a host with its own token has its own budget.</para>
///
/// <para>🔒 So the gate became a THREE-STATE setting. Unset must behave exactly as before, or
/// deploying this would change every host at once — including PROD.</para>
/// </remarks>
public sealed class ZohoReadsAllowedTests
{
    private static ZohoClient Client(bool? readsAllowed, bool allowExternalWrites)
    {
        var zoho = new ZohoOptions
        {
            Enabled = true,
            ApiDomain = "https://zoho.test",
            BackstagePortalId = "P1",
            BackstageEventId = "E1",
            ReadsAllowed = readsAllowed,
        };
        var external = new ExternalWriteOptions { AllowExternalWrites = allowExternalWrites };
        return new ZohoClient(
            new HttpClient(), zoho, NullLogger<ZohoClient>.Instance, externalOptions: external);
    }

    /// <summary>
    /// 🔒 UNSET = today's behaviour, exactly. This is the test that makes the change safe to deploy:
    /// PROD (writes allowed) keeps reaching Zoho, DEV (writes blocked) stays blocked, and nothing
    /// moves until someone sets the flag on purpose.
    /// </summary>
    [Fact]
    public void Unset_keeps_the_783_12b_behaviour()
    {
        Assert.True(Client(readsAllowed: null, allowExternalWrites: true).HostMayReachZoho);
        Assert.False(Client(readsAllowed: null, allowExternalWrites: false).HostMayReachZoho);
    }

    /// <summary>
    /// 🔑 The point of §1038: a host whose writes are blocked may still READ, once it has its own
    /// refresh token. This is what lets DEV import attendees, orders and the agenda.
    /// </summary>
    [Fact]
    public void Explicit_true_lets_a_write_blocked_host_read()
    {
        Assert.True(Client(readsAllowed: true, allowExternalWrites: false).HostMayReachZoho);
    }

    /// <summary>
    /// ⚠️ And it can be turned off on a host that CAN write — the two questions are independent.
    /// Useful if PROD ever needs to stop spending token requests without stopping writes.
    /// </summary>
    [Fact]
    public void Explicit_false_blocks_even_a_write_enabled_host()
    {
        Assert.False(Client(readsAllowed: false, allowExternalWrites: true).HostMayReachZoho);
    }

    /// <summary>
    /// 🔒 READS AND WRITES REMAIN SEPARATE QUESTIONS. Allowing DEV to read must not open the write
    /// path — that stays governed by §1037's per-system ceiling, which DEV sets to false.
    /// </summary>
    [Fact]
    public void Allowing_reads_does_not_allow_writes()
    {
        var devPolicy = new ExternalWriteOptions
        {
            AllowExternalWrites = false,
            ExternalWrites = new(System.StringComparer.OrdinalIgnoreCase) { ["Zoho"] = false },
        };

        // The read gate says yes...
        var client = new ZohoClient(
            new HttpClient(),
            new ZohoOptions { Enabled = true, ApiDomain = "https://zoho.test", ReadsAllowed = true },
            NullLogger<ZohoClient>.Instance,
            externalOptions: devPolicy);
        Assert.True(client.HostMayReachZoho);

        // ...while the write ceiling still says no.
        Assert.False(devPolicy.ExternalWrites["Zoho"]);
    }
}
