using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Organizer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Onboarding admin dashboard — counts + lists by STAGE (Pre-selected / Invited /
/// In-progress / Completed) with completion %, filterable by PERSONA, plus a grid
/// of who has / hasn't completed each step they are required to do (so organizers
/// can remind by email). Read-only aggregation from
/// <see cref="OnboardingService.BuildOverviewAsync"/>; completion is persona-aware
/// (a persona only needs ITS required steps — <see cref="OnboardingStepSets"/>).
///
/// An organizer can flip any step flag back to 0 (e.g. a speaker phones in wanting
/// a hotel after all): that re-opens the step and HANDS OFF to the email system
/// (the "remind" hook) by raising an organizer action item — the actual send is
/// the email system's job.
/// </summary>
[Authorize]
public class OnboardingModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly OnboardingService _onboarding;

    public OnboardingModel(
        ICurrentParticipantAccessor participant, OnboardingService onboarding)
    {
        _participant = participant;
        _onboarding = onboarding;
    }

    public OnboardingOverview Overview { get; private set; } = new();
    public bool AccessDenied { get; private set; }
    public string? ActionMessage { get; private set; }

    /// <summary>Persona filter (null/empty = all personas).</summary>
    [BindProperty(SupportsGet = true)]
    public PersonaGroup? Persona { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Msg { get; set; }

    /// <summary>The persona groups offered in the filter (excludes <see cref="PersonaGroup.None"/>).</summary>
    public static IReadOnlyList<PersonaGroup> FilterablePersonas { get; } = new[]
    {
        PersonaGroup.Speaker,
        PersonaGroup.Volunteer,
        PersonaGroup.MediaTeam,
        PersonaGroup.Sponsor,
        PersonaGroup.Organizer,
    };

    public static string PersonaLabel(PersonaGroup persona) => persona switch
    {
        PersonaGroup.Speaker => "Speakers",
        PersonaGroup.Volunteer => "Volunteers",
        PersonaGroup.MediaTeam => "Media team",
        PersonaGroup.Sponsor => "Sponsors",
        PersonaGroup.Organizer => "Organizers",
        _ => persona.ToString(),
    };

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        if (!string.IsNullOrEmpty(Msg)) ActionMessage = Msg;
        Overview = await _onboarding.BuildOverviewAsync(me.EventId, Persona, ct);
        return Page();
    }

    /// <summary>
    /// Flip one step flag back to 0 for one participant — re-opens the step and
    /// triggers the email-system remind hook (organizer action item).
    /// </summary>
    public async Task<IActionResult> OnPostResetStepAsync(
        int participantId, OnboardingStep step, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) return Forbid();

        var reset = await _onboarding.ResetStepAsync(me.EventId, participantId, step, ct);
        var msg = reset
            ? $"Re-opened \"{OnboardingService.LabelFor(step)}\" — a reminder has been queued."
            : "No change (that step was not completed).";
        return RedirectToPage(new { Persona, Msg = msg });
    }
}
