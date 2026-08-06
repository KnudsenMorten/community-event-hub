using CommunityHub.Core.Integrations;

namespace CommunityHub.Core.Domain;

/// <summary>
/// §908 — ONE OF MANY WAYS TO WORD A POST TYPE. A catalog, not an override.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-06: <i>"the speakersession is a catalog of samples, which you can
/// randomize to make new posts. this way it will be a mix of many different wordings"</i>. He
/// supplied 38 session wordings and 10 track wordings.</para>
///
/// <para>🔑 <b>Deliberately NOT <see cref="SoMeTemplate"/>.</b> That is the single body in force for
/// a type — one row per (edition, kind), the divergence from the shipped default. This is a POOL the
/// planner draws from, so thirteen session announcements do not open with the same sentence
/// thirteen times. Same relationship as <see cref="SoMeSettings.ActionCatalog"/> to a single
/// call-to-action (§885), one level up: there it is a phrase, here it is the whole body.</para>
///
/// <para>🔒 <b>The draw is DETERMINISTIC, from a stable hash of subject + occurrence — never
/// <see cref="Random"/>.</b> The planner re-plans on every tick (§848.2), so a real random draw
/// would re-word every un-approved post every five minutes: he would read a post, approve nothing,
/// come back and find it saying something else. The same post therefore always draws the same
/// sample, while different posts draw different ones — the §824.2E rule that already governs
/// scheduling, applied to wording.</para>
///
/// <para>⚠️ An empty catalog is an ordinary state: the planner falls back to the single
/// <see cref="SoMeTemplate"/> body / the shipped default, which is exactly today's behaviour.</para>
/// </remarks>
public class SoMeBodySample
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>Which of the five post types this is a wording for.</summary>
    public SoMeTemplateKind Kind { get; set; }

    /// <summary>
    /// The post body, with <c>{Variable}</c> placeholders and emoji exactly as written.
    /// </summary>
    /// <remarks>
    /// ⚠️ Stored verbatim, like <see cref="SoMeTemplate.Body"/> — the blank line between blocks IS
    /// the layout on LinkedIn, and a "helpful" clean-up would silently restyle every post drawn
    /// from it.
    /// </remarks>
    public string Body { get; set; } = string.Empty;

    /// <summary>
    /// Position in the catalog, so the pool has a stable order independent of database ids.
    /// </summary>
    /// <remarks>
    /// 🔒 The deterministic draw indexes into the ordered pool, so the order has to be a property of
    /// the DATA rather than of whatever order the rows happen to come back in. Re-importing the
    /// catalog in the same order therefore leaves every existing post's wording unchanged.
    /// </remarks>
    public int SortOrder { get; set; }

    /// <summary>Where this wording came from — a file name, or null when typed in.</summary>
    public string? SourceFileName { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? LastUpdatedByEmail { get; set; }
}
