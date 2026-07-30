using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using CommunityHub.Forms;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §355 speaker half — <see cref="ParticipantOnboardingResetService"/>.
///
/// <para>The test that matters most is <see cref="Reset_actually_re_opens_the_WIZARD_not_just_the_task"/>:
/// the whole reason this service exists is that <c>SpeakerWizardService</c> derives every step's
/// <c>Done</c> from the DATA, so a reset that only re-opened tasks would leave the organizer
/// looking at a wizard still reading 100%. Asserting through the real wizard — rather than
/// inspecting rows — is what proves the reset had the effect the operator wants.</para>
/// </summary>
public sealed class ParticipantOnboardingResetTests
{
    private const int EventId = 1;

    [Fact]
    public async Task Reset_actually_re_opens_the_WIZARD_not_just_the_task()
    {
        await using var db = NewDb();
        var pid = await SeedSpeakerWithEverythingDoneAsync(db);

        var before = await new SpeakerWizardService(db).BuildAsync(EventId, pid);
        Assert.True(before.AllDone, "Fixture is wrong: the speaker should start fully done.");

        var svc = new ParticipantOnboardingResetService(db);
        var r = await svc.ResetAsync(EventId, pid, new[] { "hotel", "party", "accept" });
        Assert.True(r.Ok);

        var after = await new SpeakerWizardService(db).BuildAsync(EventId, pid);
        Assert.False(after.AllDone);
        Assert.False(Step(after, "hotel").Done);
        Assert.False(Step(after, "party").Done);
        Assert.False(Step(after, "accept").Done);

        // …and everything NOT ticked is untouched. A reset that quietly widened its blast radius
        // would destroy real answers during a test run.
        Assert.True(Step(after, "dinner").Done);
        Assert.True(Step(after, "swag").Done);
        Assert.True(Step(after, "lunch").Done);
        Assert.True(Step(after, "calendar").Done);
        Assert.True(Step(after, "details").Done);
    }

    [Fact]
    public async Task A_full_reset_puts_every_step_back_to_not_done()
    {
        await using var db = NewDb();
        var pid = await SeedSpeakerWithEverythingDoneAsync(db);

        var svc = new ParticipantOnboardingResetService(db);
        var r = await svc.ResetAsync(EventId, pid, ParticipantOnboardingResetService.StepKeys);
        Assert.True(r.Ok);

        var after = await new SpeakerWizardService(db).BuildAsync(EventId, pid);
        // Every ANSWERABLE step is back to not-done. §400 appended a read-only "deadlines" summary
        // that is Done by construction (it asks nothing), so it is excluded rather than counted —
        // a reset cannot un-do a step that was never an answer.
        // §410: this speaker has no tasks OUTSIDE the wizard in the fixture, so the read-only
        // deadlines step is not offered at all — every remaining step is answerable, and a full
        // reset must put all of them back to not-done.
        Assert.All(after.Steps, s => Assert.False(s.Done));
        Assert.Equal(0, after.DoneCount);
        Assert.Equal(0, after.Percent);
    }

    [Fact]
    public async Task Resetting_details_clears_the_hub_collected_answers_but_NEVER_the_biography()
    {
        // §401 (operator 2026-07-26: "speaker reset of speaker details didnt reset the fields i had
        // manually set like company, country, accrediation"). The reset used to clear ONLY the
        // marker, so the step re-armed while the answers stayed on screen — a half-done reset, which
        // is worse than a refused one because the next test run reads as passing.
        //
        // The split pinned here is hub-collected (the speaker typed it on THIS form; Sessionize
        // never writes it) vs imported/authored. The operator re-tests on real accounts, so
        // destroying authored prose to re-arm a step would be data loss wearing a test feature's
        // clothes.
        await using var db = NewDb();
        var pid = await SeedSpeakerWithEverythingDoneAsync(db);

        await new ParticipantOnboardingResetService(db).ResetAsync(EventId, pid, new[] { "details" });

        var profile = await db.SpeakerProfiles.SingleAsync(p => p.ParticipantId == pid);

        Assert.Null(profile.BioLastEditedBySpeakerAt);
        Assert.Null(profile.Accreditation);
        Assert.Null(profile.CompanyName);
        Assert.Null(profile.Country);
        Assert.Null(profile.Gender);
        Assert.Null(profile.IsFirstTimeSpeaker);

        // Imported / authored — untouched.
        Assert.Equal("A real biography the speaker wrote.", profile.Biography);
        Assert.Equal("Cloud person", profile.Tagline);
        Assert.Equal("https://linkedin.test/in/speaker", profile.LinkedIn);
    }

    [Fact]
    public async Task Resetting_calendar_keeps_the_address_and_only_clears_the_stamp()
    {
        await using var db = NewDb();
        var pid = await SeedSpeakerWithEverythingDoneAsync(db);

        await new ParticipantOnboardingResetService(db).ResetAsync(EventId, pid, new[] { "calendar" });

        var profile = await db.SpeakerProfiles.SingleAsync(p => p.ParticipantId == pid);
        Assert.Null(profile.CalendarEmailSetAt);
        Assert.Equal("calendar@example.test", profile.CalendarEmail);
    }

    [Fact]
    public async Task A_re_opened_task_is_re_stamped_so_the_chaser_cannot_fire_immediately()
    {
        // §358: the reminder cadence is anchored on the task's CreatedAt. A reset leaving an OLD
        // CreatedAt behind lets the chaser fire on the very next pass — the double-send the
        // operator reported twice.
        await using var db = NewDb();
        var pid = await SeedSpeakerWithEverythingDoneAsync(db);

        var ancient = DateTimeOffset.UtcNow.AddDays(-200);
        db.Tasks.Add(new ParticipantTask
        {
            EventId = EventId,
            AssignedParticipantId = pid,
            SourceKey = $"hotel-form:{pid}",
            Title = "Book your hotel",
            State = TaskState.Done,
            CompletedAt = ancient,
            CreatedAt = ancient,
        });
        await db.SaveChangesAsync();

        await new ParticipantOnboardingResetService(db).ResetAsync(EventId, pid, new[] { "hotel" });

        var task = await db.Tasks.SingleAsync(t => t.SourceKey == $"hotel-form:{pid}");
        Assert.Equal(TaskState.Open, task.State);
        Assert.Null(task.CompletedAt);
        Assert.True(task.CreatedAt > ancient.AddDays(100), "CreatedAt must be re-stamped, not left old.");
    }

    [Fact]
    public async Task An_unknown_step_key_is_ignored_rather_than_throwing()
    {
        // A stale checkbox in a posted form must never 500 an organizer page.
        await using var db = NewDb();
        var pid = await SeedSpeakerWithEverythingDoneAsync(db);

        var r = await new ParticipantOnboardingResetService(db)
            .ResetAsync(EventId, pid, new[] { "hotel", "no-such-step" });

        Assert.True(r.Ok);
        Assert.Equal(new[] { "hotel" }, r.StepsReset);
    }

    [Fact]
    public async Task Selecting_nothing_is_refused_with_a_reason_not_a_silent_no_op()
    {
        await using var db = NewDb();
        var pid = await SeedSpeakerWithEverythingDoneAsync(db);

        var r = await new ParticipantOnboardingResetService(db)
            .ResetAsync(EventId, pid, Array.Empty<string>());

        Assert.False(r.Ok);
        Assert.Contains("Nothing selected", r.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_participant_from_another_edition_is_refused()
    {
        await using var db = NewDb();
        var pid = await SeedSpeakerWithEverythingDoneAsync(db);

        var r = await new ParticipantOnboardingResetService(db)
            .ResetAsync(eventId: 999, participantId: pid, new[] { "hotel" });

        Assert.False(r.Ok);
        Assert.Contains("not found", r.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Every_resettable_step_key_is_a_key_the_WIZARD_actually_uses()
    {
        // The two lists are separate by necessity (one is UI metadata, one is the wizard's own
        // build) — so pin them together. A wizard step renamed without updating this service would
        // otherwise leave a checkbox that silently resets nothing.
        var wizardKeys = new[]
        {
            "calendar", "details", "hotel", "dinner", "swag", "lunch", "signal", "party", "accept",
        };

        Assert.Equal(wizardKeys, ParticipantOnboardingResetService.Steps.Select(s => s.Key).ToArray());
    }

    // ---- fixture ----------------------------------------------------------

    private static SpeakerWizardStep Step(SpeakerWizardView view, string key) =>
        view.Steps.Single(s => s.Key == key);

    private static Data.CommunityHubDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<Data.CommunityHubDbContext>()
            .UseInMemoryDatabase($"reset-{Guid.NewGuid()}")
            .Options;
        return new Data.CommunityHubDbContext(options);
    }

    /// <summary>
    /// A speaker whose every wizard step reads DONE — each from its own source, which is the point:
    /// nine steps, eight different tables.
    /// </summary>
    private static async Task<int> SeedSpeakerWithEverythingDoneAsync(Data.CommunityHubDbContext db)
    {
        var now = DateTimeOffset.UtcNow;

        db.Events.Add(new Event { Id = EventId, Code = "TEST", IsActive = true });

        var p = new Participant
        {
            EventId = EventId,
            Email = "speaker@example.test",
            FullName = "Test Speaker",
            Role = ParticipantRole.Speaker,
            IsActive = true,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();

        // The optional wizard steps (hotel/dinner/swag/lunch) only appear when the speaker is
        // ENTITLED to them, so grant them explicitly — otherwise the wizard renders five steps and
        // a test asserting on "hotel" fails for the wrong reason.
        foreach (var item in new[]
                 {
                     OrderItem.Hotel, OrderItem.AppreciationDinner,
                     OrderItem.Swag, OrderItem.LunchPreDay,
                 })
        {
            db.ParticipantOrderOverrides.Add(new ParticipantOrderOverride
            {
                EventId = EventId, ParticipantId = p.Id, Item = item, Include = true,
            });
        }

        db.SpeakerProfiles.Add(new SpeakerProfile
        {
            EventId = EventId,
            ParticipantId = p.Id,
            Biography = "A real biography the speaker wrote.",
            BioLastEditedBySpeakerAt = now,
            CalendarEmail = "calendar@example.test",
            CalendarEmailSetAt = now,
            // §401: the hub-collected answers the speaker types on the Details form…
            Accreditation = "Microsoft MVP",
            CompanyName = "2linkIT",
            Country = "Denmark",
            Gender = "Male",
            IsFirstTimeSpeaker = false,
            // …and the imported/authored fields that a reset must never destroy.
            Tagline = "Cloud person",
            LinkedIn = "https://linkedin.test/in/speaker",
        });
        db.HotelBookings.Add(new HotelBooking { EventId = EventId, ParticipantId = p.Id });
        db.DinnerSignups.Add(new DinnerSignup { EventId = EventId, ParticipantId = p.Id });
        db.SwagPreferences.Add(new SwagPreference { EventId = EventId, ParticipantId = p.Id });
        db.LunchSignups.Add(new LunchSignup { EventId = EventId, ParticipantId = p.Id });
        db.PartyRsvps.Add(new PartyRsvp
        {
            EventId = EventId, ParticipantId = p.Id, Email = p.Email, Attending = true,
        });
        db.ParticipantPolicyAcceptances.Add(new ParticipantPolicyAcceptance
        {
            EventId = EventId, ParticipantId = p.Id, AcceptedAt = now,
        });

        await db.SaveChangesAsync();
        return p.Id;
    }
}
