namespace CommunityHub.Core.Integrations.Graphics;

/// <summary>
/// §767 / §768.16 — the ONE place that knows how files in the speaker-photo folder are named:
/// <c>speaker-photo-{ParticipantId}{.ext}</c>, e.g. <c>speaker-photo-73.jpg</c>.
/// </summary>
/// <remarks>
/// <para>🔒 <b>Why this class exists.</b> The convention was WRITTEN in two places
/// (<see cref="SpeakerPhotoArchiveService"/> and the sponsor session form) and READ in a third, which
/// assumed the §165 designer shape <c>Firstname-Lastname.jpg</c> instead. Nothing failed: the §767
/// sweep ran green in production for four scheduled runs and matched not one photo of twenty-two.
/// A convention duplicated across three files is a convention nobody owns — so it is named here, and
/// every writer composes through <see cref="Build"/> while every reader parses through
/// <see cref="TryParse"/>. Neither side formats or splits a string of its own.</para>
///
/// <para>🔒 <b>§768.16 — the name was DROPPED; the id alone identifies the file.</b> Operator
/// 2026-08-02: <i>"use id only (not speaker name and id)"</i>. The extension still follows the
/// SOURCE (§768.9 D3) — a JPEG must never be written under a <c>.png</c> name.</para>
///
/// <para>⚠️ <b><see cref="TryParse"/> stays TOLERANT of the older <c>speaker-photo-{Name}-{id}</c>
/// shape, permanently — this is not a transition window.</b> A sponsor-uploaded photo is never
/// re-archived (§764.1 counts it as <i>already stored</i>), so those files keep the old name until
/// that sponsor uploads again, which may be never. A parser that understood only the new shape would
/// silently stop matching them, which is precisely the §767 failure mode. Only two things are
/// load-bearing either way: the <c>speaker-photo-</c> prefix and a trailing integer id; anything
/// between them is a legacy display name and is not interpreted.</para>
///
/// <para>The id is what makes a match EXACT: it survives a speaker renaming themselves, which any
/// name-based match silently does not.</para>
/// </remarks>
public static class SpeakerPhotoFileName
{
    /// <summary>The prefix every archived speaker photo carries.</summary>
    public const string Prefix = "speaker-photo-";

    /// <summary>
    /// The file name for one speaker's photo: <c>speaker-photo-{participantId}{.ext}</c>.
    /// </summary>
    /// <param name="extension">
    /// The SOURCE extension, with or without its leading dot. Kept as-is in kind (§768.9 D3) and
    /// lower-cased; a blank extension yields a bare name rather than a trailing dot.
    /// </param>
    /// <remarks>
    /// 🔒 The name part is deliberately NOT a parameter. Making it optional is how a second
    /// convention gets reintroduced by a caller that "just this once" wants a readable folder.
    /// </remarks>
    public static string Build(int participantId, string? extension)
    {
        var ext = (extension ?? string.Empty).Trim().ToLowerInvariant();
        if (ext.Length > 0 && !ext.StartsWith('.')) ext = "." + ext;
        return $"{Prefix}{participantId}{ext}";
    }

    /// <summary>
    /// Parse an archived photo's file name (extension already stripped) into the participant id and
    /// the name part — <see cref="string.Empty"/> for the current id-only convention. Returns false
    /// for anything else — including a HUMAN's file dropped in under the §165
    /// <c>Firstname-Lastname.jpg</c> convention, which the caller then matches by name.
    /// </summary>
    public static bool TryParse(string bareFileName, out int participantId, out string name)
    {
        participantId = 0;
        name = string.Empty;

        if (string.IsNullOrWhiteSpace(bareFileName)) return false;
        if (!bareFileName.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;

        var body = bareFileName[Prefix.Length..];
        var lastDash = body.LastIndexOf('-');

        // The CURRENT shape — the whole body is the id, no name part at all.
        if (lastDash < 0) return TryId(body, out participantId);

        if (lastDash == 0) return false;   // "speaker-photo--73": no name, and not id-only either

        // The LEGACY shape. Only the TRAILING segment is tried as the id — names legitimately
        // contain digits ("speaker-photo-2linkit-speaker-firstname-lastname-100" is a real file).
        if (!TryId(body[(lastDash + 1)..], out participantId)) return false;

        name = body[..lastDash];
        return true;
    }

    /// <summary>
    /// Is <paramref name="fileNameOrPath"/> already on the CURRENT id-only convention? Used by the
    /// archive to decide whether a photo it has otherwise nothing to do for still needs re-writing
    /// under its new name — without that, an unchanged source URL would freeze every legacy name in
    /// the folder for ever.
    /// </summary>
    public static bool IsCurrentConvention(string? fileNameOrPath)
    {
        if (string.IsNullOrWhiteSpace(fileNameOrPath)) return false;

        var leaf = fileNameOrPath.Trim().Replace('\\', '/');
        var slash = leaf.LastIndexOf('/');
        if (slash >= 0) leaf = leaf[(slash + 1)..];

        var dot = leaf.LastIndexOf('.');
        if (dot > 0) leaf = leaf[..dot];

        return TryParse(leaf, out _, out var name) && name.Length == 0;
    }

    private static bool TryId(string candidate, out int participantId)
    {
        if (!int.TryParse(candidate, out participantId) || participantId <= 0)
        {
            participantId = 0;
            return false;
        }
        return true;
    }
}
