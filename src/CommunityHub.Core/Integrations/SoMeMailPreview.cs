using System.Net;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// §1313 — a post exactly as it will go out: the text with every variable resolved, and the picture that will be uploaded.
/// Operator 2026-09-28: <i>"this email is missing important info as it shows only the template … i need real text and
/// picture"</i> · <i>"all the reminder emails for some messages must include the graphics that go out as well so we can
/// validate the picture as well"</i>.
/// </summary>
/// <remarks>
/// 🔒 ONE resolver for the mails AND the publish (<see cref="SoMeDispatchService"/> uses <see cref="ResolveImageAsync"/>
/// too), so the picture in a reminder cannot differ from the picture LinkedIn gets. The text is resolved with the same
/// <see cref="SoMePostComposer"/> the publish uses; a variable without a value is named, not hidden.
/// </remarks>
public sealed class SoMeMailPreview
{
    private readonly CommunityHubDbContext _db;
    private readonly SoMePostComposer? _composer;
    private readonly Graphics.ISharePointFileStore? _fileStore;
    private readonly SoMeGraphicLibrary? _mediaLibrary;
    private readonly ILogger? _log;

    public SoMeMailPreview(
        CommunityHubDbContext db, SoMePostComposer? composer = null, Graphics.ISharePointFileStore? fileStore = null,
        SoMeGraphicLibrary? mediaLibrary = null, ILogger<SoMeMailPreview>? log = null)
        : this(db, composer, fileStore, mediaLibrary, (ILogger?)log) { }

    internal SoMeMailPreview(
        CommunityHubDbContext db, SoMePostComposer? composer, Graphics.ISharePointFileStore? fileStore,
        SoMeGraphicLibrary? mediaLibrary, ILogger? log)
    {
        _db = db;
        _composer = composer;
        _fileStore = fileStore;
        _mediaLibrary = mediaLibrary;
        _log = log;
    }

    /// <summary>The post as it will publish. <see cref="Picture"/> is null for a text-only or video post.</summary>
    public sealed record Preview(string Text, string? Missing, EmailAttachment? Picture, string? MediaNote);

    public async Task<Preview> BuildAsync(SoMePost post, CancellationToken ct = default)
    {
        var text = post.EffectiveText ?? string.Empty;
        string? missing = null;
        if (_composer is not null)
        {
            try
            {
                var values = await _composer.ValuesForAsync(post, ct);
                missing = SoMeEmptyVariableGate.ReasonFor(text, values);
                text = _composer.Resolve(text, values);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log?.LogWarning(ex, "§1313 post {PostId}: the text could not be resolved for the preview.", post.Id);
            }
        }

        if (post.MediaKind == SoMePostMediaKind.Video)
            return new Preview(text, missing, null, $"Video: {post.ImageRef} (not attached — too large for a mail)");

        var (bytes, _, fileName) = await ResolveImageAsync(post, ct);
        if (bytes is not { Length: > 0 })
            return new Preview(text, missing, null,
                string.IsNullOrWhiteSpace(post.ImageRef) ? "No picture — a text-only post." : $"Picture {post.ImageRef} could NOT be read — the post would go out without it.");
        // 🔴 §1312 — a sponsor picture is STORED under the company id (sponsor-ceh-14ca083d.png — the stable key every
        // sponsor's graphic uses), but the mail names it after the company: "sponsor-master-packager.png", not an internal id
        // (operator 2026-09-29 on the Master Packager announcement).
        var shown = fileName ?? post.ImageRef ?? "picture";
        if (post.Type == SoMePostType.Sponsor && !string.IsNullOrWhiteSpace(post.SponsorCompanyId)
            && await SponsorCompanyNameService.LocalNameAsync(_db, post.SponsorCompanyId, ct) is { } company
            && CommunityHub.Uploads.SponsorUploadNaming.Slug(company) is { Length: > 0 } slug)
            shown = $"sponsor-{slug}{Path.GetExtension(shown)}";
        var name = $"post-{post.Id}-{SafeName(shown)}";
        return new Preview(text, missing, new EmailAttachment(name, bytes, ContentType(name)), $"Picture attached: {shown}");
    }

    /// <summary>The preview as mail HTML: the text as it will publish, and where the picture is.</summary>
    public static string Html(Preview p, string heading = "Post text (as it will publish)")
    {
        var html = $"<p><strong>{WebUtility.HtmlEncode(heading)}:</strong><br/>"
                   + WebUtility.HtmlEncode(p.Text).Replace("\n", "<br/>") + "</p>";
        if (!string.IsNullOrWhiteSpace(p.Missing))
            html += $"<p style=\"color:#b91c1c;\"><strong>Missing:</strong> {WebUtility.HtmlEncode(p.Missing)} — the post waits until it is filled in.</p>";
        if (!string.IsNullOrWhiteSpace(p.MediaNote))
            html += $"<p><strong>Media:</strong> {WebUtility.HtmlEncode(p.MediaNote)}</p>";
        return html;
    }

    /// <summary>
    /// The picture a post publishes with — the subject's CURRENT graphic for types 1–4 (§917), else the stored reference;
    /// from the generated-graphics store or the media library (§828/§844). Fail-soft: (null, …) when it cannot be read.
    /// </summary>
    public async Task<(byte[]? Bytes, string? Alt, string? FileName)> ResolveImageAsync(SoMePost post, CancellationToken ct = default)
    {
        // 🔴 §917 — THE SUBJECT'S CURRENT GRAPHIC WINS OVER THE ONE STAMPED AT PLAN TIME (a second speaker joining a
        // session turns session-12.png into session-12.gif). Types 1–4 only; Type 5 and ad-hoc posts keep their own ref.
        var imageRef = post.ImageRef;
        var current = await new SoMeSubjectGraphic(_db).CurrentFileNameAsync(post, ct);
        if (!string.IsNullOrWhiteSpace(current) && !string.Equals(current, imageRef, StringComparison.OrdinalIgnoreCase))
        {
            _log?.LogInformation(
                "§917: post {PostId} uses its subject's CURRENT graphic {Current} (the post was planned with {Stamped}).",
                post.Id, current, imageRef ?? "none");
            imageRef = current;
        }
        if (string.IsNullOrWhiteSpace(imageRef)) return (null, null, null);

        try
        {
            // The legacy path: a generated graphic tracked as a GraphicAssets row, referenced by its SharePoint URL or path.
            if (_fileStore is { CanRead: true })
            {
                var graphic = await _db.GraphicAssets.AsNoTracking().FirstOrDefaultAsync(
                    g => g.EventId == post.EventId && (g.SharePointUrl == imageRef || g.SharePointPath == imageRef), ct);
                if (graphic?.StorageItemId is { Length: > 0 } itemId)
                {
                    var bytes = await _fileStore.DownloadAsync(itemId, ct);
                    if (bytes is { Length: > 0 }) return (bytes, "Session graphic", LastSegment(imageRef));
                }
            }

            // §828/§844: a bare FILE NAME in the library folder for this post's type.
            if (_mediaLibrary is not null)
            {
                var file = await _mediaLibrary.GetAsync(imageRef, post.TemplateKind, SoMePostMediaKind.Graphic, ct);
                if (file is not null) return (file.Content, "Experts Live Denmark", LastSegment(imageRef));
            }
            return (null, null, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.LogWarning(ex, "SoMe: image resolve failed for post {PostId}.", post.Id);
            return (null, null, null);
        }
    }

    private static string LastSegment(string reference)
    {
        var s = reference.Split('?')[0].TrimEnd('/');
        var i = s.LastIndexOfAny(new[] { '/', '\\' });
        return Uri.UnescapeDataString(i >= 0 ? s[(i + 1)..] : s);
    }

    private static string SafeName(string name)
    {
        var n = LastSegment(name);
        foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '-');
        return n.Length == 0 ? "picture.png" : n;
    }

    private static string ContentType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => "image/png",
    };
}
