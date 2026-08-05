using System.Reflection;
using CommunityHub.Core.Integrations.DocLibrary;
using CommunityHub.Core.Integrations.Graphics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §768.14 — the services that take <see cref="IDocLibraryPathResolver"/> as an OPTIONAL constructor
/// parameter really do receive it from the container.
/// </summary>
/// <remarks>
/// <para>🔒 <b>Why this is worth a test.</b> Several photo/upload services take their dependencies as
/// optional (<c>= null</c>) so tests and unconfigured hosts can construct them. That is a reasonable
/// pattern with one nasty edge: if the resolver is ever un-registered, renamed, or registered only
/// in one of the two hosts, the container silently picks the parameterless default and the service
/// comes up with <c>_paths == null</c>.</para>
///
/// <para>⚠️ Nothing would throw. <c>SpeakerPhotoService</c> would return null for every photo and
/// <c>SpeakerPhotoArchiveService</c> would report itself "inactive — no speaker-photo folder
/// configured", both of which read as ordinary quiet states. That is the same shape as the §768
/// finding that a missing folder returns an EMPTY LISTING and every caller treats empty as "nothing
/// to do" — the failure this whole audit exists to remove. A compiler cannot catch it because
/// <c>null</c> is a legal value for the parameter.</para>
/// </remarks>
public class DocLibraryResolverIsInjectedTests
{
    private static ServiceProvider BuildContainer()
    {
        var services = new ServiceCollection();
        services.AddMemoryCache();
        services.AddLogging();

        // The registration both hosts make (Program.cs in CommunityHub and CommunityHub.Jobs).
        services.AddSingleton<IDocLibraryPathResolver>(
            new DocLibraryPathResolver(new DocLibraryOptions
            {
                Enabled = true,
                SiteUrl = "https://sp.test/sites/x",
                RootFolderPath = "General/Test/EventHub",
            }));

        services.AddScoped<SpeakerPhotoService>();
        return services.BuildServiceProvider();
    }

    private static object? PrivateField(object instance, string name) =>
        instance.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(instance);

    [Fact]
    public void SpeakerPhotoService_receives_the_resolver_from_the_container()
    {
        using var provider = BuildContainer();

        var service = provider.GetRequiredService<SpeakerPhotoService>();

        // Reading the private field is deliberate: the point is precisely that a null here is
        // INVISIBLE from the outside — the service just serves no photos.
        Assert.NotNull(PrivateField(service, "_paths"));
    }

    [Fact]
    public void The_registered_resolver_resolves_the_photo_folders()
    {
        using var provider = BuildContainer();
        var paths = provider.GetRequiredService<IDocLibraryPathResolver>();

        // 🔒 §764 — "speaker photos are in 1 place only". Since §768.14 that rule is carried by ONE
        // key which the sponsor upload, the archive job and the read proxy all resolve. Pin both
        // photo keys: if either stops resolving, photos silently stop rendering.
        Assert.True(paths.TryResolve(DocLibraryPaths.SpeakerPhotos, out var speakers));
        Assert.Equal("General/Test/EventHub/Speakers/Photos", speakers);

        Assert.True(paths.TryResolve(DocLibraryPaths.VolunteerPhotos, out var volunteers));
        Assert.Equal("General/Test/EventHub/Volunteers/Photo", volunteers);
    }

    [Fact]
    public void The_sponsor_upload_keys_resolve_under_the_same_root()
    {
        using var provider = BuildContainer();
        var paths = provider.GetRequiredService<IDocLibraryPathResolver>();

        // §768.14 — the five folders that left the edition config. One root moves all of them
        // between PROD and DEV, which is the property that made the root worth having.
        Assert.True(paths.TryResolve(DocLibraryPaths.SponsorLogoWeb, out var web));
        Assert.Equal("General/Test/EventHub/Sponsors/Logo/Web", web);

        Assert.True(paths.TryResolve(DocLibraryPaths.SponsorLogoPrint, out var print));
        Assert.Equal("General/Test/EventHub/Sponsors/Logo/Print", print);

        Assert.True(paths.TryResolve(DocLibraryPaths.SponsorExhibitorWall, out var wall));
        Assert.Equal("General/Test/EventHub/Sponsors/Exhibitor Wall", wall);

        Assert.True(paths.TryResolve(DocLibraryPaths.SponsorBoothCollateral, out var collateral));
        Assert.Equal("General/Test/EventHub/Sponsors/Booth Collateral", collateral);
    }
}
