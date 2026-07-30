using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Uploads;

/// <summary>
/// §598 — confirm that every artefact CEH believes was uploaded is still in SharePoint, and stamp
/// the ones that are genuinely gone so completion reverts.
/// </summary>
/// <remarks>
/// <para><b>The bug.</b> Operator 2026-07-28: *"i have manually deleted these files on sharepoint,
/// but ceh still remember them."* The Get Started "Logos &amp; artwork" step still reported all
/// three logos uploaded and counted itself DONE. CEH treated a stored path as proof of upload
/// forever — nothing ever re-checked.</para>
///
/// <para><b>🔒 THE FAIL-SAFE IS THE HARD PART, AND IT IS THE WHOLE DESIGN.</b> An empty or failed
/// SharePoint read is INDISTINGUISHABLE from "every file was deleted". Acting on one would strip
/// every sponsor's artefacts at once and re-chase every company — far worse than a stale label. So a
/// row is stamped ONLY when the folder listing SUCCEEDED and simply did not contain the file.
/// Any throw, any auth lapse, any renamed folder ⇒ change NOTHING and say so loudly.</para>
///
/// <para>This is the §553/§555 rule, and it earned its keep earlier the same day: a Zoho read that
/// returned "0 sessions" was actually a 401, and pruning on it would have duplicated the live
/// agenda irreversibly.</para>
///
/// <para><b>Self-healing both ways.</b> A row previously stamped missing is UN-stamped when the file
/// reappears, so a re-upload (or a restore from the recycle bin) recovers with no manual repair.</para>
/// </remarks>
public sealed class SponsorArtefactVerifier
{
    private readonly CommunityHubDbContext _db;
    private readonly SharePointUploadClient _sp;
    private readonly EventEditionConfigLoader _cfg;
    private readonly EventConfigOptions _cfgOptions;
    private readonly TimeProvider _clock;
    private readonly ILogger<SponsorArtefactVerifier> _log;

    public SponsorArtefactVerifier(
        CommunityHubDbContext db, SharePointUploadClient sp, EventEditionConfigLoader cfg,
        EventConfigOptions cfgOptions, TimeProvider clock, ILogger<SponsorArtefactVerifier> log)
    {
        _db = db; _sp = sp; _cfg = cfg; _cfgOptions = cfgOptions; _clock = clock; _log = log;
    }

    /// <summary>The outcome of one verification pass, for the job log and tests.</summary>
    public sealed record Result(
        int Checked, int MarkedMissing, int Restored, int FoldersUnreadable, string? Note = null);

    /// <summary>
    /// Verify every recorded artefact for an edition. Never throws: a verification failure must not
    /// take a timer job down, and "I could not tell" is a legitimate outcome that gets reported.
    /// </summary>
    public async Task<Result> VerifyAsync(int eventId, CancellationToken ct = default)
    {
        var sp = _cfg.Load(_cfgOptions.EventConfigPath).SharePoint;
        if (sp is null || string.IsNullOrWhiteSpace(sp.SiteUrl) || !_sp.IsConfigured)
        {
            // Not a failure — SharePoint simply is not wired on this host. Say so once.
            _log.LogInformation(
                "SponsorArtefactVerifier: SharePoint is not configured on this host — nothing verified.");
            return new Result(0, 0, 0, 0, "SharePoint not configured");
        }

        var audits = await _db.SponsorUploadAudits
            .Where(a => a.EventId == eventId && a.FileName != "")
            .ToListAsync(ct);
        if (audits.Count == 0) return new Result(0, 0, 0, 0);

        // Group by KIND: one folder listing per artefact type serves every company, instead of a
        // Graph call per file. With ~20 sponsors that is 6 calls rather than 120.
        var byKind = audits.GroupBy(a => a.Kind, StringComparer.OrdinalIgnoreCase);

        int checkedCount = 0, marked = 0, restored = 0, unreadable = 0;
        var now = _clock.GetUtcNow();

        foreach (var group in byKind)
        {
            var spec = SponsorUploadKinds.Resolve(group.Key, sp);
            if (spec is null)
            {
                // An artefact kind we no longer configure. NOT evidence the file is gone.
                unreadable++;
                _log.LogWarning(
                    "SponsorArtefactVerifier: upload kind '{Kind}' has no configured folder — "
                    + "{Count} record(s) left untouched (cannot verify what we cannot locate).",
                    group.Key, group.Count());
                continue;
            }

            // 🔒 THE CONFIRMING READ. ListFolderFilesAsync THROWS on failure, which is exactly what
            // makes this safe: a throw is an UNKNOWN, never a "gone". Retried, because a single
            // transient blip must not be read as a deletion.
            IReadOnlyList<SharePointFileSnapshot>? files = null;
            string? readFailure = null;
            for (var attempt = 1; attempt <= 3 && files is null; attempt++)
            {
                try
                {
                    files = await _sp.ListFolderFilesAsync(sp.SiteUrl, sp.DriveName, spec.Folder, ct);
                }
                catch (Exception ex)
                {
                    readFailure = $"attempt {attempt}/3: {ex.Message}";
                    if (attempt < 3) await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
                }
            }

            if (files is null)
            {
                unreadable++;
                // LOUD, because this is the state that used to be silent (§570 makes it visible).
                _log.LogError(
                    "SponsorArtefactVerifier: could NOT read '{Folder}' after 3 attempts ({Failure}). "
                    + "NOTHING was marked missing for kind '{Kind}' — an unreadable folder is not "
                    + "evidence of deletion.",
                    spec.Folder, readFailure ?? "unknown", group.Key);
                continue;
            }

            var present = files.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var audit in group)
            {
                checkedCount++;
                var stillThere = present.Contains(audit.FileName);

                if (!stillThere && audit.ArtefactMissingAt is null)
                {
                    audit.ArtefactMissingAt = now;
                    marked++;
                    _log.LogWarning(
                        "SponsorArtefactVerifier: '{File}' ({Kind}) for company {Company} is GONE from "
                        + "'{Folder}' — the upload no longer counts, so its task reverts to not done.",
                        audit.FileName, audit.Kind, audit.SponsorCompanyId, spec.Folder);
                }
                else if (stillThere && audit.ArtefactMissingAt is not null)
                {
                    // Self-heals the other way: a restored or re-uploaded file clears the stamp.
                    audit.ArtefactMissingAt = null;
                    restored++;
                    _log.LogInformation(
                        "SponsorArtefactVerifier: '{File}' ({Kind}) for company {Company} is back — "
                        + "the upload counts again.",
                        audit.FileName, audit.Kind, audit.SponsorCompanyId);
                }
            }
        }

        if (marked > 0 || restored > 0) await _db.SaveChangesAsync(ct);

        _log.LogInformation(
            "SponsorArtefactVerifier: checked {Checked}, marked missing {Marked}, restored {Restored}, "
            + "unreadable folders {Unreadable}.",
            checkedCount, marked, restored, unreadable);

        return new Result(checkedCount, marked, restored, unreadable);
    }
}
