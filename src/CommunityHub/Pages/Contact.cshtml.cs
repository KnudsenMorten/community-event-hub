using CommunityHub.Content;
using CommunityHub.Core.Config;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages;

/// <summary>
/// "Contact Organizers" (operator 2026-06-21) — a single signed-in page that
/// surfaces how to reach the event organizers. The address comes from the edition
/// config (<c>placeholders.organizerEmail</c>) so it stays per-edition.
///
/// §107: the page also renders the organizer-team PROFILE cards (photo, bio, email,
/// phone, website, LinkedIn) authored in <c>config/content/&lt;edition&gt;/organizers.md</c>.
/// The copy stays in the Markdown (operator-editable); we PARSE it into structured
/// <see cref="OrganizerCard"/>s here so the view can render proper cards (matching the
/// public board-organizers layout) with clickable email / phone / website / LinkedIn.
/// </summary>
[Authorize]
public class ContactModel : PageModel
{
    private const string OrganizersSlug = "organizers";

    private readonly EventEditionConfigLoader _cfg;
    private readonly EventConfigOptions _opt;
    private readonly ContentMarkdownRenderer _renderer;

    public ContactModel(
        EventEditionConfigLoader cfg,
        EventConfigOptions opt,
        ContentMarkdownRenderer renderer)
    {
        _cfg = cfg;
        _opt = opt;
        _renderer = renderer;
    }

    public string OrganizerEmail { get; private set; } = "info@expertslive.dk";

    /// <summary>One organizer's card (any field may be null/blank → that row is hidden).</summary>
    public sealed record OrganizerCard(
        string Name, string? PhotoUrl, string? Bio,
        string? Email, string? Phone, string? Website, string? LinkedIn);

    /// <summary>The parsed organizer-team cards (§107), in authoring order.</summary>
    public List<OrganizerCard> Organizers { get; private set; } = new();

    public void OnGet()
    {
        try
        {
            var c = _cfg.Load(_opt.EventConfigPath);
            if (c.Placeholders.TryGetValue("organizerEmail", out var e) && !string.IsNullOrWhiteSpace(e))
                OrganizerEmail = e.Trim();
        }
        catch { /* keep the fallback address */ }

        var md = _renderer.TryReadMarkdown(OrganizersSlug);
        if (!string.IsNullOrWhiteSpace(md))
            Organizers = ParseOrganizers(md);
    }

    /// <summary>
    /// Parse the operator-authored organizers.md into cards. Each <c>## Name</c> heading
    /// starts a card; the following <c>- Field: value</c> bullet lines fill it
    /// (Photo / Bio / Email / Phone / Website / LinkedIn — case-insensitive). Anything
    /// before the first heading (the intro line) is ignored.
    /// </summary>
    private static List<OrganizerCard> ParseOrganizers(string md)
    {
        var cards = new List<OrganizerCard>();
        string? name = null, photo = null, bio = null, email = null, phone = null, web = null, li = null;

        void Flush()
        {
            if (name is not null)
                cards.Add(new OrganizerCard(name, photo, bio, email, phone, web, li));
            name = photo = bio = email = phone = web = li = null;
        }

        foreach (var raw in md.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                name = line[3..].Trim();
                continue;
            }
            if (name is null || !line.StartsWith("-", StringComparison.Ordinal)) continue;

            var body = line.TrimStart('-', ' ').Replace("**", "");
            var colon = body.IndexOf(':');
            if (colon <= 0) continue;
            var key = body[..colon].Trim().ToLowerInvariant();
            var val = body[(colon + 1)..].Trim();
            if (val.Length == 0 || val == "(link)") continue;

            switch (key)
            {
                case "photo": photo = val; break;
                case "bio": bio = val; break;
                case "email": email = val; break;
                case "phone": phone = val; break;
                case "website" or "web" or "blog": web = val; break;
                case "linkedin": li = val; break;
            }
        }
        Flush();
        return cards;
    }
}
