using CommunityHub.Auth;
using CommunityHub.Core.Domain.Evaluation;
using CommunityHub.Core.Evaluation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// §747 C8 — issue and revoke the service credentials external systems use to pull session reports.
/// </summary>
/// <remarks>
/// <para>🔒 <b>A key is shown ONCE and cannot be retrieved</b> — only a PBKDF2 hash is stored, so
/// nobody, including us, can read one back. Losing it means rotating it. The page says so rather
/// than letting it be discovered, exactly as the device page does.</para>
///
/// <para>⚠️ <b>These credentials open verbatim attendee comments</b>, which the brief calls the most
/// sensitive data in the system. Issue one per consumer with a name that says who holds it, so a
/// revocation decision is possible later without guesswork.</para>
/// </remarks>
[Authorize]
public class EvaluationApiClientsModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly EvaluationApiClientService _clients;

    public EvaluationApiClientsModel(
        ICurrentParticipantAccessor participant, EvaluationApiClientService clients)
    {
        _participant = participant;
        _clients = clients;
    }

    public bool AccessDenied { get; private set; }
    public string? Message { get; private set; }

    /// <summary>Issued by THIS request — the only time a key is ever visible.</summary>
    public (string Name, string Key)? IssuedKey { get; private set; }

    public IReadOnlyList<EvaluationApiClient> Clients { get; private set; }
        = Array.Empty<EvaluationApiClient>();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        Clients = await _clients.ListAsync(me.EventId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostIssueAsync(string? name, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        if (string.IsNullOrWhiteSpace(name))
        {
            Message = "Give the credential a name that says who will hold it.";
            Clients = await _clients.ListAsync(me.EventId, ct);
            return Page();
        }

        var existing = await _clients.ListAsync(me.EventId, ct);
        if (existing.Any(c => string.Equals(c.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            // The unique index would throw; saying it plainly is better than a 500, and the name is
            // how a human later decides which credential to revoke.
            Message = $"A credential named \"{name.Trim()}\" already exists. Rotate it instead, or pick another name.";
            Clients = existing;
            return Page();
        }

        var (client, key) = await _clients.IssueAsync(me.EventId, name, ct);
        IssuedKey = (client.Name, key);
        Message = "Copy the key below now — it cannot be shown again.";

        Clients = await _clients.ListAsync(me.EventId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostRotateAsync(int id, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var key = await _clients.RotateAsync(me.EventId, id, ct);
        Clients = await _clients.ListAsync(me.EventId, ct);

        if (key is null) { Message = "No such credential."; return Page(); }

        var rotated = Clients.FirstOrDefault(c => c.Id == id);
        IssuedKey = (rotated?.Name ?? "credential", key);
        Message = "Rotated. The OLD key still works until you retire it — switch the consumer over first.";
        return Page();
    }

    public async Task<IActionResult> OnPostRetirePreviousAsync(int id, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        Message = await _clients.RetirePreviousAsync(me.EventId, id, ct)
            ? "Previous key retired — only the current key works now."
            : "No such credential.";

        Clients = await _clients.ListAsync(me.EventId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostSetActiveAsync(int id, bool active, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        Message = await _clients.SetActiveAsync(me.EventId, id, active, ct)
            ? (active ? "Credential restored." : "Credential revoked — every request presenting it is now refused.")
            : "No such credential.";

        Clients = await _clients.ListAsync(me.EventId, ct);
        return Page();
    }
}
