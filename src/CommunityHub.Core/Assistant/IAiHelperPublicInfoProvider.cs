namespace CommunityHub.Core.Assistant;

/// <summary>
/// Supplies PUBLIC event information that is relevant to EVERY role (operator 2026-06-27,
/// REQUIREMENTS §149): the published speaker lineup (name + skills/tagline/bio + the
/// sessions they present), the full session catalogue (title / type / time / room /
/// speakers), and the event SCHEDULE / key times (doors open, lunch, breaks, party,
/// appreciation dinner …).
///
/// <para>Unlike <see cref="IAiHelperOrganizerOpsProvider"/> (organizer-gated), this
/// provider's sections are added to the grounding for ALL roles, so anyone can ask
/// "who is speaking about X?" or "when does lunch start?". It is therefore restricted to
/// PUBLIC, non-personal data only: it MUST honour the speaker publish HARD GATE
/// (selected-for-publish + active + speaker role — never an unselected speaker) and emit
/// only published catalogue + general logistics times — no per-person rows.</para>
///
/// Implemented in the web layer over EF Core; faked in tests. Optional: when not wired,
/// the grounding builder simply omits these sections.
/// </summary>
public interface IAiHelperPublicInfoProvider
{
    /// <summary>
    /// The public speakers/sessions/schedule grounding sections for an edition (may be
    /// empty when the lineup/grid/schedule are not published yet). Read-only.
    /// </summary>
    Task<IReadOnlyList<AiHelperGroundingSection>> GetPublicInfoAsync(
        int eventId, CancellationToken ct = default);
}
