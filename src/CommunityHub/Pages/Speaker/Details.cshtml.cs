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
/// change so the delta re-import never overwrites them.
///
/// <para>REQUIREMENTS §148: this standalone page is now a thin SHELL — it renders the shared
/// <c>_DetailsFields</c> partial and delegates load + validate + persist + the speaker-edit
/// side-effects to <see cref="SpeakerDetailsFormService"/>. The SAME service backs the inline
/// wizard step (<c>SpeakerDetailsStepHandler</c>), so the standalone page and the wizard behave
/// identically on the persist path.</para>
///
/// <para>REQUIREMENTS §195: the page has a SINGLE <em>Save</em> button. That one Save BOTH
/// persists to SQL AND pushes the speaker to Backstage/Zoho (gated by the publish + ring rules;
/// a no-op until a live Zoho speaker writer is configured). The sync is fail-safe — if it errors
/// the SQL save is kept and only a non-fatal warning is surfaced — and the organizer manual-update
/// alert is deduped to runs where a sync-relevant field actually changed. The inline wizard step
/// never syncs (it persists only).</para>
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

    // §195: ONE Save button. The single Save BOTH persists to SQL AND syncs to Zoho
    // (operator: "save must save to sql + sync to zoho"). The sync is fail-safe — a Zoho
    // error never loses the SQL save (see the try/catch below), it only adds a non-fatal
    // warning. The organizer manual-update alert is still deduped to real changes
    // (alertOnExisting: result.SyncRelevantChanged), so a no-change save stays silent.
    public Task<IActionResult> OnPostSaveAsync(CancellationToken ct) => SaveAsync(sync: true, ct);

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
                // when something the speaker owns actually changed AND the shared save
                // service didn't already mail the field-level changes (operator 2026-07-24
                // — the detailed changes mail supersedes this generic alert). A no-change
                // re-sync of an already-in-Backstage speaker stays silent.
                var r = await _sync.SyncOneAsync(
                    me.EventId, me.ParticipantId,
                    alertOnExisting: result.SyncRelevantChanged && !result.ManualUpdateMailSent, ct: ct);
                // Speaker-facing copy: never names the backend (Zoho/Backstage) or leaks an
                // exception — a speaker only needs to know whether their details reached the
                // public event site.
                if (r.Outcome == SpeakerBioSyncOutcome.BlockedNeedsManualUpdate && !result.SyncRelevantChanged)
                {
                    Message += " You're already on the public event site and nothing changed since your last save.";
                }
                else
                {
                    Message += r.Outcome switch
                    {
                        SpeakerBioSyncOutcome.PushedPublic => " Your details are now live on the public event site.",
                        SpeakerBioSyncOutcome.PushedDraft  => " Your details were sent to the public event site (awaiting the organizers' publish approval).",
                        SpeakerBioSyncOutcome.BlockedNeedsManualUpdate =>
                            " Saved. The organizers have been notified to update your public event-site listing.",
                        SpeakerBioSyncOutcome.RingGated    => " Saved. Publishing to the public event site is held until the organizers enable it for your group.",
                        SpeakerBioSyncOutcome.Disabled     => " Saved.",
                        SpeakerBioSyncOutcome.BuiltOnly    => " Saved.",
                        SpeakerBioSyncOutcome.Failed       => " Saved, but publishing to the public event site didn't go through — the organizers have been notified.",
                        _ => string.Empty,
                    };
                }
                if (r.Outcome == SpeakerBioSyncOutcome.Failed) IsError = true;
            }
            catch { IsError = true; Message += " Saved, but publishing to the public event site didn't go through — the organizers have been notified."; }
        }

        return Page();
    }
}
