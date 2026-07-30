using CommunityHub.Auth;
using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Uploads;

/// <summary>
/// §494 — DIRECT-TO-STORAGE uploads for sponsor booth collateral.
///
/// <para><b>The problem this closes.</b> §455 stopped the app holding whole files in memory, but
/// every byte still travelled browser → App Service → SharePoint. The request thread stayed
/// occupied for the entire upload, so a 25 MB brochure on a hotel wifi connection tied up a worker
/// for minutes and competed with page traffic on a shared instance. That is the last version of
/// "a big upload can hurt everyone else".</para>
///
/// <para><b>The shape.</b> Two small JSON calls bracket an upload the app never sees:</para>
/// <list type="number">
///   <item><b>begin</b> — the server resolves the destination from the SIGNED-IN user's own
///   company, asks Graph for an upload session, and returns the pre-authenticated URL.</item>
///   <item>the BROWSER PUTs the file straight to that URL, in chunks, bypassing us entirely.</item>
///   <item><b>complete</b> — the server VERIFIES the item exists in storage and only then records
///   the database row.</item>
/// </list>
///
/// <para><b>Security, and why each rule is here:</b></para>
/// <list type="bullet">
///   <item>The browser NEVER supplies a folder or path. It sends a display name; the server derives
///   the folder from configuration and the file name from the caller's own company. Otherwise an
///   upload URL becomes a write primitive for anywhere in the drive.</item>
///   <item>Both endpoints re-resolve the company from the SIGNED-IN participant. A posted company
///   id would let one sponsor write into another's folder.</item>
///   <item><b>complete</b> trusts storage, not the client: it reads the item back and uses the
///   size/URL Graph reports. A client claiming "done" for a file it never sent records nothing.</item>
///   <item>Type and size limits are enforced at <b>begin</b>, before any URL is issued, and the
///   recorded size is re-checked at <b>complete</b> — so a client cannot ask for a 1 MB PDF and
///   then push 2 GB through the session.</item>
/// </list>
/// </summary>
public static class DirectUploadEndpoints
{
    private const long MaxBytes = 25L * 1024 * 1024;
    private const int MaxCollateral = 6;
    private static readonly string[] AllowedExts = { ".jpg", ".jpeg", ".png", ".pdf" };

    public sealed record BeginRequest(string FileName, long SizeBytes);
    public sealed record BeginResponse(string UploadUrl, string Path);
    public sealed record CompleteRequest(string Path, string FileName);

    public sealed record DeckBeginRequest(int SessionId, string Kind, string FileName, long SizeBytes);
    public sealed record DeckCompleteRequest(int SessionId, string Kind, string Path);

    public static void MapDirectUploadEndpoints(this WebApplication app)
    {
        MapSponsorAssetEndpoints(app);
        MapSpeakerDeckEndpoints(app);

        // ---- BEGIN: issue an upload URL for THIS sponsor's collateral folder ------------------
        app.MapPost("/sponsor/uploads/collateral/begin", async (
                BeginRequest req,
                ICurrentParticipantAccessor participant,
                CommunityHubDbContext db,
                EventEditionConfigLoader cfg,
                EventConfigOptions cfgOptions,
                SharePointUploadClient sp,
                ILoggerFactory logs,
                CancellationToken ct) =>
        {
            var me = participant.Current;
            if (me is null) return Results.Unauthorized();

            // The company comes from the SIGNED-IN row, never from the request body.
            var companyId = await db.Participants
                .Where(p => p.Id == me.ParticipantId && p.Role == ParticipantRole.Sponsor)
                .Select(p => p.SponsorCompanyId)
                .FirstOrDefaultAsync(ct);
            if (string.IsNullOrWhiteSpace(companyId)) return Results.Forbid();

            if (req.SizeBytes <= 0 || req.SizeBytes > MaxBytes)
            {
                return Results.BadRequest(new { error = $"File is too large (max {MaxBytes / (1024 * 1024)} MB)." });
            }

            var ext = Path.GetExtension(req.FileName ?? string.Empty).ToLowerInvariant();
            if (!AllowedExts.Contains(ext))
            {
                return Results.BadRequest(new { error = $"Unsupported file type. Allowed: {string.Join(", ", AllowedExts)}." });
            }

            var count = await db.SponsorBoothMaterials.CountAsync(
                m => m.EventId == me.EventId && m.SponsorCompanyId == companyId
                     && m.Kind == BoothMaterialKind.Collateral, ct);
            if (count >= MaxCollateral)
            {
                return Results.BadRequest(new { error = $"You can add up to {MaxCollateral} collateral files." });
            }

            var config = cfg.Load(cfgOptions.EventConfigPath).SharePoint;
            if (config is null || string.IsNullOrWhiteSpace(config.SiteUrl)
                || string.IsNullOrWhiteSpace(config.BoothCollateralFolderPath) || !sp.IsConfigured)
            {
                return Results.BadRequest(new { error = "File uploads aren't available right now." });
            }

            // SERVER-side name: the sponsor's own company + a sanitized display name. The client's
            // string only ever contributes characters, never structure.
            var baseName = Sanitize(Path.GetFileNameWithoutExtension(req.FileName ?? "file"));
            var storedName = $"{Sanitize(companyId)}_{baseName}{ext}";

            try
            {
                var (uploadUrl, path) = await sp.BeginDirectUploadAsync(
                    config.SiteUrl, config.DriveName, config.BoothCollateralFolderPath, storedName, ct);
                return Results.Ok(new BeginResponse(uploadUrl, path));
            }
            catch (Exception ex)
            {
                logs.CreateLogger("DirectUpload").LogError(ex,
                    "Could not start a direct upload for company {Co}.", companyId);
                return Results.BadRequest(new { error = "The upload could not be started. Please try again." });
            }
        }).RequireAuthorization();

        // ---- COMPLETE: verify against storage, then record ------------------------------------
        app.MapPost("/sponsor/uploads/collateral/complete", async (
                CompleteRequest req,
                ICurrentParticipantAccessor participant,
                CommunityHubDbContext db,
                EventEditionConfigLoader cfg,
                EventConfigOptions cfgOptions,
                SharePointUploadClient sp,
                CancellationToken ct) =>
        {
            var me = participant.Current;
            if (me is null) return Results.Unauthorized();

            var companyId = await db.Participants
                .Where(p => p.Id == me.ParticipantId && p.Role == ParticipantRole.Sponsor)
                .Select(p => p.SponsorCompanyId)
                .FirstOrDefaultAsync(ct);
            if (string.IsNullOrWhiteSpace(companyId)) return Results.Forbid();

            var config = cfg.Load(cfgOptions.EventConfigPath).SharePoint;
            if (config is null || string.IsNullOrWhiteSpace(config.BoothCollateralFolderPath))
            {
                return Results.BadRequest(new { error = "File uploads aren't available right now." });
            }

            // The path must be INSIDE this sponsor's configured folder AND carry their company
            // prefix. Without both checks a client could "complete" someone else's upload and
            // attach it to their own company.
            var expectedPrefix = config.BoothCollateralFolderPath.TrimEnd('/') + "/";
            var expectedName = Sanitize(companyId) + "_";
            var path = req.Path ?? string.Empty;
            if (!path.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(path).StartsWith(expectedName, StringComparison.OrdinalIgnoreCase))
            {
                return Results.Forbid();
            }

            // TRUST STORAGE, NOT THE CLIENT: if the file is not really there, record nothing.
            var item = await sp.GetItemByPathAsync(config.SiteUrl, config.DriveName, path, ct);
            if (item is null) return Results.BadRequest(new { error = "The upload did not complete. Please try again." });
            if (item.SizeBytes is long got && got > MaxBytes)
            {
                return Results.BadRequest(new { error = "The uploaded file is larger than allowed." });
            }

            db.SponsorBoothMaterials.Add(new SponsorBoothMaterial
            {
                EventId = me.EventId,
                SponsorCompanyId = companyId!,
                Kind = BoothMaterialKind.Collateral,
                Url = item.WebUrl ?? string.Empty,
                FileName = string.IsNullOrWhiteSpace(req.FileName) ? item.Name : req.FileName,
                CreatedByEmail = me.Email,
            });
            await db.SaveChangesAsync(ct);

            return Results.Ok(new { ok = true, name = item.Name });
        }).RequireAuthorization();
    }

    /// <summary>
    /// §494b — the same begin/complete pair for the VERSIONED sponsor assets: exhibitor wall and
    /// the three logos. One kind-driven route instead of four copies, because the alternative is
    /// four places to forget a rule.
    ///
    /// <para>The exhibitor wall is the reason this matters most: it is capped at <b>1 GB</b>, which
    /// was never sane to push through a request thread even while streaming.</para>
    ///
    /// <para>The file NAME is versioned server-side (<c>{Prefix}{Sponsor}_v{N}.{ext}</c>) exactly as
    /// the classic handler does, so both paths produce the same names and the §68 audit history
    /// stays continuous whichever route a sponsor used.</para>
    /// </summary>
    private static void MapSponsorAssetEndpoints(WebApplication app)
    {
        app.MapPost("/sponsor/uploads/{kind:regex(^(some|print|zoho|wall)$)}/begin", async (
                string kind,
                BeginRequest req,
                ICurrentParticipantAccessor participant,
                CommunityHubDbContext db,
                EventEditionConfigLoader cfg,
                EventConfigOptions cfgOptions,
                SharePointUploadClient sp,
                ILoggerFactory logs,
                CancellationToken ct) =>
        {
            var me = participant.Current;
            if (me is null) return Results.Unauthorized();

            var companyId = await db.Participants
                .Where(p => p.Id == me.ParticipantId && p.Role == ParticipantRole.Sponsor)
                .Select(p => p.SponsorCompanyId)
                .FirstOrDefaultAsync(ct);
            if (string.IsNullOrWhiteSpace(companyId)) return Results.Forbid();

            var config = cfg.Load(cfgOptions.EventConfigPath).SharePoint;
            var spec = SponsorUploadKinds.Resolve(kind, config);
            if (spec is null || !sp.IsConfigured)
            {
                return Results.BadRequest(new { error = "Uploads aren't available right now." });
            }

            if (req.SizeBytes <= 0 || req.SizeBytes > spec.MaxBytes)
            {
                return Results.BadRequest(new { error = $"File is too large (max {spec.MaxBytes / (1024 * 1024)} MB)." });
            }
            var ext = Path.GetExtension(req.FileName ?? string.Empty).ToLowerInvariant();
            if (!spec.Exts.Contains(ext))
            {
                return Results.BadRequest(new { error = $"Unsupported file type. Allowed: {string.Join(", ", spec.Exts)}." });
            }

            var sponsorName = await ResolveSponsorNameAsync(db, me.EventId, companyId!, ct);

            try
            {
                // Same versioning rule as the classic handler: the next free _v{N} in the folder.
                var fileName = await NextVersionedNameAsync(
                    sp, config!.SiteUrl, config.DriveName, spec.Folder, spec.Prefix,
                    Sanitize(sponsorName), ext, ct);

                var (uploadUrl, path) = await sp.BeginDirectUploadAsync(
                    config.SiteUrl, config.DriveName, spec.Folder, fileName, ct);
                return Results.Ok(new BeginResponse(uploadUrl, path));
            }
            catch (Exception ex)
            {
                logs.CreateLogger("DirectUpload").LogError(ex,
                    "Could not start a {Kind} upload for company {Co}.", kind, companyId);
                return Results.BadRequest(new { error = "The upload could not be started. Please try again." });
            }
        }).RequireAuthorization();

        app.MapPost("/sponsor/uploads/{kind:regex(^(some|print|zoho|wall)$)}/complete", async (
                string kind,
                CompleteRequest req,
                ICurrentParticipantAccessor participant,
                CommunityHubDbContext db,
                EventEditionConfigLoader cfg,
                EventConfigOptions cfgOptions,
                SharePointUploadClient sp,
                TimeProvider clock,
                CommunityHub.Core.Email.IEmailSender email,
                ILoggerFactory logs,
                CancellationToken ct) =>
        {
            var me = participant.Current;
            if (me is null) return Results.Unauthorized();

            var companyId = await db.Participants
                .Where(p => p.Id == me.ParticipantId && p.Role == ParticipantRole.Sponsor)
                .Select(p => p.SponsorCompanyId)
                .FirstOrDefaultAsync(ct);
            if (string.IsNullOrWhiteSpace(companyId)) return Results.Forbid();

            var config = cfg.Load(cfgOptions.EventConfigPath).SharePoint;
            var spec = SponsorUploadKinds.Resolve(kind, config);
            if (spec is null) return Results.BadRequest(new { error = "Uploads aren't available right now." });

            // The completed path must sit inside the folder for THIS kind. Without it, a caller
            // could complete an upload from one kind and have it recorded as another.
            var expectedPrefix = spec.Folder.TrimEnd('/') + "/";
            var path = req.Path ?? string.Empty;
            if (!path.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return Results.Forbid();
            }

            var item = await sp.GetItemByPathAsync(config!.SiteUrl, config.DriveName, path, ct);
            if (item is null) return Results.BadRequest(new { error = "The upload did not complete. Please try again." });
            if (item.SizeBytes is long got && got > spec.MaxBytes)
            {
                return Results.BadRequest(new { error = "The uploaded file is larger than allowed." });
            }

            await SponsorUploadKinds.RecordAsync(
                db, kind, me.EventId, companyId!, item.Name, item.WebUrl, me.Email, clock.GetUtcNow(), ct);

            // §494d — the SAME notification the classic handler sends. Without it a direct upload
            // lands silently and the designer never learns the artwork arrived.
            var sponsorName = await ResolveSponsorNameAsync(db, me.EventId, companyId!, ct);
            await SponsorUploadKinds.NotifyAsync(
                email, spec, sponsorName, item.Name, item.WebUrl, me.Email,
                logs.CreateLogger("DirectUpload"), ct);

            return Results.Ok(new { ok = true, name = item.Name });
        }).RequireAuthorization();
    }

    /// <summary>
    /// §494c — direct-to-storage for SPEAKER DECKS, the largest files in the product (multi-hundred-
    /// MB PowerPoints). This is where not occupying a request thread matters most: the §455 failure
    /// that started all of this was a speaker's deck upload.
    ///
    /// <para>Ownership is proved at BEGIN by
    /// <see cref="SpeakerPresentationService.ResolveDirectUploadTargetAsync"/> — which refuses a
    /// session the speaker is not on — and the resulting folder + versioned name are server-chosen,
    /// so the browser never learns or influences where the bytes land.</para>
    /// </summary>
    private static void MapSpeakerDeckEndpoints(WebApplication app)
    {
        app.MapPost("/speaker/decks/begin", async (
                DeckBeginRequest req,
                ICurrentParticipantAccessor participant,
                CommunityHub.Core.Integrations.Graphics.SpeakerPresentationService decks,
                Microsoft.Extensions.Options.IOptions<CommunityHub.Core.Integrations.Graphics.GraphicsSharePointOptions> spOpts,
                SharePointUploadClient sp,
                ILoggerFactory logs,
                CancellationToken ct) =>
        {
            var me = participant.Current;
            if (me is null) return Results.Unauthorized();
            if (me.Role != ParticipantRole.Speaker) return Results.Forbid();

            if (!TryParseKind(req.Kind, out var kind)) return Results.BadRequest(new { error = "Unknown deck kind." });
            if (!decks.CanUpload(kind) || !sp.IsConfigured)
            {
                return Results.BadRequest(new { error = "Deck uploads aren't available right now." });
            }
            if (req.SizeBytes <= 0) return Results.BadRequest(new { error = "Empty file." });

            try
            {
                // Proves the speaker owns the session AND picks the versioned name.
                var (folder, fileName) = await decks.ResolveDirectUploadTargetAsync(
                    me.EventId, me.ParticipantId, req.SessionId, kind, req.FileName, ct);

                var opts = spOpts.Value;
                var (uploadUrl, path) = await sp.BeginDirectUploadAsync(
                    opts.SiteUrl, opts.DriveName, folder, fileName, ct);
                return Results.Ok(new BeginResponse(uploadUrl, path));
            }
            catch (InvalidOperationException ex)
            {
                // Wrong type, or not the speaker's session — the user-facing refusals.
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                logs.CreateLogger("DirectUpload").LogError(ex,
                    "Could not start a deck upload for session {Session}.", req.SessionId);
                return Results.BadRequest(new { error = "The upload could not be started. Please try again." });
            }
        }).RequireAuthorization();

        app.MapPost("/speaker/decks/complete", async (
                DeckCompleteRequest req,
                ICurrentParticipantAccessor participant,
                CommunityHub.Core.Integrations.Graphics.SpeakerPresentationService decks,
                Microsoft.Extensions.Options.IOptions<CommunityHub.Core.Integrations.Graphics.GraphicsSharePointOptions> spOpts,
                SharePointUploadClient sp,
                CancellationToken ct) =>
        {
            var me = participant.Current;
            if (me is null) return Results.Unauthorized();
            if (me.Role != ParticipantRole.Speaker) return Results.Forbid();
            if (!TryParseKind(req.Kind, out var kind)) return Results.BadRequest(new { error = "Unknown deck kind." });

            var opts = spOpts.Value;
            var folder = kind == CommunityHub.Core.Integrations.Graphics.PresentationKind.Final
                ? opts.PresentationFinalFolderPath
                : opts.PresentationPreviewFolderPath;

            // Must be in the folder for THIS kind, and must carry this session's stable
            // "{sessionId} - " token — so a speaker cannot complete a deck onto someone else's
            // session by posting a different path.
            var path = req.Path ?? string.Empty;
            if (string.IsNullOrWhiteSpace(folder)
                || !path.StartsWith(folder.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(path).StartsWith($"{req.SessionId} - ", StringComparison.Ordinal))
            {
                return Results.Forbid();
            }

            // Trust storage, not the client.
            var item = await sp.GetItemByPathAsync(opts.SiteUrl, opts.DriveName, path, ct);
            if (item is null) return Results.BadRequest(new { error = "The upload did not complete. Please try again." });

            await decks.CompleteDirectUploadAsync(me.EventId, me.ParticipantId, kind, ct);
            return Results.Ok(new { ok = true, name = item.Name });
        }).RequireAuthorization();
    }

    private static bool TryParseKind(string? s, out CommunityHub.Core.Integrations.Graphics.PresentationKind kind)
    {
        kind = CommunityHub.Core.Integrations.Graphics.PresentationKind.Preview;
        if (string.Equals(s, "final", StringComparison.OrdinalIgnoreCase))
        {
            kind = CommunityHub.Core.Integrations.Graphics.PresentationKind.Final;
            return true;
        }
        return string.Equals(s, "preview", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The public sponsor company name, resolved the same way every other surface does.</summary>
    private static async Task<string> ResolveSponsorNameAsync(
        CommunityHubDbContext db, int eventId, string companyId, CancellationToken ct)
    {
        var local = await db.SponsorUploadLocations
            .Where(l => l.EventId == eventId && l.SponsorCompanyId == companyId)
            .Select(l => l.CompanyName)
            .FirstOrDefaultAsync(ct);
        return CommunityHub.Core.Integrations.SponsorCompanyName.Resolve(
            local, legalName: null, billingName: null, companyId: companyId);
    }

    /// <summary>The next free <c>{prefix}{sponsor}_v{N}{ext}</c> in the folder.</summary>
    private static async Task<string> NextVersionedNameAsync(
        SharePointUploadClient sp, string siteUrl, string driveName, string folder,
        string prefix, string sponsor, string ext, CancellationToken ct)
    {
        var stem = $"{prefix}{sponsor}_v";
        var next = 1;
        try
        {
            var existing = await sp.ListFolderFilesAsync(siteUrl, driveName, folder, ct);
            foreach (var f in existing)
            {
                var name = Path.GetFileNameWithoutExtension(f.Name);
                if (!name.StartsWith(stem, StringComparison.OrdinalIgnoreCase)) continue;
                if (int.TryParse(name[stem.Length..], out var v) && v >= next) next = v + 1;
            }
        }
        catch
        {
            // A listing failure must not block the upload; worst case the version restarts at 1
            // and SharePoint's replace-on-conflict keeps the newest file.
        }
        return $"{stem}{next}{ext}";
    }

    /// <summary>File-name-safe component — letters, digits, dash, underscore. Everything else,
    /// including any path separator or traversal attempt, collapses to '-'.</summary>
    private static string Sanitize(string? s) =>
        string.IsNullOrWhiteSpace(s)
            ? "file"
            : new string(s.Trim().Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-').ToArray());
}
