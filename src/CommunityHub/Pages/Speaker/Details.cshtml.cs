using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Forms;
using CommunityHub.Forms.Steps;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Speaker;

/// <summary>
/// The consolidated "Speaker Details" page (§26c) — ONE place for everything a speaker
/// owns: name, bio + socials, photo, MS accreditation / skills, country, and contact
/// preferences. Replaces the split between the old /Forms/Speaker (bio) and the speaker
/// fields on /Profile. Bio fields seeded from Sessionize are marked speaker-edited on
/// change so the delta re-import never overwrites them. "Save &amp; Sync to Zoho" pushes
/// the speaker to Backstage (gated by the publish + ring rules; a no-op until a live
/// Zoho speaker writer is configured).
///
/// <para>REQUIREMENTS §148: this standalone page is now a thin SHELL — it renders the shared
/// <c>_DetailsFields</c> partial and delegates load + validate + persist + the speaker-edit
/// side-effects to <see cref="SpeakerDetailsFormService"/>. The SAME service backs the inline
/// wizard step (<c>SpeakerDetailsStepHandler</c>), so the standalone page and the wizard behave
/// identically on the plain Save path. The "Save &amp; sync to Zoho" action stays on THIS page
/// only (the wizard never syncs); it runs the service's plain Save then pushes to Backstage,
/// gated on whether a sync-relevant field actually changed.</para>
/// </summary>
[Authorize]
public class DetailsModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly SpeakerDetailsFormService _form;
    private readonly SpeakerBioBackstageSyncService _sync;

    public DetailsModel(
        ICurrentParticipantAccessor participant,
        SpeakerDetailsFormService form,
        SpeakerBioBackstageSyncService sync)
    {
        _participant = participant;
        _form = form;
        _sync = sync;
    }

    public static readonly string[] AccreditationOptions = CommunityHub.Pages.Forms.SpeakerModel.AccreditationOptions;
    public static readonly string[] GenderOptions = CommunityHub.Pages.Forms.SpeakerModel.GenderOptions;

    /// <summary>The shared render+edit model rendered by the <c>_DetailsFields</c> partial. Bound
    /// with an EMPTY prefix in the save handlers so the partial's flat input names (FirstName /
    /// Biography / …) match — identical to the inline wizard step.</summary>
    public SpeakerDetailsFormModel Form { get; private set; } = new();

    public bool AccessDenied { get; private set; }
    public string? Message { get; private set; }
    public bool IsError { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Speaker) { AccessDenied = true; return Page(); }

        Form = await _form.LoadAsync(me.EventId, me.ParticipantId, me.Email, ct);
        return Page();
    }

    public Task<IActionResult> OnPostSaveAsync(CancellationToken ct) => SaveAsync(sync: false, ct);
    public Task<IActionResult> OnPostSaveAndSyncAsync(CancellationToken ct) => SaveAsync(sync: true, ct);

    private async Task<IActionResult> SaveAsync(bool sync, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Speaker) { AccessDenied = true; return Page(); }

        // Bind the posted editable fields (empty prefix → flat names from the partial), then
        // delegate validate + persist + the speaker-edit side-effects to the shared service —
        // the SAME plain-save flow the inline wizard step runs.
        Form = new SpeakerDetailsFormModel { Email = me.Email };
        await TryUpdateModelAsync(Form, name: string.Empty);

        var result = await _form.SaveAsync(Form, me.EventId, me.ParticipantId, me.Email, me.Role, ModelState, ct);
        Message = Form.Message;
        if (result.Outcome != WizardStepOutcome.Advance) return Page();   // invalid → re-render with errors

        if (sync)
        {
            try
            {
                // DEDUPE: only let the sync re-email the organizers' manual-update alert
                // when something the speaker owns actually changed. A no-change re-sync of
                // an already-in-Backstage speaker stays silent.
                var r = await _sync.SyncOneAsync(
                    me.EventId, me.ParticipantId, alertOnExisting: result.SyncRelevantChanged, ct: ct);
                if (r.Outcome == SpeakerBioSyncOutcome.BlockedNeedsManualUpdate && !result.SyncRelevantChanged)
                {
                    Message += " You're already in Zoho Backstage and nothing changed since your last save, so the organizers were not re-notified.";
                }
                else
                {
                    Message += r.Outcome switch
                    {
                        SpeakerBioSyncOutcome.PushedPublic => " Created in Zoho Backstage (public).",
                        SpeakerBioSyncOutcome.PushedDraft  => " Created in Zoho Backstage (not featured — awaiting publish approval).",
                        SpeakerBioSyncOutcome.BlockedNeedsManualUpdate =>
                            " You're already in Zoho Backstage — its API can't update an existing speaker, so the organizers were emailed to update you there by hand.",
                        SpeakerBioSyncOutcome.RingGated    => " Saved. Zoho sync is held for you until the organizers enable it for your group.",
                        SpeakerBioSyncOutcome.Disabled     => " Saved. (Zoho speaker sync is off for this edition.)",
                        SpeakerBioSyncOutcome.BuiltOnly    => " Saved. (Zoho speaker sync isn't configured yet.)",
                        SpeakerBioSyncOutcome.Failed       => " Saved, but the Zoho sync failed: " + (r.Error ?? "unknown error") + ".",
                        _ => string.Empty,
                    };
                }
                if (r.Outcome == SpeakerBioSyncOutcome.Failed) IsError = true;
            }
            catch (Exception ex) { IsError = true; Message += " Zoho sync error: " + ex.Message; }
        }

        return Page();
    }
}
