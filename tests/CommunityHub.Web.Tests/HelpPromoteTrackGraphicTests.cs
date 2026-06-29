using System.Security.Claims;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Graphics;
using CommunityHub.Core.Settings;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §158 — the speaker "Help Promote" page (<see cref="CommunityHub.Pages.Speaker.GraphicsModel"/>)
/// surfaces the per-TRACK promo graphic alongside the session graphic. Drives the real page handler
/// over a fake speaker session + a fake read-store: a RELEASED Track graphic renders as its OWN card,
/// LABELLED with the track name, and its download points at the hub proxy (/speaker-graphic/{id}) —
/// never a raw SharePoint URL — and that proxy streams the bytes. FAKE names only.
/// </summary>
public sealed class HelpPromoteTrackGraphicTests
{
    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"help-promote-{Guid.NewGuid():N}")
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
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    private static GraphicsService Graphics(CommunityHubDbContext db, ISharePointFileStore store) =>
        new(db, new GraphicCompositor(), store, new NullFetcher(), new DraftOnlySocialShareGateway(),
            Options.Create(new GraphicsSharePointOptions()));

    private static SpeakerLinkedInPublishService Publish(CommunityHubDbContext db, GraphicsService graphics)
    {
        var clock = TimeProvider.System;
        var settings = new SoMeSettingsService(db, clock);
        var queue = new SoMeQueueService(db, clock);
        var dispatch = new SoMeDispatchService(db, new NullLinkedInPostPublisher(), settings, new NoopEmail(), clock);
        return new SpeakerLinkedInPublishService(
            db, queue, dispatch, settings, graphics, new FeatureGateService(db), clock);
    }

    [Fact]
    public async Task Released_track_graphic_renders_a_labelled_card_with_a_proxy_download()
    {
        using var db = NewDb();

        var evt = new Event
        {
            Code = "ELDK27", DisplayName = "Community Events Demo 2027",
            StartDate = new DateOnly(2027, 2, 4), EndDate = new DateOnly(2027, 2, 5),
            IsActive = true,
        };
        db.Events.Add(evt);
        await db.SaveChangesAsync();

        var speaker = new Participant
        {
            EventId = evt.Id, Email = "speaker.one@example.test",
            FullName = "Session Speaker One", Role = ParticipantRole.Speaker,
        };
        db.Participants.Add(speaker);
        await db.SaveChangesAsync();

        var session = new Session
        {
            EventId = evt.Id, SessionizeId = Guid.NewGuid().ToString("N"),
            Title = "Cloud Native Talk", Type = SessionType.TechnicalSession, Track = "Security",
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();

        // A RELEASED track graphic for this speaker (as the SharePoint sync would leave it).
        db.GraphicAssets.Add(new GraphicAsset
        {
            EventId = evt.Id,
            Type = GraphicAssetType.Track,
            StableKey = GraphicStableKey.ForTrack("security", speaker.Id),
            ParticipantId = speaker.Id,
            SessionId = session.Id,            // representative session → resolves the track name
            Status = GraphicAssetStatus.Released,
            SharePointPath = "Graphics/Tracks/Security.png",
            StorageItemId = "item-Security.png",
            FileName = "Security.png",
        });
        await db.SaveChangesAsync();

        var store = new FakeReadStore();
        var graphics = Graphics(db, store);
        var http = new DefaultHttpContext { User = Session(speaker) };
        var model = new CommunityHub.Pages.Speaker.GraphicsModel(
            db, new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http)),
            graphics, Publish(db, graphics))
        {
            PageContext = new PageContext { HttpContext = http },
        };

        await model.OnGetAsync(default);

        // The track graphic renders as its OWN card, labelled with the track name.
        var card = Assert.Single(model.Cards, c => c.Kind == "Track graphic");
        Assert.Equal("Security", card.Title);
        Assert.True(card.HasStoredFile);
        // Download goes through the hub proxy — NEVER the raw SharePoint URL.
        Assert.Equal($"/speaker-graphic/{card.Id}", card.DownloadUrl);

        // And that proxy actually streams the bytes for this speaker's own released graphic.
        var file = await graphics.GetSpeakerGraphicFileAsync(evt.Id, speaker.Id, card.Id);
        Assert.NotNull(file);
        Assert.NotEmpty(file!.Content);
        Assert.Equal("Security.png", file.FileName);
    }

    // ---- fakes ---------------------------------------------------------------

    private sealed class NullFetcher : ISpeakerPictureFetcher
    {
        public Task<FetchedImage?> FetchAsync(string? pictureUrl, CancellationToken ct = default) =>
            Task.FromResult<FetchedImage?>(null);
    }

    /// <summary>Read-capable store: lists nothing but DOWNLOADS bytes for the proxy.</summary>
    private sealed class FakeReadStore : ISharePointFileStore
    {
        public bool CanStore => false;
        public bool CanRead => true;
        public Task<IReadOnlyList<SharePointFileRef>> ListAsync(string relativeFolder, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SharePointFileRef>>(Array.Empty<SharePointFileRef>());
        public Task<byte[]?> DownloadAsync(string itemId, CancellationToken ct = default) =>
            Task.FromResult<byte[]?>(new byte[] { 1, 2, 3 });
        public Task<StoredFile> StoreAsync(string relativePath, byte[] content, string contentType, CancellationToken ct = default) =>
            throw new InvalidOperationException("write side not used");
        public Task DeleteAsync(string relativePath, CancellationToken ct = default) => Task.CompletedTask;
        public Task<StoredFile> UploadToFolderAsync(string relativeFolder, string fileName, byte[] content, string contentType, CancellationToken ct = default) =>
            throw new InvalidOperationException("write side not used");
        public Task DeleteFromFolderAsync(string relativeFolder, string fileName, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoopEmail : IEmailSender
    {
        public Task SendAsync(string to, string s, string h, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendAsync(string to, string s, string h, IReadOnlyCollection<string>? cc, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendAsync(string to, string s, string h, string t, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendWithIcsAsync(string to, string s, string h, string ics, string fn, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendWithAttachmentsAsync(string to, string s, string h, IReadOnlyCollection<EmailAttachment> a, CancellationToken ct = default) => Task.CompletedTask;
    }
}
