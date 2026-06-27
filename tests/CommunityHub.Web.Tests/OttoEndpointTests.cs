using System.Security.Claims;
using CommunityHub.Assistant;
using CommunityHub.Auth;
using CommunityHub.Core.Assistant;
using CommunityHub.Core.Content;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Pages.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// Endpoint-level contract for Otto (/api/otto, REQUIREMENTS §129). Drives the real
/// <see cref="OttoModel"/> handler over a fake HttpContext and proves:
///   • the handler requires a signed-in participant (no principal ⇒ 401, never a leak);
///   • the participant id + role are taken from the SERVER principal — the request body
///     carries only the question; and
///   • role-scoped retrieval holds end-to-end: a volunteer's grounding excludes
///     speaker-only content while a speaker's includes it (the assistant here echoes the
///     assembled grounding so the test can inspect exactly what the model would see).
/// FAKE names only; no network, no SQL, no secrets.
/// </summary>
public sealed class OttoEndpointTests
{
    private const int EventId = 7;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"otto-{Guid.NewGuid():N}")
            .Options);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-06-18T10:00:00Z");
    }

    // Content provider that returns text for every slug it is asked for — so a leak of a
    // speaker-only slug into a volunteer's grounding would surface in the echoed answer.
    private sealed class AllSlugsContentProvider : IOttoContentProvider
    {
        public string? GetContentMarkdown(string slug) => $"CONTENT[{slug}]";
    }

    // Echoes the assembled (already-authorized) grounding back as the answer.
    private sealed class EchoAssistant : IOttoAssistant
    {
        public bool Available => true;
        public Task<OttoAnswer> AskAsync(string question, OttoContext context, CancellationToken ct = default)
            => Task.FromResult(new OttoAnswer(true, context.ToGroundingText()));
    }

    private static ClaimsPrincipal Principal(int participantId, ParticipantRole role) =>
        new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, participantId.ToString()),
            new Claim(ClaimTypes.Email, $"p{participantId}@fake.test"),
            new Claim(ClaimTypes.Name, $"Person {participantId}"),
            new Claim(ClaimTypes.Role, role.ToString()),
            new Claim("EventId", EventId.ToString()),
        }, authenticationType: "Test"));

    private sealed class AccessorOver(ClaimsPrincipal? user) : ICurrentParticipantAccessor
    {
        public CurrentParticipant? Current => CurrentParticipant.FromPrincipal(user);
    }

    private static OttoModel BuildHandler(
        CommunityHubDbContext db, ClaimsPrincipal? user, IOttoAssistant assistant)
    {
        var grounding = new OttoGroundingBuilder(
            new AllSlugsContentProvider(),
            new WebOttoOwnDataProvider(db, new FixedClock()));
        var model = new OttoModel(
            assistant, grounding, new AccessorOver(user), new OttoRateLimiter(new FixedClock()))
        {
            PageContext = new PageContext
            {
                HttpContext = new DefaultHttpContext { User = user ?? new ClaimsPrincipal() },
            },
        };
        return model;
    }

    private static string SpeakerOnlySlug =>
        ContentPageRegistry.All.First(p => p.Roles.Contains(ParticipantRole.Speaker)).Slug;

    [Fact]
    public async Task Unauthenticated_request_is_rejected()
    {
        using var db = NewDb();
        var handler = BuildHandler(db, user: null, new EchoAssistant());

        var result = await handler.OnPostAsync(new OttoModel.OttoRequest { Question = "hi" }, default);

        var json = Assert.IsType<JsonResult>(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, json.StatusCode);
    }

    [Fact]
    public async Task Volunteer_answer_excludes_speaker_content()
    {
        using var db = NewDb();
        db.Participants.Add(new Participant { Id = 42, EventId = EventId, FullName = "Vol", Email = "v@fake.test" });
        await db.SaveChangesAsync();

        var handler = BuildHandler(db, Principal(42, ParticipantRole.Volunteer), new EchoAssistant());
        var result = await handler.OnPostAsync(
            new OttoModel.OttoRequest { Question = "what should I do?" }, default);

        var answer = (string)GetProp(result, "answer");
        Assert.DoesNotContain($"CONTENT[{SpeakerOnlySlug}]", answer);
    }

    [Fact]
    public async Task Speaker_answer_includes_speaker_content()
    {
        using var db = NewDb();
        db.Participants.Add(new Participant { Id = 7, EventId = EventId, FullName = "Spk", Email = "s@fake.test" });
        await db.SaveChangesAsync();

        var handler = BuildHandler(db, Principal(7, ParticipantRole.Speaker), new EchoAssistant());
        var result = await handler.OnPostAsync(
            new OttoModel.OttoRequest { Question = "what should I prepare?" }, default);

        var answer = (string)GetProp(result, "answer");
        Assert.Contains($"CONTENT[{SpeakerOnlySlug}]", answer);
    }

    [Fact]
    public async Task Empty_question_is_handled_gracefully()
    {
        using var db = NewDb();
        db.Participants.Add(new Participant { Id = 1, EventId = EventId, FullName = "P", Email = "p@fake.test" });
        await db.SaveChangesAsync();

        var handler = BuildHandler(db, Principal(1, ParticipantRole.Volunteer), new EchoAssistant());
        var result = await handler.OnPostAsync(new OttoModel.OttoRequest { Question = "   " }, default);

        var json = Assert.IsType<JsonResult>(result);
        Assert.Null(json.StatusCode); // 200 OK with a friendly prompt, no error
    }

    private static object GetProp(IActionResult result, string name)
    {
        var json = Assert.IsType<JsonResult>(result);
        var value = json.Value!;
        var prop = value.GetType().GetProperty(name);
        Assert.NotNull(prop);
        return prop!.GetValue(value)!;
    }
}
