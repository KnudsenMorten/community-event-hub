using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Forms;
using CommunityHub.Forms.Steps;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §356 — the sponsor speaking-session form gains a TRACK dropdown and, per speaker, a LinkedIn
/// field and a photo (operator 2026-07-26: <i>"form must include link to linkedin for speaker(s) …
/// ability to include a picture upload … a dropdown with all available tracks"</i>).
///
/// <para><b>The design decision worth pinning</b> is that LinkedIn and the photo do NOT get their own
/// storage. A sponsor-session speaker already IS a real Speaker <see cref="Participant"/> with a
/// <see cref="SpeakerProfile"/> (§292), so the sponsor is filling in that speaker's own profile on
/// their behalf. A parallel copy would look identical on the day it was written and disagree with the
/// speaker's own edits forever after.</para>
/// </summary>
public sealed class SponsorSessionTrackAndSpeakerTests
{
    private const int EventId = 1;
    private const string CompanyId = "test-2linkit";

    [Fact]
    public async Task The_track_dropdown_offers_the_tracks_the_ZOHO_PUSH_can_actually_resolve()
    {
        // The push matches a track by EXACT name against Zoho's list, so offering anything else
        // would let a sponsor pick a track that silently resolves to nothing. Sourcing the options
        // from the tracks already on the edition's sessions is what makes that impossible.
        await using var db = NewDb();
        var pid = await SeedSponsorAsync(db);
        db.Sessions.AddRange(
            Session("Security"), Session("Cloud Native"), Session("Security"), Session(null), Session(""));
        await db.SaveChangesAsync();

        var model = await Service(db).LoadAsync(EventId, pid, default);

        Assert.Equal(new[] { "Cloud Native", "Security" }, model.AvailableTracks.ToArray());
        Assert.True(model.HasTrackOptions);
    }

    [Fact]
    public async Task With_NO_tracks_yet_the_field_degrades_to_free_text_rather_than_blocking()
    {
        // A first-time sponsor filling this in before the agenda exists must not meet an empty
        // dropdown they cannot get past. The track is optional; a missing LIST is not a reason to
        // stop someone submitting their session.
        await using var db = NewDb();
        var pid = await SeedSponsorAsync(db);

        var model = await Service(db).LoadAsync(EventId, pid, default);

        Assert.Empty(model.AvailableTracks);
        Assert.False(model.HasTrackOptions);
    }

    [Fact]
    public async Task Track_options_are_offered_on_the_FIRST_visit_before_any_session_exists()
    {
        // The one visit where no SponsorSession row exists is the visit where the sponsor needs the
        // dropdown most — an early return on "no session yet" would have shipped an empty one.
        await using var db = NewDb();
        var pid = await SeedSponsorAsync(db);
        db.Sessions.Add(Session("Security"));
        await db.SaveChangesAsync();

        var model = await Service(db).LoadAsync(EventId, pid, default);

        Assert.Null(model.Title);                       // no session saved yet
        Assert.Equal(new[] { "Security" }, model.AvailableTracks.ToArray());
    }

    [Fact]
    public async Task Saving_stores_the_track_and_writes_LINKEDIN_onto_the_speakers_own_profile()
    {
        await using var db = NewDb();
        var pid = await SeedSponsorAsync(db);

        var model = new SponsorSessionModel
        {
            Title = "Securing Azure at scale",
            Abstract = "How we did it.",
            Track = "Security",
            Speaker1Name = "Alex Doe",
            Speaker1Email = "Alex.Doe@Example.Test",
            Speaker1LinkedIn = "https://www.linkedin.com/in/alexdoe",
        };
        var ms = new ModelStateDictionary();

        var outcome = await Service(db).SaveAsync(model, EventId, pid, "coord@2linkit.net", ms, default);

        Assert.Equal(WizardStepOutcome.Advance, outcome);
        var session = await db.SponsorSessions.SingleAsync();
        Assert.Equal("Security", session.Track);

        // The LinkedIn landed on the SPEAKER's profile — not on a sponsor-side copy.
        var speaker = await db.Participants.SingleAsync(p => p.Email == "alex.doe@example.test");
        var profile = await db.SpeakerProfiles.SingleAsync(p => p.ParticipantId == speaker.Id);
        Assert.Equal("https://www.linkedin.com/in/alexdoe", profile.LinkedIn);
    }

    [Fact]
    public async Task A_BLANK_linkedin_leaves_what_the_speaker_already_entered_alone()
    {
        // The speaker owns this field too. A sponsor re-saving the session and tabbing past an empty
        // box must not wipe a profile the speaker filled in themselves — the sponsor form is not the
        // authority on a person's own profile, it is a convenience for getting it started.
        await using var db = NewDb();
        var pid = await SeedSponsorAsync(db);

        var first = new SponsorSessionModel
        {
            Title = "T", Abstract = "A", Speaker1Name = "Alex Doe", Speaker1Email = "alex@example.test",
        };
        await Service(db).SaveAsync(first, EventId, pid, "coord@2linkit.net", new ModelStateDictionary(), default);

        // The speaker then fills their own profile in.
        var speaker = await db.Participants.SingleAsync(p => p.Email == "alex@example.test");
        var profile = await db.SpeakerProfiles.SingleAsync(p => p.ParticipantId == speaker.Id);
        profile.LinkedIn = "https://www.linkedin.com/in/set-by-the-speaker";
        await db.SaveChangesAsync();

        // The sponsor saves the session again without touching the LinkedIn box.
        var second = new SponsorSessionModel
        {
            Title = "T2", Abstract = "A2", Speaker1Name = "Alex Doe", Speaker1Email = "alex@example.test",
            Speaker1LinkedIn = null,
        };
        await Service(db).SaveAsync(second, EventId, pid, "coord@2linkit.net", new ModelStateDictionary(), default);

        Assert.Equal(
            "https://www.linkedin.com/in/set-by-the-speaker",
            (await db.SpeakerProfiles.SingleAsync(p => p.ParticipantId == speaker.Id)).LinkedIn);
    }

    [Fact]
    public async Task A_saved_track_and_linkedin_come_BACK_on_the_next_load()
    {
        await using var db = NewDb();
        var pid = await SeedSponsorAsync(db);

        var model = new SponsorSessionModel
        {
            Title = "T", Abstract = "A", Track = "Security",
            Speaker1Name = "Alex Doe", Speaker1Email = "alex@example.test",
            Speaker1LinkedIn = "https://www.linkedin.com/in/alexdoe",
        };
        await Service(db).SaveAsync(model, EventId, pid, "coord@2linkit.net", new ModelStateDictionary(), default);

        var reloaded = await Service(db).LoadAsync(EventId, pid, default);

        Assert.Equal("Security", reloaded.Track);
        Assert.Equal("https://www.linkedin.com/in/alexdoe", reloaded.Speaker1LinkedIn);
    }

    [Fact]
    public async Task Clearing_the_track_is_allowed_because_it_is_OPTIONAL()
    {
        // "Not sure yet" is a real answer — the organizers place the session later. A required track
        // would block a sponsor who genuinely does not know.
        await using var db = NewDb();
        var pid = await SeedSponsorAsync(db);

        var ms = new ModelStateDictionary();
        var outcome = await Service(db).SaveAsync(
            new SponsorSessionModel
            {
                Title = "T", Abstract = "A", Track = null,
                Speaker1Name = "Alex Doe", Speaker1Email = "alex@example.test",
            },
            EventId, pid, "coord@2linkit.net", ms, default);

        Assert.Equal(WizardStepOutcome.Advance, outcome);
        Assert.Null((await db.SponsorSessions.SingleAsync()).Track);
    }

    // ---- fixture ----------------------------------------------------------

    private static SponsorSessionFormService Service(CommunityHubDbContext db) =>
        new(db, TimeProvider.System);   // no SharePoint deps → photo upload reports itself unavailable

    private static Session Session(string? track) => new()
    {
        EventId = EventId,
        Title = $"Some session {Guid.NewGuid():N}",
        Track = track,
    };

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"sponsor-session-{Guid.NewGuid():N}")
            .Options);

    private static async Task<int> SeedSponsorAsync(CommunityHubDbContext db)
    {
        db.Events.Add(new Event { Id = EventId, Code = "TEST", IsActive = true });
        var p = new Participant
        {
            EventId = EventId,
            Email = "coord@2linkit.net",
            FullName = "Coordinator",
            Role = ParticipantRole.Sponsor,
            SponsorCompanyId = CompanyId,
            IsEventCoordinator = true,
            IsActive = true,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return p.Id;
    }
}
