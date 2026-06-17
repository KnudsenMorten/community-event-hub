using CommunityHub.Core.Reminders;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Unit tests for the participant "request a change" service (REQUIREMENTS §21
/// Participant: change-request path once a form is deadline-locked). Asserts the
/// happy path raises ONE organizer Action-Queue item of the right type, the
/// idempotency per (event, participant, topic), the validation gates (empty /
/// too-long / unknown participant), edition scoping, and the participant's own
/// open-request read-back.
/// </summary>
public class FormChangeRequestServiceTests
{
    private static FormChangeRequestService MakeService(
        Data.CommunityHubDbContext db, out OrganizerActionItemService actions)
    {
        // The clock only matters to the underlying action-queue upsert (UpdatedAt).
        var clock = new FixedClock(new DateTimeOffset(2027, 1, 25, 9, 0, 0, TimeSpan.Zero));
        actions = new OrganizerActionItemService(db, clock);
        return new FormChangeRequestService(db, actions);
    }

    // --- Topic helpers ----------------------------------------------------

    [Fact]
    public void ParseTopic_is_case_insensitive_and_defaults_general()
    {
        Assert.Equal(FormTopic.Hotel, FormChangeRequestService.ParseTopic("hotel"));
        Assert.Equal(FormTopic.Dinner, FormChangeRequestService.ParseTopic("Dinner"));
        Assert.Equal(FormTopic.General, FormChangeRequestService.ParseTopic("nonsense"));
        Assert.Equal(FormTopic.General, FormChangeRequestService.ParseTopic(null));
    }

    [Fact]
    public void TopicLabel_returns_friendly_label()
    {
        Assert.Equal("Hotel", FormChangeRequestService.TopicLabel(FormTopic.Hotel));
        Assert.Equal("Appreciation dinner", FormChangeRequestService.TopicLabel(FormTopic.Dinner));
        Assert.Equal("General", FormChangeRequestService.TopicLabel(FormTopic.General));
    }

    // --- Happy path -------------------------------------------------------

    [Fact]
    public async Task Submit_raises_one_change_requested_action_item()
    {
        await using var db = TestDb.New();
        var (ev, p) = await TestDb.SeedEventAndPersonAsync(db, new DateOnly(2027, 2, 1));
        var svc = MakeService(db, out var actions);

        var result = await svc.SubmitAsync(ev, p, FormTopic.Hotel, "Please move my check-in to the 8th.");

        Assert.True(result.Accepted);
        var open = await actions.GetOpenAsync(ev);
        var item = Assert.Single(open);
        Assert.Equal(FormChangeRequestService.TypeFor(FormTopic.Hotel), item.Type);
        Assert.Equal(p, item.ParticipantId);
        Assert.Contains("Hotel change requested", item.Summary);
        Assert.Contains("move my check-in", item.Summary);
    }

    [Fact]
    public async Task Submit_trims_the_message()
    {
        await using var db = TestDb.New();
        var (ev, p) = await TestDb.SeedEventAndPersonAsync(db, new DateOnly(2027, 2, 1));
        var svc = MakeService(db, out var actions);

        await svc.SubmitAsync(ev, p, FormTopic.Dinner, "   extra plus-one please   ");

        var item = Assert.Single(await actions.GetOpenAsync(ev));
        Assert.EndsWith("extra plus-one please", item.Summary);
    }

    [Fact]
    public async Task Submit_works_regardless_of_lock_date_unlike_RaiseIfLate()
    {
        // RaiseIfLate is window-gated; a change REQUEST must always go through —
        // it is exactly the post-lock dead-end this feature fixes.
        await using var db = TestDb.New();
        var (ev, p) = await TestDb.SeedEventAndPersonAsync(db, lockDate: null);
        var svc = MakeService(db, out var actions);

        var result = await svc.SubmitAsync(ev, p, FormTopic.Travel, "wrong amount on my claim");

        Assert.True(result.Accepted);
        Assert.Equal(1, await actions.CountOpenAsync(ev));
    }

    // --- Idempotency ------------------------------------------------------

    [Fact]
    public async Task Submitting_twice_for_same_topic_keeps_one_row_and_refreshes()
    {
        await using var db = TestDb.New();
        var (ev, p) = await TestDb.SeedEventAndPersonAsync(db, new DateOnly(2027, 2, 1));
        var svc = MakeService(db, out var actions);

        await svc.SubmitAsync(ev, p, FormTopic.Hotel, "first ask");
        await svc.SubmitAsync(ev, p, FormTopic.Hotel, "actually, this instead");

        var item = Assert.Single(await actions.GetOpenAsync(ev));
        Assert.Contains("actually, this instead", item.Summary);
    }

    [Fact]
    public async Task Different_topics_for_same_person_are_separate_rows()
    {
        await using var db = TestDb.New();
        var (ev, p) = await TestDb.SeedEventAndPersonAsync(db, new DateOnly(2027, 2, 1));
        var svc = MakeService(db, out var actions);

        await svc.SubmitAsync(ev, p, FormTopic.Hotel, "hotel ask");
        await svc.SubmitAsync(ev, p, FormTopic.Dinner, "dinner ask");

        Assert.Equal(2, await actions.CountOpenAsync(ev));
    }

    // --- Validation -------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Blank_message_is_rejected_and_writes_nothing(string? message)
    {
        await using var db = TestDb.New();
        var (ev, p) = await TestDb.SeedEventAndPersonAsync(db, new DateOnly(2027, 2, 1));
        var svc = MakeService(db, out var actions);

        var result = await svc.SubmitAsync(ev, p, FormTopic.Hotel, message);

        Assert.False(result.Accepted);
        Assert.Equal("empty", result.FailureReason);
        Assert.Equal(0, await actions.CountOpenAsync(ev));
    }

    [Fact]
    public async Task Over_length_message_is_rejected_and_writes_nothing()
    {
        await using var db = TestDb.New();
        var (ev, p) = await TestDb.SeedEventAndPersonAsync(db, new DateOnly(2027, 2, 1));
        var svc = MakeService(db, out var actions);

        var tooLong = new string('x', FormChangeRequestService.MaxMessageLength + 1);
        var result = await svc.SubmitAsync(ev, p, FormTopic.Hotel, tooLong);

        Assert.False(result.Accepted);
        Assert.Equal("too-long", result.FailureReason);
        Assert.Equal(0, await actions.CountOpenAsync(ev));
    }

    [Fact]
    public async Task Unknown_participant_is_rejected()
    {
        await using var db = TestDb.New();
        var (ev, _) = await TestDb.SeedEventAndPersonAsync(db, new DateOnly(2027, 2, 1));
        var svc = MakeService(db, out var actions);

        var result = await svc.SubmitAsync(ev, participantId: 99999, FormTopic.Hotel, "hello");

        Assert.False(result.Accepted);
        Assert.Equal("unknown-participant", result.FailureReason);
        Assert.Equal(0, await actions.CountOpenAsync(ev));
    }

    [Fact]
    public async Task Participant_from_another_edition_is_rejected()
    {
        await using var db = TestDb.New();
        var (evA, _)  = await TestDb.SeedEventAndPersonAsync(db, new DateOnly(2027, 2, 1));
        var (_, pB)   = await TestDb.SeedEventAndPersonAsync(db, new DateOnly(2027, 2, 1));
        var svc = MakeService(db, out var actions);

        // pB belongs to edition B; submitting against edition A must be refused.
        var result = await svc.SubmitAsync(evA, pB, FormTopic.Hotel, "cross-edition attempt");

        Assert.False(result.Accepted);
        Assert.Equal("unknown-participant", result.FailureReason);
        Assert.Equal(0, await actions.CountOpenAsync(evA));
    }

    // --- Read-back --------------------------------------------------------

    [Fact]
    public async Task GetOpenForParticipant_returns_only_my_open_change_requests()
    {
        await using var db = TestDb.New();
        var (ev, p1) = await TestDb.SeedEventAndPersonAsync(db, new DateOnly(2027, 2, 1));
        var (_, p2)  = await TestDb.SeedEventAndPersonAsync(db, new DateOnly(2027, 2, 1));
        // p2 is in another edition by the seed; add them to ev so we can prove scoping.
        var svc = MakeService(db, out var actions);

        await svc.SubmitAsync(ev, p1, FormTopic.Hotel, "p1 hotel");
        await svc.SubmitAsync(ev, p1, FormTopic.Dinner, "p1 dinner");

        // A non-change-requested item for p1 must not leak into the read-back.
        await actions.UpsertOpenAsync(ev, OrganizerActionItemService.TypeHotelChanged, p1, "late hotel edit");

        var mine = await svc.GetOpenForParticipantAsync(ev, p1);

        Assert.Equal(2, mine.Count);
        Assert.All(mine, a => Assert.StartsWith(
            OrganizerActionItemService.TypeChangeRequestedPrefix + ":", a.Type));
        Assert.All(mine, a => Assert.Equal(p1, a.ParticipantId));
        // p2 is irrelevant here (different edition) — just assert nothing of theirs appears.
        Assert.DoesNotContain(mine, a => a.ParticipantId == p2);
    }

    [Fact]
    public void LabelFor_change_requested_family_is_friendly()
    {
        // Any per-topic suffix resolves to the same friendly family label.
        Assert.Equal("Change requested (after lock)",
            OrganizerActionItemService.LabelFor(FormChangeRequestService.TypeFor(FormTopic.Hotel)));
        Assert.Equal("Change requested (after lock)",
            OrganizerActionItemService.LabelFor(FormChangeRequestService.TypeFor(FormTopic.Dinner)));
    }
}
