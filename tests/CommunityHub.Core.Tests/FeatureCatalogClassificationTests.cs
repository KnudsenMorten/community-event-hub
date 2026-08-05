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

            // 🔒 §871 — SECOND documented exception, and the SAME SHAPE as the first: a KILL SWITCH
            // over behaviour that ALREADY SHIPS, not a new capability.
            //
            // The rule above exists so a deploy never STARTS something new. This switch was added
            // (operator 2026-08-05: "it could be nice to have a button to DISABLE this one") over a
            // job that has been mailing un-gated since §623. Defaulting it OFF would make the deploy
            // silently STOP a mail he relies on — the opposite failure, and just as bad.
            if (f.Key == "speaker-gap-report")
            {
                Assert.True(f.DefaultEnabled,
                    "speaker-gap-report is a kill switch over an already-shipping job — defaulting "
                    + "it OFF would silently stop the gap mail on deploy.");
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
            // §252 F7: economic-erp-sync was removed (inert duplicate of erp-webshop-reconcile).
            "sessionize-import", "backstage-sync", "erp-webshop-reconcile",
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
                     // §252 F7: economic-erp-sync removed (inert duplicate toggle).
                     "sessionize-import", "sponsor-order-pull", "attendee-reconcile",
                     "backstage-sync", "sponsor-upload-watch",
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
        foreach (var key in new[] { "welcome-email", "reminder-jobs", "digest-emails" })
        {
            var d = FeatureCatalog.Find(key)!;
            Assert.Equal(FeatureSurface.UserImpact, d.Surface);
            Assert.True(d.IsUserImpact);
            Assert.True(d.IsRingScoped);
        }

        // 🔒 §566 step 3 / §589 — UserImpact but TILE-ONLY ⇒ no longer ring-scoped. Their ring only
        // ever hid an organizer tile. "magic-link" is here too now: the operator confirmed it "goes
        // out to anyone", and no send site ever passed it as a FeatureKey anyway.
        foreach (var key in new[] { "surveys", "magic-link", "participant-activation" })
        {
            var d = FeatureCatalog.Find(key)!;
            Assert.Equal(FeatureSurface.UserImpact, d.Surface);
            Assert.True(d.GatesTileVisibilityOnly);
            Assert.False(d.IsRingScoped);
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
        // 🔒 §589 (operator 2026-07-28): *"queues are all managed by an organizer who
        // accept/approve, etc. so no need for ring-gate here"*. The organizer's APPROVAL is the
        // gate; a participant rollout ring on top of it is the same category error §569 removed
        // from the Zoho speaker/session push. Queue access is by ROLE.
        foreach (var key in new[] { "volunteer-tasks", "volunteer-allocation", "hotel-assignment" })
        {
            var d = FeatureCatalog.Find(key)!;
            Assert.Equal(FeatureSurface.Queue, d.Surface);
            Assert.True(d.IsQueue);
            Assert.False(d.IsRingScoped);   // §589 — the approval is the gate, not a ring
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
            // §252 F5: masterclass-invites removed — the MC funnel rides welcome-email.
            // §705.12: broadcast-email + invitation-email DELETED (verified unused in PROD).
            "email-resend", "onboarding-step-reset",
            "participant-activation", "session-eval-email",
            // §699 — "sponsor-welcome" removed from the catalog: it was a Settings switch no send
            // site ever consulted (§619 found this and removed it from one list, leaving the entry).
            "sponsor-tasks", "sponsor-reminders",
            // §705.14a — "graphics-release" DELETED: it sent no mail and only hid one organizer tile,
            // while its NAME implied it governed the speaker mail (that is speaker-graphics-promote).
            "group-photo-invites", "travel-reimbursement-email",
            "test-data-cleanup", "hotel-invite",
        };

        foreach (var key in incubationUserImpact)
        {
            var d = FeatureCatalog.Find(key);
            Assert.True(d is not null, $"Feature '{key}' missing from catalog.");
            Assert.Equal(FeatureSurface.UserImpact, d!.Surface);
            Assert.True(d.IsUserImpact, $"'{key}' must be USER-IMPACT (a person notices it).");

            // §695 — the GROUP assertion was removed on purpose. It pinned
            // `FeatureGroup.Incubation`, i.e. the PARKING SPOT, when what this test exists to
            // protect is the three properties below: a person notices it, it is released no wider
            // than ring 1 by default, and it is opt-in. Asserting the group made re-homing a shipped
            // feature into its real home (§694.4: session evals → Speakers, sponsor reminders →
            // Sponsors) fail a test about RINGS, which is why 18 live features were still parked in
            // a group labelled "Incubation (test)".
            //
            // 🔒 The ring assertions stay, and they are the point: moving a feature between groups
            // must never widen its audience.
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

        // §695 — this used to iterate the Email GROUP, which is now EMPTY (its members were re-homed
        // per role / to Event settings), leaving the loop vacuous: it would have passed forever while
        // asserting nothing. Asserting over what a feature GOVERNS survives any re-filing, and is the
        // property that actually matters — the same "assert the property, not the parking spot" fix
        // §700 Batch A applied to the Incubation tests.
        var emailGoverning = FeatureCatalog.All
            .Where(f => f.Governs == FeatureGoverns.Email)
            .ToList();
        Assert.NotEmpty(emailGoverning);                   // the loop must never go vacuous again
        foreach (var f in emailGoverning)
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

    // ---- 326bz: the three governance classes the Settings page is built from ----

    [Fact]
    public void Governs_splits_the_catalog_into_backend_emails_and_features()
    {
        // BACKEND = anything not ring-scoped. The ring is meaningless for these, which is
        // why the page shows them last and offers no ring control (326by).
        foreach (var d in FeatureCatalog.All.Where(x => !x.IsRingScoped))
        {
            Assert.Equal(FeatureGoverns.Backend, d.Governs);
        }

        // EMAIL = ring-scoped AND the key governs an outbound mail. Lowering the ring stops
        // the message reaching someone.
        Assert.Equal(FeatureGoverns.Email, FeatureCatalog.Find("welcome-email")!.Governs);
        // §705.12: the "broadcast-email" exemplar went with the feature; email-resend stands in.
        Assert.Equal(FeatureGoverns.Email, FeatureCatalog.Find("email-resend")!.Governs);
        Assert.Equal(FeatureGoverns.Email, FeatureCatalog.Find("hotel-invite")!.Governs);

        // FEATURE = ring-scoped but not a mail: the ring decides who SEES it.
        // Derived, not hardcoded — a named exemplar silently rots when a key is reclassified,
        // which is exactly what §566 step 3 just did to the three keys listed here before.
        foreach (var d in FeatureCatalog.All
                     .Where(x => x.IsRingScoped && !FeatureCatalog.EmailFeatureKeys.Contains(x.Key)))
        {
            Assert.Equal(FeatureGoverns.Feature, d.Governs);
        }

        // 🔒 §566 step 3 — the TILE-ONLY keys are now BACKEND (on/off, no ring). Their ring never
        // gated the survey, the activation or the cleanup — only whether an organizer TILE was
        // visible — while reading as a real audience control. That misreading is the §326bx
        // incident and why he dropped the category outright.
        Assert.Equal(FeatureGoverns.Backend, FeatureCatalog.Find("surveys")!.Governs);
        Assert.Equal(FeatureGoverns.Backend, FeatureCatalog.Find("test-data-cleanup")!.Governs);
        Assert.Equal(FeatureGoverns.Backend, FeatureCatalog.Find("participant-activation")!.Governs);

        // 🔒 §589 — "magic-link" is NOT ring-gated (operator 2026-07-28: "it goes out to anyone").
        // Verified before removing: no send site passes it as a FeatureKey and no template declares
        // it, so its ring gated nothing. Sign-in mail is exempted by a different, correct
        // mechanism — PinLoginService sends RingExempt, because someone who asks for a sign-in link
        // and hears nothing back cannot diagnose it.
        Assert.Equal(FeatureGoverns.Backend, FeatureCatalog.Find("magic-link")!.Governs);
        Assert.False(FeatureCatalog.Find("magic-link")!.IsRingScoped);
    }

    [Fact]
    public void Every_email_template_feature_key_is_a_real_catalog_key()
    {
        // A template filed under a key that is not in the catalog would silently lose its
        // per-feature ring at the transport (BrevoEmailSender only tightens when
        // FeatureCatalog.Find(key)?.IsRingScoped == true).
        foreach (var key in CommunityHub.Core.Email.EmailTemplateCatalog.Map.Values.Select(v => v.FeatureKey).Distinct())
        {
            Assert.True(FeatureCatalog.Find(key) is not null,
                $"Email template feature key '{key}' is not in the feature catalog.");
        }
    }

    /// <summary>
    /// §336 — the TILE-ONLY set is pinned by name.
    ///
    /// A ring on one of these hides an organizer TILE and gates nothing the switch's name
    /// describes ("Magic link … Released to Ring 1" reads as if auto-login links were limited
    /// to Ring 1; they are not). The §326bx / §327e / §336 audits turned up seven of these, one
    /// at a time, each found by hand. Pinning the set means a NEW dead gate — a key whose only
    /// consumer is a `.cshtml` tile — cannot appear without someone consciously editing this
    /// list, and equally that a key cannot silently STOP gating something while keeping an
    /// honest-looking ring. If this fails, do not just update the list: work out what the key
    /// actually gates now.
    /// </summary>
    [Fact]
    public void The_tile_only_set_is_exactly_the_seven_audited_keys()
    {
        var tileOnly = FeatureCatalog.All
            .Where(d => d.GatesTileVisibilityOnly)
            .Select(d => d.Key)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                // §705.14a — "graphics-release" DELETED (no send site; one tile; misleading name).
                "magic-link",              // §336 — the seventh
                "participant-activation",
                "sponsor-tasks",
                // §699 — "sponsor-welcome" DELETED from the catalog. This test's own sibling below
                // already recorded WHY it was safe: "no send site passes ... 'sponsor-welcome' as an
                // EmailContext.FeatureKey, and no template declares either — the only references are
                // the catalog itself. So these rings gated NOTHING." It has now been removed rather
                // than left as a switch that governs nothing.
                "surveys",
                "test-data-cleanup",
            },
            tileOnly);
    }

    /// <summary>
    /// 🔒 §566 step 3 — INVERTED ON PURPOSE. A tile-only key must NOT be ring-scoped.
    ///
    /// <para>Its ring never gated the function or the e-mail whose name it carried — only whether
    /// an organizer TILE was visible — while reading exactly like a real audience control. That is
    /// the §326bx incident ("Sponsor welcome … Released to Ring 1" implying the MAILS were limited)
    /// and a direct cause of the operator losing confidence in the Settings page. He dropped the
    /// category outright: *"Category 4 (tile-only) - drop-it"*.</para>
    ///
    /// <para>THE EXCEPTION IS A SAFETY PROPERTY: a key that is ALSO a known e-mail FeatureKey keeps
    /// its ring regardless, because removing a ring from an e-mail WIDENS its audience.
    /// <c>magic-link</c> is both, and without that clause this page cleanup would have silently
    /// un-gated sign-in link mails.</para>
    /// </summary>
    [Fact]
    public void Tile_only_keys_are_never_ring_scoped()
    {
        // VERIFIED KEY BY KEY BEFORE ACCEPTING THIS: no send site passes "magic-link" or
        // "sponsor-welcome" as an EmailContext.FeatureKey, and no template declares either — the
        // only references are the catalog itself. So these rings gated NOTHING, which is precisely
        // what §326bx reported ("Sponsor welcome … Released to Ring 1" read as if the MAILS were
        // limited to Ring 1; they never were). Sign-in mail is exempted by a different and correct
        // mechanism: PinLoginService sends RingExempt.
        foreach (var d in FeatureCatalog.All.Where(d => d.GatesTileVisibilityOnly))
        {
            Assert.False(d.IsRingScoped,
                $"'{d.Key}' is tile-only, so its ring only ever hid a tile. It must NOT show a "
                + "ring — that is the §326bx misreading the operator asked to remove.");
        }
    }

    [Fact]
    public void Email_class_never_contains_a_backend_key()
    {
        // EmailFeatureKeys is a superset (it includes the transport itself); the Governs
        // split must never promote a non-ring-scoped key into the e-mail section.
        foreach (var key in FeatureCatalog.EmailFeatureKeys)
        {
            var d = FeatureCatalog.Find(key);
            if (d is null || d.IsRingScoped) continue;
            Assert.Equal(FeatureGoverns.Backend, d.Governs);
        }
    }
}