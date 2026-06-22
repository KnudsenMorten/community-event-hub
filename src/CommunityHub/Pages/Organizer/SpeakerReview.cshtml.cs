using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Entitlements;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Post-Sessionize-import <b>"Speaker &amp; order review"</b> organizer page. It is
/// the UI consumer of the shipped order-entitlement model: the organizer classifies
/// every speaker (funding, days, cross-email dedup, per-item overrides) and verifies
/// the deduped "what to order" counts the logistics team buys against.
///
/// <para>
/// Three sections: (1) the per-<see cref="OrderItem"/> deduped counts
/// (<see cref="OrderCountService"/>); (2) a per-speaker review list with live
/// effective-entitlement chips and per-row editable controls (each its own POST
/// handler, redirect-after-post); (3) a compact sponsor booth-member toggle list.
/// </para>
///
/// <para>
/// Auth: read (GET) is gated on a signed-in <see cref="ParticipantRole.Organizer"/>;
/// every write is gated on <see cref="OrganizerAuth.IsRealOrganizer"/> (a genuine
/// organizer, not an acting-as session). Everything is scoped to the caller's
/// edition (<c>me.EventId</c>).
/// </para>
/// </summary>
[Authorize]
public class SpeakerReviewModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly OrderCountService _counts;
    private readonly TimeProvider _clock;

    public SpeakerReviewModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        OrderCountService counts,
        TimeProvider clock)
    {
        _db = db;
        _participant = participant;
        _counts = counts;
        _clock = clock;
    }

    /// <summary>True when the caller is not an organizer — render a friendly notice, not the content.</summary>
    public bool AccessDenied { get; private set; }

    /// <summary>Set when the data layer fails — an honest banner instead of an unhandled 500.</summary>
    public string? Error { get; private set; }

    /// <summary>Status line shown after a redirect-after-post.</summary>
    public string? Notice { get; private set; }

    [BindProperty(SupportsGet = true)] public string? Msg { get; set; }

    /// <summary>Deduped per-item count for the edition's "what to order" summary.</summary>
    public Dictionary<OrderItem, int> Counts { get; private set; } = new();

    /// <summary>One review row per participant who HAS a speaker profile.</summary>
    public List<SpeakerRow> Speakers { get; private set; } = new();

    /// <summary>The "same person as" candidate list (all primary rows in the edition).</summary>
    public List<PersonOption> LinkTargets { get; private set; } = new();

    /// <summary>Compact sponsor booth-member section.</summary>
    public List<BoothRow> BoothMembers { get; private set; } = new();

    /// <summary>The full <see cref="OrderItem"/> set, in declared order, for the UI.</summary>
    public static IReadOnlyList<OrderItem> AllItems { get; } = Enum.GetValues<OrderItem>();

    /// <summary>One speaker review row (a participant + their speaker profile + effective items).</summary>
    public sealed record SpeakerRow(
        int ParticipantId,
        string FullName,
        string Email,
        ParticipantRole Role,
        SpeakerFunding Funding,
        bool SpeakingPreDay,
        bool SpeakingMainDay,
        int? SamePersonAsId,
        string? SamePersonAsLabel,
        IReadOnlySet<OrderItem> Effective,
        IReadOnlyDictionary<OrderItem, bool> Overrides);

    /// <summary>A candidate primary participant for the "same person as" select.</summary>
    public sealed record PersonOption(int Id, string FullName, string Email, ParticipantRole Role);

    /// <summary>One sponsor booth-member row (toggle + read-only package display).</summary>
    public sealed record BoothRow(
        int ParticipantId,
        string FullName,
        string Email,
        bool IsBoothMember,
        SponsorPackage? Package,
        bool? HasBooth);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        Notice = Msg;
        try
        {
            await LoadAsync(me.EventId, ct);
        }
        catch (Exception ex)
        {
            // Degrade to an honest banner on a 200 page rather than crash (500).
            Error = "The speaker review data could not be loaded right now.";
            System.Diagnostics.Debug.WriteLine(ex);
        }
        return Page();
    }

    // --- Section 1 funding ---------------------------------------------------
    /// <summary>Set a speaker's <see cref="SpeakerFunding"/> (drives the speaker-hat entitlements).</summary>
    public async Task<IActionResult> OnPostFundingAsync(int participantId, SpeakerFunding funding, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var profile = await GetProfileAsync(me.EventId, participantId, ct);
        if (profile is null) return RedirectToPage(new { Msg = "Speaker profile not found." });

        profile.SpeakerFunding = funding;
        profile.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return RedirectToPage(new { Msg = "Funding updated." });
    }

    /// <summary>Set both speaking-day flags on a speaker profile.</summary>
    public async Task<IActionResult> OnPostDaysAsync(
        int participantId, bool preDay, bool mainDay, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var profile = await GetProfileAsync(me.EventId, participantId, ct);
        if (profile is null) return RedirectToPage(new { Msg = "Speaker profile not found." });

        profile.SpeakingPreDay = preDay;
        profile.SpeakingMainDay = mainDay;
        profile.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return RedirectToPage(new { Msg = "Speaking days updated." });
    }

    /// <summary>
    /// Link a participant to the PRIMARY row they duplicate (cross-email dedup).
    /// Rejects self-reference and chains (the target must itself be a primary —
    /// its own <see cref="Participant.SamePersonAsId"/> must be null).
    /// </summary>
    public async Task<IActionResult> OnPostSamePersonAsync(int participantId, int samePersonAsId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var p = await _db.Participants
            .FirstOrDefaultAsync(x => x.Id == participantId && x.EventId == me.EventId, ct);
        if (p is null) return RedirectToPage(new { Msg = "Participant not found." });

        if (samePersonAsId == participantId)
            return RedirectToPage(new { Msg = "A participant cannot be the same person as themselves." });

        var target = await _db.Participants
            .FirstOrDefaultAsync(x => x.Id == samePersonAsId && x.EventId == me.EventId, ct);
        if (target is null) return RedirectToPage(new { Msg = "Selected person not found in this edition." });

        // No chains: only allow pointing at a primary (target.SamePersonAsId == null).
        if (target.SamePersonAsId is not null)
            return RedirectToPage(new { Msg = "That person is already a duplicate of someone else — pick a primary row." });

        p.SamePersonAsId = samePersonAsId;
        await _db.SaveChangesAsync(ct);
        return RedirectToPage(new { Msg = "Linked as the same person." });
    }

    /// <summary>Clear a participant's same-person link (make it a primary again).</summary>
    public async Task<IActionResult> OnPostClearSamePersonAsync(int participantId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var p = await _db.Participants
            .FirstOrDefaultAsync(x => x.Id == participantId && x.EventId == me.EventId, ct);
        if (p is null) return RedirectToPage(new { Msg = "Participant not found." });

        p.SamePersonAsId = null;
        await _db.SaveChangesAsync(ct);
        return RedirectToPage(new { Msg = "Same-person link cleared." });
    }

    /// <summary>
    /// Apply a 3-state per-item override: "default" deletes any override row,
    /// "include"/"exclude" upserts a <see cref="ParticipantOrderOverride"/> for
    /// (EventId, ParticipantId, Item).
    /// </summary>
    public async Task<IActionResult> OnPostOverrideAsync(
        int participantId, OrderItem item, string state, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var p = await _db.Participants
            .FirstOrDefaultAsync(x => x.Id == participantId && x.EventId == me.EventId, ct);
        if (p is null) return RedirectToPage(new { Msg = "Participant not found." });

        var existing = await _db.ParticipantOrderOverrides.FirstOrDefaultAsync(
            o => o.EventId == me.EventId && o.ParticipantId == participantId && o.Item == item, ct);

        if (string.Equals(state, "default", StringComparison.OrdinalIgnoreCase))
        {
            if (existing is not null)
            {
                _db.ParticipantOrderOverrides.Remove(existing);
                await _db.SaveChangesAsync(ct);
            }
            return RedirectToPage(new { Msg = $"{item} reset to default." });
        }

        var include = string.Equals(state, "include", StringComparison.OrdinalIgnoreCase);
        if (existing is null)
        {
            _db.ParticipantOrderOverrides.Add(new ParticipantOrderOverride
            {
                EventId = me.EventId,
                ParticipantId = participantId,
                Item = item,
                Include = include,
                SetByEmail = me.Email,
                SetAt = _clock.GetUtcNow(),
            });
        }
        else
        {
            existing.Include = include;
            existing.SetByEmail = me.Email;
            existing.SetAt = _clock.GetUtcNow();
        }
        await _db.SaveChangesAsync(ct);
        return RedirectToPage(new { Msg = $"{item} forced to {(include ? "include" : "exclude")}." });
    }

    // --- Section 3 booth -----------------------------------------------------
    /// <summary>Toggle a sponsor participant's <see cref="Participant.IsBoothMember"/> flag.</summary>
    public async Task<IActionResult> OnPostBoothMemberAsync(int participantId, bool isBoothMember, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var p = await _db.Participants
            .FirstOrDefaultAsync(x => x.Id == participantId && x.EventId == me.EventId, ct);
        if (p is null) return RedirectToPage(new { Msg = "Participant not found." });

        p.IsBoothMember = isBoothMember;
        await _db.SaveChangesAsync(ct);
        return RedirectToPage(new { Msg = isBoothMember ? "Marked as booth member." : "Removed from booth members." });
    }

    // --- helpers -------------------------------------------------------------
    private Task<SpeakerProfile?> GetProfileAsync(int eventId, int participantId, CancellationToken ct) =>
        _db.SpeakerProfiles.FirstOrDefaultAsync(
            s => s.EventId == eventId && s.ParticipantId == participantId, ct);

    private async Task LoadAsync(int eventId, CancellationToken ct)
    {
        Counts = await _counts.CountsAsync(eventId, ct);

        var participants = await _db.Participants
            .Where(p => p.EventId == eventId)
            .ToListAsync(ct);
        var byId = participants.ToDictionary(p => p.Id);

        var speakerProfiles = await _db.SpeakerProfiles
            .Where(s => s.EventId == eventId)
            .ToListAsync(ct);
        var profileByParticipant = speakerProfiles.ToDictionary(s => s.ParticipantId, s => s);

        var overrides = await _db.ParticipantOrderOverrides
            .Where(o => o.EventId == eventId)
            .ToListAsync(ct);
        var overridesByParticipant = overrides
            .GroupBy(o => o.ParticipantId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Candidate "same person as" targets: every PRIMARY row (no chains).
        LinkTargets = participants
            .Where(p => p.SamePersonAsId is null)
            .OrderBy(p => p.FullName)
            .Select(p => new PersonOption(p.Id, p.FullName, p.Email, p.Role))
            .ToList();

        // Section 2: every participant that HAS a speaker profile, any role.
        Speakers = participants
            .Where(p => profileByParticipant.ContainsKey(p.Id))
            .OrderBy(p => p.FullName)
            .Select(p =>
            {
                var profile = profileByParticipant[p.Id];
                var ov = overridesByParticipant.TryGetValue(p.Id, out var list)
                    ? list
                    : new List<ParticipantOrderOverride>();
                var effective = OrderEntitlements.Effective(p, profile, ov);
                var overrideMap = ov.ToDictionary(o => o.Item, o => o.Include);

                string? linkLabel = null;
                if (p.SamePersonAsId is int linkId && byId.TryGetValue(linkId, out var primary))
                    linkLabel = $"{primary.FullName} ({primary.Email})";

                return new SpeakerRow(
                    p.Id, p.FullName, p.Email, p.Role,
                    profile.SpeakerFunding, profile.SpeakingPreDay, profile.SpeakingMainDay,
                    p.SamePersonAsId, linkLabel,
                    effective, overrideMap);
            })
            .ToList();

        // Section 3: sponsor-role participants + their company package (read-only).
        var sponsors = participants
            .Where(p => p.Role == ParticipantRole.Sponsor)
            .OrderBy(p => p.FullName)
            .ToList();

        var sponsorCompanyIds = sponsors
            .Where(p => !string.IsNullOrWhiteSpace(p.SponsorCompanyId))
            .Select(p => p.SponsorCompanyId!)
            .Distinct()
            .ToList();
        var infoByCompany = await _db.SponsorInfos
            .Where(s => s.EventId == eventId && sponsorCompanyIds.Contains(s.SponsorCompanyId))
            .ToDictionaryAsync(s => s.SponsorCompanyId, s => s, ct);

        BoothMembers = sponsors.Select(p =>
        {
            SponsorPackage? pkg = null;
            bool? hasBooth = null;
            if (p.SponsorCompanyId is not null
                && infoByCompany.TryGetValue(p.SponsorCompanyId, out var info))
            {
                pkg = info.SponsorPackage;
                hasBooth = info.HasBooth;
            }
            return new BoothRow(p.Id, p.FullName, p.Email, p.IsBoothMember, pkg, hasBooth);
        }).ToList();
    }
}
