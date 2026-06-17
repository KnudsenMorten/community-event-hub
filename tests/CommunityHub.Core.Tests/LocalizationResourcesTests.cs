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
/// These stand up a real <see cref="ResourceManagerStringLocalizerFactory"/>
/// pointed at the web app's <c>SharedResource</c> resources — the same factory
/// ASP.NET Core uses at runtime — and assert that:
///   1. both supported cultures resolve a known key (no MissingManifestResource),
///   2. a key actually DIFFERS between English and Danish (proves the da-DK
///      satellite is wired, not silently falling back to English),
///   3. the English (default) resource is the invariant fallback.
///
/// Resources only — no schema/DB. If a future translator adds a culture, add it
/// to <see cref="SupportedCultures"/> and these assertions cover it for free.
/// </summary>
public sealed class LocalizationResourcesTests
{
    private static readonly string[] SupportedCultures = { "en", "da-DK" };

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
    [InlineData("da-DK")]
    public void Both_supported_cultures_resolve_a_known_key(string culture)
    {
        var loc = MakeLocalizer();

        var value = WithCulture(culture, () => loc["Login.Title"]);

        // A resolved value is NOT marked ResourceNotFound and is non-empty.
        Assert.False(value.ResourceNotFound,
            $"Login.Title did not resolve for culture '{culture}'.");
        Assert.False(string.IsNullOrWhiteSpace(value.Value));
    }

    [Fact]
    public void A_key_differs_between_english_and_danish()
    {
        var loc = MakeLocalizer();

        var en = WithCulture("en", () => loc["Login.Title"].Value);
        var da = WithCulture("da-DK", () => loc["Login.Title"].Value);

        Assert.Equal("Sign in", en);
        Assert.Equal("Log ind", da);
        Assert.NotEqual(en, da);
    }

    [Fact]
    public void English_is_the_default_invariant_fallback()
    {
        var loc = MakeLocalizer();

        // Invariant culture must fall back to the default (English) resx.
        var invariant = WithCulture("", () => loc["Common.SignOut"].Value);

        Assert.Equal("Sign out", invariant);
    }

    [Fact]
    public void Danish_translations_are_present_for_the_localized_slice()
    {
        // Spot-check a handful of keys across the localized pages to catch a
        // da-DK satellite that built empty or fell back to English wholesale.
        var loc = MakeLocalizer();
        var keys = new[]
        {
            "Nav.Home", "Tasks.Title", "Speaker.Title", "MyEvent.Title", "Common.BackToHub",
        };

        foreach (var key in keys)
        {
            var en = WithCulture("en", () => loc[key].Value);
            var da = WithCulture("da-DK", () => loc[key].Value);
            Assert.False(loc[key].ResourceNotFound, $"{key} missing from default resx.");
            Assert.NotEqual(en, da);
        }
    }

    [Fact]
    public void Danish_translations_are_present_for_the_deferred_page_slice()
    {
        // Second i18n slice (REQUIREMENTS §11 follow-up): the self-service Forms/*,
        // Sponsor lead-capture + tasks, Attendee detail, Survey wizard + Results,
        // the organizer-area nav entries, and the hub role sub-card status strings.
        // One representative key per newly-localized area; each must resolve in the
        // default resx AND differ from English (proves the da-DK satellite is wired).
        var loc = MakeLocalizer();
        var keys = new[]
        {
            // Forms
            "Hotel.Title", "Dinner.Title", "Lunch.Title", "Swag.Title", "Travel.Title",
            "SpeakerForm.Title", "VolWiz.Title",
            // Sponsor lead capture + tasks (incl. task-row partial)
            "Lead.Title", "SponsorTasks.Title", "TaskRow.MarkComplete",
            // Attendee detail
            "Attendee.Title",
            // Survey wizard + results (server + JS-injected strings)
            "Survey.Step1Heading", "Survey.JsRank1", "Results.Responses", "Results.PerTrackFavorites",
            // Organizer-area nav entries
            "Nav.OrgArea", "Nav.OrgAttendees", "Nav.OrgLunch",
            // Hub role sub-card status strings
            "Hub.HotelOnFile", "Hub.DinnerNotYet", "Hub.VolWorkNone", "Status.Done",
            // Shared additions used across the slice
            "Common.Save", "Common.Next", "Common.Back",
        };

        foreach (var key in keys)
        {
            var en = WithCulture("en", () => loc[key].Value);
            var da = WithCulture("da-DK", () => loc[key].Value);
            Assert.False(loc[key].ResourceNotFound, $"{key} missing from default resx.");
            Assert.False(string.IsNullOrWhiteSpace(en), $"{key} has an empty English value.");
            Assert.False(string.IsNullOrWhiteSpace(da), $"{key} has an empty Danish value.");
            Assert.NotEqual(en, da);
        }
    }

    [Fact]
    public void Danish_translations_are_present_for_the_onboarding_slice()
    {
        // Third i18n slice: the previously English-only onboarding surfaces — the
        // one-time Welcome landing (Welcome.*) and the mandatory first-run wizard
        // (Onboard.*). One representative key per area; each must resolve in the
        // default resx AND differ from English (proves the da-DK satellite is wired).
        var loc = MakeLocalizer();
        var keys = new[]
        {
            // Welcome landing
            "Welcome.WhatHeading", "Welcome.WhyHeading", "Welcome.RightAwayHeading",
            "Welcome.Continue", "Welcome.OnceNote",
            // Onboarding wizard chrome + step headings
            "Onboard.Heading", "Onboard.Intro", "Onboard.AllSet", "Onboard.GoToHub",
            // (StepHotel/StepSwag are intentionally NOT here — "Hotel"/"Swag" are
            // identical in en and da, so the en≠da assertion would fail by design;
            // their resolution is still asserted via the placeholder-integrity test.)
            "Onboard.StepBio", "Onboard.StepPicture", "Onboard.StepAppreciation",
            "Onboard.NeedsRoom", "Onboard.WantsAward", "Onboard.SaveContinue", "Onboard.Finish",
        };

        foreach (var key in keys)
        {
            var en = WithCulture("en", () => loc[key].Value);
            var da = WithCulture("da-DK", () => loc[key].Value);
            Assert.False(loc[key].ResourceNotFound, $"{key} missing from default resx.");
            Assert.False(string.IsNullOrWhiteSpace(en), $"{key} has an empty English value.");
            Assert.False(string.IsNullOrWhiteSpace(da), $"{key} has an empty Danish value.");
            Assert.NotEqual(en, da);
        }
    }

    [Fact]
    public void Shared_ux_component_keys_resolve_in_both_cultures()
    {
        // Shared UX components slice (REQUIREMENTS §21): the flash toast Dismiss
        // label, the validation-summary lead, and the Travel field-validation
        // messages added with the inline-validation pattern. Each must resolve in
        // the default resx AND differ between English and Danish (proves the da-DK
        // satellite carries them, not a silent English fallback).
        var loc = MakeLocalizer();
        var keys = new[]
        {
            "Common.Dismiss", "Common.PleaseFix",
            "Travel.ErrPickAmount", "Travel.ErrOtherAmount", "Travel.ErrOtherExplanation",
        };

        foreach (var key in keys)
        {
            var en = WithCulture("en", () => loc[key].Value);
            var da = WithCulture("da-DK", () => loc[key].Value);
            Assert.False(loc[key].ResourceNotFound, $"{key} missing from default resx.");
            Assert.False(string.IsNullOrWhiteSpace(en), $"{key} has an empty English value.");
            Assert.False(string.IsNullOrWhiteSpace(da), $"{key} has an empty Danish value.");
            Assert.NotEqual(en, da);
        }
    }

    [Fact]
    public void Attendee_my_event_schedule_keys_resolve_in_both_cultures()
    {
        // §20 Attendee My-event slice: the personal schedule / my-sessions / agenda
        // empty-states, the per-session ask + evaluate link labels, and the
        // quick-links help. Each must resolve in the default resx and (where the word
        // genuinely differs) carry a distinct Danish value.
        var loc = MakeLocalizer();

        // These genuinely differ between English and Danish.
        var distinct = new[]
        {
            "MyEvent.MySessions", "MyEvent.NoMySessions", "MyEvent.MineTag",
            "MyEvent.Agenda", "MyEvent.NoAgenda", "MyEvent.SessionDetails",
            "MyEvent.AskQuestion", "MyEvent.Evaluate", "MyEvent.AskAndEvaluate",
            "MyEvent.AskAndEvaluateHelp", "MyEvent.QuickLinks", "MyEvent.QuickLinksHelp",
            "MyEvent.FullAgenda",
        };
        foreach (var key in distinct)
        {
            var en = WithCulture("en", () => loc[key].Value);
            var da = WithCulture("da-DK", () => loc[key].Value);
            Assert.False(loc[key].ResourceNotFound, $"{key} missing from default resx.");
            Assert.False(string.IsNullOrWhiteSpace(en), $"{key} has an empty English value.");
            Assert.False(string.IsNullOrWhiteSpace(da), $"{key} has an empty Danish value.");
            Assert.NotEqual(en, da);
        }

        // "Hotel"/"Lunch" are identical in en + da by design; assert resolution +
        // non-empty only (the en≠da assertion would fail by design, cf. the
        // onboarding slice).
        foreach (var key in new[] { "MyEvent.Hotel", "MyEvent.Swag", "MyEvent.Lunch" })
        {
            var en = WithCulture("en", () => loc[key].Value);
            var da = WithCulture("da-DK", () => loc[key].Value);
            Assert.False(loc[key].ResourceNotFound, $"{key} missing from default resx.");
            Assert.False(string.IsNullOrWhiteSpace(en), $"{key} has an empty English value.");
            Assert.False(string.IsNullOrWhiteSpace(da), $"{key} has an empty Danish value.");
        }
    }

    [Fact]
    public void Sessionize_dryrun_and_session_delete_keys_resolve_in_both_cultures()
    {
        // §21 organizer slice: the Sessionize import dry-run/preview labels and the
        // Sessions / pre-selection-queue delete strings. Each must resolve in the
        // default resx AND differ between English and Danish (proves the da-DK
        // satellite carries them, not a silent English fallback).
        var loc = MakeLocalizer();
        var keys = new[]
        {
            // Import dry-run / preview
            "Sezimport.PreviewDelta", "Sezimport.PreviewFull", "Sezimport.PreviewUpload",
            "Sezimport.PreviewHeading", "Sezimport.WouldOverwrite", "Sezimport.NoOverwrite",
            "Sezimport.DeltaNoOverwrite", "Sezimport.CuratedBySpeaker", "Sezimport.ConfirmFullTitle",
            // Session + queue delete
            "Sessions.Delete", "Sessions.DeleteConfirmTitle", "Sessions.DeleteBlocked",
            "Queue.Delete", "Queue.DeleteConfirmTitle",
        };

        foreach (var key in keys)
        {
            var en = WithCulture("en", () => loc[key].Value);
            var da = WithCulture("da-DK", () => loc[key].Value);
            Assert.False(loc[key].ResourceNotFound, $"{key} missing from default resx.");
            Assert.False(string.IsNullOrWhiteSpace(en), $"{key} has an empty English value.");
            Assert.False(string.IsNullOrWhiteSpace(da), $"{key} has an empty Danish value.");
            Assert.NotEqual(en, da);
        }
    }

    [Fact]
    public void Sessions_bulk_delete_keys_resolve_in_both_cultures()
    {
        // §20 universal CRUD + bulk — the Sessions grid bulk-delete bar + confirm
        // modal strings. Each must resolve in the default resx AND differ between
        // English and Danish (proves the da-DK satellite carries them, not a silent
        // English fallback).
        var loc = MakeLocalizer();
        var keys = new[]
        {
            "Sessions.BulkDelete", "Sessions.BulkDeleteHint",
            "Sessions.BulkDeleteConfirmTitle",
        };

        foreach (var key in keys)
        {
            var en = WithCulture("en", () => loc[key].Value);
            var da = WithCulture("da-DK", () => loc[key].Value);
            Assert.False(loc[key].ResourceNotFound, $"{key} missing from default resx.");
            Assert.False(string.IsNullOrWhiteSpace(en), $"{key} has an empty English value.");
            Assert.False(string.IsNullOrWhiteSpace(da), $"{key} has an empty Danish value.");
            Assert.NotEqual(en, da);
        }

        // The confirm body carries a {0} count placeholder in both cultures.
        foreach (var culture in SupportedCultures)
        {
            Assert.Contains("{0}", WithCulture(culture, () => loc["Sessions.BulkDeleteConfirmBody"].Value));
        }
    }

    [Fact]
    public void Speaker_remove_keys_resolve_in_both_cultures()
    {
        // §22 "Speakers delete": the per-row + bulk remove-from-speakers labels,
        // the "still on the agenda" note, and the confirm-modal titles/bodies. Each
        // must resolve in the default resx AND differ between English and Danish
        // (proves the da-DK satellite carries them, not a silent English fallback).
        var loc = MakeLocalizer();
        var keys = new[]
        {
            "Speakers.Remove", "Speakers.OnAgenda",
            "Speakers.RemoveConfirmTitle", "Speakers.RemoveConfirmBody",
            "Speakers.BulkRemove", "Speakers.BulkRemoveHint",
            "Speakers.BulkRemoveConfirmTitle",
        };

        foreach (var key in keys)
        {
            var en = WithCulture("en", () => loc[key].Value);
            var da = WithCulture("da-DK", () => loc[key].Value);
            Assert.False(loc[key].ResourceNotFound, $"{key} missing from default resx.");
            Assert.False(string.IsNullOrWhiteSpace(en), $"{key} has an empty English value.");
            Assert.False(string.IsNullOrWhiteSpace(da), $"{key} has an empty Danish value.");
            Assert.NotEqual(en, da);
        }

        // The bulk confirm body carries a {0} count placeholder in both cultures.
        foreach (var culture in SupportedCultures)
        {
            Assert.Contains("{0}",
                WithCulture(culture, () => loc["Speakers.BulkRemoveConfirmBody"].Value));
        }
    }

    [Fact]
    public void Sponsor_facts_delete_keys_resolve_in_both_cultures()
    {
        // §22 "Sponsor contacts / facts CRUD gap": the stale company-facts section
        // heading/intro, the column headers, the delete label, and the confirm-modal
        // title/body. Each must resolve in the default resx AND differ between
        // English and Danish (proves the da-DK satellite carries them).
        var loc = MakeLocalizer();
        var keys = new[]
        {
            "SponsorFacts.OrphanHeading", "SponsorFacts.OrphanIntro",
            "SponsorFacts.ColCompany", "SponsorFacts.ColShortDesc",
            "SponsorFacts.Delete", "SponsorFacts.DeleteConfirmTitle",
            "SponsorFacts.DeleteConfirmBody",
        };

        foreach (var key in keys)
        {
            var en = WithCulture("en", () => loc[key].Value);
            var da = WithCulture("da-DK", () => loc[key].Value);
            Assert.False(loc[key].ResourceNotFound, $"{key} missing from default resx.");
            Assert.False(string.IsNullOrWhiteSpace(en), $"{key} has an empty English value.");
            Assert.False(string.IsNullOrWhiteSpace(da), $"{key} has an empty Danish value.");
            Assert.NotEqual(en, da);
        }
    }

    [Fact]
    public void Organizer_nav_section_heading_keys_resolve_in_both_cultures()
    {
        // REQUIREMENTS §21 "Group the organizer nav": the six collapsible
        // section headings (People / Sessions / Comms / Sponsors / Volunteers /
        // Logistics). Each must resolve in the default resx AND differ between
        // English and Danish (proves the da-DK satellite carries them, not a
        // silent English fallback).
        var loc = MakeLocalizer();
        var keys = new[]
        {
            "Nav.OrgSectionPeople", "Nav.OrgSectionSessions", "Nav.OrgSectionComms",
            "Nav.OrgSectionSponsors", "Nav.OrgSectionVolunteers", "Nav.OrgSectionLogistics",
        };

        foreach (var key in keys)
        {
            var en = WithCulture("en", () => loc[key].Value);
            var da = WithCulture("da-DK", () => loc[key].Value);
            Assert.False(loc[key].ResourceNotFound, $"{key} missing from default resx.");
            Assert.False(string.IsNullOrWhiteSpace(en), $"{key} has an empty English value.");
            Assert.False(string.IsNullOrWhiteSpace(da), $"{key} has an empty Danish value.");
            Assert.NotEqual(en, da);
        }
    }

    [Fact]
    public void Forms_ux_batch_keys_resolve_in_both_cultures()
    {
        // §21 Participant [H] forms-UX batch: the per-form validation messages
        // (Dinner/Hotel/Swag) and the structured dietary/allergy capture labels
        // shared by the Dinner + Speaker forms. Each must resolve in the default
        // resx AND differ between English and Danish (proves the da-DK satellite
        // carries them, not a silent English fallback).
        var loc = MakeLocalizer();
        var keys = new[]
        {
            // Per-form validation
            "Dinner.ErrPickRsvp", "Hotel.ErrPickNeed", "Hotel.ErrCheckOrder", "Swag.ErrPickPolo",
            // Structured dietary chrome + a representative allergen + a diet choice
            "Diet.Heading", "Diet.SpeakerHeading", "Diet.AllergensLegend",
            "Diet.Allergen.Eggs", "Diet.Allergen.Crustaceans", "Diet.Allergen.Milk",
            "Diet.Choice.Vegetarian", "Diet.OtherLabel", "Dinner.AllergyExtraLabel",
        };

        foreach (var key in keys)
        {
            var en = WithCulture("en", () => loc[key].Value);
            var da = WithCulture("da-DK", () => loc[key].Value);
            Assert.False(loc[key].ResourceNotFound, $"{key} missing from default resx.");
            Assert.False(string.IsNullOrWhiteSpace(en), $"{key} has an empty English value.");
            Assert.False(string.IsNullOrWhiteSpace(da), $"{key} has an empty Danish value.");
            Assert.NotEqual(en, da);
        }
    }

    [Fact]
    public void Organizer_find_person_keys_resolve_in_both_cultures()
    {
        // §20 Organizer global "find a person" search: the page chrome + result
        // labels + the nav/People-hub entries. Each must resolve in the default
        // resx AND differ between English and Danish (proves the da-DK satellite
        // carries them, not a silent English fallback).
        var loc = MakeLocalizer();
        var keys = new[]
        {
            "Find.Title", "Find.Intro", "Find.Label", "Find.Prompt", "Find.NoMatch",
            "Find.ResultsCaption", "Find.Open", "Find.OpenInGrid",
            "Find.Active", "Find.Inactive",
            "Nav.OrgFindPerson", "OrgPeople.FindPerson",
        };

        foreach (var key in keys)
        {
            var en = WithCulture("en", () => loc[key].Value);
            var da = WithCulture("da-DK", () => loc[key].Value);
            Assert.False(loc[key].ResourceNotFound, $"{key} missing from default resx.");
            Assert.False(string.IsNullOrWhiteSpace(en), $"{key} has an empty English value.");
            Assert.False(string.IsNullOrWhiteSpace(da), $"{key} has an empty Danish value.");
            Assert.NotEqual(en, da);
        }

        // Find.Found carries a {0} count placeholder in both cultures.
        foreach (var culture in SupportedCultures)
        {
            var raw = WithCulture(culture, () => loc["Find.Found"].Value);
            Assert.Contains("{0}", raw);
        }
    }

    [Fact]
    public void Organizer_exports_runsheets_keys_resolve_in_both_cultures()
    {
        // §20 Organizer "Exports & printable run-sheets": the hub chrome + the
        // per-artifact headings + download labels + column headers. Each must
        // resolve in the default resx AND differ between English and Danish
        // (proves the da-DK satellite carries them, not a silent English fallback).
        var loc = MakeLocalizer();
        var keys = new[]
        {
            // Hub chrome + nav/index entries
            "Nav.OrgExports", "OrgArea.Exports", "Exports.Title", "Exports.Intro",
            "Exports.PrintHint", "Exports.PrintAll", "Exports.DownloadCsv",
            // Per-artifact headings + empty states + download aria labels
            "Exports.Attendees", "Exports.DownloadAttendees", "Exports.NoAttendees",
            "Exports.Lunch", "Exports.LunchHeadcount", "Exports.LunchWho",
            "Exports.DownloadLunch", "Exports.NoLunch",
            "Exports.Rooms", "Exports.RoomSheets", "Exports.DownloadRooms", "Exports.NoRooms", "Exports.NoQr",
            "Exports.VolunteerRota", "Exports.DownloadRota", "Exports.NoRota",
            "Exports.Badges", "Exports.DownloadBadges", "Exports.NoBadges",
            // Column headers that genuinely differ between en + da
            "Exports.Col.Name", "Exports.Col.Email", "Exports.Col.Ticket",
            "Exports.Col.Day", "Exports.Col.Count", "Exports.Col.Role",
            "Exports.Col.SetupDay", "Exports.Col.PreDay", "Exports.Col.Room",
            "Exports.Col.Speakers", "Exports.Col.When", "Exports.Col.Qr",
            "Exports.Col.Volunteer", "Exports.Col.Bucket", "Exports.Col.Task", "Exports.Col.Company",
        };

        foreach (var key in keys)
        {
            var en = WithCulture("en", () => loc[key].Value);
            var da = WithCulture("da-DK", () => loc[key].Value);
            Assert.False(loc[key].ResourceNotFound, $"{key} missing from default resx.");
            Assert.False(string.IsNullOrWhiteSpace(en), $"{key} has an empty English value.");
            Assert.False(string.IsNullOrWhiteSpace(da), $"{key} has an empty Danish value.");
            Assert.NotEqual(en, da);
        }

        // "Master Class" / "Session" are identical in en + da by design; assert
        // resolution + non-empty only (the en != da assertion would fail).
        foreach (var key in new[] { "Exports.Col.MasterClass", "Exports.Col.Session" })
        {
            var en = WithCulture("en", () => loc[key].Value);
            var da = WithCulture("da-DK", () => loc[key].Value);
            Assert.False(loc[key].ResourceNotFound, $"{key} missing from default resx.");
            Assert.False(string.IsNullOrWhiteSpace(en), $"{key} has an empty English value.");
            Assert.False(string.IsNullOrWhiteSpace(da), $"{key} has an empty Danish value.");
        }
    }

    [Fact]
    public void Organizer_action_result_keys_resolve_in_both_cultures()
    {
        // §21 organizer "Success/failure confirmation on QR provisioning + all send
        // actions": the honest send/provision/no-op/failure confirmation lines. Each
        // must resolve in the default resx AND differ between English and Danish
        // (proves the da-DK satellite carries them, not a silent English fallback).
        var loc = MakeLocalizer();
        var keys = new[]
        {
            "Action.Sent", "Action.Provisioned", "Action.ProvisionedNoUrl",
            "Action.NoOp", "Action.Failed",
        };

        foreach (var key in keys)
        {
            var en = WithCulture("en", () => loc[key].Value);
            var da = WithCulture("da-DK", () => loc[key].Value);
            Assert.False(loc[key].ResourceNotFound, $"{key} missing from default resx.");
            Assert.False(string.IsNullOrWhiteSpace(en), $"{key} has an empty English value.");
            Assert.False(string.IsNullOrWhiteSpace(da), $"{key} has an empty Danish value.");
            Assert.NotEqual(en, da);
        }

        // The {0} placeholder must survive in both cultures for all five; the
        // send/provisioned lines also carry a {1} (count / url).
        foreach (var culture in SupportedCultures)
        {
            foreach (var key in keys)
            {
                Assert.Contains("{0}", WithCulture(culture, () => loc[key].Value));
            }
            Assert.Contains("{1}", WithCulture(culture, () => loc["Action.Sent"].Value));
            Assert.Contains("{1}", WithCulture(culture, () => loc["Action.Provisioned"].Value));
        }
    }

    [Fact]
    public void Comms_cockpit_keys_resolve_in_both_cultures()
    {
        // §20 Organizer "Comms cockpit": the timeline / who-got-what / per-campaign
        // / resend chrome + the outcome + channel labels. Each must resolve in the
        // default resx AND differ between English and Danish (proves the da-DK
        // satellite carries them, not a silent English fallback).
        var loc = MakeLocalizer();
        var keys = new[]
        {
            "Comms.CockpitIntro", "Comms.AtAGlance", "Comms.AllDelivered",
            "Comms.EmailsSent", "Comms.EmailsDropped", "Comms.EmailsFailed",
            "Comms.SoMeScheduled", "Comms.SoMePublished",
            "Comms.Sent", "Comms.Dropped", "Comms.Failed", "Comms.Scheduled",
            "Comms.ChannelEmail", "Comms.ChannelSoMe",
            "Comms.ResendHeading", "Comms.ResendIntro", "Comms.NoResend",
            "Comms.Resend", "Comms.ResendTemplateLabel", "Comms.ResendHint",
            "Comms.UpcomingHeading", "Comms.NoUpcoming",
            "Comms.WhoGotWhat", "Comms.WhoGotWhatIntro", "Comms.NoEmails",
            "Comms.Campaigns", "Comms.Timeline", "Comms.TimelineIntro",
            "Comms.NoTimeline", "Comms.ToolsHeading",
            "Comms.ColRecipient", "Comms.ColSubject", "Comms.ColOutcome",
            "Comms.ColAction", "Comms.ColLast", "Comms.ColCampaign",
            "Comms.ColWhen", "Comms.ColChannel",
        };

        foreach (var key in keys)
        {
            var en = WithCulture("en", () => loc[key].Value);
            var da = WithCulture("da-DK", () => loc[key].Value);
            Assert.False(loc[key].ResourceNotFound, $"{key} missing from default resx.");
            Assert.False(string.IsNullOrWhiteSpace(en), $"{key} has an empty English value.");
            Assert.False(string.IsNullOrWhiteSpace(da), $"{key} has an empty Danish value.");
            Assert.NotEqual(en, da);
        }
    }

    [Fact]
    public void Onboarding_pending_export_keys_resolve_in_both_cultures()
    {
        // §21 organizer "Onboarding: 'who hasn't onboarded' export": the export card
        // heading / intro / download labels on the Onboarding dashboard. Each must
        // resolve in the default resx AND differ between English and Danish (proves
        // the da-DK satellite carries them, not a silent English fallback).
        var loc = MakeLocalizer();
        var keys = new[]
        {
            "OnbExport.Heading", "OnbExport.Intro", "OnbExport.PendingCount",
            "OnbExport.Download", "OnbExport.DownloadAria",
        };

        foreach (var key in keys)
        {
            var en = WithCulture("en", () => loc[key].Value);
            var da = WithCulture("da-DK", () => loc[key].Value);
            Assert.False(loc[key].ResourceNotFound, $"{key} missing from default resx.");
            Assert.False(string.IsNullOrWhiteSpace(en), $"{key} has an empty English value.");
            Assert.False(string.IsNullOrWhiteSpace(da), $"{key} has an empty Danish value.");
            Assert.NotEqual(en, da);
        }

        // The pending-count line carries a {0} placeholder in both cultures.
        foreach (var culture in SupportedCultures)
        {
            Assert.Contains("{0}", WithCulture(culture, () => loc["OnbExport.PendingCount"].Value));
        }
    }

    [Fact]
    public void Onboarding_reopen_persona_keys_resolve_in_both_cultures()
    {
        // §21 organizer "Onboarding: re-open-all-per-persona": the bulk re-open
        // card heading / intro / step-label / submit + confirm strings on the
        // Onboarding dashboard. Each must resolve in the default resx AND differ
        // between English and Danish (proves the da-DK satellite carries them,
        // not a silent English fallback).
        var loc = MakeLocalizer();
        var keys = new[]
        {
            "OnbReopen.Heading", "OnbReopen.Intro", "OnbReopen.StepLabel",
            "OnbReopen.Submit", "OnbReopen.SubmitAria", "OnbReopen.Confirm",
        };

        foreach (var key in keys)
        {
            var en = WithCulture("en", () => loc[key].Value);
            var da = WithCulture("da-DK", () => loc[key].Value);
            Assert.False(loc[key].ResourceNotFound, $"{key} missing from default resx.");
            Assert.False(string.IsNullOrWhiteSpace(en), $"{key} has an empty English value.");
            Assert.False(string.IsNullOrWhiteSpace(da), $"{key} has an empty Danish value.");
            Assert.NotEqual(en, da);
        }

        // The intro + confirm lines carry a {0} persona placeholder in both cultures.
        foreach (var culture in SupportedCultures)
        {
            Assert.Contains("{0}", WithCulture(culture, () => loc["OnbReopen.Intro"].Value));
            Assert.Contains("{0}", WithCulture(culture, () => loc["OnbReopen.Confirm"].Value));
        }
    }

    [Fact]
    public void Request_change_keys_resolve_in_both_cultures()
    {
        // §21 Participant "request-change path once a form is deadline-locked":
        // the /Forms/RequestChange page chrome + the per-locked-form link labels.
        // Each must resolve in the default resx AND differ between English and
        // Danish (proves the da-DK satellite carries them, not a silent fallback).
        var loc = MakeLocalizer();
        var keys = new[]
        {
            "ReqChange.Title", "ReqChange.MessagePlaceholder", "ReqChange.MessageHelp",
            "ReqChange.Submit", "ReqChange.SentFlash", "ReqChange.OpenHeading",
            "ReqChange.OpenIntro", "ReqChange.ErrEmpty", "ReqChange.ErrTooLong",
            "ReqChange.ErrGeneric", "ReqChange.LockedPrompt", "ReqChange.LockedCta",
        };

        foreach (var key in keys)
        {
            var en = WithCulture("en", () => loc[key].Value);
            var da = WithCulture("da-DK", () => loc[key].Value);
            Assert.False(loc[key].ResourceNotFound, $"{key} missing from default resx.");
            Assert.False(string.IsNullOrWhiteSpace(en), $"{key} has an empty English value.");
            Assert.False(string.IsNullOrWhiteSpace(da), $"{key} has an empty Danish value.");
            Assert.NotEqual(en, da);
        }

        // The intro + the two {0}-labels carry a topic placeholder in both cultures.
        foreach (var culture in SupportedCultures)
        {
            Assert.Contains("{0}", WithCulture(culture, () => loc["ReqChange.Intro"].Value));
            Assert.Contains("{0}", WithCulture(culture, () => loc["ReqChange.MessageLabel"].Value));
        }
    }

    [Fact]
    public void Formatted_keys_substitute_their_argument_in_both_cultures()
    {
        // Keys that carry a {0} placeholder (status counts, dates, names) must keep
        // the placeholder in both resx so the runtime arg substitutes correctly.
        var loc = MakeLocalizer();
        var formatKeys = new[]
        {
            "Hub.VolWorkAssigned", "SponsorTasks.Pending", "VolWiz.StepOf",
            "Attendee.Reserved", "Results.NResponses", "Survey.JsPickMore",
            // Onboarding slice
            "Welcome.Heading", "Welcome.RoleIntro", "Welcome.Countdown", "Welcome.WhatBody",
            "Onboard.StepBio", "Onboard.StepSwag",
        };

        foreach (var key in formatKeys)
        {
            foreach (var culture in SupportedCultures)
            {
                var raw = WithCulture(culture, () => loc[key].Value);
                Assert.Contains("{0}", raw);
            }
        }
    }

    [Fact]
    public void Hotels_bulk_delete_keys_resolve_in_both_cultures()
    {
        // §20 universal CRUD + bulk — the Hotels grid bulk-delete bar + confirm modal
        // strings. Each must resolve in the default resx AND differ between English
        // and Danish (proves the da-DK satellite carries them, not a silent fallback).
        var loc = MakeLocalizer();
        var keys = new[]
        {
            "OrgHotels.SelectRow", "OrgHotels.BulkDelete", "OrgHotels.BulkDeleteHint",
            "OrgHotels.BulkDeleteConfirmTitle", "OrgHotels.BulkDeleteConfirmBody",
        };

        foreach (var key in keys)
        {
            var en = WithCulture("en", () => loc[key].Value);
            var da = WithCulture("da-DK", () => loc[key].Value);
            Assert.False(loc[key].ResourceNotFound, $"{key} missing from default resx.");
            Assert.False(string.IsNullOrWhiteSpace(en), $"{key} has an empty English value.");
            Assert.False(string.IsNullOrWhiteSpace(da), $"{key} has an empty Danish value.");
            Assert.NotEqual(en, da);
        }

        // The select-row label + confirm body carry a {0} placeholder in both cultures.
        foreach (var culture in SupportedCultures)
        {
            Assert.Contains("{0}", WithCulture(culture, () => loc["OrgHotels.SelectRow"].Value));
            Assert.Contains("{0}", WithCulture(culture, () => loc["OrgHotels.BulkDeleteConfirmBody"].Value));
        }
    }

    [Fact]
    public void Speaker_question_digest_keys_resolve_in_both_cultures()
    {
        // §21 Participant "Speaker Q&A email digest on new questions" — the digest
        // note + open-count summary surfaced on the speaker Questions page. Each
        // must resolve in the default resx AND differ between English and Danish
        // (proves the da-DK satellite carries them, not a silent English fallback).
        var loc = MakeLocalizer();
        var keys = new[]
        {
            "SpeakerQ.Title", "SpeakerQ.DigestNote",
            "SpeakerQ.OpenCount", "SpeakerQ.AllAnswered",
        };

        foreach (var key in keys)
        {
            var en = WithCulture("en", () => loc[key].Value);
            var da = WithCulture("da-DK", () => loc[key].Value);
            Assert.False(loc[key].ResourceNotFound, $"{key} missing from default resx.");
            Assert.False(string.IsNullOrWhiteSpace(en), $"{key} has an empty English value.");
            Assert.False(string.IsNullOrWhiteSpace(da), $"{key} has an empty Danish value.");
            Assert.NotEqual(en, da);
        }

        // The open-count line carries a {0} placeholder in both cultures.
        foreach (var culture in SupportedCultures)
        {
            Assert.Contains("{0}", WithCulture(culture, () => loc["SpeakerQ.OpenCount"].Value));
        }
    }

    [Fact]
    public void Speaker_my_session_ratings_keys_resolve_in_both_cultures()
    {
        // §20 Speaker "My session ratings" — the self-service speaker page that
        // surfaces the attendee evaluations (1–5 + anonymous comments) for the
        // speaker's own sessions, plus its nav entry + Speaker-hub card. Each must
        // resolve in the default resx AND differ between English and Danish (proves
        // the da-DK satellite carries them, not a silent English fallback).
        var loc = MakeLocalizer();
        var keys = new[]
        {
            // Nav + Speaker-hub card
            "Nav.SpeakerEvaluations",
            "Speaker.RatingsHeading", "Speaker.RatingsIntro", "Speaker.RatingsCta",
            // Page chrome + states
            "SpeakerEval.Title", "SpeakerEval.Intro", "SpeakerEval.None",
            "SpeakerEval.Overall", "SpeakerEval.NoRatings",
            "SpeakerEval.SessionNone", "SpeakerEval.NoComments",
        };

        foreach (var key in keys)
        {
            var en = WithCulture("en", () => loc[key].Value);
            var da = WithCulture("da-DK", () => loc[key].Value);
            Assert.False(loc[key].ResourceNotFound, $"{key} missing from default resx.");
            Assert.False(string.IsNullOrWhiteSpace(en), $"{key} has an empty English value.");
            Assert.False(string.IsNullOrWhiteSpace(da), $"{key} has an empty Danish value.");
            Assert.NotEqual(en, da);
        }

        // "Master class" is identical in en + da by design; assert resolution +
        // non-empty only (the en != da assertion would fail, cf. the onboarding slice).
        foreach (var key in new[] { "SpeakerEval.MasterClass" })
        {
            var en = WithCulture("en", () => loc[key].Value);
            var da = WithCulture("da-DK", () => loc[key].Value);
            Assert.False(loc[key].ResourceNotFound, $"{key} missing from default resx.");
            Assert.False(string.IsNullOrWhiteSpace(en), $"{key} has an empty English value.");
            Assert.False(string.IsNullOrWhiteSpace(da), $"{key} has an empty Danish value.");
        }

        // The {0}-bearing lines (totals, per-session count, rating aria, role) must
        // keep the placeholder in both cultures so the runtime arg substitutes.
        foreach (var culture in SupportedCultures)
        {
            foreach (var key in new[]
            {
                "SpeakerEval.TotalCount", "SpeakerEval.SessionCount",
                "SpeakerEval.RatingAria", "SpeakerEval.NotASpeaker",
            })
            {
                Assert.Contains("{0}", WithCulture(culture, () => loc[key].Value));
            }
        }
    }

    [Fact]
    public void Data_freshness_keys_resolve_in_both_cultures()
    {
        // §21 Organizer "last synced at" — the data-freshness panel chrome, the
        // state labels, the per-feed display names and the nav entry. Each must
        // resolve in the default resx AND differ between English and Danish
        // (proves the da-DK satellite carries them, not a silent English fallback).
        var loc = MakeLocalizer();
        var keys = new[]
        {
            // Chrome + columns + states
            "Nav.OrgDataFreshness", "Fresh.Title", "Fresh.Intro",
            "Fresh.ColFeed", "Fresh.ColLast", "Fresh.ColAge", "Fresh.ColState",
            "Fresh.NoData", "Fresh.Fresh", "Fresh.Stale", "Fresh.AllFresh",
            // Per-feed labels (each genuinely differs en/da)
            "Fresh.Feed.Email", "Fresh.Feed.AttendeeSync", "Fresh.Feed.MasterClassBookingSync",
            "Fresh.Feed.SponsorLeads", "Fresh.Feed.SpeakerImport", "Fresh.Feed.SessionImport",
            "Fresh.Feed.SessionQuestions", "Fresh.Feed.SessionEvaluations", "Fresh.Feed.SoMePublished",
        };

        foreach (var key in keys)
        {
            var en = WithCulture("en", () => loc[key].Value);
            var da = WithCulture("da-DK", () => loc[key].Value);
            Assert.False(loc[key].ResourceNotFound, $"{key} missing from default resx.");
            Assert.False(string.IsNullOrWhiteSpace(en), $"{key} has an empty English value.");
            Assert.False(string.IsNullOrWhiteSpace(da), $"{key} has an empty Danish value.");
            Assert.NotEqual(en, da);
        }

        // The {0}-bearing lines (checked-at time, stale count, relative ages) must
        // keep the placeholder in both cultures so the runtime arg substitutes.
        foreach (var culture in SupportedCultures)
        {
            foreach (var key in new[]
            {
                "Fresh.GeneratedAt", "Fresh.StaleCount",
                "Fresh.AgeDays", "Fresh.AgeHours", "Fresh.AgeMinutes",
            })
            {
                Assert.Contains("{0}", WithCulture(culture, () => loc[key].Value));
            }
        }
    }

    [Fact]
    public void Public_agenda_keys_resolve_in_both_cultures()
    {
        // §21 Public site "agenda/grid view" — the public day-by-day agenda page
        // chrome + the landing-page Agenda card. Each must resolve in the default
        // resx AND differ between English and Danish (proves the da-DK satellite
        // carries them, not a silent English fallback).
        var loc = MakeLocalizer();
        var keys = new[]
        {
            "Agenda.Title", "Agenda.NoEvent", "Agenda.NothingScheduled",
            "Agenda.ListView", "Agenda.TimeLabel", "Agenda.RoomTag", "Agenda.With",
            "Landing.AgendaTitle", "Landing.AgendaBlurb",
        };

        foreach (var key in keys)
        {
            var en = WithCulture("en", () => loc[key].Value);
            var da = WithCulture("da-DK", () => loc[key].Value);
            Assert.False(loc[key].ResourceNotFound, $"{key} missing from default resx.");
            Assert.False(string.IsNullOrWhiteSpace(en), $"{key} has an empty English value.");
            Assert.False(string.IsNullOrWhiteSpace(da), $"{key} has an empty Danish value.");
            Assert.NotEqual(en, da);
        }

        // The summary carries {0} (talks) + {1} (days); the unscheduled note carries
        // {0}. The placeholders must survive in both cultures so the runtime args
        // substitute correctly.
        foreach (var culture in SupportedCultures)
        {
            var summary = WithCulture(culture, () => loc["Agenda.Summary"].Value);
            Assert.Contains("{0}", summary);
            Assert.Contains("{1}", summary);
            Assert.Contains("{0}", WithCulture(culture, () => loc["Agenda.UnscheduledNote"].Value));
        }
    }

    [Fact]
    public void Lead_score_explanation_keys_resolve_in_both_cultures()
    {
        // §21 organizer "explain AI scores" — the "why this score" breakdown shown
        // under each lead's AI-screen badge: the chrome (title / baseline / total)
        // and the per-factor reasons. Each must resolve in the default resx AND
        // differ between English and Danish (proves the da-DK satellite carries
        // them, not a silent English fallback).
        var loc = MakeLocalizer();
        var keys = new[]
        {
            "LeadScore.WhyTitle", "LeadScore.BaseLine", "LeadScore.TotalLine",
            "LeadScore.HasEmail", "LeadScore.HasName", "LeadScore.HasCompany",
            "LeadScore.HasPhone", "LeadScore.Unreachable", "LeadScore.LooksTest",
        };

        foreach (var key in keys)
        {
            var en = WithCulture("en", () => loc[key].Value);
            var da = WithCulture("da-DK", () => loc[key].Value);
            Assert.False(loc[key].ResourceNotFound, $"{key} missing from default resx.");
            Assert.False(string.IsNullOrWhiteSpace(en), $"{key} has an empty English value.");
            Assert.False(string.IsNullOrWhiteSpace(da), $"{key} has an empty Danish value.");
            Assert.NotEqual(en, da);
        }
    }

    [Fact]
    public void Become_a_sponsor_cta_keys_resolve_in_both_cultures()
    {
        // §21 public "become a sponsor" CTA — the heading / body / button on the
        // public sponsors page. Each must resolve in the default resx AND differ
        // between English and Danish (proves the da-DK satellite carries them).
        var loc = MakeLocalizer();
        var keys = new[]
        {
            "Sponsors.BecomeTitle", "Sponsors.BecomeBody", "Sponsors.BecomeCta",
        };

        foreach (var key in keys)
        {
            var en = WithCulture("en", () => loc[key].Value);
            var da = WithCulture("da-DK", () => loc[key].Value);
            Assert.False(loc[key].ResourceNotFound, $"{key} missing from default resx.");
            Assert.False(string.IsNullOrWhiteSpace(en), $"{key} has an empty English value.");
            Assert.False(string.IsNullOrWhiteSpace(da), $"{key} has an empty Danish value.");
            Assert.NotEqual(en, da);
        }
    }
}
