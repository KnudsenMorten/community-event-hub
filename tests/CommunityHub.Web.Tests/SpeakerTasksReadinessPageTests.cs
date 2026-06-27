using System.Security.Claims;
using CommunityHub.Auth;
using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Participants;
using CommunityHub.Core.Reminders;
using CommunityHub.Pages.Speaker;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §138 (operator 2026-06-27): the speaker "Am I ready?" readiness rollup now lives at
/// the TOP of the My Tasks page (the standalone /Speaker/Readiness nav item is removed).
/// These web tests drive the real <see cref="TasksModel"/> and prove it exposes a
/// readiness rollup for a speaker with a profile, and null when there is no profile (so
/// the view omits the card). FAKE names only.
/// </summary>
public sealed class SpeakerTasksReadinessPageTests
{
    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"spk-tasks-{Guid.NewGuid():N}")
            .Options);

    private sealed class HttpContextAccessorOver(HttpContext ctx) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => ctx; set { } }
    }

    private static ClaimsPrincipal Session(Participant p)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, p.Id.ToString()),
            new(ClaimTypes.Email, p.Email),
            new(ClaimTypes.Name, p.FullName),
            new(ClaimTypes.Role, p.Role.ToString()),
            new("EventId", p.EventId.ToString()),
        };
        return new ClaimsPrincipal(
            new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    private static TasksModel NewModel(CommunityHubDbContext db, DefaultHttpContext http)
    {
        var accessor = new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http));
        // Point the deadline seeder at a non-existent config so seeding is a harmless no-op.
        var seeder = new SpeakerDeadlineSeeder(
            db, new SpeakerDeadlineOptions { ConfigPath = "does-not-exist.json" },
            TimeProvider.System);

        var model = new TasksModel(
            db,
            accessor,
            seeder,
            new FormTaskReconciler(db, TimeProvider.System),
            new SpeakerMilestoneService(db, TimeProvider.System),
            new SpeakerReadinessService(db));

        var actionContext = new ActionContext(
            http, new RouteData(), new PageActionDescriptor(), new ModelStateDictionary());
        model.PageContext = new PageContext(actionContext);
        return model;
    }

    private static async Task<int> NewEventAsync(CommunityHubDbContext db)
    {
        var evt = new Event
        {
            Code = "SPK27", CommunityName = "C", DisplayName = "SPK 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        };
        db.Events.Add(evt);
        await db.SaveChangesAsync();
        return evt.Id;
    }

    private static async Task<Participant> NewSpeakerAsync(
        CommunityHubDbContext db, int eventId, bool withProfile)
    {
        var p = new Participant
        {
            EventId = eventId, FullName = "Sam Speaker", Email = "sam@example.test",
            Role = ParticipantRole.Speaker, IsActive = true,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        if (withProfile)
        {
            db.SpeakerProfiles.Add(new SpeakerProfile
            {
                EventId = eventId, ParticipantId = p.Id, Biography = "Bio.",
            });
            await db.SaveChangesAsync();
        }
        return p;
    }

    [Fact]
    public async Task Tasks_page_exposes_readiness_rollup_for_a_speaker_with_a_profile()
    {
        using var db = NewDb();
        var eventId = await NewEventAsync(db);
        var spk = await NewSpeakerAsync(db, eventId, withProfile: true);
        var http = new DefaultHttpContext { User = Session(spk) };
        var model = NewModel(db, http);

        await model.OnGetAsync(default);

        Assert.False(model.NotSpeaker);
        Assert.NotNull(model.Readiness);
        // A fresh speaker has applicable items but most are still missing (nothing done yet).
        Assert.True(model.Readiness!.ApplicableCount > 0);
        Assert.False(model.Readiness.IsReady);
    }

    [Fact]
    public async Task Tasks_page_readiness_is_null_when_speaker_has_no_profile()
    {
        using var db = NewDb();
        var eventId = await NewEventAsync(db);
        var spk = await NewSpeakerAsync(db, eventId, withProfile: false);
        var http = new DefaultHttpContext { User = Session(spk) };
        var model = NewModel(db, http);

        await model.OnGetAsync(default);

        Assert.False(model.NotSpeaker);
        // No SpeakerProfile -> the rollup is null and the view omits the card.
        Assert.Null(model.Readiness);
    }
}
