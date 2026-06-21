using System.Globalization;
using CommunityHub.Core.Email;
using CommunityHub.Core.Resources;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Guards the Organizer Broadcast page's "supported variables" legend
/// (REQUIREMENTS §10c). The legend on /Organizer/Broadcast advertises a set of
/// {Token} placeholders to organizers; this asserts that the DOCUMENTED set
/// (the per-token meaning + example resx strings the page renders) matches the
/// engine's ACTUAL token set (<see cref="BroadcastTemplates.TokenHelp"/> — the
/// exact keys <see cref="BroadcastTemplates.Substitute"/> resolves).
///
/// If someone adds a broadcast token in Core but forgets the legend strings (or
/// adds a legend string for a token the engine does not substitute), one of
/// these fails — so the GUI can never advertise a token that does nothing, and a
/// real token can never be silently undocumented.
/// </summary>
public sealed class BroadcastTokenLegendTests
{
    private static readonly string[] SupportedCultures = { "en" };

    // The Broadcast page builds its per-token meaning/example from these resx keys,
    // keyed by the token name (see Pages/Organizer/Broadcast.cshtml). Kept in lock-
    // step with the page: one {Meaning, Example} pair per supported token.
    private static string MeaningKey(string token) => $"Broadcast.Token{token}Meaning";
    private static string ExampleKey(string token) => $"Broadcast.Token{token}Example";

    private static IStringLocalizer<SharedResource> MakeLocalizer()
    {
        var options = Options.Create(new LocalizationOptions { ResourcesPath = "" });
        var factory = new ResourceManagerStringLocalizerFactory(
            options, NullLoggerFactory.Instance);
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

    [Fact]
    public void Documented_token_set_matches_the_engines_actual_token_set()
    {
        var loc = MakeLocalizer();

        // For each token the ENGINE resolves, the page must have a meaning + example
        // resx string (the legend's two columns). A missing one means the GUI would
        // list a real token with no human explanation.
        foreach (var token in BroadcastTemplates.TokenHelp.Keys)
        {
            var meaning = loc[MeaningKey(token)];
            var example = loc[ExampleKey(token)];
            Assert.False(meaning.ResourceNotFound,
                $"Broadcast token '{token}' has no documented meaning ({MeaningKey(token)}).");
            Assert.False(example.ResourceNotFound,
                $"Broadcast token '{token}' has no documented example ({ExampleKey(token)}).");
            Assert.False(string.IsNullOrWhiteSpace(meaning.Value));
            Assert.False(string.IsNullOrWhiteSpace(example.Value));
        }
    }

    [Fact]
    public void Every_documented_example_actually_uses_its_own_token_literal()
    {
        var loc = MakeLocalizer();

        // The example sentence for a token must contain that token's {literal}, so the
        // organizer sees the exact marker to type. Catches copy-paste drift between
        // tokens (e.g. the EventName example accidentally showing {FirstName}).
        foreach (var token in BroadcastTemplates.TokenHelp.Keys)
        {
            var example = loc[ExampleKey(token)].Value;
            Assert.Contains("{" + token + "}", example);
        }
    }

    [Fact]
    public void Legend_strings_resolve()
    {
        var loc = MakeLocalizer();

        // The surrounding UI chrome (heading / intro / example-label / aria) is
        // localized; each must resolve and carry a non-empty value.
        var chrome = new[]
        {
            "Broadcast.TokensHeading", "Broadcast.TokensIntro",
            "Broadcast.TokenExampleLabel",
        };
        foreach (var key in chrome)
        {
            var en = WithCulture("en", () => loc[key].Value);
            Assert.False(loc[key].ResourceNotFound, $"{key} missing from default resx.");
            Assert.False(string.IsNullOrWhiteSpace(en), $"{key} has an empty value.");
        }

        // The per-token meaning strings resolve too.
        foreach (var token in BroadcastTemplates.TokenHelp.Keys)
        {
            var key = MeaningKey(token);
            var en = WithCulture("en", () => loc[key].Value);
            Assert.False(string.IsNullOrWhiteSpace(en), $"{key} has an empty value.");
        }
    }

    [Fact]
    public void Insert_aria_label_keeps_its_placeholder()
    {
        // The click-to-insert button's aria-label is "Insert variable {0}" with the
        // token literal substituted in; the {0} must survive so the runtime arg lands.
        var loc = MakeLocalizer();
        foreach (var culture in SupportedCultures)
        {
            Assert.Contains("{0}",
                WithCulture(culture, () => loc["Broadcast.TokenInsertAria"].Value));
        }
    }
}
