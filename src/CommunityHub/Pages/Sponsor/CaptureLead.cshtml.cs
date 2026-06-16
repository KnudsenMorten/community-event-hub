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
/// Booth lead capture for sponsor staff. A sponsor contact standing at the
/// stand types in the people they meet — name, email/phone, company, what
/// they were interested in — and the lead lands straight in the company's
/// leads pipeline and download feed (no Zoho round-trip needed).
///
/// This is the in-hub complement to the Zoho Backstage scanner: it works on
/// any phone with no app install, and a booth with no Backstage scanner set
/// up can still capture leads. Captured leads are screened on the way in and
/// flow out the existing /api/v1/sponsors/{id}/leads.csv|json export.
///
/// Mobile-first: the form is a single column that works at ~360px.
///
/// Auth: Sponsor role only (this is data entry into the company's own
/// pipeline). Organizers run leads from /Organizer/SponsorAdmin/Leads.
/// </summary>
[Authorize]
public class CaptureLeadModel : PageModel
{
    private const int RecentTake = 15;

    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly SponsorLeadCaptureService _capture;

    public CaptureLeadModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        SponsorLeadCaptureService capture)
    {
        _db = db;
        _participant = participant;
        _capture = capture;
    }

    public bool AccessDenied { get; private set; }
    public bool NoCompanyLink { get; private set; }
    public string? Message { get; private set; }
    public string? Error { get; private set; }

    public List<SponsorLead> RecentLeads { get; private set; } = new();

    [BindProperty] public string? FirstName { get; set; }
    [BindProperty] public string? LastName  { get; set; }
    [BindProperty] public string? Email     { get; set; }
    [BindProperty] public string? Phone     { get; set; }
    [BindProperty] public string? Company   { get; set; }
    [BindProperty] public string? JobTitle  { get; set; }
    [BindProperty] public string? Notes     { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        if (me.Role != ParticipantRole.Sponsor)
        {
            AccessDenied = true;
            return Page();
        }

        var companyId = await GetCompanyIdAsync(me.ParticipantId, ct);
        if (string.IsNullOrWhiteSpace(companyId))
        {
            NoCompanyLink = true;
            return Page();
        }

        await LoadRecentAsync(me.EventId, companyId!, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        if (me.Role != ParticipantRole.Sponsor)
        {
            AccessDenied = true;
            return Page();
        }

        var companyId = await GetCompanyIdAsync(me.ParticipantId, ct);
        if (string.IsNullOrWhiteSpace(companyId))
        {
            NoCompanyLink = true;
            return Page();
        }

        var input = new SponsorLeadCaptureInput(
            FirstName, LastName, Email, Phone, Company, JobTitle, Notes);

        var result = await _capture.CaptureAsync(me.EventId, companyId!, me.Email, input, ct);
        if (result.Ok)
        {
            Message = result.Message;
            // Clear the form so the next person can be entered straight away.
            FirstName = LastName = Email = Phone = Company = JobTitle = Notes = null;
            ModelState.Clear();
        }
        else
        {
            Error = result.Message;
        }

        await LoadRecentAsync(me.EventId, companyId!, ct);
        return Page();
    }

    private async Task LoadRecentAsync(int eventId, string companyId, CancellationToken ct) =>
        RecentLeads = await _capture.GetBoothCapturedAsync(eventId, companyId, RecentTake, ct);

    private async Task<string?> GetCompanyIdAsync(int participantId, CancellationToken ct) =>
        await _db.Participants
            .Where(p => p.Id == participantId)
            .Select(p => p.SponsorCompanyId)
            .FirstOrDefaultAsync(ct);
}
