using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages;

/// <summary>
/// Contributors / acknowledgements page. Lists the Experts Live Denmark
/// organizer team plus every external contributor who helped shape the
/// hub (code, design, content, sponsor outreach, etc). Public page --
/// no auth required -- so contributors get credit visible to anyone
/// browsing the event site.
///
/// The list is hand-curated here (vs read from a config file) because:
///   - The set changes once per year-ish, not per deploy.
///   - Sorting + grouping requires editorial judgement (organizer vs
///     contributor; alphabetical within group), which is awkward to
///     express in a JSON file.
/// Editing this list is a one-line code change with a tag-driven
/// publish; that's a fair trade for the clarity.
///
/// Public credits page: opt OUT of the fail-closed FallbackPolicy so anonymous
/// visitors can see it (operator 2026-06-27). No personal data beyond name +
/// public LinkedIn + role, which is the whole point of a credits page.
/// </summary>
[AllowAnonymous]
public class ContributorsModel : PageModel
{
    /// <summary>Organizer team -- the people running the event year-round.</summary>
    public List<Contributor> Organizers { get; } = new()
    {
        new("Morten Knudsen",          "Microsoft MVP -- Security \u00b7 Azure \u00b7 Security Copilot", "https://www.linkedin.com/in/knudsenmorten/",                      "Organizer"),
        new("Martin Byskov",           "Microsoft MVP -- Modern Workplace",                              "https://www.linkedin.com/in/byskov",                                "Organizer"),
        new("Morten Leth Hedegaard",   "Microsoft MVP -- Modern Workplace",                              "https://www.linkedin.com/in/morten-leth-hedegaard-37756820/",       "Organizer"),
        new("Kent Agerlund",           "Microsoft MVP -- Enterprise Mobility",                           "https://www.linkedin.com/in/kentagerlund",                          "Organizer"),
    };

    /// <summary>External contributors who helped the hub in any way.</summary>
    public List<Contributor> Contributors { get; } = new()
    {
        new("Laura Gulbe",             "Software Central",                                                "https://www.linkedin.com/in/lauragulbe/",                           "Contributor"),
    };

    /// <summary>
    /// One credited person. <paramref name="PhotoUrl"/> is optional (null = no
    /// photo, the default for everyone today) so an organizer can later add a
    /// headshot URL per person without a schema change; the view shows initials
    /// when it is absent. No real photo URLs are committed here.
    /// </summary>
    public record Contributor(
        string Name, string Role, string? LinkedIn, string Tag, string? PhotoUrl = null);

    /// <summary>
    /// Up-to-two-letter initials for a person, used as the avatar fallback when
    /// no <see cref="Contributor.PhotoUrl"/> is set. First letter of the first +
    /// last whitespace-separated word (single-word names give one letter);
    /// returns "?" for a blank name so the avatar is never empty.
    /// </summary>
    public static string Initials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var parts = name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1) return parts[0][..1].ToUpperInvariant();
        return (parts[0][..1] + parts[^1][..1]).ToUpperInvariant();
    }
}
