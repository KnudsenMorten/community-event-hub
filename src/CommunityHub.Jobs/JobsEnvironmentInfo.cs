using CommunityHub.Core.Email;
using Microsoft.Extensions.Hosting;

namespace CommunityHub.Jobs;

/// <summary>
/// Functions-worker adapter for the Core <see cref="IEnvironmentInfo"/> seam,
/// backed by the host's <see cref="IHostEnvironment"/> — the Jobs counterpart of
/// the web app's HostEnvironmentInfo. Lets Core services read the real environment
/// without referencing the web project. (The attendee-provisioning welcome path
/// bypasses the DEV guard deliberately; this exists so the Core service's
/// constructor dependency resolves in the Jobs container.)
/// </summary>
public sealed class JobsEnvironmentInfo : IEnvironmentInfo
{
    private readonly IHostEnvironment _host;

    public JobsEnvironmentInfo(IHostEnvironment host) => _host = host;

    public bool IsDevelopment => _host.IsDevelopment();

    public string EnvironmentName => _host.EnvironmentName;
}
