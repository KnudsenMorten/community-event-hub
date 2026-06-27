namespace CommunityHub.Core.Assistant;

/// <summary>
/// Supplies ORGANIZER-ONLY operational aggregates as already-scoped grounding sections
/// for the assistant's ops mode (REQUIREMENTS §133). The implementation answers ops
/// questions — speaker readiness / missing slides (§134), sponsor missing deliverables
/// (§135), master-class non-selections, and participation / task / attendee counts
/// (§132 data) — via CURATED, TYPED, READ-ONLY aggregate queries over the mirror + crew
/// data. It is NEVER free-form text-to-SQL and never writes.
///
/// <para><b>SECURITY — authorization at retrieval.</b> This provider is the organizer-only
/// grounding source. The gate lives in <see cref="AiHelperGroundingBuilder"/>, which calls it
/// ONLY when the SERVER-resolved caller role is <see cref="Domain.ParticipantRole.Organizer"/>
/// (mirrors the §69 organizer-only telemetry rule). A non-organizer's grounding never
/// invokes it, so ops/aggregate data is unreachable for non-organizers by construction —
/// the gate is in this retrieval layer, never in the model prompt.</para>
/// </summary>
public interface IAiHelperOrganizerOpsProvider
{
    /// <summary>
    /// Build the organizer ops aggregates for the edition as grounding sections. Read-only.
    /// Implementations must be robust (one failing aggregate must not break the rest) and
    /// must never throw to the builder.
    /// </summary>
    Task<IReadOnlyList<AiHelperGroundingSection>> GetOpsAggregatesAsync(
        int eventId, CancellationToken ct = default);
}
