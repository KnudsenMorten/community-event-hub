using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Integrations.Sponsors;

/// <summary>
/// The booth-staff input a sponsor contact fills in when they capture a
/// lead at their stand. All fields except a contactable channel are
/// optional; the service requires <b>either</b> an email <b>or</b> a phone
/// so the lead is actually reachable.
/// </summary>
public sealed record SponsorLeadCaptureInput(
    string? FirstName,
    string? LastName,
    string? Email,
    string? Phone,
    string? Company,
    string? JobTitle,
    string? Notes);

/// <summary>Outcome of one booth-capture attempt.</summary>
public sealed record SponsorLeadCaptureResult(bool Ok, string Message, int? LeadId);

/// <summary>
/// Captures a lead entered by a sponsor's booth staff directly in the hub.
///
/// This is the IN-HUB complement to the Zoho-pull (<see cref="SponsorLeadSyncService"/>):
/// the same <see cref="SponsorLead"/> store, but the row originates from a
/// person typing it on the booth rather than from a CRM sync. Captured rows
/// carry no <see cref="SponsorLead.ZohoRecordId"/> (so the filtered-unique
/// Zoho index does not collide) and are tagged
/// <see cref="SponsorLeadCaptureMethod.ManualBooth"/> with the capturing
/// contact's email for provenance.
///
/// New rows run through the shared <see cref="SponsorLeadScreeningService"/>
/// (the same heuristic the Zoho pull uses) so a junk/test entry is screened
/// the moment it is captured. Captured leads then flow out the existing
/// per-sponsor CSV / JSON export API for free — no API change needed.
///
/// Nothing is ever hard-deleted: an operator (or the screen) can mark a
/// captured lead Junk/Ignore, but the row stays for audit + screen training.
/// </summary>
public sealed class SponsorLeadCaptureService
{
    private readonly CommunityHubDbContext _db;
    private readonly SponsorLeadScreeningService _screen;
    private readonly TimeProvider _clock;

    public SponsorLeadCaptureService(
        CommunityHubDbContext db,
        SponsorLeadScreeningService screen,
        TimeProvider clock)
    {
        _db = db;
        _screen = screen;
        _clock = clock;
    }

    /// <summary>
    /// Validate + persist one booth-captured lead for a sponsor company.
    /// Returns a friendly result the page surfaces; never throws on a
    /// validation problem.
    /// </summary>
    public async Task<SponsorLeadCaptureResult> CaptureAsync(
        int eventId,
        string sponsorCompanyId,
        string capturedByEmail,
        SponsorLeadCaptureInput input,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sponsorCompanyId))
            return new SponsorLeadCaptureResult(false, "No sponsor company is linked to your account.", null);

        var first   = Trim(input.FirstName, 200);
        var last    = Trim(input.LastName, 200);
        var email   = Trim(input.Email, 320);
        var phone   = Trim(input.Phone, 60);
        var company = Trim(input.Company, 300);
        var jobs    = Trim(input.JobTitle, 200);
        var notes   = Trim(input.Notes, 4000);

        // Reachability: a lead nobody can follow up on is not worth storing.
        if (string.IsNullOrEmpty(email) && string.IsNullOrEmpty(phone))
            return new SponsorLeadCaptureResult(false,
                "Add at least an email or a phone number so the lead can be followed up.", null);

        if (!string.IsNullOrEmpty(email) && !(email.Contains('@') && email.Contains('.')))
            return new SponsorLeadCaptureResult(false,
                "That email address does not look valid — check it or use a phone number instead.", null);

        if (string.IsNullOrEmpty(first) && string.IsNullOrEmpty(last) && string.IsNullOrEmpty(company))
            return new SponsorLeadCaptureResult(false,
                "Add at least a name or a company so you can recognise this lead later.", null);

        var full = string.Join(" ", new[] { first, last }.Where(s => !string.IsNullOrEmpty(s)));
        var now = _clock.GetUtcNow();

        var lead = new SponsorLead
        {
            EventId          = eventId,
            SponsorCompanyId = sponsorCompanyId,
            ZohoRecordId     = string.Empty,          // hub-local; keeps it out of the Zoho unique index
            LeadKind         = SponsorLeadKind.Lead,
            CaptureMethod    = SponsorLeadCaptureMethod.ManualBooth,
            CapturedByEmail  = Trim(capturedByEmail, 320),
            FirstName        = first ?? string.Empty,
            LastName         = last ?? string.Empty,
            FullName         = full,
            Email            = email ?? string.Empty,
            Phone            = phone ?? string.Empty,
            Company          = company ?? string.Empty,
            JobTitle         = jobs ?? string.Empty,
            Source           = "Booth (hub capture)",
            Notes            = notes ?? string.Empty,
            CapturedAt       = now,
            LastSyncedAt     = now,
            Status           = SponsorLeadStatus.Open,
        };

        // Screen on the way in (same heuristic as the Zoho pull). This may
        // auto-junk an unmistakable test entry; a real prospect stays Open.
        _screen.Screen(lead, now);

        _db.SponsorLeads.Add(lead);
        await _db.SaveChangesAsync(ct);

        return new SponsorLeadCaptureResult(true,
            "Lead captured. It is now in your leads pipeline and download feed.", lead.Id);
    }

    /// <summary>
    /// The leads this sponsor company captured in the hub (booth staff),
    /// most-recent first. Excludes Junk so the booth list stays clean; the
    /// row is never deleted, just hidden here.
    /// </summary>
    public async Task<List<SponsorLead>> GetBoothCapturedAsync(
        int eventId, string sponsorCompanyId, int take, CancellationToken ct) =>
        await _db.SponsorLeads
            .Where(l => l.EventId == eventId
                        && l.SponsorCompanyId == sponsorCompanyId
                        && l.CaptureMethod == SponsorLeadCaptureMethod.ManualBooth
                        && l.Status != SponsorLeadStatus.Junk)
            .OrderByDescending(l => l.CapturedAt)
            .Take(take)
            .ToListAsync(ct);

    private static string? Trim(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        return v.Length > max ? v.Substring(0, max) : v;
    }
}
