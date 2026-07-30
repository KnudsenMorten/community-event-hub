using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// §575/§586 — reports what CEH holds that Zoho Backstage does NOT, per speaker, as a mail the
/// organizers can act on by hand.
/// </summary>
/// <remarks>
/// <para><b>Why a MAIL and not a push.</b> Operator 2026-07-28: *"i am also missing a speaker mail,
/// which shows the gaps between ceh and zoho. for example per larsen has set country + skills"*, and
/// then, once the API limits were established: *"once it exist in zoho (session, speaker) we must
/// send mail to info@expertslive.dk with things that must be added manually by org team"*. The v3
/// speakers API is CREATE-ONLY — PUT, PATCH and POST to <c>/speakers/{id}</c> all answer
/// <c>404 "Please provide valid method"</c> (§584, probed with a non-existent id). So a mail is not
/// a fallback here; it is the only thing the API permits.</para>
///
/// <para><b>🔒 COUNTRY IS DELIBERATELY NOT COMPARED.</b> Verified live 2026-07-29: the speakers API
/// returns no <c>country</c> field on the list OR the per-id record. Diffing it would mark EVERY
/// speaker as missing a country forever, and no action could close it — exactly the §594 "Tags
/// missing" mail the operator received for tags he had already entered. §582: honour the declared
/// limitation, because a false gap is worse than no gap — he acts on it. Country is reported ONCE
/// per speaker instead, as an unverifiable "please check", never as a detected deviation.</para>
///
/// <para><b>Hash-deduped.</b> One mail when a gap appears or changes — never one per pass. That is
/// his standing rule and the §302 "70-mail night" is why.</para>
/// </remarks>
public sealed class SpeakerZohoGapReporter
{
    private readonly CommunityHubDbContext _db;
    private readonly ZohoClient _zoho;
    private readonly Email.ZohoChangeNotifier? _notifier;
    private readonly ILogger<SpeakerZohoGapReporter>? _log;

    public SpeakerZohoGapReporter(
        CommunityHubDbContext db, ZohoClient zoho,
        Email.ZohoChangeNotifier? notifier = null,
        ILogger<SpeakerZohoGapReporter>? log = null)
    {
        _db = db; _zoho = zoho; _notifier = notifier; _log = log;
    }

    /// <summary>One speaker's gaps, for the mail and for tests.</summary>
    public sealed record SpeakerGap(string Email, string Name, IReadOnlyList<string> Gaps);

    /// <summary>The outcome of one pass.</summary>
    public sealed record Result(
        bool SourceAvailable, string? UnavailableReason,
        int Compared, int WithGaps, int Mailed);

    /// <summary>
    /// Compare every LINKED speaker's CEH values against the live Zoho record and mail the
    /// differences. Never throws — a reporting failure must not take a timer job down.
    /// </summary>
    public async Task<Result> RunAsync(int eventId, CancellationToken ct = default)
    {
        var pull = await _zoho.GetBackstageSpeakersAsync(
            await _zoho.GetAccessTokenAsync(ct) ?? string.Empty, ct);

        // ⚠️ An unavailable pull is NOT "everything is missing". Report and change nothing — the
        // §553/§555 rule that stopped a 401 from being read as "0 sessions".
        if (!pull.IsAvailable)
        {
            _log?.LogWarning(
                "SpeakerZohoGapReporter: speakers unavailable ({Reason}) — nothing compared, nothing mailed.",
                pull.UnavailableReason);
            return new Result(false, pull.UnavailableReason, 0, 0, 0);
        }

        var live = pull.Speakers
            .Where(s => !string.IsNullOrWhiteSpace(s.SpeakerId))
            .ToDictionary(s => s.SpeakerId!, StringComparer.OrdinalIgnoreCase);

        var linked = await _db.SpeakerProfiles
            .Where(p => p.EventId == eventId
                        && p.BackstageSpeakerId != null && p.BackstageSpeakerId != "")
            .Join(_db.Participants, p => p.ParticipantId, pa => pa.Id,
                (p, pa) => new { Profile = p, pa.Email, pa.FullName })
            .ToListAsync(ct);

        var found = new List<SpeakerGap>();
        var compared = 0;

        foreach (var row in linked)
        {
            if (!live.TryGetValue(row.Profile.BackstageSpeakerId!, out var z)) continue;
            compared++;

            var gaps = GapsFor(row.Profile, z);
            if (gaps.Count > 0)
                found.Add(new SpeakerGap(row.Email ?? "", row.FullName ?? row.Email ?? "(unnamed)", gaps));
        }

        if (found.Count == 0)
        {
            _log?.LogInformation(
                "SpeakerZohoGapReporter: compared {Compared} speaker(s) — no gaps.", compared);
            return new Result(true, null, compared, 0, 0);
        }

        // Hash-dedupe on the WHOLE report: the same outstanding set must not re-mail every pass.
        var hash = Hash(found);
        var stamp = await _db.SpeakerProfiles
            .Where(p => p.EventId == eventId && p.ZohoGapNotifiedHash != null)
            .Select(p => p.ZohoGapNotifiedHash)
            .FirstOrDefaultAsync(ct);

        if (string.Equals(stamp, hash, StringComparison.Ordinal))
        {
            _log?.LogInformation(
                "SpeakerZohoGapReporter: {Count} speaker(s) still have gaps — unchanged since the last "
                + "mail, so nothing was sent.", found.Count);
            return new Result(true, null, compared, found.Count, 0);
        }

        var mailed = 0;
        if (_notifier is not null)
        {
            var lines = new List<string>
            {
                "<b>These speakers are missing information in Zoho Backstage that CEH already holds.</b> "
                + "The speakers API is create-only, so these cannot be pushed — they need adding by "
                + "hand in Backstage.",
            };
            foreach (var g in found)
            {
                lines.Add($"<b>{Enc(g.Name)}</b> &lt;{Enc(g.Email)}&gt;\n• "
                          + string.Join("\n• ", g.Gaps.Select(Enc)));
            }
            lines.Add("<br><i>You will not be mailed about the same set again — only when it changes.</i>");

            await _notifier.NotifyAsync("Speakers — details missing in Backstage", lines, ct,
                actionable: true,
                actionUrl: "https://eldk27.eventhub.expertslive.dk/Organizer/PendingSpeakers",
                actionText: "Open Organizer → Speakers");
            mailed = found.Count;
        }

        // Stamp every linked profile so the dedupe is stable regardless of which row is read back.
        foreach (var row in linked) row.Profile.ZohoGapNotifiedHash = hash;
        await _db.SaveChangesAsync(ct);

        _log?.LogInformation(
            "SpeakerZohoGapReporter: compared {Compared}, {WithGaps} with gaps, mailed {Mailed}.",
            compared, found.Count, mailed);

        return new Result(true, null, compared, found.Count, mailed);
    }

    /// <summary>
    /// The gaps for one speaker — CEH has a value, Zoho does not. Only READABLE fields are compared.
    /// </summary>
    public static IReadOnlyList<string> GapsFor(SpeakerProfile ceh, BackstageSpeaker z)
    {
        var gaps = new List<string>();

        // SKILLS — readable, and the operator's own example. CEH derives it from the Microsoft
        // accreditation + MVP categories (ZohoFieldMap.SpeakerSkills, §302b).
        var skills = ZohoFieldMap.SpeakerSkills(ceh);
        if (Has(skills) && !Has(z.Skills))
            gaps.Add($"{ZohoFieldMap.Speaker.Skills.GuiLabel}: \"{skills!.Trim()}\"");

        // COMPANY / DESIGNATION / BIO — all readable, so a blank in Zoho is a REAL gap.
        if (Has(ceh.CompanyName) && !Has(z.Company))
            gaps.Add($"{ZohoFieldMap.Speaker.Company.GuiLabel}: \"{ceh.CompanyName!.Trim()}\"");
        if (Has(ceh.Tagline) && !Has(z.Tagline))
            gaps.Add($"{ZohoFieldMap.Speaker.Tagline.GuiLabel}: \"{ceh.Tagline!.Trim()}\"");
        if (Has(ceh.Biography) && !Has(z.Bio))
            gaps.Add($"{ZohoFieldMap.Speaker.Biography.GuiLabel}: (bio text is set in CEH but empty in Backstage)");

        // SOCIAL — the two CEH fields merge into Zoho's single social object (FieldKind.Object), so
        // existence is the only meaningful check (§302b: "just validate it exist").
        if (Has(ceh.LinkedIn) && !Has(z.LinkedIn))
            gaps.Add($"{ZohoFieldMap.Speaker.LinkedIn.GuiLabel}: \"{ceh.LinkedIn!.Trim()}\"");
        if (Has(ceh.Twitter) && !Has(z.Twitter))
            gaps.Add($"{ZohoFieldMap.Speaker.Twitter.GuiLabel}: \"{ceh.Twitter!.Trim()}\"");

        // 🔒 COUNTRY — NOT a comparison. Zoho never returns it (§623), so we cannot know whether it
        // is set. Reported as an unverifiable check, and ONLY when CEH actually has a value, so the
        // line carries the answer rather than just a chore.
        if (!BackstageSpeaker.CountryIsReadable && Has(ceh.Country))
            gaps.Add($"{ZohoFieldMap.Speaker.Country.GuiLabel}: \"{ceh.Country!.Trim()}\" "
                     + "— Backstage does not report this field, so please confirm it is set");

        return gaps;
    }

    private static bool Has(string? v) => !string.IsNullOrWhiteSpace(v);
    private static string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);

    private static string Hash(IEnumerable<SpeakerGap> gaps) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(
                string.Join("\n", gaps.OrderBy(g => g.Email, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.Email + "|" + string.Join(";", g.Gaps))))))[..32];
}
