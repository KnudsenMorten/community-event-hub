using System.Text.RegularExpressions;
using CommunityHub.Core.Content;
using CommunityHub.Core.Domain;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §680 — the build-failing gate over the REAL shipped welcome copy.
/// </summary>
/// <remarks>
/// <para>🔒 <b>The runtime is deliberately fail-soft, so this is where an authoring or packaging
/// mistake has to die.</b> <see cref="WelcomeCopyStore"/> answers "no copy" for a missing file and
/// the wizard simply omits the step — correct at runtime (one content file must never take Get
/// Started down for a whole role) and exactly how a mistake becomes INVISIBLE: the feature is just
/// absent, which looks like it was never built. The same bargain <c>TaskBodyCatalogTests</c>
/// strikes: harmless at runtime, loud at build time.</para>
/// </remarks>
public class WelcomeCopyCatalogTests
{
    private static readonly WelcomeCopyStore Copy = new();

    /// <summary>
    /// The tokens <c>WelcomeFormService</c> actually supplies. A file naming anything else would
    /// render as NOTHING — silently, mid-sentence — which is the §688 failure mode.
    /// </summary>
    private static readonly string[] SuppliedTokens =
        { "firstName", "eventDisplayName", "eventCodeParens", "eventCode" };

    /// <summary>The six roles §680 names.</summary>
    public static TheoryData<ParticipantRole> WelcomedRoles() => new()
    {
        ParticipantRole.Speaker,
        ParticipantRole.Sponsor,
        ParticipantRole.Volunteer,
        ParticipantRole.Media,
        ParticipantRole.EventPartner,
        ParticipantRole.Attendee,
    };

    [Theory]
    [MemberData(nameof(WelcomedRoles))]
    public void Every_welcomed_role_has_real_copy_on_disk(ParticipantRole role)
    {
        Assert.NotNull(WelcomeCopyStore.SlugFor(role));
        Assert.True(
            Copy.Exists(role),
            $"No welcome copy for {role}. Expected a non-empty file at {Copy.ResolvePath(role)} — "
            + "without it the wizard silently omits the welcome step for this role.");
    }

    [Fact]
    public void Organizer_has_no_welcome_and_is_not_an_error()
    {
        // Staff get no welcome MAIL either (WelcomeVariants.TemplateKeyFor returns null), so they
        // get no welcome STEP. Absence here is the design, not a missing file.
        Assert.Null(WelcomeCopyStore.SlugFor(ParticipantRole.Organizer));
        Assert.False(Copy.Exists(ParticipantRole.Organizer));
        Assert.Equal(string.Empty, Copy.LoadRaw(ParticipantRole.Organizer));
    }

    [Theory]
    [MemberData(nameof(WelcomedRoles))]
    public void Every_token_used_is_one_the_step_actually_supplies(ParticipantRole role)
    {
        var used = Regex.Matches(Copy.LoadRaw(role), @"\{\{(\w+)\}\}")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        var unknown = used.Except(SuppliedTokens, StringComparer.Ordinal).ToList();
        Assert.True(
            unknown.Count == 0,
            $"{role}'s welcome copy uses token(s) nobody supplies: {string.Join(", ", unknown)}. "
            + "An unmapped token renders as NOTHING, mid-sentence, on a participant-facing page.");
    }

    [Theory]
    [MemberData(nameof(WelcomedRoles))]
    public void The_two_things_680_removes_are_absent(ParticipantRole role)
    {
        // 🔒 §680, verbatim: "except there is no link to get started (button) or support /
        // questions". §680.2's tell was that nothing had been STRIPPED because nothing had been
        // derived — so assert on the strip, not on the prose.
        var raw = Copy.LoadRaw(role);

        Assert.DoesNotContain("{{hubUrl}}", raw, StringComparison.Ordinal);       // the Get-Started CTA
        Assert.DoesNotContain("{{supportEmail}}", raw, StringComparison.Ordinal); // the support block
        // The hard-coded hub address the mails carry. Someone already inside the wizard does not
        // need a link to the thing they are looking at.
        Assert.DoesNotContain("hub.expertslive.dk", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Substitute_resolves_known_tokens_and_erases_unknown_ones()
    {
        var result = WelcomeCopyStore.Substitute(
            "Hi {{firstName}}, welcome to {{eventDisplayName}}{{eventCodeParens}}.{{mystery}}",
            new Dictionary<string, string>
            {
                ["firstName"] = "Mo",
                ["eventDisplayName"] = "Experts Live Denmark 2027",
                ["eventCodeParens"] = " (ELDK27)",
            });

        Assert.Equal("Hi Mo, welcome to Experts Live Denmark 2027 (ELDK27).", result);
        // Never literal braces on a participant-facing page.
        Assert.DoesNotContain("{{", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Substitute_encodes_the_value_at_the_seam()
    {
        // The copy files are trusted, in-repo markdown and Markdig passes raw HTML through by
        // design — but {{firstName}} is IMPORTED participant data, not copy. Encoding here is what
        // keeps a name from becoming markup.
        var result = WelcomeCopyStore.Substitute(
            "Hi {{firstName}}.",
            new Dictionary<string, string> { ["firstName"] = "<script>alert(1)</script>" });

        Assert.DoesNotContain("<script>", result, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Substitute_leaves_a_source_with_no_tokens_untouched()
    {
        const string source = "Nothing to replace here.";
        Assert.Same(source, WelcomeCopyStore.Substitute(source, new Dictionary<string, string>()));
    }
}
