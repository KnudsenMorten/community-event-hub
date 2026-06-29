using System.Text.Json;

namespace CommunityHub.Core.Domain;

/// <summary>
/// One question in a <see cref="Quiz"/> pool (REQUIREMENTS §171). The answer
/// options live as a JSON array of strings in <see cref="OptionsJson"/>;
/// <see cref="CorrectIndex"/> points at the correct option in that ORIGINAL order.
/// The play engine never sends the correct index to the browser and re-shuffles
/// the option order per attempt (anti-copy) — so the client only ever sees the
/// prompt + the options, never which one is right (server-authoritative).
/// </summary>
public class QuizQuestion
{
    public int Id { get; set; }

    public int QuizId { get; set; }
    public Quiz Quiz { get; set; } = null!;

    /// <summary>The question text shown to the player.</summary>
    public string Prompt { get; set; } = string.Empty;

    /// <summary>The answer options as a JSON array of strings, in their ORIGINAL
    /// order (the order <see cref="CorrectIndex"/> indexes into). Stored as an
    /// unbounded text column (cf. SyncDelta.ChangesJson) so the option count is
    /// open-ended; use <see cref="Options"/> to read/write it as a list.</summary>
    public string OptionsJson { get; set; } = "[]";

    /// <summary>The zero-based index of the correct option in the ORIGINAL
    /// <see cref="Options"/> order. Never leaves the server.</summary>
    public int CorrectIndex { get; set; }

    /// <summary>The short "why" shown after the player answers (the LEARNING bit) —
    /// it teaches, not just tests.</summary>
    public string Explanation { get; set; } = string.Empty;

    /// <summary>False = excluded from the draw (organizer can disable a single
    /// question without deleting it). Default true.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Authoring order in the organizer editor (lower first).</summary>
    public int SortOrder { get; set; }

    /// <summary>Convenience view over <see cref="OptionsJson"/> as a string list.
    /// Not mapped — reading deserializes, assigning serializes the JSON column.</summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public IReadOnlyList<string> Options
    {
        get => ParseOptions(OptionsJson);
        set => OptionsJson = SerializeOptions(value);
    }

    /// <summary>Parse an options JSON array to a list (empty on null/blank/bad JSON).</summary>
    public static IReadOnlyList<string> ParseOptions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Serialize an options list to the JSON column value.</summary>
    public static string SerializeOptions(IEnumerable<string>? options) =>
        JsonSerializer.Serialize((options ?? Array.Empty<string>()).ToList());
}
