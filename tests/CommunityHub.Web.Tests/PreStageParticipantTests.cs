using System.Security.Claims;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Reminders;
using CommunityHub.Pages.Organizer;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §941 — PRE-STAGE A PERSON FROM THE PARTICIPANTS PAGE.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-07: <i>"in the participant view, I would like to have a NEW button, which
/// is used to prestage a person and fill out the initial information like firstname, lastname,
/// email, state (active/inactive) and link to a role. make a simple form for that"</i>.</para>
///
/// <para>🔴 <b>Why the button is tested and not just the form.</b> The create form already existed
/// at <c>/Organizer/EditParticipant</c> with no id, and had for a long time — what was missing was
/// any way to REACH it. He reported the same thing twice before it was built, and both times the
/// gap was navigational, not functional. A test that only exercises the page model would have been
/// green throughout that entire period, so the link itself is asserted.</para>
///
/// <para>FAKE names and addresses only.</para>
/// </remarks>
public sealed class PreStageParticipantTests
{
    private const int EventId = 21;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"prestage-{Guid.NewGuid():N}")
            .Options);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-08-07T10:00:00Z");
    }

    /// <summary>Pre-staging with "send welcome" off must not send — so nothing here ever does.</summary>
    private sealed class NoOpEmailSender : IEmailSender
    {
        public Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task SendAsync(string toEmail, string subject, string htmlBody, IReadOnlyCollection<string>? cc, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task SendAsync(string toEmail, string subject, string htmlBody, string textBody, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task SendWithIcsAsync(string toEmail, string subject, string htmlBody, string icsContent, string icsFileName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task SendWithAttachmentsAsync(string toEmail, string subject, string htmlBody, IReadOnlyCollection<EmailAttachment> attachments, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class HttpContextAccessorOver(HttpContext ctx) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => ctx; set { } }
    }

    private static EditParticipantModel NewModel(CommunityHubDbContext db, DefaultHttpContext http)
    {
        var clock = new FixedClock();
        var templates = new EmailTemplateProvider(Options.Create(new EmailTemplateOptions()));
        return new EditParticipantModel(
            db,
            new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http)),
            new WelcomeEmailService(db, templates, new NoOpEmailSender(), clock),
            new AttendeeOneDayWelcomeEmailService(db, templates, new NoOpEmailSender(), clock),
            clock)
        {
            PageContext = new PageContext { HttpContext = http },
        };
    }

    private static ClaimsPrincipal OrganizerSession(Participant org) =>
        new(new ClaimsIdentity(
            new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, org.Id.ToString()),
                new(ClaimTypes.Email, org.Email),
                new(ClaimTypes.Name, org.FullName),
                new(ClaimTypes.Role, org.Role.ToString()),
                new("EventId", org.EventId.ToString()),
            },
            CookieAuthenticationDefaults.AuthenticationScheme));

    private static async Task<Participant> SeedOrganizerAsync(CommunityHubDbContext db)
    {
        var org = new Participant
        {
            EventId = EventId, Email = "org@example.test", FullName = "Org Person",
            Role = ParticipantRole.Organizer, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(org);
        await db.SaveChangesAsync();
        return org;
    }

    // ---------------------------------------------------------------- the form

    [Fact]
    public async Task Pre_stage_composes_first_and_last_name_into_the_stored_full_name()
    {
        using var db = NewDb();
        var org = await SeedOrganizerAsync(db);

        var model = NewModel(db, new DefaultHttpContext { User = OrganizerSession(org) });
        model.Id = null;                       // create
        model.FirstName = "Robin";
        model.LastName = "Solberg";
        model.Email = "robin.solberg@example.test";
        model.Role = ParticipantRole.Volunteer;
        model.IsActive = true;

        await model.OnPostAsync(default);

        var created = await db.Participants
            .FirstOrDefaultAsync(p => p.Email == "robin.solberg@example.test");
        Assert.NotNull(created);
        // 🔑 One stored name, no schema change — the two boxes are a form affordance, not a column.
        Assert.Equal("Robin Solberg", created!.FullName);
        Assert.Equal(ParticipantRole.Volunteer, created.Role);
        Assert.True(created.IsActive);
    }

    [Fact]
    public async Task Pre_staged_person_can_be_created_inactive()
    {
        using var db = NewDb();
        var org = await SeedOrganizerAsync(db);

        var model = NewModel(db, new DefaultHttpContext { User = OrganizerSession(org) });
        model.Id = null;
        model.FirstName = "Casey";
        model.LastName = "Bergman";
        model.Email = "casey.bergman@example.test";
        model.Role = ParticipantRole.Speaker;
        model.IsActive = false;                // "state (active/inactive)" is his wording

        await model.OnPostAsync(default);

        var created = await db.Participants
            .FirstOrDefaultAsync(p => p.Email == "casey.bergman@example.test");
        Assert.NotNull(created);
        Assert.False(created!.IsActive);
    }

    [Fact]
    public async Task Pre_stage_without_a_name_is_rejected_and_says_which_boxes_are_empty()
    {
        using var db = NewDb();
        var org = await SeedOrganizerAsync(db);

        var model = NewModel(db, new DefaultHttpContext { User = OrganizerSession(org) });
        model.Id = null;
        model.Email = "nameless@example.test";
        model.Role = ParticipantRole.Attendee;

        await model.OnPostAsync(default);

        Assert.False(await db.Participants.AnyAsync(p => p.Email == "nameless@example.test"));
        Assert.NotNull(model.Error);
        // ⚠️ The create form has no box labelled "Full name", so an error naming one would send the
        // organizer hunting for a field that is not on screen (§949 is the same complaint).
        Assert.Contains("First name", model.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Full name", model.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Editing_an_existing_person_still_uses_the_single_full_name_box()
    {
        using var db = NewDb();
        var org = await SeedOrganizerAsync(db);
        var target = new Participant
        {
            EventId = EventId, Email = "existing@example.test", FullName = "Robin van der Berg",
            Role = ParticipantRole.Attendee, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(target);
        await db.SaveChangesAsync();

        var model = NewModel(db, new DefaultHttpContext { User = OrganizerSession(org) });
        model.Id = target.Id;
        model.Email = "existing@example.test";
        model.FullName = "Robin van der Berg";
        model.Role = ParticipantRole.Attendee;
        model.IsActive = true;
        // 🔒 First/Last are left null, as the edit form does not post them. The composition must NOT
        // fire and blank the name — this is the regression that would silently rename everybody who
        // was ever edited.
        await model.OnPostAsync(default);

        var reloaded = await db.Participants.FindAsync(target.Id);
        Assert.Equal("Robin van der Berg", reloaded!.FullName);
    }

    // ---------------------------------------------------------------- the button

    [Fact]
    public void Participants_page_links_to_the_create_form()
    {
        var razor = ReadPage(Path.Combine("Organizer", "Participants.cshtml"));

        Assert.Contains("asp-page=\"/Organizer/EditParticipant\"", razor);
        Assert.Contains("New participant", razor);
    }

    [Fact]
    public void Create_form_asks_for_first_and_last_name_but_the_edit_form_does_not()
    {
        var razor = ReadPage(Path.Combine("Organizer", "EditParticipant.cshtml"));

        Assert.Contains("asp-for=\"FirstName\"", razor);
        Assert.Contains("asp-for=\"LastName\"", razor);
        // Both spellings still exist in the file — the point is that they are BRANCHED on IsNew,
        // not that one replaced the other.
        Assert.Contains("Model.IsNew", razor);
        Assert.Contains("asp-for=\"FullName\"", razor);
    }

    private static string ReadPage(string relative) =>
        File.ReadAllText(Path.Combine(FindDir("src", "CommunityHub", "Pages"), relative));

    private static string FindDir(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, Path.Combine(parts));
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            $"Could not locate {Path.Combine(parts)} from {AppContext.BaseDirectory}");
    }
}
