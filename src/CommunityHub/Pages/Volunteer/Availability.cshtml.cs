using CommunityHub.Auth;
using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations.Sessions;
using CommunityHub.Core.Volunteers;
using CommunityHub.Forms.Steps;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging.Abstractions;

namespace CommunityHub.Pages.Volunteer;

/// <summary>
/// VOLUNTEER "My Availability" (operator 2026-06-21): a volunteer marks, per
/// event day, how much they can work — Full (whole day), Half (split: work part,
/// attend part), Blocked (attending only) or Unavailable (not present). Plus any
/// extra lead-up days configured for volunteers (e.g. the packing day).
/// Coordinators read this when assigning shifts so a volunteer is never scheduled
/// outside their windows. On Save the volunteer's availability is emailed to the
/// volunteer lead (operator 2026-06-23). Self-service only — a volunteer edits
/// their own row via <see cref="ICurrentParticipantAccessor"/>; the client never
/// supplies the id.
///
/// <para>REQUIREMENTS §148: this standalone page is now a thin SHELL — it renders the shared
/// <c>_AvailabilityFields</c> partial and delegates load + persist + ALL side-effects (first
/// submission applies + emails the lead; a later edit enqueues a delta-approval) to
/// <see cref="VolunteerAvailabilityFormService"/>. The SAME service backs the inline wizard
/// step (<c>VolunteerAvailabilityStepHandler</c>), so the standalone page and the wizard
/// behave identically. The page is still deep-linked from My schedule, so its behaviour is
/// unchanged.</para>
/// </summary>
[Authorize]
public class AvailabilityModel : PageModel
{
    /// <summary>The volunteer lead who is notified whenever a volunteer saves their availability.</summary>
    public const string NotifyEmail = "mlh@expertslive.dk";

    private readonly ICurrentParticipantAccessor _participant;
    private readonly CommunityHubDbContext _db;
    private readonly EventEditionConfigLoader _cfg;
    private readonly EventConfigOptions _cfgOptions;
    private readonly IEmailSender _email;
    private readonly SyncDeltaQueueService _queue;
    private readonly VolunteerAvailabilityFormService? _service;

    public AvailabilityModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        EventEditionConfigLoader cfg,
        EventConfigOptions cfgOptions,
        IEmailSender email,
        SyncDeltaQueueService queue,
        ILogger<AvailabilityModel> logger,
        VolunteerAvailabilityFormService? service = null)
    {
        _db = db;
        _participant = participant;
        _cfg = cfg;
        _cfgOptions = cfgOptions;
        _email = email;
        _queue = queue;
        _service = service;
    }

    /// <summary>
    /// The shared submit-service. In production DI injects the registered instance; when the
    /// page is constructed directly (e.g. the web tests pass the legacy 7-arg constructor) it
    /// is built from the page's own dependencies so behaviour is identical.
    /// </summary>
    private VolunteerAvailabilityFormService Service =>
        _service ?? new VolunteerAvailabilityFormService(
            _db, _cfg, _cfgOptions, _email, _queue,
            NullLogger<VolunteerAvailabilityFormService>.Instance);

    /// <summary>
    /// One editable row per event day, pre-filled with any saved value. RawNote is
    /// the full stored Note (incl. any "[slot]" tag — used to re-select the right
    /// option); UserNote is the volunteer's free text only (shown in the textarea).
    /// </summary>
    public record DayRow(DateOnly Day, string Label, VolunteerAvailabilityLevel Level, string? RawNote)
    {
        public string? UserNote => VolunteerDayOptions.StripSlot(RawNote);
    }

    public class DayInput
    {
        public DateOnly Day { get; set; }
        /// <summary>The chosen option's stable slot id (see <see cref="VolunteerDayOptions"/>).</summary>
        public string? Slot { get; set; }
        public string? Note { get; set; }
    }

    /// <summary>The shared render+edit model rendered by the <c>_AvailabilityFields</c> partial
    /// (Days + LastSavedAt). The SAME model the inline wizard step renders.</summary>
    public VolunteerAvailabilityFormModel Form { get; private set; } = new();

    /// <summary>The posted per-day choices — bound with the flat <c>Inputs[i].*</c> keys the
    /// partial emits (identical to the inline wizard step).</summary>
    [BindProperty] public List<DayInput> Inputs { get; set; } = new();

    [TempData] public string? Notice { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        Form = await Service.LoadAsync(me.EventId, me.ParticipantId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        // Delegate persist + all side-effects to the shared service (same flow as the inline
        // wizard step): first submission applies + emails the lead, a later edit enqueues a
        // delta for organizer approval. The service ignores client-injected out-of-edition days.
        Form = new VolunteerAvailabilityFormModel { Inputs = Inputs };
        await Service.SaveAsync(Form, me.EventId, me.ParticipantId, me.FullName, me.Email, ct);

        // PRG: surface the service's flash and reload via OnGet.
        Notice = Form.Notice;
        return RedirectToPage();
    }
}
