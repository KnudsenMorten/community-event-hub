using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Visa;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Forms;

/// <summary>
/// §1374 — the visa form. Sign-in required, and only for a participant an organizer flagged
/// <see cref="Participant.NeedsVisa"/>; everyone else gets 404, the same as the menu not showing it.
/// The application ID the organizers enter is shown at the top once set.
/// </summary>
[Authorize]
public class VisaModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly VisaApplicationService _visa;
    private readonly VisaLetterStore _letters;

    public VisaModel(ICurrentParticipantAccessor participant, VisaApplicationService visa, VisaLetterStore letters)
    {
        _participant = participant;
        _visa = visa;
        _letters = letters;
    }

    /// <summary>§1383 — the letter the organizers uploaded for this applicant (null = none yet).</summary>
    public VisaLetterInfo? Letter { get; private set; }

    /// <summary>
    /// §1383 — download YOUR letter. 🔒 No id is taken from the request: the row is looked up by the
    /// signed-in participant, so nobody can fetch another applicant's file. Same 404 gate as the form.
    /// </summary>
    public async Task<IActionResult> OnGetLetterAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!await _visa.MaySeeFormAsync(me.EventId, me.ParticipantId, ct)) return NotFound();

        var letter = await _letters.GetAsync(me.EventId, me.ParticipantId, ct);
        if (letter is null) return NotFound();
        return File(letter.Content, "application/pdf", letter.FileName);
    }

    [BindProperty] public VisaApplication Form { get; set; } = new();

    // Both dates as day / month / year dropdowns (operator 2026-10-28: "date of birth is missing year" ·
    // "it shows a date only but missing year" — the browser date field hid the year).
    [BindProperty] public int? DobDay { get; set; }
    [BindProperty] public int? DobMonth { get; set; }
    [BindProperty] public int? DobYear { get; set; }
    [BindProperty] public int? ArrDay { get; set; }
    [BindProperty] public int? ArrMonth { get; set; }
    [BindProperty] public int? ArrYear { get; set; }

    /// <summary>Years offered for a date of birth: newest first, 100 years back.</summary>
    public IEnumerable<int> BirthYears => Enumerable.Range(DateTime.UtcNow.Year - 100, 101).Reverse();
    /// <summary>Years offered for the arrival: this year and the next.</summary>
    public IEnumerable<int> ArrivalYears => Enumerable.Range(DateTime.UtcNow.Year, 2);

    /// <summary>This edition's hotels (name, address, stored value) for "Where will you stay".</summary>
    public IReadOnlyList<(string Name, string? Address, string Value)> Hotels { get; private set; } =
        Array.Empty<(string, string?, string)>();

    public IReadOnlyList<string> Errors { get; private set; } = Array.Empty<string>();
    public string? Message { get; private set; }
    public DateTimeOffset? SubmittedAt { get; private set; }
    public string? ApplicationId { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!await _visa.MaySeeFormAsync(me.EventId, me.ParticipantId, ct)) return NotFound();

        var saved = await _visa.GetAsync(me.EventId, me.ParticipantId, ct);
        if (saved is not null) Form = saved;
        if (saved?.SubmittedAt is null && string.IsNullOrWhiteSpace(Form.Email)) Form.Email = me.Email;
        if (Form.DateOfBirth is DateOnly dob) (DobDay, DobMonth, DobYear) = (dob.Day, dob.Month, dob.Year);
        if (Form.ArrivalDate is DateOnly arr) (ArrDay, ArrMonth, ArrYear) = (arr.Day, arr.Month, arr.Year);
        // Not answered yet ⇒ start from the hotel an organizer already placed them in.
        if (string.IsNullOrWhiteSpace(Form.Accommodation))
            Form.Accommodation = await _visa.AssignedHotelValueAsync(me.EventId, me.ParticipantId, ct);
        await LoadStatusAsync(me, ct);
        if (TempData["VisaSaved"] is string m) Message = m;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!await _visa.MaySeeFormAsync(me.EventId, me.ParticipantId, ct)) return NotFound();

        var dateErrors = new List<(string Label, string Message)>();
        Form.DateOfBirth = Combine("Date of birth", DobDay, DobMonth, DobYear, dateErrors);
        Form.ArrivalDate = Combine("Planned arrival date", ArrDay, ArrMonth, ArrYear, dateErrors);

        Errors = await _visa.SubmitAsync(me.EventId, me.ParticipantId, Form, ct);
        // An impossible date replaces the service's "not filled in" for that field.
        foreach (var (label, message) in dateErrors)
            Errors = Errors.Where(e => !e.Contains(label)).Prepend(message).ToList();
        if (Errors.Count > 0)
        {
            await LoadStatusAsync(me, ct);
            return Page();
        }
        TempData["VisaSaved"] = "Thank you — your details are saved. The organizers will add your application ID here when it is ready.";
        return RedirectToPage();
    }

    /// <summary>
    /// All three parts or nothing: a missing part leaves the date empty (the service then reports the
    /// field as not filled in). An impossible date (31 February) is reported in words.
    /// </summary>
    private static DateOnly? Combine(string label, int? day, int? month, int? year, List<(string, string)> errors)
    {
        if (day is not int d || month is not int m || year is not int y) return null;
        if (m is < 1 or > 12 || y is < 1900 or > 2100 || d < 1 || d > DateTime.DaysInMonth(y, m))
        {
            errors.Add((label, $"{label}: {d} is not a day in that month."));
            return null;
        }
        return new DateOnly(y, m, d);
    }

    private async Task LoadStatusAsync(CurrentParticipant me, CancellationToken ct)
    {
        var saved = await _visa.GetAsync(me.EventId, me.ParticipantId, ct);
        SubmittedAt = saved?.SubmittedAt;
        ApplicationId = saved?.ApplicationId;
        Letter = await _letters.InfoAsync(me.EventId, me.ParticipantId, ct);
        Hotels = await _visa.HotelChoicesAsync(me.EventId, ct);
    }
}
