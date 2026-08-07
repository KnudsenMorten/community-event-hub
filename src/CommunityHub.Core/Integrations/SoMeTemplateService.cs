using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Integrations;

/// <summary>One template as the editor shows it.</summary>
/// <param name="Kind">Which of the five types.</param>
/// <param name="Title">Human label for the picker.</param>
/// <param name="Body">The body in force — the override when one exists, else the shipped default.</param>
/// <param name="IsOverridden">True when this edition has its own version.</param>
/// <param name="UpdatedAt">When it was last changed here (null while shipped).</param>
/// <param name="UpdatedByEmail">Who last changed it (null while shipped).</param>
/// <param name="UnknownPlaceholders">
/// Placeholders nothing can resolve — a typo caught before it publishes, not after.
/// </param>
public sealed record SoMeTemplateView(
    SoMeTemplateKind Kind,
    string Title,
    string Body,
    bool IsOverridden,
    DateTimeOffset? UpdatedAt,
    string? UpdatedByEmail,
    IReadOnlyList<string> UnknownPlaceholders);

/// <summary>
/// §824.2C — reads and writes the five per-edition SoMe post templates.
/// </summary>
/// <remarks>
/// <para>🔒 <b>Shipped default in code, only the divergence in the database.</b> No row means the
/// <see cref="SoMeTemplateCatalog"/> default is live, so improving a default reaches every edition
/// that has not deliberately overridden it — and <see cref="ResetAsync"/> is a DELETE, not a copy of
/// today's default frozen into a row. Seeding five rows per edition would pin each edition to
/// whatever the wording happened to be the day it was created.</para>
/// </remarks>
public sealed class SoMeTemplateService
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public SoMeTemplateService(CommunityHubDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>The five types in his order (1–5), each with the body actually in force.</summary>
    public async Task<IReadOnlyList<SoMeTemplateView>> ListAsync(
        int eventId, CancellationToken ct = default)
    {
        var overrides = await _db.SoMeTemplates
            .Where(t => t.EventId == eventId)
            .ToDictionaryAsync(t => t.Kind, ct);

        return Enum.GetValues<SoMeTemplateKind>()
            .OrderBy(k => (int)k)
            .Select(k =>
            {
                overrides.TryGetValue(k, out var row);
                var body = row?.Body ?? SoMeTemplateCatalog.DefaultBody(k);
                return new SoMeTemplateView(
                    k, SoMeTemplateCatalog.Title(k), body,
                    IsOverridden: row is not null,
                    UpdatedAt: row?.UpdatedAt ?? row?.CreatedAt,
                    UpdatedByEmail: row?.LastUpdatedByEmail,
                    UnknownPlaceholders: SoMeTemplateRenderer.UnknownPlaceholders(body));
            })
            .ToList();
    }

    /// <summary>The body in force for one type — what the composer renders.</summary>
    public async Task<string> BodyAsync(
        int eventId, SoMeTemplateKind kind, CancellationToken ct = default)
    {
        var row = await _db.SoMeTemplates
            .Where(t => t.EventId == eventId && t.Kind == kind)
            .Select(t => t.Body)
            .FirstOrDefaultAsync(ct);

        return string.IsNullOrWhiteSpace(row) ? SoMeTemplateCatalog.DefaultBody(kind) : row;
    }

    /// <summary>
    /// Save an edition's own version. Returns the placeholders it uses that nothing can resolve.
    /// </summary>
    /// <remarks>
    /// <para>⚠️ <b>Unknown placeholders do NOT block the save.</b> He may be mid-edit, and a page that
    /// refuses to keep his work because a token is half-typed loses the work. They are returned so
    /// the page can warn — visibly, next to the field — and the same check runs again before a post
    /// is queued, where refusing is cheap.</para>
    ///
    /// <para>🔒 Saving a body identical to the shipped default REMOVES the override rather than
    /// storing a duplicate: otherwise an edition that was merely "looked at" silently stops tracking
    /// improvements to the default.</para>
    /// </remarks>
    public async Task<IReadOnlyList<string>> SaveAsync(
        int eventId, SoMeTemplateKind kind, string body, string? byEmail,
        CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var row = await _db.SoMeTemplates
            .FirstOrDefaultAsync(t => t.EventId == eventId && t.Kind == kind, ct);

        // Verbatim: interior whitespace and newlines ARE the layout on LinkedIn. Only the outer
        // edges are trimmed, because a trailing newline is never intentional in a post body.
        var cleaned = (body ?? string.Empty).Trim();

        if (string.Equals(cleaned, SoMeTemplateCatalog.DefaultBody(kind).Trim(), StringComparison.Ordinal))
        {
            if (row is not null) _db.SoMeTemplates.Remove(row);
            await _db.SaveChangesAsync(ct);
            return Array.Empty<string>();
        }

        if (row is null)
        {
            _db.SoMeTemplates.Add(new SoMeTemplate
            {
                EventId = eventId, Kind = kind, Body = cleaned,
                CreatedAt = now, UpdatedAt = now, LastUpdatedByEmail = byEmail,
            });
        }
        else
        {
            row.Body = cleaned;
            row.UpdatedAt = now;
            row.LastUpdatedByEmail = byEmail;
        }

        await _db.SaveChangesAsync(ct);
        return SoMeTemplateRenderer.UnknownPlaceholders(cleaned);
    }

    /// <summary>Drop this edition's override so the shipped default is live again.</summary>
    public async Task<bool> ResetAsync(
        int eventId, SoMeTemplateKind kind, CancellationToken ct = default)
    {
        var row = await _db.SoMeTemplates
            .FirstOrDefaultAsync(t => t.EventId == eventId && t.Kind == kind, ct);
        if (row is null) return false;

        _db.SoMeTemplates.Remove(row);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Render one template against SAMPLE values, so the editor can show what a post will look like
    /// without needing a real sponsor or session picked first.
    /// </summary>
    /// <remarks>
    /// The sample values are obviously fake (<c>Contoso</c>, <c>Sample Speaker</c>) on purpose: a
    /// preview using a REAL sponsor's name is one screenshot away from looking like a post that
    /// already went out.
    /// </remarks>
    public static string PreviewWithSamples(string body) =>
        SoMeTemplateRenderer.Render(body, new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["TrackName"] = "AI",
            ["SpeakerNames"] = "Sample Speaker | Another Speaker",
            ["SessionTitle"] = "A sample session title",
            ["IntroText"] = "(the AI-generated intro appears here)",
            ["SponsorName"] = "Contoso",
            ["SponsorLinkedInUrl"] = "Contoso",
            ["SponsorTier"] = "Gold",
            ["SponsorList"] = "Contoso | Fabrikam",
            ["SponsorWebsite"] = "contoso.com",
            ["SponsorHashtag"] = "#Contoso",
            ["SponsorSocialMediaCompanyDescription"] = "(the sponsor's own description from CEH)",
            ["EventDisplayName"] = "Experts Live Denmark 2027",
            ["EventDates"] = "9+10 February 2027",
            ["EventVenue"] = "Bella Center, Copenhagen",
            ["EventSystemUrl"] = "https://eldk27.expertslive.dk",
            ["EventTags"] = "#ELDK27 #ExpertsLiveDK …",
            ["EditionCode"] = "ELDK27",
            ["OrganizerLinkedInUrls"] = "Organizer One | Organizer Two",
            // §935 — his spelling needs its own sample, or the preview renders a literal
            // "{Organizers}". Caught by the "preview leaves no braces" test the moment the shipped
            // Footer switched spellings — the same class of miss as the KnownVariables gap.
            ["Organizers"] = "Organizer One | Organizer Two",
            // §858.16 / §884.3 — the mention variables. The samples show BOTH outcomes on purpose:
            // roughly a quarter of speakers and half of sponsor contacts cannot be mentioned at all
            // (LinkedIn only tags people who follow the page), so a preview showing every name as a
            // tag would set an expectation the live post cannot meet.
            ["Speakers"] = "Sample Speaker | Another Speaker (not tagged — does not follow the page)",
            ["SponsorSigner"] = "Sample Signer",
            ["SponsorEventCoordinators"] = "Sample Coordinator | Another Coordinator",
            // §885 — one phrase from his own catalog. The sample says so, because a preview showing
            // a fixed sentence would hide the fact that each post draws a different one.
            ["Action_catalog_random"] = "(one call-to-action phrase from your catalog)",
            // §888.2 — same values as EditionCode / EventDisplayName above, under his names.
            ["EventNameShort"] = "ELDK27",
            ["EventNameLong"] = "Experts Live Denmark 2027",
            ["EventVenueCityCountry"] = "Bella Center, Copenhagen, Denmark",
        });
}
