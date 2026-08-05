namespace CommunityHub.Core.Integrations.DocLibrary;

/// <summary>
/// §769 / work-order §6.1 — the static FILE names that must be settings rather than compiled-in
/// strings or guesses.
/// </summary>
/// <remarks>
/// <para>Work order §3.2, on <c>EventGraphicsTemplate</c>: <i>"Path and both filenames must be
/// editable settings on the Organizer page (§6.1)"</i>.</para>
///
/// <para>🔑 <b>What these replace.</b> The graphics build picks its two template assets out of the
/// folder by GUESSING — the first image whose name contains "logo" is the wordmark, and the first
/// other image is the background. That works until somebody adds a third image, or names the
/// background <c>Company-logo-backdrop.png</c>, at which point the artwork silently renders with the
/// wrong asset. §768 established the opposite rule for this library: <b>the folder is the source of
/// truth, typos included</b> — which only works if the exact name can be written down.</para>
///
/// <para>⚠️ <b>Unset is still legitimate.</b> With no value the build keeps its existing heuristic,
/// so nothing changes for an installation that has never opened this page. The setting is the way to
/// be exact when being exact matters.</para>
/// </remarks>
public static class DocLibraryFileNames
{
    /// <summary>The background/canvas image inside <c>EventGraphicsTemplate</c>.</summary>
    public const string TemplateBackground = nameof(TemplateBackground);

    /// <summary>
    /// The white LOGO overlaid on generated artwork.
    /// </summary>
    /// <remarks>
    /// 🔒 §783.11 — the STORAGE KEY keeps the word "wordmark" because it is what any already-saved
    /// setting row is keyed on, and renaming it would silently orphan that value and drop the build
    /// back to guessing. The operator-facing LABEL is "Logo" (see <see cref="Editable"/>). Operator
    /// 2026-08-03: <i>"i prefer logo as name instead, none knows what wordmark is"</i>.
    /// </remarks>
    public const string TemplateWordmark = nameof(TemplateWordmark);

    /// <summary>
    /// §783.11 — the SHIPPED default file name per key: what the folder actually contains today.
    /// </summary>
    /// <remarks>
    /// <para>🔒 <b>Why a named default and not the old heuristic.</b> The heuristic was "the first
    /// image whose name contains 'logo'" and "the first other image". Operator 2026-08-03:
    /// <i>"it must be named specific to avoid critical mistake"</i> — and he is right, because the
    /// failure is silent: a third image in the folder, or a background that happens to have "logo"
    /// in its name, renders every generated graphic with the wrong asset and reports success.</para>
    ///
    /// <para>⚠️ These are matched case-insensitively and byte-for-byte otherwise. The real names
    /// carry an UPPERCASE extension (<c>Template.JPG</c>) and spaces plus an underscore
    /// (<c>LOGO EXPERTS LIVE denmark WHITE_no shadow.png</c>) — none of which may be trimmed,
    /// collapsed or normalised, per §768's "the folder is the source of truth, typos included".</para>
    ///
    /// <para>An explicit setting still WINS over this default; the default only replaces the guess.</para>
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, string> ShippedDefaults =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [TemplateBackground] = "Template.JPG",
            [TemplateWordmark] = "LOGO EXPERTS LIVE denmark WHITE_no shadow.png",
        };

    /// <summary>The shipped default file name for a key, or null when it has none.</summary>
    public static string? DefaultFor(string key) =>
        ShippedDefaults.TryGetValue(key ?? string.Empty, out var name) ? name : null;

    /// <summary>
    /// Every editable file-name key, with its operator-facing LABEL and what it is for, in page
    /// order. §783.11 — the label is what the page shows; the key is what the value is stored under.
    /// </summary>
    public static readonly IReadOnlyList<(string Key, string Label, string Purpose)> Editable =
    [
        (TemplateBackground, "Background image",
            "The background image every generated graphic is drawn on. Leave blank to use the "
            + "shipped default below."),
        (TemplateWordmark, "Logo",
            "The white logo laid over generated graphics. Leave blank to use the shipped default "
            + "below."),
    ];
}
