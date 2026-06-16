namespace CommunityHub.Core.Email;

/// <summary>
/// A reusable, organizer-facing broadcast message template: a starting point an
/// organizer picks, then edits freely before sending. These are <b>not</b> the
/// branded HTML shells in <c>templates/emails</c> (those are the layout the
/// message is poured into); a <see cref="BroadcastTemplate"/> is just the
/// human-typed subject + body text, with simple <c>{Token}</c> placeholders the
/// organizer can keep or remove.
///
/// Deliberately code constants, not a database table: the built-in set ships
/// with the app, needs no migration, and the organizer always customises the
/// loaded text before sending, so there is nothing per-edition to persist.
/// </summary>
public sealed record BroadcastTemplate(
    string Key,
    string DisplayName,
    string Subject,
    string Body);

/// <summary>
/// The built-in broadcast templates and the <c>{Token}</c> substitution shared
/// by the preview and the send path. Pure and side-effect free, so it is unit
/// tested without a database, an app, or SMTP.
/// </summary>
public static class BroadcastTemplates
{
    /// <summary>
    /// The personalisation tokens an organizer may use in a broadcast subject or
    /// body. They are substituted per recipient at send time (and with a sample
    /// value in the preview). The set is intentionally tiny — a broadcast is one
    /// message to many people, so only per-person basics are available.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> TokenHelp =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["FirstName"] = "the recipient's first name (falls back to \"there\")",
            ["EventName"] = "the edition's display name",
        };

    /// <summary>The built-in starting points, in display order.</summary>
    public static readonly IReadOnlyList<BroadcastTemplate> BuiltIn = new[]
    {
        new BroadcastTemplate(
            Key: "blank",
            DisplayName: "Blank message",
            Subject: "",
            Body: ""),

        // NOTE: the branded broadcast shell already renders a personal
        // "Hi {firstName}," header, so the bodies below do NOT repeat the
        // greeting. The {FirstName}/{EventName} tokens are still available to
        // organizers anywhere in the subject or body if they want them.
        new BroadcastTemplate(
            Key: "generic",
            DisplayName: "Generic announcement",
            Subject: "An update from the {EventName} team",
            Body:
                "We wanted to share a quick update with you.\n\n" +
                "[ Write your message here. ]\n\n" +
                "Thanks,\nThe {EventName} team"),

        new BroadcastTemplate(
            Key: "reminder",
            DisplayName: "Friendly reminder",
            Subject: "A quick reminder for {EventName}",
            Body:
                "Just a friendly reminder ahead of {EventName}.\n\n" +
                "[ Tell people what to do and by when. ]\n\n" +
                "If you have already taken care of this, thank you — you can " +
                "ignore this message.\n\n" +
                "Thanks,\nThe {EventName} team"),

        new BroadcastTemplate(
            Key: "welcome",
            DisplayName: "Welcome / introduction",
            Subject: "Welcome to {EventName}",
            Body:
                "Welcome — we are glad to have you as part of {EventName}.\n\n" +
                "[ Introduce what happens next and where to find things. ]\n\n" +
                "See you soon,\nThe {EventName} team"),
    };

    /// <summary>Find a built-in template by its key, or null if unknown.</summary>
    public static BroadcastTemplate? Find(string? key) =>
        string.IsNullOrWhiteSpace(key)
            ? null
            : BuiltIn.FirstOrDefault(
                t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Substitute <c>{Token}</c> placeholders in organizer-typed text with the
    /// supplied values. Case-insensitive on the token name; an unknown token is
    /// left exactly as typed (so a stray "{foo}" survives instead of vanishing,
    /// which is friendlier when an organizer mistypes a token). The values are
    /// substituted verbatim — callers that emit HTML must HTML-encode the result
    /// (the broadcast page encodes the whole body when it builds paragraphs).
    /// </summary>
    public static string Substitute(
        string text, IReadOnlyDictionary<string, string> values)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;

        return System.Text.RegularExpressions.Regex.Replace(
            text,
            @"\{(\w+)\}",
            m => values.TryGetValue(m.Groups[1].Value, out var v)
                ? v
                : m.Value);
    }
}
