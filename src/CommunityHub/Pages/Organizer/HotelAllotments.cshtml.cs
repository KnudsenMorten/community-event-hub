using System.Globalization;
using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// §326bs — the hotel ALLOTMENT board: contracted rooms per night against the rooms
/// actually needed, per hotel, so an organizer can see at a glance where to ask for
/// more rooms and where to release rooms before they stop being refundable.
///
/// <para>Read-only aggregation via <see cref="HotelAllotmentService"/>; the only
/// writes are the explicit allotment / cut-off saves an organizer submits.
/// Organizer-gated server-side on every handler.</para>
/// </summary>
[Authorize]
public class HotelAllotmentsModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly HotelAllotmentService _allotments;

    public HotelAllotmentsModel(
        ICurrentParticipantAccessor participant, HotelAllotmentService allotments)
    {
        _participant = participant;
        _allotments = allotments;
    }

    public bool AccessDenied { get; private set; }
    public string? Message { get; private set; }
    public AllotmentBoard Board { get; private set; } = new();
    public IReadOnlyList<HotelPick> HotelPicks { get; private set; } = Array.Empty<HotelPick>();

    public sealed record HotelPick(int Id, string Name);

    public async Task<IActionResult> OnGetAsync(string? msg, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!me.Role.HasOrganizerAccess()) { AccessDenied = true; return Page(); }

        // §1386 — this board now lives on the ONE hotel page (Hotel overview), via _HotelNightsEditor.
        // The POST handlers below stay (the partial posts to them) and return there.
        await Task.CompletedTask;
        return RedirectToPage("/Organizer/HotelOverview", new { msg });
    }

    /// <summary>
    /// Save one hotel's whole row in a single post. The night keys travel as
    /// <c>nights[i]</c> / <c>rooms[i]</c> pairs so a blank input can mean "not
    /// contracted" (delete the row) rather than 0 — the two are different facts and
    /// the service keeps them apart.
    /// </summary>
    public async Task<IActionResult> OnPostSaveRowAsync(
        int hotelId, string[] nights, string?[] rooms, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var map = new Dictionary<DateOnly, int?>();
        for (var i = 0; i < nights.Length && i < rooms.Length; i++)
        {
            if (!DateOnly.TryParse(nights[i], CultureInfo.InvariantCulture, out var night)) continue;
            var raw = rooms[i];
            if (string.IsNullOrWhiteSpace(raw)) { map[night] = null; continue; }
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                map[night] = n;
        }

        var changed = await _allotments.SetAllotmentsAsync(me.EventId, hotelId, map, ct, me.Email);
        return RedirectToPage("/Organizer/HotelOverview", new { msg = changed == 0
            ? "No changes to save."
            : $"Saved {changed} night(s) for this hotel." });
    }

    /// <summary>Fill a contiguous range with one number — the fast path for a flat block.</summary>
    public async Task<IActionResult> OnPostFillRangeAsync(
        int hotelId, DateOnly fromNight, DateOnly toNight, int rooms, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var changed = await _allotments.SetRangeAsync(me.EventId, hotelId, fromNight, toNight, rooms, ct, me.Email);
        return RedirectToPage("/Organizer/HotelOverview", new { msg = $"Filled {changed} night(s) with {Math.Max(0, rooms)} room(s)." });
    }

    public async Task<IActionResult> OnPostSaveCutoffAsync(
        int hotelId, DateOnly cutoffDate, string label, int? releasePercent, string? notes, int? warnDaysBefore,
        CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var ok = await _allotments.SaveCutoffAsync(
            me.EventId, hotelId, cutoffDate, label ?? string.Empty, releasePercent, notes, ct, warnDaysBefore);
        return RedirectToPage("/Organizer/HotelOverview", new { msg = ok
            ? $"Saved the release deadline for {cutoffDate:d MMM yyyy}."
            : "A release deadline needs a hotel and a label — nothing was saved." });
    }

    /// <summary>§1386 — freeze today's allocation as the ORIGINAL reservation (nights not frozen yet).</summary>
    public async Task<IActionResult> OnPostFreezeOriginalAsync(int hotelId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var n = await _allotments.FreezeOriginalAsync(me.EventId, hotelId, me.Email, ct: ct);
        return RedirectToPage("/Organizer/HotelOverview", new { msg = n == 0
            ? "Every night already has an original reservation recorded."
            : $"Original reservation recorded for {n} night(s)." });
    }

    public async Task<IActionResult> OnPostDeleteCutoffAsync(int cutoffId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var ok = await _allotments.DeleteCutoffAsync(me.EventId, cutoffId, ct);
        return RedirectToPage("/Organizer/HotelOverview", new { msg = ok ? "Release deadline removed." : "That deadline no longer exists." });
    }

    private async Task LoadAsync(int eventId, CancellationToken ct)
    {
        Board = await _allotments.BuildAsync(eventId, ct);
        HotelPicks = Board.Rows
            .Where(r => !r.IsUnassigned)
            .Select(r => new HotelPick(r.HotelId!.Value, r.HotelName))
            .ToList();
    }

    /// <summary>Cell background: red when short, amber when idle rooms are held, plain otherwise.</summary>
    public static string CellStyle(AllotmentCell c)
    {
        if (c.NotContracted) return c.Demand > 0 ? "background:#fee2e2;" : string.Empty;
        if (c.IsShort) return "background:#fee2e2;";
        if (c.HasSlack) return "background:#dcfce7;";   // §1386 — covered is GREEN; the surplus shows as a separate yellow "release N"
        return "background:#dcfce7;";
    }
}
