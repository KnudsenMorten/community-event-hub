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
/// every speaker (§299 6.1 category — Community / Sponsor / Guest, Guest hotel
/// nights, cross-email dedup, per-item overrides) and classifies them for the deduped "what
/// to order" counts the logistics team buys against. Presenting days are DERIVED
/// from linked sessions (§299 C5) and shown read-only.
///
/// <para>
/// Two sections: (1) a per-speaker review list with live effective-entitlement chips and
/// per-row editable controls (each its own POST handler, redirect-after-post); (2) a
/// compact sponsor booth-member toggle list.
/// </para>
///
/// <para>§327j — the deduped "what to order" count strip was REMOVED (operator 2026-07-25:
/// the page "mixed 2 major things into one page"). Those numbers now live with the thing
/// being ordered, where each reconciles against a named run-sheet — Swag, Lunch, the dinner
/// run-sheet and HotelAllotments. Two surfaces showing the same count is how they end up
/// disagreeing. Classification here still DRIVES them.</para>
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
    private readonly TimeProvider _clock;

    public SpeakerReviewModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        TimeProvider clock)
    {
        _db = db;
        _participant = participant;
        _clock = clock;
    }

    /// <summary>True when the caller is not an organizer — render a friendly notice, not the content.</summary>
    public bool AccessDenied { get; private set; }

    /// <summary>Set when the data layer fails — an honest banner instead of an unhandled 500.</summary>
    public string? Error { get; private set; }

    /// <summary>Status line shown after a redirect-after-post.</summary>
    public string? Notice { get; private set; }

    [BindProperty(SupportsGet = true)] public string? Msg { get; set; }


    /// <summary>One review row per participant who HAS a speaker profile.</summary>
    public List<SpeakerRow> Speakers { get; private set; } = new();

    /// <summary>The "same person as" candidate list (all primary rows in the edition).</summary>
    public List<PersonOption> LinkTargets { get; private set; } = new();

    /// <summary>Compact sponsor booth-member section.</summary>
    public List<BoothRow> BoothMembers { get; private set; } = new();

    /// <summary>
    /// §299 OPEN-22 completeness check: ACTIVE participant rows that share an e-mail with
    /// another row in the edition but are NOT linked via <c>SamePersonAsId</c> — each group is
    /// one human counted more than once. The ordering counts must not be trusted
    /// while this list is non-empty. (Cross-e-mail duplicates — the same human under two
    /// addresses — are only detectable once linked; this catches the same-address kind.)
    /// </summary>
    public List<string> UnlinkedDuplicateWarnings { get; private set; } = new();

    /// <summary>The full <see cref="OrderItem"/> set, in declared order, for the UI.</summary>
    public static IReadOnlyList<OrderItem> AllItems { get; } = Enum.GetValues<OrderItem>();

    /// <summary>One speaker review row (a participant + their speaker profile + effective items).</summary>
    /// <param name="Category">§299 6.1 — the organizer-set category; null = uncategorized (blocks activation, excluded from counts).</param>
    /// <param name="GuestFundedNights">§299 6.3 — organizer-entered ELDK-funded hotel nights; Guest category only.</param>
    /// <param name="LegacyFunding">LEGACY (§299 C3) — the retired funding value, shown for audit only.</param>
    /// <param name="PresentsPreDay">§299 C5 — DERIVED from linked sessions (read-only; the stored flag is retired).</param>
    /// <param name="PresentsMainDay">§299 C5 — DERIVED from linked sessions (read-only).</param>
    public sealed record SpeakerRow(
        int ParticipantId,
        string FullName,
        string Email,
        ParticipantRole Role,
        SpeakerCategory? Category,
        int? GuestFundedNights,
        SpeakerFunding LegacyFunding,
        bool PresentsPreDay,
        bool PresentsMainDay,
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

    // --- Section 1 category --------------------------------------------------
    /// <summary>
    /// §299 6.1 — set (or clear) a speaker's <see cref="SpeakerCategory"/>, the
    /// canonical classification driving the speaker-hat entitlements. A null
    /// <paramref name="category"/> (the "(not set)" option) marks the speaker
    /// uncategorized again: excluded from every count and blocked from
    /// activation. Also accepts the Guest-only ELDK-funded hotel nights
    /// (<paramref name="guestFundedNights"/>) so category + nights save in one
    /// post; the nights value is ignored (cleared) for non-Guest categories.
    /// </summary>
    public async Task<IActionResult> OnPostCategoryAsync(
        int participantId, SpeakerCategory? category, int? guestFundedNights, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var profile = await GetProfileAsync(me.EventId, participantId, ct);
        if (profile is null) return RedirectToPage(new { Msg = "Speaker profile not found." });

        if (guestFundedNights is < 0)
            return RedirectToPage(new { Msg = "Guest hotel nights cannot be negative." });

        profile.Category = category;
        // Guest nights are meaningful for the Guest category only — a switch away
        // from Guest clears the stale value so the tallies never read it again.
        profile.GuestFundedNights = category == SpeakerCategory.Guest ? guestFundedNights : null;
        profile.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return RedirectToPage(new
        {
            Msg = category is null
                ? "Category cleared — this speaker is uncategorized (excluded from all counts, cannot be activated)."
                : $"Category set to {category}.",
        });
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

        // §299 7.4 hard constraint: a non-exhibitor (no-booth) sponsor cannot have a booth
        // member — enforced at ASSIGNMENT time, not just hidden in the GUI.
        if (isBoothMember)
        {
            var hasBooth = !string.IsNullOrWhiteSpace(p.SponsorCompanyId)
                && await _db.SponsorInfos.AnyAsync(
                    s => s.EventId == me.EventId
                         && s.SponsorCompanyId == p.SponsorCompanyId
                         && s.SponsorPackage >= SponsorPackage.Gold, ct);
            if (!hasBooth)
            {
                return RedirectToPage(new
                {
                    Msg = "Cannot mark as booth member: this company has no exhibitor booth "
                          + "(digital-only sponsorship). Raise the company's package first if that is wrong.",
                });
            }
        }

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

        // §299 C5: presenting days DERIVE from linked sessions (SpeakerDayScope) —
        // shown read-only per row and fed into the entitlement computation.
        var daysBySpeaker = await SpeakerDayScope.DaysBySpeakerAsync(_db, eventId, ct);

        // §299 OPEN-22 completeness check: same-e-mail groups with more than one UNLINKED
        // active row = one human who would be counted twice. Surface before ordering.
        UnlinkedDuplicateWarnings = participants
            .Where(p => p.IsActive && !string.IsNullOrWhiteSpace(p.Email))
            .GroupBy(p => p.Email.Trim().ToLowerInvariant())
            .Where(g => g.Count(x => x.SamePersonAsId is null) > 1)
            .Select(g => $"{g.First().Email}: {string.Join(" + ", g.Select(x => x.Role))} "
                         + "— link the secondary row (\"same person as\") so the human is counted once.")
            .OrderBy(s => s)
            .ToList();

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
                var days = daysBySpeaker.TryGetValue(p.Id, out var d) ? d : SpeakerDays.None;
                var effective = OrderEntitlements.Effective(p, profile, days, ov);
                var overrideMap = ov.ToDictionary(o => o.Item, o => o.Include);

                string? linkLabel = null;
                if (p.SamePersonAsId is int linkId && byId.TryGetValue(linkId, out var primary))
                    linkLabel = $"{primary.FullName} ({primary.Email})";

                return new SpeakerRow(
                    p.Id, p.FullName, p.Email, p.Role,
                    profile.Category, profile.GuestFundedNights, profile.SpeakerFunding,
                    days.PresentsPreDay, days.PresentsMainDay,
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
