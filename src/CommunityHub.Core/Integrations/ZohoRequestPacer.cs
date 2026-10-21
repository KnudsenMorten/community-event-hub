using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// §1361 — the Zoho Backstage API request budget: at most <see cref="MaxPerWindow"/> requests per rolling
/// <see cref="Window"/> per process, and a pause after Zoho says "Request limit exceeded". Pure arithmetic — the
/// handler (<see cref="ZohoRequestPacer"/>) only waits and forwards.
/// </summary>
/// <remarks>
/// <para>Measured on PROD 2026-10-19 (Zoho publishes no limit for Backstage): at every xx:x5 tick the 5- and 10-minute
/// jobs fired ~275 Zoho calls in 8 seconds; the first "HTTP 400 — Request limit exceeded" came at call #110, three
/// seconds in, and 41 calls were refused every run. The sponsor sync walks companies in a fixed order, so the same
/// ones (Egiss, Truesec, Master Packager, VirtualMetric, SquaredUp) were refused every run and their changes never
/// reached Backstage. Zoho's other APIs document 100 requests/minute per organisation; this budget stays under it.</para>
/// <para>🔑 Per PROCESS: the jobs host can scale out, and each instance has its own budget. The pause after a refusal
/// is what stops the remaining calls of a burst hitting the wall anyway.</para>
/// </remarks>
public sealed class ZohoRequestBudget
{
    public const int MaxPerWindow = 80;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    /// <summary>How long nothing is sent after Zoho refused with "Request limit exceeded".</summary>
    public static readonly TimeSpan PauseAfterRefusal = TimeSpan.FromSeconds(65);

    private readonly Queue<DateTimeOffset> _sent = new();
    private DateTimeOffset _pausedUntil = DateTimeOffset.MinValue;
    private readonly object _gate = new();

    /// <summary>True while a refusal pause is in force.</summary>
    public bool IsPaused(DateTimeOffset now)
    {
        lock (_gate) return now < _pausedUntil;
    }

    /// <summary>
    /// Reserve a slot for a request at <paramref name="now"/>: <see cref="TimeSpan.Zero"/> = go now (the slot is taken);
    /// otherwise how long to wait before asking again. Never reserves while paused.
    /// </summary>
    public TimeSpan TryReserve(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (now < _pausedUntil) return _pausedUntil - now;
            while (_sent.Count > 0 && now - _sent.Peek() >= Window) _sent.Dequeue();
            if (_sent.Count < MaxPerWindow)
            {
                _sent.Enqueue(now);
                return TimeSpan.Zero;
            }
            var wait = _sent.Peek() + Window - now;
            return wait > TimeSpan.Zero ? wait : TimeSpan.FromMilliseconds(50);
        }
    }

    /// <summary>Zoho refused with "Request limit exceeded": send nothing more for <see cref="PauseAfterRefusal"/>.</summary>
    public void Refused(DateTimeOffset now)
    {
        lock (_gate)
        {
            var until = now + PauseAfterRefusal;
            if (until > _pausedUntil) _pausedUntil = until;
        }
    }

    /// <summary>Zoho's answer when its request limit is hit: HTTP 400 with this message (or a 429).</summary>
    public static bool IsLimitRefusal(HttpStatusCode status, string? body) =>
        status == HttpStatusCode.TooManyRequests
        || (status == HttpStatusCode.BadRequest && body is not null
            && body.Contains("Request limit exceeded", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// §1361 — every Zoho Backstage API call passes here (the typed <see cref="ZohoClient"/> HttpClient, both hosts). Paces to
/// <see cref="ZohoRequestBudget"/>; after a "Request limit exceeded" it stops calling Zoho for the pause and answers the
/// callers itself with the same refusal, so a burst does not keep hitting the wall — the callers already treat that as
/// "unavailable, retry next run". The token endpoint (accounts.zoho.*) is NOT paced here: it has its own, tighter limit and
/// its own cooldown (§783.12, §1142).
/// </summary>
public sealed class ZohoRequestPacer : DelegatingHandler
{
    /// <summary>One budget per process, shared by every ZohoClient instance (typed HttpClients are transient).</summary>
    public static readonly ZohoRequestBudget Shared = new();

    /// <summary>A caller never waits longer than this for a slot; past it the request is answered as refused.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(2);

    private readonly ZohoRequestBudget _budget;
    private readonly TimeProvider _clock;
    private readonly ILogger? _log;

    public ZohoRequestPacer() : this(Shared, TimeProvider.System, null) { }

    public ZohoRequestPacer(ZohoRequestBudget budget, TimeProvider clock, ILogger? log = null)
    {
        _budget = budget;
        _clock = clock;
        _log = log;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (!IsApiCall(request.RequestUri)) return await base.SendAsync(request, ct);

        var waited = TimeSpan.Zero;
        while (true)
        {
            var now = _clock.GetUtcNow();
            if (_budget.IsPaused(now)) return Refusal(request, "paused after Zoho's request limit");
            var wait = _budget.TryReserve(now);
            if (wait == TimeSpan.Zero) break;
            if (waited + wait > MaxWait) return Refusal(request, "request budget full");
            await Task.Delay(wait, _clock, ct);
            waited += wait;
        }

        var response = await base.SendAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.TooManyRequests)
        {
            await response.Content.LoadIntoBufferAsync(ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (ZohoRequestBudget.IsLimitRefusal(response.StatusCode, body))
            {
                _budget.Refused(_clock.GetUtcNow());
                _log?.LogWarning("§1361 Zoho request limit hit — pausing Zoho API calls for {Seconds}s in this process.",
                    (int)ZohoRequestBudget.PauseAfterRefusal.TotalSeconds);
            }
        }
        return response;
    }

    /// <summary>The Backstage API (zohoapis.*), not the OAuth token endpoint.</summary>
    public static bool IsApiCall(Uri? uri) =>
        uri is not null && uri.Host.Contains("zohoapis.", StringComparison.OrdinalIgnoreCase);

    /// <summary>The same answer Zoho gives, so every caller's existing "unavailable, retry next run" path applies.</summary>
    private static HttpResponseMessage Refusal(HttpRequestMessage request, string why) =>
        new(HttpStatusCode.BadRequest)
        {
            RequestMessage = request,
            Content = new StringContent(
                $"{{\"status_code\":\"400\",\"message\":\"Request limit exceeded (CEH: {why}; not sent)\"}}",
                Encoding.UTF8, "application/json"),
        };
}
