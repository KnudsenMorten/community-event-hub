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
                (p, pa) => new { Profile = p, pa.Email, pa.FullName, pa.Role })
            .ToListAsync(ct);

        var found = new List<SpeakerGap>();
        var compared = 0;

        foreach (var row in linked)
        {
            // §1365 — only the Speaker role is compared; `linked` stays whole so every profile is stamped below.
            if (!IsReported(row.Role)) continue;
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
            // §1364 — no intro line of our own: the notifier already explains that Backstage is updated by hand, and a
            // second paragraph rendered as a stray bullet. The two arrows say which side to change.
            var lines = new List<string>
            {
                "<b>(new value in Zoho from hub)</b> — set it in Backstage · "
                + "<b>(new value in hub from Zoho)</b> — the hub is missing it, add it in CEH.",
            };
            foreach (var g in found)
            {
                lines.Add($"<b>{Enc(g.Name)}</b> &lt;{Enc(g.Email)}&gt;\n• "
                          + string.Join("\n• ", g.Gaps.Select(Enc)));
            }
            lines.Add("<br><i>You will not be mailed about the same set again — only when it changes.</i>");

            // 🔴 §762 — the CTA used to point at /Organizer/PendingSpeakers with the text "Open
            // Organizer → Speakers". He clicked it and found it EMPTY, and reasonably concluded the
            // mail was broken: that page lists speakers HELD FROM THE ZOHO FLOW (uncategorized, not
            // activated, out of ring) — a completely different population from "speaker records in
            // Backstage missing a field". A link that reliably shows nothing is worse than no link.
            //
            // It now opens the participants list, from which each speaker's row carries the §761
            // read-only "Speaker details (copy/paste)" block — which is the value he actually needs
            // in front of him while typing into Backstage.
            //
            // §763 — manualOnly: the speakers API is create-only, so CEH wrote NOTHING here. The mail
            // must not claim it did, nor ask him to publish a change that does not exist.
            await _notifier.NotifyAsync("Speakers — Backstage and CEH differ", lines, ct,
                actionable: true,
                actionUrl: "https://eldk27.eventhub.expertslive.dk/Organizer/Participants?role=Speaker",
                actionText: "Open the speakers in CEH (to copy the values)",
                manualOnly: true);
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
    /// §1365 — is this participant in the gap mail? Only the <see cref="ParticipantRole.Speaker"/> role.
    /// </summary>
    /// <remarks>
    /// Operator 2026-10-21: *"only take the role speaker … so we dont include organizers, volunteers, sponsors in that
    /// alert mail"*. Sponsors and organizers who also speak never fill the CEH speaker form, so CEH has no company or
    /// skills for them and every "(new value in hub from Zoho)" line was an action nobody could take — it never cleared.
    /// </remarks>
    public static bool IsReported(ParticipantRole role) => role == ParticipantRole.Speaker;

    /// <summary>
    /// The gaps for one speaker — CEH has a value, Zoho does not. Only READABLE fields are compared.
    /// </summary>
    /// <remarks>
    /// 🔒 §760 — <b>VALUES ARE PRINTED BARE, never wrapped in quotes.</b> Operator 2026-08-01:
    /// <i>"remove the "" around string as it makes it impossible to easily cut/paste"</i>.
    /// <para>This mail exists BECAUSE the Backstage speakers API is create-only, so every value here
    /// has to be pasted into Backstage <b>by hand</b>. A double-click selects a URL but not the
    /// quotes; a drag selection catches them — and a pasted <c>"https://…/in/klabier"</c> is a broken
    /// link. The quotes made the one action the mail exists for harder, on every line.</para>
    /// <para>The label and its colon already delimit the value. Where a value could be genuinely
    /// ambiguous, SAY so in words (as the bio line does) rather than relying on quote marks.</para>
    /// </remarks>
    public static IReadOnlyList<string> GapsFor(SpeakerProfile ceh, BackstageSpeaker z)
    {
        var gaps = new List<string>();

        // 🔴 §1364 — CEH IS THE MASTER, and the CONTENT must match, not only exist. Operator 2026-10-20: *"compare the
        // speaker fields from ceh (master) to zoho … tagline, bio, country, company, skills (accreditation) … they must
        // match, with ceh as master … it must be in the speaker gap job so we get a mail reminder"*. Before, a field was
        // reported only when Backstage was BLANK, so a Backstage value that had drifted from CEH was never seen.
        // 🔁 A blank in CEH with a value in Backstage is the OTHER direction: CEH lacks something Backstage has. It is
        // reported as "(add to CEH) <- value" so the organizers can catch CEH up (operator: "if field is blank in ceh, but
        // exist in zoho. tell me so i can update ceh … i will then make a catch-up into ceh and next sync will be fine").
        // It is never "clear it in Backstage" — that would delete real data CEH simply never captured.

        // SKILLS — CEH derives it from the Microsoft accreditation + MVP categories (ZohoFieldMap.SpeakerSkills, §302b);
        // compared as a SET, so order, case and ", " vs "," are not a difference.
        CompareField(gaps, ZohoFieldMap.Speaker.Skills.GuiLabel, ZohoFieldMap.SpeakerSkills(ceh), z.Skills, SkillSet);

        // COMPANY / DESIGNATION / BIO — compared as plain text, ignoring capitalisation ("InSpark" = "Inspark").
        CompareField(gaps, ZohoFieldMap.Speaker.Company.GuiLabel, ceh.CompanyName, z.Company, ComparableText);
        CompareField(gaps, ZohoFieldMap.Speaker.Tagline.GuiLabel, ceh.Tagline, z.Tagline, ComparableText);
        // §1364 — the bio line carries the plain text (Backstage stores HTML), so it pastes cleanly either way.
        CompareField(gaps, ZohoFieldMap.Speaker.Biography.GuiLabel, PlainText(ceh.Biography), PlainText(z.Bio), ComparableText);

        // SOCIAL — the two CEH fields merge into Zoho's single social object (FieldKind.Object), so
        // existence is the only meaningful check (§302b: "just validate it exist").
        if (Has(ceh.LinkedIn) && !Has(z.LinkedIn))
            gaps.Add(Line(ZohoFieldMap.Speaker.LinkedIn.GuiLabel, ceh.LinkedIn!.Trim()));
        if (!Has(ceh.LinkedIn) && Has(z.LinkedIn))
            gaps.Add(AddToCehLine(ZohoFieldMap.Speaker.LinkedIn.GuiLabel, z.LinkedIn!.Trim()));
        if (Has(ceh.Twitter) && !Has(z.Twitter))
            gaps.Add(Line(ZohoFieldMap.Speaker.Twitter.GuiLabel, ceh.Twitter!.Trim()));
        if (!Has(ceh.Twitter) && Has(z.Twitter))
            gaps.Add(AddToCehLine(ZohoFieldMap.Speaker.Twitter.GuiLabel, z.Twitter!.Trim()));

        // 🔒 COUNTRY — NOT a comparison. Zoho never returns it (§623), so we cannot know whether it
        // is set. Reported as an unverifiable check, and ONLY when CEH actually has a value, so the
        // line carries the answer rather than just a chore.
        //
        // 🔒 §762 — …UNLESS an organizer has already CONFIRMED this exact value is set over there.
        // Without this the line could never clear: Backstage never reports country back, so the
        // reporter re-emitted it on every comparison for ever, and those permanent lines rode along
        // on every future re-send triggered by someone else's real gap. Operator: *"i have just
        // completed all the changes. so i need to approve/complete them somewhere so they dont come
        // again."*
        //
        // 🔑 Compared by VALUE, not by a flag: if CEH's country later changes, the confirmation no
        // longer matches and the line comes back — a new value is a new fact, and suppressing it
        // would silently hide a genuinely wrong Backstage record.
        // ✅ §893 — COUNTRY IS NOW A REAL COMPARISON. The §623 "not readable" finding was an
        // artefact: Zoho OMITS an unset field, and on the day it was measured no speaker had a
        // country set, so an empty sample read as a missing feature.
        //
        // 🔑 That fixes the actual complaint — operator 2026-08-06: *"i now get a daily mail saying
        // to check country. but that is wrong as we have new knowledge now"*. The line used to be
        // an unverifiable chore that could only be silenced by ticking a box; it is now either a
        // genuine gap or absent.
        //
        // 🔒 ABSENT ≠ EMPTY: a missing key means Backstage has NO country, which IS the gap worth
        // reporting. A present key that matches is silence.
        // §1364 — CEH blank, Backstage set: CEH is missing it — add it to CEH.
        if (BackstageSpeaker.CountryIsReadable && !Has(ceh.Country) && Has(z.Country))
            gaps.Add(AddToCehLine(ZohoFieldMap.Speaker.Country.GuiLabel, z.Country!.Trim()));

        if (BackstageSpeaker.CountryIsReadable && Has(ceh.Country))
        {
            if (!Has(z.Country))
            {
                gaps.Add(Line(ZohoFieldMap.Speaker.Country.GuiLabel, ceh.Country!.Trim()));
            }
            else if (!CountryMatches(ceh.Country, z.Country))
            {
                // ⚠️ Zoho stores an ISO-2 CODE ("DK"); CEH may hold a name ("Denmark"). Only a
                // real disagreement is reported — otherwise every speaker would differ forever.
                // §1364 — the value to put in Backstage, not the old one.
                gaps.Add(Line(ZohoFieldMap.Speaker.Country.GuiLabel, ceh.Country!.Trim()));
            }
        }

        return gaps;
    }

    /// <summary>
    /// §893 — does CEH's country agree with Backstage's? Zoho returns an <b>ISO-2 code</b> (`DK`);
    /// CEH may hold a display name (`Denmark`), so a plain string compare would mark every speaker
    /// as differing forever — the §594 "Tags missing" mail again.
    /// </summary>
    /// <remarks>
    /// 🔒 <b>Unknown ⇒ treated as MATCHING, deliberately.</b> §582: a false gap is worse than no
    /// gap, because he acts on it. If the code is not one we can map, we say nothing rather than
    /// accuse a record that may be perfectly correct.
    /// </remarks>
    /// <remarks>
    /// 🔑 §894 — delegates to the SHARED <see cref="CountryCodeMapper"/> rather than keeping a second
    /// list. Two country maps in one codebase drift, and then the ERP sync and the speaker report
    /// disagree about what "Danmark" means ([[ceh-count-the-shared-things]]).
    /// </remarks>
    public static bool CountryMatches(string? cehCountry, string? zohoCountry) =>
        CountryCodeMapper.SameCountry(cehCountry, zohoCountry);

    /// <summary>
    /// §762 — has an organizer confirmed that THIS country value is set in Backstage?
    /// </summary>
    /// <remarks>
    /// Trimmed + case-insensitive so "dk" and "DK " count as the same confirmation — the value is
    /// typed by a human on both sides, and a whitespace difference re-nagging him about work he has
    /// done would be the defect all over again.
    /// </remarks>
    public static bool CountryIsConfirmed(SpeakerProfile ceh) =>
        Has(ceh.CountryConfirmedInBackstage)
        && string.Equals(
            ceh.CountryConfirmedInBackstage!.Trim(),
            (ceh.Country ?? string.Empty).Trim(),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// §1364 — one CEH-master comparison. CEH has a value that Backstage lacks or holds differently ⇒ set it in
    /// Backstage. CEH is blank but Backstage has a value ⇒ add it to CEH (the other direction). Both blank or equal ⇒ silent.
    /// </summary>
    private static void CompareField(
        List<string> gaps, string label, string? ceh, string? zoho, Func<string?, string> normalise)
    {
        var c = normalise(ceh);
        var z = normalise(zoho);
        if (string.Equals(c, z, StringComparison.Ordinal)) return;
        gaps.Add(c.Length == 0 ? AddToCehLine(label, PlainText(zoho)) : Line(label, ceh!.Trim()));
    }

    /// <summary>
    /// §1364 — one mail line: the field and the value to put in Backstage, nothing else. Operator 2026-10-20:
    /// *"maybe Tagline (new value) ->"*. The value stays bare (§760: no quotes, so it pastes cleanly).
    /// </summary>
    /// <remarks>Operator 2026-10-20: *"i need to be more specific like (new value in zoho from hub) or (new value in hub
    /// from zoho)"* — the label says which system to change and where the value comes from.</remarks>
    public static string Line(string label, string newValue) => $"{label} (new value in Zoho from hub) -> {newValue}";

    /// <summary>§1364 — the other direction: CEH is blank, Backstage has this value — copy it into CEH.</summary>
    public static string AddToCehLine(string label, string backstageValue) => $"{label} (new value in hub from Zoho) -> {backstageValue}";

    /// <summary>§1364 — plain text, case-insensitive: "InSpark" and "Inspark" are the same company.</summary>
    public static string ComparableText(string? value) => PlainText(value).ToLowerInvariant();

    /// <summary>§1364 — text as a reader sees it: tags removed, entities decoded, whitespace collapsed.</summary>
    public static string PlainText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var noTags = System.Text.RegularExpressions.Regex.Replace(value, "<[^>]+>", " ");
        var decoded = System.Net.WebUtility.HtmlDecode(noTags).Replace(' ', ' ');
        return System.Text.RegularExpressions.Regex.Replace(decoded, @"\s+", " ").Trim();
    }

    /// <summary>§1364 — skills as a set: order, case and ", " vs "," are not a difference.</summary>
    public static string SkillSet(string? value) =>
        string.Join(",", (value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => !string.Equals(s, "None", StringComparison.OrdinalIgnoreCase))
            .Select(s => s.ToLowerInvariant())
            .Distinct()
            .OrderBy(s => s, StringComparer.Ordinal));

    private static bool Has(string? v) => !string.IsNullOrWhiteSpace(v);
    private static string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);

    private static string Hash(IEnumerable<SpeakerGap> gaps) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(
                string.Join("\n", gaps.OrderBy(g => g.Email, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.Email + "|" + string.Join(";", g.Gaps))))))[..32];
}
