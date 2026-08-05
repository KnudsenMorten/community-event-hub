using CommunityHub.Core.Integrations;

namespace CommunityHub.Core.Domain;

/// <summary>
/// §842.2 — HOW OFTEN ONE POST TYPE IS ANNOUNCED, per edition, operator-editable.
///
/// <para>Operator 2026-08-05: <i>"i need a smart way to manage cadence, frequency per post type as
/// organizer interface"</i>. The counts used to be <b>hardcoded</b> in
/// <c>SoMeScheduleService.CollectSubjectsAsync</c> (§824.1 — tracks ×2, sessions ×1, tiers ×2,
/// sponsors ×2), so changing one needed a deploy.</para>
///
/// <para>🔒 <b>A row is an OVERRIDE, not the truth.</b> Absent ⇒ the shipped §824.1 default applies,
/// exactly like <c>SoMeTemplate</c> overrides the shipped template body. That way an edition that
/// never opens the page behaves as it always did, and there is no seeding step to forget.</para>
///
/// <para>⚠️ <b>Lowering a frequency never deletes anything.</b> The scheduler adds and never curates
/// (§824.21a): a lower number means FEWER NEW posts on the next run, not the removal of posts he may
/// already have approved, edited or published.</para>
///
/// <para>🔒 <b>Type 5 (event posts) is deliberately NOT governed by this.</b> Its dates come from his
/// deck (§828.7/§834.4); a frequency setting would either be ignored or would override the dates he
/// wrote, and both are worse than not offering the knob.</para>
/// </summary>
public class SoMeCadenceSetting
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>Which of the five post types this governs.</summary>
    public SoMeTemplateKind Kind { get; set; }

    /// <summary>
    /// Whether this type is planned at all. False stops the type without deleting a thing — the
    /// posts it already created stay exactly as they are.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How many times each subject of this type is announced (§824.1's "occurrences"). 0 is
    /// equivalent to disabling the type; the planner clamps rather than trusting it blindly.
    /// </summary>
    public int Occurrences { get; set; } = 1;

    public DateTimeOffset? UpdatedAt { get; set; }
    public string? LastUpdatedByEmail { get; set; }
}
