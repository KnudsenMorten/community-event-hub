namespace CommunityHub.Core.Integrations.DocLibrary;

/// <summary>
/// §769 / work-order §6.1 — what an operator is allowed to type into a path or file-name field.
/// </summary>
/// <remarks>
/// <para>🔒 <b>Every rule here is a silent failure that has already happened, or is one edit away.</b>
/// A document-library path does not fail loudly when it is wrong: the store returns an EMPTY LISTING
/// and every caller reads that as "nothing to do" (§768, §767 — four green production runs against a
/// folder full of photos). So the last moment a bad value can be caught with a human present is the
/// save; after that it is invisible until someone notices a missing graphic.</para>
///
/// <para>⚠️ The two that look like tidiness and are not: a <b>leading slash</b> makes the path
/// absolute from the drive root and silently escapes the configured root — the PROD/DEV boundary
/// (§768.7) — and <b>doubled separators</b> produce an empty segment that Graph resolves to a
/// different folder than the eye reads. Both present as "folder not found".</para>
/// </remarks>
public static class DocLibraryValueValidator
{
    /// <summary>Characters SharePoint/OneDrive refuse in a file or folder name.</summary>
    /// <remarks>
    /// <c>/</c> is legal here and NOT in this set — it is the path separator. <c>\</c> is refused
    /// rather than silently translated: a Windows-shaped path pasted into this box is a mistake worth
    /// telling the operator about, not one to guess the intent of.
    /// </remarks>
    public const string IllegalCharacters = "\"*:<>?\\|";

    /// <summary>
    /// Validate a folder path RELATIVE to the configured root. Returns null when it is acceptable,
    /// otherwise the message to show the operator.
    /// </summary>
    public static string? ValidatePath(string? value)
    {
        var v = (value ?? string.Empty).Trim();

        if (v.Length == 0)
            return "Enter a folder path, or use Restore default to go back to the built-in value.";

        if (v.StartsWith('/') || v.StartsWith('\\'))
            return "Remove the leading slash — the path is relative to the configured root, and a "
                 + "leading slash escapes it to the drive root.";

        if (v.EndsWith('/') || v.EndsWith('\\'))
            return "Remove the trailing slash.";

        if (v.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || v.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return "Paste the folder path, not a URL — everything here is relative to the configured "
                 + "root (the part after the document library).";

        if (v.Contains("..", StringComparison.Ordinal))
            return "'..' is not allowed — a path may not climb out of the configured root.";

        if (v.Contains("//", StringComparison.Ordinal))
            return "Remove the doubled slash — an empty folder name resolves to a different folder "
                 + "than it reads as.";

        var illegal = v.Where(c => IllegalCharacters.Contains(c)).Distinct().ToArray();
        if (illegal.Length > 0)
            return $"These characters are not allowed in a folder name: {string.Join(' ', illegal)}";

        if (v.Any(char.IsControl))
            return "The path contains a control character — retype it rather than pasting.";

        if (v.Split('/').Any(seg => seg.Trim().Length == 0))
            return "One of the folder names is blank.";

        if (v.Split('/').Any(seg => seg != seg.Trim()))
            return "A folder name starts or ends with a space. SharePoint silently trims those, so "
                 + "the folder you get is not the one you typed.";

        if (v.Length > 400)
            return "That path is too long (400 characters maximum).";

        return null;
    }

    /// <summary>
    /// Validate a static FILE name (work-order §6.1: the template background and wordmark are
    /// settings). Same rules, minus the separator: a file name has no folders in it.
    /// </summary>
    public static string? ValidateFileName(string? value)
    {
        var v = (value ?? string.Empty).Trim();

        if (v.Length == 0)
            return "Enter a file name, or use Restore default.";

        if (v.Contains('/') || v.Contains('\\'))
            return "This is a file NAME, not a path — it lives in the folder above.";

        var illegal = v.Where(c => IllegalCharacters.Contains(c)).Distinct().ToArray();
        if (illegal.Length > 0)
            return $"These characters are not allowed in a file name: {string.Join(' ', illegal)}";

        if (v.Any(char.IsControl))
            return "The file name contains a control character — retype it rather than pasting.";

        if (!v.Contains('.'))
            return "Include the extension — the file name must match the file in the library exactly.";

        if (v.Length > 255)
            return "That file name is too long (255 characters maximum).";

        return null;
    }
}
