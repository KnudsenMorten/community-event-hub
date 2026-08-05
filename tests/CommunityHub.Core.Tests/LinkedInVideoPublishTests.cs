using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CommunityHub.Core.Integrations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §844 — NATIVE VIDEO PUBLISHING: the multipart upload, and the two rules that protect him.
///
/// <para>🔒 §844.2 (operator 2026-08-05: <i>"we prefer videos more than graphics, but fallback is
/// graphics for us"</i>) — the fallback is for "there is no video". Once a video is attached the post
/// publishes as a video or fails; it must NEVER quietly downgrade to the graphic, because he would
/// have no way to know the lesser asset went out.</para>
///
/// <para>🔴 And a video is <b>not postable the moment the bytes land</b> — LinkedIn processes it
/// asynchronously, so a post attached too early has a video that never plays.</para>
/// </summary>
public sealed class LinkedInVideoPublishTests
{
    private sealed class RouteHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string, HttpResponseMessage> _responder;
        public List<string> Urls { get; } = new();
        public List<string> Bodies { get; } = new();

        public RouteHandler(Func<HttpRequestMessage, string, HttpResponseMessage> responder) =>
            _responder = responder;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var url = req.RequestUri!.ToString();
            Urls.Add(url);
            var body = req.Content is null ? string.Empty : await req.Content.ReadAsStringAsync(ct);
            Bodies.Add(body);
            return _responder(req, url);
        }
    }

    private const string VideoUrn = "urn:li:video:C123";

    private static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    /// <summary>Two upload parts, so the multipart path is genuinely exercised.</summary>
    private static string InitBody() => JsonSerializer.Serialize(new
    {
        value = new
        {
            video = VideoUrn,
            uploadToken = "tok-123",
            uploadInstructions = new[]
            {
                new { uploadUrl = "https://upload.example/part1", firstByte = 0, lastByte = 3 },
                new { uploadUrl = "https://upload.example/part2", firstByte = 4, lastByte = 7 },
            },
        },
    });

    private static RouteHandler Handler(string videoStatus, List<string>? partsSeen = null) =>
        new((req, url) =>
        {
            if (url.Contains("videos?action=initializeUpload")) return Json(InitBody());

            if (url.StartsWith("https://upload.example/"))
            {
                partsSeen?.Add(url);
                var ok = new HttpResponseMessage(HttpStatusCode.OK);
                ok.Headers.ETag = new EntityTagHeaderValue("\"etag-" + url[^1] + "\"");
                return ok;
            }

            if (url.Contains("videos?action=finalizeUpload")) return new HttpResponseMessage(HttpStatusCode.OK);

            if (url.Contains("/rest/videos/")) return Json(JsonSerializer.Serialize(new { status = videoStatus }));

            if (url.EndsWith("/rest/posts"))
            {
                var r = new HttpResponseMessage(HttpStatusCode.Created);
                r.Headers.TryAddWithoutValidation("x-restli-id", "urn:li:share:999");
                return r;
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });

    private static LinkedInOptions Live() =>
        new() { Enabled = true, AccessToken = "tok", DryRun = false };

    private static LinkedInPost VideoPost(byte[] video) =>
        new("12345", "Hello!", "clip.mp4", Array.Empty<string>(), VideoBytes: video);

    [Fact]
    public async Task A_video_is_uploaded_in_parts_finalized_and_attached_to_the_post()
    {
        var parts = new List<string>();
        var h = Handler("AVAILABLE", parts);
        var pub = new LiveLinkedInPostPublisher(new HttpClient(h), Live(), NullLogger<LiveLinkedInPostPublisher>.Instance);

        var result = await pub.PublishAsync(VideoPost(new byte[8]));

        Assert.True(result.Published);

        // Every part was PUT — a single upload would silently truncate an 80 MB file (§844.7).
        Assert.Equal(2, parts.Count);

        Assert.Contains(h.Urls, u => u.Contains("videos?action=initializeUpload"));
        Assert.Contains(h.Urls, u => u.Contains("videos?action=finalizeUpload"));

        // finalize must carry BOTH ETags, in order — that is what LinkedIn verifies the parts by.
        var finalize = h.Bodies[h.Urls.FindIndex(u => u.Contains("finalizeUpload"))];
        Assert.Contains("etag-1", finalize);
        Assert.Contains("etag-2", finalize);

        // The post references the VIDEO urn.
        var postBody = h.Bodies[h.Urls.FindIndex(u => u.EndsWith("/rest/posts"))];
        Assert.Contains(VideoUrn, postBody);
    }

    /// <summary>
    /// 🔴 The rule that protects him: still-processing means WAIT, not publish-the-graphic.
    /// </summary>
    [Fact]
    public async Task A_video_still_processing_holds_the_post_and_never_downgrades_to_the_graphic()
    {
        var h = Handler("PROCESSING");
        var pub = new LiveLinkedInPostPublisher(new HttpClient(h), Live(), NullLogger<LiveLinkedInPostPublisher>.Instance);

        var post = new LinkedInPost(
            "12345", "Hello!", "clip.mp4", Array.Empty<string>(),
            ImageBytes: new byte[] { 1, 2, 3 },   // a graphic IS available — and must not be used
            VideoBytes: new byte[8]);

        var result = await pub.PublishAsync(post);

        Assert.False(result.Published);

        // 🔒 NOTHING was posted — the graphic did not go out in the video's place.
        Assert.DoesNotContain(h.Urls, u => u.EndsWith("/rest/posts"));
        Assert.DoesNotContain(h.Urls, u => u.Contains("images?action=initializeUpload"));

        // And he is told why, in terms that say it will still go out.
        Assert.Contains("still being processed", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_unreadable_video_status_is_treated_as_not_ready_rather_than_assumed_fine()
    {
        // Holding a post for one more tick costs minutes; publishing a video that never plays is
        // public and permanent. Any doubt ⇒ not ready.
        var h = new RouteHandler((req, url) =>
            url.Contains("initializeUpload") ? Json(InitBody())
            : url.StartsWith("https://upload.example/") ? Etagged()
            : url.Contains("finalizeUpload") ? new HttpResponseMessage(HttpStatusCode.OK)
            : url.Contains("/rest/videos/") ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : new HttpResponseMessage(HttpStatusCode.Created));

        var pub = new LiveLinkedInPostPublisher(new HttpClient(h), Live(), NullLogger<LiveLinkedInPostPublisher>.Instance);

        var result = await pub.PublishAsync(VideoPost(new byte[8]));

        Assert.False(result.Published);
        Assert.DoesNotContain(h.Urls, u => u.EndsWith("/rest/posts"));

        static HttpResponseMessage Etagged()
        {
            var r = new HttpResponseMessage(HttpStatusCode.OK);
            r.Headers.ETag = new EntityTagHeaderValue("\"e\"");
            return r;
        }
    }

    /// <summary>
    /// 🔒 §844 (operator 2026-08-05: <i>"it is very important that when you upload vidos it is not
    /// per url, but using upload as it will then autoplay inside linkedin"</i>).
    /// </summary>
    /// <remarks>
    /// A URL posts as a LINK PREVIEW CARD — a thumbnail the viewer must click, which leaves LinkedIn.
    /// A natively uploaded video plays IN THE FEED, and autoplays. They are different products from
    /// the reader's side, so this asserts the mechanism rather than trusting it stays correct:
    /// the post body must carry the <c>urn:li:video:</c> id and must NOT carry the file name or any
    /// http reference as its content.
    /// </remarks>
    [Fact]
    public async Task A_video_is_posted_as_an_UPLOADED_asset_never_as_a_url()
    {
        var h = Handler("AVAILABLE");
        var pub = new LiveLinkedInPostPublisher(new HttpClient(h), Live(), NullLogger<LiveLinkedInPostPublisher>.Instance);

        // ImageRef names a file AND looks like something that could be turned into a link.
        var post = new LinkedInPost(
            "12345", "Watch this!", "https://example.com/clip.mp4", Array.Empty<string>(),
            VideoBytes: new byte[8]);

        var result = await pub.PublishAsync(post);
        Assert.True(result.Published);

        var body = h.Bodies[h.Urls.FindIndex(u => u.EndsWith("/rest/posts"))];

        // The uploaded asset is what is attached.
        Assert.Contains("\"id\":\"" + VideoUrn + "\"", body.Replace(" ", string.Empty));

        // 🔒 And the reference is NOT sent as content — no article/link card, no file URL.
        Assert.DoesNotContain("https://example.com/clip.mp4", body);
        Assert.DoesNotContain("\"article\"", body);
    }

    [Fact]
    public async Task A_post_with_no_video_still_uses_the_graphic()
    {
        // §844.2 — the fallback is real and must keep working: video preferred, graphic otherwise.
        var h = ImageHandler();

        var pub = new LiveLinkedInPostPublisher(new HttpClient(h), Live(), NullLogger<LiveLinkedInPostPublisher>.Instance);

        var result = await pub.PublishAsync(new LinkedInPost(
            "12345", "Hello!", "card.png", Array.Empty<string>(), ImageBytes: new byte[] { 1, 2, 3 }));

        Assert.True(result.Published);
        Assert.Contains(h.Urls, u => u.Contains("images?action=initializeUpload"));
        Assert.DoesNotContain(h.Urls, u => u.Contains("videos?action="));
    }

    /// <summary>
    /// 🔒 §844.10 (operator 2026-08-05: <i>"i dont want url uploads of either images or videos -
    /// only real upload"</i>) — the same rule as the video one, applied to IMAGES.
    /// </summary>
    /// <remarks>
    /// An image referenced by URL renders as a LINK CARD; a natively uploaded one renders as the
    /// picture. The image path already uploaded bytes (§324) — this pins it, so a future change
    /// cannot turn <c>ImageRef</c> into a posted link without failing here.
    /// </remarks>
    [Fact]
    public async Task An_image_is_posted_as_an_UPLOADED_asset_never_as_a_url()
    {
        var h = ImageHandler();
        var pub = new LiveLinkedInPostPublisher(new HttpClient(h), Live(), NullLogger<LiveLinkedInPostPublisher>.Instance);

        var result = await pub.PublishAsync(new LinkedInPost(
            "12345", "Hello!", "https://example.com/card.png", Array.Empty<string>(),
            ImageBytes: new byte[] { 1, 2, 3 }));

        Assert.True(result.Published);

        var body = h.Bodies[h.Urls.FindIndex(u => u.EndsWith("/rest/posts"))];
        Assert.Contains("urn:li:image:I1", body);
        Assert.DoesNotContain("https://example.com/card.png", body);
        Assert.DoesNotContain("\"article\"", body);
    }

    /// <summary>
    /// 🔒 §844.10 — with NO bytes there is still no URL fallback: the post goes out as TEXT ONLY.
    /// </summary>
    /// <remarks>
    /// This is the tempting shortcut the rule forbids — "we have a reference, post it as a link".
    /// A link card is not the picture he made, so text-only is the honest outcome.
    /// </remarks>
    [Fact]
    public async Task A_reference_without_bytes_never_becomes_a_link_card()
    {
        var h = ImageHandler();
        var pub = new LiveLinkedInPostPublisher(new HttpClient(h), Live(), NullLogger<LiveLinkedInPostPublisher>.Instance);

        var result = await pub.PublishAsync(new LinkedInPost(
            "12345", "Hello!", "https://example.com/card.png", Array.Empty<string>()));

        Assert.True(result.Published);
        Assert.DoesNotContain(h.Urls, u => u.Contains("action=initializeUpload"));

        var body = h.Bodies[h.Urls.FindIndex(u => u.EndsWith("/rest/posts"))];
        Assert.DoesNotContain("https://example.com/card.png", body);
        Assert.DoesNotContain("\"content\"", body);
    }

    private static RouteHandler ImageHandler() =>
        new((req, url) =>
            url.Contains("images?action=initializeUpload")
                ? Json(JsonSerializer.Serialize(new { value = new { uploadUrl = "https://upload.example/img", image = "urn:li:image:I1" } }))
            : url.StartsWith("https://upload.example/") ? new HttpResponseMessage(HttpStatusCode.OK)
            : CreatedResponse());

    private static HttpResponseMessage CreatedResponse()
    {
        var r = new HttpResponseMessage(HttpStatusCode.Created);
        r.Headers.TryAddWithoutValidation("x-restli-id", "urn:li:share:1");
        return r;
    }
}
