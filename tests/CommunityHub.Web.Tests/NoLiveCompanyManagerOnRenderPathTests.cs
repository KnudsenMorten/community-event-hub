using System;
using System.IO;
using System.Linq;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §443 (operator 2026-07-27): organizer pages render company names from CEH SQL, never from a
/// live call per company. Company Manager is gone now, so the live-call guard went with it; what
/// stays is the SHARED local resolver, so the name lookups cannot drift apart again.
/// </summary>
public sealed class NoLiveCompanyManagerOnRenderPathTests
{
    private static string PagesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "CommunityHub", "Pages");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate src/CommunityHub/Pages");
    }

    /// <summary>
    /// The replacement is a SHARED local resolver, not five more copies. Nine hand-rolled
    /// `ResolveCompanyNamesAsync` bodies are exactly how the old ones drifted apart (one page had
    /// already grown a local-name fallback the others lacked).
    /// </summary>
    [Fact]
    public void The_organizer_pages_resolve_names_through_the_shared_local_service()
    {
        var organizerDir = Path.Combine(PagesDir(), "Organizer");
        var resolvers = Directory
            .EnumerateFiles(organizerDir, "*.cshtml.cs", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains("ResolveCompanyNamesAsync"))
            .ToList();

        Assert.NotEmpty(resolvers);
        foreach (var f in resolvers)
        {
            Assert.True(
                File.ReadAllText(f).Contains("SponsorCompanyNameService.ResolveFromLocalAsync"),
                $"{Path.GetFileName(f)} resolves company names without the shared local service.");
        }
    }
}
