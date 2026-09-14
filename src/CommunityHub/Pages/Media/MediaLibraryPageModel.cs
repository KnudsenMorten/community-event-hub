using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Graphics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Media;

/// <summary>
/// §1078 — the shared behaviour of the two media libraries (pictures, video). One page shape, two
/// folders; the subclass says which.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-11: <i>"Add 'Media Management' in main menu with 2 menu-items: Picture
/// upload management … Video upload management … Both links give the media team permissions with the
/// app so they have full permissions to add/delete files. it should not run in their user context,
/// but through the app context"</i>.</para>
///
/// <para>🔴 <b>This page exists because a link could not do what he asked.</b> The two URLs he
/// supplied open SharePoint in the VISITOR's own session — their user context, their tenant account,
/// their permissions on the library. Every file surface in the hub already works the other way (§160:
/// speakers, sponsors and attendees never receive a SharePoint link), and that is what "through the
/// app context" means in practice: the hub lists, uploads, downloads and deletes with the app's own
/// SharePoint credentials, and the media crew simply sign in here.</para>
///
/// <para>🔒 <b>Media + Organizer only.</b> <c>ParticipantRole.Media</c> already existed — press /
/// photo / video crew — so this needed no new role, no migration and no change to the auth model.</para>
/// </remarks>
[Authorize]
[RequestSizeLimit(1_073_741_824)]
[RequestFormLimits(MultipartBodyLengthLimit = 1_073_741_824)]
public abstract class MediaLibraryPageModel : PageModel
{
    private readonly MediaLibraryService _library;
    private readonly ICurrentParticipantAccessor _participant;

    protected MediaLibraryPageModel(MediaLibraryService library, ICurrentParticipantAccessor participant)
    {
        _library = library;
        _participant = participant;
    }

    /// <summary>Which folder this page manages.</summary>
    public abstract MediaLibraryKind Kind { get; }

    /// <summary>The heading, in the operator's own words for the menu item.</summary>
    public abstract string Heading { get; }

    /// <summary>What belongs in this folder, said plainly so the two pages are not confusable.</summary>
    public abstract string WhatBelongsHere { get; }

    public bool AccessDenied { get; private set; }
    public bool NotConfigured { get; private set; }
    public bool CanManage { get; private set; }
    public string? Message { get; private set; }
    public string? Error { get; private set; }

    /// <summary>
    /// §1078b (operator 2026-08-11: <i>"sharepoint goes to diff urls depending on env"</i>) — the
    /// folder THIS environment resolves to, shown on the page. DEV and PROD carry different roots,
    /// and two empty libraries look identical on screen.
    /// </summary>
    public string ResolvedFolder { get; private set; } = string.Empty;
    public string SiteUrl { get; private set; } = string.Empty;

    /// <summary>Set when the folder does not exist here — distinct from "it exists and is empty".</summary>
    public bool FolderMissing { get; private set; }

    /// <summary>Set when the two configuration sections disagree about site or library.</summary>
    public string? ConfigurationProblem { get; private set; }

    public IReadOnlyList<MediaFile> Files { get; private set; } = Array.Empty<MediaFile>();

    [BindProperty] public IFormFile? Upload { get; set; }

    /// <summary>Media crew and organizers. Nobody else has any business in the event's raw footage.</summary>
    private static bool MayManage(CurrentParticipant me) =>
        me.Role is ParticipantRole.Media or ParticipantRole.Organizer;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!MayManage(me)) { AccessDenied = true; return Page(); }

        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostUploadAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!MayManage(me)) return Forbid();

        if (Upload is null || Upload.Length == 0)
        {
            Error = "Choose a file first.";
            await LoadAsync(ct);
            return Page();
        }

        var reason = _library.RejectionReason(Upload.FileName);
        if (reason is not null)
        {
            Error = reason;
            await LoadAsync(ct);
            return Page();
        }

        // 🔑 STREAMED (§455): a 2 GB video must never be buffered into the web app's memory — let
        // alone twice, which is what reading it into a byte[] on a shared App Service instance does.
        await using var stream = Upload.OpenReadStream();
        var stored = await _library.UploadAsync(
            Kind, Upload.FileName, stream, Upload.Length,
            string.IsNullOrWhiteSpace(Upload.ContentType)
                ? MediaLibraryService.ContentTypeOf(Upload.FileName)
                : Upload.ContentType,
            ct);

        Message = stored
            ? $"Uploaded “{MediaLibraryService.SafeName(Upload.FileName)}”."
            : null;
        if (!stored)
            Error = "The library is not connected, so nothing was uploaded.";

        await LoadAsync(ct);
        return Page();
    }

    /// <summary>
    /// ⚠️ A real delete in the event's document library. He asked for it — <i>"full permissions to
    /// add/delete files"</i> — so the button exists and says what it does; the page asks for a
    /// confirmation because the hub cannot undo it.
    /// </summary>
    public async Task<IActionResult> OnPostDeleteAsync(string fileName, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!MayManage(me)) return Forbid();

        var removed = await _library.DeleteAsync(Kind, fileName, ct);
        if (removed) Message = $"Deleted “{MediaLibraryService.SafeName(fileName)}”.";
        else Error = "That file is no longer there — nothing was deleted.";

        await LoadAsync(ct);
        return Page();
    }

    /// <summary>
    /// Download THROUGH the hub (the §160 rule): the bytes are streamed with the app's credentials,
    /// so a media person never needs — and never receives — a SharePoint URL.
    /// </summary>
    public async Task<IActionResult> OnGetDownloadAsync(string fileName, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!MayManage(me)) return Forbid();

        var file = await _library.DownloadAsync(Kind, fileName, ct);
        if (file is null) return NotFound();

        return File(file.Content, file.ContentType, file.FileName);
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        CanManage = _library.CanManage(Kind);
        // 🔒 "Not configured" is said out loud rather than rendering as an empty folder. An empty
        // list and an unwired integration look identical to a reader, and only one of them means
        // "upload your pictures here".
        NotConfigured = !_library.CanRead(Kind);
        ConfigurationProblem = _library.ConfigurationProblem;
        ResolvedFolder = _library.ResolvedFolder(Kind);
        SiteUrl = _library.SiteUrl;
        Files = await _library.ListAsync(Kind, ct);

        // §1078b — only worth asking when the folder LOOKS empty: an empty listing is the one case
        // where "there is nothing here" and "this environment's folder does not exist" are
        // indistinguishable. A folder with files in it has answered the question already.
        if (Files.Count == 0 && !NotConfigured)
        {
            var probe = await _library.ProbeAsync(Kind, ct);
            FolderMissing = probe is { Exists: false };
        }
    }
}
