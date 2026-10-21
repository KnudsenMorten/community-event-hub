using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Tests.Scenario;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// 🔴 §1250 — share-tracking noise is stripped off LinkedIn / X URLs on the way IN.
/// </summary>
/// <remarks>
/// <para>Operator 2026-09-20, after four speaker pushes failed and the error mail asked him to
/// chase the speakers: *"i propose that you fix this yourself in the importer or inside ceh for
/// linkedin and twitter. this is generic issues that is easy to fix"* · *"strip after ? etc"*.</para>
///
/// <para>✅ All four real failures are pinned here as cases. The event platform rejected them with
/// <c>"Sorry, an unexpected error has occurred"</c> (× 3) and <c>"The Linkedin URL cannot exceed
/// 125 characters"</c> — the last one over the limit ENTIRELY because of the tracking suffix, so
/// removing the cause removes the length problem too.</para>
/// </remarks>
public sealed class SocialUrlNormalizerTests
{
    [Theory]
    // The four that actually failed on PROD.
    [InlineData("https://twitter.com/cbrhh?s=09", "https://twitter.com/cbrhh")]
    [InlineData("https://twitter.com/paulismama?lang=de", "https://twitter.com/paulismama")]
    [InlineData("https://twitter.com/@ErlendRushfeldt", "https://twitter.com/ErlendRushfeldt")]
    [InlineData("https://www.linkedin.com/in/sourabhmhaisekar?utm_source=share&utm_campaign=share_via"
                + "&utm_content=profile&utm_medium=android_app",
                "https://www.linkedin.com/in/sourabhmhaisekar")]
    // And the other shapes that arrive from a phone.
    [InlineData("https://www.linkedin.com/in/sandra-kiel/", "https://www.linkedin.com/in/sandra-kiel")]
    [InlineData("https://x.com/someone#about", "https://x.com/someone")]
    public void Tracking_noise_is_stripped(string input, string expected)
    {
        Assert.Equal(expected, SocialUrlNormalizer.Clean(input));
    }

    [Theory]
    [InlineData("https://www.linkedin.com/in/christopherbrumm")]
    [InlineData("https://twitter.com/cbrhh")]
    public void A_clean_url_is_returned_UNCHANGED(string url)
    {
        // 🔒 Cleaning must be a no-op on a good URL, or every import would dirty every row.
        Assert.Equal(url, SocialUrlNormalizer.Clean(url));
    }

    [Fact]
    public void The_host_is_never_touched()
    {
        // ⚠️ The '@' rule is PATH-only. An '@' before the first path separator is credentials or a
        // typo; stripping it there would mangle a URL rather than repair one.
        Assert.Equal("https://twitter.com", SocialUrlNormalizer.Clean("https://twitter.com/"));
        Assert.Equal("https://user@host.example/x", SocialUrlNormalizer.Clean("https://user@host.example/x"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_in_blank_out(string? input) => Assert.Null(SocialUrlNormalizer.Clean(input));

    [Fact]
    public void The_length_problem_disappears_with_its_cause()
    {
        const string url = "https://www.linkedin.com/in/sourabhmhaisekar?utm_source=share"
                         + "&utm_campaign=share_via&utm_content=profile&utm_medium=android_app";
        Assert.Equal(127, url.Length);

        var cleaned = SocialUrlNormalizer.Clean(url)!;

        Assert.True(cleaned.Length <= 125, "the platform's limit is 125");
        Assert.Equal(44, cleaned.Length);
    }

    // ---- the repair of values already stored -----------------------------------------------

    private static async Task<SpeakerProfile> ImportThenReimportAsync(
        CommunityHubDbContext db, string storedTwitter, bool speakerEdited)
    {
        db.Events.Add(new Event
        {
            Id = 1, CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 10), EndDate = new DateOnly(2027, 2, 10),
        });
        var p = new Participant
        {
            EventId = 1, Email = "s@x.invalid", FullName = "S", Role = ParticipantRole.Speaker,
            IsActive = true, LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();

        var prof = new SpeakerProfile
        {
            EventId = 1, ParticipantId = p.Id, FirstName = "S", LastName = "X",
            Twitter = storedTwitter,
        };
        if (speakerEdited) prof.MarkSpeakerEdited(SpeakerProfile.BioFields.Twitter, DateTimeOffset.UtcNow);
        db.SpeakerProfiles.Add(prof);
        await db.SaveChangesAsync();

        var (import, _) = ScenarioFixture.NewImporter(db);
        await import.ImportSpeakersAsync(
            1,
            new[] { new SessionizeSpeaker("s@x.invalid", "S", "X", null, SessionizeId: "spk-1") },
            Array.Empty<string>(),
            sendWelcome: false);

        return db.SpeakerProfiles.Single();
    }

    [Fact]
    public async Task A_dirty_url_ALREADY_STORED_is_repaired_by_the_next_import()
    {
        // 🔑 Without this the four speakers already failing would stay broken for ever: the parser
        // cleans what ARRIVES, but a delta import never overwrites a non-empty field. The fix
        // would have looked like it had not worked.
        using var db = ScenarioFixture.NewDb();

        var prof = await ImportThenReimportAsync(db, "https://twitter.com/cbrhh?s=09", speakerEdited: false);

        Assert.Equal("https://twitter.com/cbrhh", prof.Twitter);
    }

    [Fact]
    public async Task A_url_the_SPEAKER_edited_is_left_exactly_as_they_wrote_it()
    {
        // 🔒 The repair is for values that came FROM the call-for-speakers tool. A speaker's own
        // edit is theirs — the same rule every other bio field follows.
        using var db = ScenarioFixture.NewDb();

        var prof = await ImportThenReimportAsync(db, "https://twitter.com/mine?s=09", speakerEdited: true);

        Assert.Equal("https://twitter.com/mine?s=09", prof.Twitter);
    }
}
