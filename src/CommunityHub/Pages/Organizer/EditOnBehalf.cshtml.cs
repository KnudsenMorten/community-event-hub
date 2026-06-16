using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// "Modify on behalf" — an organizer changes a couple of per-person logistics
/// fields FOR a participant (hotel needed / not-needed, swag polo size). The
/// writes land on the SAME <see cref="HotelBooking"/> / <see cref="SwagPreference"/>
/// rows the participant's own pages read, so the change shows up on that person's
/// own view immediately. Each change is audited and (if late) raises the
/// existing organizer action queue. Organizer-only, server-enforced.
/// </summary>
[Authorize]
public class EditOnBehalfModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly ModifyOnBehalfService _modify;
    private readonly ImpersonationAuditService _audit;

    public EditOnBehalfModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        ModifyOnBehalfService modify,
        ImpersonationAuditService audit)
    {
        _db = db;
        _participant = participant;
        _modify = modify;
        _audit = audit;
    }

    [BindProperty(SupportsGet = true)]
    public int ParticipantId { get; set; }

    public string TargetName { get; private set; } = string.Empty;
    public string TargetEmail { get; private set; } = string.Empty;
    public bool AccessDenied { get; private set; }
    public bool NotFound_ { get; private set; }
    public string? Message { get; private set; }
    public bool IsError { get; private set; }

    // Current values (for the form).
    [BindProperty] public bool NeedsRoom { get; set; }
    [BindProperty] public string? PoloChoice { get; set; }

    public string[] PoloSizes => SwagOptions.PoloSizes;
    public string NoPoloLabel => SwagOptions.NoPoloLabel;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        return await LoadAsync(me.EventId, ct);
    }

    public async Task<IActionResult> OnPostHotelAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var (result, summary) = await _modify.SetHotelNeededAsync(
            me.EventId, ParticipantId, NeedsRoom, ct);
        if (result == ModifyOnBehalfService.ModifyResult.NotFound)
        {
            NotFound_ = true;
            return Page();
        }

        await _audit.RecordAsync(
            me.EventId, ImpersonationActorKind.Organizer,
            actorParticipantId: me.ParticipantId, actorLabel: $"{me.FullName} ({me.Email})",
            targetParticipantId: ParticipantId,
            action: ImpersonationAuditService.ActionModifyHotel, detail: summary, ct: ct);

        Message = $"{summary}. The change is now on the participant's own view.";
        return await LoadAsync(me.EventId, ct);
    }

    public async Task<IActionResult> OnPostSwagAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var (result, summary) = await _modify.SetPoloSizeAsync(
            me.EventId, ParticipantId, PoloChoice, SwagOptions.PoloSizes, SwagOptions.NoPoloLabel, ct);
        if (result == ModifyOnBehalfService.ModifyResult.NotFound)
        {
            // Could be a bad participant id OR an unknown size; reload + warn.
            var exists = await _db.Participants.AnyAsync(
                p => p.Id == ParticipantId && p.EventId == me.EventId, ct);
            if (!exists) { NotFound_ = true; return Page(); }
            IsError = true;
            Message = "That polo size is not one of the offered options.";
            return await LoadAsync(me.EventId, ct);
        }

        await _audit.RecordAsync(
            me.EventId, ImpersonationActorKind.Organizer,
            actorParticipantId: me.ParticipantId, actorLabel: $"{me.FullName} ({me.Email})",
            targetParticipantId: ParticipantId,
            action: ImpersonationAuditService.ActionModifySwag, detail: summary, ct: ct);

        Message = $"{summary}. The change is now on the participant's own view.";
        return await LoadAsync(me.EventId, ct);
    }

    private async Task<IActionResult> LoadAsync(int eventId, CancellationToken ct)
    {
        var target = await _db.Participants.FirstOrDefaultAsync(
            p => p.Id == ParticipantId && p.EventId == eventId, ct);
        if (target is null) { NotFound_ = true; return Page(); }

        TargetName = target.FullName;
        TargetEmail = target.Email;

        var booking = await _db.HotelBookings.FirstOrDefaultAsync(
            h => h.EventId == eventId && h.ParticipantId == ParticipantId, ct);
        NeedsRoom = booking?.NeedsRoom ?? false;

        var swag = await _db.SwagPreferences.FirstOrDefaultAsync(
            s => s.EventId == eventId && s.ParticipantId == ParticipantId, ct);
        PoloChoice = swag is { WantsPolo: true } ? swag.PoloSize : SwagOptions.NoPoloLabel;

        return Page();
    }

    private static bool IsRealOrganizer(CurrentParticipant me) =>
        me.Role == ParticipantRole.Organizer && !me.IsActingAs;
}
