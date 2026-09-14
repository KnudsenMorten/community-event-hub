using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityHub.Core.Assistant;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations;

/// <summary>The judge's answer: eligible or not, with a short reason when not.</summary>
public sealed record SoMeTextVerdict(bool Eligible, string? Reason);

/// <summary>
/// §1060(l) — ASK THE MODEL WHETHER A SESSION DESCRIPTION IS A REAL DESCRIPTION.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-11: <i>"why not send the text to ai and let it judge it and return the
/// eligibility. you can send the text and context and it return 0 or 1 if eligible"</i>.</para>
///
/// <para>🔑 <b>This does NOT replace <see cref="SoMePlaceholderText"/> — it removes its guess.</b> The
/// rules keep every case that is certain and free (blank, a bare <c>TBD</c>), and this is asked only
/// about the ambiguous middle. That middle is exactly what the old 140-character bound was
/// approximating, so the arbitrary constant goes away rather than being tuned again.</para>
///
/// <para>🔒 <b>Never called from the gate.</b> The verdict is written to
/// <c>Session.SoMeTextEligible</c> by a daily sweep and READ from there — see that property for why
/// a live call would be unsafe now that the lead-time window is gone.</para>
///
/// <para>⚠️ <b>Never throws, and an unconfigured or failing model returns <c>null</c>, not a
/// verdict.</b> "I could not ask" and "the answer is no" must stay distinguishable: collapsing them
/// would let an outage read as "every session is ineligible" and stop the whole campaign silently —
/// the §858.16h lesson (a throttled sweep must never be mistaken for a real negative).</para>
/// </remarks>
public sealed class SoMeTextEligibilityJudge
{
    private static readonly JsonSerializerOptions JsonOpts =
        new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly HttpClient _http;
    private readonly OpenAiOptions _options;
    private readonly ILogger<SoMeTextEligibilityJudge>? _log;

    public SoMeTextEligibilityJudge(
        HttpClient http, OpenAiOptions options, ILogger<SoMeTextEligibilityJudge>? log = null)
    {
        _http = http;
        _options = options;
        _log = log;
    }

    /// <summary>True when a verdict can even be attempted.</summary>
    public bool IsConfigured => _options.IsConfigured;

    /// <summary>
    /// The rules the model judges under. A constant so the wording that decides what reaches
    /// LinkedIn is reviewable in a diff.
    /// </summary>
    /// <remarks>
    /// ⚠️ The instruction to be GENEROUS is deliberate and load-bearing. A false negative withholds a
    /// real session from the campaign and tells nobody; a false positive publishes one weak post,
    /// which is visible and fixable. The same asymmetry is written into
    /// <see cref="SoMePlaceholderText"/>, and both must lean the same way or the pair contradicts
    /// itself at the boundary.
    /// </remarks>
    public const string SystemPrompt =
        "You decide whether a conference session description is REAL CONTENT or a PLACEHOLDER. "
        + "A placeholder says the description is missing, unfinished or coming later - for example "
        + "\"TBD\", \"coming soon\", \"we are working on it and will publish the abstract shortly\", "
        + "or a note addressed to the organizers rather than to an audience. "
        + "Real content describes the talk, its topic, or who it is for - even briefly, even in a "
        + "single sentence, and in any language. "
        + "BE GENEROUS: if it says anything at all about the subject matter, it is real. Only answer "
        + "0 when the text's whole purpose is to say that the description is not ready. "
        + "A description that merely MENTIONS words like \"soon\" or \"coming\" while describing a "
        + "topic is REAL. "
        + "Answer with strict JSON and nothing else: {\"eligible\":1|0,\"reason\":\"<max 12 words, "
        + "only when 0>\"}";

    /// <summary>
    /// Judge one session's description. Returns <c>null</c> when no verdict could be obtained —
    /// the caller must treat that as "not asked", never as "not eligible".
    /// </summary>
    public async Task<SoMeTextVerdict?> JudgeAsync(
        string? title, string? abstractText, CancellationToken ct = default)
    {
        if (!_options.IsConfigured) return null;

        // 🔒 The rules answer the certain cases without spending a call — and this keeps the two
        // implementations consistent by CONSTRUCTION at the one boundary they share.
        if (SoMePlaceholderText.IsMissingOrPlaceholder(abstractText))
            return new SoMeTextVerdict(false, "the description is empty or a placeholder");

        try
        {
            var payload = new
            {
                max_tokens = 60,
                // 🔒 ZERO. This is a judgement, not copy: the same text must answer the same way on
                // two consecutive days, or the daily sweep would flip a session in and out of the
                // campaign on its own. SoMeIntroGenerator uses 0.8 for the opposite reason.
                temperature = 0.0,
                messages = new object[]
                {
                    new { role = "system", content = SystemPrompt },
                    new { role = "user", content = $"Title: {title}\n\nDescription:\n{abstractText}" },
                },
            };

            var endpoint = _options.Endpoint!.TrimEnd('/');
            var url = $"{endpoint}/openai/deployments/{_options.Deployment}/chat/completions"
                      + $"?api-version={_options.ApiVersion}";

            using var msg = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(payload, options: JsonOpts),
            };
            msg.Headers.TryAddWithoutValidation("api-key", _options.ApiKey);

            using var resp = await _http.SendAsync(msg, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log?.LogWarning(
                    "SoMe text judge: model returned {Status} — no verdict formed (the session keeps "
                    + "its previous one).", (int)resp.StatusCode);
                return null;
            }

            using var doc = await JsonDocument.ParseAsync(
                await resp.Content.ReadAsStreamAsync(ct), default, ct);

            var content = doc.RootElement
                .GetProperty("choices")[0].GetProperty("message").GetProperty("content")
                .GetString();

            return Parse(content);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // a host shutdown is not a verdict either
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "SoMe text judge: call failed — no verdict formed.");
            return null;
        }
    }

    /// <summary>
    /// Parse the model's JSON. ⚠️ An unparseable answer is <c>null</c> — "not asked" — rather than a
    /// 0, for the same reason a failed call is: a model that starts replying in prose must not read
    /// as "every session is ineligible".
    /// </summary>
    internal static SoMeTextVerdict? Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        // Models sometimes fence JSON even when told not to.
        var text = content.Trim();
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;

        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            if (!doc.RootElement.TryGetProperty("eligible", out var eligible)) return null;

            var value = eligible.ValueKind switch
            {
                JsonValueKind.Number => eligible.GetInt32() != 0,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                // ⚠️ An UNRECOGNISED string is null, not false. Written as `is "1" or "true"` this
                // returned false for anything else — so a model answering {"eligible":"maybe"} was
                // read as a firm NO. That is the one mistake this whole class is shaped to avoid,
                // and only the test caught it: the code looked obviously right.
                JsonValueKind.String => eligible.GetString() switch
                {
                    "1" or "true" or "True" or "yes" => true,
                    "0" or "false" or "False" or "no" => false,
                    _ => (bool?)null,
                },
                _ => (bool?)null,
            };
            if (value is not bool ok) return null;

            var reason = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() : null;
            return new SoMeTextVerdict(ok, string.IsNullOrWhiteSpace(reason) ? null : reason!.Trim());
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
