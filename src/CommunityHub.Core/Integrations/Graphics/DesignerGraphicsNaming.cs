using System.Text;

namespace CommunityHub.Core.Integrations.Graphics;

/// <summary>
/// Pure, deterministic naming + sanitisation for the external-designer pipeline (REQUIREMENTS
/// §165). Speaker photos are NAMED BY THE SPEAKER (<c>Firstname-Lastname.jpg</c>) and build
/// folders by the session / master-class / track TITLE. Everything here is traversal-safe: no
/// produced name can contain a path separator, <c>..</c>, or an OS/SharePoint-invalid character,
/// so a hostile title / name can never escape its folder.
/// </summary>
public static class DesignerGraphicsNaming
{
    // Known image extensions we will carry through from a photo URL; anything else ⇒ .jpg.
    private static readonly IReadOnlySet<string> ImageExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".gif", ".webp" };

    // Characters SharePoint / Windows reject in a file or folder leaf.
    private static readonly char[] InvalidLeafChars = "/\\:*?\"<>|".ToCharArray();

    /// <summary>
    /// The sanitised NAME BASE for a speaker (no extension, no de-dupe suffix): name tokens
    /// (letters/digits only) joined by single dashes, e.g. <c>"Firstname Lastname"</c> →
    /// <c>"Firstname-Lastname"</c>. Falls back to <c>speaker-{id}</c> when the name yields nothing
    /// (blank, or only punctuation/traversal characters). NEVER contains a separator or <c>..</c>.
    /// </summary>
    public static string SanitizeNameBase(string? fullName, int participantId)
    {
        var tokens = (fullName ?? string.Empty)
            .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(KeepAlphanumeric)
            .Where(t => t.Length > 0)
            .ToList();

        return tokens.Count == 0 ? $"speaker-{participantId}" : string.Join('-', tokens);
    }

    /// <summary>
    /// The collision-safe photo FILE NAME for a speaker: the sanitised base (+ a stable
    /// <c>-{participantId}</c> suffix when <paramref name="appendIdSuffix"/>, i.e. another speaker
    /// shares the base) + an extension carried from <paramref name="sourceUrl"/> (a known image
    /// extension) or <c>.jpg</c>. Deterministic — the same inputs always yield the same name.
    /// </summary>
    public static string BuildPhotoFileName(string baseName, string? sourceUrl, int participantId, bool appendIdSuffix)
    {
        var stem = appendIdSuffix ? $"{baseName}-{participantId}" : baseName;
        return stem + ExtensionFromUrl(sourceUrl);
    }

    /// <summary>
    /// A safe folder-leaf for a session / track title: invalid leaf chars and control chars
    /// dropped, whitespace collapsed to single spaces, leading/trailing dots + spaces trimmed,
    /// and any <c>..</c> neutralised. Falls back to <c>Untitled</c> when nothing usable remains.
    /// The result can never traverse out of its parent.
    /// </summary>
    public static string SanitizeFolderSegment(string? title)
    {
        var sb = new StringBuilder((title ?? string.Empty).Length);
        var prevSpace = false;
        foreach (var ch in title ?? string.Empty)
        {
            if (char.IsControl(ch) || Array.IndexOf(InvalidLeafChars, ch) >= 0)
            {
                continue;
            }
            if (char.IsWhiteSpace(ch))
            {
                if (!prevSpace && sb.Length > 0) sb.Append(' ');
                prevSpace = true;
                continue;
            }
            sb.Append(ch);
            prevSpace = false;
        }

        var cleaned = sb.ToString().Replace("..", string.Empty).Trim().Trim('.').Trim();
        return cleaned.Length == 0 ? "Untitled" : cleaned;
    }

    /// <summary>
    /// Derive a parent-FOLDER web URL from a contained FILE's web URL by dropping the last path
    /// segment (the file leaf). Used to surface an organizer-only folder link in the brief without
    /// an extra Graph call. Returns the input unchanged when it has no resolvable parent.
    /// </summary>
    public static string FolderUrlFromFileUrl(string? fileWebUrl)
    {
        if (string.IsNullOrWhiteSpace(fileWebUrl)) return string.Empty;

        var url = fileWebUrl.Trim();
        var queryIndex = url.IndexOfAny(new[] { '?', '#' });
        var basePart = queryIndex >= 0 ? url[..queryIndex] : url;

        var lastSlash = basePart.TrimEnd('/').LastIndexOf('/');
        return lastSlash <= "https://".Length ? url : basePart[..lastSlash];
    }

    private static string KeepAlphanumeric(string token)
    {
        var sb = new StringBuilder(token.Length);
        foreach (var ch in token)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        }
        return sb.ToString();
    }

    private static string ExtensionFromUrl(string? sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl)) return ".jpg";
        try
        {
            // Strip query/fragment, take the last path segment, read its extension.
            var path = Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri) ? uri.AbsolutePath : sourceUrl;
            var ext = Path.GetExtension(path);
            return ImageExtensions.Contains(ext) ? ext.ToLowerInvariant() : ".jpg";
        }
        catch
        {
            return ".jpg";
        }
    }
}
