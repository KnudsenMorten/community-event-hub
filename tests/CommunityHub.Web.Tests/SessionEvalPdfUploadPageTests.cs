using System.Security.Claims;
using System.Text;
using CommunityHub.Auth;
using CommunityHub.Core.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations.Graphics;
using CommunityHub.Core.Reminders;
using CommunityHub.Pages.Organizer;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §192 (reworking §166) page-handler path: the organizer per-session upload on
/// <see cref="SessionEvaluationsModel"/>. Drives the real POST over a fake organizer session
/// with a FAKE SharePoint store (no network) and proves: a SCORE and an OPEN-feedback upload
/// land in the folder under their kind-tagged deterministic names, record PROVENANCE
/// (who/when), surface per-session in the organizer list, email the session's speaker(s); a
/// missing kind / unconfigured store / non-PDF is rejected with a clear note and changes
/// nothing. FAKE names only.
/// </summary>
public sealed class SessionEvalPdfUploadPageTests
{
    private const int EventId = 31;
    private const string Folder = "General/Events/ELDK 2027/EventHub/Speakers/SessionEvals-PDF";

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"evalpdf-page-{Guid.NewGuid():N}")
            .Options);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-06-28T10:00:00Z");
    }

    private sealed class HttpContextAccessorOver(HttpContext ctx) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => ctx; set { } }
    }

    /// <summary>Captures sends instead of hitting SMTP.</summary>
    private sealed class CapturingSender : IEmailSender
    {
        public List<(string To, string Subject, string Html)> Sent { get; } = new();
        public Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default)
        { Sent.Add((toEmail, subject, htmlBody)); return Task.CompletedTask; }
        public Task SendAsync(string toEmail, string subject, string htmlBody, IReadOnlyCollection<string>? cc, CancellationToken ct = default)
        { Sent.Add((toEmail, subject, htmlBody)); return Task.CompletedTask; }
        public Task SendAsync(string toEmail, string subject, string htmlBody, string textBody, CancellationToken ct = default)
        { Sent.Add((toEmail, subject, htmlBody)); return Task.CompletedTask; }
        public Task SendWithIcsAsync(string toEmail, string subject, string htmlBody, string ics, string icsName, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task SendWithAttachmentsAsync(string toEmail, string subject, string htmlBody, IReadOnlyCollection<EmailAttachment> a, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private static ClaimsPrincipal OrganizerSession(Participant org)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, org.Id.ToString()),
            new(ClaimTypes.Email, org.Email),
            new(ClaimTypes.Name, org.FullName),
            new(ClaimTypes.Role, org.Role.ToString()),
            new("EventId", org.EventId.ToString()),
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    private static SessionEvaluationsModel NewModel(
        CommunityHubDbContext db, DefaultHttpContext http, ISharePointFileStore store, CapturingSender sender, string folder = Folder)
    {
        var accessor = new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http));
        var eval = new SessionEvaluationService(db, new FixedClock());
        var pdf = new SessionEvalPdfService(store, Options.Create(new GraphicsSharePointOptions
        {
            Enabled = true,
            SiteUrl = "https://contoso.sharepoint.example.test/sites/eldk",
            SessionEvalPdfFolderPath = folder,
        }), db);
        var magic = new EmailMagicLinkService(
            db,
            DataProtectionProvider.Create(
                new DirectoryInfo(Path.Combine(Path.GetTempPath(), "ceh-dp-evalpdf-tests"))),
            new FixedClock());
        return new SessionEvaluationsModel(accessor, eval, pdf, db, sender, magic, NullLogger<SessionEvaluationsModel>.Instance)
        {
            PageContext = new PageContext { HttpContext = http },
        };
    }

    private static IFormFile PdfUpload(string name = "eval.pdf")
    {
        var bytes = Encoding.ASCII.GetBytes("%PDF-1.4 fake");
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "Pdf", name)
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/pdf",
        };
    }

    private static async Task<(Participant org, Session session)> SeedAsync(CommunityHubDbContext db)
    {
        var org = new Participant { EventId = EventId, Email = "org@example.test", FullName = "Org Person", Role = ParticipantRole.Organizer };
        var speaker = new Participant { EventId = EventId, Email = "speaker@example.test", FullName = "Session Speaker", Role = ParticipantRole.Speaker };
        db.Participants.AddRange(org, speaker);
        await db.SaveChangesAsync();

        var session = new Session { EventId = EventId, Title = "Zero Trust 101", Type = SessionType.TechnicalSession };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();
        db.SessionSpeakers.Add(new SessionSpeaker { SessionId = session.Id, ParticipantId = speaker.Id });
        await db.SaveChangesAsync();
        return (org, session);
    }

    [Fact]
    public async Task Score_upload_stores_the_kind_file_records_provenance_and_emails_the_speaker()
    {
        using var db = NewDb();
        var (org, session) = await SeedAsync(db);
        var store = new FakePdfStore(canRead: true, canStore: true);
        var sender = new CapturingSender();

        var http = new DefaultHttpContext { User = OrganizerSession(org) };
        var model = NewModel(db, http, store, sender);
        model.UploadSessionId = session.Id;
        model.UploadKind = "score";
        model.Pdf = PdfUpload();

        await model.OnPostUploadPdfAsync(default);

        // The kind-tagged deterministic file landed (§299 OPEN-30 CehId-prefixed name);
        // nothing leaks a SharePoint URL.
        Assert.Equal($"{session.Id}-scores.pdf", Assert.Single(store[Folder]).Name);

        // Provenance recorded (who/when) — §192c.
        var prov = Assert.Single(db.SessionEvaluationFiles.Where(f => f.SessionId == session.Id));
        Assert.Equal(EvaluationPdfKind.Score, prov.Kind);
        Assert.Equal("Org Person", prov.UploadedByName);
        Assert.Equal(org.Id, prov.UploadedByParticipantId);

        // Speaker emailed + the "results emailed" marker stamped.
        Assert.NotNull((await db.Sessions.FindAsync(session.Id))!.EvaluationEmailedAt);
        var sent = Assert.Single(sender.Sent);
        Assert.Equal("speaker@example.test", sent.To);
        Assert.Contains("evaluation is ready", sent.Subject, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(model.PdfMessage);

        // The organizer list surfaces the score file (per-session) with its provenance.
        var row = Assert.Single(model.PdfSessions);
        Assert.NotNull(row.Score);
        Assert.Equal("Org Person", row.Score!.UploadedByName);
        Assert.Null(row.Open);
    }

    [Fact]
    public async Task Score_and_open_feedback_upload_independently_into_two_files()
    {
        using var db = NewDb();
        var (org, session) = await SeedAsync(db);
        var store = new FakePdfStore(canRead: true, canStore: true);
        var sender = new CapturingSender();
        var http = new DefaultHttpContext { User = OrganizerSession(org) };

        var m1 = NewModel(db, http, store, sender);
        m1.UploadSessionId = session.Id; m1.UploadKind = "score"; m1.Pdf = PdfUpload();
        await m1.OnPostUploadPdfAsync(default);

        var m2 = NewModel(db, http, store, sender);
        m2.UploadSessionId = session.Id; m2.UploadKind = "feedback"; m2.Pdf = PdfUpload();
        await m2.OnPostUploadPdfAsync(default);

        var names = store[Folder].Select(f => f.Name).OrderBy(n => n).ToArray();
        // §299 OPEN-30: {CehId}-scores.pdf (mandatory) + {CehId}-openfeedback.pdf (optional).
        Assert.Equal(new[] { $"{session.Id}-openfeedback.pdf", $"{session.Id}-scores.pdf" }, names);

        var row = Assert.Single(m2.PdfSessions);
        Assert.NotNull(row.Score);
        Assert.NotNull(row.Open);
    }

    [Fact]
    public async Task Speaker_email_uses_a_go_magic_link_not_a_bare_speaker_url()
    {
        // §190: the "Open My Sessions" CTA must auto-sign-in — it must route through the
        // speaker's personal /go/{token} magic-link (deep-linking to /Speaker), NOT a
        // bare {host}/Speaker URL that dumps a signed-out speaker on /Login.
        using var db = NewDb();
        var (org, session) = await SeedAsync(db);
        var store = new FakePdfStore(canRead: true, canStore: true);
        var sender = new CapturingSender();

        var http = new DefaultHttpContext { User = OrganizerSession(org) };
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("hub.example.test");
        var model = NewModel(db, http, store, sender);
        model.UploadSessionId = session.Id;
        model.UploadKind = "score";
        model.Pdf = PdfUpload();

        await model.OnPostUploadPdfAsync(default);

        var sent = Assert.Single(sender.Sent);
        // The CTA is the speaker's auto-login magic-link deep-linking to /Speaker…
        Assert.Contains("href=\"https://hub.example.test/go/", sent.Html);
        Assert.Contains("r=%2FSpeaker", sent.Html);              // deep-link target = /Speaker
        // …and never a bare /Speaker hub link that would land on /Login.
        Assert.DoesNotContain("href=\"https://hub.example.test/Speaker\"", sent.Html);
        // §191: white button text is forced so dark-mode clients cannot darken it.
        Assert.Contains("color:#ffffff !important", sent.Html);
    }

    [Fact]
    public async Task Upload_without_a_kind_is_rejected()
    {
        using var db = NewDb();
        var (org, session) = await SeedAsync(db);
        var store = new FakePdfStore(canRead: true, canStore: true);
        var sender = new CapturingSender();

        var http = new DefaultHttpContext { User = OrganizerSession(org) };
        var model = NewModel(db, http, store, sender);
        model.UploadSessionId = session.Id;
        model.UploadKind = null;
        model.Pdf = PdfUpload();

        await model.OnPostUploadPdfAsync(default);

        Assert.Empty(store[Folder]);
        Assert.Empty(db.SessionEvaluationFiles);
        Assert.NotNull(model.PdfError);
    }

    [Fact]
    public async Task Upload_is_inert_with_a_clear_note_when_the_folder_is_not_configured()
    {
        using var db = NewDb();
        var (org, session) = await SeedAsync(db);
        // Store reports it cannot store → the service's CanManage is false.
        var store = new FakePdfStore(canRead: false, canStore: false);
        var sender = new CapturingSender();

        var http = new DefaultHttpContext { User = OrganizerSession(org) };
        var model = NewModel(db, http, store, sender);
        model.UploadSessionId = session.Id;
        model.UploadKind = "score";
        model.Pdf = PdfUpload();

        await model.OnPostUploadPdfAsync(default);

        Assert.Empty(db.SessionEvaluationFiles);      // nothing recorded
        Assert.Empty(sender.Sent);                    // nothing emailed
        Assert.NotNull(model.PdfError);               // a clear "not configured" note
    }

    [Fact]
    public async Task Non_pdf_upload_is_rejected()
    {
        using var db = NewDb();
        var (org, session) = await SeedAsync(db);
        var store = new FakePdfStore(canRead: true, canStore: true);
        var sender = new CapturingSender();

        var http = new DefaultHttpContext { User = OrganizerSession(org) };
        var model = NewModel(db, http, store, sender);
        model.UploadSessionId = session.Id;
        model.UploadKind = "score";

        var bytes = Encoding.ASCII.GetBytes("not a pdf");
        model.Pdf = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "Pdf", "notes.txt")
        {
            Headers = new HeaderDictionary(),
            ContentType = "text/plain",
        };

        await model.OnPostUploadPdfAsync(default);

        Assert.Empty(store[Folder]);
        Assert.Empty(db.SessionEvaluationFiles);
        Assert.NotNull(model.PdfError);
    }

    private static SharePointFileRef File(string name) =>
        new("item-" + name, name, "https://store.example.test/" + name);

    private sealed class FakePdfStore : ISharePointFileStore
    {
        private readonly Dictionary<string, List<SharePointFileRef>> _byFolder = new(StringComparer.OrdinalIgnoreCase);
        public FakePdfStore(bool canRead, bool canStore) { CanRead = canRead; CanStore = canStore; }

        public List<SharePointFileRef> this[string folder]
        {
            get
            {
                if (!_byFolder.TryGetValue(folder, out var list)) { list = new(); _byFolder[folder] = list; }
                return list;
            }
        }

        public bool CanRead { get; }
        public bool CanStore { get; }

        public Task<IReadOnlyList<SharePointFileRef>> ListAsync(string relativeFolder, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SharePointFileRef>>(
                CanRead && _byFolder.TryGetValue(relativeFolder, out var list) ? list.ToList() : new List<SharePointFileRef>());

        public Task<byte[]?> DownloadAsync(string itemId, CancellationToken ct = default) =>
            Task.FromResult<byte[]?>(CanRead ? new byte[] { 1, 2, 3 } : null);

        public Task<StoredFile> UploadToFolderAsync(string relativeFolder, string fileName, byte[] content, string contentType, CancellationToken ct = default)
        {
            if (!CanStore) throw new InvalidOperationException("cannot write");
            this[relativeFolder].RemoveAll(f => f.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase));
            this[relativeFolder].Add(File(fileName));
            return Task.FromResult(new StoredFile($"{relativeFolder}/{fileName}", "https://store.example.test/" + fileName, "item-" + fileName));
        }

        public Task DeleteFromFolderAsync(string relativeFolder, string fileName, CancellationToken ct = default)
        {
            this[relativeFolder].RemoveAll(f => f.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase));
            return Task.CompletedTask;
        }

        public Task<StoredFile> StoreAsync(string relativePath, byte[] content, string contentType, CancellationToken ct = default) =>
            throw new InvalidOperationException("root store not used by §192");
        public Task DeleteAsync(string relativePath, CancellationToken ct = default) => Task.CompletedTask;
    }
}
