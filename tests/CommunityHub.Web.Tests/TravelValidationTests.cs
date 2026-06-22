using System.Security.Claims;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Resources;
using CommunityHub.Pages.Forms;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// Server-side field validation for the Travel reimbursement form (REQUIREMENTS
/// §21 shared validation pattern). Specifically pins the previously-silent
/// "Other"-blank bug: choosing "Other" with no amount used to save a NULL claim
/// without any error. These drive the real <see cref="TravelModel"/> POST handler
/// over an in-memory DB + fake speaker session and assert the field error is added
/// and NOTHING is persisted. FAKE names only.
/// </summary>
public sealed class TravelValidationTests
{
    private const int EventId = 7;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"travel-{Guid.NewGuid():N}")
            .Options);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-06-15T10:00:00Z");
    }

    private sealed class HttpContextAccessorOver(HttpContext ctx) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => ctx; set { } }
    }

    private static IStringLocalizer<SharedResource> MakeLocalizer()
    {
        var options = Options.Create(new LocalizationOptions { ResourcesPath = "" });
        var factory = new ResourceManagerStringLocalizerFactory(options, NullLoggerFactory.Instance);
        return new StringLocalizer<SharedResource>(factory);
    }

    private static ClaimsPrincipal SpeakerSession(Participant p)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, p.Id.ToString()),
            new(ClaimTypes.Email, p.Email),
            new(ClaimTypes.Name, p.FullName),
            new(ClaimTypes.Role, p.Role.ToString()),
            new("EventId", p.EventId.ToString()),
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    private static TravelModel NewModel(CommunityHubDbContext db, DefaultHttpContext http)
    {
        var accessor = new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http));
        return new TravelModel(db, accessor, new FixedClock(), MakeLocalizer())
        {
            PageContext = new PageContext { HttpContext = http },
        };
    }

    private static async Task<Participant> SeedSpeakerAsync(CommunityHubDbContext db)
    {
        db.Events.Add(new Event
        {
            Id = EventId, CommunityName = "Test Community", Code = "TEST",
            DisplayName = "Test Event",
            StartDate = new DateOnly(2026, 11, 1), EndDate = new DateOnly(2026, 11, 2),
        });
        var speaker = new Participant
        {
            EventId = EventId, Email = "spk@example.test", FullName = "Speaker Person",
            Role = ParticipantRole.Speaker, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(speaker);
        await db.SaveChangesAsync();
        // FEATURE B: travel is entitlement-gated; a SUPPORTED speaker is entitled to
        // OrderItem.TravelReimbursement, so the form is accessible (a sponsor-self-funded
        // speaker would be denied — covered by FormEntitlementGateTests).
        db.SpeakerProfiles.Add(new SpeakerProfile
        {
            EventId = EventId, ParticipantId = speaker.Id,
            SpeakerFunding = SpeakerFunding.Supported,
            SpeakingPreDay = true, SpeakingMainDay = true,
        });
        await db.SaveChangesAsync();
        return speaker;
    }

    [Fact]
    public async Task Other_with_blank_amount_is_rejected_and_saves_nothing()
    {
        using var db = NewDb();
        var speaker = await SeedSpeakerAsync(db);
        var http = new DefaultHttpContext { User = SpeakerSession(speaker) };
        var model = NewModel(db, http);

        model.RequestReimbursement = true;
        model.OriginCity = "Berlin, Germany";
        model.AmountChoice = TravelModel.ChoiceOther;
        model.OtherAmountEur = null;            // the bug: blank "Other" amount
        model.Explanation = "Cheapest route via X";

        var result = await model.OnPostAsync(default);

        Assert.IsType<PageResult>(result);
        Assert.False(model.ModelState.IsValid);
        Assert.True(model.ModelState.ContainsKey(nameof(TravelModel.OtherAmountEur)));
        // Nothing persisted.
        Assert.Empty(await db.TravelReimbursements.ToListAsync());
        Assert.Null(model.Message);
    }

    [Fact]
    public async Task Other_with_blank_explanation_is_rejected()
    {
        using var db = NewDb();
        var speaker = await SeedSpeakerAsync(db);
        var http = new DefaultHttpContext { User = SpeakerSession(speaker) };
        var model = NewModel(db, http);

        model.RequestReimbursement = true;
        model.AmountChoice = TravelModel.ChoiceOther;
        model.OtherAmountEur = 250m;
        model.Explanation = "   ";               // whitespace-only

        var result = await model.OnPostAsync(default);

        Assert.IsType<PageResult>(result);
        Assert.True(model.ModelState.ContainsKey(nameof(TravelModel.Explanation)));
        Assert.Empty(await db.TravelReimbursements.ToListAsync());
    }

    [Fact]
    public async Task Claiming_without_choosing_an_amount_is_rejected()
    {
        using var db = NewDb();
        var speaker = await SeedSpeakerAsync(db);
        var http = new DefaultHttpContext { User = SpeakerSession(speaker) };
        var model = NewModel(db, http);

        model.RequestReimbursement = true;
        model.AmountChoice = TravelModel.ChoiceNone;   // nothing picked

        var result = await model.OnPostAsync(default);

        Assert.IsType<PageResult>(result);
        Assert.True(model.ModelState.ContainsKey(nameof(TravelModel.AmountChoice)));
        Assert.Empty(await db.TravelReimbursements.ToListAsync());
    }

    [Fact]
    public async Task Other_with_a_valid_amount_and_explanation_saves()
    {
        using var db = NewDb();
        var speaker = await SeedSpeakerAsync(db);
        var http = new DefaultHttpContext { User = SpeakerSession(speaker) };
        var model = NewModel(db, http);

        model.RequestReimbursement = true;
        model.OriginCity = "Oslo, Norway";
        model.AmountChoice = TravelModel.ChoiceOther;
        model.OtherAmountEur = 275m;
        model.Explanation = "Cheapest economy flight is above the EUR 300 cap.";

        var result = await model.OnPostAsync(default);

        Assert.IsType<PageResult>(result);
        Assert.True(model.ModelState.IsValid);
        var row = Assert.Single(await db.TravelReimbursements.ToListAsync());
        Assert.Equal(275m, row.ClaimAmountEur);
        Assert.NotNull(model.Message);
    }

    [Fact]
    public async Task Preset_cap_choice_needs_no_other_amount()
    {
        using var db = NewDb();
        var speaker = await SeedSpeakerAsync(db);
        var http = new DefaultHttpContext { User = SpeakerSession(speaker) };
        var model = NewModel(db, http);

        model.RequestReimbursement = true;
        model.AmountChoice = TravelModel.Choice400;   // a preset cap, no "Other"
        model.OtherAmountEur = null;

        var result = await model.OnPostAsync(default);

        Assert.IsType<PageResult>(result);
        Assert.True(model.ModelState.IsValid);
        var row = Assert.Single(await db.TravelReimbursements.ToListAsync());
        Assert.Equal(TravelModel.Cap400, row.ClaimAmountEur);
    }

    [Fact]
    public async Task Not_claiming_skips_validation_entirely()
    {
        using var db = NewDb();
        var speaker = await SeedSpeakerAsync(db);
        var http = new DefaultHttpContext { User = SpeakerSession(speaker) };
        var model = NewModel(db, http);

        model.RequestReimbursement = false;
        model.AmountChoice = TravelModel.ChoiceNone;   // irrelevant when opting out

        var result = await model.OnPostAsync(default);

        Assert.IsType<PageResult>(result);
        Assert.True(model.ModelState.IsValid);
        var row = Assert.Single(await db.TravelReimbursements.ToListAsync());
        Assert.Null(row.ClaimAmountEur);
    }
}
