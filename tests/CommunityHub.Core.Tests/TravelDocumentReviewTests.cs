using System.Net;
using System.Text;
using CommunityHub.Core.Assistant;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Entitlements;
using CommunityHub.Core.Integrations.DocLibrary;
using CommunityHub.Core.Travel;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1387 — travel document review: the AI verdict parser, the submit → review hand-off, Accept (ERP once),
/// Send back (reopen + task + speaker mail) and the sweep's once-per-document-set organizer notice.
/// In-memory DB, a stubbed model endpoint, a recording sender, fake names.
/// </summary>
public sealed class TravelDocumentReviewTests
{
    private const int EventId = 1;

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-11-08T12:00:00Z");
    }

    private sealed class Model : HttpMessageHandler
    {
        public string Answer { get; set; } = "{}";
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public int Calls { get; private set; }
        public string? LastBody { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            var wrapped = System.Text.Json.JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = Answer } } } });
            return new HttpResponseMessage(Status) { Content = new StringContent(wrapped, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Mail : IEmailSender
    {
        private readonly IEmailContextAccessor _ctx;
        public Mail(IEmailContextAccessor ctx) => _ctx = ctx;
        public List<(string To, string Subject, int Attachments, EmailContext? Ctx)> Sent { get; } = new();
        /// <summary>Per sent mail, in the same order as <see cref="Sent"/>: its body and its CC list.</summary>
        public List<(string Body, IReadOnlyCollection<string>? Cc)> Details { get; } = new();
        public Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken cancellationToken = default)
        { Sent.Add((toEmail, subject, 0, _ctx.Current)); Details.Add((htmlBody, null)); return Task.CompletedTask; }
        public Task SendAsync(string toEmail, string subject, string htmlBody, IReadOnlyCollection<string>? cc, CancellationToken cancellationToken = default)
        { Sent.Add((toEmail, subject, 0, _ctx.Current)); Details.Add((htmlBody, cc)); return Task.CompletedTask; }
        public Task SendAsync(string toEmail, string subject, string htmlBody, EmailReplyTo? replyTo, CancellationToken cancellationToken = default)
            => SendAsync(toEmail, subject, htmlBody, cancellationToken);
        public Task SendAsync(string toEmail, string subject, string htmlBody, string textBody, CancellationToken cancellationToken = default)
            => SendAsync(toEmail, subject, htmlBody, cancellationToken);
        public Task SendWithIcsAsync(string toEmail, string subject, string htmlBody, string icsContent, string icsFileName, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("not used");
        public Task SendWithAttachmentsAsync(string toEmail, string subject, string htmlBody, IReadOnlyCollection<EmailAttachment> attachments, CancellationToken cancellationToken = default)
        { Sent.Add((toEmail, subject, attachments.Count, _ctx.Current)); Details.Add((htmlBody, null)); return Task.CompletedTask; }
        public Task SendWithAttachmentsAsync(string toEmail, string subject, string htmlBody, IReadOnlyCollection<EmailAttachment> attachments,
            IReadOnlyCollection<string>? cc, CancellationToken cancellationToken = default)
        { Sent.Add((toEmail, subject, attachments.Count, _ctx.Current)); Details.Add((htmlBody, cc)); return Task.CompletedTask; }
    }

    private sealed record World(CommunityHubDbContext Db, TravelReviewService Review, Model Ai, Mail Mail, int ClaimId, int Pid);

    private static async Task<World> NewWorldAsync(bool configured = true, bool withRings = false)
    {
        var db = new CommunityHubDbContext(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"trv-{Guid.NewGuid():N}").Options);
        var clock = new Clock();
        var ctx = new EmailContextAccessor();
        var mail = new Mail(ctx);
        var ai = new Model();
        var options = configured
            ? new OpenAiOptions { Enabled = true, Endpoint = "https://ai.example", Deployment = "d", ApiKey = "k" }
            : new OpenAiOptions();
        var validator = new TravelDocumentValidator(new HttpClient(ai), options);
        var templates = new EmailTemplateProvider(Options.Create(new EmailTemplateOptions { TemplateDirectory = RepoPaths.EmailTemplates() }));
        var review = new TravelReviewService(db, clock, validator, new TravelErpMailer(db, mail, ctx),
            new TravelClaimLock(db, clock, new TravelClaimLockOptions { FreezeEnabled = true }), mail, templates, ctx,
            new LogisticsRecipients { ReviewMailbox = "ops@crew.example" },
            rings: withRings
                ? new EmailTemplateRingService(db, new CommunityHub.Core.Settings.FeatureGateService(db), TimeProvider.System)
                : null);

        db.Events.Add(new Event { Id = EventId, Code = "T27", CommunityName = "Test", DisplayName = "Test 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true });
        var p = new Participant { EventId = EventId, FullName = "Ada Speaker", Email = "ada@speaker.example",
            Role = ParticipantRole.Speaker, IsActive = true, LifecycleState = ParticipantLifecycleState.Active };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        var claim = new TravelReimbursement { EventId = EventId, ParticipantId = p.Id, RequestReimbursement = true,
            ClaimAmountEur = 400, OriginCity = "Oslo", HasCompany = false, SubmittedAt = clock.GetUtcNow() };
        db.TravelReimbursements.Add(claim);
        db.TravelReceipts.Add(new TravelReceipt { EventId = EventId, ParticipantId = p.Id, FileName = "ticket.png",
            ContentType = "image/png", Content = new byte[] { 1, 2, 3 }, SizeBytes = 3, Kind = TravelDocumentKind.TicketReceipt });
        db.Tasks.Add(new ParticipantTask { EventId = EventId, AssignedParticipantId = p.Id, Title = "Submit travel reimbursement",
            SourceKey = $"{TravelReviewService.SubmitTaskKey}:{p.Id}", State = TaskState.Done });
        await db.SaveChangesAsync();
        return new World(db, review, ai, mail, claim.Id, p.Id);
    }

    private const string NotApproved =
        "{\"verdict\":\"not_approved\",\"summary\":\"The personal declaration is missing.\",\"checks\":["
        + "{\"requirement\":\"Receipt with payment\",\"ok\":true,\"note\":null},"
        + "{\"requirement\":\"Personal declaration\",\"ok\":false,\"note\":\"No declaration uploaded\"}]}";

    // ---- parser ------------------------------------------------------------------------------

    [Fact]
    public void Parse_reads_the_verdict_and_checklist_and_a_failed_line_overrules_approved()
    {
        var v = TravelDocumentValidator.Parse(
            "```json\n{\"verdict\":\"approved\",\"summary\":\"ok\",\"checks\":[{\"requirement\":\"Invoice\",\"ok\":false,\"note\":\"no IBAN\"}]}\n```");
        Assert.NotNull(v);
        Assert.Equal(TravelAiVerdict.NotApproved, v!.Verdict);
        Assert.Equal(false, Assert.Single(v.Checks).Ok);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("I think it looks fine")]
    [InlineData("{\"verdict\":\"maybe\"}")]
    public void Parse_returns_null_for_anything_that_is_not_the_answer_we_asked_for(string? content) =>
        Assert.Null(TravelDocumentValidator.Parse(content));

    [Fact]
    public void The_requirements_name_the_six_week_date_and_only_the_second_document_that_applies()
    {
        TravelClaimFacts F(bool? company) => new("A", "Norway", "Oslo", 400, null, company,
            null, new DateOnly(2027, 2, 9), new DateOnly(2027, 1, 10));

        var personal = TravelDocumentValidator.RequirementsText(F(false));
        Assert.Contains("2026-12-29", personal);           // 42 days before 9 Feb 2027
        Assert.Contains("2027-01-10", personal);
        Assert.Contains("PERSONAL DECLARATION", personal);
        Assert.DoesNotContain("COMPANY INVOICE", personal);   // "company field is not used on personal claim"
        Assert.Contains("PRIVATE PERSON", personal);

        var company = TravelDocumentValidator.RequirementsText(F(true));
        Assert.Contains("COMPANY INVOICE", company);
        Assert.DoesNotContain("PERSONAL DECLARATION", company);

        var unknown = TravelDocumentValidator.RequirementsText(F(null));
        Assert.Contains("COMPANY INVOICE", unknown);
        Assert.Contains("PERSONAL DECLARATION", unknown);
        Assert.Contains("whichever was uploaded", unknown);

        // "if you cannot figure out ticket type" + a higher class claimed within the economy cap is not a failure.
        Assert.Contains("ok null", personal);
        Assert.Contains("within the cap", personal);
    }

    // ---- validate ----------------------------------------------------------------------------

    [Fact]
    public async Task Validate_stores_the_verdict_sends_the_image_and_mails_nobody()
    {
        var w = await NewWorldAsync();
        w.Ai.Answer = NotApproved;

        var v = await w.Review.ValidateAsync(EventId, w.ClaimId);

        Assert.Equal(TravelAiVerdict.NotApproved, v!.Verdict);
        var row = await w.Db.TravelReimbursements.SingleAsync();
        Assert.Equal(TravelAiVerdict.NotApproved, row.AiVerdict);
        Assert.Equal(2, TravelReviewService.Checks(row).Count);
        Assert.Contains("data:image/png;base64", w.Ai.LastBody);
        Assert.Contains("PERSONAL DECLARATION is required", w.Ai.LastBody);
        Assert.Empty(w.Mail.Sent);
    }

    [Fact]
    public async Task A_failed_call_or_no_configuration_is_could_not_check_never_not_approved()
    {
        var w = await NewWorldAsync();
        w.Ai.Status = HttpStatusCode.InternalServerError;
        Assert.Equal(TravelAiVerdict.CouldNotCheck, (await w.Review.ValidateAsync(EventId, w.ClaimId))!.Verdict);

        var off = await NewWorldAsync(configured: false);
        Assert.Equal(TravelAiVerdict.CouldNotCheck, (await off.Review.ValidateAsync(EventId, off.ClaimId))!.Verdict);
        Assert.Equal(0, off.Ai.Calls);
    }

    // ---- accept / send back --------------------------------------------------------------------

    [Fact]
    public async Task Accept_sends_the_claim_to_the_ERP_inbox_once()
    {
        var w = await NewWorldAsync();
        await w.Review.OnSubmittedAsync(EventId, w.Pid);

        Assert.True((await w.Review.AcceptAsync(EventId, w.ClaimId, "org@crew.example", null)).Ok);
        Assert.True((await w.Review.AcceptAsync(EventId, w.ClaimId, "org@crew.example", null)).Ok);

        var erp = Assert.Single(w.Mail.Sent);
        Assert.Equal(TravelErpMailer.ErpInbox, erp.To);
        Assert.Equal(1, erp.Attachments);
        Assert.True(erp.Ctx!.RingExempt);
        var row = await w.Db.TravelReimbursements.SingleAsync();
        Assert.Equal(TravelReviewState.Accepted, row.ReviewState);
        Assert.NotNull(row.ErpSentAt);
    }

    [Fact]
    public async Task Accepting_a_claim_finance_already_has_does_not_send_it_again()
    {
        var w = await NewWorldAsync();
        var row = await w.Db.TravelReimbursements.SingleAsync();
        row.ReviewState = TravelReviewState.UnderReview;
        row.ErpSentAt = row.SubmittedAt;   // as the migration stamps a pre-§1387 claim
        await w.Db.SaveChangesAsync();

        await w.Review.AcceptAsync(EventId, w.ClaimId, "org@crew.example", null);

        Assert.Empty(w.Mail.Sent);
    }

    [Fact]
    public async Task Send_back_reopens_the_claim_and_task_and_mails_the_speaker_ring_gated()
    {
        var w = await NewWorldAsync();
        w.Ai.Answer = NotApproved;
        await w.Review.ValidateAsync(EventId, w.ClaimId);
        var row = await w.Db.TravelReimbursements.SingleAsync();
        row.ErpSentAt = row.SubmittedAt;
        await w.Db.SaveChangesAsync();

        var r = await w.Review.SendBackAsync(EventId, w.ClaimId, "org@crew.example", "Please add the declaration");

        Assert.True(r.Ok);
        row = await w.Db.TravelReimbursements.SingleAsync();
        Assert.Null(row.SubmittedAt);                                  // open for new uploads
        Assert.Equal(TravelReviewState.SentBack, row.ReviewState);
        Assert.Null(row.ErpSentAt);                                    // corrected files reach finance on the next Accept
        Assert.Equal(TaskState.Open, (await w.Db.Tasks.SingleAsync()).State);
        var m = Assert.Single(w.Mail.Sent);
        Assert.Equal("ada@speaker.example", m.To);
        Assert.False(m.Ctx!.RingExempt);
        Assert.Equal(TravelReviewService.SentBackMailKey, m.Ctx.TemplateName);
        Assert.Equal("travel-reimbursement-email", m.Ctx.FeatureKey);
    }

    /// <summary>
    /// §1390 — operator 2026-11-09: three send-backs "were not received". The mail had no ring row, so it
    /// resolved to Ring 0 and the transport dropped the speaker's copy while the dialog said "Mail to …".
    /// It must say it was NOT sent, and send nothing.
    /// </summary>
    [Fact]
    public async Task Send_back_says_not_sent_when_the_mail_ring_excludes_the_speaker()
    {
        var w = await NewWorldAsync(withRings: true);
        w.Ai.Answer = NotApproved;
        await w.Review.ValidateAsync(EventId, w.ClaimId);

        var r = await w.Review.SendBackAsync(EventId, w.ClaimId, "org@crew.example", "Please add the declaration");

        Assert.True(r.Ok);                                             // the claim IS sent back
        Assert.Contains("NOT sent", r.Message);
        Assert.Contains("Ring 0", r.Message);
        Assert.Empty(w.Mail.Sent);
        var log = await w.Db.TravelReviewLog.Where(l => l.Kind == TravelReviewLogKinds.MailedSpeaker).ToListAsync();
        Assert.Contains(log, l => l.Text.StartsWith("NOT mailed", StringComparison.Ordinal));

        // Re-send while still blocked: refused, still nothing sent.
        var again = await w.Review.SendBackAsync(EventId, w.ClaimId, "org@crew.example", "Second try");
        Assert.False(again.Ok);
        Assert.Empty(w.Mail.Sent);

        // He opens the mail to Ring 3 → Re-send goes.
        w.Db.EmailTemplateRings.Add(new EmailTemplateRing
        {
            EventId = EventId, TemplateKey = TravelReviewService.SentBackMailKey,
            ReleasedToRing = CommunityHub.Core.Settings.Ring.Broad,
        });
        await w.Db.SaveChangesAsync();
        var third = await w.Review.SendBackAsync(EventId, w.ClaimId, "org@crew.example", "Third try");
        Assert.True(third.Ok);
        Assert.Single(w.Mail.Sent);
    }

    [Fact]
    public async Task Send_back_copies_the_organizers_mailbox()
    {
        var w = await NewWorldAsync();
        w.Ai.Answer = NotApproved;
        await w.Review.ValidateAsync(EventId, w.ClaimId);

        await w.Review.SendBackAsync(EventId, w.ClaimId, "org@crew.example", "Please add the declaration");

        Assert.Equal(new[] { "ops@crew.example" }, Assert.Single(w.Mail.Details).Cc);   // "send copy of mail to info as cc"
        Assert.Equal(1, Assert.Single(w.Mail.Sent).Attachments);                         // "include attachments they submitted"
    }

    [Fact]
    public async Task The_review_needed_mail_carries_the_submitted_documents()
    {
        var w = await NewWorldAsync();
        w.Ai.Answer = NotApproved;
        await w.Review.OnSubmittedAsync(EventId, w.Pid);

        await w.Review.SweepAsync(EventId);

        Assert.Equal(1, Assert.Single(w.Mail.Sent).Attachments);
    }

    // ---- accept / override one requirement (operator 2026-11-08) --------------------------------

    [Fact]
    public async Task An_organizer_accepts_a_missing_line_it_reads_approved_by_override_and_the_speaker_is_not_asked_for_it()
    {
        var w = await NewWorldAsync();
        w.Ai.Answer = NotApproved;
        await w.Review.ValidateAsync(EventId, w.ClaimId);

        var r = await w.Review.AcceptCheckAsync(EventId, w.ClaimId, 1, "org@crew.example", "Business class, claimed at economy cap");

        Assert.True(r.Ok);
        var row = await w.Db.TravelReimbursements.SingleAsync();
        var checks = TravelReviewService.Checks(row);
        Assert.True(checks[1].IsAccepted);
        Assert.Equal("org@crew.example", checks[1].AcceptedBy);
        Assert.Equal("Approved (organizer override)", TravelReviewService.EffectiveVerdictText(row.AiVerdict, checks));
        Assert.False((await w.Review.AcceptCheckAsync(EventId, w.ClaimId, 0, "org@crew.example", null)).Ok);   // a met line has nothing to accept

        await w.Review.SendBackAsync(EventId, w.ClaimId, "org@crew.example", null);
        Assert.DoesNotContain("No declaration uploaded", Assert.Single(w.Mail.Details).Body);   // accepted = not the speaker's to fix

        Assert.True((await w.Review.UndoCheckAsync(EventId, w.ClaimId, 1, "org@crew.example")).Ok);
        Assert.False(TravelReviewService.Checks(await w.Db.TravelReimbursements.SingleAsync())[1].IsAccepted);
    }

    [Fact]
    public async Task An_acceptance_survives_a_re_check_of_the_same_requirement_and_a_failed_re_check_keeps_the_old_result()
    {
        var w = await NewWorldAsync();
        w.Ai.Answer = NotApproved;
        await w.Review.ValidateAsync(EventId, w.ClaimId);
        await w.Review.AcceptCheckAsync(EventId, w.ClaimId, 1, "org@crew.example", "ok");

        await w.Review.ValidateAsync(EventId, w.ClaimId);   // same answer again
        Assert.True(TravelReviewService.Checks(await w.Db.TravelReimbursements.SingleAsync())[1].IsAccepted);

        w.Ai.Status = HttpStatusCode.InternalServerError;
        await w.Review.ValidateAsync(EventId, w.ClaimId);
        var row = await w.Db.TravelReimbursements.SingleAsync();
        Assert.Equal(TravelAiVerdict.NotApproved, row.AiVerdict);            // previous verdict kept
        Assert.True(TravelReviewService.Checks(row)[1].IsAccepted);           // and the acceptance
        Assert.StartsWith("The re-check could not run", row.AiSummary);
    }

    [Fact]
    public async Task Finance_sees_every_requirement_and_the_organizers_overrides()
    {
        var w = await NewWorldAsync();
        w.Ai.Answer = "{\"verdict\":\"not_approved\",\"summary\":\"Business class booked.\",\"checks\":["
            + "{\"requirement\":\"Receipt with payment\",\"ok\":true,\"note\":null},"
            + "{\"requirement\":\"Economy class\",\"ok\":false,\"note\":\"Business class ticket\"},"
            + "{\"requirement\":\"Booked six weeks before\",\"ok\":null,\"note\":\"Booking date not shown\"}]}";
        await w.Review.OnSubmittedAsync(EventId, w.Pid);
        await w.Review.ValidateAsync(EventId, w.ClaimId);
        await w.Review.AcceptCheckAsync(EventId, w.ClaimId, 1, "org@crew.example", "Claimed at the economy cap");

        await w.Review.AcceptAsync(EventId, w.ClaimId, "org@crew.example", "Fine by us");

        var body = Assert.Single(w.Mail.Details).Body;
        Assert.Contains("Document review", body);
        Assert.Contains("Receipt with payment", body);
        Assert.Contains("Met", body);
        Assert.Contains("Accepted by organizer (was missing)", body);
        Assert.Contains("Claimed at the economy cap", body);
        Assert.Contains("In question", body);
        Assert.Contains("Fine by us", body);
        Assert.Contains("org@crew.example", body);
    }

    [Fact]
    public void A_claim_never_reviewed_has_no_review_section_for_finance() =>
        Assert.Equal("", TravelErpMailer.ReviewSection(new TravelReimbursement()));

    // ---- sweep -------------------------------------------------------------------------------

    [Fact]
    public async Task The_sweep_checks_new_submissions_and_tells_the_organizers_once_per_document_set()
    {
        var w = await NewWorldAsync();
        w.Ai.Answer = NotApproved;
        await w.Review.OnSubmittedAsync(EventId, w.Pid);

        Assert.Equal((1, 1), await w.Review.SweepAsync(EventId));
        Assert.Equal((0, 0), await w.Review.SweepAsync(EventId));   // same documents: no second call, no second mail
        Assert.Equal(1, w.Ai.Calls);

        var m = Assert.Single(w.Mail.Sent);
        Assert.Equal("ops@crew.example", m.To);
        Assert.True(m.Ctx!.RingExempt);
        Assert.Contains("Ada Speaker", m.Subject);

        // A new document = a new set: checked and (still not approved) told again.
        w.Db.TravelReceipts.Add(new TravelReceipt { EventId = EventId, ParticipantId = w.Pid, FileName = "decl.pdf",
            ContentType = "application/pdf", Content = new byte[] { 9 }, SizeBytes = 1, Kind = TravelDocumentKind.PersonalDeclaration });
        await w.Db.SaveChangesAsync();
        Assert.Equal((1, 1), await w.Review.SweepAsync(EventId));
    }

    [Fact]
    public async Task The_sweep_leaves_claims_finance_already_has_to_the_organizer_buttons()
    {
        var w = await NewWorldAsync();
        var row = await w.Db.TravelReimbursements.SingleAsync();
        row.ReviewState = TravelReviewState.UnderReview;
        row.ErpSentAt = row.SubmittedAt;
        await w.Db.SaveChangesAsync();

        Assert.Equal((0, 0), await w.Review.SweepAsync(EventId));
        Assert.Equal(0, w.Ai.Calls);
        Assert.Empty(w.Mail.Sent);
    }

    /// <summary>
    /// Operator 2026-11-08: "i must be able to see when new response from speaker come in, automatically validate all and send
    /// judgement to info of what to do, push back or accept" — an approved response is reported too, once, recommending Accept.
    /// </summary>
    [Fact]
    public async Task An_approved_response_is_reported_once_to_the_organizers_recommending_accept()
    {
        var w = await NewWorldAsync();
        w.Ai.Answer = "{\"verdict\":\"approved\",\"summary\":\"All requirements met\",\"checks\":[{\"requirement\":\"Receipt\",\"ok\":true}]}";
        await w.Review.OnSubmittedAsync(EventId, w.Pid);

        Assert.Equal((1, 1), await w.Review.SweepAsync(EventId));
        Assert.Equal((0, 0), await w.Review.SweepAsync(EventId));

        var m = Assert.Single(w.Mail.Sent);
        Assert.Equal("ops@crew.example", m.To);
        Assert.Contains("Accept", m.Subject);
        Assert.Contains($"claim-{w.ClaimId}", Assert.Single(w.Mail.Details).Body);   // "Open this claim" deep link
    }

    [Fact]
    public void The_recommendation_follows_the_open_lines()
    {
        TravelCheckItem Ok(string r) => new(r, true, null);
        TravelCheckItem Missing(string r) => new(r, false, "x");
        TravelCheckItem Question(string r) => new(r, null, "?");

        Assert.Equal("Accept", TravelReviewService.Recommendation(TravelAiVerdict.Approved, new[] { Ok("Receipt") }).Short);
        Assert.Equal("Accept after a look", TravelReviewService.Recommendation(TravelAiVerdict.Approved, new[] { Ok("Receipt"), Question("Class") }).Short);
        var sendBack = TravelReviewService.Recommendation(TravelAiVerdict.NotApproved, new[] { Missing("Invoice") });
        Assert.Equal("Send back", sendBack.Short);
        Assert.Contains("Invoice", sendBack.Detail);
        Assert.Equal("Accept", TravelReviewService.Recommendation(TravelAiVerdict.NotApproved,
            new[] { Missing("Class") with { AcceptedAt = DateTimeOffset.UnixEpoch, AcceptedBy = "o" } }).Short);   // overridden
        Assert.Equal("Check by hand", TravelReviewService.Recommendation(TravelAiVerdict.CouldNotCheck, Array.Empty<TravelCheckItem>()).Short);
    }

    // ---- dialog log, previews, reminder, speaker wording (operator 2026-11-08) ---------------------

    [Fact]
    public async Task Every_step_of_the_dialog_is_logged()
    {
        var w = await NewWorldAsync();
        w.Ai.Answer = NotApproved;
        await w.Review.OnSubmittedAsync(EventId, w.Pid);
        await w.Review.SweepAsync(EventId);                                                     // AI check + mail to info@
        await w.Review.AcceptCheckAsync(EventId, w.ClaimId, 1, "org@crew.example", "fine");
        await w.Review.SendBackAsync(EventId, w.ClaimId, "org@crew.example", "Thanks!");

        var kinds = (await w.Db.TravelReviewLog.OrderBy(l => l.Id).ToListAsync()).Select(l => l.Kind).ToList();
        Assert.Equal(new[]
        {
            TravelReviewLogKinds.Submitted, TravelReviewLogKinds.AiCheck, TravelReviewLogKinds.MailedOrganizers,
            TravelReviewLogKinds.FindingAccepted, TravelReviewLogKinds.SentBack, TravelReviewLogKinds.MailedSpeaker,
        }, kinds);
        var dialog = (await w.Review.DialogAsync(EventId))[w.ClaimId];
        Assert.Equal(TravelReviewLogKinds.MailedSpeaker, dialog[0].Kind);                       // newest first
        Assert.Contains("Thanks!", dialog.Single(d => d.Kind == TravelReviewLogKinds.SentBack).Text);
    }

    [Fact]
    public async Task The_send_back_preview_is_the_mail_that_is_sent_and_changes_nothing()
    {
        var w = await NewWorldAsync();
        w.Ai.Answer = NotApproved;
        await w.Review.ValidateAsync(EventId, w.ClaimId);

        var preview = await w.Review.PreviewSendBackAsync(EventId, w.ClaimId, "A kind note");

        Assert.NotNull(preview);
        Assert.Equal("ada@speaker.example", preview!.To);
        Assert.Equal(new[] { "ops@crew.example" }, preview.Cc);
        Assert.Equal(new[] { "ticket.png" }, preview.AttachmentNames);
        Assert.Empty(w.Mail.Sent);                                                             // nothing sent
        Assert.NotNull((await w.Db.TravelReimbursements.SingleAsync()).SubmittedAt);           // claim unchanged

        await w.Review.SendBackAsync(EventId, w.ClaimId, "org@crew.example", "A kind note");
        Assert.Equal(preview.Html, Assert.Single(w.Mail.Details).Body);                        // what you saw is what went out
    }

    [Fact]
    public async Task The_accept_preview_shows_the_finance_mail_without_sending_it()
    {
        var w = await NewWorldAsync();
        w.Ai.Answer = NotApproved;
        await w.Review.OnSubmittedAsync(EventId, w.Pid);
        await w.Review.ValidateAsync(EventId, w.ClaimId);

        var preview = await w.Review.PreviewAcceptAsync(EventId, w.ClaimId, "OK to pay", "org@crew.example");

        Assert.Equal(TravelErpMailer.ErpInbox, preview!.To);
        Assert.Contains("Document review", preview.Html);
        Assert.Contains("OK to pay", preview.Html);
        Assert.Empty(w.Mail.Sent);
        Assert.Equal(TravelReviewState.UnderReview, (await w.Db.TravelReimbursements.SingleAsync()).ReviewState);
    }

    [Fact]
    public async Task The_speaker_reads_the_kind_sentence_and_a_gentle_mail()
    {
        var w = await NewWorldAsync();
        w.Ai.Answer = "{\"verdict\":\"not_approved\",\"summary\":\"No invoice.\",\"checks\":["
            + "{\"requirement\":\"Invoice or declaration\",\"ok\":false,\"note\":\"No invoice\","
            + "\"speaker_note\":\"We've got your ticket, thank you! 🙂 We just have nowhere to send the money yet.\"}]}";
        await w.Review.ValidateAsync(EventId, w.ClaimId);

        await w.Review.SendBackAsync(EventId, w.ClaimId, "org@crew.example", null);

        var body = Assert.Single(w.Mail.Details).Body;
        Assert.Contains("nowhere to send the money", body);
        Assert.Contains("What we still need", body);
        Assert.DoesNotContain("What is missing", body);
        Assert.Contains("Thank you so much", body);
        // "they dont know where to find details of personal declaration or invoice" — the complete list, for THEIR document
        // (this speaker answered "no company": the declaration, never the company invoice).
        Assert.Contains("A personal declaration", body);
        Assert.Contains("IBAN, BIC/SWIFT", body);
        Assert.Contains("DK44197588", body);
        Assert.DoesNotContain("A company invoice", body);
    }

    [Fact]
    public void The_requirements_list_names_the_document_the_speaker_needs()
    {
        var company = TravelReviewService.RequirementsHtml(true, new DateOnly(2027, 1, 10));
        Assert.Contains("A company invoice", company);
        Assert.DoesNotContain("A personal declaration", company);
        Assert.Contains("10 January 2027", company);

        var unknown = TravelReviewService.RequirementsHtml(null, null, "https://hub.example/Forms/Travel#requirements");
        Assert.Contains("If you have a registered company: an invoice", unknown);
        Assert.Contains("No company? A personal declaration", unknown);
        Assert.Contains("href=\"https://hub.example/Forms/Travel#requirements\"", unknown);   // "so they can click"
    }

    [Fact]
    public async Task The_daily_reminder_lists_claims_waiting_more_than_two_days_once_a_day()
    {
        var w = await NewWorldAsync();
        Assert.False(await w.Review.RemindWaitingAsync(EventId));                              // nothing waiting yet
        var row = await w.Db.TravelReimbursements.SingleAsync();
        row.ReviewState = TravelReviewState.UnderReview;
        row.SubmittedAt = DateTimeOffset.Parse("2026-11-05T09:00:00Z");                         // 3 days before the clock
        await w.Db.SaveChangesAsync();

        Assert.True(await w.Review.RemindWaitingAsync(EventId));
        Assert.False(await w.Review.RemindWaitingAsync(EventId));                              // once a day

        var m = Assert.Single(w.Mail.Sent);
        Assert.Equal("ops@crew.example", m.To);
        Assert.True(m.Ctx!.RingExempt);
        Assert.Contains("Ada Speaker", Assert.Single(w.Mail.Details).Body);
    }
}
