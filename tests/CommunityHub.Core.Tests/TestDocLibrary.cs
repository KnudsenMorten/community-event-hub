using CommunityHub.Core.Integrations.DocLibrary;

namespace CommunityHub.Core.Tests;

/// <summary>
/// A configured <see cref="IDocLibraryPathResolver"/> for tests.
/// </summary>
/// <remarks>
/// 🔑 Uses a root that is obviously NEITHER environment. If a test ever asserted against a real
/// root, a copy-paste into production configuration would look plausible; <c>General/TEST/EventHub</c>
/// cannot be mistaken for the live tree or the DEV one.
/// </remarks>
public static class TestDocLibrary
{
    public const string Root = "General/TEST/EventHub";

    /// <summary>The full path a registered key resolves to under the test root.</summary>
    public static string PathFor(string key) => Resolver().Resolve(key);

    public static DocLibraryPathResolver Resolver(string root = Root) =>
        new(new DocLibraryOptions
        {
            Enabled = true,
            SiteUrl = "https://contoso.sharepoint.example.test/sites/eldk",
            DriveName = "Documents",
            RootFolderPath = root,
        });
}
