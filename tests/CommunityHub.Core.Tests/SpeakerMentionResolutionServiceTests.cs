using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §858.16c — the scheduled sweep that caches a speaker's mentionable person URN, and the
/// <c>{Speakers}</c> token that reads it.
///
/// <para>🔒 The rule these protect: resolution happens in the JOB (LinkedIn carries a DAY throttle and
/// a member URN never changes); the token only READS. A token that called LinkedIn would be slow,
/// throttled, and wrong.</para>
/// </summary>
public sealed class SpeakerMentionResolutionServiceTests
{
    private static CommunityHubDbContext NewDb() => new(
        new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"mention-{Guid.NewGuid():N}").Options);

    // ---------- name selection ----------

    /// <summary>
    /// §884.1 — the sweep now works from the PERSON, so the only name it has is the full name.
    /// The SURNAME must land in the last slot, because that is what the search keyword is built
    /// from and a surname is far more selective than a given name (§858.16h).
    /// </summary>
    [Theory]
    [InlineData("Jan Vidar Elven", "Jan Vidar", "Elven")]
    [InlineData("Morten Leth Hedegaard", "Morten Leth", "Hedegaard")]
    [InlineData("Morten Knudsen", "Morten", "Knudsen")]
    [InlineData("Cher", "Cher", null)]
    [InlineData("  ", null, null)]
    public void A_full_name_splits_with_the_surname_last(
        string fullName, string? expectedFirst, string? expectedLast)
    {
        var (first, last) = SpeakerMentionResolutionService.SplitName(fullName);

        Assert.Equal(expectedFirst, first);
        Assert.Equal(expectedLast, last);
    }

    // ---------- the {Speakers} token reads the cache ----------

    /// <summary>
    /// The whole point of the feature, in one assertion: a follower is mentioned, a non-follower
    /// appears as their plain FULL NAME so the reader still sees who spoke and the operator can tag
    /// them by hand. 16 of 22 real speakers are on the left; 6 are on the right.
    /// </summary>
    [Fact]
    public async Task Speakers_renders_a_mention_for_a_cached_urn_and_a_plain_name_without_one()
    {
        using var db = NewDb();
        var session = await SeedSessionAsync(db,
            ("Morten Knudsen", "urn:li:person:c3hh-hQmZ3"),   // follower — cached URN
            ("Nikki Chapple", null));                          // non-follower — no URN

        var values = await new SoMeVariableResolver(db).SessionValuesAsync(session);

        Assert.Equal(
            "@[Morten Knudsen](urn:li:person:c3hh-hQmZ3) | Nikki Chapple",
            values["Speakers"]);
    }

    /// <summary>
    /// §858.4 — {SpeakerNames} is the deliberately UNTAGGED spelling and must not change. Two
    /// spellings, one rule: adding mentions must not silently rewrite existing copy.
    /// </summary>
    [Fact]
    public async Task SpeakerNames_stays_untagged_when_Speakers_carries_mentions()
    {
        using var db = NewDb();
        var session = await SeedSessionAsync(db, ("Morten Knudsen", "urn:li:person:c3hh-hQmZ3"));

        var values = await new SoMeVariableResolver(db).SessionValuesAsync(session);

        Assert.Equal("Morten Knudsen", values["SpeakerNames"]);
        Assert.DoesNotContain("urn:li:person", values["SpeakerNames"] ?? string.Empty);
    }

    /// <summary>A post with no speakers prints no line rather than "no speakers yet" (§824.15).</summary>
    [Fact]
    public async Task No_speakers_yields_no_value_rather_than_a_placeholder()
    {
        using var db = NewDb();
        var session = await SeedSessionAsync(db);

        var values = await new SoMeVariableResolver(db).SessionValuesAsync(session);

        Assert.Null(values["Speakers"]);
    }

    /// <summary>
    /// §858.16 — the mention the token emits must be exactly what the publisher keeps verbatim.
    /// If these two ever disagree, every mention publishes as visible markup.
    /// </summary>
    [Fact]
    public async Task The_token_output_survives_the_commentary_escaper()
    {
        using var db = NewDb();
        var session = await SeedSessionAsync(db, ("Morten Knudsen", "urn:li:person:c3hh-hQmZ3"));

        var values = await new SoMeVariableResolver(db).SessionValuesAsync(session);
        var escaped = LiveLinkedInPostPublisher.EscapeCommentaryPreservingMentions(
            $"Meet {values["Speakers"]} at ELDK27 (room 300)");

        Assert.Contains("@[Morten Knudsen](urn:li:person:c3hh-hQmZ3)", escaped);
        Assert.Contains(@"\(room 300\)", escaped);
    }

    // ---------- the 30-minute cadence is only safe because of these skips (§884.4) ----------

    /// <summary>
    /// A resolved person is NEVER asked again — a member URN does not change, and re-asking would
    /// spend a day-limited quota on a question already answered.
    /// </summary>
    [Fact]
    public void Somebody_with_an_id_is_skipped_forever()
        => Assert.True(SpeakerMentionResolutionService.ShouldSkip(
            1, "urn:li:person:AAA", DateTimeOffset.UtcNow.AddYears(-1), DateTimeOffset.UtcNow));

    /// <summary>
    /// The case the half-hourly cadence exists for: somebody added minutes ago has never been
    /// checked, so they are asked about on the very next run and can be tagged within the hour.
    /// </summary>
    [Fact]
    public void Somebody_never_checked_is_always_asked()
        => Assert.False(SpeakerMentionResolutionService.ShouldSkip(
            1, null, null, DateTimeOffset.UtcNow));

    /// <summary>
    /// 🔒 The rule that makes 30 minutes affordable. ~30 non-followers asked 48×/day would be ~1,500
    /// calls against a DAY limit; the quota would be gone and every answer would become
    /// LookupFailed — which reads exactly like "nobody follows the page" (§858.16h).
    /// </summary>
    [Theory]
    [InlineData(1, true)]      // asked an hour ago    ⇒ still trusted, skip
    [InlineData(23, true)]     // just inside the day  ⇒ skip
    [InlineData(25, false)]    // a day later          ⇒ ask again, they may have followed since
    public void A_negative_answer_is_trusted_for_a_day_then_re_asked(int hoursAgo, bool expectSkip)
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(expectSkip, SpeakerMentionResolutionService.ShouldSkip(
            1, null, now.AddHours(-hoursAgo), now));
    }

    private static async Task<int> SeedSessionAsync(
        CommunityHubDbContext db, params (string FullName, string? Urn)[] speakers)
    {
        var ev = new Event { CommunityName = "Demo", Code = "DEMO", DisplayName = "Demo 2027" };
        db.Events.Add(ev);
        await db.SaveChangesAsync();

        var session = new Session { EventId = ev.Id, Title = "A session", Track = "Security" };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();

        foreach (var (fullName, urn) in speakers)
        {
            var p = new Participant { EventId = ev.Id, FullName = fullName, IsActive = true };
            db.Participants.Add(p);
            await db.SaveChangesAsync();

            // §884.1 — the URN lives on the PERSON now, not on the speaker profile.
            p.LinkedInPersonUrn = urn;
            p.LinkedInPersonUrnStatus = urn is null ? "NotAFollower" : "Resolved";
            await db.SaveChangesAsync();

            db.SpeakerProfiles.Add(new SpeakerProfile { EventId = ev.Id, ParticipantId = p.Id });
            db.SessionSpeakers.Add(new SessionSpeaker { SessionId = session.Id, ParticipantId = p.Id });
            await db.SaveChangesAsync();
        }

        return session.Id;
    }
}
