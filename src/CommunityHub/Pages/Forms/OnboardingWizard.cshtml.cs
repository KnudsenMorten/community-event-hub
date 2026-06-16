using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Forms;

/// <summary>
/// The onboarding wizard for any signed-in persona — the initial mandatory
/// steps a freshly-activated participant runs once:
///   1. Verify / update bio
///   2. Update / replace bio picture
///   3. Hotel
///   4. Appreciation
///   5. Swag
/// Each completed step sets its <c>Participant.OnboardingCompleted_*</c> flag via
/// <see cref="OnboardingService.MarkStepCompleteAsync"/>, so the organizer
/// onboarding overview tracks who is done. Step state + the per-step data are
/// carried in bound fields between steps; a refresh mid-wizard is harmless.
///
/// The wizard writes the participant's input to the existing form entities
/// (<see cref="SpeakerProfile"/> bio/photo, <see cref="HotelBooking"/>,
/// <see cref="SwagPreference"/>) so it does not duplicate storage — it is the
/// guided first-run path over those forms, plus the completion flags.
/// </summary>
[Authorize]
public class OnboardingWizardModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly OnboardingService _onboarding;
    private readonly TimeProvider _clock;

    public OnboardingWizardModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        OnboardingService onboarding,
        TimeProvider clock)
    {
        _db = db;
        _participant = participant;
        _onboarding = onboarding;
        _clock = clock;
    }

    /// <summary>
    /// The PERSONA-required step sequence for the signed-in participant. Steps
    /// differ per persona (<see cref="OnboardingStepSets"/>): a sponsor sees only
    /// appreciation + swag, a speaker sees all five. The wizard walks this list,
    /// so <see cref="Step"/> is a 1-based index INTO it, not a fixed 1..5.
    /// </summary>
    public IReadOnlyList<OnboardingStep> Steps { get; private set; } = Array.Empty<OnboardingStep>();

    /// <summary>How many steps this persona must complete.</summary>
    public int StepCount => Steps.Count;

    /// <summary>Current wizard step, 1..StepCount (1-based index into <see cref="Steps"/>).</summary>
    [BindProperty] public int Step { get; set; } = 1;

    /// <summary>The actual <see cref="OnboardingStep"/> the current page is on.</summary>
    public OnboardingStep CurrentStep =>
        Step >= 1 && Step <= Steps.Count ? Steps[Step - 1] : OnboardingStep.Bio;

    // --- Step 1: bio --------------------------------------------------------
    [BindProperty] public string? Biography { get; set; }
    [BindProperty] public string? Tagline { get; set; }

    // --- Step 2: picture ----------------------------------------------------
    [BindProperty] public string? PhotoUrl { get; set; }

    // --- Step 3: hotel ------------------------------------------------------
    [BindProperty] public bool NeedsRoom { get; set; }
    [BindProperty] public string? HotelNotes { get; set; }

    // --- Step 4: appreciation ----------------------------------------------
    [BindProperty] public bool WantsAppreciationAward { get; set; } = true;
    [BindProperty] public bool WantsCredlyBadge { get; set; } = true;

    // --- Step 5: swag -------------------------------------------------------
    [BindProperty] public bool WantsPolo { get; set; }
    [BindProperty] public string? PoloSize { get; set; }
    [BindProperty] public bool WantsJacket { get; set; }
    [BindProperty] public string? JacketSize { get; set; }

    // --- View state ---------------------------------------------------------
    public string? Message { get; private set; }
    public bool Finished { get; private set; }
    public bool[] Completed { get; private set; } = Array.Empty<bool>();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        ResolveSteps(me);
        await PrefillAsync(me, ct);
        Step = 1;

        // A persona with no required steps (e.g. an attendee) has nothing to do.
        if (StepCount == 0) Finished = true;
        return Page();
    }

    /// <summary>Forward a step.</summary>
    public IActionResult OnPostNext()
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        ResolveSteps(me);
        if (Step < StepCount) Step++;
        return Page();
    }

    /// <summary>Back a step.</summary>
    public IActionResult OnPostBack()
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        ResolveSteps(me);
        if (Step > 1) Step--;
        return Page();
    }

    /// <summary>Load the persona's required-step sequence for the current participant.</summary>
    private void ResolveSteps(CurrentParticipant me) =>
        Steps = OnboardingStepSets.For(me.Role);

    /// <summary>
    /// Save the current step's data, mark it complete, and advance. The handler
    /// is one entry point keyed by <see cref="Step"/> so the wizard stays a
    /// single postback surface.
    /// </summary>
    public Task<IActionResult> OnPostCompleteStepAsync(CancellationToken ct)
        => CompleteStepCore(ct);

    private async Task<IActionResult> CompleteStepCore(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        ResolveSteps(me);
        if (StepCount == 0) { Finished = true; return Page(); }

        var step = CurrentStep;
        switch (step)
        {
            case OnboardingStep.Bio:          await SaveBioAsync(me, ct); break;
            case OnboardingStep.Picture:      await SavePictureAsync(me, ct); break;
            case OnboardingStep.Hotel:        await SaveHotelAsync(me, ct); break;
            case OnboardingStep.Appreciation: await SaveAppreciationAsync(me, ct); break;
            case OnboardingStep.Swag:         await SaveSwagAsync(me, ct); break;
        }

        await _onboarding.MarkStepCompleteAsync(me.EventId, me.ParticipantId, step, ct);

        if (Step < StepCount)
        {
            Step++;
            Message = $"\"{OnboardingService.LabelFor(step)}\" saved.";
            await RefreshCompletedAsync(me, ct);
            return Page();
        }

        Finished = true;
        Message = "All onboarding steps complete — thank you!";
        await RefreshCompletedAsync(me, ct);
        return Page();
    }

    // ----- per-step persistence -------------------------------------------
    private async Task<SpeakerProfile> GetOrCreateProfileAsync(
        CurrentParticipant me, CancellationToken ct)
    {
        var profile = await _db.SpeakerProfiles.FirstOrDefaultAsync(
            s => s.EventId == me.EventId && s.ParticipantId == me.ParticipantId, ct);
        if (profile is null)
        {
            profile = new SpeakerProfile
            {
                EventId = me.EventId,
                ParticipantId = me.ParticipantId,
                CreatedAt = _clock.GetUtcNow(),
            };
            _db.SpeakerProfiles.Add(profile);
        }
        return profile;
    }

    private async Task SaveBioAsync(CurrentParticipant me, CancellationToken ct)
    {
        var profile = await GetOrCreateProfileAsync(me, ct);
        var now = _clock.GetUtcNow();
        profile.Biography = string.IsNullOrWhiteSpace(Biography) ? profile.Biography : Biography.Trim();
        profile.Tagline = string.IsNullOrWhiteSpace(Tagline) ? profile.Tagline : Tagline.Trim();
        profile.MarkSpeakerEdited(SpeakerProfile.BioFields.Biography, now);
        profile.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
    }

    private async Task SavePictureAsync(CurrentParticipant me, CancellationToken ct)
    {
        var profile = await GetOrCreateProfileAsync(me, ct);
        var now = _clock.GetUtcNow();
        if (!string.IsNullOrWhiteSpace(PhotoUrl)) profile.PhotoUrl = PhotoUrl.Trim();
        profile.MarkSpeakerEdited(SpeakerProfile.BioFields.PhotoUrl, now);
        profile.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
    }

    private async Task SaveHotelAsync(CurrentParticipant me, CancellationToken ct)
    {
        var hotel = await _db.HotelBookings.FirstOrDefaultAsync(
            h => h.EventId == me.EventId && h.ParticipantId == me.ParticipantId, ct);
        if (hotel is null)
        {
            hotel = new HotelBooking
            {
                EventId = me.EventId,
                ParticipantId = me.ParticipantId,
                CreatedAt = _clock.GetUtcNow(),
            };
            _db.HotelBookings.Add(hotel);
        }
        else
        {
            hotel.UpdatedAt = _clock.GetUtcNow();
        }
        hotel.NeedsRoom = NeedsRoom;
        hotel.Notes = string.IsNullOrWhiteSpace(HotelNotes) ? null : HotelNotes.Trim();
        await _db.SaveChangesAsync(ct);
    }

    private async Task<SwagPreference> GetOrCreateSwagAsync(
        CurrentParticipant me, CancellationToken ct)
    {
        var swag = await _db.SwagPreferences.FirstOrDefaultAsync(
            s => s.EventId == me.EventId && s.ParticipantId == me.ParticipantId, ct);
        if (swag is null)
        {
            swag = new SwagPreference
            {
                EventId = me.EventId,
                ParticipantId = me.ParticipantId,
                CreatedAt = _clock.GetUtcNow(),
            };
            _db.SwagPreferences.Add(swag);
        }
        else
        {
            swag.UpdatedAt = _clock.GetUtcNow();
        }
        return swag;
    }

    private async Task SaveAppreciationAsync(CurrentParticipant me, CancellationToken ct)
    {
        var swag = await GetOrCreateSwagAsync(me, ct);
        swag.WantsGift = WantsAppreciationAward;
        swag.WantsCredlyBadge = WantsCredlyBadge;
        await _db.SaveChangesAsync(ct);
    }

    private async Task SaveSwagAsync(CurrentParticipant me, CancellationToken ct)
    {
        var swag = await GetOrCreateSwagAsync(me, ct);
        swag.WantsPolo = WantsPolo;
        swag.PoloSize = WantsPolo && !string.IsNullOrWhiteSpace(PoloSize) ? PoloSize.Trim() : null;
        swag.WantsJacket = WantsJacket;
        swag.JacketSize = WantsJacket && !string.IsNullOrWhiteSpace(JacketSize) ? JacketSize.Trim() : null;
        await _db.SaveChangesAsync(ct);
    }

    // ----- prefill / progress ---------------------------------------------
    private async Task PrefillAsync(CurrentParticipant me, CancellationToken ct)
    {
        var profile = await _db.SpeakerProfiles.AsNoTracking().FirstOrDefaultAsync(
            s => s.EventId == me.EventId && s.ParticipantId == me.ParticipantId, ct);
        Biography = profile?.Biography;
        Tagline = profile?.Tagline;
        PhotoUrl = profile?.PhotoUrl;

        var hotel = await _db.HotelBookings.AsNoTracking().FirstOrDefaultAsync(
            h => h.EventId == me.EventId && h.ParticipantId == me.ParticipantId, ct);
        NeedsRoom = hotel?.NeedsRoom ?? false;
        HotelNotes = hotel?.Notes;

        var swag = await _db.SwagPreferences.AsNoTracking().FirstOrDefaultAsync(
            s => s.EventId == me.EventId && s.ParticipantId == me.ParticipantId, ct);
        if (swag is not null)
        {
            WantsAppreciationAward = swag.WantsGift;
            WantsCredlyBadge = swag.WantsCredlyBadge;
            WantsPolo = swag.WantsPolo;
            PoloSize = swag.PoloSize;
            WantsJacket = swag.WantsJacket;
            JacketSize = swag.JacketSize;
        }

        await RefreshCompletedAsync(me, ct);
    }

    private async Task RefreshCompletedAsync(CurrentParticipant me, CancellationToken ct)
    {
        var p = await _db.Participants.AsNoTracking().FirstOrDefaultAsync(
            x => x.Id == me.ParticipantId && x.EventId == me.EventId, ct);
        if (p is null) return;
        // Completion aligned to THIS persona's step sequence (index-for-index
        // with Steps), so the progress chips match the steps actually shown.
        Completed = Steps.Select(s => OnboardingStepSets.IsStepDone(p, s)).ToArray();
    }
}
