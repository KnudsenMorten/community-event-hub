using System.Text.RegularExpressions;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Pages.Volunteer;

/// <summary>
/// PUBLIC (anonymous) volunteer-application form. Anyone with the URL can
/// submit their interest in volunteering at the currently-active event. The
/// submission creates a <see cref="Participant"/> row with
/// <c>Role = ParticipantRole.Volunteer</c> and <c>IsActive = false</c> --
/// that combination is the "applicant pending review" flag. An organizer
/// later approves them via the Organizer dashboard (flip IsActive to true),
/// after which the person can PIN-log-in and use the volunteer-availability
/// form to pick shifts.
///
/// URL: published as /volunteer/signup so it can be linked from social /
/// the conference website without needing /Forms/* slug knowledge.
/// </summary>
[AllowAnonymous]
public class SignupModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;
    private readonly ILogger<SignupModel> _log;

    public SignupModel(CommunityHubDbContext db, TimeProvider clock, ILogger<SignupModel> log)
    {
        _db = db;
        _clock = clock;
        _log = log;
    }

    // --- Bound form fields --------------------------------------------------
    [BindProperty] public string FullName   { get; set; } = string.Empty;
    [BindProperty] public string Email      { get; set; } = string.Empty;
    [BindProperty] public string? Phone     { get; set; }
    [BindProperty] public string? Country   { get; set; }
    [BindProperty] public string? Interests { get; set; }
    [BindProperty] public string? Motivation { get; set; }

    // --- Shift availability (the survey core) -------------------------------
    [BindProperty] public List<string> SelectedShifts { get; set; } = new();
    [BindProperty] public string? PreferredRole { get; set; }
    [BindProperty] public int MaxHoursPerDay { get; set; } = 8;

    /// <summary>The shift catalogue — single source shared with the logged-in wizard.</summary>
    public static IReadOnlyList<string> ShiftCatalogue =>
        CommunityHub.Pages.Forms.VolunteerWizardModel.ShiftCatalogue;

    private const char ShiftDelimiter = '|';

    /// <summary>
    /// Honeypot. Hidden CSS-off-screen on the page. Humans cannot see it; bots
    /// fill every input. Any non-empty value -> silent 200 OK (no DB write).
    /// </summary>
    [BindProperty] public string? Website { get; set; }

    // --- View-side state ----------------------------------------------------
    public string? EventDisplayName { get; private set; }
    public bool NoActiveEvent { get; private set; }
    public bool SubmittedOk { get; private set; }
    public string? SubmittedName { get; private set; }
    public string? SubmittedEmail { get; private set; }
    public string? ErrorMessage { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var active = await GetActiveEventAsync(ct);
        if (active is null) { NoActiveEvent = true; return Page(); }
        EventDisplayName = active.DisplayName;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        // Honeypot. Pretend success without writing anything -- attackers don't
        // learn that their probe was detected, real users never trigger this.
        if (!string.IsNullOrWhiteSpace(Website))
        {
            _log.LogInformation("Volunteer signup honeypot tripped from {Ip}", HttpContext.Connection.RemoteIpAddress);
            SubmittedOk = true;
            SubmittedName = "there";
            SubmittedEmail = "(no email recorded)";
            EventDisplayName = "the event";
            return Page();
        }

        var active = await GetActiveEventAsync(ct);
        if (active is null) { NoActiveEvent = true; return Page(); }
        EventDisplayName = active.DisplayName;

        // --- Validate inputs -------------------------------------------------
        var fullName = (FullName ?? string.Empty).Trim();
        var email    = (Email    ?? string.Empty).Trim().ToLowerInvariant();
        var phone    = string.IsNullOrWhiteSpace(Phone)   ? null : Phone.Trim();

        if (fullName.Length < 2)
        {
            ErrorMessage = "Please enter your full name.";
            return Page();
        }
        if (!IsPlausibleEmail(email))
        {
            ErrorMessage = "Please enter a valid email address.";
            return Page();
        }

        // --- Dedup: same email applying twice to the same edition ----------
        // The Participant table has a unique (EventId, Email) index. If an
        // entry already exists, we surface a friendly message instead of a
        // crash. Two flavours:
        //   1) existing entry is an active participant       -> "you're already in"
        //   2) existing entry is a pending applicant         -> "got your earlier application"
        //   3) existing entry is some other inactive role    -> generic "contact organizer"
        var existing = await _db.Participants.FirstOrDefaultAsync(
            p => p.EventId == active.Id && p.Email == email, ct);
        if (existing is not null)
        {
            if (existing.IsActive)
            {
                ErrorMessage = $"Looks like {email} is already registered for this event. If you forgot how to sign in, request a PIN at the Sign in page.";
            }
            else if (existing.Role == ParticipantRole.Volunteer)
            {
                // Re-submission from a pending applicant: refresh their shift
                // availability so an updated survey overwrites the earlier one.
                await SaveAvailabilityAsync(active.Id, existing.Id, ct);
                SubmittedOk    = true;
                SubmittedName  = string.IsNullOrWhiteSpace(existing.FullName) ? fullName : existing.FullName;
                SubmittedEmail = existing.Email;
                return Page();
            }
            else
            {
                ErrorMessage = $"{email} is already on file for this event. Please contact the organizer team.";
            }
            return Page();
        }

        // --- Create the applicant -----------------------------------------
        var applicant = new Participant
        {
            EventId   = active.Id,
            Email     = email,
            FullName  = fullName,
            Phone     = phone,
            Role      = ParticipantRole.Volunteer,
            IsActive  = false,                 // <-- "applicant, not yet selected"
            // Onboarding pre-selection queue: lands inactive, from the interest
            // form. An organizer validates + activates it in the queue.
            LifecycleState = ParticipantLifecycleState.Inactive,
            QueueSource    = ParticipantQueueSource.VolunteerInterestForm,
            CreatedAt = _clock.GetUtcNow(),
        };
        _db.Participants.Add(applicant);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            _log.LogWarning(ex, "Volunteer signup DB write failed for {Email}", email);
            ErrorMessage = "We hit a problem saving your application. Please try again in a moment.";
            return Page();
        }

        // Save their shift availability (the survey core) against the new applicant.
        await SaveAvailabilityAsync(active.Id, applicant.Id, ct);

        _log.LogInformation(
            "New volunteer applicant: id={Id} email={Email} name={Name} event={EventId} country={Country} interests={Interests} shifts={Shifts}",
            applicant.Id, email, fullName, active.Id, Country ?? "(none)", Interests ?? "(none)", SelectedShifts.Count);

        SubmittedOk    = true;
        SubmittedName  = fullName.Split(' ').First();
        SubmittedEmail = email;
        return Page();
    }

    // ------- Helpers --------------------------------------------------------

    /// <summary>
    /// Upsert the applicant's <see cref="VolunteerAvailability"/> from the survey's
    /// shift selection. Tampered values are dropped against the catalogue; hours are
    /// clamped to a sane day (1..24). No row is written when nothing valid was
    /// picked (the person can still be a pending applicant without stated shifts).
    /// </summary>
    private async Task SaveAvailabilityAsync(int eventId, int participantId, CancellationToken ct)
    {
        var clean = (SelectedShifts ?? new List<string>())
            .Where(s => ShiftCatalogue.Contains(s))
            .Distinct()
            .ToList();
        if (clean.Count == 0) return;

        var hours = Math.Clamp(MaxHoursPerDay, 1, 24);
        var preferred = string.IsNullOrWhiteSpace(PreferredRole) ? null : PreferredRole.Trim();

        var availability = await _db.VolunteerAvailabilities.FirstOrDefaultAsync(
            v => v.EventId == eventId && v.ParticipantId == participantId, ct);
        if (availability is null)
        {
            availability = new VolunteerAvailability
            {
                EventId = eventId,
                ParticipantId = participantId,
                CreatedAt = _clock.GetUtcNow(),
            };
            _db.VolunteerAvailabilities.Add(availability);
        }
        else
        {
            availability.UpdatedAt = _clock.GetUtcNow();
        }
        availability.SelectedShifts = string.Join(ShiftDelimiter, clean);
        availability.PreferredRole = preferred;
        availability.MaxHoursPerDay = hours;
        await _db.SaveChangesAsync(ct);
    }

    private async Task<Event?> GetActiveEventAsync(CancellationToken ct)
    {
        return await _db.Events
            .Where(e => e.IsActive)
            .OrderByDescending(e => e.Id)
            .FirstOrDefaultAsync(ct);
    }

    private static readonly Regex EmailShape = new(
        @"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

    private static bool IsPlausibleEmail(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        if (s.Length > 320) return false;
        return EmailShape.IsMatch(s);
    }
}
