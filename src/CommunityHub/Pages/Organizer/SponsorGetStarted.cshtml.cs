using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Sponsors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// 🔴 §1210 — WHO HAS NOT FINISHED "GET STARTED", AND WHOSE INBOX TO CHASE.
/// </summary>
/// <remarks>
/// <para>Operator 2026-09-12: <i>"can you give me an overview of all sponsors that have not completed
/// the get started process. i need event sponsor name + coordinator name + email"</i>.</para>
///
/// <para>🔑 The three columns he named ARE the page — company, coordinator, email — because that is a
/// chase list, not a dashboard. Progress and the open steps come along so a 90%-complete sponsor owing
/// one logo is not chased in the same breath as one that has not started.</para>
///
/// <para>🔒 The definition of "completed" is <c>SponsorWizardService</c>'s, shared with the sponsor's
/// own page and §250's digest — never a second rule that could call a finished sponsor unfinished.</para>
/// </remarks>
[Authorize]
public class SponsorGetStartedModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly SponsorGetStartedReport _report;

    public SponsorGetStartedModel(
        ICurrentParticipantAccessor participant, SponsorGetStartedReport report)
    {
        _participant = participant;
        _report = report;
    }

    public bool AccessDenied { get; private set; }

    public IReadOnlyList<SponsorGetStartedRow> Rows { get; private set; } =
        Array.Empty<SponsorGetStartedRow>();

    /// <summary>Companies with no event coordinator at all — nobody to chase (§854).</summary>
    public int NobodyToChase => Rows.Count(r => r.HasNobodyToChase);

    /// <summary>Every coordinator email on the list, de-duplicated — one paste into a mail client.</summary>
    public string AllEmails => string.Join("; ", Rows
        .SelectMany(r => r.Coordinators)
        .Select(c => c.Email)
        .Where(e => !string.IsNullOrWhiteSpace(e))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(e => e, StringComparer.OrdinalIgnoreCase));

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        Rows = await _report.NotCompletedAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>
    /// The step keys in his words. Falls back to the key rather than inventing a title — an unknown
    /// step is a wizard change, and a made-up label would describe the wrong thing (§767).
    /// </summary>
    public static string StepLabel(string key) => key switch
    {
        "welcome" => "Welcome",
        "company" => "Company info",
        "coordinator" => "Event coordinator",
        "contacts" => "Contacts",
        "logos" => "Logos",
        "booth-members" => "Booth members",
        "boothmembers" => "Booth members",
        "booth-materials" => "Booth materials",
        "boothmaterials" => "Booth materials",
        "booth-checkin" => "Booth check-in",
        "boothcheckin" => "Booth check-in",
        "party" => "Party",
        "session" => "Sponsor session",
        "masterclass" => "Master class",
        _ => key,
    };
}
