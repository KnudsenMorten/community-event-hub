using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Tests.Scenario;
using CommunityHub.Core.Visa;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1383 — (1) a submitted visa application tells the organizers' mailbox (info@ by default), once on the
/// first send and again only when a later save changes an answer; (2) the PDF letter an organizer hands
/// back: a real PDF header is required, oversize is refused, and each applicant reads only their own row.
/// Fake data only.
/// </summary>
public sealed class VisaNotifyAndLetterTests
{
    private const int EventId = 1;

    private sealed class CapturingSender : IEmailSender
    {
        private readonly IEmailContextAccessor _ctx;
        public CapturingSender(IEmailContextAccessor ctx) => _ctx = ctx;
        public bool Throw { get; set; }
        public readonly List<(string To, string Subject, string Html, EmailReplyTo? ReplyTo, EmailContext? Ctx)> Sent = new();

        public Task SendAsync(string to, string s, string h, EmailReplyTo? replyTo, CancellationToken ct = default)
        {
            if (Throw) throw new InvalidOperationException("smtp down");
            Sent.Add((to, s, h, replyTo, _ctx.Current));
            return Task.CompletedTask;
        }
        public Task SendAsync(string to, string s, string h, CancellationToken ct = default) => SendAsync(to, s, h, (EmailReplyTo?)null, ct);
        public Task SendAsync(string to, string s, string h, IReadOnlyCollection<string>? cc, CancellationToken ct = default) => SendAsync(to, s, h, ct);
        public Task SendAsync(string to, string s, string h, string t, CancellationToken ct = default) => SendAsync(to, s, h, ct);
        public Task SendWithIcsAsync(string to, string s, string h, string ics, string fn, CancellationToken ct = default) => SendAsync(to, s, h, ct);
        public Task SendWithAttachmentsAsync(string to, string s, string h, IReadOnlyCollection<EmailAttachment> a, CancellationToken ct = default) => SendAsync(to, s, h, ct);
    }

    private static async Task<(CommunityHubDbContext Db, Participant Flagged, Participant Other)> SeedAsync()
    {
        var db = new CommunityHubDbContext(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"visa1383-{Guid.NewGuid():N}").Options);
        db.Events.Add(new Event
        {
            Id = EventId, Code = "ELDK27", CommunityName = "ELDK", DisplayName = "ELDK 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        });
        var flagged = new Participant { EventId = EventId, Email = "spk@example.com", FullName = "Asha Rao", Role = ParticipantRole.Speaker, NeedsVisa = true };
        var other = new Participant { EventId = EventId, Email = "vol@example.com", FullName = "Bo Berg", Role = ParticipantRole.Volunteer, NeedsVisa = true };
        db.Participants.AddRange(flagged, other);
        await db.SaveChangesAsync();
        return (db, flagged, other);
    }

    private static (VisaApplicationService Svc, CapturingSender Sender) Build(CommunityHubDbContext db, string? notifyTo = null)
    {
        var ctx = new EmailContextAccessor();
        var sender = new CapturingSender(ctx);
        var templates = new EmailTemplateProvider(Options.Create(
            new EmailTemplateOptions { TemplateDirectory = RepoPaths.EmailTemplates() }));
        var options = notifyTo is null ? new VisaNotifyOptions() : new VisaNotifyOptions { NotifyEmailTo = notifyTo };
        var notifier = new VisaNotifier(db, templates, sender, ctx, options);
        return (new VisaApplicationService(db, notifier: notifier), sender);
    }

    private static VisaApplication Complete() => new()
    {
        FirstNames = "Asha", LastName = "Rao", DateOfBirth = new DateOnly(1990, 5, 1), Gender = "Female",
        Nationality = "IN", HomeStreet = "MG Road 1", HomePostalCode = "560001", HomeCity = "Bengaluru",
        HomeCountry = "IN", Phone = "+91 1234", Email = "asha@example.com",
        CompanyName = "Contoso", CompanyStreet = "Tech Park 2", CompanyPostalCode = "560002",
        CompanyCity = "Bengaluru", CompanyCountry = "IN", CompanyEmail = "info@contoso.example",
        CompanyWebsite = "https://contoso.example", DepartmentAndRole = "Cloud architect",
        VisitedBefore = false, CompanyActivity = "IT consulting", ContactOrigin = "Call for speakers",
        RelationshipDuration = "This is the first contact", FinancialScope = "None",
        ArrivalDate = new DateOnly(2027, 2, 7), LengthOfVisitDays = 5, Entries = "Single",
        Purpose = "Speaking at the conference.", TravelPaidBy = "My employer", Accommodation = "Conference hotel",
    };

    private static byte[] Pdf(int size = 400)
    {
        var bytes = new byte[size];
        var head = Encoding.ASCII.GetBytes("%PDF-1.7\n%fake test letter\n");
        Array.Copy(head, bytes, head.Length);
        return bytes;
    }

    // ---------- (1) the notice to the organizers' mailbox ----------

    [Fact]
    public async Task The_first_submit_mails_info_once_ring_exempt_with_reply_to_the_applicant()
    {
        var (db, flagged, _) = await SeedAsync();
        var (svc, sender) = Build(db);

        Assert.Empty(await svc.SubmitAsync(EventId, flagged.Id, Complete()));

        var mail = Assert.Single(sender.Sent);
        Assert.Equal("info@expertslive.dk", mail.To);
        Assert.Contains("Visa application submitted: Asha Rao", mail.Subject);
        Assert.Equal(VisaNotifier.MailKey, mail.Ctx?.TemplateName);
        Assert.True(mail.Ctx?.RingExempt);
        Assert.Equal("spk@example.com", mail.ReplyTo?.Email);
        Assert.Contains($"/Organizer/VisaApplications?Show={flagged.Id}", mail.Html);
        // No passport-grade data in the mail: the answers stay in the hub.
        Assert.DoesNotContain("1990", mail.Html);
        Assert.DoesNotContain("MG Road", mail.Html);
    }

    [Fact]
    public async Task A_resubmit_that_changes_an_answer_mails_updated_naming_the_field_not_its_value()
    {
        var (db, flagged, _) = await SeedAsync();
        var (svc, sender) = Build(db);
        await svc.SubmitAsync(EventId, flagged.Id, Complete());

        var edit = Complete();
        edit.HomeStreet = "Brigade Road 9";
        Assert.Empty(await svc.SubmitAsync(EventId, flagged.Id, edit));

        Assert.Equal(2, sender.Sent.Count);
        var mail = sender.Sent[1];
        Assert.Contains("Visa application updated: Asha Rao", mail.Subject);
        Assert.Contains("Home address (street and number)", mail.Html);
        Assert.DoesNotContain("Brigade Road", mail.Html);
    }

    [Fact]
    public async Task A_resubmit_with_the_same_answers_sends_nothing()
    {
        var (db, flagged, _) = await SeedAsync();
        var (svc, sender) = Build(db);
        await svc.SubmitAsync(EventId, flagged.Id, Complete());

        Assert.Empty(await svc.SubmitAsync(EventId, flagged.Id, Complete()));

        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task A_refused_submit_sends_nothing()
    {
        var (db, flagged, _) = await SeedAsync();
        var (svc, sender) = Build(db);
        var input = Complete();
        input.LastName = null;

        Assert.NotEmpty(await svc.SubmitAsync(EventId, flagged.Id, input));

        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task A_mail_failure_does_not_fail_the_applicants_save()
    {
        var (db, flagged, _) = await SeedAsync();
        var (svc, sender) = Build(db);
        sender.Throw = true;

        Assert.Empty(await svc.SubmitAsync(EventId, flagged.Id, Complete()));

        Assert.NotNull((await db.VisaApplications.SingleAsync()).SubmittedAt);
    }

    [Fact]
    public async Task A_blank_notify_address_turns_the_notice_off()
    {
        var (db, flagged, _) = await SeedAsync();
        var (svc, sender) = Build(db, notifyTo: " ");

        Assert.Empty(await svc.SubmitAsync(EventId, flagged.Id, Complete()));

        Assert.Empty(sender.Sent);
    }

    [Fact]
    public void The_notice_is_registered_as_a_ring_exempt_organizer_mailbox_mail()
    {
        Assert.Equal("visa-application-submitted", VisaNotifier.MailKey);
        Assert.True(EmailTemplateCatalog.Map.ContainsKey(VisaNotifier.MailKey));
        Assert.True(EmailTemplateCatalog.IsRingExempt(VisaNotifier.MailKey));
        Assert.True(EmailTemplateCatalog.IsInternalMailboxMail(VisaNotifier.MailKey));
        Assert.Equal(EmailAudience.Organizer, EmailTemplateCatalog.AudienceFor(VisaNotifier.MailKey));
        Assert.Contains("info@", EmailTemplateCatalog.RecipientHint(VisaNotifier.MailKey));
    }

    // ---------- (2) the PDF letter ----------

    [Fact]
    public async Task An_upload_with_a_real_pdf_header_is_stored_and_a_new_one_replaces_it()
    {
        var (db, flagged, _) = await SeedAsync();
        var store = new VisaLetterStore(db);

        Assert.Null(await store.UploadAsync(EventId, flagged.Id, @"C:\tmp\Invitation letter.pdf", Pdf(400)));
        Assert.Null(await store.UploadAsync(EventId, flagged.Id, "second.pdf", Pdf(500)));

        var row = await db.VisaLetters.SingleAsync();
        Assert.Equal("second.pdf", row.FileName);
        Assert.Equal(500, row.SizeBytes);
        Assert.Equal("Invitation letter.pdf", VisaLetterStore.CleanName(@"C:\tmp\Invitation letter.pdf"));
    }

    [Fact]
    public async Task A_non_pdf_is_refused_even_when_named_pdf()
    {
        var (db, flagged, _) = await SeedAsync();
        var store = new VisaLetterStore(db);
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };

        var problem = await store.UploadAsync(EventId, flagged.Id, "letter.pdf", png);

        Assert.Contains("not a PDF", problem);
        Assert.Empty(db.VisaLetters);
        Assert.NotNull(await store.UploadAsync(EventId, flagged.Id, "empty.pdf", Array.Empty<byte>()));
    }

    [Fact]
    public async Task An_oversize_file_is_refused()
    {
        var (db, flagged, _) = await SeedAsync();
        var store = new VisaLetterStore(db);

        var problem = await store.UploadAsync(EventId, flagged.Id, "big.pdf", Pdf((int)VisaLetterStore.MaxBytes + 1));

        Assert.Contains("too large", problem);
        Assert.Empty(db.VisaLetters);
    }

    [Fact]
    public async Task A_letter_is_only_for_someone_on_the_visa_list()
    {
        var (db, _, _) = await SeedAsync();
        var outsider = new Participant { EventId = EventId, Email = "att@example.com", FullName = "Cy Dahl", Role = ParticipantRole.Attendee };
        db.Participants.Add(outsider);
        await db.SaveChangesAsync();

        var problem = await new VisaLetterStore(db).UploadAsync(EventId, outsider.Id, "x.pdf", Pdf());

        Assert.NotNull(problem);
        Assert.Empty(db.VisaLetters);
    }

    [Fact]
    public async Task Each_applicant_reads_only_their_own_letter()
    {
        var (db, flagged, other) = await SeedAsync();
        var store = new VisaLetterStore(db);
        await store.UploadAsync(EventId, flagged.Id, "asha.pdf", Pdf());

        Assert.Equal("asha.pdf", (await store.GetAsync(EventId, flagged.Id))?.FileName);
        Assert.Null(await store.GetAsync(EventId, other.Id));
        Assert.Null(await store.GetAsync(EventId + 1, flagged.Id));   // another edition
        Assert.True(await store.RemoveAsync(EventId, flagged.Id));
        Assert.Null(await store.InfoAsync(EventId, flagged.Id));
    }
}
