using CommunityHub.Core.Integrations.DocLibrary;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §768 Phase 2 — the one place allowed to build a document-library path.
/// </summary>
/// <remarks>
/// 🔑 <b>What is worth pinning.</b> Every failure this registry exists to prevent is SILENT: a path
/// that escapes its root, a key that resolves to nothing, an unregistered folder invented at a call
/// site. None of them throw at runtime — they produce an empty listing that every caller reads as
/// "nothing to do". So the contract is asserted here, where a regression is loud.
/// </remarks>
public sealed class DocLibraryPathResolverTests
{
    private const string ProdRoot = "General/Events/ELDK 2027/EventHub";
    private const string DevRoot = "General/DEVELOPMENT/EventHub";

    private static DocLibraryPathResolver Resolver(
        string root = ProdRoot, bool enabled = true,
        Dictionary<string, string>? paths = null,
        Dictionary<string, string>? fileNames = null,
        string siteUrl = "https://example.test/sites/x")
    {
        var options = new DocLibraryOptions
        {
            Enabled = enabled,
            SiteUrl = siteUrl,
            DriveName = "Documents",
            RootFolderPath = root,
            Paths = paths ?? new(StringComparer.OrdinalIgnoreCase),
            FileNames = fileNames ?? new(StringComparer.OrdinalIgnoreCase),
        };
        return new DocLibraryPathResolver(new StaticMonitor(options));
    }

    // ---- the root is the only difference between environments ---------------------------

    /// <summary>
    /// 🔒 The property the whole design rests on: PROD and DEV differ in the root and NOTHING else.
    /// Operator: "same folder names as prod - just under the dev root".
    /// </summary>
    [Fact]
    public void The_same_key_differs_between_environments_only_by_its_root()
    {
        var prod = Resolver(ProdRoot).Resolve(DocLibraryPaths.SpeakerPhotos);
        var dev = Resolver(DevRoot).Resolve(DocLibraryPaths.SpeakerPhotos);

        Assert.Equal($"{ProdRoot}/Speakers/Photos", prod);
        Assert.Equal($"{DevRoot}/Speakers/Photos", dev);

        // The tail is identical — only the root moved.
        Assert.Equal(prod[ProdRoot.Length..], dev[DevRoot.Length..]);
    }

    [Fact]
    public void Every_registered_active_path_resolves_under_the_root()
    {
        var resolver = Resolver();
        foreach (var d in DocLibraryPaths.Required)
        {
            var path = resolver.Resolve(d.Key);
            Assert.StartsWith(ProdRoot + "/", path);
            Assert.DoesNotContain("//", path);
            Assert.False(path.EndsWith('/'));
        }
    }

    // ---- refusing to guess --------------------------------------------------------------

    [Fact]
    public void An_unregistered_key_throws_and_names_itself()
    {
        var ex = Assert.Throws<DocLibraryPathException>(
            () => Resolver().Resolve("SomeFolderSomeoneInvented"));

        Assert.Equal("SomeFolderSomeoneInvented", ex.Key);
        Assert.Contains("not a registered path", ex.Message);
    }

    /// <summary>
    /// ⚠️ An empty root must NOT quietly resolve to the drive root — that would silently point every
    /// sweep at the top of the library.
    /// </summary>
    [Fact]
    public void An_unset_root_throws_rather_than_resolving_to_the_drive_root()
    {
        var ex = Assert.Throws<DocLibraryPathException>(
            () => Resolver(root: "").Resolve(DocLibraryPaths.SpeakerPhotos));

        Assert.Contains("RootFolderPath", ex.Message);
    }

    // ---- normalisation ------------------------------------------------------------------

    [Theory]
    [InlineData("/General/Events/X/")]          // leading + trailing slash
    [InlineData("General//Events//X")]          // doubled separators
    [InlineData("  General/Events/X  ")]        // surrounding whitespace
    [InlineData(@"General\Events\X")]           // backslashes
    public void Root_shapes_that_would_silently_escape_or_mis_resolve_are_normalised(string root)
    {
        var path = Resolver(root).Resolve(DocLibraryPaths.SpeakerPhotos);
        Assert.Equal("General/Events/X/Speakers/Photos", path);
    }

    [Fact]
    public void Spaces_are_preserved_because_encoding_belongs_to_the_transport()
    {
        // Both real roots contain spaces; percent-encoding here would double-encode downstream.
        var path = Resolver().Resolve(DocLibraryPaths.VenueGoodToKnow);
        Assert.Equal($"{ProdRoot}/Venue/Good to know", path);
        Assert.DoesNotContain("%20", path);
    }

    // ---- overrides ----------------------------------------------------------------------

    /// <summary>
    /// 🔒 §769.1 D1 — a CHANGED CONTRACT. This test failing on the change is what proved it landed.
    /// </summary>
    /// <remarks>
    /// <para>A <c>DocLibrary:Paths:*</c> app setting used to override the registered default. It no
    /// longer does anything at all: per-key paths are the operator's data now, edited on the
    /// organizer paths page and stored in the database, and the app-setting layer is RETIRED
    /// (operator 2026-08-02 — one override layer, so a folder can never be defined twice again).</para>
    ///
    /// <para>⚠️ It is not silently ignored either: <see cref="IDocLibraryPathResolver.Validate"/>
    /// names every surviving key, because a path that reads as authoritative in the Azure portal
    /// while the product uses a different one is the two-switch trap — which is how the §768 drift
    /// began. The layer that DOES override now is pinned in <c>DocLibraryPathSettingsTests</c>.</para>
    /// </remarks>
    [Fact]
    public void A_DocLibrary_Paths_app_setting_no_longer_overrides_anything()
    {
        var resolver = Resolver(paths: new(StringComparer.OrdinalIgnoreCase)
        {
            [DocLibraryPaths.SpeakerPhotos] = "Speakers/Portraits",
        });

        Assert.Equal($"{ProdRoot}/Speakers/Photos", resolver.Resolve(DocLibraryPaths.SpeakerPhotos));

        // "Overridden" now means "an operator edited this on the page" — the only version of the
        // question the page can usefully answer.
        Assert.All(resolver.All(), r => Assert.False(r.IsOverridden));
    }

    // ---- validation ---------------------------------------------------------------------

    [Fact]
    public void A_sound_configuration_reports_no_problems()
    {
        Assert.Empty(Resolver().Validate());
    }

    /// <summary>An unset root is ONE problem, not one per key — a wall of errors hides the cause.</summary>
    [Fact]
    public void An_unset_root_is_reported_once_at_the_root()
    {
        var problems = Resolver(root: "").Validate();
        Assert.Single(problems);
        Assert.Contains("RootFolderPath", problems[0]);
    }

    /// <summary>
    /// §769.1 D1 — a surviving path app setting is reported whether or not the key even exists. It
    /// has no effect now, so the fix is always the same: delete it and set the path on the page.
    /// </summary>
    [Fact]
    public void A_leftover_path_app_setting_is_reported_not_ignored()
    {
        var problems = Resolver(paths: new(StringComparer.OrdinalIgnoreCase)
        {
            ["SpeakrPhotos"] = "Speakers/Photos",   // typo — and now doubly pointless
        }).Validate();

        Assert.Contains(problems, p => p.Contains("SpeakrPhotos") && p.Contains("NO effect"));
    }

    /// <summary>
    /// The traversal and absolute-URL rules moved WITH the values they guard: they used to live in
    /// <c>Validate()</c> over app settings, and now live in <see cref="DocLibraryValueValidator"/>,
    /// which runs at the SAVE — with a human present to read the message, instead of in a startup
    /// log nobody was watching.
    /// </summary>
    [Theory]
    [InlineData("../../etc")]
    [InlineData("https://contoso.sharepoint.com/sites/x/Shared Documents/Speakers")]
    public void Traversal_and_absolute_urls_are_rejected(string value)
    {
        Assert.NotNull(DocLibraryValueValidator.ValidatePath(value));
    }

    /// <summary>Deliberately disabled is a legitimate state, not a misconfiguration.</summary>
    [Fact]
    public void A_disabled_library_reports_no_problems_and_is_not_configured()
    {
        var resolver = Resolver(enabled: false);
        Assert.Empty(resolver.Validate());
        Assert.False(resolver.IsConfigured);
    }

    // ---- static file names --------------------------------------------------------------

    [Fact]
    public void Static_file_names_are_settings_not_literals()
    {
        var resolver = Resolver(fileNames: new(StringComparer.OrdinalIgnoreCase)
        {
            ["EventGraphicsTemplateBackground"] = "Template.JPG",
        });

        Assert.True(resolver.TryResolveFileName("EventGraphicsTemplateBackground", out var name));
        Assert.Equal("Template.JPG", name);
        Assert.False(resolver.TryResolveFileName("NotConfigured", out _));
    }

    // ---- the registry itself ------------------------------------------------------------

    [Fact]
    public void Registry_keys_are_unique_and_every_default_is_relative()
    {
        var keys = DocLibraryPaths.All.Select(d => d.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (var d in DocLibraryPaths.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(d.DefaultRelativePath), d.Key);
            Assert.False(d.DefaultRelativePath.StartsWith('/'), d.Key);
            Assert.DoesNotContain("..", d.DefaultRelativePath);
            // 🔒 No default may carry its own root — that is what makes a path environment-portable.
            Assert.DoesNotContain("General/", d.DefaultRelativePath);
        }
    }

    /// <summary>
    /// 🔒 The regression guard the work order calls "the only thing preventing the same drift
    /// recurring" — seeded with the folders that were renamed or deleted in the reorganisation.
    /// </summary>
    [Theory]
    [InlineData("Grahics")]                    // the original misspelling, renamed in the library
    [InlineData("ELDK/Graphics")]              // deleted
    [InlineData("ELDK/LogoPack")]              // deleted
    [InlineData("Logo-SoMeBranding")]          // now Logo/Web
    [InlineData("Logo-Zoho")]                  // merged into Logo/Web
    [InlineData("Logo-Print")]                 // now Logo/Print
    [InlineData("Booth Materials Zoho")]       // now Booth Collateral
    [InlineData("SessionEvalsQR")]             // now SessionEvaluations/QR
    [InlineData("SessionEvaluationsFinal")]    // now SessionEvaluations/Result
    public void No_registered_default_may_reintroduce_a_retired_folder(string retired)
    {
        foreach (var d in DocLibraryPaths.All)
            Assert.DoesNotContain(retired, d.DefaultRelativePath, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class StaticMonitor : IOptionsMonitor<DocLibraryOptions>
    {
        public StaticMonitor(DocLibraryOptions value) => CurrentValue = value;
        public DocLibraryOptions CurrentValue { get; }
        public DocLibraryOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<DocLibraryOptions, string?> listener) => null;
    }
}
