using System.Text.RegularExpressions;
using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Integrations.Sponsors;

/// <summary>
/// The direct-contact links for one captured lead, so a sponsor can act on a
/// lead <b>in the hub without any API key or script</b> (REQUIREMENTS §20
/// Participant "Leads no-API-key contact link"). A plain mailto:/tel: anchor is
/// the universal fallback when the sponsor hasn't set up the JSON/CSV feed and
/// has no CRM integration — it opens their own mail/phone app with the lead
/// pre-addressed. Pure value; <see cref="MailtoHref"/> / <see cref="TelHref"/>
/// are null when the lead has no usable address / phone.
/// </summary>
/// <param name="LeadId">The lead row id (for the per-lead action route).</param>
/// <param name="DisplayName">Best display name (full name, else "{first} {last}", else the email).</param>
/// <param name="Email">The lead's raw email (for display), or empty.</param>
/// <param name="Phone">The lead's raw phone (for display), or empty.</param>
/// <param name="Company">The lead's company (for display), or empty.</param>
/// <param name="MailtoHref">A ready <c>mailto:</c> href with a pre-filled subject, or null when no valid email.</param>
/// <param name="TelHref">A ready <c>tel:</c> href, or null when no usable phone.</param>
public sealed record SponsorLeadContact(
    int LeadId,
    string DisplayName,
    string Email,
    string Phone,
    string Company,
    string? MailtoHref,
    string? TelHref)
{
    /// <summary>True when there is at least one way to reach this lead directly.</summary>
    public bool IsContactable => MailtoHref is not null || TelHref is not null;
}

/// <summary>
/// PURE, side-effect-free shaping of a captured <see cref="SponsorLead"/> into the
/// direct-contact links a sponsor uses when they have no leads-API key wired up
/// (REQUIREMENTS §20 Participant). No DB / clock / I/O — fully unit-testable; the
/// page just maps the sponsor's own rows through <see cref="Build"/>.
///
/// Safety: only a syntactically plausible email becomes a <c>mailto:</c>, and the
/// pieces that go into the href are URL-encoded (and stripped of CR/LF) so a lead
/// field can never inject extra mail headers or break out of the anchor. The phone
/// keeps only dialable characters.
/// </summary>
public static class SponsorLeadContactLinkBuilder
{
    // Deliberately permissive single-@ check — we are not validating deliverability,
    // only refusing to emit a mailto: for something that obviously isn't an address.
    private static readonly Regex EmailShape =
        new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

    private static readonly Regex Dialable = new(@"[^\d+]", RegexOptions.Compiled);

    /// <summary>
    /// Shape one lead. <paramref name="subjectTemplate"/> is the localized subject
    /// line for the mailto (e.g. "Following up from {0}"); {0} is filled with
    /// <paramref name="eventDisplayName"/>. A blank template falls back to a plain
    /// "Following up" so the link still works.
    /// </summary>
    public static SponsorLeadContact Build(
        SponsorLead lead, string eventDisplayName, string? subjectTemplate = null)
    {
        var email = (lead.Email ?? string.Empty).Trim();
        var phone = (lead.Phone ?? string.Empty).Trim();

        var name = !string.IsNullOrWhiteSpace(lead.FullName)
            ? lead.FullName.Trim()
            : string.Join(' ',
                new[] { lead.FirstName, lead.LastName }
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s.Trim()))
              is { Length: > 0 } joined
                ? joined
                : email;

        return new SponsorLeadContact(
            LeadId: lead.Id,
            DisplayName: string.IsNullOrWhiteSpace(name) ? "(unnamed lead)" : name,
            Email: email,
            Phone: phone,
            Company: (lead.Company ?? string.Empty).Trim(),
            MailtoHref: BuildMailto(email, eventDisplayName, subjectTemplate),
            TelHref: BuildTel(phone));
    }

    /// <summary>The mailto: href, or null when the address isn't a plausible email.</summary>
    public static string? BuildMailto(string? email, string eventDisplayName, string? subjectTemplate)
    {
        var addr = StripNewlines((email ?? string.Empty).Trim());
        if (!EmailShape.IsMatch(addr)) return null;

        var template = string.IsNullOrWhiteSpace(subjectTemplate) ? "Following up" : subjectTemplate;
        var subject = template.Contains("{0}")
            ? string.Format(System.Globalization.CultureInfo.CurrentCulture,
                template, eventDisplayName ?? string.Empty)
            : template;
        subject = StripNewlines(subject).Trim();

        // Encode both the address (path) and the subject (query) so a crafted
        // field cannot inject extra mail headers or query params.
        return $"mailto:{Uri.EscapeDataString(addr)}?subject={Uri.EscapeDataString(subject)}";
    }

    /// <summary>The tel: href (digits + a single leading +), or null when nothing dialable remains.</summary>
    public static string? BuildTel(string? phone)
    {
        var raw = StripNewlines((phone ?? string.Empty).Trim());
        if (raw.Length == 0) return null;

        // Keep only digits and '+', then collapse to at most one leading '+'.
        var kept = Dialable.Replace(raw, string.Empty);
        var hasPlus = kept.StartsWith('+');
        var digits = kept.Replace("+", string.Empty);
        if (digits.Length == 0) return null;

        return "tel:" + (hasPlus ? "+" : string.Empty) + digits;
    }

    private static string StripNewlines(string s) =>
        s.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
