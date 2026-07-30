using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Forms.Steps;

/// <summary>
/// Render + edit model for the sponsor Booth-check-in wizard step (§229 / §285 Phase 2).
/// Bound with an EMPTY prefix by the wizard host, so <see cref="Slot"/> matches the posted
/// radio name. <see cref="CurrentSlot"/> is display-only (the currently-saved answer).
/// </summary>
public sealed class SponsorBoothCheckInModel
{
    /// <summary>The posted radio selection — a <see cref="BoothCheckInSlots"/> value.</summary>
    public string? Slot { get; set; }

    /// <summary>§298: how many booth members will check in on the pre-day (feeds the pre-day lunch
    /// headcount). Bound from the POST; pre-filled from the saved value on load.</summary>
    public int? MemberCount { get; set; }

    /// <summary>The currently-saved slot (drives the checked radio); never bound from POST.</summary>
    public string? CurrentSlot { get; set; }
}

/// <summary>
/// Shared submit-service for the sponsor Booth-check-in step (§285 Phase 2). Encapsulates the
/// SAME load + validate + persist as the standalone Company Details "Booth check-in" section
/// (<c>CompanyDetailsModel.OnPostBoothCheckInAsync</c>) so the inline wizard step and the page
/// stay consistent. Hub-only data (no Zoho sync), which is why this is the safe FIRST sponsor
/// section to run inline. Self-registers via <see cref="IWizardFormService"/>.
/// </summary>
public sealed class SponsorBoothCheckInFormService : IWizardFormService
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public SponsorBoothCheckInFormService(CommunityHubDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    private Task<string?> CompanyIdAsync(int participantId, CancellationToken ct) =>
        _db.Participants.Where(p => p.Id == participantId)
            .Select(p => p.SponsorCompanyId).FirstOrDefaultAsync(ct);

    public async Task<SponsorBoothCheckInModel> LoadAsync(int eventId, int participantId, CancellationToken ct)
    {
        var companyId = await CompanyIdAsync(participantId, ct);
        var current = companyId is null ? null : await _db.SponsorInfos.AsNoTracking()
            .Where(s => s.EventId == eventId && s.SponsorCompanyId == companyId)
            .Select(s => new { s.BoothCheckInSlot, s.BoothCheckInMemberCount })
            .FirstOrDefaultAsync(ct);
        return new SponsorBoothCheckInModel
        {
            CurrentSlot = current?.BoothCheckInSlot,
            MemberCount = current?.BoothCheckInMemberCount,
        };
    }

    public async Task<WizardStepOutcome> SaveAsync(
        SponsorBoothCheckInModel model, int eventId, int participantId, string email,
        ModelStateDictionary modelState, CancellationToken ct)
    {
        var companyId = await CompanyIdAsync(participantId, ct);
        if (companyId is null) return WizardStepOutcome.NotRelevant;

        var info = await _db.SponsorInfos
            .FirstOrDefaultAsync(s => s.EventId == eventId && s.SponsorCompanyId == companyId, ct);
        // Booth check-in is exhibitor-only — the wizard plan already gates the step on HasBooth,
        // but guard here too so a stray POST can't create/alter a non-exhibitor row.
        if (info is null || !info.HasBooth) return WizardStepOutcome.NotRelevant;

        model.CurrentSlot = info.BoothCheckInSlot;
        if (!BoothCheckInSlots.IsValid(model.Slot))
        {
            modelState.AddModelError(nameof(model.Slot),
                "Please pick when you expect to arrive at your booth (or the not-participating option).");
            return WizardStepOutcome.Invalid;
        }

        // §477: the head count is MANDATORY — it feeds the pre-day LUNCH order, and a blank meant
        // an exhibitor's team was silently missing from the catering count.
        //
        // ❗ Required only when they ARE attending: asking "how many will check in" after they chose
        // "We don't expect to participate on pre-day" would be unanswerable, so the opt-out stays
        // exempt (and still clears any previously-saved count below).
        var attending = model.Slot != BoothCheckInSlots.NotParticipating;
        if (attending && model.MemberCount is not (int and > 0))
        {
            modelState.AddModelError(nameof(model.MemberCount),
                "Please tell us how many booth members will check in on the pre-day — we use it to order the pre-day lunch.");
            return WizardStepOutcome.Invalid;
        }

        info.BoothCheckInSlot = model.Slot;
        // §298: booth-member count only means something when the team IS attending the pre-day;
        // clear it on the not-participating opt-out. Clamp to a sane 1..50.
        info.BoothCheckInMemberCount = attending
            ? Math.Min(model.MemberCount!.Value, 50)
            : null;
        info.BoothCheckInSetAt = _clock.GetUtcNow();
        info.BoothCheckInSetByEmail = email;
        await _db.SaveChangesAsync(ct);

        model.CurrentSlot = model.Slot;   // §291: saved ⇒ the step's done-signal now reads true
        return WizardStepOutcome.Advance;
    }
}
