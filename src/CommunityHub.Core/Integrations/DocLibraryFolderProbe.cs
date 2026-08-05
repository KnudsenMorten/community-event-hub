namespace CommunityHub.Core.Integrations;

/// <summary>
/// §769 — the result of testing one document-library folder: does it exist, what is in it, and the
/// real error when it cannot be reached.
/// </summary>
/// <remarks>
/// 🔒 <b><see cref="Exists"/> and <see cref="FileCount"/> are separate on purpose.</b> Everywhere
/// else in this product a missing folder and an empty folder look identical — the store returns an
/// empty list and every caller reads "nothing to do". That equivalence is the single defect the
/// whole §768 audit exists to remove, so the one place built to TEST a folder must never collapse
/// them again.
/// </remarks>
/// <param name="Exists">The folder resolved. False ⇒ read <see cref="Error"/>.</param>
/// <param name="FileCount">Files directly inside (first 200).</param>
/// <param name="FolderCount">Sub-folders directly inside (first 200).</param>
/// <param name="LastModified">Newest child, else the folder's own stamp.</param>
/// <param name="Error">Why it failed, verbatim — null on success.</param>
public sealed record DocLibraryFolderProbe(
    bool Exists,
    int FileCount,
    int FolderCount,
    DateTimeOffset? LastModified,
    string? Error)
{
    /// <summary>A probe that could not run at all — configuration, permission, or an outage.</summary>
    public static DocLibraryFolderProbe Failed(string error) =>
        new(Exists: false, FileCount: 0, FolderCount: 0, LastModified: null, Error: error);

    /// <summary>True when the folder exists and holds nothing — a real state, not a failure.</summary>
    public bool IsEmpty => Exists && FileCount == 0 && FolderCount == 0;

    /// <summary>One line for the page, in the operator's terms.</summary>
    public string Summary =>
        !Exists ? (Error ?? "The folder could not be reached.")
        : IsEmpty ? "The folder exists and is empty."
        : $"{FileCount} file(s)"
          + (FolderCount > 0 ? $", {FolderCount} sub-folder(s)" : string.Empty)
          + (LastModified is { } m ? $" · newest {m.UtcDateTime:yyyy-MM-dd HH:mm} UTC" : string.Empty);
}
