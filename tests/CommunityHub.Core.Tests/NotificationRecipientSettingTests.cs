using CommunityHub.Core.Email;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Settings;
using CommunityHub.Core.Tests.Scenario;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §742 — the Get Started 100%-completion notice must SAY where it goes and let the operator change
/// it. Operator 2026-07-31: <i>"it must go to mok@expertslive.dk and i need to be able to control
/// where it goes and state in settings page"</i>.
///
/// <para>🔒 The address used to be a C# constant, so the Settings row advertised THAT it notified
/// and never WHO it reached — the §694/§698 shape of "a control that does not state what it
/// governs". It is now per-edition data on the feature row.</para>
/// </summary>
public sealed class NotificationRecipientSettingTests
{
    private const int EventId = 1;
    private const string Key = GetStartedCompletionNotifier.FeatureKey;

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 7, 31, 8, 0, 0, TimeSpan.Zero);
    }

    private static (FeatureSettingsService Settings, FeatureGateService Gate) NewServices(
        CommunityHubDbContextAccessor db) => (db.Settings, db.Gate);

    /// <summary>Small holder so each test gets one context with both services over it.</summary>
    private sealed class CommunityHubDbContextAccessor : IDisposable
    {
        public CommunityHub.Core.Data.CommunityHubDbContext Db { get; }
        public FeatureSettingsService Settings { get; }
        public FeatureGateService Gate { get; }

        public CommunityHubDbContextAccessor()
        {
            Db = ScenarioFixture.NewDb();
            Settings = new FeatureSettingsService(Db, new FixedClock());
            Gate = new FeatureGateService(Db);
        }

        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public async Task Unset_falls_back_to_the_built_in_ops_mailbox()
    {
        using var ctx = new CommunityHubDbContextAccessor();
        var (_, gate) = NewServices(ctx);

        var to = await gate.GetNotificationRecipientAsync(Key, EventId, EngineAlertSender.Recipient);

        // 🔒 Unchanged until he changes it — and that default IS the address he asked for.
        Assert.Equal("mok@expertslive.dk", to);
        Assert.Equal(EngineAlertSender.Recipient, to);
    }

    [Fact]
    public async Task Setting_an_address_redirects_the_notice()
    {
        using var ctx = new CommunityHubDbContextAccessor();
        var (settings, gate) = NewServices(ctx);

        var ok = await settings.SetNotificationRecipientAsync(
            EventId, Key, "info@expertslive.dk", "mok@expertslive.dk");

        Assert.True(ok);
        Assert.Equal("info@expertslive.dk",
            await gate.GetNotificationRecipientAsync(Key, EventId, EngineAlertSender.Recipient));
    }

    [Fact]
    public async Task Clearing_it_restores_the_default_rather_than_sending_to_nobody()
    {
        // 🔒 The safe direction for a control whose failure mode is SILENCE: an empty box must not
        // mean "deliver to no one", because that failure is invisible until someone notices a mail
        // that never arrived.
        using var ctx = new CommunityHubDbContextAccessor();
        var (settings, gate) = NewServices(ctx);

        await settings.SetNotificationRecipientAsync(EventId, Key, "info@expertslive.dk", "mok@x.dk");
        await settings.SetNotificationRecipientAsync(EventId, Key, "   ", "mok@x.dk");

        Assert.Equal(EngineAlertSender.Recipient,
            await gate.GetNotificationRecipientAsync(Key, EventId, EngineAlertSender.Recipient));
    }

    [Fact]
    public async Task A_feature_that_sends_no_notice_REFUSES_an_address()
    {
        // A stored address on a switch that mails nobody would be a setting that governs nothing —
        // the §326bx defect this page has had removed twice. The refusal is reported, not swallowed,
        // so the page can say why instead of appearing to save.
        using var ctx = new CommunityHubDbContextAccessor();
        var (settings, _) = NewServices(ctx);

        var ok = await settings.SetNotificationRecipientAsync(
            EventId, FeatureCatalog.OutboundEmailKey, "someone@x.dk", "mok@x.dk");

        Assert.False(ok);
    }

    [Fact]
    public void Only_the_get_started_notice_advertises_a_recipient_box_today()
    {
        // Pins the SCOPE he set: *"it is the notifcations when someone complete the get started and
        // reaches 100% we are talking about"*. If another feature gains SendsOpsNotice later, this
        // test is the deliberate stop to confirm the Settings page really should grow a second box.
        var senders = FeatureCatalog.All.Where(d => d.SendsOpsNotice).Select(d => d.Key).ToList();

        Assert.Equal(new[] { Key }, senders);
    }
}
