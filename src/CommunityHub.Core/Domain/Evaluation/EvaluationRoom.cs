namespace CommunityHub.Core.Domain.Evaluation;

/// <summary>
/// §743 C2 — a room, and the hinge of the whole attribution chain: <b>device → room → session</b>.
/// </summary>
/// <remarks>
/// <para>🔴 <b>CEH's room is FREE TEXT</b> (<see cref="Session.Room"/> is a nullable string), so
/// these rows are created from whatever names the CEH sessions carry, matched on a normalised key
/// (trimmed, case- and whitespace-insensitive). That has a consequence worth stating before it
/// bites: <b>renaming a room in CEH creates a NEW room here</b>, and the device stays linked to the
/// old one — which silently stops attributing its presses.</para>
///
/// <para>🔒 The sync therefore <b>never moves a device by itself</b>. It creates rooms and reports
/// what it saw; re-pointing a physical unit is a human act, because the unit is a physical object
/// in a physical room and only a person knows where it actually is.</para>
/// </remarks>
public class EvaluationRoom
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>The room name as CEH spells it — shown to organisers exactly as stored.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The match key: <see cref="Name"/> trimmed, lower-cased, inner whitespace collapsed. Stored
    /// rather than computed so the unique index can enforce "one room per name per event" — and so
    /// "Room 1", "room  1" and "Room 1 " cannot become three rooms with one device between them.
    /// </summary>
    public string NameKey { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
}
