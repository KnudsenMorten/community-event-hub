using CommunityHub.Core.Email;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Diagnostics;

/// <summary>
/// §545(b)/§545(c) — one line per integration, which is the whole point.
/// </summary>
/// <remarks>
/// <para>§545(c): *"a new integration cannot ship unmonitored"*. Credential alerting is therefore
/// registration-shaped rather than code-shaped: adding an integration means adding
/// <c>.AddCredentialFailureAlert("Name")</c> beside its <c>AddHttpClient</c>, and a reviewer can see
/// at a glance which clients have it and which do not.</para>
/// </remarks>
public static class CredentialFailureAlertExtensions
{
    /// <summary>
    /// Alert the operator when this integration starts rejecting our credential.
    /// </summary>
    /// <param name="integration">The name the operator will read, e.g. "SharePoint".</param>
    public static IHttpClientBuilder AddCredentialFailureAlert(
        this IHttpClientBuilder builder, string integration) =>
        builder.AddHttpMessageHandler(sp => new CredentialFailureAlertHandler(
            integration,
            // Resolved per call, from the ROOT provider: the handler is long-lived and capturing a
            // scoped sender would pin a disposed DbContext for the lifetime of the HttpClient.
            () => sp.GetService<EngineAlertSender>(),
            sp.GetService<ILoggerFactory>()?.CreateLogger("CredentialFailure." + integration)));
}
