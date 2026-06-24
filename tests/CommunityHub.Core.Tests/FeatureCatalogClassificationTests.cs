using CommunityHub.Core.Settings;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// The §23 TEST/RELEASE GATE for the feature-customization framework. An advanced
/// feature is NOT "done" until it is correctly classified in the one catalog the
/// GUI + jobs + tests read. These assertions hold the catalog honest:
///   - every entry declares a tier AND a group (no un-grouped capability),
///   - every advanced feature defaults OFF (opt-in) — except the email master
///     switch, which is the documented exception (defaults ON so the hub mails on
///     day one; turning it off is the global kill),
///   - keys are unique, stable kebab-case, with i18n name + description keys,
///   - every declared dependency resolves to another catalog entry.
///
/// The "a disabled advanced feature is inert (no work / no sends)" half of the
/// gate is proved behaviourally in <see cref="FeatureGateServiceTests"/> and the
/// job/service gate tests.
/// </summary>
public sealed class FeatureCatalogClassificationTests
{
    [Fact]
    public void Catalog_is_non_empty()
    {
        Assert.NotEmpty(FeatureCatalog.All);
    }

    [Fact]
    public void Every_entry_declares_a_tier_and_a_group()
    {
        foreach (var f in FeatureCatalog.All)
        {
            Assert.True(
                f.Tier is FeatureTier.Core or FeatureTier.Advanced,
                $"'{f.Key}' has no valid tier.");
            Assert.True(
                System.Enum.IsDefined(typeof(FeatureGroup), f.Group),
                $"'{f.Key}' has no valid group.");
        }
    }

    [Fact]
    public void Every_advanced_feature_defaults_off_except_the_email_master_switch()
    {
        foreach (var f in FeatureCatalog.All.Where(f => f.IsAdvanced))
        {
            if (f.Key == FeatureCatalog.OutboundEmailKey)
            {
                // The documented exception: the master email switch defaults ON.
                Assert.True(f.DefaultEnabled,
                    "The outbound-email master switch must default ON (it is the global kill switch).");
                continue;
            }
            Assert.False(f.DefaultEnabled,
                $"Advanced feature '{f.Key}' must default OFF (opt-in) so a deploy never springs new behaviour.");
        }
    }

    [Fact]
    public void Keys_are_unique_stable_kebab_case()
    {
        var keys = FeatureCatalog.All.Select(f => f.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct(System.StringComparer.Ordinal).Count());

        foreach (var key in keys)
        {
            Assert.False(string.IsNullOrWhiteSpace(key));
            Assert.Matches("^[a-z0-9]+(-[a-z0-9]+)*$", key);
        }
    }

    [Fact]
    public void Every_entry_carries_i18n_name_and_description_keys()
    {
        foreach (var f in FeatureCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(f.DisplayNameKey), $"'{f.Key}' has no name key.");
            Assert.False(string.IsNullOrWhiteSpace(f.DescriptionKey), $"'{f.Key}' has no description key.");
        }
    }

    [Fact]
    public void Every_declared_dependency_resolves_to_a_catalog_entry()
    {
        foreach (var f in FeatureCatalog.All)
        {
            foreach (var dep in f.DependsOn)
            {
                Assert.True(FeatureCatalog.Find(dep) is not null,
                    $"'{f.Key}' depends on unknown feature '{dep}'.");
                Assert.NotEqual(f.Key, dep); // no self-dependency
            }
        }
    }

    [Fact]
    public void The_real_advanced_integrations_are_present_and_classified_advanced()
    {
        // The §23 seed set: the operator's real advanced integrations/automation.
        var required = new[]
        {
            "sessionize-import", "backstage-sync", "economic-erp-sync",
            "some-scheduling", "linkedin-queue", "reminder-jobs", "digest-emails",
            "welcome-email", "magic-link", "surveys",
            // §23 residual: the remaining advanced sync jobs + their web triggers.
            "sponsor-order-pull", "sponsor-leads", "sponsor-upload-watch", "attendee-reconcile",
            FeatureCatalog.OutboundEmailKey,
        };

        foreach (var key in required)
        {
            var d = FeatureCatalog.Find(key);
            Assert.True(d is not null, $"Catalog is missing the required feature '{key}'.");
            Assert.Equal(FeatureTier.Advanced, d!.Tier);
        }
    }

    [Fact]
    public void DefaultEnabled_lookup_falls_open_for_unknown_keys()
    {
        // A non-feature key (e.g. a core capability never in the catalog) must not
        // silently gate to OFF — the gate fails open for unknown keys.
        Assert.True(FeatureCatalog.DefaultEnabled("not-a-real-feature"));
    }

    // ---- §23 OPERATOR RULES (release-ring rollout) ------------------------

    /// <summary>
    /// RULE 1 (operator 2026-06-21: "default features released to ring 1, not 0") —
    /// the descriptor default released ring is Ring1, so a NEW catalog feature added
    /// with no explicit ring releases to ring 1 (visible to ring-0 + ring-1 testers,
    /// not yet ring-2/Broad) and never auto-exposes broadly in prod.
    /// </summary>
    [Fact]
    public void New_feature_default_released_ring_is_ring1()
    {
        // A descriptor constructed WITHOUT specifying the ring (a future new entry).
        var future = new FeatureDescriptor(
            "future-feature", "X.Name", "X.Desc",
            FeatureGroup.Attendees, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: System.Array.Empty<string>());

        Assert.Equal(Ring.Ring1, future.DefaultReleasedToRing);
    }

    /// <summary>
    /// §23a STARTING RING (operator 2026-06-21: "all features at ring 1") — every
    /// catalog feature is pinned to Ring.Ring1 (controlled-rollout posture before
    /// go-live; ring-0 + ring-1 testers see it until promoted to Broad). None sits
    /// on the old Ring0 default.
    /// </summary>
    [Fact]
    public void Feature_surface_classification_is_consistent()
    {
        // (1) ENGINE — core plumbing (pulls/syncs/transport): GA/Broad, NOT ring-scoped.
        foreach (var key in new[]
                 {
                     "sessionize-import", "sponsor-order-pull", "attendee-reconcile",
                     "backstage-sync", "economic-erp-sync", "sponsor-upload-watch",
                 })
        {
            var d = FeatureCatalog.Find(key)!;
            Assert.Equal(FeatureSurface.Engine, d.Surface);
            Assert.True(d.IsEngine);
            Assert.False(d.IsRingScoped);
            Assert.Equal(Ring.Broad, d.DefaultReleasedToRing);   // GA, runs for all
        }

        // (2) ENGINE-QUEUED — backend fed by a queue commit: GA/Broad, NOT ring-scoped
        // (inert until a queue commits scoped data).
        foreach (var key in new[] { "some-scheduling", "linkedin-queue" })
        {
            var d = FeatureCatalog.Find(key)!;
            Assert.Equal(FeatureSurface.EngineQueued, d.Surface);
            Assert.True(d.IsEngine);
            Assert.False(d.IsRingScoped);
            Assert.Equal(Ring.Broad, d.DefaultReleasedToRing);
        }

        // sponsor-leads is a backend EXPORT to the sponsor (Zoho link, no API yet) —
        // Engine, kill-switch only, GA (operator 2026-06-22). Not ring-scoped.
        var sl = FeatureCatalog.Find("sponsor-leads")!;
        Assert.Equal(FeatureSurface.Engine, sl.Surface);
        Assert.False(sl.IsRingScoped);
        Assert.Equal(Ring.Broad, sl.DefaultReleasedToRing);

        // (4) USER-IMPACT — a human experiences it (email/task/GUI): ring-scoped,
        // staged-rolled on the target user.
        foreach (var key in new[]
                 {
                     "welcome-email", "magic-link", "reminder-jobs", "digest-emails",
                     "surveys",
                 })
        {
            var d = FeatureCatalog.Find(key)!;
            Assert.Equal(FeatureSurface.UserImpact, d.Surface);
            Assert.True(d.IsUserImpact);
            Assert.True(d.IsRingScoped);
        }

        // outbound-email is the ENGINE transport (+ global kill switch); held at
        // Ring1 as the email ceiling until go-live.
        var oe = FeatureCatalog.Find(FeatureCatalog.OutboundEmailKey)!;
        Assert.Equal(FeatureSurface.Engine, oe.Surface);
        Assert.False(oe.IsRingScoped);
    }

    /// <summary>
    /// (3) QUEUE surfaces (operator 2026-06-22): organizer-operated staging+commit
    /// for an engine — ring-scoped (2nd-confirm + ring-scoped impact at commit). These
    /// three are ALREADY-SHIPPED features, so they default to Broad (GA) — no behaviour
    /// regression — while the ring-scoping mechanism (CommitAsync) lets an organizer
    /// lower the ring to Ring1 to test a change. (A NEW queue feature is born Ring1.)
    /// </summary>
    [Fact]
    public void Queue_surfaces_are_classified_and_ring_scoped()
    {
        foreach (var key in new[] { "volunteer-tasks", "volunteer-allocation", "hotel-assignment" })
        {
            var d = FeatureCatalog.Find(key)!;
            Assert.Equal(FeatureSurface.Queue, d.Surface);
            Assert.True(d.IsQueue);
            Assert.True(d.IsRingScoped);   // ring-scopable — dial down to Ring1 to test
            Assert.False(d.IsEngine);
            Assert.Equal(Ring.Broad, d.DefaultReleasedToRing);   // shipped ⇒ GA default
        }
    }

    /// <summary>
    /// The Incubation user-impact GUI actions (operator 2026-06-22): every new
    /// mass-impact GUI action (broadcast, invitations, activations, assignments,
    /// task creation, releases) is born in Incubation, classified USER-IMPACT and
    /// released to Ring1 so only ring-1 testers see + exercise it until promoted.
    /// These back the hub tiles (HubTile.FeatureKey) so the GUI badges + gates them.
    /// </summary>
    [Fact]
    public void Incubation_user_impact_actions_are_classified_and_ring1()
    {
        // NB: volunteer-tasks / volunteer-allocation / hotel-assignment are QUEUE
        // surfaces (see Queue_surfaces_are_classified_and_ring_scoped), not here.
        var incubationUserImpact = new[]
        {
            "broadcast-email", "invitation-email", "email-resend", "onboarding-step-reset",
            "participant-activation", "masterclass-invites", "session-eval-email",
            "sponsor-welcome", "sponsor-tasks", "sponsor-reminders",
            "group-photo-invites", "travel-reimbursement-email", "graphics-release",
            "test-data-cleanup", "hotel-invite", "dinner-invite",
        };

        foreach (var key in incubationUserImpact)
        {
            var d = FeatureCatalog.Find(key);
            Assert.True(d is not null, $"Incubation feature '{key}' missing from catalog.");
            Assert.Equal(FeatureSurface.UserImpact, d!.Surface);
            Assert.True(d.IsUserImpact, $"'{key}' must be USER-IMPACT (a person notices it).");
            Assert.Equal(FeatureGroup.Incubation, d.Group);
            Assert.Equal(Ring.Ring1, d.DefaultReleasedToRing);
            Assert.False(d.DefaultEnabled, $"'{key}' must default OFF (opt-in).");
        }
    }

    [Fact]
    public void Existing_features_are_released_to_ring1_or_broad_never_ring0()
    {
        // Operator 2026-06-22: a catalog feature is either still in TESTING (Ring1)
        // or promoted to GA (Broad) — never born dev-only (Ring0). The tested backend
        // pulls/syncs are GA/Broad; email + sensitive features stay Ring1.
        foreach (var f in FeatureCatalog.All)
        {
            Assert.True(
                f.DefaultReleasedToRing == Ring.Ring1 || f.DefaultReleasedToRing == Ring.Broad,
                $"{f.Key} should be Ring1 (testing) or Broad (GA); got {f.DefaultReleasedToRing}.");
            Assert.NotEqual(Ring.Ring0, f.DefaultReleasedToRing);
        }
    }

    /// <summary>
    /// RULE 2 — every OUTBOUND-EMAIL feature is released only to Ring1, so mail
    /// reaches ring 0 + ring 1 and NOTHING in ring 2 / ring 3. Critical safety net.
    /// </summary>
    [Fact]
    public void Every_outbound_email_feature_is_released_to_ring1()
    {
        var emailFeatures = new[]
        {
            FeatureCatalog.OutboundEmailKey, "welcome-email", "magic-link",
            "reminder-jobs", "digest-emails",
        };

        foreach (var key in emailFeatures)
        {
            var d = FeatureCatalog.Find(key);
            Assert.True(d is not null, $"Email feature '{key}' missing from catalog.");
            Assert.Equal(Ring.Ring1, d!.DefaultReleasedToRing);
        }

        // And every feature in the Email GROUP is at ring 1 (none widened to Broad).
        foreach (var f in FeatureCatalog.All.Where(f => f.Group == FeatureGroup.Email))
        {
            Assert.Equal(Ring.Ring1, f.DefaultReleasedToRing);
        }
    }

    /// <summary>
    /// An unknown (non-feature) key still falls OPEN to Broad for the released-ring
    /// lookup — a non-feature call is never silently restricted to ring 0.
    /// </summary>
    [Fact]
    public void DefaultReleasedToRing_lookup_falls_open_to_broad_for_unknown_keys()
    {
        Assert.Equal(Ring.Broad, FeatureCatalog.DefaultReleasedToRing("not-a-real-feature"));
    }

    private static bool IsOutboundEmail(string key) => key is
        FeatureCatalog.OutboundEmailKey or "welcome-email" or "magic-link"
        or "reminder-jobs" or "digest-emails";

    [Fact]
    public void ByGroup_returns_groups_in_display_order_and_covers_every_entry()
    {
        var grouped = FeatureCatalog.ByGroup();
        var flattened = grouped.SelectMany(g => g).Select(f => f.Key).ToHashSet();
        Assert.Equal(FeatureCatalog.All.Select(f => f.Key).ToHashSet(), flattened);

        // Groups are ordered by the enum value (the GUI render order).
        var order = grouped.Select(g => (int)g.Key).ToList();
        Assert.Equal(order.OrderBy(x => x).ToList(), order);
    }
}
