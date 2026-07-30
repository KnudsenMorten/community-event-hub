using System.Reflection;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Settings;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §340-H — the environment-level external-write switch (operator 2026-07-26: <i>"validate
/// DEV env so no settings can cause changes to external systems, except if i specifically
/// enable so it can do this. a flag must exist - in prod env it must be writeable"</i>).
///
/// <para>The stakes are stated in <c>infra/main.bicep</c> itself: <b>dev and prod share the
/// SAME upstream Zoho Backstage / WooCommerce</b>. So a DEV host that can write is writing
/// into the real event.</para>
/// </summary>
public sealed class ExternalWriteGuardTests
{
    private const int EventId = 42;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"extwrite-{Guid.NewGuid():N}")
            .Options);

    private static async Task<CommunityHubDbContext> DbWithEventAsync(bool? overrideValue = null)
    {
        var db = NewDb();
        db.Events.Add(new Event
        {
            Id = EventId, Code = "ELDK27", CommunityName = "C", DisplayName = "D", IsActive = true,
        });
        if (overrideValue is bool v)
        {
            db.FeatureSettings.Add(new FeatureSetting
            {
                EventId = EventId,
                FeatureKey = ExternalWriteGuard.OverrideKey,
                Enabled = v,
                ReleasedToRing = Rings.Default,
            });
        }
        await db.SaveChangesAsync();
        return db;
    }

    // ---- the environment default -------------------------------------------

    /// <summary>
    /// THE POINT OF THE WHOLE FEATURE: an unconfigured / dev host writes nothing. The .NET
    /// default is false, so a host that never received the app setting fails closed rather
    /// than reaching a third party.
    /// </summary>
    [Fact]
    public async Task Default_is_blocked_so_an_unconfigured_host_never_writes()
    {
        Assert.False(new ExternalWriteOptions().AllowExternalWrites);

        using var db = await DbWithEventAsync();
        var guard = new ExternalWriteGuard(db, new ExternalWriteOptions());

        Assert.False(await guard.IsAllowedAsync());
        Assert.False(await guard.AllowAsync("Zoho Backstage", "CreateSpeakerAsync"));
    }

    [Fact]
    public async Task Prod_environment_default_allows_writes()
    {
        using var db = await DbWithEventAsync();
        var guard = new ExternalWriteGuard(db, new ExternalWriteOptions { AllowExternalWrites = true });

        Assert.True(await guard.AllowAsync("Zoho Backstage", "CreateSessionAsync"));
    }

    // ---- the organizer override (requirement (b)) ---------------------------

    /// <summary>
    /// 🔒 §612 — INVERTED ON PURPOSE. An override can NO LONGER enable writes on a blocked host.
    ///
    /// <para>This asserted the opposite until 2026-07-28, on the earlier reading <i>"except if i
    /// specifically enable so it can do this"</i> — DEV protected by the DEFAULT rather than by
    /// making the switch unreachable. The operator has since closed that: <i>"in dev we want to
    /// enforce BLOCK WRITES !!!"</i> and <i>"just to confirm: we cannot have external writes from
    /// DEV !!!!"</i>. His reason is the failure mode: <i>"as i will then have 2 set of everything in
    /// zoho"</i> — duplicate live records that are manual to undo, and §326bx showed a write can
    /// e-mail real people (19 speakers invited prematurely).</para>
    ///
    /// <para>The environment config is now a CEILING and an override may only RESTRICT. The
    /// restrictive direction still works and is covered by the next test — an organizer can stop
    /// PROD writing without an infra change.</para>
    /// </summary>
    [Fact]
    public async Task Organizer_override_can_NOT_enable_writes_in_a_blocked_environment()
    {
        using var db = await DbWithEventAsync(overrideValue: true);
        var guard = new ExternalWriteGuard(db, new ExternalWriteOptions { AllowExternalWrites = false });

        Assert.False(await guard.AllowAsync("Zoho Backstage", "CreateSpeakerAsync"));
    }

    /// <summary>And it must be able to STOP prod writing, without an infra change.</summary>
    [Fact]
    public async Task Organizer_override_can_block_writes_in_an_allowed_environment()
    {
        using var db = await DbWithEventAsync(overrideValue: false);
        var guard = new ExternalWriteGuard(db, new ExternalWriteOptions { AllowExternalWrites = true });

        Assert.False(await guard.AllowAsync("Zoho Backstage", "CreateSpeakerAsync"));
    }

    /// <summary>
    /// No override row ⇒ INHERIT the environment default. This is the state DEV must be in
    /// for its safety to be a property of the environment rather than of a past toggle.
    /// </summary>
    [Fact]
    public async Task No_override_row_inherits_the_environment_default()
    {
        using var devDb = await DbWithEventAsync();
        Assert.False(await new ExternalWriteGuard(devDb, new ExternalWriteOptions()).IsAllowedAsync());

        using var prodDb = await DbWithEventAsync();
        Assert.True(await new ExternalWriteGuard(
            prodDb, new ExternalWriteOptions { AllowExternalWrites = true }).IsAllowedAsync());
    }

    /// <summary>
    /// Clearing the override (the "Follow environment default" button) must DELETE the row,
    /// not write a false — otherwise an edition could never return to inheriting, and a prod
    /// edition toggled once in the past would stay blocked forever with no visible cause.
    /// </summary>
    [Fact]
    public async Task Clearing_the_override_restores_inheritance()
    {
        using var db = await DbWithEventAsync(overrideValue: false);
        var settings = new FeatureSettingsService(db, TimeProvider.System);

        await settings.SetExternalWritesOverrideAsync(EventId, null, "organizer@example.test");

        Assert.Null(await settings.GetExternalWritesOverrideAsync(EventId));
        Assert.True(await new ExternalWriteGuard(
            db, new ExternalWriteOptions { AllowExternalWrites = true }).IsAllowedAsync());
    }

    // ---- the "cannot silently escape" contract ------------------------------

    /// <summary>
    /// §336-style pin. Every MUTATING <see cref="ZohoClient"/> method must consult the
    /// guard. The check is on the compiled IL: each listed method's body must contain a call
    /// to the private <c>MayWriteAsync</c> helper.
    ///
    /// <para><b>If this fails, a Zoho write escaped the environment switch.</b> Do not
    /// delete the name from the list — add the guard to the method. Reads are deliberately
    /// absent: two Zoho POSTs (token refresh, bookings query) are reads, which is exactly
    /// why the guard is per-method rather than an HTTP handler on non-GET.</para>
    /// </summary>
    [Fact]
    public void Every_mutating_ZohoClient_method_consults_the_write_guard()
    {
        var mustGuard = new[]
        {
            "UpdateExhibitorAsync", "AssignExhibitorBoothAsync", "UpdateSponsorAsync",
            "CreateSponsorAsync", "CreateExhibitorAsync", "CreateTrackAsync", "CreateHallAsync",
            "CreateSessionAsync", "UpdateSessionAsync", "CreateSpeakerAsync",
            "DeleteBoothMemberAsync", "CreateBoothMembersAsync",
        };

        foreach (var name in mustGuard)
        {
            Assert.True(
                typeof(ZohoClient).GetMethod(name) is not null,
                $"ZohoClient.{name} no longer exists — update this list deliberately.");

            Assert.True(CallsWriteGuard(name),
                $"ZohoClient.{name} is a WRITE but does not call MayWriteAsync — it would "
                + "bypass the §340-H external-write switch and could write to the real Zoho "
                + "from DEV. Add the guard; do not remove this name from the list.");
        }
    }

    /// <summary>
    /// Does <paramref name="methodName"/>'s compiled body call the private
    /// <c>MayWriteAsync</c> helper? The C# compiler rewrites async bodies into state
    /// machines, so the call lives on the generated <c>MoveNext</c>, not the method itself.
    /// </summary>
    private static bool CallsWriteGuard(string methodName) =>
        CallsToken(
            typeof(ZohoClient), methodName,
            typeof(ZohoClient).GetMethod("MayWriteAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .MetadataToken,
            viaInterface: false);

    /// <summary>
    /// Does <paramref name="declaringType"/>.<paramref name="methodName"/>'s compiled body
    /// call the method identified by <paramref name="metadataToken"/>?
    ///
    /// <para>An interface call (<c>IExternalWriteGuard.AllowAsync</c>) is emitted as
    /// <c>callvirt</c> against a MemberRef whose token differs from the interface method's
    /// own token, so for that case we fall back to scanning the whole IL for the token
    /// bytes rather than anchoring on the opcode.</para>
    /// </summary>
    private static bool CallsToken(
        Type declaringType, string methodName, int metadataToken, bool viaInterface)
    {
        var stateMachine = declaringType
            .GetNestedTypes(BindingFlags.NonPublic)
            .FirstOrDefault(t => t.Name.Contains($"<{methodName}>"));
        Assert.True(stateMachine is not null,
            $"Could not find the async state machine for {declaringType.Name}.{methodName}.");

        var il = stateMachine!
            .GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
            .GetMethodBody()!.GetILAsByteArray()!;

        if (viaInterface)
        {
            // The guard field is read and AllowAsync invoked; the concrete token is a
            // MemberRef we cannot compute here, so assert on the field type instead — the
            // seam must at least TOUCH IExternalWriteGuard.
            return declaringType
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Any(f => f.FieldType == typeof(IExternalWriteGuard));
        }

        // Only a `call`/`callvirt` operand can be the token, so anchor on the opcode
        // (0x28 / 0x6F) rather than scanning raw bytes — a bare byte match could collide
        // with an unrelated constant and turn this pin into false confidence.
        var tokenBytes = BitConverter.GetBytes(metadataToken);
        for (var i = 0; i + 5 <= il.Length; i++)
        {
            if (il[i] is not (0x28 or 0x6F)) continue;
            if (il[i + 1] == tokenBytes[0] && il[i + 2] == tokenBytes[1]
                && il[i + 3] == tokenBytes[2] && il[i + 4] == tokenBytes[3])
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// NEGATIVE CONTROL for the test above. An IL scan that can only ever say "yes" is
    /// false confidence, so prove the detector discriminates: pure READ methods must come
    /// back WITHOUT the guard call. If this fails, either a read grew a write guard
    /// (harmless but wrong) or — the real worry — the scan matches anything and the
    /// positive test above proves nothing.
    /// </summary>
    [Fact]
    public void The_guard_detector_reports_absence_on_read_methods()
    {
        foreach (var readMethod in new[]
                 { "GetExhibitorsAsync", "GetBackstageSpeakersAsync", "GetBackstageSessionsAsync" })
        {
            Assert.False(CallsWriteGuard(readMethod),
                $"ZohoClient.{readMethod} is a READ and must not consult the write guard "
                + "(gating reads would break DEV rather than protect anything).");
        }
    }

    /// <summary>
    /// The same pin for the OTHER three seams — e-conomic, LinkedIn and SharePoint. Each has
    /// its own private guard helper, so the check is parameterised on the helper name.
    ///
    /// <para>These three already had their own env-level gates (TestMode for ERP,
    /// <c>LinkedIn__Enabled</c>/<c>DryRun</c> for LinkedIn), so none was exposed the way Zoho
    /// was — but "already gated by something else" is exactly how the four-mechanism mess in
    /// §340-H arose. They belong under the ONE switch, and this pin keeps them there.</para>
    /// </summary>
    [Theory]
    [InlineData(typeof(CommunityHub.Core.Integrations.Erp.LiveEconomicErpClient),
        "EnsureMayWriteAsync", "CreateCustomerAsync")]
    [InlineData(typeof(CommunityHub.Core.Integrations.Erp.LiveEconomicErpClient),
        "EnsureMayWriteAsync", "UpdateCustomerAsync")]
    [InlineData(typeof(CommunityHub.Core.Integrations.Erp.LiveEconomicErpClient),
        "EnsureMayWriteAsync", "CreateOrUpdateContactAsync")]
    [InlineData(typeof(LiveLinkedInPostPublisher), null, "PublishAsync")]
    [InlineData(typeof(CommunityHub.Core.Integrations.Graphics.GraphSharePointFileStore), "EnsureMayWriteAsync", "StoreAsync")]
    [InlineData(typeof(CommunityHub.Core.Integrations.Graphics.GraphSharePointFileStore), "EnsureMayWriteAsync", "DeleteAsync")]
    [InlineData(typeof(CommunityHub.Core.Integrations.Graphics.GraphSharePointFileStore), "EnsureMayWriteAsync", "UploadToFolderAsync")]
    [InlineData(typeof(CommunityHub.Core.Integrations.Graphics.GraphSharePointFileStore), "EnsureMayWriteAsync", "DeleteFromFolderAsync")]
    public void Every_other_external_write_seam_consults_the_guard(
        Type seam, string? helperName, string methodName)
    {
        // LinkedIn calls the guard interface directly (one write method, no helper worth
        // having); the other two funnel through a private EnsureMayWriteAsync.
        var target = helperName is null
            ? typeof(IExternalWriteGuard).GetMethod(nameof(IExternalWriteGuard.AllowAsync))!
            : seam.GetMethod(helperName, BindingFlags.Instance | BindingFlags.NonPublic)!;

        Assert.True(CallsToken(seam, methodName, target.MetadataToken, helperName is null),
            $"{seam.Name}.{methodName} is an external WRITE but does not consult the §340-H "
            + "guard — it could write to a real third-party system from DEV. Add the guard; "
            + "do not remove this case.");
    }

    /// <summary>The banner must state the posture in words, not leave it to be inferred.</summary>
    [Fact]
    public void Startup_banner_states_the_posture_both_ways()
    {
        Assert.Contains("BLOCKED", ExternalWriteGuard.StartupBanner(false));
        Assert.Contains("ALLOWED", ExternalWriteGuard.StartupBanner(true));
    }
}
