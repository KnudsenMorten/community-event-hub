using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Entitlements;
using CommunityHub.Core.Integrations.DocLibrary;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Travel;

/// <summary>§1387 — the outcome of an organizer action, for the page banner.</summary>
public sealed record TravelReviewResult(bool Ok, string Message);

/// <summary>
/// §1387 — a mail exactly as it would go out (operator 2026-11-08: <i>"maybe show mail before send"</i>): rendered by the
/// same code that sends it. <see cref="Warning"/> says when pressing send would not actually send (finance already has it).
/// </summary>
public sealed record TravelMailPreview(string To, IReadOnlyList<string> Cc, string Subject, string Html,
    IReadOnlyList<string> AttachmentNames, string? Warning)
{
    /// <summary>The files themselves (empty on a finance preview; the page shows names only).</summary>
    public IReadOnlyCollection<EmailAttachment> Attachments { get; init; } = Array.Empty<EmailAttachment>();
}

/// <summary>
/// §1387 — the organizer review of travel claims: AI validation, Accept (→ ERP inbox, once) and Send
/// back (→ claim re-opened + speaker mail), plus the sweep that validates new submissions and tells the
/// organizers' mailbox when one is not approved.
/// </summary>
/// <remarks>
/// 🔒 The AI never decides. A claim leaves Under review only by an organizer's Accept or Send back.
/// 🔒 Finance gets a claim ONCE: <see cref="TravelReimbursement.ErpSentAt"/> is stamped on the first
/// send; a claim submitted before §1387 was already sent on submit and is stamped by the migration.
/// </remarks>
public sealed class TravelReviewService
{
    public const string SentBackMailKey = "travel-claim-sent-back";
    public const string ReviewNeededMailKey = "travel-claim-review-needed";
    public const string SubmitTaskKey = "travel:submit-ticket-invoice";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;
    private readonly TravelDocumentValidator _validator;
    private readonly TravelErpMailer _erp;
    private readonly TravelClaimLock _lock;
    private readonly IEmailSender _email;
    private readonly EmailTemplateProvider? _templates;
    private readonly IEmailContextAccessor? _ctx;
    private readonly LogisticsRecipients? _recipients;
    private readonly EventConfigOptions? _config;
    private readonly ILogger<TravelReviewService>? _log;

    public TravelReviewService(
        CommunityHubDbContext db, TimeProvider clock, TravelDocumentValidator validator, TravelErpMailer erp,
        TravelClaimLock claimLock, IEmailSender email, EmailTemplateProvider? templates = null,
        IEmailContextAccessor? ctx = null, LogisticsRecipients? recipients = null,
        EventConfigOptions? config = null, ILogger<TravelReviewService>? log = null)
    {
        _db = db;
        _clock = clock;
        _validator = validator;
        _erp = erp;
        _lock = claimLock;
        _email = email;
        _templates = templates;
        _ctx = ctx;
        _recipients = recipients;
        _config = config;
        _log = log;
    }

    // ---- submit hook -------------------------------------------------------------------------

    /// <summary>
    /// Called by the speaker form when a claim is submitted: Under review, verdict cleared (the sweep
    /// re-checks it). The ERP mail is NOT sent here any more.
    /// </summary>
    public async Task OnSubmittedAsync(int eventId, int participantId, CancellationToken ct = default)
    {
        var row = await _db.TravelReimbursements.Include(r => r.Participant)
            .FirstOrDefaultAsync(r => r.EventId == eventId && r.ParticipantId == participantId, ct);
        if (row is null) return;
        var resubmitted = row.SentBackCount > 0;
        row.ReviewState = TravelReviewState.UnderReview;
        row.AiVerdict = TravelAiVerdict.NotChecked;
        await _db.SaveChangesAsync(ct);

        var files = await _db.TravelReceipts.Where(r => r.EventId == eventId && r.ParticipantId == participantId)
            .OrderBy(r => r.UploadedAt).Select(r => r.FileName).ToListAsync(ct);
        await LogAsync(row, TravelReviewLogKinds.Submitted,
            (resubmitted ? $"Resubmitted after send-back ({row.SentBackCount}×)" : "Submitted")
            + $" — EUR {row.ClaimAmountEur?.ToString("0.##", CultureInfo.InvariantCulture) ?? "?"}, {files.Count} file(s)"
            + (files.Count == 0 ? "." : ": " + string.Join(", ", files) + "."),
            row.Participant?.Email, ct);
    }

    // ---- dialog log (operator 2026-11-08: "can we also get log of dialog") -----------------------

    /// <summary>Append one step to a claim's dialog log.</summary>
    public async Task LogAsync(TravelReimbursement row, string kind, string text, string? actor, CancellationToken ct = default)
    {
        _db.TravelReviewLog.Add(new TravelReviewLogEntry
        {
            EventId = row.EventId, TravelReimbursementId = row.Id, ParticipantId = row.ParticipantId,
            At = _clock.GetUtcNow(), Actor = actor, Kind = kind, Text = Cap(text, 4000),
        });
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Append one step by claim id (for the page's own actions: reopen, paid).</summary>
    public async Task LogAsync(int eventId, int claimId, string kind, string text, string? actor, CancellationToken ct = default)
    {
        var row = await _db.TravelReimbursements.AsNoTracking().FirstOrDefaultAsync(r => r.Id == claimId && r.EventId == eventId, ct);
        if (row is not null) await LogAsync(row, kind, text, actor, ct);
    }

    /// <summary>Every claim's dialog, newest step first.</summary>
    public async Task<Dictionary<int, List<TravelReviewLogEntry>>> DialogAsync(int eventId, CancellationToken ct = default) =>
        (await _db.TravelReviewLog.AsNoTracking().Where(l => l.EventId == eventId)
            .OrderByDescending(l => l.At).ThenByDescending(l => l.Id).ToListAsync(ct))
        .GroupBy(l => l.TravelReimbursementId).ToDictionary(g => g.Key, g => g.ToList());

    private static string Cap(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    // ---- recommendation (operator 2026-11-08: "send judgement to info of what to do, push back or accept") ---

    /// <summary>What the hub recommends the organizers do with a claim, and why.</summary>
    public static (string Short, string Detail) Recommendation(TravelAiVerdict verdict, IReadOnlyList<TravelCheckItem> checks)
    {
        var open = checks.Where(c => c.IsOpenFailure).ToList();
        var question = checks.Where(c => c.Ok is null && !c.IsAccepted).ToList();
        if (verdict is TravelAiVerdict.NotChecked or TravelAiVerdict.CouldNotCheck)
            return ("Check by hand", "The AI could not check these documents — please look at them yourself.");
        if (open.Count > 0)
            return ("Send back", "Ask the speaker for: " + string.Join("; ", open.Select(c => c.Requirement))
                + (question.Count > 0 ? $". Also look at: {string.Join("; ", question.Select(c => c.Requirement))}." : "."));
        if (question.Count > 0)
            return ("Accept after a look", "Nothing is missing, but please look at: " + string.Join("; ", question.Select(c => c.Requirement))
                + " — accept those lines if they are fine.");
        return ("Accept", "Every requirement is met" + (checks.Any(c => c.IsAccepted) ? " or accepted by an organizer." : "."));
    }

    // ---- validate ----------------------------------------------------------------------------

    /// <summary>Run the AI check on one claim and store the verdict. Sends nothing.</summary>
    public async Task<TravelDocumentVerdict?> ValidateAsync(int eventId, int claimId, CancellationToken ct = default)
    {
        var row = await _db.TravelReimbursements.FirstOrDefaultAsync(r => r.Id == claimId && r.EventId == eventId, ct);
        if (row is null || !row.RequestReimbursement) return null;

        var (facts, files) = await InputsAsync(row, ct);
        var verdict = await _validator.ValidateAsync(facts, files, ct);

        // An organizer's acceptance of a requirement survives a re-check that names the same requirement
        // (a re-check that could not run keeps the old list, acceptances and all).
        var previous = Checks(row);
        if (verdict.Verdict == TravelAiVerdict.CouldNotCheck && verdict.Checks.Count == 0 && previous.Count > 0)
        {
            // The re-check did not run: keep the previous verdict and checklist (acceptances and all), and say so.
            var why = $"The re-check could not run ({verdict.Summary}) — showing the previous result. ";
            var prior = row.AiSummary is { } s && s.StartsWith("The re-check could not run", StringComparison.Ordinal)
                ? s[(s.IndexOf("previous result. ", StringComparison.Ordinal) is var i and >= 0 ? i + 17 : s.Length)..]
                : row.AiSummary ?? "";
            var text = why + prior;
            row.AiSummary = text.Length <= 1000 ? text : text[..999] + "…";
            await _db.SaveChangesAsync(ct);
            await LogAsync(row, TravelReviewLogKinds.AiCheck, $"The re-check could not run ({verdict.Summary}); the previous result is kept.", "AI check", ct);
            return verdict;
        }
        var checks = CarryAcceptances(previous, verdict.Checks);

        row.AiVerdict = verdict.Verdict;
        row.AiSummary = verdict.Summary;
        row.AiFindingsJson = JsonSerializer.Serialize(checks, Json);
        row.AiCheckedAt = _clock.GetUtcNow();
        row.AiInputHash = HashOf(row, files);
        await _db.SaveChangesAsync(ct);

        var rec = Recommendation(row.AiVerdict, checks);
        await LogAsync(row, TravelReviewLogKinds.AiCheck,
            $"{EffectiveVerdictText(row.AiVerdict, checks)} — {verdict.Summary} Recommendation: {rec.Short}. {rec.Detail}", "AI check", ct);
        return verdict;
    }

    /// <summary>Validate every submitted claim (organizer "Validate all submitted"). Sends nothing. Returns how many were checked.</summary>
    public async Task<int> ValidateAllSubmittedAsync(int eventId, CancellationToken ct = default)
    {
        var ids = await _db.TravelReimbursements
            .Where(r => r.EventId == eventId && r.RequestReimbursement && r.SubmittedAt != null
                        && r.ReviewState != TravelReviewState.Accepted)
            .Select(r => r.Id)
            .ToListAsync(ct);
        foreach (var id in ids) await ValidateAsync(eventId, id, ct);
        return ids.Count;
    }

    public static IReadOnlyList<TravelCheckItem> Checks(TravelReimbursement row)
    {
        if (string.IsNullOrWhiteSpace(row.AiFindingsJson)) return Array.Empty<TravelCheckItem>();
        try { return JsonSerializer.Deserialize<List<TravelCheckItem>>(row.AiFindingsJson, Json) ?? new(); }
        catch (JsonException) { return Array.Empty<TravelCheckItem>(); }
    }

    /// <summary>New AI lines, keeping an organizer's acceptance where the requirement name is the same.</summary>
    public static IReadOnlyList<TravelCheckItem> CarryAcceptances(IReadOnlyList<TravelCheckItem> previous, IReadOnlyList<TravelCheckItem> fresh)
    {
        var accepted = previous.Where(c => c.IsAccepted)
            .GroupBy(c => Key(c.Requirement)).ToDictionary(g => g.Key, g => g.Last());
        return fresh.Select(c => c.Ok != true && accepted.TryGetValue(Key(c.Requirement), out var a)
            ? c with { AcceptedBy = a.AcceptedBy, AcceptedAt = a.AcceptedAt, AcceptNote = a.AcceptNote }
            : c).ToList();

        static string Key(string s) => string.Join(' ', s.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// The verdict as the organizer reads it: the AI's, except that a Not approved claim whose every missing line an
    /// organizer has accepted reads "Approved (organizer override)".
    /// </summary>
    public static string EffectiveVerdictText(TravelAiVerdict verdict, IReadOnlyList<TravelCheckItem> checks) => verdict switch
    {
        TravelAiVerdict.NotApproved when checks.Count > 0 && checks.Any(c => c.IsAccepted) && !checks.Any(c => c.IsOpenFailure)
            => "Approved (organizer override)",
        TravelAiVerdict.Approved when checks.Any(c => c.IsAccepted) => "Approved (with organizer overrides)",
        TravelAiVerdict.Approved => "Approved",
        TravelAiVerdict.NotApproved => "Not approved",
        TravelAiVerdict.CouldNotCheck => "Could not check",
        _ => "Not checked",
    };

    // ---- accept / undo one requirement ---------------------------------------------------------

    /// <summary>Accept one checklist line (missing or in question) as an organizer, with an optional note.</summary>
    public async Task<TravelReviewResult> AcceptCheckAsync(int eventId, int claimId, int index, string? actor, string? note, CancellationToken ct = default)
        => await EditCheckAsync(eventId, claimId, index, c => c.CanAccept
            ? c with { AcceptedBy = actor, AcceptedAt = _clock.GetUtcNow(), AcceptNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim() }
            : null, "accepted", ct);

    /// <summary>Undo an organizer's acceptance of one checklist line.</summary>
    public async Task<TravelReviewResult> UndoCheckAsync(int eventId, int claimId, int index, string? actor, CancellationToken ct = default)
        => await EditCheckAsync(eventId, claimId, index, c => c.IsAccepted
            ? c with { AcceptedBy = null, AcceptedAt = null, AcceptNote = null }
            : null, "no longer accepted", ct);

    private async Task<TravelReviewResult> EditCheckAsync(int eventId, int claimId, int index,
        Func<TravelCheckItem, TravelCheckItem?> change, string verb, CancellationToken ct)
    {
        var row = await _db.TravelReimbursements.Include(r => r.Participant)
            .FirstOrDefaultAsync(r => r.Id == claimId && r.EventId == eventId, ct);
        if (row is null) return new(false, "That claim no longer exists.");
        var checks = Checks(row).ToList();
        if (index < 0 || index >= checks.Count) return new(false, "That requirement is no longer on the checklist — validate again and retry.");
        var updated = change(checks[index]);
        if (updated is null) return new(false, $"\"{checks[index].Requirement}\" cannot be {verb} in its current state.");
        checks[index] = updated;
        row.AiFindingsJson = JsonSerializer.Serialize(checks, Json);
        await _db.SaveChangesAsync(ct);
        await LogAsync(row,
            updated.IsAccepted ? TravelReviewLogKinds.FindingAccepted : TravelReviewLogKinds.FindingUndone,
            updated.IsAccepted
                ? $"Accepted \"{updated.Requirement}\"" + (string.IsNullOrWhiteSpace(updated.AcceptNote) ? "." : $": {updated.AcceptNote}")
                : $"Undid the acceptance of \"{updated.Requirement}\".",
            updated.IsAccepted ? updated.AcceptedBy : null, ct);
        return new(true, $"{row.Participant.FullName}: \"{updated.Requirement}\" {verb}.");
    }

    // ---- accept ------------------------------------------------------------------------------

    /// <summary>Accept the documents: Accepted, and the claim goes to the ERP inbox if it has not already.</summary>
    public async Task<TravelReviewResult> AcceptAsync(int eventId, int claimId, string? actor, string? note, CancellationToken ct = default)
    {
        var row = await _db.TravelReimbursements.Include(r => r.Participant)
            .FirstOrDefaultAsync(r => r.Id == claimId && r.EventId == eventId, ct);
        if (row is null) return new(false, "That claim no longer exists.");
        if (row.SubmittedAt is null || !row.RequestReimbursement) return new(false, "The claim is not submitted, so there is nothing to accept.");

        row.ReviewState = TravelReviewState.Accepted;
        row.ReviewedAt = _clock.GetUtcNow();
        row.ReviewedByEmail = actor;
        row.ReviewNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();

        var sent = false;
        if (row.ErpSentAt is null)
        {
            await _erp.SendAsync(eventId, row.ParticipantId, row.Participant.FullName, row.Participant.Email, row, ct);
            row.ErpSentAt = _clock.GetUtcNow();
            sent = true;
        }
        await _db.SaveChangesAsync(ct);
        await LogAsync(row, TravelReviewLogKinds.Accepted,
            "Accepted the documents" + (string.IsNullOrWhiteSpace(row.ReviewNote) ? "." : $": {row.ReviewNote}"), actor, ct);
        if (sent)
            await LogAsync(row, TravelReviewLogKinds.MailedFinance,
                $"Claim sent to finance ({TravelErpMailer.ErpInbox}) with the documents and the full review.", "hub", ct);
        return new(true, sent
            ? $"Accepted {row.Participant.FullName}'s documents and sent the claim to the finance inbox."
            : $"Accepted {row.Participant.FullName}'s documents (finance already received this claim earlier; not sent again).");
    }

    // ---- send back ---------------------------------------------------------------------------

    /// <summary>
    /// Send back to the speaker: the claim re-opens (they can upload again), their travel task is open
    /// again, and they get a mail with the organizer's note and the AI's missing items.
    /// </summary>
    public async Task<TravelReviewResult> SendBackAsync(int eventId, int claimId, string? actor, string? note, CancellationToken ct = default)
    {
        var row = await _db.TravelReimbursements.Include(r => r.Participant)
            .FirstOrDefaultAsync(r => r.Id == claimId && r.EventId == eventId, ct);
        if (row is null) return new(false, "That claim no longer exists.");

        // Operator 2026-11-08: "i want to resent to the 3 speakers that we didnt approve … resend from new template" — a claim
        // that is already sent back (and not resubmitted) gets the mail again with the new note; it is not a second send-back.
        if (row.ReviewState == TravelReviewState.SentBack && row.SubmittedAt is null)
        {
            row.ReviewedAt = _clock.GetUtcNow();
            row.ReviewedByEmail = actor;
            row.ReviewNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
            await _db.SaveChangesAsync(ct);
            await LogAsync(row, TravelReviewLogKinds.SentBack,
                "Re-sent the send-back mail" + (row.ReviewNote is null ? "." : $" with the note: {row.ReviewNote}"), actor, ct);
            var again = await BuildSpeakerMailAsync(eventId, row, row.ReviewNote, ct);
            var resent = again is not null && await SendSpeakerMailAsync(eventId, row, again, ct);
            if (resent)
                await LogAsync(row, TravelReviewLogKinds.MailedSpeaker,
                    $"Mail to {again!.To}" + (again.Cc.Count == 0 ? "" : $" (cc {string.Join(", ", again.Cc)})")
                    + $": \"{again.Subject}\", {again.AttachmentNames.Count} document(s) attached.", "hub", ct);
            return new(resent, resent
                ? $"Re-sent the mail to {row.Participant.FullName}. The claim stays open for them."
                : $"The mail to {row.Participant.FullName} could not be sent — tell them directly.");
        }

        if (row.SubmittedAt is not null) await _lock.ReopenAsync(eventId, row.ParticipantId, actor, ct);
        // ReopenAsync saved via its own query; re-read so the edits below land on the current row.
        await _db.Entry(row).ReloadAsync(ct);

        row.ReviewState = TravelReviewState.SentBack;
        row.ReviewedAt = _clock.GetUtcNow();
        row.ReviewedByEmail = actor;
        row.ReviewNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        row.SentBackCount++;
        // Finance may already hold the OLD documents (a pre-§1387 claim, or an accepted one sent back).
        // The corrected documents must reach them, so the next Accept sends the claim again.
        row.ErpSentAt = null;

        var task = await _db.Tasks.FirstOrDefaultAsync(t => t.EventId == eventId && t.AssignedParticipantId == row.ParticipantId
                                                             && t.SourceKey == $"{SubmitTaskKey}:{row.ParticipantId}", ct);
        if (task is not null && task.State == TaskState.Done)
        {
            task.State = TaskState.Open;
            task.CompletedAt = null;
        }
        await _db.SaveChangesAsync(ct);

        var missingNow = Checks(row).Where(c => c.IsOpenFailure).Select(c => c.Requirement).ToList();
        await LogAsync(row, TravelReviewLogKinds.SentBack,
            "Sent back to the speaker" + (string.IsNullOrWhiteSpace(row.ReviewNote) ? "" : $" with the note: {row.ReviewNote}")
            + (missingNow.Count == 0 ? "." : $" Still needed: {string.Join("; ", missingNow)}."), actor, ct);

        var mail = await BuildSpeakerMailAsync(eventId, row, row.ReviewNote, ct);
        var mailed = mail is not null && await SendSpeakerMailAsync(eventId, row, mail, ct);
        if (mailed)
            await LogAsync(row, TravelReviewLogKinds.MailedSpeaker,
                $"Mail to {mail!.To}" + (mail.Cc.Count == 0 ? "" : $" (cc {string.Join(", ", mail.Cc)})")
                + $": \"{mail.Subject}\", {mail.AttachmentNames.Count} document(s) attached.", "hub", ct);
        return new(true, $"Sent back to {row.Participant.FullName}: the claim is open again"
            + (mailed ? " and they were e-mailed the note and what we still need." : " (the e-mail could not be sent — tell them directly)."));
    }

    // ---- previews (operator 2026-11-08: "maybe show mail before send") ---------------------------

    /// <summary>The exact send-back mail, without sending it or changing the claim.</summary>
    public async Task<TravelMailPreview?> PreviewSendBackAsync(int eventId, int claimId, string? note, CancellationToken ct = default)
    {
        var row = await _db.TravelReimbursements.AsNoTracking().Include(r => r.Participant)
            .FirstOrDefaultAsync(r => r.Id == claimId && r.EventId == eventId, ct);
        return row is null ? null : await BuildSpeakerMailAsync(eventId, row, string.IsNullOrWhiteSpace(note) ? null : note.Trim(), ct);
    }

    /// <summary>The exact finance mail Accept would send, without sending it or changing the claim.</summary>
    public async Task<TravelMailPreview?> PreviewAcceptAsync(int eventId, int claimId, string? note, string? actor, CancellationToken ct = default)
    {
        var row = await _db.TravelReimbursements.AsNoTracking().Include(r => r.Participant)
            .FirstOrDefaultAsync(r => r.Id == claimId && r.EventId == eventId, ct);
        if (row is null) return null;
        // The claim as it will be at the moment of sending.
        row.ReviewState = TravelReviewState.Accepted;
        row.ReviewedAt = _clock.GetUtcNow();
        row.ReviewedByEmail = actor;
        row.ReviewNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        var (subject, html, files) = await _erp.RenderAsync(eventId, row.ParticipantId, row.Participant.FullName, row.Participant.Email, row, ct);
        return new TravelMailPreview(TravelErpMailer.ErpInbox, Array.Empty<string>(), subject, html, files.Select(f => f.FileName).ToList(),
            row.ErpSentAt is null ? null : "Finance already received this claim earlier — Accept will NOT send it again.");
    }

    /// <summary>The send-back mail, as it goes out. <paramref name="note"/> is the organizer's note.</summary>
    private async Task<TravelMailPreview?> BuildSpeakerMailAsync(int eventId, TravelReimbursement row, string? note, CancellationToken ct)
    {
        if (_templates is null) return null;
        var p = row.Participant;
        var ev = await _db.Events.Where(e => e.Id == eventId).Select(e => new { e.Code, e.CommunityName }).FirstOrDefaultAsync(ct);
        var tokens = _templates.NewTokenSet(row.ParticipantId);
        tokens["firstName"] = string.IsNullOrWhiteSpace(p.FullName) ? "there" : p.FullName.Split(' ')[0];
        tokens["eventCode"] = ev?.Code ?? "event";
        tokens["communityName"] = ev?.CommunityName ?? "";
        tokens["noteBlock"] = string.IsNullOrWhiteSpace(note) ? ""
            : $"<p style=\"margin:0 0 16px;\">{WebUtility.HtmlEncode(note)}</p>";
        // Accepted lines are not the speaker's to fix. The speaker reads the AI's kind sentence when it wrote one.
        var missing = Checks(row).Where(c => c.IsOpenFailure).ToList();
        tokens["missingBlock"] = missing.Count == 0 ? ""
            : "<p style=\"margin:0 0 6px;\"><strong>What we still need</strong></p><ul style=\"margin:0 0 16px;\">"
              + string.Concat(missing.Select(m => "<li style=\"margin:0 0 6px;\">"
                  + (string.IsNullOrWhiteSpace(m.SpeakerNote)
                      ? WebUtility.HtmlEncode(m.Requirement) + (string.IsNullOrWhiteSpace(m.Note) ? "" : $" — {WebUtility.HtmlEncode(m.Note)}")
                      : WebUtility.HtmlEncode(m.SpeakerNote))
                  + "</li>"))
              + "</ul>";
        // Operator 2026-11-08: "the email for travel reimbursement is missing all details about what invoice must contain
        // we have complete list" — the same list the travel form shows, for the document this speaker needs.
        // The link uses the token set's hub URL — the speaker's personal sign-in link when one is configured.
        var hub = tokens.TryGetValue("hubUrl", out var h) ? h?.TrimEnd('/') : null;
        tokens["requirementsBlock"] = RequirementsHtml(row.HasCompany, TravelDeadline(),
            string.IsNullOrWhiteSpace(hub) ? null : hub + "/Forms/Travel#requirements");
        var rendered = _templates.Render(SentBackMailKey, tokens);
        // Operator 2026-11-08: "send copy of mail to info as cc when you send to speaker".
        var cc = _recipients?.ReviewMailbox ?? LogisticsRecipients.DefaultReviewMailbox;
        var ccList = string.Equals(cc, p.Email, StringComparison.OrdinalIgnoreCase) ? Array.Empty<string>() : new[] { cc };
        var files = await AttachmentsAsync(row, ct);
        return new TravelMailPreview(p.Email, ccList, rendered.Subject, rendered.HtmlBody, files.Select(f => f.FileName).ToList(), null)
        {
            Attachments = files,
        };
    }

    /// <summary>
    /// The complete document requirements, as the speaker's travel form states them (Booking requirements, Submission
    /// process, Billing details), for the second document THIS speaker needs: a company invoice (has a company), a personal
    /// declaration (no company), or both when they did not say.
    /// </summary>
    public static string RequirementsHtml(bool? hasCompany, DateOnly? deadline, string? travelPageUrl = null)
    {
        // Operator 2026-11-08: "make it user friendly" — two numbered steps, one short point per line, the why in plain words.
        const string box = "<div style=\"margin:0 0 12px;padding:12px 14px;background:#f6f8fb;border-radius:8px;\">";
        const string ul = "<ul style=\"margin:4px 0 0;padding-left:20px;\">";
        const string li = "<li style=\"margin:0 0 3px;\">";
        const string bank = "Your bank details: bank name, IBAN, BIC/SWIFT and account holder name";
        var sb = new StringBuilder();
        sb.Append("<p style=\"margin:0 0 8px;\"><strong>📋 Here's everything we need, so you don't have to look for it</strong></p>");

        sb.Append(box + "<strong>1. Your flight receipt</strong> (PDF, JPG or PNG) — it should show:" + ul
            + li + "Your name</li>" + li + "The route and the dates</li>" + li + "The total amount you paid</li></ul>"
            + "<div style=\"margin-top:6px;color:#4b5563;\">A booking confirmation without the payment isn't quite enough, sorry. "
            + "Flew a higher class? No problem — we simply reimburse up to the economy amount.</div></div>");

        var choose = hasCompany is null;
        if (choose)
            sb.Append("<p style=\"margin:0 0 6px;\"><strong>2. Then ONE of these two</strong> — whichever fits you:</p>");
        if (hasCompany != false)
            sb.Append(box + "<strong>" + (choose ? "2a. If you have a registered company: an invoice" : "2. A company invoice") + "</strong> made out to us, showing:" + ul
                + li + "Your company's name and address (the sender)</li>"
                + li + "Our details as the recipient (see the box below)</li>"
                + li + "A short description, e.g. “Speaker travel reimbursement — Experts Live DK”</li>"
                + li + "The total amount</li>"
                + li + bank + "</li></ul></div>");
        if (hasCompany != true)
            sb.Append(box + "<strong>" + (choose ? "2b. No company? A personal declaration" : "2. A personal declaration") + "</strong> — a simple signed note from you, showing:" + ul
                + li + "Your full name and address</li>"
                + li + "A valid ID number (passport or national ID)</li>"
                + li + "Who it's for: Experts Live Denmark</li>"
                + li + "What the costs are for (your flight to the event)</li>"
                + li + "The total amount you claim</li>"
                + li + bank + "</li></ul></div>");

        sb.Append("<p style=\"margin:0 0 12px;padding:10px 14px;border:1px dashed #c7d0dd;border-radius:8px;\"><strong>Our details (for the invoice or declaration)</strong><br />"
                  + "Experts Live Denmark<br />Strandvejen 21A<br />6000 Kolding<br />Denmark<br />VAT Number: DK44197588</p>");
        // Operator 2026-11-08: "they dont know where to find details … help them with details or link" · "so they can click".
        if (!string.IsNullOrWhiteSpace(travelPageUrl))
            sb.Append($"<p style=\"margin:0 0 12px;\">👉 <a href=\"{WebUtility.HtmlEncode(travelPageUrl)}\" style=\"color:#1d4ed8;font-weight:600;\">See the full instructions and upload your documents on your travel page</a></p>");
        if (deadline is DateOnly d)
            sb.Append($"<p style=\"margin:0 0 16px;\">⏰ Please upload everything on your travel page by <strong>{d.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)}</strong>.</p>");
        return sb.ToString();
    }

    private async Task<bool> SendSpeakerMailAsync(int eventId, TravelReimbursement row, TravelMailPreview mail, CancellationToken ct)
    {
        try
        {
            using (_ctx?.Set(new EmailContext(SentBackMailKey, eventId, row.ParticipantId, row.Participant.FullName,
                       TemplateName: SentBackMailKey, FeatureKey: "travel-reimbursement-email")))
            {
                var cc = mail.Cc.Count == 0 ? null : mail.Cc;
                if (mail.Attachments.Count > 0)
                    await _email.SendWithAttachmentsAsync(mail.To, mail.Subject, mail.Html, mail.Attachments, cc, ct);
                else
                    await _email.SendAsync(mail.To, mail.Subject, mail.Html, cc, ct);
            }
            return true;
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Travel send-back mail failed for participant {Pid}", row.ParticipantId);
            return false;
        }
    }

    // ---- sweep -------------------------------------------------------------------------------

    /// <summary>
    /// Validate every claim that is Under review, not yet with finance, and not checked on its current
    /// documents; mail the organizers' mailbox once per document set when the verdict is not Approved.
    /// Returns (checked, notified).
    /// </summary>
    public async Task<(int Checked, int Notified)> SweepAsync(int eventId, CancellationToken ct = default)
    {
        var rows = await _db.TravelReimbursements.Include(r => r.Participant)
            .Where(r => r.EventId == eventId && r.RequestReimbursement && r.SubmittedAt != null
                        && r.ReviewState == TravelReviewState.UnderReview && r.ErpSentAt == null)
            .ToListAsync(ct);

        int checkedN = 0, notified = 0;
        foreach (var row in rows)
        {
            var (_, files) = await InputsAsync(row, ct);
            var hash = HashOf(row, files);
            if (row.AiVerdict == TravelAiVerdict.NotChecked || row.AiInputHash != hash)
            {
                await ValidateAsync(eventId, row.Id, ct);
                checkedN++;
            }
            // Operator 2026-11-08: "i must be able to see when new response from speaker come in … send judgement to info of
            // what to do" — EVERY new document set is reported once, approved ones included, with the recommendation.
            if (row.AiVerdict != TravelAiVerdict.NotChecked && row.AiNotifiedHash != row.AiInputHash)
            {
                if (await MailOrganizersAsync(eventId, row, files.Count, ct))
                {
                    row.AiNotifiedHash = row.AiInputHash;
                    await _db.SaveChangesAsync(ct);
                    notified++;
                }
            }
        }
        return (checkedN, notified);
    }

    private async Task<bool> MailOrganizersAsync(int eventId, TravelReimbursement row, int fileCount, CancellationToken ct)
    {
        if (_templates is null) return false;
        var to = _recipients?.ReviewMailbox ?? LogisticsRecipients.DefaultReviewMailbox;
        try
        {
            var checks = Checks(row);
            var rec = Recommendation(row.AiVerdict, checks);
            var tokens = _templates.NewTokenSet(null);
            tokens["speakerName"] = row.Participant.FullName;   // plain: it is also the subject
            tokens["verdict"] = EffectiveVerdictText(row.AiVerdict, checks);
            tokens["recommendation"] = rec.Short;
            tokens["recommendationDetail"] = WebUtility.HtmlEncode(rec.Detail);
            tokens["recommendationColour"] = rec.Short.StartsWith("Accept", StringComparison.Ordinal) ? "#166534"
                : rec.Short == "Send back" ? "#991b1b" : "#92400e";
            tokens["summary"] = WebUtility.HtmlEncode(row.AiSummary ?? "");
            tokens["checksBlock"] = checks.Count == 0 ? ""
                : "<ul style=\"margin:0 0 12px;padding-left:18px;\">" + string.Concat(checks.Select(c =>
                    $"<li style=\"margin:0 0 4px;\">{(c.IsAccepted ? "✓ (accepted)" : c.Ok switch { true => "✓", false => "✗", _ => "?" })} {WebUtility.HtmlEncode(c.Requirement)}"
                    + (string.IsNullOrWhiteSpace(c.Note) ? "" : $" — {WebUtility.HtmlEncode(c.Note)}") + "</li>")) + "</ul>";
            tokens["amount"] = row.ClaimAmountEur?.ToString("0.##", CultureInfo.InvariantCulture) ?? "?";
            tokens["fileCount"] = fileCount.ToString(CultureInfo.InvariantCulture);
            tokens["hasCompany"] = row.HasCompany switch { true => "yes", false => "no", _ => "not stated" };
            tokens["resubmitBlock"] = row.SentBackCount == 0 ? ""
                : $"<p style=\"margin:0 0 12px;\">This is a <strong>new response</strong> after we sent the claim back ({row.SentBackCount}×)"
                  + (string.IsNullOrWhiteSpace(row.ReviewNote) ? "." : $". Our last note: “{WebUtility.HtmlEncode(row.ReviewNote)}”.") + "</p>";
            tokens["claimId"] = row.Id.ToString(CultureInfo.InvariantCulture);

            var rendered = _templates.Render(ReviewNeededMailKey, tokens);
            var files = await AttachmentsAsync(row, ct);
            using (_ctx?.Set(new EmailContext(ReviewNeededMailKey, eventId, null, null, TemplateName: ReviewNeededMailKey, RingExempt: true)))
            {
                if (files.Count > 0) await _email.SendWithAttachmentsAsync(to, rendered.Subject, rendered.HtmlBody, files, ct);
                else await _email.SendAsync(to, rendered.Subject, rendered.HtmlBody, ct);
            }
            await LogAsync(row, TravelReviewLogKinds.MailedOrganizers,
                $"Mail to {to}: recommendation {rec.Short}, {files.Count} document(s) attached.", "hub", ct);
            return true;
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Travel review-needed mail failed for claim {Id}", row.Id);
            return false;
        }
    }

    // ---- daily reminder (operator 2026-11-08: a daily reminder for claims waiting > 2 days) ------

    public const string WaitingMailKey = "travel-claims-waiting";
    public const int WaitingDays = 2;
    private const string ReminderJob = "TravelReviewJob";

    /// <summary>
    /// Once a day (from 08:00 Copenhagen), tell the organizers' mailbox which claims have waited for a decision more than
    /// <see cref="WaitingDays"/> days. Nothing waiting = no mail. Returns true when it mailed.
    /// </summary>
    public async Task<bool> RemindWaitingAsync(int eventId, CancellationToken ct = default)
    {
        if (_templates is null) return false;
        var now = _clock.GetUtcNow();
        var cph = TimeZoneInfo.ConvertTime(now, CopenhagenZone());
        if (cph.Hour < 8) return false;
        var today = $"reminder:{cph:yyyy-MM-dd}";

        var state = await _db.JobRunStates.FirstOrDefaultAsync(s => s.FunctionName == ReminderJob, ct);
        if (state?.LastContentHash == today) return false;

        var cutoff = now.AddDays(-WaitingDays);
        var waiting = await _db.TravelReimbursements.Include(r => r.Participant)
            .Where(r => r.EventId == eventId && r.RequestReimbursement && r.SubmittedAt != null && r.SubmittedAt <= cutoff
                        && r.ReviewState == TravelReviewState.UnderReview && r.ErpSentAt == null)
            .OrderBy(r => r.SubmittedAt)
            .ToListAsync(ct);
        if (waiting.Count == 0) return false;

        var to = _recipients?.ReviewMailbox ?? LogisticsRecipients.DefaultReviewMailbox;
        var tokens = _templates.NewTokenSet(null);
        tokens["count"] = waiting.Count.ToString(CultureInfo.InvariantCulture);
        tokens["days"] = WaitingDays.ToString(CultureInfo.InvariantCulture);
        tokens["listBlock"] = "<ul style=\"margin:0 0 12px;padding-left:18px;\">" + string.Concat(waiting.Select(r =>
        {
            var rec = Recommendation(r.AiVerdict, Checks(r));
            var days = (int)Math.Floor((now - r.SubmittedAt!.Value).TotalDays);
            return $"<li style=\"margin:0 0 4px;\"><strong>{WebUtility.HtmlEncode(r.Participant.FullName)}</strong> — waiting {days} day(s), "
                   + $"EUR {r.ClaimAmountEur?.ToString("0.##", CultureInfo.InvariantCulture) ?? "?"}, recommendation: {WebUtility.HtmlEncode(rec.Short)}</li>";
        })) + "</ul>";
        var rendered = _templates.Render(WaitingMailKey, tokens);
        try
        {
            using (_ctx?.Set(new EmailContext(WaitingMailKey, eventId, null, null, TemplateName: WaitingMailKey, RingExempt: true)))
            {
                await _email.SendAsync(to, rendered.Subject, rendered.HtmlBody, ct);
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Travel waiting-claims reminder failed");
            return false;
        }

        if (state is null)
        {
            state = new JobRunState { FunctionName = ReminderJob };
            _db.JobRunStates.Add(state);
        }
        state.LastContentHash = today;
        state.LastContentMailedAt = now;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private static TimeZoneInfo CopenhagenZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Copenhagen"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time"); }
    }

    /// <summary>
    /// The documents the speaker submitted, as attachments (operator 2026-11-08: <i>"include attachments … they
    /// submitted in the mails"</i>) — the same files finance receives.
    /// </summary>
    private async Task<List<EmailAttachment>> AttachmentsAsync(TravelReimbursement row, CancellationToken ct) =>
        (await _db.TravelReceipts
            .Where(r => r.EventId == row.EventId && r.ParticipantId == row.ParticipantId)
            .OrderBy(r => r.UploadedAt)
            .ToListAsync(ct))
        .Where(r => r.Content is { Length: > 0 })
        .Select(r => new EmailAttachment(r.FileName, r.Content, r.ContentType))
        .ToList();

    // ---- inputs --------------------------------------------------------------------------------

    private async Task<(TravelClaimFacts Facts, IReadOnlyList<TravelClaimFile> Files)> InputsAsync(TravelReimbursement row, CancellationToken ct)
    {
        var p = await _db.Participants.Where(x => x.Id == row.ParticipantId).Select(x => x.FullName).FirstOrDefaultAsync(ct) ?? "";
        var country = await _db.SpeakerProfiles.Where(s => s.EventId == row.EventId && s.ParticipantId == row.ParticipantId)
            .Select(s => s.Country).FirstOrDefaultAsync(ct);
        var start = await _db.Events.Where(e => e.Id == row.EventId).Select(e => (DateOnly?)e.StartDate).FirstOrDefaultAsync(ct);
        var files = await _db.TravelReceipts
            .Where(r => r.EventId == row.EventId && r.ParticipantId == row.ParticipantId)
            .OrderBy(r => r.UploadedAt)
            .Select(r => new TravelClaimFile(r.FileName, r.ContentType, r.Kind, r.Content))
            .ToListAsync(ct);
        var facts = new TravelClaimFacts(p, country, row.OriginCity, row.ClaimAmountEur, row.Explanation,
            row.HasCompany, row.SubmittedAt, start, TravelDeadline());
        return (facts, files);
    }

    /// <summary>The travel submission deadline from the edition's speaker-deadline config (key "travel").</summary>
    private DateOnly? TravelDeadline()
    {
        try
        {
            var eventPath = _config?.EventConfigPath ?? "config/event.eldk27.json";
            var file = Path.GetFileName(eventPath);
            var name = file.StartsWith("event.", StringComparison.OrdinalIgnoreCase) ? "speaker-deadlines." + file[6..] : null;
            if (name is null) return null;
            var path = ConfigPaths.Resolve(Path.Combine(Path.GetDirectoryName(eventPath) ?? "config", name));
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var d in doc.RootElement.GetProperty("deadlines").EnumerateArray())
                if (d.TryGetProperty("key", out var k) && k.GetString() == "travel"
                    && d.TryGetProperty("dueDate", out var due)
                    && DateOnly.TryParse(due.GetString(), CultureInfo.InvariantCulture, out var date))
                    return date;
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
        }
        return null;
    }

    /// <summary>What a verdict was formed on: the files (name, size, label) and the claim facts that matter.</summary>
    public static string HashOf(TravelReimbursement row, IReadOnlyList<TravelClaimFile> files)
    {
        var s = string.Join("|", files.Select(f => $"{f.FileName}:{f.Content.Length}:{(int)f.Kind}"))
                + $"#{row.ClaimAmountEur}#{row.HasCompany}#{row.OriginCity}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..32];
    }
}
