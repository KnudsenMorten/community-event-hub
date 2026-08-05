namespace CommunityHub.Core.Domain;

/// <summary>Which kind of document-library setting a row overrides.</summary>
public enum DocLibrarySettingKind
{
    /// <summary>A folder path, relative to the configured root — <c>DocLibrary:Paths:{Key}</c>.</summary>
    Path = 0,

    /// <summary>A static FILE name inside a folder — <c>DocLibrary:FileNames:{Key}</c>.</summary>
    FileName = 1,
}

/// <summary>
/// §769 — an operator's edit of ONE document-library path or file name, persisted so it takes effect
/// without a redeploy.
/// </summary>
/// <remarks>
/// <para>🔒 <b>This is the ONLY override layer.</b> Operator 2026-08-02 chose the database over the
/// <c>DocLibrary:Paths:*</c> app settings, which are retired: two places defining one folder is
/// exactly what §768 was raised to remove, and an Azure setting leaves no diff, no history and no
/// review trail. The layering is now: <b>registry default → this row → nothing else</b>, with the
/// ROOT alone remaining an app setting because it is the PROD/DEV boundary (§768.7, §769.1 D2).</para>
///
/// <para>⚠️ <b>No <c>EventId</c>, deliberately.</b> A folder belongs to the deployment, not to an
/// edition: the root is per host, and every edition on that host reads the same tree. Adding an
/// edition key here would imply two editions could point one key at two folders, which the single
/// configured root makes impossible anyway.</para>
///
/// <para>An absent row means the registered default applies — so a fresh install starts correct with
/// nothing seeded, and "restore default" is a DELETE rather than a write of the default's value.
/// Writing the default in would freeze it: a later change to the registry would then not reach the
/// installations that had "restored" it.</para>
/// </remarks>
public class DocLibrarySettingOverride
{
    public int Id { get; set; }

    /// <summary>Path or file name.</summary>
    public DocLibrarySettingKind Kind { get; set; }

    /// <summary>The registered key, e.g. <c>SpeakerPhotos</c>.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>The operator's value. Never blank — a blank edit deletes the row instead.</summary>
    public string Value { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The organizer who last changed it.</summary>
    public string? UpdatedByEmail { get; set; }
}

/// <summary>
/// §769 — the CHANGE HISTORY the work order asks for: who changed what, when, from what to what.
/// </summary>
/// <remarks>
/// 🔑 <b>Append-only, and it outlives the override row.</b> "Restore default" deletes the override —
/// without a separate history table the fact that a path was ever changed would vanish with it, and
/// the question this page exists to answer ("when did this folder move, and who moved it?") would be
/// unanswerable exactly when it matters. A null <see cref="NewValue"/> IS the restore-to-default
/// event, and a null <see cref="OldValue"/> is the first edit away from the default.
/// </remarks>
public class DocLibrarySettingChange
{
    public int Id { get; set; }

    public DocLibrarySettingKind Kind { get; set; }

    public string Key { get; set; } = string.Empty;

    /// <summary>What it was — null when the registered default was in force.</summary>
    public string? OldValue { get; set; }

    /// <summary>What it became — null when it was restored to the registered default.</summary>
    public string? NewValue { get; set; }

    public DateTimeOffset ChangedAt { get; set; } = DateTimeOffset.UtcNow;

    public string? ChangedByEmail { get; set; }
}
