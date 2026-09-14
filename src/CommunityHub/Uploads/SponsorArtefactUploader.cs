using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;

namespace CommunityHub.Uploads;

/// <summary>
/// §600/§603 — the ONE way to receive a sponsor artefact: validate → versioned name → stream →
/// record → notify. Every surface that accepts a sponsor file calls this.
/// </summary>
/// <remarks>
/// <para><b>Why this exists now.</b> The task conversion (operator 2026-07-28: *"all sponsor tasks
/// must be revisited … i dont want to use this sponsor upload more prefer to use the newer method as
/// it handles version, better performance when uploading"*) needs a task to accept a file IN PLACE
/// rather than sending the sponsor to another page. Adding a fifth copy of the upload code to do
/// that would be the exact mistake <see cref="SponsorUploadKinds"/> was created to prevent.</para>
///
/// <para><b>The duplication was real, and is now gone (§768.14).</b> <c>NextVersionedNameAsync</c>
/// used to exist in FOUR places — <c>SponsorLogosFormService</c>, <c>CompanyDetails</c>,
/// <c>DirectUploadEndpoints</c> and here — each building the versioned name itself. §494b documented
/// what that costs: *"Two copies of those rules is exactly the shape that produced §482 (a list and
/// an editor that quietly described different things)"*, and §494d records a live instance — the
/// direct-to-storage path silently skipped the designer notification because it was a second copy.
/// All four now call <see cref="SponsorUploadKinds.NextVersionedNameAsync"/>, which names the file
/// through <see cref="SponsorUploadNaming"/> — the same code the graphics matcher reads it back
/// with.</para>
///
/// <para><b>Streaming, not buffering (§455).</b> Exhibitor-wall artwork is capped at 1 GB, so the
/// file goes straight into Graph's chunked session — peak memory is one chunk, whatever the artwork
/// weighs. Never <c>ToArray()</c> a sponsor upload.</para>
/// </remarks>
public sealed class SponsorArtefactUploader
{
    private const long Mb = 1024 * 1024;

    private readonly CommunityHubDbContext _db;
    private readonly SharePointUploadClient _sp;
    private readonly IEmailSender _email;
    private readonly EventEditionConfigLoader _cfg;
    private readonly EventConfigOptions _cfgOptions;
    private readonly Core.Integrations.DocLibrary.IDocLibraryPathResolver _paths;
    private readonly ILogger<SponsorArtefactUploader> _log;
    private readonly IEmailContextAccessor? _ctx;

    public SponsorArtefactUploader(
        CommunityHubDbContext db, SharePointUploadClient sp, IEmailSender email,
        EventEditionConfigLoader cfg, EventConfigOptions cfgOptions,
        Core.Integrations.DocLibrary.IDocLibraryPathResolver paths,
        ILogger<SponsorArtefactUploader> log,
        // §1072 — needed so the designer notice declares itself ops mail; without it the ring gate
        // drops every recipient as an unknown address and the mail vanishes silently.
        IEmailContextAccessor? ctx = null)
    {
        _db = db; _sp = sp; _email = email; _cfg = cfg; _cfgOptions = cfgOptions;
        _paths = paths; _log = log; _ctx = ctx;
    }

    /// <summary>The outcome of one upload. <paramref name="Error"/> is null on success.</summary>
    public sealed record Result(bool Ok, string? FileName, string? WebUrl, string? Error)
    {
        public static Result Fail(string error) => new(false, null, null, error);
    }

    /// <summary>
    /// Receive one artefact. Returns a user-safe error rather than throwing, so a caller can render
    /// it inline — an upload failure must never take a page down.
    /// </summary>
    public async Task<Result> UploadAsync(
        string kind, IFormFile file, int eventId, string companyId, string sponsorName,
        string byEmail, CancellationToken ct)
    {
        var sp = _cfg.Load(_cfgOptions.EventConfigPath).SharePoint;
        var spec = SponsorUploadKinds.Resolve(kind, _paths, sp);
        if (spec is null || sp is null)
            return Result.Fail("That upload isn't available right now.");

        // --- validation, stated in the sponsor's terms ---------------------------------------
        if (file is null || file.Length == 0)
            return Result.Fail("Please choose a file to upload.");
        if (file.Length > spec.MaxBytes)
            return Result.Fail($"That file is too large — the limit is {spec.MaxBytes / Mb} MB.");

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!spec.Accepts(ext))
            return Result.Fail($"That file type isn't supported here. Please upload {string.Join(" or ", spec.Exts)}.");

        try
        {
            var fileName = await SponsorUploadKinds.NextVersionedNameAsync(
                _sp, sp.SiteUrl, sp.DriveName, spec.Folder, kind, sponsorName, ext, _log, ct);

            // §455 — stream, never buffer.
            await using var stream = file.OpenReadStream();
            var (_, webUrl, _) = await _sp.UploadFileStreamAsync(
                sp.SiteUrl, sp.DriveName, spec.Folder, fileName, stream, file.Length,
                string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
                ct);

            // Shared record + notify — the SAME helpers the other paths use, so the logo pointer,
            // the §68 audit row and the designer mail cannot drift between surfaces (§494d).
            await SponsorUploadKinds.RecordAsync(
                _db, kind, eventId, companyId, fileName, webUrl, byEmail, DateTimeOffset.UtcNow, ct);
            await SponsorUploadKinds.NotifyAsync(
                _email, spec, sponsorName, fileName, webUrl, byEmail, _log, ct, _ctx);

            return new Result(true, fileName, webUrl, null);
        }
        catch (SharePointUploadException ex)
        {
            // Surface the real reason to the log, a calm one to the sponsor. Today's live example of
            // why this matters: the SharePoint client secret had expired and EVERY upload was
            // failing with AADSTS7000215 while nothing on screen said so (§599.1).
            _log.LogWarning(ex, "Sponsor artefact upload ({Kind}) failed for {Company}.", kind, companyId);
            return Result.Fail("The upload could not be saved just now. Please try again — "
                               + "if it keeps failing, tell the organizers, it is not your file.");
        }
    }

    /// <summary>
    /// File-name-safe component — letters, digits, dash, underscore. Everything else, including any
    /// path separator or traversal attempt, collapses to '-'.
    /// </summary>
    /// <remarks>
    /// ⚠️ §768.14 — no longer used for the VERSIONED sponsor uploads, which take their whole name
    /// from <see cref="SponsorUploadNaming"/> (that slug is what the graphics matcher reads back).
    /// Kept for callers that name a file from free text and have no naming contract to honour.
    /// </remarks>
    public static string Sanitize(string? s) =>
        string.IsNullOrWhiteSpace(s)
            ? "file"
            : new string(s.Trim().Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-').ToArray());
}
