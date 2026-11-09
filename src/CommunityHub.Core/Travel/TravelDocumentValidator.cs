using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CommunityHub.Core.Assistant;
using CommunityHub.Core.Documents;
using CommunityHub.Core.Domain;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Travel;

/// <summary>§1387 — the facts about a claim the AI needs besides the files.</summary>
public sealed record TravelClaimFacts(
    string SpeakerName, string? Country, string? OriginCity, decimal? ClaimAmountEur, string? Explanation,
    bool? HasCompany, DateTimeOffset? SubmittedAt, DateOnly? EventStart, DateOnly? SubmissionDeadline);

/// <summary>§1387 — one file handed to the AI.</summary>
public sealed record TravelClaimFile(string FileName, string ContentType, TravelDocumentKind Kind, byte[] Content);

/// <summary>§1387 — the AI's answer.</summary>
public sealed record TravelDocumentVerdict(TravelAiVerdict Verdict, string Summary, IReadOnlyList<TravelCheckItem> Checks);

/// <summary>
/// §1387 — asks the edition's Azure OpenAI deployment whether a speaker's travel-reimbursement
/// documents meet the requirements the form shows them (operator 2026-11-08: <i>"add an extra
/// AI-generated validation of the documents, so we can make pushback to the speaker"</i>).
/// </summary>
/// <remarks>
/// <para>🔒 <b>Advisory, never a rejection.</b> A person always presses Accept or Send back. A call
/// that fails, a model that answers in prose, or a file the model cannot read is
/// <see cref="TravelAiVerdict.CouldNotCheck"/> — never "not approved" (the §1060 rule).</para>
/// <para>🔒 <b>Personal data.</b> Declarations carry passport / ID numbers and invoices carry bank
/// details. They go to the edition's own Azure OpenAI deployment for the check only; the prompt tells
/// the model never to repeat them, and only the checklist (present / missing) is stored.</para>
/// <para>Images go to the model as images; PDFs as their extracted text. A scanned PDF without a
/// text layer is marked unreadable, and the verdict is then at best Could not check for that file.</para>
/// </remarks>
public sealed class TravelDocumentValidator
{
    private static readonly JsonSerializerOptions JsonOpts =
        new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>Text per PDF handed to the model — receipts are short; this bounds a 40-page upload.</summary>
    public const int MaxTextPerFile = 12_000;

    private readonly HttpClient _http;
    private readonly OpenAiOptions _options;
    private readonly ILogger<TravelDocumentValidator>? _log;

    public TravelDocumentValidator(HttpClient http, OpenAiOptions options, ILogger<TravelDocumentValidator>? log = null)
    {
        _http = http;
        _options = options;
        _log = log;
    }

    public bool IsConfigured => _options.IsConfigured;

    /// <summary>The requirements, as the speaker form states them. Kept in ONE place for the prompt.</summary>
    public static string RequirementsText(TravelClaimFacts f)
    {
        var sixWeeks = f.EventStart?.AddDays(-42);
        var sb = new StringBuilder();
        sb.AppendLine("TRAVEL REIMBURSEMENT REQUIREMENTS (what the speaker was told):");
        sb.AppendLine("- Flights only, economy class. Upgrades, premium economy and business class are not reimbursable. "
                      + "BUT a speaker may book a higher class and claim only up to the economy cap: if a higher class was booked "
                      + "and the amount claimed is within the cap, the class requirement is MET (ok true) with a note that a higher "
                      + "class was booked. If the documents do not show the class, the class requirement is ok null (cannot tell), "
                      + "never false.");
        sb.AppendLine("- Caps: European air travel EUR 400 single-speaker session (round trip) or EUR 300 per person co-speaking; "
                      + "intercontinental EUR 800 single-speaker or EUR 600 per person co-speaking. A higher amount needs an explanation.");
        sb.AppendLine("- Not covered: taxi/Uber/public transport/own car in Denmark, seat upgrades, ground transport, parking, meals.");
        sb.AppendLine($"- Tickets must be booked at least six weeks before the event"
                      + (sixWeeks is null ? "" : $" (on or before {sixWeeks.Value:yyyy-MM-dd})")
                      + ". Later bookings are reimbursed only up to the economy price at the six-week mark unless an exception was pre-approved.");
        sb.AppendLine("- ORIGINAL RECEIPT or PAYMENT CONFIRMATION showing the traveller's NAME, the ROUTE, the DATES and the TOTAL AMOUNT PAID. "
                      + "A booking confirmation without payment details is NOT sufficient.");
        const string invoice = "COMPANY INVOICE, issued to Experts Live Denmark, Strandvejen 21A, 6000 Kolding, Denmark, "
                      + "VAT DK44197588, with sender details, recipient details, a description (e.g. 'Speaker travel reimbursement - Experts Live DK'), "
                      + "the total amount, and bank details: bank name, IBAN, BIC/SWIFT, account holder name.";
        const string declaration = "PERSONAL DECLARATION: full name, address, a valid identification number (passport or national ID), "
                      + "the recipient entity, a description of the costs, the total amount claimed, and bank details: bank name, IBAN, BIC/SWIFT, account holder name.";
        // Operator 2026-11-08: "fx company field is not used on personal claim" — only the second document that
        // applies is a requirement; the other one's fields are never reported as missing.
        switch (f.HasCompany)
        {
            case true:
                sb.AppendLine("- A receipt alone is not sufficient; ALSO a " + invoice);
                sb.AppendLine("  The speaker invoices through a registered company: do NOT check personal-declaration items (ID number, home address).");
                break;
            case false:
                sb.AppendLine("- A receipt alone is not sufficient; ALSO a " + declaration);
                sb.AppendLine("  The speaker claims as a PRIVATE PERSON: company items (company name, company VAT/CVR number, company invoice) "
                              + "do NOT apply — never check them and never report them as missing.");
                break;
            default:
                sb.AppendLine("- A receipt alone is not sufficient; ALSO ONE of the following — whichever was uploaded; check only that one "
                              + "and never report the other one's items as missing:");
                sb.AppendLine("  * " + invoice);
                sb.AppendLine("  * " + declaration);
                break;
        }
        if (f.SubmissionDeadline is DateOnly d)
            sb.AppendLine($"- Submission deadline: {d:yyyy-MM-dd}.");
        return sb.ToString();
    }

    public const string SystemPrompt =
        "You check a conference speaker's travel-reimbursement documents against stated requirements, for the organizers. "
        + "Be precise and fair: mark a requirement as met only when the documents show it; mark it missing when they clearly do not; "
        + "use null when you cannot tell (unreadable file, ambiguous). The amount claimed must not exceed the amount paid. "
        + "Check that the route fits the chosen cap (European vs intercontinental). "
        + "NEVER repeat passport/ID numbers, IBANs, account numbers or card numbers in your answer - only say whether they are present. "
        + "Answer with strict JSON only: {\"verdict\":\"approved\"|\"not_approved\",\"summary\":\"<one sentence: what is missing, or 'All requirements met'>\","
        + "\"checks\":[{\"requirement\":\"<short name>\",\"ok\":true|false|null,\"note\":\"<max 20 words, for the organizers>\","
        + "\"speaker_note\":\"<only when ok is false or null: one or two short sentences TO THE SPEAKER>\"}]}. "
        + "speaker_note is read by the speaker, a volunteer who came to speak at a community conference: be warm, humble and "
        + "gentle, never blaming; say first what we already have, then what we still need and WHY, in plain words; at most one "
        + "friendly emoji. Examples: \"We've got your ticket, thank you! 🙂 We just have nowhere to send the money yet: please add "
        + "an invoice (or the personal declaration) with your bank details, so the Danish tax authority accepts the payment.\" · "
        + "\"Your receipt is here 👍 but it doesn't show the travel dates. Could you add the booking confirmation that shows them?\" "
        + "List EVERY requirement that applies to this claim as a check, met or not, so the organizers see the whole picture. "
        + "verdict is approved only when no check is false; a check you cannot tell (null) is shown to the organizers as "
        + "'in question' and on its own does not make the verdict not_approved.";

    /// <summary>Judge one claim. Never throws on a model problem: that is <see cref="TravelAiVerdict.CouldNotCheck"/>.</summary>
    public async Task<TravelDocumentVerdict> ValidateAsync(
        TravelClaimFacts facts, IReadOnlyList<TravelClaimFile> files, CancellationToken ct = default)
    {
        if (!_options.IsConfigured)
            return CouldNot("The AI check is not configured on this host.");
        if (files.Count == 0)
            return new TravelDocumentVerdict(TravelAiVerdict.NotApproved, "No documents were uploaded.",
                new[] { new TravelCheckItem("Documents uploaded", false, "No files") });

        try
        {
            var parts = new List<object> { new { type = "text", text = UserText(facts, files) } };
            foreach (var f in files.Where(IsImage))
            {
                parts.Add(new { type = "text", text = $"Image file: {f.FileName} (labelled by the speaker as: {KindText(f.Kind)})" });
                parts.Add(new { type = "image_url", image_url = new { url = $"data:{ImageMime(f)};base64,{Convert.ToBase64String(f.Content)}", detail = "high" } });
            }

            var payload = new
            {
                max_tokens = 2500,   // every requirement + a speaker sentence per open line
                temperature = 0.0,
                response_format = new { type = "json_object" },
                messages = new object[]
                {
                    new { role = "system", content = SystemPrompt },
                    new { role = "user", content = parts.ToArray() },
                },
            };

            var url = $"{_options.Endpoint!.TrimEnd('/')}/openai/deployments/{_options.Deployment}/chat/completions?api-version={_options.ApiVersion}";
            using var msg = new HttpRequestMessage(HttpMethod.Post, url) { Content = OpenAiChatContent.Create(payload, _options, JsonOpts) };
            msg.Headers.TryAddWithoutValidation("api-key", _options.ApiKey);

            using var resp = await _http.SendAsync(msg, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log?.LogWarning("Travel document check: model returned {Status}.", (int)resp.StatusCode);
                return CouldNot($"The AI service answered {(int)resp.StatusCode}; check the documents by hand.");
            }

            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), default, ct);
            var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            return Parse(content) ?? CouldNot("The AI answer could not be read; check the documents by hand.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Travel document check failed.");
            return CouldNot("The AI check failed; check the documents by hand.");
        }
    }

    private static string UserText(TravelClaimFacts f, IReadOnlyList<TravelClaimFile> files)
    {
        var sb = new StringBuilder(RequirementsText(f));
        sb.AppendLine();
        sb.AppendLine("THE CLAIM:");
        sb.AppendLine($"- Speaker: {f.SpeakerName}");
        sb.AppendLine($"- Country: {f.Country ?? "(unknown)"}; travelling from: {f.OriginCity ?? "(not given)"}");
        sb.AppendLine($"- Amount claimed: {(f.ClaimAmountEur is decimal a ? "EUR " + a.ToString("0.##", CultureInfo.InvariantCulture) : "(not given)")}");
        if (!string.IsNullOrWhiteSpace(f.Explanation)) sb.AppendLine($"- Speaker's explanation: {f.Explanation}");
        sb.AppendLine($"- Registered company: {(f.HasCompany switch { true => "yes - a COMPANY INVOICE is required", false => "no - a PERSONAL DECLARATION is required", _ => "not stated - accept either an invoice or a declaration" })}");
        if (f.SubmittedAt is DateTimeOffset s) sb.AppendLine($"- Submitted: {s:yyyy-MM-dd}");
        sb.AppendLine();
        sb.AppendLine($"FILES ({files.Count}):");
        foreach (var file in files)
        {
            sb.AppendLine($"--- {file.FileName} (labelled: {KindText(file.Kind)})");
            if (IsImage(file)) { sb.AppendLine("(image, attached below)"); continue; }
            var text = DocumentTextExtractor.Extract(file.FileName, file.Content);
            if (string.IsNullOrWhiteSpace(text)) { sb.AppendLine("(UNREADABLE: no text layer - probably a scan. Treat its contents as unknown.)"); continue; }
            sb.AppendLine(text.Length > MaxTextPerFile ? text[..MaxTextPerFile] + "\n(truncated)" : text);
        }
        return sb.ToString();
    }

    /// <summary>Parse the model's JSON; null when it is not the shape we asked for.</summary>
    public static TravelDocumentVerdict? Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        var text = content.Trim();
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            var root = doc.RootElement;
            if (!root.TryGetProperty("verdict", out var v) || v.ValueKind != JsonValueKind.String) return null;
            var verdict = v.GetString()?.Trim().ToLowerInvariant() switch
            {
                "approved" => TravelAiVerdict.Approved,
                "not_approved" or "not approved" or "notapproved" => TravelAiVerdict.NotApproved,
                _ => (TravelAiVerdict?)null,
            };
            if (verdict is null) return null;

            var checks = new List<TravelCheckItem>();
            if (root.TryGetProperty("checks", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in arr.EnumerateArray())
                {
                    var req = c.TryGetProperty("requirement", out var r) ? r.GetString() : null;
                    if (string.IsNullOrWhiteSpace(req)) continue;
                    bool? ok = c.TryGetProperty("ok", out var o)
                        ? o.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null }
                        : null;
                    var note = c.TryGetProperty("note", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                    var forSpeaker = ok != true && c.TryGetProperty("speaker_note", out var sn) && sn.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(sn.GetString()) ? sn.GetString()!.Trim() : null;
                    checks.Add(new TravelCheckItem(Cap(req!, 120), ok, note is null ? null : Cap(note, 240),
                        SpeakerNote: forSpeaker is null ? null : Cap(forSpeaker, 400)));
                }
            }
            // 🔒 "Approved" with a failed line is not approved: the model's own checklist wins.
            if (verdict == TravelAiVerdict.Approved && checks.Any(c => c.Ok == false)) verdict = TravelAiVerdict.NotApproved;

            var summary = root.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            return new TravelDocumentVerdict(verdict.Value,
                Cap(string.IsNullOrWhiteSpace(summary) ? (verdict == TravelAiVerdict.Approved ? "All requirements met" : "Requirements not met") : summary!.Trim(), 900),
                checks);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static TravelDocumentVerdict CouldNot(string why) =>
        new(TravelAiVerdict.CouldNotCheck, why, Array.Empty<TravelCheckItem>());

    private static bool IsImage(TravelClaimFile f) =>
        f.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
        || f.FileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
        || f.FileName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
        || f.FileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase);

    private static string ImageMime(TravelClaimFile f) =>
        f.FileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";

    public static string KindText(TravelDocumentKind k) => k switch
    {
        TravelDocumentKind.TicketReceipt => "ticket / receipt",
        TravelDocumentKind.Invoice => "company invoice",
        TravelDocumentKind.PersonalDeclaration => "personal declaration",
        TravelDocumentKind.Other => "other",
        _ => "not labelled",
    };

    private static string Cap(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
