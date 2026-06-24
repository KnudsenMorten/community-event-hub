using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Organizer GUI to manage a sponsor's Company Manager (webshop) record — edit any
/// of the key fields, especially the DEFAULT SIGNER, DEFAULT EVENT COORDINATOR and
/// internal notes / special agreements. On save it PUTs to Company Manager and
/// CASCADES a default-event-coordinator change into CEH (SponsorInfo) and Zoho
/// Backstage (the sponsor/exhibitor contact), so the default person changes
/// everywhere automatically.
/// </summary>
[Authorize]
public class SponsorWebshopCompanyModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly CommunityHubDbContext _db;
    private readonly CompanyManagerClient _cm;
    private readonly CompanyManagerOptions _cmOptions;
    private readonly TimeProvider _clock;
    private readonly CommunityHub.Core.Integrations.SponsorZohoSyncService _zohoSync;
    private readonly ILogger<SponsorWebshopCompanyModel> _log;

    public SponsorWebshopCompanyModel(
        ICurrentParticipantAccessor participant,
        CommunityHubDbContext db,
        CompanyManagerClient cm,
        CompanyManagerOptions cmOptions,
        TimeProvider clock,
        CommunityHub.Core.Integrations.SponsorZohoSyncService zohoSync,
        ILogger<SponsorWebshopCompanyModel> log)
    {
        _participant = participant;
        _db = db;
        _cm = cm;
        _cmOptions = cmOptions;
        _clock = clock;
        _zohoSync = zohoSync;
        _log = log;
    }

    public bool AccessDenied { get; private set; }
    public bool NotConfigured { get; private set; }
    [TempData] public string? ActionMessage { get; set; }
    public string? Error { get; private set; }

    public int? SelectedCompanyId { get; private set; }
    public record CompanyPick(string CompanyId, string Name);
    public List<CompanyPick> Companies { get; private set; } = new();

    // Editable company fields.
    public string CompanyName { get; private set; } = string.Empty;
    public string? PublicName { get; private set; }
    public string? WebAddress { get; private set; }
    public string? LinkedIn { get; private set; }
    public string? Twitter { get; private set; }
    public string? Notes { get; private set; }
    public int DefaultSignerUserId { get; private set; }
    public int DefaultCoordinatorUserId { get; private set; }
    public IReadOnlyList<CompanyManagerUser> Users { get; private set; } = Array.Empty<CompanyManagerUser>();

    private CurrentParticipant? Guard()
    {
        var me = _participant.Current;
        if (me is null || me.Role != ParticipantRole.Organizer) { AccessDenied = me is not null; return null; }
        return me;
    }

    public async Task<IActionResult> OnGetAsync(int? companyId, CancellationToken ct)
    {
        var me = Guard();
        if (_participant.Current is null) return RedirectToPage("/Login");
        if (me is null) return Page();
        if (!_cmOptions.Enabled) { NotConfigured = true; return Page(); }

        if (companyId is int cid)
        {
            SelectedCompanyId = cid;
            await LoadCompanyAsync(cid, ct);
        }
        else
        {
            await LoadCompanyListAsync(me.EventId, ct);
        }
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(
        int companyId, string? publicName, string? webAddress, string? linkedIn, string? twitter,
        string? notes, int signerUserId, int coordinatorUserId, CancellationToken ct)
    {
        var me = Guard();
        if (_participant.Current is null) return RedirectToPage("/Login");
        if (me is null) return Page();
        if (!_cmOptions.Enabled) { NotConfigured = true; return Page(); }

        // What's the coordinator BEFORE the save? (to detect a change for the cascade)
        var before = await _cm.GetCompanyAsync(companyId, ct);
        var prevCoordinator = before?.EventCoordinationDefaultContactUserId ?? 0;

        // INVARIANT: a sponsor company must always have a default signer AND a
        // default event coordinator. Don't allow clearing them.
        if (signerUserId <= 0 || coordinatorUserId <= 0)
        {
            Error = "A sponsor must always have a default signer AND a default event coordinator.";
            SelectedCompanyId = companyId;
            await LoadCompanyAsync(companyId, ct);
            return Page();
        }

        var fields = new Dictionary<string, object?>
        {
            ["company_name_public"] = publicName ?? string.Empty,
            ["web_address"] = webAddress ?? string.Empty,
            ["linkedin_url"] = linkedIn ?? string.Empty,
            ["twitter_url"] = twitter ?? string.Empty,
            ["notes"] = notes ?? string.Empty,
            ["default_signer_id"] = signerUserId,
            ["event_coordination_default_contact_id"] = coordinatorUserId,
        };

        var ok = await _cm.UpdateCompanyAsync(companyId, fields, ct);
        if (!ok)
        {
            Error = "Could not save to Company Manager. Please try again.";
            SelectedCompanyId = companyId;
            await LoadCompanyAsync(companyId, ct);
            return Page();
        }

        // CASCADE: a default-event-coordinator change flows to CEH + Zoho.
        var cascade = "";
        if (coordinatorUserId != prevCoordinator)
            cascade = await CascadeCoordinatorAsync(me.EventId, companyId, coordinatorUserId, ct);

        ActionMessage = $"Saved to Company Manager.{cascade}";
        return RedirectToPage(new { companyId });
    }

    /// <summary>
    /// Push the new default event coordinator into CEH (SponsorInfo) + Zoho. Resolves
    /// the CM user (first/last/email/phone) and updates the sponsor's coordinator
    /// fields, then triggers the Zoho field+contact sync.
    /// </summary>
    private async Task<string> CascadeCoordinatorAsync(int eventId, int companyId, int coordinatorUserId, CancellationToken ct)
    {
        try
        {
            var user = await _cm.GetUserAsync(coordinatorUserId, ct);
            if (user is null) return " (coordinator changed; CEH/Zoho update skipped — user not found.)";

            var companyKey = companyId.ToString();
            var info = await _db.SponsorInfos.FirstOrDefaultAsync(
                s => s.EventId == eventId && s.SponsorCompanyId == companyKey, ct);
            if (info is null)
            {
                info = new SponsorInfo { EventId = eventId, SponsorCompanyId = companyKey, CreatedAt = _clock.GetUtcNow() };
                _db.SponsorInfos.Add(info);
            }
            else info.UpdatedAt = _clock.GetUtcNow();

            info.EventCoordinatorFirstName = NullIf(user.FirstName);
            info.EventCoordinatorLastName = NullIf(user.LastName);
            info.EventCoordinatorEmail = NullIf(user.Email);
            info.EventCoordinatorPhone = NullIf(user.Phone);
            await _db.SaveChangesAsync(ct);

            var company = await _cm.GetCompanyAsync(companyId, ct);
            var publicName = company is null ? companyKey
                : (!string.IsNullOrWhiteSpace(company.PublicName) ? company.PublicName : company.Name);
            var sync = await _zohoSync.SyncAsync(eventId, companyKey, publicName, ct);
            return sync.Enabled && sync.Error is null
                ? " Default event coordinator updated in CEH + Zoho."
                : " Default event coordinator updated in CEH (Zoho sync pending).";
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Coordinator cascade failed for company {Co}.", companyId);
            return " (coordinator changed; CEH/Zoho cascade hit an error.)";
        }
    }

    private async Task LoadCompanyListAsync(int eventId, CancellationToken ct)
    {
        var ids = await _db.SponsorInfos
            .Where(s => s.EventId == eventId)
            .Select(s => s.SponsorCompanyId)
            .Distinct()
            .ToListAsync(ct);

        foreach (var id in ids.OrderBy(x => x))
        {
            var name = id;
            if (int.TryParse(id, out var cid))
            {
                try
                {
                    var c = await _cm.GetCompanyAsync(cid, ct);
                    if (c is not null) name = !string.IsNullOrWhiteSpace(c.PublicName) ? c.PublicName : c.Name;
                }
                catch (Exception ex) { _log.LogWarning(ex, "Webshop mgmt: name lookup failed for {Co}.", id); }
            }
            Companies.Add(new CompanyPick(id, name));
        }
        Companies = Companies.OrderBy(c => c.Name).ToList();
    }

    private async Task LoadCompanyAsync(int companyId, CancellationToken ct)
    {
        var c = await _cm.GetCompanyAsync(companyId, ct);
        if (c is null) { Error = "Company not found in Company Manager."; return; }
        CompanyName = c.Name;
        PublicName = c.PublicName;
        WebAddress = c.WebsiteUrl;
        LinkedIn = c.LinkedInUrl;
        Twitter = c.TwitterUrl;
        Notes = c.Notes;
        DefaultSignerUserId = c.DefaultSignerUserId;
        DefaultCoordinatorUserId = c.EventCoordinationDefaultContactUserId;
        Users = await _cm.GetCompanyUsersAsync(companyId, ct);
    }

    private static string? NullIf(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
