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
/// §172/§281 — the speaker "Help Promote" page gives EACH session/master-class graphic card its
/// OWN LinkedIn + X share, with post text tailored to THAT session. §281 (operator 2026-07-10):
/// the post uses the operator's fixed format and its single link is the AGENDA &amp; TICKETS site
/// (<c>https://eldk27.expertslive.dk</c>) — the per-session <c>/Sessions/{id}</c> link was
/// superseded. Drives the real page handler over a fake speaker session + read-store. Verifies:
/// per-card share text contains the session TITLE and exactly the one agenda link, the two
/// cards' texts DIFFER, the X intent is the X composer, a TRACK card's share links to the public
/// list filtered by track, and a plain speaker-headshot card stays download-only (no share).
/// FAKE names only.
/// </summary>
public sealed class HelpPromotePerSessionShareTests
{
    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"help-promote-share-{Guid.NewGuid():N}")
            .Options);

    private sealed class HttpContextAccessorOver(HttpContext ctx) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => ctx; set { } }
    }

    private static ClaimsPrincipal Principal(Participant p)
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

    private static CommunityHub.Pages.Speaker.GraphicsModel NewModel(
        CommunityHubDbContext db, GraphicsService graphics, Participant speaker)
    {
        var http = new DefaultHttpContext { User = Principal(speaker) };
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("hub.example.test");
        // §326p: the self-post feature (and its ctor plumbing) is removed again.
        return new CommunityHub.Pages.Speaker.GraphicsModel(
            db, new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http)),
            graphics, Publish(db, graphics))
        {
            PageContext = new PageContext { HttpContext = http },
        };
    }

    [Fact]
    public async Task Each_session_card_has_its_own_share_with_title_and_agenda_link_and_they_differ()
    {
        using var db = NewDb();

        var evt = new Event
        {
            Code = "ELDK27", DisplayName = "Community Events Demo 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
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

        var talk = new Session
        {
            EventId = evt.Id, SessionizeId = Guid.NewGuid().ToString("N"),
            Title = "Cloud Native Talk", Type = SessionType.TechnicalSession,
        };
        var masterClass = new Session
        {
            EventId = evt.Id, SessionizeId = Guid.NewGuid().ToString("N"),
            Title = "Hands On Lab", Type = SessionType.MasterClass,
        };
        db.Sessions.AddRange(talk, masterClass);
        await db.SaveChangesAsync();

        db.GraphicAssets.AddRange(
            new GraphicAsset
            {
                EventId = evt.Id, Type = GraphicAssetType.Session,
                StableKey = GraphicStableKey.ForSession(talk.Id, speaker.Id),
                ParticipantId = speaker.Id, SessionId = talk.Id,
                Status = GraphicAssetStatus.Released,
                StorageItemId = "item-talk.png", FileName = "talk.png",
            },
            new GraphicAsset
            {
                EventId = evt.Id, Type = GraphicAssetType.Session,
                StableKey = GraphicStableKey.ForSession(masterClass.Id, speaker.Id),
                ParticipantId = speaker.Id, SessionId = masterClass.Id,
                Status = GraphicAssetStatus.Released,
                StorageItemId = "item-mc.png", FileName = "mc.png",
            });
        await db.SaveChangesAsync();

        var graphics = Graphics(db, new FakeReadStore());
        var model = NewModel(db, graphics, speaker);

        await model.OnGetAsync(default);

        var talkCard = Assert.Single(model.Cards, c => c.Title == "Cloud Native Talk");
        var mcCard = Assert.Single(model.Cards, c => c.Title == "Hands On Lab");

        // Each card has BOTH a LinkedIn and an X share.
        Assert.NotNull(talkCard.LinkedInShare);
        Assert.NotNull(talkCard.XShare);
        Assert.NotNull(mcCard.LinkedInShare);

        // The post text is tailored to THAT session: the title in the operator's §281 format.
        Assert.Contains("Cloud Native Talk", talkCard.LinkedInShare!.Text);
        Assert.Contains("Hands On Lab", mcCard.LinkedInShare!.Text);

        // The two posts DIFFER (per-session, not one generic post).
        Assert.NotEqual(talkCard.LinkedInShare.Text, mcCard.LinkedInShare.Text);

        // §281 (supersedes §196's per-session link): the post carries EXACTLY ONE link — the
        // AGENDA & TICKETS site (https://eldk27.expertslive.dk) — so LinkedIn/X card that page.
        // The per-session /Sessions/{id} URL and the old "Get your ticket" line are GONE.
        // Holds for both LinkedIn and X.
        foreach (var draft in new[] { talkCard.LinkedInShare, talkCard.XShare!, mcCard.LinkedInShare })
        {
            Assert.DoesNotContain("Get your ticket", draft.Text);
            Assert.DoesNotContain("hub.example.test/Sessions/", draft.Text);
            Assert.Contains("Agenda & tickets: https://eldk27.expertslive.dk", draft.Text);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(draft.Text, "https?://"));
        }

        // §326y (operator's plan B): the LinkedIn intent PREFILLS the composer with the
        // post text AND carries the session URL, so LinkedIn renders that URL's OG card
        // (= the session graphic) — text + picture, ready to Post.
        Assert.StartsWith("https://www.linkedin.com/feed/?shareActive=true&text=", talkCard.LinkedInShare.IntentUrl);
        var prefill = Uri.UnescapeDataString(talkCard.LinkedInShare.IntentUrl.Split("text=")[1]);
        // The killer bug: a literal '&' truncates the prefill at LinkedIn's re-parse (the
        // live post stopped at "…Network with peers"). The prefill must carry NONE.
        Assert.DoesNotContain("&", prefill);
        Assert.Contains("Network with peers and friends", prefill);
        // §326z/§326ab: the CTA points at the external ticket site; the hashtag line tags
        // the community page; and the session URL (the card source — its og:image is the
        // graphic) is the BARE last line, with NO prefix (operator: "never show 'My
        // session'").
        Assert.Contains("👉 Agenda and tickets: https://eldk27.expertslive.dk", prefill);
        Assert.DoesNotContain("My session", prefill);
        // §326ac: no raw company-page URL — a plain-text intent cannot carry a real
        // @-mention (that is an API annotation), so the page is tagged by the speaker.
        Assert.Contains("#ELDK27 #ExpertsLiveDK", prefill);
        Assert.DoesNotContain("linkedin.com/company", prefill);
        var lastLine = prefill.Split('\n')[^1].Trim();
        Assert.StartsWith("https://", lastLine);        // bare URL, no label
        Assert.Contains("/Sessions/", lastLine);        // …and it is the card source
        // The FULL §281 body stays on the draft (the clipboard source), agenda link intact.
        Assert.Contains("Agenda & tickets: https://eldk27.expertslive.dk", talkCard.LinkedInShare.Text);
        Assert.StartsWith("https://twitter.com/intent/tweet?text=", talkCard.XShare!.IntentUrl);
        Assert.Contains(Uri.EscapeDataString("Cloud Native Talk"), talkCard.XShare.IntentUrl);
        Assert.Contains("&url=" + Uri.EscapeDataString("http"), talkCard.XShare.IntentUrl);

        // Download is still offered, via the hub proxy (never a raw SharePoint URL).
        Assert.Equal($"/speaker-graphic/{talkCard.Id}", talkCard.DownloadUrl);
    }

    [Fact]
    public async Task Track_card_share_links_to_the_track_filtered_public_list_and_headshot_is_download_only()
    {
        using var db = NewDb();

        var evt = new Event
        {
            Code = "ELDK27", DisplayName = "Community Events Demo 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
            IsActive = true,
        };
        db.Events.Add(evt);
        await db.SaveChangesAsync();

        var speaker = new Participant
        {
            EventId = evt.Id, Email = "speaker.two@example.test",
            FullName = "Session Speaker Two", Role = ParticipantRole.Speaker,
        };
        db.Participants.Add(speaker);
        await db.SaveChangesAsync();

        var session = new Session
        {
            EventId = evt.Id, SessionizeId = Guid.NewGuid().ToString("N"),
            Title = "Security Deep Dive", Type = SessionType.TechnicalSession, Track = "Security",
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();

        db.GraphicAssets.AddRange(
            // A speaker HEADSHOT graphic — no session ⇒ download-only, no share.
            new GraphicAsset
            {
                EventId = evt.Id, Type = GraphicAssetType.Speaker,
                StableKey = GraphicStableKey.ForSpeaker(speaker.Id),
                ParticipantId = speaker.Id,
                Status = GraphicAssetStatus.Released,
                StorageItemId = "item-headshot.png", FileName = "headshot.png",
            },
            // A TRACK graphic — share links to the public list filtered by that track.
            new GraphicAsset
            {
                EventId = evt.Id, Type = GraphicAssetType.Track,
                StableKey = GraphicStableKey.ForTrack("security", speaker.Id),
                ParticipantId = speaker.Id, SessionId = session.Id,
                Status = GraphicAssetStatus.Released,
                StorageItemId = "item-track.png", FileName = "Security.png",
            });
        await db.SaveChangesAsync();

        var graphics = Graphics(db, new FakeReadStore());
        var model = NewModel(db, graphics, speaker);

        await model.OnGetAsync(default);

        var headshot = Assert.Single(model.Cards, c => c.Kind == "Speaker graphic");
        Assert.Null(headshot.LinkedInShare);
        Assert.Null(headshot.XShare);
        Assert.Equal($"/speaker-graphic/{headshot.Id}", headshot.DownloadUrl);

        var trackCard = Assert.Single(model.Cards, c => c.Kind == "Track graphic");
        Assert.Equal("Security", trackCard.Title);
        Assert.NotNull(trackCard.LinkedInShare);
        Assert.Contains("Security", trackCard.LinkedInShare!.Text);
        Assert.Contains($"https://hub.example.test/Sessions?FilterTrack={Uri.EscapeDataString("Security")}",
            trackCard.LinkedInShare.Text);
    }

    // ---- fakes ---------------------------------------------------------------

    private sealed class NullFetcher : ISpeakerPictureFetcher
    {
        public Task<FetchedImage?> FetchAsync(string? pictureUrl, CancellationToken ct = default) =>
            Task.FromResult<FetchedImage?>(null);
    }

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
