using CommunityHub.Core.Email;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Diagnostics;

/// <summary>
/// §545(b) CREDENTIAL — one <see cref="DelegatingHandler"/> that turns a dead credential on ANY
/// integration into an immediate, throttled mail to the operator.
/// </summary>
/// <remarks>
/// <para><b>Why a handler and not a check in each client.</b> The same argument that makes
/// <c>EngineErrorAlertMiddleware</c> work: one chokepoint every call already passes through, so a
/// NEW integration is covered by adding one registration line rather than by remembering to write
/// the alert. §545(c) asked for exactly this — *"so a new integration cannot ship unmonitored"*.</para>
///
/// <para><b>Tonight is the argument for it.</b> Zoho has had a credential alert since §524; nothing
/// else did. In the same session a <b>dead SharePoint client secret in BOTH environments</b> (§598)
/// and a never-set Backstage read flag (§621; 🗑 deleted in §754.5) each hid for an unknown time.
/// Every one of them was visible in a log and invisible to him.</para>
///
/// <para>🔒 <b>Fail-soft on the OBSERVING.</b> An alert that throws would turn a recoverable 403
/// into a crashed job, so everything from classification to sending is swallowed and the response
/// comes back untouched.</para>
///
/// <para>🔴 <b>§1113 — but NOT fail-soft on the body read, and the difference is the whole bug.</b>
/// Reading the body happens inside the handler pipeline, where the content is still the live network
/// stream. A read that dies half way leaves it consumed and unbuffered, and the caller's own read
/// then throws <i>"The stream was already consumed"</i> — a non-transient exception describing none
/// of what happened. Swallowing that is not fail-soft; it is handing back a corpse. The transport
/// fault is now rethrown instead, where the retry handler can act on it and a human can read it.</para>
/// </remarks>
public sealed class CredentialFailureAlertHandler : DelegatingHandler
{
    private readonly string _integration;
    private readonly Func<EngineAlertSender?> _alerts;
    private readonly ILogger? _log;

    /// <param name="integration">The name the operator will read, e.g. "SharePoint".</param>
    /// <param name="alerts">
    /// Resolved lazily: the handler outlives a scope, and capturing a scoped sender would keep a
    /// dead DbContext alive for the lifetime of the HttpClient.
    /// </param>
    public CredentialFailureAlertHandler(
        string integration, Func<EngineAlertSender?> alerts, ILogger? log = null)
    {
        _integration = integration;
        _alerts = alerts;
        _log = log;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var response = await base.SendAsync(request, ct);

        // Fast path: the overwhelming majority of calls. Read nothing, allocate nothing.
        if (response.IsSuccessStatusCode
            && response.StatusCode != System.Net.HttpStatusCode.OK) return response;

        // 🔒 Only read the body when the status is one that could carry a hidden auth failure
        // (§524: Zoho answers 200 with {"error":"invalid_code"} for a revoked grant). Reading
        // every 200 body would double the memory cost of every list pull for nothing.
        string? body = null;
        var status = response.StatusCode;
        var couldHideAuthFailure =
            status is System.Net.HttpStatusCode.OK
                   or System.Net.HttpStatusCode.Unauthorized
                   or System.Net.HttpStatusCode.Forbidden
                   or System.Net.HttpStatusCode.BadRequest;

        // 🔴 §1113 — OUTSIDE the swallow-everything block below, on purpose. The catch that keeps a
        // failed ALERT from breaking a working call would also bury a failed BODY READ, and those
        // two are opposites: the first leaves the response intact, the second has already destroyed
        // it. Burying the second is exactly how the operator got "Order 10764: The stream was
        // already consumed" instead of a network error.
        if (couldHideAuthFailure && response.Content is not null)
        {
            // 🔴 §1113 — BUFFER FIRST, AND LET A FAILED BUFFERING THROUGH.
            //
            // ⚠️ The line this replaced read the body with a comment saying *"Buffered by
            // HttpClient, so reading here does NOT consume it for the caller"*. That is true only
            // when the read SUCCEEDS. `HttpClient` buffers the response AFTER the handler chain
            // returns, so in here the content is still the live network stream; a read that dies
            // half way — a socket reset, the connection dropped — leaves it CONSUMED AND
            // UNBUFFERED, and the caller's own read then throws
            // `InvalidOperationException: The stream was already consumed. It cannot be read
            // again.`
            //
            // 🔑 <b>That message named nothing and blamed the wrong layer.</b> It reached the
            // operator as *"Order 10764: The stream was already consumed. It cannot be read
            // again."* on an invoicing report (2026-08-20) — an error about a network blip,
            // rendered as an unexplained internal fault against a specific customer's order.
            // Worse, `InvalidOperationException` is not transient, so the retry handler wrapped
            // around this one saw nothing to retry.
            //
            // 🔒 So: rethrow the REAL fault. It is an `HttpRequestException`/`IOException`, which
            // the outer <see cref="Integrations.TransientFaultRetryHandler"/> does retry, and
            // which says what actually happened if it survives to a human. Observing must never
            // break the call it observed — and handing back a response whose body can no longer
            // be read is breaking it, just quietly.
            try
            {
                await response.Content.LoadIntoBufferAsync(ct);
            }
            catch (Exception readEx) when (readEx is not OperationCanceledException)
            {
                _log?.LogWarning(readEx,
                    "CredentialFailureAlertHandler[{Integration}]: reading the response body "
                    + "failed, so the credential check was skipped and the transport fault is "
                    + "being surfaced to the caller.",
                    _integration);
                response.Dispose();
                throw;
            }

            // Safe now: the content is a buffer, and the caller re-reads it from memory.
            body = await response.Content.ReadAsStringAsync(ct);
        }

        try
        {
            var verdict = CredentialFailureDetector.Classify(_integration, status, body);
            if (!verdict.ShouldAlert) return response;

            _log?.LogError(
                "CREDENTIAL[{Integration}]: {Verdict} — {Detail}",
                _integration, verdict.Verdict, verdict.Detail);

            var sender = _alerts();
            if (sender is null) return response;

            var url = request.RequestUri?.GetLeftPart(UriPartial.Path) ?? "(unknown)";
            await sender.AlertAsync(
                $"{_integration} credential problem — integration blocked [ELDK27]",
                $"<p>{System.Net.WebUtility.HtmlEncode(verdict.Detail)}</p>"
                + $"<p>Call: <code>{System.Net.WebUtility.HtmlEncode(request.Method.ToString())} "
                + $"{System.Net.WebUtility.HtmlEncode(url)}</code> &rarr; <b>{(int)status}</b></p>"
                + "<p>Until this is fixed, everything that depends on this integration is doing "
                + "nothing — it will not fail loudly on its own, and it cannot retry its way out.</p>",
                ct, throttleKey: CredentialFailureDetector.ThrottleKey(_integration));
        }
        catch (Exception ex)
        {
            // Observing must never break the call it observed.
            _log?.LogWarning(ex,
                "CredentialFailureAlertHandler[{Integration}]: the check itself failed; ignored.",
                _integration);
        }

        return response;
    }
}
