using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Sponsors;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Offline tests for <see cref="SponsorLeadCaptureService"/> — booth-staff
/// lead capture into the shared SponsorLead store. Uses the EF Core InMemory
/// provider so the real DbContext mapping + the screening service run, no SQL.
/// </summary>
public sealed class SponsorLeadCaptureServiceTests
{
    private const int EventId = 1;
    private const string CompanyId = "42";
    private const string StaffEmail = "booth.staff@sponsor.test";

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"capture-{Guid.NewGuid():N}")
            .Options);

    private static SponsorLeadCaptureService NewService(CommunityHubDbContext db) =>
        new(db, new SponsorLeadScreeningService(), TimeProvider.System);

    private static SponsorLeadCaptureInput Input(
        string? first = "Grace", string? last = "Hopper", string? email = "grace@example-corp.test",
        string? phone = null, string? company = "ExampleCorp", string? job = "CTO", string? notes = "Wants a demo") =>
        new(first, last, email, phone, company, job, notes);

    [Fact]
    public async Task Capture_persists_a_manual_booth_lead_with_provenance()
    {
        using var db = NewDb();
        var svc = NewService(db);

        var result = await svc.CaptureAsync(EventId, CompanyId, StaffEmail, Input(), default);

        Assert.True(result.Ok);
        Assert.NotNull(result.LeadId);

        var lead = await db.SponsorLeads.SingleAsync();
        Assert.Equal(EventId, lead.EventId);
        Assert.Equal(CompanyId, lead.SponsorCompanyId);
        Assert.Equal(SponsorLeadCaptureMethod.ManualBooth, lead.CaptureMethod);
        Assert.Equal(StaffEmail, lead.CapturedByEmail);
        Assert.Equal(SponsorLeadKind.Lead, lead.LeadKind);
        Assert.Equal("Grace Hopper", lead.FullName);
        Assert.Equal(string.Empty, lead.ZohoRecordId);           // stays out of the Zoho unique index
        Assert.Equal("Booth (hub capture)", lead.Source);
        Assert.Equal(SponsorLeadStatus.Open, lead.Status);
        Assert.NotNull(lead.AiScreenScore);                       // screened on the way in
    }

    [Fact]
    public async Task Capture_rejects_a_lead_with_no_email_and_no_phone()
    {
        using var db = NewDb();
        var svc = NewService(db);

        var result = await svc.CaptureAsync(EventId, CompanyId, StaffEmail,
            Input(email: null, phone: null), default);

        Assert.False(result.Ok);
        Assert.Contains("email or a phone", result.Message);
        Assert.Empty(db.SponsorLeads);
    }

    [Fact]
    public async Task Capture_accepts_phone_only_lead()
    {
        using var db = NewDb();
        var svc = NewService(db);

        var result = await svc.CaptureAsync(EventId, CompanyId, StaffEmail,
            Input(email: null, phone: "+45 12 34 56 78"), default);

        Assert.True(result.Ok);
        var lead = await db.SponsorLeads.SingleAsync();
        Assert.Equal("+45 12 34 56 78", lead.Phone);
        Assert.Equal(string.Empty, lead.Email);
    }

    [Fact]
    public async Task Capture_rejects_an_obviously_invalid_email()
    {
        using var db = NewDb();
        var svc = NewService(db);

        var result = await svc.CaptureAsync(EventId, CompanyId, StaffEmail,
            Input(email: "not-an-email", phone: null), default);

        Assert.False(result.Ok);
        Assert.Empty(db.SponsorLeads);
    }

    [Fact]
    public async Task Capture_rejects_when_no_name_and_no_company()
    {
        using var db = NewDb();
        var svc = NewService(db);

        var result = await svc.CaptureAsync(EventId, CompanyId, StaffEmail,
            Input(first: null, last: null, company: null, email: "anon@example-corp.test"), default);

        Assert.False(result.Ok);
        Assert.Empty(db.SponsorLeads);
    }

    [Fact]
    public async Task Capture_rejects_when_company_is_not_linked()
    {
        using var db = NewDb();
        var svc = NewService(db);

        var result = await svc.CaptureAsync(EventId, "", StaffEmail, Input(), default);

        Assert.False(result.Ok);
        Assert.Empty(db.SponsorLeads);
    }

    [Fact]
    public async Task Capture_auto_junks_an_unmistakable_test_entry()
    {
        using var db = NewDb();
        var svc = NewService(db);

        // "test" name pattern + @example. email is the screen's auto-junk case.
        var result = await svc.CaptureAsync(EventId, CompanyId, StaffEmail,
            new SponsorLeadCaptureInput("test", "test", "test@example.test", null, "TestCo", null, null),
            default);

        Assert.True(result.Ok);                                   // still saved (nothing hard-deleted)
        var lead = await db.SponsorLeads.SingleAsync();
        Assert.Equal(SponsorLeadStatus.Junk, lead.Status);
        Assert.Equal("test-entry", lead.AiScreenLabel);
    }

    [Fact]
    public async Task GetBoothCaptured_returns_only_manual_non_junk_leads_newest_first()
    {
        using var db = NewDb();
        var svc = NewService(db);

        // A Zoho-synced lead — must NOT appear in the booth list.
        db.SponsorLeads.Add(new SponsorLead
        {
            EventId = EventId, SponsorCompanyId = CompanyId, ZohoRecordId = "zoho-1",
            CaptureMethod = SponsorLeadCaptureMethod.ZohoSync, FullName = "From CRM",
            Status = SponsorLeadStatus.Open, CapturedAt = DateTimeOffset.UtcNow.AddHours(-5),
        });
        await db.SaveChangesAsync();

        await svc.CaptureAsync(EventId, CompanyId, StaffEmail, Input(first: "First", last: "Booth"), default);
        await Task.Delay(5);
        await svc.CaptureAsync(EventId, CompanyId, StaffEmail, Input(first: "Second", last: "Booth"), default);

        var list = await svc.GetBoothCapturedAsync(EventId, CompanyId, 10, default);

        Assert.Equal(2, list.Count);                              // CRM lead excluded
        Assert.All(list, l => Assert.Equal(SponsorLeadCaptureMethod.ManualBooth, l.CaptureMethod));
        Assert.Equal("Second Booth", list[0].FullName);          // newest first
    }
}
