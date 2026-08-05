using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Graphics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Organizer SoMe-graphics console (REQUIREMENTS §18 step 4, §784.12).
///  - LISTING: every speaker/session graphic that exists and what it is OF. §784.12(a) retired
///    the review gate — artwork is visible to its speaker as soon as it is made — so this page
///    reports what exists rather than gating it.
///  - OVERRULE: replace a graphic with the organizer's own PNG. The stable key / path / link
///    stay identical (replace-in-place) so the hub→SharePoint link never breaks. This is what
///    vetting became: correct it after, not approve it before.
///  - INTERNAL SPONSOR GRAPHICS: listed here for the organizers' own SoMe posts —
///    these are NEVER shown to the sponsor, and they keep the Generated status.
/// Organizer-only. Mobile-first.
/// </summary>
[Authorize]
public class GraphicsModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly GraphicsService _graphics;

    public GraphicsModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        GraphicsService graphics)
    {
        _db = db;
        _participant = participant;
        _graphics = graphics;
    }

    public bool AccessDenied { get; private set; }
    public string? Message { get; private set; }
    public string? Error { get; private set; }

    public IReadOnlyList<Row> ReviewQueue { get; private set; } = Array.Empty<Row>();
    public IReadOnlyList<Row> SponsorGraphics { get; private set; } = Array.Empty<Row>();

    [BindProperty] public int AssetId { get; set; }
    [BindProperty] public IFormFile? Replacement { get; set; }

    public sealed record Row(
        int Id, string Type, string? Subject, string Status,
        bool Overridden, string? Url, bool HasFile);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    // ⚰️ §784.12(a) — OnPostRelease / OnPostUnrelease DELETED WITH THE GATE.
    //
    // Operator 2026-08-03: generated graphics "should be published to speaker right away so they
    // can see them". A graphic is now Released when it is written
    // (GraphicsService.InitialStatusFor), so there is nothing for a Release click to do — and an
    // Unrelease click would hide artwork the speaker has already been mailed about, which is a
    // worse state than the one the gate was protecting against. Vetting is the Replace below:
    // correct it after, keeping the same link. GraphicsService.ReleaseAsync/UnreleaseAsync stay —
    // the bulk backlog sweep and the tests still use that half of the model.

    public async Task<IActionResult> OnPostPullSharePointGraphicsAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var result = await _graphics.PullSessionGraphicsAsync(me.EventId, ct);
        if (result.Matched == 0 && result.Unmatched == 0)
        {
            Message = "SharePoint graphics pull is not configured yet — nothing was pulled. "
                + "Set the SharePoint site + the MasterClass / Sessions folder paths to enable it.";
        }
        else
        {
            Message = $"Pulled session graphics from SharePoint: {result.Matched} matched, "
                + $"{result.Unmatched} session(s) had no matching file. Matched graphics are "
                + "listed below and are already visible to their speakers (§784.12a).";
        }
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostOverruleAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        if (Replacement is null || Replacement.Length == 0)
        {
            Error = "Choose a PNG file to upload as the replacement.";
            await LoadAsync(me.EventId, ct);
            return Page();
        }

        using var ms = new MemoryStream();
        await Replacement.CopyToAsync(ms, ct);
        await _graphics.OverruleAsync(me.EventId, AssetId, ms.ToArray(), ct);
        Message = "Replacement uploaded. The link to the graphic stays the same.";
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    private async Task LoadAsync(int eventId, CancellationToken ct)
    {
        // §784.12(a) — EVERYTHING, not just the un-released. The old call asked for
        // Status == Generated, which auto-release makes a permanently empty set: the page would
        // have quietly shown nothing at all instead of the catalogue he asked for.
        var queue = await _graphics.GetSpeakerFacingGraphicsAsync(eventId, ct);
        var sponsors = await _graphics.GetInternalSponsorGraphicsAsync(eventId, ct);

        // 🔴 §784.12 — EVERY ROW MUST SAY WHAT IT IS OF.
        //
        // Operator 2026-08-03: *"i cannot see all sponsors or sponsor names. i cannot see session
        // names - and speaker is not relevant"*. The queue rendered eleven rows reading
        // "Session · — · Generated" and one "SponsorCategory", with nothing to tell them apart.
        //
        // 🔒 Why every row was blank: the subject was resolved ONLY from `ParticipantId`, and a
        // SESSION graphic carries a `SessionId` instead — so the lookup missed by construction and
        // fell through to null. A Sponsor row showed `SponsorCompanyId`, a raw ERP id, which is why
        // he could not see sponsor NAMES. Track / TrackBundle / SponsorCategory had no case at all.
        //
        // ⚠️ A review queue whose rows cannot be told apart is not a review queue: Release, Replace
        // and Open are all per-row, and the only way to know which session you were acting on was to
        // open every preview in turn.
        var all = queue.Concat(sponsors).ToList();

        var participantIds = all
            .Where(g => g.ParticipantId is not null).Select(g => g.ParticipantId!.Value).Distinct().ToList();
        var names = await _db.Participants
            .Where(p => participantIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.FullName, ct);

        var sessionIds = all
            .Where(g => g.SessionId is not null).Select(g => g.SessionId!.Value).Distinct().ToList();
        var sessionTitles = await _db.Sessions
            .Where(s => sessionIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.Title, ct);

        var companyIds = all
            .Where(g => !string.IsNullOrWhiteSpace(g.SponsorCompanyId))
            .Select(g => g.SponsorCompanyId!).Distinct().ToList();
        var companyNames = await _db.SponsorInfos
            .Where(s => s.SponsorCompanyId != null && companyIds.Contains(s.SponsorCompanyId))
            .Where(s => s.CompanyName != null && s.CompanyName != "")
            .Select(s => new { s.SponsorCompanyId, s.CompanyName })
            .ToDictionaryAsync(s => s.SponsorCompanyId!, s => s.CompanyName!, ct);

        Row Map(GraphicAsset g)
        {
            var subject = g.Type switch
            {
                // The NAME, falling back to the id only when the company is not mirrored yet — an
                // id is still better than a blank row, and it says which record to go and look at.
                GraphicAssetType.Sponsor =>
                    g.SponsorCompanyId is { Length: > 0 } cid
                        ? (companyNames.TryGetValue(cid, out var cn) ? cn : cid)
                        : null,

                GraphicAssetType.Session or GraphicAssetType.Track =>
                    g.SessionId is int sid && sessionTitles.TryGetValue(sid, out var t) ? t
                    : g.ParticipantId is int spid && names.TryGetValue(spid, out var sn) ? sn
                    : null,

                _ => g.ParticipantId is int pid && names.TryGetValue(pid, out var n) ? n : null,
            };

            // 🔑 Last resort: the StableKey. A grouping graphic (TrackBundle, SponsorCategory) has
            // neither a participant nor a session — its identity IS its key ("the platinum tier",
            // "the Security track"). Showing that beats showing a dash.
            subject ??= string.IsNullOrWhiteSpace(g.StableKey) ? null : g.StableKey;

            return new Row(g.Id, g.Type.ToString(), subject, g.Status.ToString(),
                g.IsOrganizerOverridden, g.SharePointUrl, !string.IsNullOrEmpty(g.SharePointUrl));
        }

        ReviewQueue = queue.Select(Map).ToList();   // already sponsor-free by construction
        SponsorGraphics = sponsors.Select(Map).ToList();
    }
}
