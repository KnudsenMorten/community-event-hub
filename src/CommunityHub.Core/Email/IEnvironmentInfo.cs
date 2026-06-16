namespace CommunityHub.Core.Email;

/// <summary>
/// A tiny Core seam that reports whether the app is running in the Development
/// environment, so Core services can hard-gate a DEV-only behaviour without
/// depending on the web project's <c>IWebHostEnvironment</c>. The web app
/// registers an adapter over <c>IHostEnvironment.IsDevelopment()</c>; tests
/// supply a stub so both the allowed (DEV) and refused (non-DEV) paths can be
/// asserted deterministically.
/// </summary>
public interface IEnvironmentInfo
{
    /// <summary>True only when the host environment is "Development".</summary>
    bool IsDevelopment { get; }

    /// <summary>The raw environment name (e.g. "Development", "Production").</summary>
    string EnvironmentName { get; }
}
