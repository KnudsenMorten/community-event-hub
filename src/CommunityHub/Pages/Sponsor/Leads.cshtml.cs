using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Sponsors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Sponsor;

/// <summary>
/// Sponsor-facing view of their leads-API setup. Shows:
///   - their company id
///   - the metadata of their current API key (prefix + issue date),
///     or a friendly note + "ask the organizer" CTA when no key has
///     been issued yet
///   - download URLs (CSV + JSON) with their company id baked in
///   - 3 PowerShell samples + a browser-direct URL, also pre-filled
///     with their company id
///
/// The RAW key itself is NEVER shown here -- only the SHA256 hash is
/// stored, so even an organizer cannot retrieve it after issue time.
/// The "I lost my key" path is to ask the organizer to regenerate
/// (which revokes the old one).
///
/// Auth: Sponsor role (their own data) or Organizer (read-only view
/// when assisting a sponsor). Sponsors without a linked SponsorCompanyId
/// see a friendly "your account is not yet linked" message.
/// </summary>
[Authorize]
public class LeadsModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly ISponsorApiKeyService _keys;

    public LeadsModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        ISponsorApiKeyService keys)
    {
        _db = db;
        _participant = participant;
        _keys = keys;
    }

    public bool AccessDenied { get; private set; }
    public bool NoCompanyLink { get; private set; }

    public string? SponsorCompanyId  { get; private set; }
    public string? KeyPrefix         { get; private set; }
    public DateTimeOffset? IssuedAt  { get; private set; }
    public string? IssuedByEmail     { get; private set; }
    public string  BaseUrl           { get; private set; } = "";

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        // Sponsors see their own; Organizers can view too (read-only).
        if (me.Role != ParticipantRole.Sponsor && me.Role != ParticipantRole.Organizer)
        {
            AccessDenied = true;
            return Page();
        }

        // CurrentParticipant doesn't carry SponsorCompanyId; resolve it
        // from the Participants table using the same pattern as
        // /Sponsor/Index.cshtml.cs.
        SponsorCompanyId = await _db.Participants
            .Where(p => p.Id == me.ParticipantId)
            .Select(p => p.SponsorCompanyId)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(SponsorCompanyId))
        {
            NoCompanyLink = true;
            return Page();
        }

        BaseUrl = $"{Request.Scheme}://{Request.Host.Value}";
        var key = await _keys.GetCurrentAsync(me.EventId, SponsorCompanyId!, ct);
        if (key is not null)
        {
            KeyPrefix     = key.KeyPrefix;
            IssuedAt      = key.IssuedAt;
            IssuedByEmail = key.IssuedByEmail;
        }
        return Page();
    }
}
