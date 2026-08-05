using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests.Scenario;

/// <summary>
/// §827 — a speaker who changes their e-mail in Sessionize stays ONE person, and the sign-in links
/// minted for the address they left stop working.
/// </summary>
/// <remarks>
/// <para>Measured on 2026-08-04, not imagined: a real speaker became two participants sharing one
/// <c>SessionizeSpeakerId</c>. His session moved to the new row leaving the original with none, he
/// was welcomed a second time, and he resurfaced as a pending speaker two days after completing Get
/// Started. The importer had his stable identity the whole time and matched on e-mail.</para>
///
/// <para>🔒 E-mail is a MUTABLE ATTRIBUTE of a person; the Sessionize id IS the person.</para>
/// </remarks>
public sealed class SessionizeEmailChangeTests
{
    private const string SpeakersJson = """
    [
      { "id": "spk-1", "firstName": "Thomas", "lastName": "Speaker", "fullName": "Thomas Speaker", "links": [] }
    ]
    """;

    private static SessionizeParseResult ParseWith(string email) =>
        SessionizeApiClient.ParseSpeakers(
            SpeakersJson, new Dictionary<string, string> { ["spk-1"] = email });

    private static async Task<(int EventId, int ParticipantId)> ImportOnceAsync(
        Data.CommunityHubDbContext db, string email)
    {
        var seed = await ScenarioSeed.SeedAsync(db);
        var (import, _) = ScenarioFixture.NewImporter(db);
        var parsed = ParseWith(email);

        await import.ImportSpeakersAsync(
            seed.EventId, parsed.Speakers, parsed.Warnings, sendWelcome: false);

        var p = await db.Participants.SingleAsync(x => x.Email == email);
        return (seed.EventId, p.Id);
    }

    [Fact]
    public async Task A_changed_email_moves_the_person_instead_of_forking_them()
    {
        using var db = ScenarioFixture.NewDb();
        var (eventId, originalId) = await ImportOnceAsync(db, "old@example.test");

        var (import, _) = ScenarioFixture.NewImporter(db);
        var changed = ParseWith("new@example.test");
        var result = await import.ImportSpeakersAsync(
            eventId, changed.Speakers, changed.Warnings, sendWelcome: false);

        // 🔒 ONE person, MOVED — not a second one created. (Scoped to this Sessionize speaker: the
        // scenario seed has other speakers of its own.)
        Assert.Equal(0, result.Created);

        var moved = await db.Participants.SingleAsync(p => p.Email == "new@example.test");
        Assert.Equal(originalId, moved.Id);
        // The old address must be GONE, not left behind as a second row — that fork is the defect.
        Assert.Empty(await db.Participants.Where(p => p.Email == "old@example.test").ToListAsync());

        // And still one profile for this Sessionize id, not two.
        Assert.Single(await db.SpeakerProfiles.Where(sp => sp.SessionizeSpeakerId == "spk-1").ToListAsync());
    }

    [Fact]
    public async Task The_links_minted_for_the_old_address_are_revoked()
    {
        using var db = ScenarioFixture.NewDb();
        var (eventId, participantId) = await ImportOnceAsync(db, "old@example.test");

        var now = DateTimeOffset.UtcNow;
        db.MagicLinkGrants.AddRange(
            new MagicLinkGrant
            {
                ParticipantId = participantId, RecipientEmail = "old@example.test",
                TokenIdHash = "hash-old", Purpose = "hub", MultiUse = true,
                CreatedAt = now.AddDays(-5), ExpiresAt = now.AddDays(90),
            },
            new MagicLinkGrant
            {
                ParticipantId = participantId, RecipientEmail = "new@example.test",
                TokenIdHash = "hash-new", Purpose = "hub", MultiUse = true,
                CreatedAt = now.AddDays(-1), ExpiresAt = now.AddDays(90),
            });
        await db.SaveChangesAsync();

        var (import, _) = ScenarioFixture.NewImporter(db);
        var changed = ParseWith("new@example.test");
        var result = await import.ImportSpeakersAsync(
            eventId, changed.Speakers, changed.Warnings, sendWelcome: false);

        var old = await db.MagicLinkGrants.SingleAsync(g => g.TokenIdHash == "hash-old");
        var fresh = await db.MagicLinkGrants.SingleAsync(g => g.TokenIdHash == "hash-new");

        // 🔒 The usual reason an address changes is that somebody left the company that owns the old
        // one — and these are MULTI-USE standing grants, so a link in that inbox is a live credential.
        Assert.NotNull(old.RevokedAt);
        // ⚠️ Scoped to the OLD address: a link already issued at the new one is theirs and must live.
        Assert.Null(fresh.RevokedAt);
        // Surfaced in the run summary, since this service has no audit trail of its own (§827.4).
        Assert.Equal(1, result.RevokedMagicLinks);
    }

    [Fact]
    public async Task An_unchanged_email_revokes_nothing()
    {
        using var db = ScenarioFixture.NewDb();
        var (eventId, participantId) = await ImportOnceAsync(db, "same@example.test");

        db.MagicLinkGrants.Add(new MagicLinkGrant
        {
            ParticipantId = participantId, RecipientEmail = "same@example.test",
            TokenIdHash = "hash-keep", Purpose = "hub", MultiUse = true,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-5), ExpiresAt = DateTimeOffset.UtcNow.AddDays(90),
        });
        await db.SaveChangesAsync();

        var (import, _) = ScenarioFixture.NewImporter(db);
        var again = ParseWith("same@example.test");
        var result = await import.ImportSpeakersAsync(
            eventId, again.Speakers, again.Warnings, sendWelcome: false);

        // The importer runs hourly (§825). Revoking on every pass would log the whole roster out.
        Assert.Equal(0, result.RevokedMagicLinks);
        Assert.Null((await db.MagicLinkGrants.SingleAsync()).RevokedAt);
    }
}
