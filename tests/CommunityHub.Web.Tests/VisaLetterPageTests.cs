using System.Security.Claims;
using System.Text;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Visa;
using CommunityHub.Pages.Forms;
using CommunityHub.Pages.Organizer;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §1383 — the PDF letter per visa applicant, through the real page handlers: only a real organizer can
/// upload (a speaker gets 403 and nothing is stored), a non-PDF is refused with a message, and an
/// applicant downloads only THEIR letter (another applicant gets 404, nobody can name an id).
/// Fake data only.
/// </summary>
public sealed class VisaLetterPageTests
{
    private const int EventId = 71;

    private sealed class FakeAccessor(CurrentParticipant? cur) : ICurrentParticipantAccessor
    {
        public CurrentParticipant? Current { get; } = cur;
    }

    private sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object?> LoadTempData(HttpContext context) => new Dictionary<string, object?>();
        public void SaveTempData(HttpContext context, IDictionary<string, object?> values) { }
    }

    private static CurrentParticipant? SignedIn(Participant p) =>
        CurrentParticipant.FromPrincipal(new ClaimsPrincipal(new ClaimsIdentity(new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, p.Id.ToString()),
            new(ClaimTypes.Email, p.Email),
            new(ClaimTypes.Name, p.FullName),
            new(ClaimTypes.Role, p.Role.ToString()),
            new("EventId", p.EventId.ToString()),
        }, CookieAuthenticationDefaults.AuthenticationScheme)));

    private sealed record World(CommunityHubDbContext Db, Participant Organizer, Participant Asha, Participant Bo);

    private static async Task<World> SeedAsync()
    {
        var db = new CommunityHubDbContext(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"visa-letter-{Guid.NewGuid():N}").Options);
        db.Events.Add(new Event
        {
            Id = EventId, Code = "V27", CommunityName = "C", DisplayName = "V 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        });
        var org = new Participant { EventId = EventId, Email = "org@x.test", FullName = "Org", Role = ParticipantRole.Organizer, IsActive = true };
        var asha = new Participant { EventId = EventId, Email = "asha@x.test", FullName = "Asha Rao", Role = ParticipantRole.Speaker, IsActive = true, NeedsVisa = true };
        var bo = new Participant { EventId = EventId, Email = "bo@x.test", FullName = "Bo Berg", Role = ParticipantRole.Volunteer, IsActive = true, NeedsVisa = true };
        db.Participants.AddRange(org, asha, bo);
        await db.SaveChangesAsync();
        return new World(db, org, asha, bo);
    }

    private static HttpContext Http() => new DefaultHttpContext
    {
        RequestServices = new ServiceCollection().BuildServiceProvider(),
    };

    private static VisaApplicationsModel OrganizerPage(World w, Participant me)
    {
        var http = Http();
        return new VisaApplicationsModel(new FakeAccessor(SignedIn(me)), new VisaApplicationService(w.Db), w.Db,
            TimeProvider.System, new VisaLetterStore(w.Db))
        {
            PageContext = new PageContext { HttpContext = http },
            TempData = new TempDataDictionary(http, new NullTempDataProvider()),
        };
    }

    private static VisaModel ApplicantPage(World w, Participant me) =>
        new(new FakeAccessor(SignedIn(me)), new VisaApplicationService(w.Db), new VisaLetterStore(w.Db))
        {
            PageContext = new PageContext { HttpContext = Http() },
        };

    private static IFormFile Upload(byte[] bytes, string name) =>
        new FormFile(new MemoryStream(bytes), 0, bytes.Length, "Letter", name)
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/pdf",
        };

    private static byte[] Pdf() => Encoding.ASCII.GetBytes("%PDF-1.4\n%fake letter for tests\n%%EOF\n");

    [Fact]
    public async Task A_non_organizer_cannot_upload_a_letter()
    {
        var w = await SeedAsync();
        var page = OrganizerPage(w, w.Asha);
        page.ParticipantId = w.Asha.Id;
        page.Letter = Upload(Pdf(), "mine.pdf");

        var result = await page.OnPostLetterAsync(CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
        Assert.Empty(w.Db.VisaLetters);
    }

    [Fact]
    public async Task An_organizer_uploads_a_pdf_and_a_non_pdf_is_refused_with_a_message()
    {
        var w = await SeedAsync();
        var page = OrganizerPage(w, w.Organizer);
        page.ParticipantId = w.Asha.Id;
        page.Letter = Upload(Encoding.ASCII.GetBytes("MZ not a pdf at all"), "letter.pdf");

        await page.OnPostLetterAsync(CancellationToken.None);
        Assert.Empty(w.Db.VisaLetters);
        Assert.Contains("not a PDF", page.TempData["VisaMessage"] as string);

        page.Letter = Upload(Pdf(), "Invitation Letter.pdf");
        var result = await page.OnPostLetterAsync(CancellationToken.None);

        Assert.IsType<RedirectToPageResult>(result);
        var row = await w.Db.VisaLetters.SingleAsync();
        Assert.Equal(w.Asha.Id, row.ParticipantId);
        Assert.Equal("Invitation Letter.pdf", row.FileName);
    }

    [Fact]
    public async Task An_applicant_downloads_only_their_own_letter()
    {
        var w = await SeedAsync();
        await new VisaLetterStore(w.Db).UploadAsync(EventId, w.Asha.Id, "asha.pdf", Pdf());

        var mine = await ApplicantPage(w, w.Asha).OnGetLetterAsync(CancellationToken.None);
        var file = Assert.IsType<FileContentResult>(mine);
        Assert.Equal("application/pdf", file.ContentType);
        Assert.Equal("asha.pdf", file.FileDownloadName);

        // Bo is flagged too but has no letter: he does not get Asha's.
        Assert.IsType<NotFoundResult>(await ApplicantPage(w, w.Bo).OnGetLetterAsync(CancellationToken.None));
        // Someone not flagged for a visa gets the form's 404.
        Assert.IsType<NotFoundResult>(await ApplicantPage(w, w.Organizer).OnGetLetterAsync(CancellationToken.None));
    }
}
