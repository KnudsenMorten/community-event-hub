using System.Text.RegularExpressions;

namespace CommunityHub.Core.Email;

/// <summary>
/// Converts the lightweight task-description markup (the same authors write in
/// sponsor.&lt;edition&gt;.json and that the web renders via TaskTextLinkifier:
/// <c>**bold**</c>, <c>__underline__</c>, <c>[label](url)</c>, and "* " bullets)
/// into readable PLAIN TEXT for non-HTML consumers — the ICS <c>DESCRIPTION</c>
/// field and any plain-text reminder body. Without this the raw <c>**</c>/<c>__</c>
/// markers leak verbatim into the calendar entry.
/// </summary>
public static class TaskMarkup
{
    private static readonly Regex MdLink = new(
        @"\[([^\]\n]+?)\]\((https?://[^\s)]+)\)", RegexOptions.Compiled);
    private static readonly Regex Bold = new(
        @"\*\*(?=\S)([^*\n]+?)\*\*", RegexOptions.Compiled);
    private static readonly Regex Underline = new(
        @"__(?=\S)([^_\n]+?)__", RegexOptions.Compiled);
    private static readonly Regex Bullet = new(
        @"(?m)^[ \t]*[*-][ \t]+", RegexOptions.Compiled);

    /// <summary>Strip the inline markup to readable plain text.</summary>
    public static string ToPlainText(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var t = MdLink.Replace(s, "$1: $2"); // "label: https://…"
        t = Bold.Replace(t, "$1");
        t = Underline.Replace(t, "$1");
        t = Bullet.Replace(t, "• ");
        return t;
    }
}
