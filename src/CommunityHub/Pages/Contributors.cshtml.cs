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
/// </summary>
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

    public record Contributor(string Name, string Role, string? LinkedIn, string Tag);
}
