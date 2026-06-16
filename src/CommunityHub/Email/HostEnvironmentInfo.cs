using CommunityHub.Core.Email;

namespace CommunityHub.Email;

/// <summary>
/// Web-app adapter for the Core <see cref="IEnvironmentInfo"/> seam, backed by
/// the ASP.NET host's <see cref="IHostEnvironment"/>. Lets Core's DEV-only
/// welcome-with-login guard read the real environment without depending on the
/// web project.
/// </summary>
public sealed class HostEnvironmentInfo : IEnvironmentInfo
{
    private readonly IHostEnvironment _host;

    public HostEnvironmentInfo(IHostEnvironment host) => _host = host;

    public bool IsDevelopment =>
        Microsoft.Extensions.Hosting.HostEnvironmentEnvExtensions.IsDevelopment(_host);

    public string EnvironmentName => _host.EnvironmentName;
}
