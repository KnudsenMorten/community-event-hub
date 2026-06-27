using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using CommunityHub.Pages.Volunteer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// The anonymous volunteer sign-up is a 3-step wizard (operator 2026-06-23):
/// about-you → availability → collaboration agreement. Submitting creates the
/// pending applicant, records per-day availability + a VolunteerAvailability row
/// (LinkedIn / agreement timestamp), and requires the agreement to be accepted.
/// Drives the real <see cref="SignupModel"/> over an in-memory DB. FAKE names only.
/// </summary>
public sealed class VolunteerSignupSurveyTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 23, 9, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class NoOpEmail : IEmailSender
    {
        public Task SendAsync(string to, string s, string h, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendAsync(string to, string s, string h, string t, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendAsync(string to, string s, string h, IReadOnlyCollection<string>? cc, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendWithIcsAsync(string to, string s, string h, string ics, string fn, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendWithAttachmentsAsync(string to, string s, string h, IReadOnlyCollection<EmailAttachment> attachments, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"vol-signup-{Guid.NewGuid():N}")
            .Options);

    private static readonly DateOnly PreDay = new(2027, 2, 9);
    private static readonly DateOnly MainDay = new(2027, 2, 10);

    private static async Task<int> SeedEventAsync(CommunityHubDbContext db)
    {
        var ev = new Event
        {
            CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true,
            StartDate = MainDay, EndDate = MainDay, PreDayDate = PreDay,
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        return ev.Id;
    }

    private static SignupModel NewModel(CommunityHubDbContext db) =>
        new(db, new FixedClock(),
            new EventEditionConfigLoader(),
            new EventConfigOptions { EventConfigPath = "config/does-not-exist.json" }, // empty config => no extra days
            new SharePointUploadClient(new HttpClient(), new SharePointUploadOptions()), // IsConfigured=false => photo skipped
            new NoOpEmail(),
            NullLogger<SignupModel>.Instance)
        {
            PageContext = new PageContext { HttpContext = new DefaultHttpContext() },
        };

    [Fact]
    public async Task OnGet_builds_the_day_list_without_throwing()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        var m = NewModel(db);

        await m.OnGetAsync(default);

        Assert.False(m.NoActiveEvent);
        Assert.Equal(2, m.Days.Count); // pre-day + main day
    }

    [Fact]
    public async Task Submitting_with_agreement_creates_applicant_availability_and_meta()
    {
        using var db = NewDb();
        var eventId = await SeedEventAsync(db);
        var m = NewModel(db);
        m.FullName = "Avery Helper";
        m.Email = "Avery@Example.com";
        m.Phone = "+45 12345678";
        m.LinkedInUrl = "https://www.linkedin.com/in/avery";
        m.AgreementAccepted = true;
        m.Availability = new()
        {
            // Slots map to the per-day option sets (REQUIREMENTS §45): "Full day" =>
            // Full, "Not able to help" => Unavailable.
            new() { Day = PreDay, Slot = "Full day" },
            new() { Day = MainDay, Slot = "Not able to help", Note = "family" },
        };

        await m.OnPostAsync(default);

        Assert.True(m.SubmittedOk);
        var p = await db.Participants.SingleAsync(x => x.EventId == eventId);
        Assert.Equal(ParticipantRole.Volunteer, p.Role);
        Assert.False(p.IsActive); // pending applicant
        Assert.Equal("avery@example.com", p.Email);

        var days = await db.VolunteerDayAvailabilities.Where(x => x.ParticipantId == p.Id).ToListAsync();
        Assert.Equal(2, days.Count);
        var mainDay = days.Single(d => d.Day == MainDay);
        Assert.Equal(VolunteerAvailabilityLevel.Unavailable, mainDay.Level);
        // The chosen slot is persisted as a "[slot]" tag in Note, with the free note after it.
        Assert.Contains("[Not able to help]", mainDay.Note);
        Assert.Contains("family", mainDay.Note);

        var meta = await db.VolunteerAvailabilities.SingleAsync(x => x.ParticipantId == p.Id);
        Assert.Equal("https://www.linkedin.com/in/avery", meta.LinkedInUrl);
        Assert.NotNull(meta.AgreementAcceptedAt);
    }

    [Fact]
    public async Task Submitting_without_agreement_is_rejected_and_creates_nothing()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        var m = NewModel(db);
        m.FullName = "Bo Helper";
        m.Email = "bo@example.com";
        m.AgreementAccepted = false;

        await m.OnPostAsync(default);

        Assert.False(m.SubmittedOk);
        Assert.NotNull(m.ErrorMessage);
        Assert.Equal(0, await db.Participants.CountAsync());
    }

    [Fact]
    public async Task Honeypot_silently_succeeds_without_writing()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        var m = NewModel(db);
        m.FullName = "Bot";
        m.Email = "bot@example.com";
        m.AgreementAccepted = true;
        m.Website = "http://spam.example"; // honeypot tripped

        await m.OnPostAsync(default);

        Assert.True(m.SubmittedOk);
        Assert.Equal(0, await db.Participants.CountAsync());
    }
}
