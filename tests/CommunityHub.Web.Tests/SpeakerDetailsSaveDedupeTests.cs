using System.Security.Claims;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Settings;
using CommunityHub.Pages.Speaker;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// Bug fix #2 — Speaker "Details" page. Proves:
///   • the plain "Save" handler (OnPostSaveAsync) persists WITHOUT touching Zoho /
///     emailing the organizers, and
///   • "Save &amp; sync" only re-emails the organizers' manual-update alert when a
///     Zoho-relevant field actually changed (dedupe — it used to fire on EVERY save).
/// FAKE names only.
/// </summary>
public sealed class SpeakerDetailsSaveDedupeTests
{
    private const int EventId = 71;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"spkdetails-{Guid.NewGuid():N}")
            .Options);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-06-26T10:00:00Z");
    }

    /// <summary>Records every email so the test can count the manual-update alerts.</summary>
    private sealed class RecordingEmailSender : IEmailSender
    {
        public List<string> To { get; } = new();
        public Task SendAsync(string to, string s, string h, CancellationToken ct = default) { To.Add(to); return Task.CompletedTask; }
        public Task SendAsync(string to, string s, string h, IReadOnlyCollection<string>? cc, CancellationToken ct = default) { To.Add(to); return Task.CompletedTask; }
        public Task SendAsync(string to, string s, string h, string t, CancellationToken ct = default) { To.Add(to); return Task.CompletedTask; }
        public Task SendWithIcsAsync(string to, string s, string h, string ics, string f, CancellationToken ct = default) { To.Add(to); return Task.CompletedTask; }
        public Task SendWithAttachmentsAsync(string to, string s, string h, IReadOnlyCollection<EmailAttachment> a, CancellationToken ct = default) { To.Add(to); return Task.CompletedTask; }
    }

    /// <summary>A live Backstage writer that always reports the speaker already exists.</summary>
    private sealed class ExistsBlockedApi : IBackstageSpeakerBioApi
    {
        public bool CanWrite => true;
        public Task<BackstageSpeakerUpsertResult> UpsertSpeakerBioAsync(SpeakerBioRecord record, CancellationToken ct) =>
            Task.FromResult(new BackstageSpeakerUpsertResult(BackstageSpeakerAction.ExistsBlocked, "fake-id"));
    }

    private sealed class HttpContextAccessorOver(HttpContext ctx) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => ctx; set { } }
    }

    private static ClaimsPrincipal Session(Participant p) =>
        new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, p.Id.ToString()),
            new Claim(ClaimTypes.Email, p.Email),
            new Claim(ClaimTypes.Name, p.FullName),
            new Claim(ClaimTypes.Role, p.Role.ToString()),
            new Claim("EventId", p.EventId.ToString()),
        }, CookieAuthenticationDefaults.AuthenticationScheme));

    private static async Task<Participant> SeedSpeakerAsync(CommunityHubDbContext db)
    {
        db.Events.Add(new Event
        {
            Id = EventId, Code = "SD27", CommunityName = "C", DisplayName = "SD 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        });
        var p = new Participant
        {
            EventId = EventId, FullName = "Sven Speaker", Email = "sven@example.test",
            Role = ParticipantRole.Speaker, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();

        db.SpeakerProfiles.Add(new SpeakerProfile
        {
            EventId = EventId, ParticipantId = p.Id, Biography = "Old bio.",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return p;
    }

    private static async Task EnableSyncAsync(CommunityHubDbContext db)
    {
        var settings = new FeatureSettingsService(db, TimeProvider.System);
        await settings.SetEnabledAsync(EventId, "backstage-speaker-sync", true, "org@expertslive.dk");
        await settings.SetReleasedRingAsync(EventId, "backstage-speaker-sync", Ring.Broad, "org@expertslive.dk");
    }

    private static (DetailsModel model, RecordingEmailSender email) NewModel(
        CommunityHubDbContext db, Participant p)
    {
        var http = new DefaultHttpContext { User = Session(p) };
        var accessor = new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http));
        var email = new RecordingEmailSender();
        var sync = new SpeakerBioBackstageSyncService(
            db, new ExistsBlockedApi(),
            Options.Create(new BackstageSpeakerBioSyncOptions { Enabled = true }),
            email, new FeatureGateService(db), new RingResolver(db));
        var model = new DetailsModel(db, accessor, sync, new FixedClock())
        {
            PageContext = new PageContext { HttpContext = http },
        };
        return (model, email);
    }

    [Fact]
    public async Task Plain_Save_persists_without_syncing_or_emailing()
    {
        using var db = NewDb();
        var p = await SeedSpeakerAsync(db);
        await EnableSyncAsync(db);
        var (model, email) = NewModel(db, p);

        model.Biography = "A freshly edited bio.";
        var result = await model.OnPostSaveAsync(default);

        Assert.IsType<PageResult>(result);
        var saved = await db.SpeakerProfiles.SingleAsync();
        Assert.Equal("A freshly edited bio.", saved.Biography);
        Assert.Empty(email.To);   // plain Save never touches Zoho / emails organizers
    }

    [Fact]
    public async Task SaveAndSync_emails_once_then_dedupes_unchanged_resave()
    {
        using var db = NewDb();
        var p = await SeedSpeakerAsync(db);
        await EnableSyncAsync(db);

        // 1) Save & sync WITH a real bio change → organizers are emailed once.
        var (m1, email) = NewModel(db, p);
        m1.Biography = "Brand new bio for sync.";
        await m1.OnPostSaveAndSyncAsync(default);
        Assert.Single(email.To);
        Assert.Equal(SpeakerBioBackstageSyncService.AlertEmail, email.To[0]);

        // 2) Save & sync again with NO change (same persisted bio) → no new email.
        var (m2, email2) = NewModel(db, p);
        m2.Biography = "Brand new bio for sync.";   // identical to what is now stored
        await m2.OnPostSaveAndSyncAsync(default);
        Assert.Empty(email2.To);   // dedupe: organizers NOT re-notified on an unchanged save
    }
}
