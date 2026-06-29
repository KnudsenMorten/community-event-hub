using System.Text;
using CommunityHub.Core.Audit;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Sponsors;
using CommunityHub.Core.Settings;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// Sponsor leads pipeline driver. Runs hourly at :15 and does two things:
///
///   1. SYNC (05:15 UTC only, before the digest window): pull leads from
///      Zoho CRM into DbSet&lt;SponsorLead&gt;. Gated by Zoho.CrmEnabled —
///      disabled installs skip with one log line.
///   2. DIGEST (every run): for each sponsor company with notifications
///      enabled, send the DELTA of leads captured since LastDeltaSentAt.
///      Daily-cadence prefs only fire on the 06:15 UTC run; RealTime prefs
///      fire on every hourly run that has new leads. The cursor update IS
///      the dedup — a lead is never digested twice (CONTEXT.md 11e spirit).
///
/// Recipients: the pref's comma/semicolon-separated list; when empty, all
/// sponsor-contact participant emails for the company.
/// </summary>
public sealed class SponsorLeadsJob
{
    private readonly CommunityHubDbContext _db;
    private readonly SponsorLeadSyncService _sync;
    private readonly ZohoOptions _zohoOptions;
    private readonly EmailTemplateProvider _templates;
    private readonly IEmailSender _emailSender;
    private readonly TimeProvider _clock;
    private readonly FeatureGateService _gate;
    private readonly IAuditTrail _audit;
    private readonly ILogger<SponsorLeadsJob> _log;

    public SponsorLeadsJob(
        CommunityHubDbContext db,
        SponsorLeadSyncService sync,
        ZohoOptions zohoOptions,
        EmailTemplateProvider templates,
        IEmailSender emailSender,
        TimeProvider clock,
        FeatureGateService gate,
        IAuditTrail audit,
        ILogger<SponsorLeadsJob> log)
    {
        _db = db;
        _sync = sync;
        _zohoOptions = zohoOptions;
        _templates = templates;
        _emailSender = emailSender;
        _clock = clock;
        _gate = gate;
        _audit = audit;
        _log = log;
    }

    [Function("SponsorLeadsJob")]
    public async Task Run([TimerTrigger("0 15 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        var activeEvent = await _db.Events
            .Where(e => e.IsActive)
            .Select(e => new { e.Id, e.DisplayName })
            .FirstOrDefaultAsync(ct);
        if (activeEvent is null)
        {
            _log.LogWarning("SponsorLeadsJob: no active event.");
            return;
        }

        // GATE (REQUIREMENTS §23): the sponsor-leads pipeline (CRM pull + delta
        // digests) is an advanced feature, off by default. When disabled for this
        // edition the job no-ops — no CRM sync, no digests sent.
        if (!await _gate.IsFeatureEnabledAsync("sponsor-leads", activeEvent.Id, ct))
        {
            _log.LogInformation(
                "SponsorLeadsJob: event {EventId} — feature 'sponsor-leads' disabled, skipped.",
                activeEvent.Id);
            return;
        }

        var now = _clock.GetUtcNow();

        // 1. Nightly CRM pull (05:15 UTC). The service self-gates on config.
        if (now.Hour == 5)
        {
            var result = await _sync.SyncAsync(activeEvent.Id, ct);
            _log.LogInformation("SponsorLeadsJob sync: {Message}", result.Message);
            // Named Engine event (REQUIREMENTS §24): the CRM pull. (The delta digests
            // below are user-impact emails, already captured as Email events.) Only
            // audit a pull that ran + changed something.
            if (result.Ran && (result.Created + result.Updated > 0))
                await _audit.RecordAsync(new AuditEntry
                {
                    EventId = activeEvent.Id,
                    Category = AuditCategory.Engine,
                    Action = "sponsor-leads",
                    ActorEmail = "system",
                    Source = AuditSource.Job,
                    Outcome = AuditOutcome.Success,
                    Summary = $"Sponsor leads CRM sync: {result.Created} created, {result.Updated} updated, "
                        + $"{result.Screened} screened",
                }, ct);
        }
        else if (!_zohoOptions.CrmEnabled)
        {
            _log.LogDebug("SponsorLeadsJob: CRM pull disabled; digest pass only.");
        }

        // 2. Delta digests.
        var prefs = await _db.SponsorLeadNotificationPrefs
            .Where(p => p.EventId == activeEvent.Id && p.Enabled)
            .ToListAsync(ct);
        if (prefs.Count == 0)
        {
            _log.LogInformation("SponsorLeadsJob: no enabled notification prefs.");
            return;
        }

        var digestsSent = 0;
        foreach (var pref in prefs)
        {
            // Daily cadence fires only on the 06:15 UTC run.
            if (pref.Cadence == SponsorLeadNotifyCadence.Daily && now.Hour != 6) continue;

            var since = pref.LastDeltaSentAt ?? DateTimeOffset.MinValue;
            var q = _db.SponsorLeads.Where(l =>
                l.EventId == activeEvent.Id
                && l.SponsorCompanyId == pref.SponsorCompanyId
                && l.CapturedAt > since
                && l.Status != SponsorLeadStatus.Ignore);
            if (pref.SkipJunk)
            {
                q = q.Where(l => l.Status != SponsorLeadStatus.Junk);
            }
            var fresh = await q.OrderBy(l => l.CapturedAt).ToListAsync(ct);
            if (fresh.Count == 0) continue;

            var recipients = await ResolveRecipientsAsync(activeEvent.Id, pref, ct);
            if (recipients.Count == 0)
            {
                _log.LogWarning(
                    "SponsorLeadsJob: digest for {Company} has {Count} leads but no recipients.",
                    pref.SponsorCompanyId, fresh.Count);
                continue;
            }

            // §169: render the digest PER recipient so each coordinator's {{hubUrl}} CTA is
            // their OWN /go/{token} auto-login magic-link (a recipient must never sign in AS
            // another). Resolve each recipient address to a Participant in this edition; an
            // external/free-text recipient with no Participant keeps the plain hub URL
            // (fail-safe — NewTokenSet swallows a null id and never throws). The lead list
            // body is identical for everyone, so the per-recipient cost is just the token map.
            var pidByEmail = (await _db.Participants
                    .Where(p => p.EventId == activeEvent.Id)
                    .Select(p => new { p.Id, p.Email })
                    .ToListAsync(ct))
                .GroupBy(p => p.Email.Trim().ToLowerInvariant())
                .ToDictionary(g => g.Key, g => g.First().Id);
            var leadListHtml = BuildLeadListHtml(fresh);

            var ok = false;
            foreach (var to in recipients)
            {
                int? pid = pidByEmail.TryGetValue(to.Trim().ToLowerInvariant(), out var found)
                    ? found : null;

                // Plain-text tokens are HTML-encoded by the renderer at the seam
                // (EmailTemplateRenderer, REQUIREMENTS §10c-4); leadListHtml is a
                // sender-built HTML fragment (raw-HTML token) so it stays verbatim.
                var tokens = _templates.NewTokenSet(pid);
                tokens["eventDisplayName"] = activeEvent.DisplayName;
                tokens["sponsorCompany"] = pref.SponsorCompanyId;
                tokens["leadCount"] = fresh.Count.ToString();
                tokens["leadListHtml"] = leadListHtml;
                var rendered = _templates.Render("sponsor-leads-digest", tokens);

                try
                {
                    await _emailSender.SendAsync(to, rendered.Subject, rendered.HtmlBody, ct);
                    ok = true;
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex,
                        "SponsorLeadsJob: digest to {To} for {Company} failed.",
                        to, pref.SponsorCompanyId);
                }
            }

            // Advance the cursor only when at least one recipient got the
            // delta — a fully-failed send retries the same window next run.
            if (ok)
            {
                pref.LastDeltaSentAt = fresh[^1].CapturedAt;
                await _db.SaveChangesAsync(ct);
                digestsSent++;
            }
        }

        _log.LogInformation("SponsorLeadsJob: {Count} digest(s) sent.", digestsSent);
    }

    private async Task<List<string>> ResolveRecipientsAsync(
        int eventId, SponsorLeadNotificationPref pref, CancellationToken ct)
    {
        var explicitList = (pref.Recipients ?? string.Empty)
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(r => r.Contains('@'))
            .ToList();
        if (explicitList.Count > 0) return explicitList;

        // Fallback: every active sponsor contact of the company.
        return await _db.Participants
            .Where(p => p.EventId == eventId
                        && p.IsActive
                        && p.Role == ParticipantRole.Sponsor
                        && p.SponsorCompanyId == pref.SponsorCompanyId)
            .Select(p => p.Email)
            .Distinct()
            .ToListAsync(ct);
    }

    private static string BuildLeadListHtml(IReadOnlyList<SponsorLead> leads)
    {
        var sb = new StringBuilder();
        foreach (var l in leads)
        {
            sb.Append("<tr>")
              .Append("<td style=\"padding:8px 10px;border-bottom:1px solid #e5e7eb;\">")
              .Append(Enc(string.IsNullOrWhiteSpace(l.FullName) ? "(no name)" : l.FullName));
            if (!string.IsNullOrWhiteSpace(l.JobTitle) || !string.IsNullOrWhiteSpace(l.Company))
            {
                sb.Append("<br><span style=\"color:#6b7280;font-size:13px;\">")
                  .Append(Enc(string.Join(" — ", new[] { l.JobTitle, l.Company }
                      .Where(s => !string.IsNullOrWhiteSpace(s)))))
                  .Append("</span>");
            }
            sb.Append("</td>")
              .Append("<td style=\"padding:8px 10px;border-bottom:1px solid #e5e7eb;word-break:break-all;\">")
              .Append(Enc(l.Email))
              .Append(string.IsNullOrWhiteSpace(l.Phone) ? "" : "<br>" + Enc(l.Phone))
              .Append("</td>")
              .Append("<td style=\"padding:8px 10px;border-bottom:1px solid #e5e7eb;white-space:nowrap;\">")
              .Append(l.CapturedAt.UtcDateTime.ToString("dd MMM HH:mm"))
              .Append("</td>")
              .Append("</tr>");
        }
        return sb.ToString();
    }

    private static string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);
}
