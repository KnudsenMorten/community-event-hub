using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations;

/// <summary>Outcome of one upload-watcher run.</summary>
public sealed record SponsorUploadWatchResult(
    int LocationsChecked,
    int FilesObserved,
    int FilesNew,
    int FilesChanged,
    int NotificationsSent,
    int Errors);

/// <summary>
/// Polls every provisioned <see cref="SponsorUploadLocation"/> on the active
/// edition, diffs the current SharePoint folder contents against the
/// last-known state, and emails the configured recipients when a sponsor
/// adds or replaces a file. New file =&gt; "uploaded" notification; same
/// driveItem id but different ETag =&gt; "updated" notification.
///
/// Tolerant by design: a missing folder, a single failing Graph call, or a
/// transient SMTP error never aborts the whole run -- the next poll
/// reconverges. Notifications are per-recipient so the BrevoEmailSender
/// test-mode redirect annotates each one correctly.
/// </summary>
public sealed class SponsorUploadWatchService
{
    private readonly CommunityHubDbContext _db;
    private readonly SharePointUploadClient _sp;
    private readonly EventEditionConfigLoader _eventConfigLoader;
    private readonly EventConfigOptions _eventConfigOptions;
    private readonly IEmailSender _email;
    private readonly ILogger<SponsorUploadWatchService> _log;

    public SponsorUploadWatchService(
        CommunityHubDbContext db,
        SharePointUploadClient sp,
        EventEditionConfigLoader eventConfigLoader,
        EventConfigOptions eventConfigOptions,
        IEmailSender email,
        ILogger<SponsorUploadWatchService> log)
    {
        _db = db;
        _sp = sp;
        _eventConfigLoader = eventConfigLoader;
        _eventConfigOptions = eventConfigOptions;
        _email = email;
        _log = log;
    }

    public async Task<SponsorUploadWatchResult> RunAsync(CancellationToken ct = default)
    {
        if (!_sp.IsConfigured)
        {
            _log.LogInformation("SponsorUploadWatchService: SharePoint integration not configured, skipping.");
            return new SponsorUploadWatchResult(0, 0, 0, 0, 0, 0);
        }

        var editionFacts = _eventConfigLoader.Load(_eventConfigOptions.EventConfigPath);
        var sp = editionFacts.SharePoint;
        if (sp is null || string.IsNullOrWhiteSpace(sp.SiteUrl))
        {
            _log.LogInformation("SponsorUploadWatchService: no sharepoint section in event config, skipping.");
            return new SponsorUploadWatchResult(0, 0, 0, 0, 0, 0);
        }

        var activeEventIds = await _db.Events
            .Where(e => e.IsActive)
            .Select(e => e.Id)
            .ToListAsync(ct);

        var locations = await _db.SponsorUploadLocations
            .Where(l => activeEventIds.Contains(l.EventId)
                        && !string.IsNullOrEmpty(l.NotifyEmailsCsv))
            .Include(l => l.Files)
            .ToListAsync(ct);

        var observed = 0;
        var newCount = 0;
        var changedCount = 0;
        var sent = 0;
        var errors = 0;

        foreach (var loc in locations)
        {
            IReadOnlyList<SharePointFileSnapshot> files;
            try
            {
                files = await _sp.ListFolderFilesAsync(sp.SiteUrl, sp.DriveName, loc.FolderPath, ct);
            }
            catch (SharePointUploadException ex)
            {
                _log.LogWarning(ex,
                    "SponsorUploadWatchService: list failed for {Folder} (sponsor {Co}), continuing.",
                    loc.FolderPath, loc.SponsorCompanyId);
                errors++;
                continue;
            }

            observed += files.Count;

            var byItemId = loc.Files.ToDictionary(f => f.GraphItemId, StringComparer.Ordinal);
            var recipients = SplitEmails(loc.NotifyEmailsCsv);

            foreach (var file in files)
            {
                var isNew = !byItemId.TryGetValue(file.ItemId, out var known);
                var isChanged = !isNew
                    && !string.Equals(known!.ETag ?? string.Empty, file.ETag ?? string.Empty, StringComparison.Ordinal);

                if (!isNew && !isChanged) continue;

                if (isNew)
                {
                    newCount++;
                    var row = new SponsorUploadFile
                    {
                        SponsorUploadLocationId = loc.Id,
                        FileName = Truncate(file.Name, 400) ?? string.Empty,
                        GraphItemId = file.ItemId,
                        ETag = Truncate(file.ETag, 128),
                        LastModifiedUtc = file.LastModifiedUtc,
                        FirstSeenAt = DateTimeOffset.UtcNow,
                    };
                    _db.SponsorUploadFiles.Add(row);
                    known = row;
                }
                else
                {
                    changedCount++;
                    known!.FileName = Truncate(file.Name, 400) ?? string.Empty;
                    known.ETag = Truncate(file.ETag, 128);
                    known.LastModifiedUtc = file.LastModifiedUtc;
                }

                var verb = isNew ? "uploaded" : "updated";
                var subject = RenderSubject(loc.NotifySubject, loc.CompanyName)
                    ?? $"[Sponsor upload] {loc.CompanyName} {verb} {file.Name}";
                var body = BuildEmailBody(loc, file, verb);

                foreach (var to in recipients)
                {
                    try
                    {
                        await _email.SendAsync(to, subject, body, ct);
                        sent++;
                    }
                    catch (Exception ex)
                    {
                        errors++;
                        _log.LogError(ex,
                            "SponsorUploadWatchService: failed to email {To} about {File} in {Folder}; will retry on next run.",
                            to, file.Name, loc.FolderPath);
                    }
                }

                known.LastNotifiedAt = DateTimeOffset.UtcNow;

                // Also COPY a new/changed LOGO into the central logo-collection
                // folder so organizers have every sponsor logo in one place
                // (operator 2026-06-23). Same site + drive; named
                // "{Company} - {file}" so logos never collide and a re-upload
                // overwrites cleanly. Tolerant: a copy failure is logged and never
                // aborts the run (re-run the watcher, or the sponsor re-uploads, to
                // re-attempt).
                if (!string.IsNullOrWhiteSpace(sp.LogoCollectionFolderPath)
                    && string.Equals(loc.Subfolder, "LOGO", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var bytes = await _sp.DownloadItemContentAsync(sp.SiteUrl, sp.DriveName, file.ItemId, ct);
                        if (bytes is not null)
                        {
                            var destName = $"{SanitizeNameComponent(loc.CompanyName)} - {file.Name}";
                            await _sp.UploadFileAsync(
                                sp.SiteUrl, sp.DriveName, sp.LogoCollectionFolderPath, destName,
                                bytes, GuessContentType(file.Name), ct);
                            _log.LogInformation(
                                "SponsorUploadWatchService: copied logo '{File}' for {Co} into the collection folder as '{Dest}'.",
                                file.Name, loc.CompanyName, destName);
                        }
                    }
                    catch (Exception ex)
                    {
                        errors++;
                        _log.LogError(ex,
                            "SponsorUploadWatchService: failed to copy logo '{File}' for {Co} into the collection folder; "
                            + "notification still sent, will re-attempt on the next change.",
                            file.Name, loc.CompanyName);
                    }
                }
            }
        }

        if (newCount > 0 || changedCount > 0)
        {
            await _db.SaveChangesAsync(ct);
        }

        _log.LogInformation(
            "SponsorUploadWatchService: {Loc} folders checked, {Obs} files observed, "
            + "{New} new + {Chg} changed, {Sent} notifications, {Err} errors.",
            locations.Count, observed, newCount, changedCount, sent, errors);

        return new SponsorUploadWatchResult(
            LocationsChecked:  locations.Count,
            FilesObserved:     observed,
            FilesNew:          newCount,
            FilesChanged:      changedCount,
            NotificationsSent: sent,
            Errors:            errors);
    }

    private static List<string> SplitEmails(string csv) =>
        (csv ?? string.Empty)
        .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(s => s.Contains('@'))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static string? RenderSubject(string template, string companyName)
    {
        if (string.IsNullOrWhiteSpace(template)) return null;
        return template
            .Replace("{{companyName}}", companyName ?? string.Empty);
    }

    private static string BuildEmailBody(SponsorUploadLocation loc, SharePointFileSnapshot file, string verb)
    {
        var when = (file.LastModifiedUtc ?? DateTimeOffset.UtcNow).ToString("yyyy-MM-dd HH:mm 'UTC'");
        var folderLink = string.IsNullOrEmpty(loc.EditLinkUrl)
            ? loc.FolderPath
            : $"<a href=\"{System.Net.WebUtility.HtmlEncode(loc.EditLinkUrl)}\">Open folder</a>";

        return $@"<p>Sponsor <b>{System.Net.WebUtility.HtmlEncode(loc.CompanyName)}</b> {verb} a file in their <code>{System.Net.WebUtility.HtmlEncode(loc.Subfolder)}</code> folder.</p>
<ul>
  <li><b>File:</b> {System.Net.WebUtility.HtmlEncode(file.Name)}</li>
  <li><b>When:</b> {when}</li>
  <li><b>Folder:</b> {System.Net.WebUtility.HtmlEncode(loc.FolderPath)}</li>
  <li><b>Link:</b> {folderLink}</li>
</ul>
<p>Please review the file for quality.</p>";
    }

    private static string? Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return s.Length <= max ? s : s.Substring(0, max);
    }

    /// <summary>
    /// Make a company name safe to use as part of a SharePoint file name: drop the
    /// characters SharePoint rejects (<c>" * : &lt; &gt; ? / \ |</c>) and collapse
    /// whitespace, so the collected logo lands as a clean "{Company} - {file}".
    /// </summary>
    private static string SanitizeNameComponent(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Sponsor";
        var cleaned = new string(name
            .Where(c => "\"*:<>?/\\|".IndexOf(c) < 0)
            .ToArray());
        cleaned = string.Join(" ", cleaned.Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(cleaned) ? "Sponsor" : cleaned;
    }

    /// <summary>Best-effort content type from a logo file's extension (PUT infers from the name anyway).</summary>
    private static string GuessContentType(string fileName)
    {
        var ext = System.IO.Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".png"  => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif"  => "image/gif",
            ".svg"  => "image/svg+xml",
            ".webp" => "image/webp",
            ".pdf"  => "application/pdf",
            ".eps"  => "application/postscript",
            ".ai"   => "application/postscript",
            _        => "application/octet-stream",
        };
    }
}
