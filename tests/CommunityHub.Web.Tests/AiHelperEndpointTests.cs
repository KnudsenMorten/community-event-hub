using System.Security.Claims;
using CommunityHub.Assistant;
using CommunityHub.Auth;
using CommunityHub.Core.Assistant;
using CommunityHub.Core.Content;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Pages.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// Endpoint-level contract for the AI Community Helper (/api/ai-helper, REQUIREMENTS §129).
/// Drives the real <see cref="AiHelperModel"/> handler over a fake HttpContext and proves:
///   • the handler requires a signed-in participant (no principal ⇒ 401, never a leak);
///   • the participant id + role are taken from the SERVER principal — the request body
///     carries only the question; and
///   • role-scoped retrieval holds end-to-end: a volunteer's grounding excludes
///     speaker-only content while a speaker's includes it (the assistant here echoes the
///     assembled grounding so the test can inspect exactly what the model would see).
/// FAKE names only; no network, no SQL, no secrets.
/// </summary>
public sealed class AiHelperEndpointTests
{
    private const int EventId = 7;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"ai-helper-{Guid.NewGuid():N}")
            .Options);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-06-18T10:00:00Z");
    }

    // Content provider that returns text for every slug it is asked for — so a leak of a
    // speaker-only slug into a volunteer's grounding would surface in the echoed answer.
    private sealed class AllSlugsContentProvider : IAiHelperContentProvider
    {
        public string? GetContentMarkdown(string slug) => $"CONTENT[{slug}]";
    }

    // Echoes the assembled (already-authorized) grounding back as the answer.
    private sealed class EchoAssistant : IAiHelperAssistant
    {
        public bool Available => true;
        public Task<AiHelperAnswer> AskAsync(string question, AiHelperContext context, CancellationToken ct = default)
            => Task.FromResult(new AiHelperAnswer(true, context.ToGroundingText()));
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

    // Records every intake send (the §137 routing target) for the wire-in tests.
    private sealed class CapturingSender : IEmailSender
    {
        public readonly List<(string To, string Subject)> Sent = new();
        public Task SendAsync(string to, string s, string h, CancellationToken ct = default)
        { Sent.Add((to, s)); return Task.CompletedTask; }
        public Task SendAsync(string to, string s, string h, IReadOnlyCollection<string>? cc, CancellationToken ct = default)
            => SendAsync(to, s, h, ct);
        public Task SendAsync(string to, string s, string h, string t, CancellationToken ct = default)
            => SendAsync(to, s, h, ct);
        public Task SendWithIcsAsync(string to, string s, string h, string ics, string fn, CancellationToken ct = default)
            => SendAsync(to, s, h, ct);
        public Task SendWithAttachmentsAsync(string to, string s, string h, IReadOnlyCollection<EmailAttachment> a, CancellationToken ct = default)
            => SendAsync(to, s, h, ct);
    }

    private static readonly FeedbackIntakeOptions FeedbackOpts = new();

    private static FeedbackIntakeService Feedback(CommunityHubDbContext db, IEmailSender sender) =>
        new(db, new FeedbackIntakeDetector(FeedbackOpts), FeedbackOpts, sender, new EmailContextAccessor(), new FixedClock());

    private static AiHelperModel BuildHandler(
        CommunityHubDbContext db, ClaimsPrincipal? user, IAiHelperAssistant assistant,
        IEmailSender? sender = null)
    {
        var grounding = new AiHelperGroundingBuilder(
            new AllSlugsContentProvider(),
            new WebAiHelperOwnDataProvider(db, new FixedClock(), new CommunityHub.Core.Sponsors.SponsorDeliverablesService(db)));
        var model = new AiHelperModel(
            assistant, grounding, new AccessorOver(user), new AiHelperRateLimiter(new FixedClock()),
            Feedback(db, sender ?? new CapturingSender()))
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

        var result = await handler.OnPostAsync(new AiHelperModel.AiHelperRequest { Question = "hi" }, default);

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
            new AiHelperModel.AiHelperRequest { Question = "what should I do?" }, default);

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
            new AiHelperModel.AiHelperRequest { Question = "what should I prepare?" }, default);

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
        var result = await handler.OnPostAsync(new AiHelperModel.AiHelperRequest { Question = "   " }, default);

        var json = Assert.IsType<JsonResult>(result);
        Assert.Null(json.StatusCode); // 200 OK with a friendly prompt, no error
    }

    // --- §137 INTAKE wire-in -------------------------------------------------

    [Fact]
    public async Task Bug_message_is_captured_to_feed_and_routed_to_dev_mailbox()
    {
        using var db = NewDb();
        db.Participants.Add(new Participant { Id = 9, EventId = EventId, FullName = "Vol", Email = "v@fake.test" });
        await db.SaveChangesAsync();

        var sender = new CapturingSender();
        var handler = BuildHandler(db, Principal(9, ParticipantRole.Volunteer), new EchoAssistant(), sender);

        var result = await handler.OnPostAsync(
            new AiHelperModel.AiHelperRequest { Question = "there is a bug on the agenda page" }, default);

        Assert.True((bool)GetProp(result, "captured"));
        Assert.Equal("Bug", (string)GetProp(result, "kind"));
        // Stored to the feed with the SERVER-resolved role + id (not from the body).
        var item = await db.FeedbackItems.SingleAsync();
        Assert.Equal(FeedbackKind.Bug, item.Kind);
        Assert.Equal(9, item.ParticipantId);
        Assert.Equal(ParticipantRole.Volunteer, item.Role);
        // Emailed to the dev mailbox.
        Assert.Equal("mok@expertslive.dk", Assert.Single(sender.Sent).To);
    }

    [Fact]
    public async Task Contact_organizers_path_routes_to_the_organizers_and_skips_the_assistant()
    {
        using var db = NewDb();
        db.Participants.Add(new Participant { Id = 11, EventId = EventId, FullName = "Att", Email = "a@fake.test" });
        await db.SaveChangesAsync();

        var sender = new CapturingSender();
        var handler = BuildHandler(db, Principal(11, ParticipantRole.Attendee), new EchoAssistant(), sender);

        var result = await handler.OnPostAsync(
            new AiHelperModel.AiHelperRequest { Question = "Please have someone call me", ContactOrganizers = true },
            default);

        Assert.Equal("Question", (string)GetProp(result, "kind"));
        var item = await db.FeedbackItems.SingleAsync();
        Assert.Equal(FeedbackKind.Question, item.Kind);
        Assert.Equal(11, item.ParticipantId);                 // server-resolved identity
        Assert.Equal("info@expertslive.dk", Assert.Single(sender.Sent).To);
    }

    [Fact]
    public async Task Ordinary_question_captures_no_feed_item()
    {
        using var db = NewDb();
        db.Participants.Add(new Participant { Id = 12, EventId = EventId, FullName = "P", Email = "p@fake.test" });
        await db.SaveChangesAsync();

        var sender = new CapturingSender();
        var handler = BuildHandler(db, Principal(12, ParticipantRole.Volunteer), new EchoAssistant(), sender);

        var result = await handler.OnPostAsync(
            new AiHelperModel.AiHelperRequest { Question = "when does lunch start?" }, default);

        Assert.False((bool)GetProp(result, "captured"));
        Assert.Equal(0, await db.FeedbackItems.CountAsync());
        Assert.Empty(sender.Sent);
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
