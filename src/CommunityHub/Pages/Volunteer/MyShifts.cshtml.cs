using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Volunteers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Volunteer;

/// <summary>
/// VOLUNTEER self-service shift management (REQUIREMENTS §20 Volunteer "Shift
/// availability + decline/swap + per-task instructions"): ONE mobile-first page
/// where a volunteer, for each shift they are assigned to, can <b>confirm</b>
/// they can take it, <b>decline</b> it, or <b>request a swap</b> (offer it back),
/// each with an optional note — and read the shift's <b>per-task instructions</b>.
/// All writes go through <see cref="VolunteerShiftService"/>, which enforces the
/// volunteer can only touch their OWN assigned shift (the participant id comes
/// from the session, never the client) and raises a coordinator-visible signal on
/// the existing organizer action queue when a shift is declined / swap-requested.
///
/// Booking / event check-in / live headcount are OUT OF SCOPE (Zoho Backstage).
/// </summary>
[Authorize]
public class MyShiftsModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly VolunteerScheduleBuilder _schedule;
    private readonly VolunteerShiftService _shifts;

    public MyShiftsModel(
        ICurrentParticipantAccessor participant,
        VolunteerScheduleBuilder schedule,
        VolunteerShiftService shifts)
    {
        _participant = participant;
        _schedule = schedule;
        _shifts = shifts;
    }

    public VolunteerSchedule Schedule { get; private set; } =
        new(Array.Empty<VolunteerScheduleEntry>(), Array.Empty<VolunteerHelpRequest>());

    [TempData] public string? Notice { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        Schedule = await _schedule.BuildAsync(me.EventId, me.ParticipantId, ct);
        return Page();
    }

    public Task<IActionResult> OnPostConfirmAsync(int taskId, CancellationToken ct) =>
        ApplyAsync(taskId, ShiftDecisionStatus.Confirmed, null, ct);

    public Task<IActionResult> OnPostDeclineAsync(int taskId, string? note, CancellationToken ct) =>
        ApplyAsync(taskId, ShiftDecisionStatus.Declined, note, ct);

    public Task<IActionResult> OnPostSwapAsync(int taskId, string? note, CancellationToken ct) =>
        ApplyAsync(taskId, ShiftDecisionStatus.SwapRequested, note, ct);

    public Task<IActionResult> OnPostWithdrawAsync(int taskId, CancellationToken ct) =>
        ApplyAsync(taskId, ShiftDecisionStatus.None, null, ct);

    private async Task<IActionResult> ApplyAsync(
        int taskId, ShiftDecisionStatus decision, string? note, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        try
        {
            await _shifts.SetDecisionAsync(me.EventId, me.ParticipantId, taskId, decision, note, ct);
            Notice = decision switch
            {
                ShiftDecisionStatus.Declined => "Shift declined — a coordinator will reassign it.",
                ShiftDecisionStatus.SwapRequested => "Swap requested — a coordinator will pick it up.",
                ShiftDecisionStatus.Confirmed => "Thanks — you're confirmed for this shift.",
                _ => "Shift updated.",
            };
            return RedirectToPage();
        }
        catch (VolunteerValidationException ex) { Notice = ex.Message; return RedirectToPage(); }
        catch (VolunteerAccessDeniedException) { return Forbid(); }
    }
}
