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

        var body = bare[Prefix.Length..];

        // The CURRENT shape — the whole body is the id.
        if (int.TryParse(body, out participantId) && participantId > 0) return true;

        // 🔴 §1132 — the NAME ALIAS, `volunteer-photo-{Name}-{id}`. This branch is REQUIRED, not a
        // nicety: ParticipantPhotoCleanupService deletes a volunteer's photos by the id this method
        // parses, so without it the alias survives the person's deactivation — a photo of someone
        // who asked to be removed, left in SharePoint. Only the TRAILING segment is tried as the id,
        // because names legitimately contain digits.
        participantId = 0;
        var lastDash = body.LastIndexOf('-');
        if (lastDash <= 0) return false;

        return int.TryParse(body[(lastDash + 1)..], out participantId) && participantId > 0;
    }

    /// <summary>
    /// §1132 — the NAME ALIAS: a SECOND file per volunteer, named after the person, written
    /// ALONGSIDE the authoritative <see cref="Build"/> file. Null when the name sanitises to nothing.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-25: <i>"same for volunteers that upload pics, could be geat to have 2
    /// files - one with id and another with name"</i>.</para>
    ///
    /// <para>🔒 Same shape and same reasoning as
    /// <see cref="SpeakerPhotoFileName.BuildAlias"/> — including the trailing id, which keeps two
    /// volunteers with the same name from overwriting each other. The sanitiser is shared with the
    /// speaker one so the two folders can never disagree about how a name becomes a file name.</para>
    /// </remarks>
    public static string? BuildAlias(int participantId, string? fullName, string? extension)
    {
        var name = SpeakerPhotoFileName.SanitiseName(fullName);
        if (name.Length == 0) return null;

        var ext = (extension ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(ext)) ext = ".jpg";
        if (!ext.StartsWith('.')) ext = "." + ext;

        return $"{Prefix}{name}-{participantId}{ext}";
    }

    /// <summary>
    /// Does this file CARRY AN ID — the current <c>volunteer-photo-{id}</c> file or its §1132
    /// <c>volunteer-photo-{Name}-{id}</c> alias?
    /// </summary>
    /// <remarks>
    /// ⚠️ Since §1132 this is broader than its name suggests, and the ONE caller
    /// (<c>ParticipantPhotoCleanupService</c>) wants exactly the broader meaning: it uses this to
    /// exclude files already matched by id from the ambiguous name-keyed branch. An alias belongs in
    /// the id branch — it is deleted unambiguously — so counting it as "carries an id" is correct
    /// rather than a convenient coincidence.
    /// </remarks>
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
