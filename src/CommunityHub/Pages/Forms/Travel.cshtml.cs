using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Resources;
using CommunityHub.Forms;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace CommunityHub.Pages.Forms;

[Authorize]
public class TravelModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly TimeProvider _clock;
    private readonly IStringLocalizer<SharedResource> _loc;

    public TravelModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        TimeProvider clock,
        IStringLocalizer<SharedResource> loc)
    {
        _db = db;
        _participant = participant;
        _clock = clock;
        _loc = loc;
    }

    public static readonly ParticipantRole[] EligibleRoles =
    {
        ParticipantRole.Speaker,
    };

    /// <summary>
    /// FEATURE B: travel reimbursement is gated by ENTITLEMENT
    /// (<see cref="OrderItem.TravelReimbursement"/>), not role alone. Only a
    /// Supported speaker is entitled — a sponsor-self-funded / organizer-funded
    /// speaker is NOT, so they are denied the form even though their role is
    /// Speaker. The historical role set was speaker-only, so there is no
    /// non-speaker access to preserve here.
    /// </summary>
    private Task<bool> IsEligibleAsync(CurrentParticipant me, CancellationToken ct) =>
        FormEntitlementGate.IsEntitledAsync(
            _db, me.EventId, me.ParticipantId, OrderItem.TravelReimbursement, ct);

    public const decimal Cap300 = 300m;
    public const decimal Cap400 = 400m;

    /// <summary>Choice token submitted from the dropdown.</summary>
    public const string Choice300   = "300";
    public const string Choice400   = "400";
    public const string ChoiceOther = "other";
    public const string ChoiceNone  = "";   // "I'm not claiming reimbursement"

    public const string SubmitInvoiceTaskKey = "travel:submit-ticket-invoice";

    [BindProperty] public bool   RequestReimbursement { get; set; }
    [BindProperty] public string? OriginCity { get; set; }
    /// <summary>One of: "", "300", "400", "other".</summary>
    [BindProperty] public string? AmountChoice { get; set; }
    /// <summary>Only used when AmountChoice == "other".</summary>
    [BindProperty] public decimal? OtherAmountEur { get; set; }
    [BindProperty] public string? Explanation { get; set; }

    public string FullName { get; private set; } = string.Empty;
    public string Email { get; private set; } = string.Empty;
    public ParticipantRole Role { get; private set; }
    public bool AccessDenied { get; private set; }
    public string? Message { get; private set; }
    public DateOnly? SubmitInvoiceDueDate { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        FullName = me.FullName;
        Email = me.Email;
        Role = me.Role;
        if (!await IsEligibleAsync(me, ct))
        {
            AccessDenied = true;
            return Page();
        }

        SubmitInvoiceDueDate = await GetInvoiceDueDateAsync(me.EventId, ct);

        var existing = await _db.TravelReimbursements.FirstOrDefaultAsync(
            t => t.EventId == me.EventId && t.ParticipantId == me.ParticipantId, ct);
        if (existing is not null)
        {
            RequestReimbursement = existing.RequestReimbursement;
            OriginCity = existing.OriginCity;
            Explanation = existing.Explanation;

            (AmountChoice, OtherAmountEur) = DecomposeAmount(existing.ClaimAmountEur);
        }
        else
        {
            // Default = "No, not claiming" so the form is opt-in.
            RequestReimbursement = false;
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
        if (!await IsEligibleAsync(me, ct))
        {
            AccessDenied = true;
            return Page();
        }

        SubmitInvoiceDueDate = await GetInvoiceDueDateAsync(me.EventId, ct);

        // ---- Field-level validation (REQUIREMENTS §21 shared validation pattern).
        // Only meaningful when the user IS claiming; the opt-out path needs none.
        // This closes the known "Other"-blank bug: previously selecting "Other"
        // with an empty amount silently saved a NULL claim (no error shown). Now
        // it fails validation against the specific field, re-renders the form with
        // an inline message, and persists nothing.
        if (RequestReimbursement)
        {
            if (string.IsNullOrEmpty(AmountChoice))
            {
                ModelState.AddModelError(nameof(AmountChoice), _loc["Travel.ErrPickAmount"]);
            }
            else if (AmountChoice == ChoiceOther)
            {
                if (OtherAmountEur is not > 0)
                {
                    ModelState.AddModelError(nameof(OtherAmountEur), _loc["Travel.ErrOtherAmount"]);
                }
                if (string.IsNullOrWhiteSpace(Explanation))
                {
                    ModelState.AddModelError(nameof(Explanation), _loc["Travel.ErrOtherExplanation"]);
                }
            }
        }

        if (!ModelState.IsValid)
        {
            // Re-render with the field errors; do not touch the database.
            return Page();
        }

        var row = await _db.TravelReimbursements.FirstOrDefaultAsync(
            t => t.EventId == me.EventId && t.ParticipantId == me.ParticipantId, ct);

        if (row is null)
        {
            row = new TravelReimbursement
            {
                EventId = me.EventId,
                ParticipantId = me.ParticipantId,
                CreatedAt = _clock.GetUtcNow(),
            };
            _db.TravelReimbursements.Add(row);
        }
        else
        {
            row.UpdatedAt = _clock.GetUtcNow();
        }

        row.RequestReimbursement = RequestReimbursement;
        row.OriginCity = string.IsNullOrWhiteSpace(OriginCity) ? null : OriginCity.Trim();

        if (RequestReimbursement)
        {
            row.ClaimAmountEur = ComposeAmount(AmountChoice, OtherAmountEur);
            row.Explanation = string.IsNullOrWhiteSpace(Explanation) ? null : Explanation.Trim();
        }
        else
        {
            // Not claiming: clear the claim fields so the row reflects opt-out.
            row.ClaimAmountEur = null;
            row.Explanation = null;
        }

        await _db.SaveChangesAsync(ct);

        await SyncSubmitInvoiceTaskAsync(me.EventId, me.ParticipantId,
            wantsTask: RequestReimbursement, ct);

        Message = RequestReimbursement
            ? "Your travel reimbursement request has been saved. " +
              "A 'Submit flight ticket + invoice' task has been added to your hub."
            : "Saved. You are not claiming reimbursement; any submit-ticket+invoice task has been removed.";
        return Page();
    }

    private static (string?, decimal?) DecomposeAmount(decimal? amount)
    {
        if (amount is null) return (ChoiceNone, null);
        if (amount == Cap300) return (Choice300, null);
        if (amount == Cap400) return (Choice400, null);
        return (ChoiceOther, amount);
    }

    private static decimal? ComposeAmount(string? choice, decimal? other) => choice switch
    {
        Choice300 => Cap300,
        Choice400 => Cap400,
        ChoiceOther => other is > 0 ? other : null,
        _ => null,
    };

    private async Task<DateOnly?> GetInvoiceDueDateAsync(int eventId, CancellationToken ct)
    {
        var startDate = await _db.Events
            .Where(e => e.Id == eventId)
            .Select(e => (DateOnly?)e.StartDate)
            .FirstOrDefaultAsync(ct);
        return startDate?.AddDays(-30);
    }

    private async Task SyncSubmitInvoiceTaskAsync(
        int eventId, int participantId, bool wantsTask, CancellationToken ct)
    {
        var sourceKey = $"{SubmitInvoiceTaskKey}:{participantId}";
        var existing = await _db.Tasks.FirstOrDefaultAsync(
            t => t.EventId == eventId
                 && t.AssignedParticipantId == participantId
                 && t.SourceKey == sourceKey, ct);

        if (wantsTask)
        {
            if (existing is null)
            {
                var due = await GetInvoiceDueDateAsync(eventId, ct);
                _db.Tasks.Add(new ParticipantTask
                {
                    EventId = eventId,
                    AssignedParticipantId = participantId,
                    Title = "Submit flight ticket + invoice for reimbursement",
                    Description =
                        $"Send your economy flight ticket + invoice to ELDK by {due:dd/MM/yyyy} " +
                        "(30 days before the event).",
                    DueDate = due,
                    State = TaskState.Open,
                    SourceKey = sourceKey,
                    CreatedAt = _clock.GetUtcNow(),
                });
                await _db.SaveChangesAsync(ct);
            }
        }
        else if (existing is not null && existing.State != TaskState.Done)
        {
            _db.Tasks.Remove(existing);
            await _db.SaveChangesAsync(ct);
        }
    }
}
