using CommunityHub.Core.Integrations;

namespace CommunityHub.Core.Domain;

/// <summary>
/// §824.2C — one edition's OVERRIDE of a shipped SoMe post template.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-04: <i>"SoMe post editor - where i can edit a post … Post editor must
/// support SoMe post templates, where we have 5 templates (type 1-5)"</i>.</para>
///
/// <para>🔒 <b>A row exists ONLY when he has changed something.</b> No row means the shipped default
/// in <see cref="SoMeTemplateCatalog"/> is in force — so improving a default reaches every edition
/// that has not deliberately overridden it, and "reset" is a DELETE rather than a copy of today's
/// default frozen into the database. Seeding all five rows per edition up front would silently pin
/// each edition to whatever the wording happened to be on the day it was created.</para>
///
/// <para>The same reasoning the e-mail templates use: ship the default in code, store only the
/// divergence.</para>
/// </remarks>
public class SoMeTemplate
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>Which of the five post types this overrides.</summary>
    public SoMeTemplateKind Kind { get; set; }

    /// <summary>
    /// The post body, with <c>{Variable}</c> placeholders and emojis exactly as he typed them.
    /// </summary>
    /// <remarks>
    /// ⚠️ Stored verbatim — no trimming of interior whitespace, no newline normalisation. The blank
    /// line between blocks IS the layout on LinkedIn, and emoji are ordinary text here; a "helpful"
    /// clean-up would silently restyle a post he had already approved.
    /// </remarks>
    public string Body { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? LastUpdatedByEmail { get; set; }
}
