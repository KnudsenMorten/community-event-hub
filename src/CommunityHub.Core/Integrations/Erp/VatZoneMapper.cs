namespace CommunityHub.Core.Integrations.Erp;

/// <summary>
/// A Danish VAT zone (<i>momszone</i>), as e-conomic names them.
/// </summary>
/// <remarks>
/// The numbers are e-conomic's own and are already relied on elsewhere in this codebase —
/// <c>WebshopInvoiceLineComposer.ResolveProductNumber</c> maps 1 to the taxed product and 2/3/4 to
/// the untaxed one. They are not invented here.
/// </remarks>
public enum VatZone
{
    /// <summary>Not known — the country was blank or is not recognised.</summary>
    /// <remarks>
    /// 🔴 A zone must never be GUESSED. Mis-zoning is a tax error on a real invoice, and the two
    /// wrong answers are not symmetric: charging Danish VAT to a foreign customer is a refund and an
    /// apology, while omitting it wrongly is money the organiser owes. Unknown is reported.
    /// </remarks>
    Unknown = 0,

    /// <summary>Denmark. e-conomic zone 1 — <i>Indland</i>, with VAT.</summary>
    Indland = 1,

    /// <summary>An EU member state other than Denmark. e-conomic zone 2 — <i>EU (rubrik B)</i>.</summary>
    Eu = 2,

    /// <summary>Outside the EU. e-conomic zone 3 — <i>Udland (rubrik C)</i>.</summary>
    Udland = 3,

    /// <summary>Danish, but exempt. e-conomic zone 4 — <i>Indland uden moms</i>.</summary>
    /// <remarks>⚠️ Never derived from a country: it is a property of the customer, not of where they
    /// are, so it can only ever be set by hand in the ERP.</remarks>
    IndlandUdenMoms = 4,
}

/// <summary>
/// §1166 — the one place that turns a country into a VAT zone.
/// </summary>
/// <remarks>
/// <para>Operator 2026-09-02: <i>"we have different VAT categories. It depends on the country, where
/// the sponsor are from. For example Norway and UK are Udland C but European countries are B. We need
/// to have a country vat mapper, as we now also need this logic in the travel reimbursement
/// module"</i>.</para>
///
/// <para>🔴 <b>THE RULE IS EU MEMBERSHIP, NOT GEOGRAPHY — and that is precisely why he named Norway
/// and the UK.</b> Both are unmistakably European and neither is in the EU, so "European countries
/// are B" cannot be implemented as "is it in Europe?". Reading it that way would put Norway, the UK,
/// Switzerland and Iceland in rubrik B, which is the wrong VAT treatment on a real invoice.</para>
///
/// <para>⚠️ <b>The United Kingdom is the trap with a date on it.</b> It WAS in the EU. Any list
/// copied from an older source, or any memory of one, still contains it — so it is called out here
/// rather than left to be noticed.</para>
///
/// <para>🔒 <b>Unknown is an answer.</b> A country that is blank or unrecognised returns
/// <see cref="VatZone.Unknown"/>; no caller may fall back to a zone, because a guessed VAT zone is a
/// tax error that looks like data.</para>
///
/// <para>🔴 <b>THIS IS NOT THE AUTHORITY FOR AN ERP CUSTOMER, AND MUST NOT BECOME ONE.</b> Operator
/// 2026-09-02: <i>"actually it comes from erp (synced to CM), so it is fine"</i>. A customer's VAT
/// zone is decided in e-conomic and mirrored into Company Manager by the sync, so deriving it again
/// here would replace an authoritative value with a computed one.</para>
///
/// <para>🔑 And a country cannot express the whole answer anyway: <i>Indland uden moms</i> is a
/// property of the CUSTOMER — an exemption — not of where they are, so no mapper could derive it.
/// ⇒ <b>The ERP decides for a customer it holds; this answers where there is no ERP customer at
/// all</b>, which is the travel-claim case — a speaker is a person with a country and no e-conomic
/// record.</para>
/// </remarks>
public static class VatZoneMapper
{
    private const string Denmark = "DK";

    /// <summary>
    /// The 27 EU member states. ⚠️ Excludes the UK (left 2020) and every EFTA country
    /// (Norway, Switzerland, Iceland, Liechtenstein).
    /// </summary>
    private static readonly HashSet<string> EuMemberCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "AT", "BE", "BG", "HR", "CY", "CZ", "DK", "EE", "FI", "FR", "DE", "GR", "HU", "IE",
        "IT", "LV", "LT", "LU", "MT", "NL", "PL", "PT", "RO", "SK", "SI", "ES", "SE",
    };

    /// <summary>
    /// Country NAMES to ISO codes, for sources that give a name rather than a code.
    /// </summary>
    /// <remarks>
    /// 🔑 Needed because the two callers are fed differently: the ERP side carries a 2-letter billing
    /// country, while a speaker profile imported from the session system carries a spelled-out name.
    /// One mapper has to accept both, or each caller grows its own half-right version.
    /// </remarks>
    private static readonly Dictionary<string, string> NameToCode = new(StringComparer.OrdinalIgnoreCase)
    {
        ["denmark"] = "DK", ["danmark"] = "DK",
        ["sweden"] = "SE", ["sverige"] = "SE",
        ["norway"] = "NO", ["norge"] = "NO",
        ["finland"] = "FI", ["suomi"] = "FI",
        ["iceland"] = "IS", ["island"] = "IS",
        ["germany"] = "DE", ["deutschland"] = "DE", ["tyskland"] = "DE",
        ["netherlands"] = "NL", ["the netherlands"] = "NL", ["holland"] = "NL",
        ["belgium"] = "BE", ["france"] = "FR", ["spain"] = "ES", ["portugal"] = "PT",
        ["italy"] = "IT", ["austria"] = "AT", ["ireland"] = "IE", ["poland"] = "PL",
        ["czechia"] = "CZ", ["czech republic"] = "CZ", ["slovakia"] = "SK", ["slovenia"] = "SI",
        ["hungary"] = "HU", ["romania"] = "RO", ["bulgaria"] = "BG", ["croatia"] = "HR",
        ["greece"] = "GR", ["estonia"] = "EE", ["latvia"] = "LV", ["lithuania"] = "LT",
        ["luxembourg"] = "LU", ["malta"] = "MT", ["cyprus"] = "CY",
        // Non-EU, and the reason this mapper exists.
        ["united kingdom"] = "GB", ["great britain"] = "GB", ["england"] = "GB",
        ["scotland"] = "GB", ["wales"] = "GB", ["uk"] = "GB", ["u.k."] = "GB",
        ["switzerland"] = "CH", ["schweiz"] = "CH",
        ["liechtenstein"] = "LI",
        ["united states"] = "US", ["united states of america"] = "US", ["usa"] = "US",
        ["u.s.a."] = "US", ["america"] = "US",
        ["canada"] = "CA", ["australia"] = "AU", ["new zealand"] = "NZ",
        ["india"] = "IN", ["japan"] = "JP", ["brazil"] = "BR", ["south africa"] = "ZA",
        ["ukraine"] = "UA", ["serbia"] = "RS", ["turkey"] = "TR", ["türkiye"] = "TR",
        ["israel"] = "IL", ["singapore"] = "SG", ["united arab emirates"] = "AE",
    };

    /// <summary>Resolve a country — a 2-letter code or a name — to its VAT zone.</summary>
    public static VatZone Resolve(string? country)
    {
        var code = ToCode(country);
        if (code is null) return VatZone.Unknown;

        if (string.Equals(code, Denmark, StringComparison.OrdinalIgnoreCase)) return VatZone.Indland;

        return EuMemberCodes.Contains(code) ? VatZone.Eu : VatZone.Udland;
    }

    /// <summary>
    /// The ISO 2-letter code for a country given as a code or a name, or null if unrecognised.
    /// </summary>
    /// <remarks>
    /// ⚠️ A 2-letter input is accepted as a code WITHOUT checking it against a list of real
    /// countries. That is deliberate: an unknown code still resolves to <see cref="VatZone.Udland"/>
    /// (not in the EU list), which is the correct treatment for anywhere outside the EU — whereas
    /// rejecting it would report "unknown" for a perfectly valid country nobody had listed.
    /// </remarks>
    public static string? ToCode(string? country)
    {
        var trimmed = (country ?? string.Empty).Trim();
        if (trimmed.Length == 0) return null;

        if (trimmed.Length == 2 && trimmed.All(char.IsLetter)) return trimmed.ToUpperInvariant();

        return NameToCode.TryGetValue(trimmed, out var code) ? code : null;
    }

    /// <summary>
    /// The token used in the finance mail: <c>INDLAND</c>, <c>EU</c>, <c>UDLAND</c> or
    /// <c>UNKNOWN</c>.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-09-02: the subject must carry <c>[MOMSZONE=UDLAND]</c> or
    /// <c>[MOMSZONE=EU]</c>. <c>INDLAND</c> completes the set — a Danish speaker is neither of the
    /// two he named, and leaving the token off for them would make its absence ambiguous with a
    /// mail sent before this existed.</para>
    ///
    /// <para>🔴 <c>UNKNOWN</c> is deliberately NOT a Danish word: it must be unmistakable that no
    /// zone was determined, rather than looking like a fourth zone somebody could book against.</para>
    /// </remarks>
    public static string Token(VatZone zone) => zone switch
    {
        VatZone.Indland => "INDLAND",
        VatZone.Eu => "EU",
        VatZone.Udland => "UDLAND",
        VatZone.IndlandUdenMoms => "INDLAND_UDEN_MOMS",
        _ => "UNKNOWN",
    };

    /// <summary>The token for a country, in one step — what a mail subject wants.</summary>
    public static string TokenFor(string? country) => Token(Resolve(country));

    /// <summary>The e-conomic zone number, or null when the zone is unknown.</summary>
    /// <remarks>
    /// 🔒 Null rather than a default. A caller writing a VAT zone onto a real invoice must handle
    /// "we do not know" explicitly; silently sending 1 (Danish, with VAT) or 3 (outside the EU)
    /// would be a tax decision made by a fallback.
    /// </remarks>
    public static int? EconomicZoneNumber(VatZone zone) =>
        zone == VatZone.Unknown ? null : (int)zone;
}
