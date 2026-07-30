using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Settings;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §515 — a release ring PER MAIL, so "ring 2 for speakers welcome and ring 1 for sponsors" is
/// actually expressible.
///
/// <para>Before this, the ring lived only on the FEATURE key and template→feature is many-to-one:
/// <c>welcome-speaker</c>, <c>welcome-sponsor</c>, <c>welcome-volunteer</c>, <c>welcome-media</c>,
/// <c>welcome-eventpartner</c> and the entire Master Class funnel all map to <c>welcome-email</c>.
/// One ring governed every persona's welcome at once, which is what blocked the operator from
/// going live with speakers alone.</para>
/// </summary>
public sealed class EmailTemplateRingTests
{
    private const int EventId = 7;
    private static readonly DateTimeOffset Now =
        new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"tpl-ring-{Guid.NewGuid():N}").Options);

    private static EmailTemplateRingService NewService(CommunityHubDbContext db) =>
        new(db, new FeatureGateService(db), new FixedClock(Now));

    // ---- the operator's actual case ----------------------------------------

    [Fact]
    public async Task Speaker_welcome_and_sponsor_welcome_can_sit_on_DIFFERENT_rings()
    {
        // The whole point of §515. Both share the welcome-email feature, so before this they
        // could only ever move together.
        using var db = NewDb();
        var svc = NewService(db);

        Assert.True(await svc.SetRingAsync(EventId, "welcome-speaker", Ring.Ring2, "op@test"));
        Assert.True(await svc.SetRingAsync(EventId, "welcome-sponsor", Ring.Ring1, "op@test"));

        Assert.Equal(Ring.Ring2, await svc.GetEffectiveRingAsync(EventId, "welcome-speaker"));
        Assert.Equal(Ring.Ring1, await svc.GetEffectiveRingAsync(EventId, "welcome-sponsor"));
    }

    [Fact]
    public async Task An_override_REPLACES_the_feature_ring_rather_than_tightening_it()
    {
        // Every other clamp in the ring model only narrows (§514). That rule cannot express what
        // he needs: under a Ring1 feature, a template set to Ring2 would clamp back to Ring1 and
        // the control could never do anything at all.
        using var db = NewDb();
        var settings = new FeatureSettingsService(db, new FixedClock(Now));
        await settings.SetReleasedRingAsync(EventId, "welcome-email", Ring.Ring1, null);
        var svc = NewService(db);

        await svc.SetRingAsync(EventId, "welcome-speaker", Ring.Ring2, "op@test");

        Assert.Equal(Ring.Ring2, await svc.GetEffectiveRingAsync(EventId, "welcome-speaker"));
    }

    /// <summary>
    /// 🔒 §705.2 — A REGISTERED MAIL WITH NO RING OF ITS OWN FAILS CLOSED TO Ring0. It no longer
    /// inherits its feature's ring.
    /// </summary>
    /// <remarks>
    /// This test asserted the OPPOSITE until 2026-07-29 (<c>Without_an_override_a_template_follows_its_
    /// feature_ring</c>), and the inversion is the POINT of §705, not a regression. Operator: *"i want
    /// INDIVIDUAL EMAIL RING GATES - one for each ! and remove features gates relevant for emails."*
    /// With the feature ring gone there is nothing legitimate left to inherit.
    ///
    /// <para>⚠️ Fail CLOSED specifically, because the old fallback for an unresolvable key was
    /// <c>Rings.Default</c> = <b>Broad</b>: a mail that slipped through unregistered would have gone to
    /// EVERYONE, and welcome + Master Class mail is the worst possible place for that (§699.2). Silence
    /// is recoverable; a mass-send is not.</para>
    ///
    /// <para>Safe to flip because the STATE was made explicit FIRST — all 32 non-exempt registered mails
    /// were given an explicit row in BOTH editions, each seeded with the ring it already resolved to. So
    /// this path is unreachable for every shipped mail; it guards the NEXT one someone adds.</para>
    /// </remarks>
    [Fact]
    public async Task Without_a_ring_of_its_own_a_registered_mail_fails_CLOSED_to_Ring0()
    {
        using var db = NewDb();
        var settings = new FeatureSettingsService(db, new FixedClock(Now));
        // Even with the FEATURE released Broad, the mail must not inherit it.
        await settings.SetReleasedRingAsync(EventId, "welcome-email", Ring.Broad, null);

        Assert.Equal(Ring.Ring0,
            await NewService(db).GetEffectiveRingAsync(EventId, "welcome-sponsor"));
    }

    /// <summary>
    /// §705 — every mail stands alone. Setting one does not move its siblings, and the siblings do not
    /// fall back to a shared feature ring either: they fail closed until given their own.
    /// </summary>
    [Fact]
    public async Task Each_mail_carries_its_OWN_ring_and_siblings_are_unaffected()
    {
        using var db = NewDb();
        var settings = new FeatureSettingsService(db, new FixedClock(Now));
        // A Broad feature ring must not leak into any of the 13 mails sharing this key.
        await settings.SetReleasedRingAsync(EventId, "welcome-email", Ring.Broad, null);
        var svc = NewService(db);

        await svc.SetRingAsync(EventId, "welcome-speaker", Ring.Ring2, "op@test");

        Assert.Equal(Ring.Ring2, await svc.GetEffectiveRingAsync(EventId, "welcome-speaker"));
        // 🔒 The siblings do NOT become Broad — the §515 trap, inverted and closed.
        Assert.Equal(Ring.Ring0, await svc.GetEffectiveRingAsync(EventId, "welcome-sponsor"));
        Assert.Equal(Ring.Ring0, await svc.GetEffectiveRingAsync(EventId, "masterclass-confirmed"));
    }

    /// <summary>
    /// Clearing a mail's ring is still not a one-way door — but it returns the mail to FAIL-CLOSED
    /// (Ring0), not to a feature ring. An operator can undo a decision; undoing it cannot silently
    /// widen an audience.
    /// </summary>
    [Fact]
    public async Task Clearing_the_ring_returns_the_mail_to_fail_closed()
    {
        using var db = NewDb();
        var settings = new FeatureSettingsService(db, new FixedClock(Now));
        await settings.SetReleasedRingAsync(EventId, "welcome-email", Ring.Broad, null);
        var svc = NewService(db);

        await svc.SetRingAsync(EventId, "welcome-speaker", Ring.Ring2, "op@test");
        Assert.True(await svc.ClearRingAsync(EventId, "welcome-speaker"));

        Assert.Equal(Ring.Ring0, await svc.GetEffectiveRingAsync(EventId, "welcome-speaker"));
    }

    [Fact]
    public async Task Setting_a_ring_twice_upserts_rather_than_duplicating()
    {
        using var db = NewDb();
        var svc = NewService(db);

        await svc.SetRingAsync(EventId, "welcome-speaker", Ring.Ring1, "op@test");
        await svc.SetRingAsync(EventId, "welcome-speaker", Ring.Ring2, "op@test");

        Assert.Equal(1, await db.EmailTemplateRings.CountAsync());
        Assert.Equal(Ring.Ring2, await svc.GetEffectiveRingAsync(EventId, "welcome-speaker"));
    }

    [Fact]
    public async Task An_unknown_template_key_is_refused()
    {
        // A typo must not create a row that silently governs nothing.
        using var db = NewDb();

        Assert.False(await NewService(db).SetRingAsync(EventId, "not-a-template", Ring.Broad, "op@test"));
        Assert.Equal(0, await db.EmailTemplateRings.CountAsync());
    }

    [Fact]
    public async Task Rings_are_scoped_per_edition()
    {
        using var db = NewDb();
        var svc = NewService(db);

        await svc.SetRingAsync(EventId, "welcome-speaker", Ring.Broad, "op@test");

        // A sibling edition is untouched and still follows its feature.
        Assert.NotEqual(Ring.Broad, await svc.GetEffectiveRingAsync(EventId + 1, "welcome-speaker"));
    }

    [Fact]
    public async Task The_editor_records_who_changed_it()
    {
        // This decides who receives real mail, so it is audited.
        using var db = NewDb();
        await NewService(db).SetRingAsync(EventId, "welcome-speaker", Ring.Ring2, "op@test");

        var row = await db.EmailTemplateRings.SingleAsync();
        Assert.Equal("op@test", row.UpdatedByEmail);
        Assert.Equal(Now, row.UpdatedAt);
    }

    // ---- the Settings page listing -----------------------------------------

    [Fact]
    public async Task Every_catalog_template_is_listed_so_none_is_uncontrollable()
    {
        // His words: "i still dont see the complete list of welcome emails and others on the
        // feature settings page, so i cannot control it".
        using var db = NewDb();

        var all = await NewService(db).GetAllAsync(EventId);

        Assert.Equal(EmailTemplateCatalog.Map.Count, all.Count);
        foreach (var key in EmailTemplateCatalog.Map.Keys)
            Assert.Contains(all, s => string.Equals(s.TemplateKey, key, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_listing_reports_how_many_mails_share_a_feature()
    {
        // So the page can warn that changing the FEATURE ring moves the siblings too.
        using var db = NewDb();
        var all = await NewService(db).GetAllAsync(EventId);

        var speaker = all.Single(s => s.TemplateKey == "welcome-speaker");
        Assert.True(speaker.SharedWithCount > 0,
            "welcome-speaker shares welcome-email with the other persona welcomes and the funnel.");
    }

    [Fact]
    public void Each_persona_welcome_files_under_its_own_role()
    {
        Assert.Equal(EmailAudience.Speaker, EmailTemplateCatalog.AudienceFor("welcome-speaker"));
        Assert.Equal(EmailAudience.Sponsor, EmailTemplateCatalog.AudienceFor("welcome-sponsor"));
        Assert.Equal(EmailAudience.Volunteer, EmailTemplateCatalog.AudienceFor("welcome-volunteer"));
        Assert.Equal(EmailAudience.Media, EmailTemplateCatalog.AudienceFor("welcome-media"));
        Assert.Equal(EmailAudience.EventPartner, EmailTemplateCatalog.AudienceFor("welcome-eventpartner"));
    }

    [Fact]
    public void The_whole_master_class_funnel_files_under_attendees_together()
    {
        // §252 F5 — the funnel must stay visible as one group so it is never split by accident.
        foreach (var key in new[]
                 {
                     "masterclass-selection-invite", "masterclass-confirmed", "masterclass-waitlisted",
                     "masterclass-cancelled", "masterclass-offer", "masterclass-promoted",
                 })
        {
            Assert.Equal(EmailAudience.Attendee, EmailTemplateCatalog.AudienceFor(key));
        }
    }

    [Fact]
    public void An_unclassified_template_falls_to_Everyone_not_to_a_wrong_role()
    {
        // Safe default: it shows in the general section rather than hiding under a role.
        Assert.Equal(EmailAudience.Everyone, EmailTemplateCatalog.AudienceFor("pin-signin"));
        Assert.Equal(EmailAudience.Everyone, EmailTemplateCatalog.AudienceFor("some-future-template"));
    }

    [Fact]
    public void Audience_grouping_covers_every_catalog_template_exactly_once()
    {
        var grouped = EmailTemplateCatalog.ByAudience().SelectMany(g => g).ToList();

        Assert.Equal(EmailTemplateCatalog.Map.Count, grouped.Count);
        Assert.Equal(grouped.Count, grouped.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
