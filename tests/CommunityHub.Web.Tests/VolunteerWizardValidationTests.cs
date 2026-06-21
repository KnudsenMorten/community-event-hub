using System.Security.Claims;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Pages.Forms;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// Server-side form validation for the volunteer availability wizard
/// (<see cref="VolunteerWizardModel"/>). Two confirmed bugs are covered here so
/// they cannot regress:
///   1. MaxHoursPerDay used to be silently Math.Clamp'd (a volunteer who typed
///      40 was recorded as 24 with no feedback). The Finish POST must now REJECT
///      out-of-range / non-numeric hours and surface a message — what the
///      volunteer saw must equal what is saved.
///   2. Step 3 used to let you Confirm with ZERO shifts selected. The Finish POST
///      must now reject an empty availability server-side (the disabled button is
///      not enough), writing no row with empty shifts.
/// A valid submission must still save end-to-end. FAKE names only.
/// </summary>
public sealed class VolunteerWizardValidationTests
{
    private const int EventId = 11;
    private const int ParticipantId = 42;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"volwiz-{Guid.NewGuid():N}")
            .Options);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-06-15T10:00:00Z");
    }

    private sealed class HttpContextAccessorOver(HttpContext ctx) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => ctx; set { } }
    }

    private static ClaimsPrincipal VolunteerSession()
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, ParticipantId.ToString()),
            new(ClaimTypes.Email, "vol@example.test"),
            new(ClaimTypes.Name, "Vol Unteer"),
            new(ClaimTypes.Role, ParticipantRole.Volunteer.ToString()),
            new("EventId", EventId.ToString()),
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    private static VolunteerWizardModel NewModel(CommunityHubDbContext db, DefaultHttpContext http)
    {
        var clock = new FixedClock();
        var accessor = new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http));
        return new VolunteerWizardModel(db, accessor, clock)
        {
            PageContext = new PageContext { HttpContext = http },
        };
    }

    private static VolunteerWizardModel NewSignedInModel(CommunityHubDbContext db)
    {
        var http = new DefaultHttpContext { User = VolunteerSession() };
        return NewModel(db, http);
    }

    // (c) A valid submission still works end-to-end.
    [Fact]
    public async Task Valid_submission_saves_availability_row()
    {
        using var db = NewDb();
        var model = NewSignedInModel(db);
        model.Step = 3;
        model.SelectedShifts = new List<string> { "Session room support", "Teardown (after close)" };
        model.PreferredRole = "Floater";
        model.MaxHoursPerDay = 6;

        await model.OnPostFinishAsync(default);

        Assert.True(model.Saved);
        Assert.Null(model.ValidationError);
        var row = await db.VolunteerAvailabilities.SingleAsync(
            v => v.EventId == EventId && v.ParticipantId == ParticipantId);
        Assert.Equal(6, row.MaxHoursPerDay);
        Assert.Contains("Session room support", row.SelectedShifts);
        Assert.Contains("Teardown (after close)", row.SelectedShifts);
    }

    // (a) Out-of-range hours are rejected and surfaced — NOT silently clamped.
    [Fact]
    public async Task Out_of_range_hours_are_rejected_not_silently_clamped()
    {
        using var db = NewDb();
        var model = NewSignedInModel(db);
        model.Step = 3;
        model.SelectedShifts = new List<string> { "Setup (day before)" };
        model.MaxHoursPerDay = 40; // would previously be silently saved as 24

        await model.OnPostFinishAsync(default);

        Assert.False(model.Saved);
        Assert.NotNull(model.ValidationError);
        Assert.Equal(3, model.Step); // stays on review step
        // No row may be written with the silently-clamped value.
        Assert.False(await db.VolunteerAvailabilities.AnyAsync());
    }

    // (a) Non-numeric hours (int model-binding failure) are surfaced, not defaulted.
    [Fact]
    public async Task Non_numeric_hours_modelstate_error_is_surfaced()
    {
        using var db = NewDb();
        var model = NewSignedInModel(db);
        model.Step = 3;
        model.SelectedShifts = new List<string> { "Setup (day before)" };
        // Simulate the framework's int model-binding failure on "abc".
        model.ModelState.AddModelError(nameof(VolunteerWizardModel.MaxHoursPerDay),
            "The value 'abc' is not valid.");
        model.ModelState.SetModelValue(nameof(VolunteerWizardModel.MaxHoursPerDay),
            new ValueProviderResult("abc"));

        await model.OnPostFinishAsync(default);

        Assert.False(model.Saved);
        Assert.NotNull(model.ValidationError);
        Assert.False(await db.VolunteerAvailabilities.AnyAsync());
    }

    // (b) Empty availability is rejected server-side (disabled button not enough).
    [Fact]
    public async Task Empty_shift_submit_is_rejected_server_side()
    {
        using var db = NewDb();
        var model = NewSignedInModel(db);
        model.Step = 3;
        model.SelectedShifts = new List<string>(); // none selected
        model.MaxHoursPerDay = 8;

        await model.OnPostFinishAsync(default);

        Assert.False(model.Saved);
        Assert.NotNull(model.ValidationError);
        Assert.Equal(3, model.Step);
        // No participant row may be written with empty availability.
        Assert.False(await db.VolunteerAvailabilities.AnyAsync());
    }

    // (b) Tampered shift values that are not in the catalogue collapse to empty
    // and are rejected the same way (defence-in-depth past the disabled button).
    [Fact]
    public async Task Non_catalogue_shifts_only_are_rejected_as_empty()
    {
        using var db = NewDb();
        var model = NewSignedInModel(db);
        model.Step = 3;
        model.SelectedShifts = new List<string> { "Not a real shift" };
        model.MaxHoursPerDay = 8;

        await model.OnPostFinishAsync(default);

        Assert.False(model.Saved);
        Assert.NotNull(model.ValidationError);
        Assert.False(await db.VolunteerAvailabilities.AnyAsync());
    }
}
