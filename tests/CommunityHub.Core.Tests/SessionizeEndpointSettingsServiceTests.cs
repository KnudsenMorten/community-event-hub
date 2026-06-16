using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Reminders;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Offline tests for <see cref="SessionizeEndpointSettingsService"/> — the
/// organizer endpoint admin + endpoint-change handling (Replace/Merge).
///
/// Asserts: set the endpoint (first time, no change flag); change the endpoint
/// (change flagged, prior choice reset, live options updated); the Replace/Merge
/// prompt maps to the importer's Full/Delta modes; and that NO import is run by any
/// of these calls (the service has no importer dependency at all — it cannot run one).
/// </summary>
public sealed class SessionizeEndpointSettingsServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 15, 8, 0, 0, TimeSpan.Zero);

    private static (SessionizeEndpointSettingsService svc, SessionizeApiOptions opts,
        Core.Data.CommunityHubDbContext db, FixedClock clock) NewSut(string configDefault = "")
    {
        var db = TestDb.New();
        var opts = new SessionizeApiOptions
        {
            Enabled = true,
            EndpointId = configDefault,
            View = SessionizeView.Speakers,
        };
        var clock = new FixedClock(T0);
        return (new SessionizeEndpointSettingsService(db, opts, clock), opts, db, clock);
    }

    private static async Task<int> SeedEventAsync(Core.Data.CommunityHubDbContext db)
    {
        var (eventId, _) = await TestDb.SeedEventAndPersonAsync(db, lockDate: null);
        return eventId;
    }

    [Theory]
    [InlineData(SessionizeChangeMode.Replace, SessionizeImportMode.Full)]
    [InlineData(SessionizeChangeMode.Merge, SessionizeImportMode.Delta)]
    [InlineData(SessionizeChangeMode.None, SessionizeImportMode.Delta)] // safe default
    public void Change_mode_maps_to_import_mode(SessionizeChangeMode change, SessionizeImportMode expected)
    {
        Assert.Equal(expected, SessionizeEndpointSettingsService.ToImportMode(change));
    }

    [Fact]
    public async Task First_save_of_endpoint_is_not_flagged_as_a_change()
    {
        var (svc, opts, db, _) = NewSut(configDefault: "");
        var eventId = await SeedEventAsync(db);

        var result = await svc.SaveEndpointAsync(eventId, "abc12345", view: "Speakers", byEmail: "o@expertslive.dk");

        Assert.False(result.EndpointChanged);
        Assert.Equal("abc12345", await svc.GetEffectiveEndpointIdAsync(eventId));
        Assert.Null(result.Setting.EndpointLastChangedAt);
        Assert.Equal(SessionizeChangeMode.None, result.Setting.PendingChangeMode);
        // Live options updated so the running client uses the new id (no restart).
        Assert.Equal("abc12345", opts.EndpointId);
    }

    [Fact]
    public async Task Saving_value_equal_to_config_default_is_not_a_change()
    {
        var (svc, _, db, _) = NewSut(configDefault: "defaultid");
        var eventId = await SeedEventAsync(db);

        var result = await svc.SaveEndpointAsync(eventId, "defaultid");

        Assert.False(result.EndpointChanged);
    }

    [Fact]
    public async Task Changing_the_endpoint_flags_a_change_and_records_previous()
    {
        var (svc, opts, db, clock) = NewSut(configDefault: "");
        var eventId = await SeedEventAsync(db);

        await svc.SaveEndpointAsync(eventId, "eldk26code");
        clock.Set(T0.AddHours(1));
        var result = await svc.SaveEndpointAsync(eventId, "eldk27code");

        Assert.True(result.EndpointChanged);
        Assert.Equal("eldk26code", result.Setting.PreviousEndpointId);
        Assert.Equal(T0.AddHours(1), result.Setting.EndpointLastChangedAt);
        Assert.True(result.Setting.AwaitingChangeChoice);
        Assert.Equal("eldk27code", opts.EndpointId);
    }

    [Fact]
    public async Task Recording_replace_choice_maps_to_full_and_clears_the_prompt()
    {
        var (svc, _, db, _) = NewSut();
        var eventId = await SeedEventAsync(db);
        await svc.SaveEndpointAsync(eventId, "first");
        await svc.SaveEndpointAsync(eventId, "second"); // a change → awaiting choice

        var mode = await svc.RecordChangeChoiceAsync(eventId, SessionizeChangeMode.Replace);

        Assert.Equal(SessionizeImportMode.Full, mode);
        var row = await svc.LoadAsync(eventId);
        Assert.Equal(SessionizeChangeMode.Replace, row!.PendingChangeMode);
        Assert.NotNull(row.ChangeModeChosenAt);
        Assert.False(row.AwaitingChangeChoice); // prompt cleared once a choice is made
    }

    [Fact]
    public async Task Recording_merge_choice_maps_to_delta()
    {
        var (svc, _, db, _) = NewSut();
        var eventId = await SeedEventAsync(db);
        await svc.SaveEndpointAsync(eventId, "first");
        await svc.SaveEndpointAsync(eventId, "second");

        var mode = await svc.RecordChangeChoiceAsync(eventId, SessionizeChangeMode.Merge);

        Assert.Equal(SessionizeImportMode.Delta, mode);
        Assert.Equal(SessionizeChangeMode.Merge, (await svc.LoadAsync(eventId))!.PendingChangeMode);
    }

    [Fact]
    public async Task A_new_endpoint_change_resets_a_prior_choice()
    {
        var (svc, _, db, _) = NewSut();
        var eventId = await SeedEventAsync(db);
        await svc.SaveEndpointAsync(eventId, "first");
        await svc.SaveEndpointAsync(eventId, "second");
        await svc.RecordChangeChoiceAsync(eventId, SessionizeChangeMode.Merge);

        // Endpoint changes again → the old choice must no longer stand; re-prompt.
        var result = await svc.SaveEndpointAsync(eventId, "third");

        Assert.True(result.EndpointChanged);
        Assert.Equal(SessionizeChangeMode.None, result.Setting.PendingChangeMode);
        Assert.True(result.Setting.AwaitingChangeChoice);
    }

    [Fact]
    public async Task Choosing_a_mode_without_a_pending_change_throws()
    {
        var (svc, _, db, _) = NewSut();
        var eventId = await SeedEventAsync(db);
        await svc.SaveEndpointAsync(eventId, "only"); // first save, not a change

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RecordChangeChoiceAsync(eventId, SessionizeChangeMode.Replace));
    }

    [Fact]
    public async Task None_is_not_a_valid_recorded_choice()
    {
        var (svc, _, db, _) = NewSut();
        var eventId = await SeedEventAsync(db);
        await svc.SaveEndpointAsync(eventId, "first");
        await svc.SaveEndpointAsync(eventId, "second");

        await Assert.ThrowsAsync<ArgumentException>(
            () => svc.RecordChangeChoiceAsync(eventId, SessionizeChangeMode.None));
    }

    [Fact]
    public async Task Effective_id_falls_back_to_config_default_when_no_row_override()
    {
        var (svc, _, db, _) = NewSut(configDefault: "fromconfig");
        var eventId = await SeedEventAsync(db);

        // No save yet → effective is the config default.
        Assert.Equal("fromconfig", await svc.GetEffectiveEndpointIdAsync(eventId));

        // Blank save keeps the config fallback (no override stored).
        await svc.SaveEndpointAsync(eventId, "   ");
        Assert.Equal("fromconfig", await svc.GetEffectiveEndpointIdAsync(eventId));
    }
}
