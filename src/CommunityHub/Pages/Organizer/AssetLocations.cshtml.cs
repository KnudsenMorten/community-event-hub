using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Graphics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Organizer admin settings place (REQUIREMENTS §18 step 1) to configure, PER
/// PERSONA GROUP (volunteers / speakers / media / organizers), the SharePoint link
/// where that group's bio details / files are stored. Stores only the pointers
/// (site / drive / root folder / browse URL) — never SPN credentials. Real links
/// are operator-entered. Organizer-only. Mobile-first.
/// </summary>
[Authorize]
public class AssetLocationsModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly AssetLocationService _locations;

    public AssetLocationsModel(
        ICurrentParticipantAccessor participant, AssetLocationService locations)
    {
        _participant = participant;
        _locations = locations;
    }

    public bool AccessDenied { get; private set; }
    public string? Message { get; private set; }

    public IReadOnlyList<Row> Rows { get; private set; } = Array.Empty<Row>();

    [BindProperty] public AssetPersonaGroup Group { get; set; }
    [BindProperty] public string? SiteUrl { get; set; }
    [BindProperty] public string? DriveName { get; set; }
    [BindProperty] public string? RootFolderPath { get; set; }
    [BindProperty] public string? BrowseUrl { get; set; }
    [BindProperty] public string? Notes { get; set; }

    public sealed record Row(
        AssetPersonaGroup Group, string GroupName,
        string? SiteUrl, string? DriveName, string? RootFolderPath, string? BrowseUrl, string? Notes);

    public static readonly AssetPersonaGroup[] AllGroups =
        Enum.GetValues<AssetPersonaGroup>();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        await _locations.UpsertAsync(
            me.EventId, Group, SiteUrl, DriveName, RootFolderPath, BrowseUrl, Notes, me.Email, ct);
        Message = $"Saved the {Group} location.";
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    private async Task LoadAsync(int eventId, CancellationToken ct)
    {
        var rows = await _locations.ListAsync(eventId, ct);
        var byGroup = rows.ToDictionary(r => r.PersonaGroup);

        Rows = AllGroups.Select(g =>
            byGroup.TryGetValue(g, out var r)
                ? new Row(g, g.ToString(), r.SiteUrl, r.DriveName, r.RootFolderPath, r.BrowseUrl, r.Notes)
                : new Row(g, g.ToString(), null, null, null, null, null))
            .ToList();
    }
}
