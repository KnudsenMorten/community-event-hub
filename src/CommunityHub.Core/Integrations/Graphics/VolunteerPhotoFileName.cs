namespace CommunityHub.Core.Integrations.Graphics;

/// <summary>
/// §769.9 — the ONE place that knows how a volunteer photo is named:
/// <c>volunteer-photo-{ParticipantId}{.ext}</c>.
/// </summary>
/// <remarks>
/// <para>🔒 <b>Operator 2026-08-02: <i>"volunteer pictures switch to id to make consistent"</i></b> —
/// the same decision as §768.16 made for speaker photos, for the same reasons, one folder along.</para>
///
/// <para><b>Why a name could never work here.</b> The old convention was <c>{Full Name}.ext</c>, and
/// it fails in two ways that matter: it does not survive a rename, and two volunteers with the same
/// name collide on ONE file. The second one bit immediately — §6.9's cleanup has to DELETE a
/// departing volunteer's photo, and with name-keyed files "Ada Lovelace" leaving would take the
/// active Ada Lovelace's picture with her. The id makes the match exact, so the deletion can be
/// exact.</para>
///
/// <para>⚠️ <b><see cref="TryParse"/> stays tolerant of the legacy name shape</b> — the existing
/// files in <c>Volunteers/Photo</c> carry it, and a volunteer's photo is only rewritten when they
/// upload again. A reader that understood only the new shape would stop seeing every photo already
/// there, which is the §767 failure exactly.</para>
///
/// <para>The extension follows the SOURCE (§768.9 D3): a JPEG is never written under a .png name.</para>
/// </remarks>
public static class VolunteerPhotoFileName
{
    /// <summary>The prefix every id-named volunteer photo carries.</summary>
    public const string Prefix = "volunteer-photo-";

    /// <summary>Characters SharePoint refuses in a file name.</summary>
    private const string Illegal = "\"*:<>?/\\|";

    /// <summary>The legacy fallback leaf, when a name sanitised to nothing.</summary>
    public const string LegacyFallback = "Volunteer";

    /// <summary>
    /// The file name for one volunteer's photo — <c>volunteer-photo-{participantId}{.ext}</c>.
    /// </summary>
    public static string Build(int participantId, string? extension)
    {
        var ext = (extension ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(ext)) ext = ".jpg";
        if (!ext.StartsWith('.')) ext = "." + ext;
        return $"{Prefix}{participantId}{ext}";
    }

    /// <summary>
    /// Read a stored file name. Returns the participant id for the CURRENT shape; for a legacy
    /// <c>{Full Name}.ext</c> file it returns false, and the caller matches it by name instead.
    /// </summary>
    public static bool TryParse(string? fileName, out int participantId)
    {
        participantId = 0;
        if (string.IsNullOrWhiteSpace(fileName)) return false;

        var bare = fileName.Trim();
        var dot = bare.LastIndexOf('.');
        if (dot > 0) bare = bare[..dot];

        if (!bare.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;

        return int.TryParse(bare[Prefix.Length..], out participantId) && participantId > 0;
    }

    /// <summary>Is this the CURRENT id-based convention?</summary>
    public static bool IsCurrentConvention(string? fileName) => TryParse(fileName, out _);

    /// <summary>
    /// The LEGACY name part, exactly as the old upload wrote it (illegal characters out, spaces
    /// kept). Kept only so the files already in the folder can still be recognised.
    /// </summary>
    public static string LegacyNameComponent(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return LegacyFallback;
        var cleaned = new string(name.Where(c => Illegal.IndexOf(c) < 0).ToArray());
        cleaned = string.Join(" ", cleaned.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(cleaned) ? LegacyFallback : cleaned;
    }

    /// <summary>Does this file belong to this volunteer — under either convention?</summary>
    public static bool Matches(string? fileName, int participantId, string? fullName)
    {
        if (TryParse(fileName, out var id)) return id == participantId;

        if (string.IsNullOrWhiteSpace(fileName)) return false;
        var bare = fileName.Trim();
        var dot = bare.LastIndexOf('.');
        if (dot > 0) bare = bare[..dot];
        return string.Equals(bare, LegacyNameComponent(fullName), StringComparison.OrdinalIgnoreCase);
    }
}
