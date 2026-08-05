using CommunityHub.Core.Integrations.DocLibrary;

namespace CommunityHub.Web.Tests;

/// <summary>
/// A configured <see cref="IDocLibraryPathResolver"/> for the web tests.
/// </summary>
/// <remarks>
/// Mirrors the Core test helper. Deliberately duplicated rather than shared: the two test projects
/// do not reference each other, and a test-only helper is not worth a third assembly.
/// </remarks>
public static class TestDocLibrary
{
    public const string Root = "General/TEST/EventHub";

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
