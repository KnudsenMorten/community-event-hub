using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages;

/// <summary>
/// §1077 stage 3 — THE COMPANY'S OWN PAGE: four steps, no login, reached by a token link.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-11: <i>"same as what you made yesterday so i click the link and get
/// access"</i> — the §1040 monitor-token pattern. It also has to serve a marketing coordinator who
/// is not an attendee at all, which no sign-in could do, and it avoids opening
/// <c>OneDayAccessGate</c> for two roles: four holes in a deliberate gate, plus a new "may this
/// person see this?" question on every attendee surface.</para>
///
/// <para>🔴 <b>Unlike the §1040 monitor page, this one WRITES</b> — that page's fourth defence was
/// "it is read-only", and it is gone here. What replaces it: every write lands on the ONE company
/// the token resolves to, there is no company id in any form post, and the page reveals no personal
/// data at all (the company's own name, and whatever they type themselves). A forwarded link can
/// misstate this company's wishes; it can never reach another company or read anybody's details.</para>
///
/// <para>🔒 <c>noindex</c> in the view: a link pasted into a public thread must not become a search
/// result. That is defence against the accident — the token defends against the attacker.</para>
/// </remarks>
[AllowAnonymous]
[RequestSizeLimit(52_428_800)]
[RequestFormLimits(MultipartBodyLengthLimit = 52_428_800)]
public class VolumePackageWizardModel : PageModel
{
    private readonly VolumePackageWizardService _wizard;
    private readonly VolumePackageGroupPhotoService _groupPhoto;

    public VolumePackageWizardModel(
        VolumePackageWizardService wizard, VolumePackageGroupPhotoService groupPhoto)
    {
        _wizard = wizard;
        _groupPhoto = groupPhoto;
    }

    public VolumePackageCompany? Company { get; private set; }

    /// <summary>
    /// §1077 stage 4 — the group-photo slot, once an organizer has scheduled one.
    /// </summary>
    /// <remarks>
    /// 🔒 Shown on the SAME token page rather than mailed to the company's people: <b>CEH must never
    /// write to the attendees about the photo</b> — some of them will not want to be photographed.
    /// The coordinator downloads the calendar file and forwards it inside their own company, which
    /// is exactly the operator's instruction.
    /// </remarks>
    public GroupPhotoRegistration? Photo { get; private set; }

    /// <summary>1..4. Kept in the query string so Back works and a refresh does not lose the place.</summary>
    public int Step { get; private set; } = 1;

    public string? Message { get; private set; }
    public string? Error { get; private set; }
    public bool CanUploadLogo => _wizard.CanUploadLogo;

    /// <summary>True once the company has said yes or no — the page then shows what it recorded.</summary>
    public bool Finished => Company?.WizardCompletedAt is not null;

    [BindProperty] public bool Keynote { get; set; }
    [BindProperty] public bool Social { get; set; }
    [BindProperty] public bool GroupPhoto { get; set; }
    [BindProperty] public string? CoordinatorName { get; set; }
    [BindProperty] public string? CoordinatorEmail { get; set; }
    [BindProperty] public string? CoordinatorMobile { get; set; }
    [BindProperty] public string? LinkedInUrl { get; set; }
    [BindProperty] public IFormFile? LogoWeb { get; set; }
    [BindProperty] public IFormFile? LogoPrint { get; set; }

    public async Task<IActionResult> OnGetAsync(string token, int step, CancellationToken ct)
    {
        var company = await _wizard.ResolveAsync(token, ct);
        if (company is null) return NotFound();

        await _wizard.NoteOpenedAsync(company, ct);
        Bind(company, step);
        Photo = await _groupPhoto.ForCompanyAsync(company.Id, ct);
        return Page();
    }

    /// <summary>
    /// §1077 stage 4 — the calendar file for the coordinator to forward internally.
    /// </summary>
    /// <remarks>
    /// 🔑 It carries the SAME stable UID as the organizer's invite, so a slot that moves UPDATES the
    /// entry a coordinator already forwarded instead of adding a second one to every colleague's
    /// calendar. ⚠️ And it names NO attendee — a file meant to be passed on must not turn every
    /// forwarded copy into an accept/decline on somebody else's behalf.
    /// </remarks>
    public async Task<IActionResult> OnGetCalendarAsync(string token, CancellationToken ct)
    {
        var company = await _wizard.ResolveAsync(token, ct);
        if (company is null) return NotFound();

        var ics = await _groupPhoto.CoordinatorIcsAsync(company.Id, ct);
        if (ics is null) return NotFound();

        var safe = string.Join("-", company.CustomName.Split(Path.GetInvalidFileNameChars()));
        return File(System.Text.Encoding.UTF8.GetBytes(ics), "text/calendar", $"group-photo-{safe}.ics");
    }

    /// <summary>Step 1 — the three benefits.</summary>
    public async Task<IActionResult> OnPostParticipationAsync(string token, CancellationToken ct)
    {
        var company = await _wizard.ResolveAsync(token, ct);
        if (company is null) return NotFound();

        await _wizard.SaveParticipationAsync(company, Keynote, Social, GroupPhoto, ct);
        return RedirectToPage(new { token, step = 2 });
    }

    /// <summary>
    /// The explicit "we do not want to participate". 🔒 It ends the wizard AND its reminders —
    /// stopping the asking is part of the requirement, not a courtesy.
    /// </summary>
    public async Task<IActionResult> OnPostDeclineAsync(string token, CancellationToken ct)
    {
        var company = await _wizard.ResolveAsync(token, ct);
        if (company is null) return NotFound();

        await _wizard.DeclineAsync(company, ct);
        Bind(company, 1);
        Message = "Thank you — we have noted that you would rather not take part, and we will not "
                + "ask again.";
        return Page();
    }

    /// <summary>Step 2 — the coordinator.</summary>
    public async Task<IActionResult> OnPostCoordinatorAsync(string token, CancellationToken ct)
    {
        var company = await _wizard.ResolveAsync(token, ct);
        if (company is null) return NotFound();

        await _wizard.SaveCoordinatorAsync(
            company, CoordinatorName, CoordinatorEmail, CoordinatorMobile, ct);
        return RedirectToPage(new { token, step = 3 });
    }

    /// <summary>Step 3 — the two logos. Either may be uploaded on its own.</summary>
    public async Task<IActionResult> OnPostLogoAsync(string token, CancellationToken ct)
    {
        var company = await _wizard.ResolveAsync(token, ct);
        if (company is null) return NotFound();

        foreach (var (file, kind) in new[]
                 {
                     (LogoWeb, VolumePackageLogoKind.Web),
                     (LogoPrint, VolumePackageLogoKind.Print),
                 })
        {
            if (file is null || file.Length == 0) continue;

            if (VolumePackageWizardService.LogoRejectionReason(file.FileName, file.Length) is { } why)
            {
                Error = why;
                Bind(company, 3);
                return Page();
            }

            await using var stream = file.OpenReadStream();
            var ok = await _wizard.SaveLogoAsync(
                company, kind, file.FileName, stream, file.Length,
                string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
                ct);

            if (!ok)
            {
                Error = "We could not store that file just now. Please try again, or send it to the "
                      + "organizers by e-mail.";
                Bind(company, 3);
                return Page();
            }
        }

        return RedirectToPage(new { token, step = 4 });
    }

    /// <summary>Step 4 — LinkedIn, and done.</summary>
    public async Task<IActionResult> OnPostFinishAsync(string token, CancellationToken ct)
    {
        var company = await _wizard.ResolveAsync(token, ct);
        if (company is null) return NotFound();

        await _wizard.SaveLinkedInAsync(company, LinkedInUrl, ct);
        await _wizard.CompleteAsync(company, ct);

        Bind(company, 4);
        Message = "Thank you — that is everything we need.";
        return Page();
    }

    private void Bind(VolumePackageCompany company, int step)
    {
        Company = company;
        Step = step is >= 1 and <= 4 ? step : 1;

        Keynote = company.ApprovedKeynoteMention;
        Social = company.ApprovedSocialMediaAnnouncement;
        GroupPhoto = company.ApprovedGroupPhoto;
        CoordinatorName = company.GroupPhotoContactName;
        CoordinatorEmail = company.GroupPhotoContactEmail;
        CoordinatorMobile = company.GroupPhotoContactMobile;
        LinkedInUrl = company.LinkedInUrl;
    }
}
