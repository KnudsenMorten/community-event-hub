using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations.Sponsors;

/// <summary>Outcome of one sync run, surfaced on the admin page.</summary>
public sealed record SponsorLeadSyncResult(
    bool Ran, string Message, int Created, int Updated, int Screened);

/// <summary>
/// Pulls sponsor leads from Zoho CRM into DbSet&lt;SponsorLead&gt;.
/// Idempotent: re-pulling the same Zoho record updates the existing row in
/// place (keyed by EventId + ZohoRecordId); hub-local processing state
/// (Status / notes / reply audit) is NEVER overwritten by a re-sync — Zoho
/// owns the content columns, the hub owns the workflow columns.
///
/// Gated by Zoho.Enabled AND Zoho.CrmEnabled (default off): turning it on
/// requires the OAuth refresh token to carry ZohoCRM.modules.READ and the
/// CRM records to be tagged with the sponsor company id.
///
/// New rows run through <see cref="SponsorLeadScreeningService"/> (the
/// heuristic baseline of the AI screen): every lead gets a score + label;
/// only unmistakable test/garbage entries are auto-junked, everything else
/// is left for the operator.
/// </summary>
public sealed class SponsorLeadSyncService
{
    private readonly CommunityHubDbContext _db;
    private readonly ZohoClient _zoho;
    private readonly ZohoOptions _options;
    private readonly SponsorLeadScreeningService _screen;
    private readonly TimeProvider _clock;
    private readonly ILogger<SponsorLeadSyncService> _log;

    public SponsorLeadSyncService(
        CommunityHubDbContext db,
        ZohoClient zoho,
        ZohoOptions options,
        SponsorLeadScreeningService screen,
        TimeProvider clock,
        ILogger<SponsorLeadSyncService> log)
    {
        _db = db;
        _zoho = zoho;
        _options = options;
        _screen = screen;
        _clock = clock;
        _log = log;
    }

    public async Task<SponsorLeadSyncResult> SyncAsync(int eventId, CancellationToken ct)
    {
        if (!_options.Enabled || !_options.CrmEnabled)
        {
            return new SponsorLeadSyncResult(false,
                "Zoho CRM lead pull is disabled (Zoho__CrmEnabled=false). "
                + "Enable it once the refresh token has the ZohoCRM.modules.READ "
                + "scope and CRM records carry the sponsor company id field.",
                0, 0, 0);
        }

        var token = await _zoho.GetAccessTokenAsync(ct);
        if (token is null)
        {
            return new SponsorLeadSyncResult(false,
                "Could not obtain a Zoho access token - check client id/secret/refresh token.",
                0, 0, 0);
        }

        var now = _clock.GetUtcNow();
        int created = 0, updated = 0, screened = 0;

        var modules = _options.CrmModules
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var module in modules)
        {
            var pulled = await _zoho.GetCrmLeadsAsync(token, module, ct);
            _log.LogInformation("SponsorLeadSync: {Count} records from CRM module {Module}.",
                pulled.Count, module);

            foreach (var p in pulled)
            {
                var row = await _db.SponsorLeads.FirstOrDefaultAsync(
                    l => l.EventId == eventId && l.ZohoRecordId == p.ZohoRecordId, ct);

                if (row is null)
                {
                    row = new SponsorLead
                    {
                        EventId = eventId,
                        ZohoRecordId = p.ZohoRecordId,
                        CapturedAt = p.CreatedTime,
                    };
                    _db.SponsorLeads.Add(row);
                    created++;
                }
                else
                {
                    updated++;
                }

                // Content columns: Zoho is the source of truth on every sync.
                row.SponsorCompanyId = p.SponsorCompanyId;
                row.LeadKind  = MapKind(p.Module);
                row.FirstName = p.FirstName;
                row.LastName  = p.LastName;
                row.FullName  = p.FullName;
                row.Email     = p.Email;
                row.Phone     = p.Phone;
                row.Company   = p.Company;
                row.JobTitle  = p.JobTitle;
                row.City      = p.City;
                row.Country   = p.Country;
                row.Source    = p.Source;
                row.Notes     = p.Notes;
                row.LastSyncedAt = now;

                // Workflow columns (Status / reply audit) deliberately untouched.

                // Screen anything not screened yet (new rows + pre-screen backlog).
                if (row.AiScreenedAt is null)
                {
                    _screen.Screen(row, now);
                    screened++;
                }
            }
        }

        await _db.SaveChangesAsync(ct);
        var msg = $"Sync complete: {created} new, {updated} updated, {screened} screened.";
        _log.LogInformation("SponsorLeadSync: {Message}", msg);
        return new SponsorLeadSyncResult(true, msg, created, updated, screened);
    }

    private static SponsorLeadKind MapKind(string module) => module.ToLowerInvariant() switch
    {
        "leads"    => SponsorLeadKind.Lead,
        "contacts" => SponsorLeadKind.Inquiry,
        "events"   => SponsorLeadKind.Meeting,
        _          => SponsorLeadKind.Lead,
    };
}

/// <summary>
/// Heuristic baseline of the "AI screen": deterministic, explainable
/// scoring 0-100 with a short label. High = looks like a real prospect.
/// Conservative on purpose — only unmistakable test entries are auto-junked
/// (status change), every other verdict is advisory (badge on the grid).
/// Operator overrides stay in the row (Status + StatusNote), which is the
/// training data a future model-based screen learns from.
///
/// The scoring math itself lives in <see cref="SponsorLeadScoreExplainer"/> —
/// the single source of truth — so the score persisted here and the
/// "why this score" breakdown the organizer sees can never drift apart.
/// </summary>
public sealed class SponsorLeadScreeningService
{
    public void Screen(SponsorLead lead, DateTimeOffset now)
    {
        var breakdown = SponsorLeadScoreExplainer.Compute(lead);

        lead.AiScreenScore = breakdown.FinalScore;
        lead.AiScreenLabel = breakdown.Label;
        lead.AiScreenedAt  = now;

        // Auto-junk ONLY the unmistakable case, and only if the operator
        // hasn't already touched the row.
        if (breakdown.LooksTest && lead.Status == SponsorLeadStatus.Open && lead.StatusChangedAt is null)
        {
            lead.Status = SponsorLeadStatus.Junk;
            lead.StatusNote = "Auto-junked by heuristic screen (test-entry pattern).";
            lead.StatusChangedAt = now;
            lead.StatusChangedByEmail = "ai-screen";
        }
    }
}
