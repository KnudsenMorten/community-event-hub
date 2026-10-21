using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Tests.Scenario;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1256 — the four noise sources measured in PROD on 2026-09-20, each of which asked the operator
/// to act on something that was either already answered, invisible, or not his to fix:
/// <list type="bullet">
/// <item>§1256 — an APPROVED speaker "disappeared" delta was ignored, so the alert repeated;</item>
/// <item>§1256c — a Sessionize biography arrived wrapped in <c>&lt;p&gt;</c> and was stored, shown
///   and mailed as markup;</item>
/// <item>§1256d — a title differing only by a double space was reported as a change, in a mail
///   whose own HTML collapsed the difference away.</item>
/// </list>
/// </summary>
public class AlertNoiseAndTextNormalizationTests
{
    // --- §1256: an approved speaker delta silences the alert ------------------

    private static async Task<(int ev, int profileId)> SeedLinkedSpeakerAsync(
        CommunityHub.Core.Data.CommunityHubDbContext db, string sessionizeId)
    {
        var e = new Event
        {
            CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        };
        db.Events.Add(e);
        await db.SaveChangesAsync();

        var p = new Participant
        {
            EventId = e.Id, Email = "gone@x.dk", FullName = "Gone Speaker",
            Role = ParticipantRole.Speaker, IsActive = false,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();

        var sp = new SpeakerProfile { EventId = e.Id, ParticipantId = p.Id, SessionizeSpeakerId = sessionizeId };
        db.SpeakerProfiles.Add(sp);
        await db.SaveChangesAsync();
        return (e.Id, sp.Id);
    }

    [Fact]
    public async Task A_speaker_missing_from_the_pull_is_reported_once()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, _) = await SeedLinkedSpeakerAsync(db, "sessionize-id-1");
        var detector = new SessionizeDisappearanceDetector(db);

        var result = await detector.ScanAsync(ev, Array.Empty<string>(), Array.Empty<string>());

        Assert.Single(result.DisappearedSpeakers);
    }

    [Fact]
    public async Task An_APPROVED_delta_stops_the_speaker_being_reported_again()
    {
        // 🔴 The live case: delta approved at 16:35, a fresh Pending one raised at 17:50, and the
        // alert arriving every 10 minutes because the answer was never read.
        using var db = ScenarioFixture.NewDb();
        var (ev, profileId) = await SeedLinkedSpeakerAsync(db, "sessionize-id-1");
        db.SyncDeltas.Add(new SyncDelta
        {
            EventId = ev,
            EntityType = SyncDeltaEntityType.Speaker,
            EntityId = profileId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ChangeKind = SyncDeltaChangeKind.Disappeared,
            Status = SyncDeltaStatus.Approved,
        });
        await db.SaveChangesAsync();
        var detector = new SessionizeDisappearanceDetector(db);

        var result = await detector.ScanAsync(ev, Array.Empty<string>(), Array.Empty<string>());

        Assert.Empty(result.DisappearedSpeakers);
    }

    [Fact]
    public async Task A_PENDING_delta_still_reports_the_speaker()
    {
        // 🔒 Pending is precisely what the mail exists to chase — only an explicit approval silences it.
        using var db = ScenarioFixture.NewDb();
        var (ev, profileId) = await SeedLinkedSpeakerAsync(db, "sessionize-id-1");
        db.SyncDeltas.Add(new SyncDelta
        {
            EventId = ev,
            EntityType = SyncDeltaEntityType.Speaker,
            EntityId = profileId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ChangeKind = SyncDeltaChangeKind.Disappeared,
            Status = SyncDeltaStatus.Pending,
        });
        await db.SaveChangesAsync();
        var detector = new SessionizeDisappearanceDetector(db);

        var result = await detector.ScanAsync(ev, Array.Empty<string>(), Array.Empty<string>());

        Assert.Single(result.DisappearedSpeakers);
    }

    // --- §1256c: markup never reaches a bio ----------------------------------

    [Fact]
    public void A_paragraph_wrapped_bio_becomes_plain_text_with_its_break_kept()
    {
        var html = "<p>First paragraph about the speaker.</p><p>Second paragraph.</p>";

        var text = HtmlTextNormalizer.ToPlainText(html);

        Assert.Equal("First paragraph about the speaker.\n\nSecond paragraph.", text);
        Assert.DoesNotContain("<", text);
    }

    [Fact]
    public void A_line_break_tag_becomes_a_real_line_break()
    {
        Assert.Equal("One\nTwo", HtmlTextNormalizer.ToPlainText("One<br/>Two"));
        Assert.Equal("One\nTwo", HtmlTextNormalizer.ToPlainText("One<BR>Two"));
    }

    [Fact]
    public void Entities_are_decoded_so_a_pasted_ampersand_reads_normally()
    {
        Assert.Equal("Security & Compliance", HtmlTextNormalizer.ToPlainText("<p>Security &amp; Compliance</p>"));
    }

    [Fact]
    public void Plain_text_is_left_exactly_as_it_is()
    {
        const string plain = "Jörgen is a consultant.\n\nHe speaks about Intune.";
        Assert.Equal(plain, HtmlTextNormalizer.ToPlainText(plain));
        Assert.False(HtmlTextNormalizer.LooksLikeHtml(plain));
    }

    [Fact]
    public void Null_stays_null_so_not_set_is_not_turned_into_empty()
    {
        Assert.Null(HtmlTextNormalizer.ToPlainText(null));
    }

    // --- §1256d: a difference nobody can see is not a difference -------------

    [Fact]
    public void A_double_space_is_not_a_visible_difference()
    {
        // The live case: CEH held "Panel:  Security & Compliance" (29 chars), Zoho
        // "Panel: Security & Compliance" (28) — and the mail printed them identically.
        Assert.False(TextComparison.DiffersVisibly(
            "Panel:  Security & Compliance", "Panel: Security & Compliance"));
    }

    [Fact]
    public void Leading_and_trailing_whitespace_is_not_a_visible_difference()
    {
        Assert.False(TextComparison.DiffersVisibly("  Keynote ", "Keynote"));
    }

    [Fact]
    public void A_real_edit_is_still_a_visible_difference()
    {
        Assert.True(TextComparison.DiffersVisibly("Panel: Security", "Panel: Security & Compliance"));
        Assert.True(TextComparison.DiffersVisibly("keynote", "Keynote"));   // case is visible
    }

    [Fact]
    public void Collapsing_keeps_the_words_and_only_removes_the_padding()
    {
        Assert.Equal("Panel: Security & Compliance",
            TextComparison.CollapseWhitespace("  Panel:   Security &   Compliance  "));
    }
}
