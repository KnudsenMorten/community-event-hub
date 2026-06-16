using System.Text;
using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Export;
using CommunityHub.Core.Reminders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// The organizer Action Queue: the drain for late participant changes that need
/// a human to re-confirm with a downstream vendor (hotel, dinner caterer, ...).
/// Self-service form handlers raise items via
/// <see cref="OrganizerActionItemService.RaiseIfLateAsync"/>; here an organizer
/// reviews them, marks them resolved with a note, re-opens if needed, and can
/// export the open queue as CSV. Organizer-only.
/// </summary>
[Authorize]
public class ActionQueueModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly OrganizerActionItemService _actions;
    private readonly CommunityHub.Core.Email.OnboardingStepResetEmailService _stepResetEmails;

    public ActionQueueModel(
        ICurrentParticipantAccessor participant,
        OrganizerActionItemService actions,
        CommunityHub.Core.Email.OnboardingStepResetEmailService stepResetEmails)
    {
        _participant = participant;
        _actions = actions;
        _stepResetEmails = stepResetEmails;
    }

    public bool AccessDenied { get; private set; }
    public string? Message { get; private set; }

    /// <summary>Optional type filter (e.g. "hotel-changed"); empty = all types.</summary>
    [BindProperty(SupportsGet = true)]
    public string TypeFilter { get; set; } = string.Empty;

    public IReadOnlyList<Row> OpenItems { get; private set; } = Array.Empty<Row>();
    public IReadOnlyList<Row> ResolvedItems { get; private set; } = Array.Empty<Row>();
    public IReadOnlyList<(string Code, string Label, int Count)> TypeCounts { get; private set; }
        = Array.Empty<(string, string, int)>();

    public sealed record Row(
        int Id, string Type, string TypeLabel, string Who, string? Email,
        string Summary, DateTimeOffset LastActivity,
        DateTimeOffset? ResolvedAt, string? ResolvedNotes);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostResolveAsync(
        int id, string? notes, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        var ok = await _actions.ResolveAsync(me.EventId, id, notes, ct);
        Message = ok ? "Marked resolved." : "That item was not found (or already resolved).";
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostReopenAsync(int id, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        var ok = await _actions.ReopenAsync(me.EventId, id, ct);
        Message = ok ? "Re-opened." : "That item was not found (or already open).";
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>Consume the open onboarding-step-reset items now (10a-6): email
    /// each affected person pointing them at the wizard, then resolve the item.
    /// The daily ReminderJob does this automatically; this is the "send now"
    /// button so an organizer need not wait for the nightly run.</summary>
    public async Task<IActionResult> OnPostSendStepResetsAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        var sent = await _stepResetEmails.SendPendingAsync(me.EventId, ct);
        Message = sent == 0
            ? "No open onboarding-step-reset reminders to send."
            : $"Sent {sent} onboarding-step-reset reminder(s).";
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    public async Task<IActionResult> OnGetExportAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) return Forbid();

        var open = await _actions.GetOpenAsync(
            me.EventId, string.IsNullOrWhiteSpace(TypeFilter) ? null : TypeFilter, ct);

        var header = new[] { "Type", "Participant", "Email", "What changed", "Last activity" };
        var rows = open.Select(a => (IReadOnlyList<string>)new[]
        {
            OrganizerActionItemService.LabelFor(a.Type),
            a.Participant?.FullName ?? "(unknown)",
            a.Participant?.Email ?? string.Empty,
            a.Summary,
            (a.UpdatedAt ?? a.CreatedAt).ToString("yyyy-MM-dd HH:mm"),
        });

        var csv = CsvWriter.Write(header, rows);
        return File(Encoding.UTF8.GetBytes(csv), "text/csv", "action-queue.csv");
    }

    private async Task LoadAsync(int eventId, CancellationToken ct)
    {
        var typeArg = string.IsNullOrWhiteSpace(TypeFilter) ? null : TypeFilter;

        var open = await _actions.GetOpenAsync(eventId, typeArg, ct);
        OpenItems = open.Select(ToRow).ToList();

        var resolved = await _actions.GetResolvedAsync(eventId, take: 25, ct: ct);
        ResolvedItems = resolved.Select(ToRow).ToList();

        // Per-type open counts for the filter chips (ignores current filter so
        // the organizer always sees the full breakdown).
        var allOpen = await _actions.GetOpenAsync(eventId, type: null, ct: ct);
        TypeCounts = allOpen
            .GroupBy(a => a.Type)
            .Select(g => (g.Key, OrganizerActionItemService.LabelFor(g.Key), g.Count()))
            .OrderByDescending(x => x.Item3)
            .ThenBy(x => x.Item2)
            .ToList();
    }

    private static Row ToRow(OrganizerActionItem a) => new(
        a.Id,
        a.Type,
        OrganizerActionItemService.LabelFor(a.Type),
        a.Participant?.FullName ?? "(unknown participant)",
        a.Participant?.Email,
        a.Summary,
        a.UpdatedAt ?? a.CreatedAt,
        a.ResolvedAt,
        a.ResolvedNotes);
}
