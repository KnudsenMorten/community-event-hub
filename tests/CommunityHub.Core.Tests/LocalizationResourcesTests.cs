using System.Globalization;
using CommunityHub.Core.Resources;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// i18n resolution tests for the participant-facing UI (REQUIREMENTS §11).
///
/// The hub is ENGLISH-ONLY (operator directive 2026-06-18 — see REQUIREMENTS).
/// The Danish (da-DK) satellite was removed; there is a single SharedResource.resx
/// (English / invariant). These stand up a real
/// <see cref="ResourceManagerStringLocalizerFactory"/> pointed at that resource —
/// the same factory ASP.NET Core uses at runtime — and assert that:
///   1. the supported culture (en) resolves a known key (no MissingManifestResource),
///   2. the English (default) resource is the invariant fallback,
///   3. format-string keys keep their {n} placeholders so runtime args substitute,
///   4. de-duplicated keys stay removed.
///
/// Resources only — no schema/DB. If a future translator adds a culture, add it to
/// <see cref="SupportedCultures"/> (and ship a matching satellite) and these
/// placeholder/resolution assertions cover it for free.
/// </summary>
public sealed class LocalizationResourcesTests
{
    private static readonly string[] SupportedCultures = { "en" };

    private static IStringLocalizer<SharedResource> MakeLocalizer()
    {
        // ResourcesPath is empty: the SharedResource type lives in the
        // CommunityHub.Core.Resources namespace, so its full type name already
        // equals the embedded .resources base name (no ResourcesPath prefix).
        // Must match Program.cs (AddLocalization). See SharedResource doc-comment.
        var options = Options.Create(new LocalizationOptions { ResourcesPath = "" });
        var factory = new ResourceManagerStringLocalizerFactory(options, NullLoggerFactory.Instance);
        return new StringLocalizer<SharedResource>(factory);
    }

    private static T WithCulture<T>(string culture, Func<T> body)
    {
        var prevUi = CultureInfo.CurrentUICulture;
        var prev = CultureInfo.CurrentCulture;
        try
        {
            var ci = new CultureInfo(culture);
            CultureInfo.CurrentUICulture = ci;
            CultureInfo.CurrentCulture = ci;
            return body();
        }
        finally
        {
            CultureInfo.CurrentUICulture = prevUi;
            CultureInfo.CurrentCulture = prev;
        }
    }

    [Theory]
    [InlineData("en")]
    public void Supported_culture_resolves_a_known_key(string culture)
    {
        var loc = MakeLocalizer();

        var value = WithCulture(culture, () => loc["Login.Title"]);

        // A resolved value is NOT marked ResourceNotFound and is non-empty.
        Assert.False(value.ResourceNotFound,
            $"Login.Title did not resolve for culture '{culture}'.");
        Assert.False(string.IsNullOrWhiteSpace(value.Value));
    }

    [Fact]
    public void English_is_the_default_invariant_fallback()
    {
        var loc = MakeLocalizer();

        // Invariant culture must fall back to the default (English) resx.
        var invariant = WithCulture("", () => loc["Common.SignOut"].Value);

        Assert.Equal("Sign out", invariant);
        Assert.Equal("Sign in", WithCulture("en", () => loc["Login.Title"].Value));
    }

    [Fact]
    public void Representative_keys_across_the_localized_surface_resolve()
    {
        // Spot-check a broad set of keys across the participant + organizer pages to
        // catch a resx that built empty or lost a key. Each must resolve in the
        // default (English) resx (no raw-key leak / MissingManifestResource).
        var loc = MakeLocalizer();
        var keys = new[]
        {
            // Core nav / chrome / shared
            "Nav.Home", "Common.Save", "Common.Next", "Common.Back",
            "Common.SignOut", "Common.Dismiss", "Common.PleaseFix",
            "Common.Saving", "Common.Sending", "Common.BackToHub",
            "Layout.SkipToContent", "Layout.TicketInfo", "Layout.VisitEventSite",
            // Participant hubs + forms
            "Tasks.Title", "Speaker.Title", "MyEvent.Title",
            "Hotel.Title", "Dinner.Title", "Lunch.Title", "Swag.Title", "Travel.Title",
            "SpeakerForm.Title", "VolWiz.Title",
            // Onboarding + welcome
            "Welcome.WhatHeading", "Welcome.Continue",
            "Onboard.Heading", "Onboard.AllSet", "Onboard.GoToHub",
            // Status badges
            "Status.Done", "Status.Pending",
            // Organizer area
            "Lead.Title", "SponsorTasks.Title", "TaskRow.MarkComplete", "Attendee.Title",
            "Nav.OrgArea", "Nav.OrgAttendees", "Nav.OrgLunch",
            "Sessions.Delete", "Sessions.BulkDelete", "Speakers.Remove",
            "SponsorFacts.Delete", "Find.Title", "Exports.Title",
            "Comms.CockpitIntro", "Settings.Title", "OrgSurveys.Title",
            // Public site
            "Agenda.Title", "SessOv.Title", "MC.BeforeYouArrive",
            "Public.NoLiveEvent", "Sponsors.BecomeTitle",
            // Hub sub-card status strings
            "Hub.HotelOnFile", "Hub.DinnerNotYet", "Hub.VolWorkNone",
        };

        foreach (var key in keys)
        {
            var value = WithCulture("en", () => loc[key]);
            Assert.False(value.ResourceNotFound, $"{key} missing from default resx.");
            Assert.False(string.IsNullOrWhiteSpace(value.Value), $"{key} has an empty value.");
        }
    }

    [Fact]
    public void Formatted_keys_keep_their_placeholders()
    {
        // Keys that carry runtime args must keep their {n} placeholders so
        // string.Format substitutes correctly at render time.
        var loc = MakeLocalizer();

        // Single {0} placeholder.
        var single = new[]
        {
            "Common.CharCount", "Hub.VolWorkAssigned", "SponsorTasks.Pending",
            "VolWiz.StepOf", "Attendee.Reserved", "Results.NResponses",
            "Survey.JsPickMore", "Welcome.Heading", "Welcome.RoleIntro",
            "Welcome.Countdown", "Welcome.WhatBody", "Onboard.StepBio", "Onboard.StepSwag",
            "Find.Found", "Sessions.BulkDeleteConfirmBody", "Speakers.BulkRemoveConfirmBody",
            "OnbExport.PendingCount", "OnbReopen.Intro", "OnbReopen.Confirm",
            "ReqChange.Intro", "ReqChange.MessageLabel", "Agenda.UnscheduledNote",
            "SpeakerQ.OpenCount", "Plan.SavedCount", "Plan.RoomTag",
            "Plan.SaveAria", "Plan.RemoveAria", "RoomBlocks.StateOver",
            "Settings.ToggleAria", "Settings.DependsOn", "TestCleanup.ConfirmBody",
            "MC.PresentedBy", "MC.LastUpdated",
        };
        foreach (var key in single)
        {
            foreach (var culture in SupportedCultures)
            {
                Assert.Contains("{0}", WithCulture(culture, () => loc[key].Value));
            }
        }

        // {0} AND {1} placeholders.
        var dual = new[]
        {
            "Common.CharCount", "RoomBlocks.PlanWarn", "Settings.DependencyWarning",
            "Agenda.Summary", "SessOv.Showing",
        };
        foreach (var key in dual)
        {
            foreach (var culture in SupportedCultures)
            {
                var raw = WithCulture(culture, () => loc[key].Value);
                Assert.Contains("{0}", raw);
                Assert.Contains("{1}", raw);
            }
        }

        // {0}/{1}/{2} placeholders.
        foreach (var culture in SupportedCultures)
        {
            var summary = WithCulture(culture, () => loc["TestCleanup.Summary"].Value);
            Assert.Contains("{0}", summary);
            Assert.Contains("{1}", summary);
            Assert.Contains("{2}", summary);
        }
    }

    [Fact]
    public void Action_result_keys_keep_their_argument_placeholders()
    {
        // The honest send/provision/no-op/failure confirmation lines all carry {0};
        // the send/provisioned lines also carry {1} (count / url).
        var loc = MakeLocalizer();
        var keys = new[]
        {
            "Action.Sent", "Action.Provisioned", "Action.ProvisionedNoUrl",
            "Action.NoOp", "Action.Failed",
        };

        foreach (var culture in SupportedCultures)
        {
            foreach (var key in keys)
            {
                var value = WithCulture(culture, () => loc[key]);
                Assert.False(value.ResourceNotFound, $"{key} missing from default resx.");
                Assert.Contains("{0}", value.Value);
            }
            Assert.Contains("{1}", WithCulture(culture, () => loc["Action.Sent"].Value));
            Assert.Contains("{1}", WithCulture(culture, () => loc["Action.Provisioned"].Value));
        }
    }

    [Fact]
    public void Deduplicated_no_live_event_keys_stay_removed()
    {
        // The four public list pages standardize on the single shared key
        // Public.NoLiveEvent; the old per-page duplicates were removed. Guard so a
        // future edit can't silently reintroduce them.
        var loc = MakeLocalizer();

        Assert.False(loc["Public.NoLiveEvent"].ResourceNotFound,
            "Public.NoLiveEvent missing from default resx.");
        Assert.True(loc["Agenda.NoEvent"].ResourceNotFound,
            "Agenda.NoEvent should be removed (use Public.NoLiveEvent).");
        Assert.True(loc["SessOv.NoEvent"].ResourceNotFound,
            "SessOv.NoEvent should be removed (use Public.NoLiveEvent).");
    }
}
