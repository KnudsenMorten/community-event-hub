using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1080 stage 2 — the suppression list and the signed unsubscribe link.
/// </summary>
/// <remarks>
/// <para>🔴 <b>This is the stage that has to exist before the first campaign.</b> The legal basis for
/// the imported list is the existing-customer relationship (his decision, 2026-08-12), and that
/// basis requires a working unsubscribe. An unsubscribe link that arrives in the second campaign is
/// the one that gets a sending IP listed.</para>
/// </remarks>
public sealed class MailSuppressionTests
{
    private const int EventId = 42;
    private const string Secret = "a-test-signing-secret";

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"suppress-{Guid.NewGuid():N}").Options);

    private static MailSuppressionService New(CommunityHubDbContext db, string? secret = Secret) =>
        new(db, secret);

    [Fact]
    public async Task An_unsubscribe_is_recorded_and_then_reported()
    {
        using var db = NewDb();
        var svc = New(db);

        Assert.False(await svc.IsSuppressedAsync(EventId, "someone@x.test"));
        Assert.True(await svc.SuppressAsync(EventId, "Someone@X.test", MailSuppressionReason.Unsubscribed));

        // 🔑 Case and whitespace are not identity — the address is normalised on both sides.
        Assert.True(await svc.IsSuppressedAsync(EventId, "  someone@x.test "));
    }

    /// <summary>Idempotent: a second unsubscribe is not an error, and the FIRST reason is kept.</summary>
    [Fact]
    public async Task Suppressing_twice_keeps_the_first_reason()
    {
        using var db = NewDb();
        var svc = New(db);

        Assert.True(await svc.SuppressAsync(EventId, "a@x.test", MailSuppressionReason.Unsubscribed));
        Assert.False(await svc.SuppressAsync(EventId, "a@x.test", MailSuppressionReason.HardBounce));

        var row = await db.MailSuppressions.SingleAsync();
        Assert.Equal(MailSuppressionReason.Unsubscribed, row.Reason);
    }

    /// <summary>
    /// ⚠️ No address ⇒ treated as SUPPRESSED. A blank recipient cannot be written to, and answering
    /// "not suppressed" would send a batch job looking for somewhere to deliver it.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_missing_address_is_treated_as_suppressed(string? email)
    {
        using var db = NewDb();
        Assert.True(await New(db).IsSuppressedAsync(EventId, email));
    }

    [Fact]
    public async Task Rubbish_is_never_stored_as_a_suppression()
    {
        using var db = NewDb();
        Assert.False(await New(db).SuppressAsync(EventId, "not-an-address", MailSuppressionReason.Manual));
        Assert.Empty(db.MailSuppressions);
    }

    /// <summary>People do ask to be put back on; the only way back must not be a database edit.</summary>
    [Fact]
    public async Task An_organizer_can_undo_a_suppression()
    {
        using var db = NewDb();
        var svc = New(db);
        await svc.SuppressAsync(EventId, "a@x.test", MailSuppressionReason.Unsubscribed);

        Assert.True(await svc.UnsuppressAsync(EventId, "a@x.test"));
        Assert.False(await svc.IsSuppressedAsync(EventId, "a@x.test"));
    }

    /// <summary>
    /// 🔑 The batch check: one query, then filter in memory. Fifty round-trips per batch is how a
    /// send job becomes the slowest thing in the system.
    /// </summary>
    [Fact]
    public async Task The_suppressed_set_is_one_query_and_matches_case_insensitively()
    {
        using var db = NewDb();
        var svc = New(db);
        await svc.SuppressAsync(EventId, "a@x.test", MailSuppressionReason.Unsubscribed);
        await svc.SuppressAsync(EventId, "b@x.test", MailSuppressionReason.HardBounce);

        var set = await svc.SuppressedSetAsync(EventId);

        Assert.Equal(2, set.Count);
        Assert.Contains("A@X.test", set);
    }

    // ---- the signed link -------------------------------------------------

    [Fact]
    public void A_link_verifies_for_the_address_it_was_made_for()
    {
        using var db = NewDb();
        var svc = New(db);

        var token = svc.TokenFor(EventId, "someone@x.test");

        Assert.True(svc.VerifyToken(EventId, "someone@x.test", token));
        Assert.True(svc.VerifyToken(EventId, "SOMEONE@X.TEST", token));   // normalised
    }

    /// <summary>
    /// 🔴 The property that makes the link safe: editing the address in the URL invalidates it, so
    /// nobody can unsubscribe somebody else by guessing.
    /// </summary>
    [Fact]
    public void A_link_cannot_be_reused_for_another_address_or_another_edition()
    {
        using var db = NewDb();
        var svc = New(db);
        var token = svc.TokenFor(EventId, "someone@x.test");

        Assert.False(svc.VerifyToken(EventId, "someone-else@x.test", token));
        Assert.False(svc.VerifyToken(EventId + 1, "someone@x.test", token));
        Assert.False(svc.VerifyToken(EventId, "someone@x.test", token + "x"));
        Assert.False(svc.VerifyToken(EventId, "someone@x.test", null));
    }

    /// <summary>A different secret cannot mint a link ours accepts.</summary>
    [Fact]
    public void A_link_signed_with_another_secret_is_refused()
    {
        using var db = NewDb();
        var theirs = New(db, "a-different-secret").TokenFor(EventId, "someone@x.test");

        Assert.False(New(db).VerifyToken(EventId, "someone@x.test", theirs));
    }

    /// <summary>
    /// 🔴 <b>No secret ⇒ no links, and the caller must refuse to send.</b> Failing closed is the
    /// only safe answer: a mass mailing whose unsubscribe link is dead costs the sending reputation
    /// and the legal basis at once.
    /// </summary>
    [Fact]
    public void With_no_secret_no_link_can_be_made_and_none_verifies()
    {
        using var db = NewDb();
        var svc = New(db, secret: null);

        Assert.False(svc.CanMakeLinks);
        Assert.Equal(string.Empty, svc.TokenFor(EventId, "someone@x.test"));
        Assert.Equal(string.Empty, svc.LinkFor("https://hub.test", EventId, "someone@x.test"));
        Assert.Equal(string.Empty, svc.FooterHtml("https://hub.test", EventId, "someone@x.test", "C"));
        Assert.False(svc.VerifyToken(EventId, "someone@x.test", "anything"));
    }

    [Fact]
    public void The_link_carries_the_address_and_the_signature()
    {
        using var db = NewDb();
        var svc = New(db);

        var link = svc.LinkFor("https://hub.test/", EventId, "Someone+tag@x.test");

        Assert.StartsWith("https://hub.test/unsubscribe?e=", link);
        Assert.Contains("%2Btag", link);            // the address is URL-escaped
        Assert.Contains("&t=", link);
    }

    /// <summary>
    /// 🔴 The footer says WHY they are receiving it. A legal basis nobody is told about reads as
    /// spam, whatever the law says.
    /// </summary>
    [Fact]
    public void The_footer_states_the_reason_and_offers_the_way_out()
    {
        using var db = NewDb();

        var footer = New(db).FooterHtml("https://hub.test", EventId, "a@x.test", "Experts Live Denmark");

        Assert.Contains("taken part in a Experts Live Denmark event before", footer);
        Assert.Contains("Unsubscribe from event mails", footer);
        Assert.Contains("/unsubscribe?e=a%40x.test", footer);
    }
}
