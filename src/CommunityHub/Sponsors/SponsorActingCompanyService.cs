using CommunityHub.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Sponsors;

/// <summary>One company a sponsor contact may act for, with the name to show in the switcher.</summary>
public sealed record SponsorCompanyOption(string CompanyId, string DisplayName, bool IsPrimary);

/// <summary>
/// §1236 — WHICH sponsor company the signed-in contact is acting for right now.
/// </summary>
/// <remarks>
/// <para>🔑 <b>The choke point the portal was missing.</b> Every sponsor page used to re-run
/// <c>Participants.Where(p =&gt; p.Id == me).Select(p =&gt; p.SponsorCompanyId)</c> inline — 13 copies of the
/// same question, which is why "one person, one company" could not be relaxed without touching every
/// page. They now all ask here, and the switcher has ONE place to change.</para>
///
/// <para>🔴 <b>THE COOKIE IS NEVER TRUSTED.</b> It carries only a PROPOSED company; every read
/// re-checks it against the person's real links (§1235) and falls back to their primary company if it
/// does not hold. A tampered cookie therefore cannot show another company's tasks, leads or uploads —
/// which matters because this value is what the whole sponsor portal authorizes against.</para>
///
/// <para>⚠️ A contact with ONE company is completely unaffected: one option, no switcher, and the same
/// answer as before.</para>
/// </remarks>
public sealed class SponsorActingCompanyService
{
    /// <summary>Session cookie (no expiry): the choice lasts for the browser session, per device.</summary>
    public const string CookieName = "ceh.sponsor.company";

    private readonly CommunityHubDbContext _db;
    private readonly IHttpContextAccessor _http;

    public SponsorActingCompanyService(CommunityHubDbContext db, IHttpContextAccessor http)
    {
        _db = db;
        _http = http;
    }

    /// <summary>
    /// Every company this contact may act for — their primary plus any §1235 links — with the company
    /// name for the switcher. Ordered: primary first, then by name.
    /// </summary>
    public async Task<IReadOnlyList<SponsorCompanyOption>> OptionsAsync(
        int participantId, int eventId, CancellationToken ct = default)
    {
        var primary = await _db.Participants
            .Where(p => p.Id == participantId)
            .Select(p => p.SponsorCompanyId)
            .FirstOrDefaultAsync(ct);

        var linked = await _db.SponsorCompanyContacts
            .Where(x => x.EventId == eventId && x.ParticipantId == participantId)
            .Select(x => x.SponsorCompanyId)
            .ToListAsync(ct);

        var ids = new List<string>();
        if (!string.IsNullOrWhiteSpace(primary)) ids.Add(primary!);
        foreach (var id in linked)
        {
            if (!ids.Contains(id, StringComparer.OrdinalIgnoreCase)) ids.Add(id);
        }
        if (ids.Count == 0) return Array.Empty<SponsorCompanyOption>();

        var names = await _db.SponsorInfos
            .Where(s => s.EventId == eventId && ids.Contains(s.SponsorCompanyId))
            .Select(s => new { s.SponsorCompanyId, s.CompanyName })
            .ToListAsync(ct);

        return ids
            .Select(id => new SponsorCompanyOption(
                id,
                names.FirstOrDefault(n => n.SponsorCompanyId == id)?.CompanyName is { Length: > 0 } nm
                    ? nm
                    // A company with no synced name still has to be pickable, and its id is the only
                    // honest label — better than an empty entry the contact cannot tell apart.
                    : $"Company {id}",
                IsPrimary: string.Equals(id, primary, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(o => o.IsPrimary)
            .ThenBy(o => o.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The company this contact is acting for: their cookie choice when it is still one of theirs,
    /// otherwise their primary company. Null when they have no company at all.
    /// </summary>
    public async Task<string?> CurrentAsync(
        int participantId, int eventId, CancellationToken ct = default)
    {
        var options = await OptionsAsync(participantId, eventId, ct);
        if (options.Count == 0) return null;

        var proposed = _http.HttpContext?.Request.Cookies[CookieName];
        if (!string.IsNullOrWhiteSpace(proposed))
        {
            var match = options.FirstOrDefault(
                o => string.Equals(o.CompanyId, proposed, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match.CompanyId;
            // 🔒 Not theirs (stale after a roster change, or tampered) ⇒ silently fall back to the
            // primary company rather than failing the page. The portal keeps working and shows the
            // company they certainly own.
        }

        // OptionsAsync orders the primary company first, so this is "their primary, else the only
        // company they have" — the pre-§1236 answer, unchanged for a single-company contact.
        return options[0].CompanyId;
    }

    /// <summary>
    /// Record the contact's choice. Returns false — and writes nothing — when the company is not one
    /// of theirs.
    /// </summary>
    public async Task<bool> TrySelectAsync(
        int participantId, int eventId, string? companyId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(companyId)) return false;

        var options = await OptionsAsync(participantId, eventId, ct);
        var match = options.FirstOrDefault(
            o => string.Equals(o.CompanyId, companyId, StringComparison.OrdinalIgnoreCase));
        if (match is null) return false;

        _http.HttpContext?.Response.Cookies.Append(CookieName, match.CompanyId, new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,          // a portal-scope choice, not tracking
            SameSite = SameSiteMode.Lax,
            Secure = _http.HttpContext?.Request.IsHttps ?? true,
            Path = "/",
        });
        return true;
    }
}
