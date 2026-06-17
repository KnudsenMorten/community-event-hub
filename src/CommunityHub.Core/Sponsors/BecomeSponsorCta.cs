namespace CommunityHub.Core.Sponsors;

/// <summary>
/// Configuration for the PUBLIC "become a sponsor" call-to-action shown on the
/// public sponsors page (REQUIREMENTS §21 — "Sponsors page offers no way for a
/// prospective sponsor to actually reach out"). Bound from the
/// <c>BecomeSponsor</c> config section (e.g. integrations.&lt;edition&gt;.json /
/// the gitignored *.custom.json, or environment).
///
/// Nothing here is a secret — a sponsorship contact address is meant to be public.
/// But the shipped config carries only an empty/placeholder so a real address never
/// lands in the public mirror; the operator fills it per edition. When neither a
/// <see cref="ContactEmail"/> nor a <see cref="ContactUrl"/> is set the CTA is
/// hidden entirely (no dead link), so the page is honest when it is not configured.
/// </summary>
public sealed class BecomeSponsorOptions
{
    public const string SectionName = "BecomeSponsor";

    /// <summary>
    /// The sponsorship-enquiry email address (e.g. the organizer team's sponsor
    /// inbox). Drives a <c>mailto:</c> CTA. Blank ⇒ no email CTA.
    /// </summary>
    public string? ContactEmail { get; set; }

    /// <summary>
    /// An optional external "become a sponsor" page / form URL. Used INSTEAD of the
    /// mailto when set (a hosted prospectus / form is preferred over raw email).
    /// Blank ⇒ fall back to the email CTA.
    /// </summary>
    public string? ContactUrl { get; set; }

    /// <summary>
    /// Optional subject line for the <c>mailto:</c> CTA. <c>{0}</c> is substituted
    /// with the event display name. Blank ⇒ a sensible default subject is used.
    /// </summary>
    public string? EmailSubjectFormat { get; set; }
}

/// <summary>
/// The resolved CTA to render: a label + an href (either a built <c>mailto:</c> or
/// the external contact URL) and a flag for whether it opens a new tab (external
/// URL) vs. the same context (mailto). Built by <see cref="BecomeSponsorCtaBuilder"/>.
/// </summary>
public sealed record BecomeSponsorCta(string Href, bool IsExternal);

/// <summary>
/// Pure (no DB / no clock / no I/O) builder for the public "become a sponsor" CTA.
/// Given the bound <see cref="BecomeSponsorOptions"/> + the active edition's display
/// name, it returns the href to use — preferring a configured external contact URL,
/// otherwise a well-formed <c>mailto:</c> with an event-stamped subject — or
/// <c>null</c> when nothing is configured (the page then shows no CTA).
///
/// Unit-testable in isolation so the precedence (URL over email), the mailto subject
/// substitution, and the "nothing configured ⇒ no CTA" contract are all pinned.
/// </summary>
public static class BecomeSponsorCtaBuilder
{
    /// <summary>The default mailto subject when none is configured. <c>{0}</c> = event name.</summary>
    public const string DefaultSubjectFormat = "Sponsorship enquiry — {0}";

    /// <summary>
    /// Build the CTA href, or <c>null</c> when neither a contact URL nor a contact
    /// email is configured. A configured external URL wins over the email.
    /// </summary>
    /// <param name="options">The bound config (may be null / empty).</param>
    /// <param name="eventDisplayName">Active edition name, substituted into the mailto subject.</param>
    public static BecomeSponsorCta? Build(BecomeSponsorOptions? options, string? eventDisplayName)
    {
        if (options is null) return null;

        var url = Trim(options.ContactUrl);
        if (url is not null)
        {
            return new BecomeSponsorCta(url, IsExternal: true);
        }

        var email = Trim(options.ContactEmail);
        if (email is not null)
        {
            var subjectFormat = Trim(options.EmailSubjectFormat) ?? DefaultSubjectFormat;
            var subject = string.Format(subjectFormat, eventDisplayName ?? string.Empty).Trim();
            var href = subject.Length > 0
                ? $"mailto:{email}?subject={Uri.EscapeDataString(subject)}"
                : $"mailto:{email}";
            return new BecomeSponsorCta(href, IsExternal: false);
        }

        return null;
    }

    private static string? Trim(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
