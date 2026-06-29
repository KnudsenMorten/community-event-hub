using System.Security.Claims;
using System.Text;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations.Graphics;
using CommunityHub.Core.Reminders;
using CommunityHub.Pages.Organizer;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §166 page-handler path: the organizer "Final evaluation PDFs" upload on
/// <see cref="SessionEvaluationsModel"/>. Drives the real POST over a fake organizer session
/// with a FAKE SharePoint store (no network) and proves: a successful upload sets
/// <see cref="Session.EvaluationFormUrl"/> to the HUB PROXY url (NEVER a SharePoint URL) and
/// emails the session's speaker(s); and that an unconfigured store reports a clear note and
/// changes nothing. FAKE names only.
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
        public List<(string To, string Subject)> Sent { get; } = new();
        public Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default)
        { Sent.Add((toEmail, subject)); return Task.CompletedTask; }
        public Task SendAsync(string toEmail, string subject, string htmlBody, IReadOnlyCollection<string>? cc, CancellationToken ct = default)
        { Sent.Add((toEmail, subject)); return Task.CompletedTask; }
        public Task SendAsync(string toEmail, string subject, string htmlBody, string textBody, CancellationToken ct = default)
        { Sent.Add((toEmail, subject)); return Task.CompletedTask; }
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
        return new SessionEvaluationsModel(accessor, eval, pdf, db, sender, NullLogger<SessionEvaluationsModel>.Instance)
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
    public async Task Upload_sets_the_proxy_url_and_emails_the_speaker()
    {
        using var db = NewDb();
        var (org, session) = await SeedAsync(db);
        var store = new FakePdfStore(canRead: true, canStore: true);
        var sender = new CapturingSender();

        var http = new DefaultHttpContext { User = OrganizerSession(org) };
        var model = NewModel(db, http, store, sender);
        model.UploadSessionId = session.Id;
        model.Pdf = PdfUpload();

        await model.OnPostUploadPdfAsync(default);

        var reloaded = await db.Sessions.FindAsync(session.Id);
        Assert.Equal($"/session-eval/{session.Id}/download", reloaded!.EvaluationFormUrl); // HUB proxy, not SharePoint
        Assert.DoesNotContain("sharepoint", reloaded.EvaluationFormUrl!, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(reloaded.EvaluationEmailedAt);

        // The deterministic file landed in the folder, and the speaker was emailed.
        Assert.Equal($"session-{session.Id}.pdf", Assert.Single(store[Folder]).Name);
        var sent = Assert.Single(sender.Sent);
        Assert.Equal("speaker@example.test", sent.To);
        Assert.Contains("evaluation is ready", sent.Subject, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(model.PdfMessage);
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
        model.Pdf = PdfUpload();

        await model.OnPostUploadPdfAsync(default);

        var reloaded = await db.Sessions.FindAsync(session.Id);
        Assert.Null(reloaded!.EvaluationFormUrl);     // nothing set
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

        var bytes = Encoding.ASCII.GetBytes("not a pdf");
        model.Pdf = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "Pdf", "notes.txt")
        {
            Headers = new HeaderDictionary(),
            ContentType = "text/plain",
        };

        await model.OnPostUploadPdfAsync(default);

        Assert.Null((await db.Sessions.FindAsync(session.Id))!.EvaluationFormUrl);
        Assert.Empty(store[Folder]);
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
            throw new InvalidOperationException("root store not used by §166");
        public Task DeleteAsync(string relativePath, CancellationToken ct = default) => Task.CompletedTask;
    }
}
