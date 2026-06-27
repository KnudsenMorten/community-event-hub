using CommunityHub.Core.Integrations;

namespace CommunityHub.Pages.Shared;

/// <summary>
/// View model for the shared <c>_AttendeeTelemetryPanel</c> partial (§55) — the
/// ranked breakdown tables + segment + topic-filter dropdowns rendered IDENTICALLY
/// on the public anonymous page (<c>/attendee-telemetry</c>), the sponsor in-area
/// page (<c>/Sponsor/Telemetry</c>) and the organizer page (<c>/Organizer/Telemetry</c>).
///
/// All three pages build their <see cref="AttendeeTelemetry"/> from the same
/// <see cref="AttendeeTelemetryService"/> and hand it to this single partial, so
/// the markup, controls and tables can never drift apart.
/// </summary>
/// <param name="Data">
/// The aggregate telemetry for the chosen segment + optional topic filter, or
/// <c>null</c> when the live Zoho feed is unavailable (the partial shows a friendly
/// "not available" notice).
/// </param>
/// <param name="Segments">The sponsor-relevant analysis segments for the dropdown.</param>
/// <param name="IsOrganizer">
/// True only on the organizer surface (<c>/Organizer/Telemetry</c>). Gates the §69
/// organizer-only tables (e.g. "Top companies") so they are NEVER shown on the public
/// or sponsor pages.
/// </param>
public sealed record AttendeeTelemetryPanelModel(
    AttendeeTelemetry? Data,
    IReadOnlyList<TelemetrySegment> Segments,
    bool IsOrganizer = false);
