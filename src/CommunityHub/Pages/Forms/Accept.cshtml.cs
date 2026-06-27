using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Forms;

/// <summary>
/// "I accept" Get-Started step (REQUIREMENTS §119): a required checkbox linking the
/// Code of Conduct + Privacy Policy. Ticking the box + submitting PERSISTS the
/// acceptance (who/when) as a <see cref="ParticipantPolicyAcceptance"/> row — not a
/// transient tick — so it is auditable. Applies across all roles' Get-Started flows.
/// </summary>
[Authorize]
public class AcceptModel : PageModel
{
    public const string CodeOfConductUrl = "https://expertslive.dk/code-of-conduct/";
    public const string PrivacyPolicyUrl = "https://expertslive.dk/privacy-policy/";

    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly TimeProvider _clock;

    public AcceptModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        TimeProvider clock)
    {
        _db = db;
        _participant = participant;
        _clock = clock;
    }

    [BindProperty] public bool Accept { get; set; }

    public bool AlreadyAccepted { get; private set; }
    public DateTimeOffset? AcceptedAt { get; private set; }
    public string? AcceptedByEmail { get; private set; }
    public string? Message { get; private set; }
    public bool IsError { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        await LoadAsync(me.EventId, me.ParticipantId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        if (!Accept)
        {
            await LoadAsync(me.EventId, me.ParticipantId, ct);
            IsError = true;
            Message = "Please tick \"I accept\" to continue.";
            return Page();
        }

        var existing = await _db.ParticipantPolicyAcceptances.FirstOrDefaultAsync(
            a => a.EventId == me.EventId && a.ParticipantId == me.ParticipantId, ct);
        if (existing is null)
        {
            _db.ParticipantPolicyAcceptances.Add(new ParticipantPolicyAcceptance
            {
                EventId = me.EventId,
                ParticipantId = me.ParticipantId,
                AcceptedByEmail = me.Email,
                AcceptedAt = _clock.GetUtcNow(),
                CodeOfConductUrl = CodeOfConductUrl,
                PrivacyPolicyUrl = PrivacyPolicyUrl,
            });
            await _db.SaveChangesAsync(ct);
        }

        await LoadAsync(me.EventId, me.ParticipantId, ct);
        Message = "Thank you — your acceptance has been recorded.";
        return Page();
    }

    private async Task LoadAsync(int eventId, int participantId, CancellationToken ct)
    {
        var row = await _db.ParticipantPolicyAcceptances.AsNoTracking().FirstOrDefaultAsync(
            a => a.EventId == eventId && a.ParticipantId == participantId, ct);
        if (row is not null)
        {
            AlreadyAccepted = true;
            AcceptedAt = row.AcceptedAt;
            AcceptedByEmail = row.AcceptedByEmail;
            Accept = true;
        }
    }
}
