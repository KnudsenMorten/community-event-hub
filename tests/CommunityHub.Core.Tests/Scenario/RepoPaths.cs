namespace CommunityHub.Core.Tests.Scenario;

/// <summary>
/// Locates files in the working tree relative to the test assembly, so scenario
/// tests can assert against the REAL shipped config (e.g. the speaker-deadline
/// dates) rather than a copy that could drift.
/// </summary>
public static class RepoPaths
{
    /// <summary>Walk up from the test bin folder to the repo root (the dir holding CommunityHub.sln).</summary>
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CommunityHub.sln")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repo root (CommunityHub.sln).");
    }

    /// <summary>The shipped per-edition speaker-deadlines config.</summary>
    public static string SpeakerDeadlinesConfig() =>
        Path.Combine(RepoRoot(), "config", "speaker-deadlines.eldk27.json");

    /// <summary>The shipped email-template directory (welcome.html etc.).</summary>
    public static string EmailTemplates() =>
        Path.Combine(RepoRoot(), "templates", "emails");
}
