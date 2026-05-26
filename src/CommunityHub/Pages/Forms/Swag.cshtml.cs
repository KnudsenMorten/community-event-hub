using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Forms;

[Authorize]
public class SwagModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly TimeProvider _clock;

    public SwagModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        TimeProvider clock)
    {
        _db = db;
        _participant = participant;
        _clock = clock;
    }

    /// <summary>SourceKey prefix used for the "complete the swag form" task.</summary>
    public const string SwagTaskKey = "swag-form";

    /// <summary>Roles eligible to fill out the swag form.</summary>
    public static readonly ParticipantRole[] EligibleRoles =
    {
        ParticipantRole.Volunteer,
        ParticipantRole.Speaker,
        ParticipantRole.MasterclassSpeaker,
        ParticipantRole.Organizer,
    };

    /// <summary>17 polo size options. The last option means "no polo".</summary>
    public const string NoPoloLabel = "I wear my own clothes";
    public static readonly string[] PoloSizes =
    {
        "XS (men)", "S (men)", "M (men)", "L (men)",
        "XL (men)", "XXL (men)", "3XL (men)", "4XL (men)",
        "XS (women)", "S (women)", "M (women)", "L (women)",
        "XL (women)", "XXL (women)", "3XL (women)", "4XL (women)",
        NoPoloLabel,
    };

    /// <summary>Same size set is reused for the jacket. Same "no jacket" sentinel.</summary>
    public const string NoJacketLabel = "I don't want a jacket";
    public static readonly string[] JacketSizes =
    {
        "XS (men)", "S (men)", "M (men)", "L (men)",
        "XL (men)", "XXL (men)", "3XL (men)", "4XL (men)",
        "XS (women)", "S (women)", "M (women)", "L (women)",
        "XL (women)", "XXL (women)", "3XL (women)", "4XL (women)",
        NoJacketLabel,
    };

    [BindProperty] public string? PoloChoice { get; set; }
    [BindProperty] public string? JacketChoice { get; set; }
    [BindProperty] public bool WantsGift { get; set; } = true;
    [BindProperty] public bool WantsCredlyBadge { get; set; } = true;
    [BindProperty] public string? Notes { get; set; }

    public string FullName { get; private set; } = string.Empty;
    public string Email { get; private set; } = string.Empty;
    public ParticipantRole Role { get; private set; }
    public bool AccessDenied { get; private set; }
    public string? Message { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        FullName = me.FullName;
        Email = me.Email;
        Role = me.Role;
        if (!EligibleRoles.Contains(me.Role))
        {
            AccessDenied = true;
            return Page();
        }

        await EnsureSwagTaskExistsAsync(me.EventId, me.ParticipantId, ct);

        var existing = await _db.SwagPreferences.FirstOrDefaultAsync(
            s => s.EventId == me.EventId && s.ParticipantId == me.ParticipantId, ct);
        if (existing is not null)
        {
            PoloChoice = existing.WantsPolo ? existing.PoloSize : NoPoloLabel;
            JacketChoice = existing.WantsJacket ? existing.JacketSize : NoJacketLabel;
            WantsGift = existing.WantsGift;
            WantsCredlyBadge = existing.WantsCredlyBadge;
            Notes = existing.Notes;
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        FullName = me.FullName;
        Email = me.Email;
        Role = me.Role;
        if (!EligibleRoles.Contains(me.Role))
        {
            AccessDenied = true;
            return Page();
        }

        var pref = await _db.SwagPreferences.FirstOrDefaultAsync(
            s => s.EventId == me.EventId && s.ParticipantId == me.ParticipantId, ct);

        if (pref is null)
        {
            pref = new SwagPreference
            {
                EventId = me.EventId,
                ParticipantId = me.ParticipantId,
                CreatedAt = _clock.GetUtcNow(),
            };
            _db.SwagPreferences.Add(pref);
        }
        else
        {
            pref.UpdatedAt = _clock.GetUtcNow();
        }

        if (string.IsNullOrWhiteSpace(PoloChoice) || PoloChoice == NoPoloLabel)
        {
            pref.WantsPolo = false;
            pref.PoloSize = null;
        }
        else
        {
            pref.WantsPolo = true;
            pref.PoloSize = PoloSizes.Contains(PoloChoice) ? PoloChoice : null;
        }

        if (string.IsNullOrWhiteSpace(JacketChoice) || JacketChoice == NoJacketLabel)
        {
            pref.WantsJacket = false;
            pref.JacketSize = null;
        }
        else
        {
            pref.WantsJacket = true;
            pref.JacketSize = JacketSizes.Contains(JacketChoice) ? JacketChoice : null;
        }

        pref.WantsGift = WantsGift;
        pref.WantsCredlyBadge = WantsCredlyBadge;
        pref.Notes = Notes;

        await _db.SaveChangesAsync(ct);
        await MarkSwagTaskDoneAsync(me.EventId, me.ParticipantId, ct);
        Message = "Your swag preferences have been saved.";
        return Page();
    }

    private async Task EnsureSwagTaskExistsAsync(
        int eventId, int participantId, CancellationToken ct)
    {
        var sourceKey = $"{SwagTaskKey}:{participantId}";
        var exists = await _db.Tasks.AnyAsync(
            t => t.EventId == eventId
                 && t.AssignedParticipantId == participantId
                 && t.SourceKey == sourceKey, ct);
        if (exists) return;

        var due = await _db.Events
            .Where(e => e.Id == eventId)
            .Select(e => (DateOnly?)e.StartDate.AddDays(-21))
            .FirstOrDefaultAsync(ct);

        _db.Tasks.Add(new ParticipantTask
        {
            EventId = eventId,
            AssignedParticipantId = participantId,
            Title = "Complete the Swag preferences form",
            Description = "Pick your polo size (or 'I wear my own clothes'). " +
                          "Saving the form marks this task Done.",
            DueDate = due,
            State = TaskState.Open,
            SourceKey = sourceKey,
            CreatedAt = _clock.GetUtcNow(),
        });
        await _db.SaveChangesAsync(ct);
    }

    private async Task MarkSwagTaskDoneAsync(
        int eventId, int participantId, CancellationToken ct)
    {
        var sourceKey = $"{SwagTaskKey}:{participantId}";
        var task = await _db.Tasks.FirstOrDefaultAsync(
            t => t.EventId == eventId
                 && t.AssignedParticipantId == participantId
                 && t.SourceKey == sourceKey, ct);
        if (task is null || task.State == TaskState.Done) return;
        task.State = TaskState.Done;
        await _db.SaveChangesAsync(ct);
    }
}
