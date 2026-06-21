using System.Text;
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

    /// <summary>
    /// How many people are still onboarding (every stage except Completed) — drives
    /// the "who hasn't onboarded yet" export card's count. Derived from the
    /// overview's stage stats so it matches the dashboard exactly.
    /// </summary>
    public int PendingCount => Overview.StageStats
        .Where(s => s.Stage != OnboardingStage.Completed)
        .Sum(s => s.Count);

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
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var reset = await _onboarding.ResetStepAsync(me.EventId, participantId, step, ct);
        var msg = reset
            ? $"Re-opened \"{OnboardingService.LabelFor(step)}\" — a reminder has been queued."
            : "No change (that step was not completed).";
        return RedirectToPage(new { Persona, Msg = msg });
    }

    /// <summary>
    /// Re-open one step for EVERY person in a persona who currently has it done —
    /// the bulk sibling of the per-row "Re-open". Each affected person gets the
    /// same email-system remind hand-off. Organizer-gated server-side; honours the
    /// current persona filter for the redirect. Reports an honest count (or a
    /// no-op when nobody had that step done).
    /// </summary>
    public async Task<IActionResult> OnPostReopenPersonaStepAsync(
        PersonaGroup personaGroup, OnboardingStep step, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var result = await _onboarding.ResetStepForPersonaAsync(
            me.EventId, personaGroup, step, ct);

        var personaName = PersonaLabel(personaGroup);
        var stepName = OnboardingService.LabelFor(step);
        var msg = result.IsNoOp
            ? $"No change — nobody in {personaName} had \"{stepName}\" completed."
            : $"Re-opened \"{stepName}\" for {result.Reopened} person(s) in {personaName} — a reminder has been queued for each.";

        // Keep the page filtered to the persona we just acted on so the organizer
        // sees the result in context.
        return RedirectToPage(new { Persona = personaGroup, Msg = msg });
    }

    /// <summary>
    /// Download the "who hasn't onboarded yet" list as CSV — every participant in
    /// the pipeline who has NOT completed all their persona's required steps, with
    /// the steps still missing. Honours the current persona filter. Organizer-gated
    /// server-side; read-only (never writes). UTF-8 with a BOM so Excel detects the
    /// encoding for Danish names.
    /// </summary>
    public async Task<IActionResult> OnGetPendingCsvAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var csv = await _onboarding.BuildPendingCsvAsync(me.EventId, Persona, ct);
        var bytes = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(csv)).ToArray();
        var fileName = Persona is null
            ? "onboarding-pending.csv"
            : $"onboarding-pending-{Persona.Value.ToString().ToLowerInvariant()}.csv";
        return File(bytes, "text/csv", fileName);
    }
}
