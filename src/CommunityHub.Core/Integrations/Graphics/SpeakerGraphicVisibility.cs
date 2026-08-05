using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Integrations.Graphics;

/// <summary>
/// THE ONE DEFINITION of "a graphic this speaker may see" (REQUIREMENTS §18, §767 phase 2).
/// </summary>
/// <remarks>
/// <para>🔒 <b>Why this is a shared filter and not three similar Where clauses.</b> Four surfaces
/// answer the same ownership question — the Help Promote page, the server-proxied download, the
/// LinkedIn publish, and the "your graphics are ready" mail — and they were four separate copies of
/// <c>ParticipantId == me</c>. §767 phase 2 keys a session graphic <c>session:{id}</c> with NO
/// participant, so widening one copy and not the others produces the worst possible outcome: a card
/// that appears on the page and then refuses to download or publish, with the mail either silent or
/// announcing something the page will not show. They now read the same rule.</para>
///
/// <para><b>The rule.</b> Released, never a sponsor graphic, and EITHER carrying the speaker's own
/// <c>ParticipantId</c> (speaker graphics, plus the per-speaker session/track rows the SharePoint
/// pull writes) OR belonging to a SESSION THEY SPEAK ON (the phase-2 shared session graphic — one
/// file, a frame per speaker, their face on it).</para>
///
/// <para>⚠️ The release gate stays exactly where it was. This widens WHICH ROWS belong to a
/// speaker, never whether an unreleased graphic can be seen. A speaker removed from a session stops
/// matching on the next query, with nothing to clean up.</para>
/// </remarks>
public static class SpeakerGraphicVisibility
{
    /// <summary>Every released graphic that belongs to this speaker, under both ownership rules.</summary>
    public static IQueryable<GraphicAsset> GraphicsVisibleToSpeaker(
        this CommunityHubDbContext db, int eventId, int participantId) =>
        db.GraphicAssets
            .Where(g => g.EventId == eventId
                        && g.Status == GraphicAssetStatus.Released   // THE GATE — unchanged
                        && g.Type != GraphicAssetType.Sponsor        // never sponsor-internal
                        && (g.ParticipantId == participantId
                            || (g.SessionId != null
                                && db.SessionSpeakers.Any(
                                    ss => ss.SessionId == g.SessionId!.Value
                                          && ss.ParticipantId == participantId))));
}
