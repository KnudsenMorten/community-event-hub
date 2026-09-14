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
    /// §1132 — the NAME ALIAS: a SECOND file per speaker, named after the person, written ALONGSIDE
    /// the authoritative <see cref="Build"/> file. Returns <c>null</c> when the name sanitises to
    /// nothing, in which case only the id file exists.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-25: <i>"when i need to find a photo file for a speaker and i dont know
    /// the speaker i have to look their id first (=extra work) … i would love to have 1 extra file
    /// per speaker on sharepoint with their name in … same folder, just 2 files"</i>, and
    /// — asked about collisions — he chose the name-plus-id shape.</para>
    ///
    /// <para>🔑 <b>THE SHAPE IS DELIBERATELY THE LEGACY ONE</b>, <c>speaker-photo-{Name}-{id}</c>,
    /// and that is what makes this safe rather than a re-run of the bug §768.16 fixed. Because
    /// <see cref="TryParse"/> already recognises it:</para>
    /// <list type="bullet">
    /// <item><b>The graphics builder indexes it by ID ONLY.</b> <c>SoMeBundleBuildService</c>
    /// <c>continue</c>s before the name-slug branch for anything this parses, and the id-only file
    /// overwrites it in the index regardless of listing order — so an alias can NEVER beat the real
    /// photo. Had the alias used a bare <c>{Name}.jpg</c> shape it would have registered a NAME key,
    /// and the name pass runs FIRST, so it would have won — for precisely the speakers mid-rename.
    /// That is the §768.16 defect exactly.</item>
    /// <item><b>Deactivation cleanup deletes it.</b> <c>ParticipantPhotoCleanupService</c> matches on
    /// the parsed id, so both files go. A bare-name alias would not parse and would be left behind —
    /// a photo of a deactivated person orphaned in SharePoint.</item>
    /// </list>
    ///
    /// <para>🔒 The id suffix is what makes two people with the same name safe: the alias is unique
    /// per participant, so one person's photo can never overwrite another's.</para>
    ///
    /// <para>⚠️ A RENAME leaves the old alias behind — the new one is written under the new name and
    /// nothing knows the previous one. That is accepted: the id file stays correct and authoritative,
    /// the stale alias is only a duplicate in a folder a human browses, and cleanup still removes
    /// every file carrying the id when the person is deactivated.</para>
    /// </remarks>
    public static string? BuildAlias(int participantId, string? fullName, string? extension)
    {
        var name = SanitiseName(fullName);
        if (name.Length == 0) return null;

        var ext = (extension ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(ext)) ext = ".jpg";
        if (!ext.StartsWith('.')) ext = "." + ext;

        return $"{Prefix}{name}-{participantId}{ext}";
    }

    /// <summary>
    /// §1132 — a name reduced to a safe, readable file-name component: <c>Morten Knudsen</c> →
    /// <c>Morten-Knudsen</c>. Empty when nothing usable survives.
    /// </summary>
    /// <remarks>
    /// <para>🔒 Danish letters are KEPT (æøå) — SharePoint accepts them and the whole point is that
    /// he recognises the name at a glance. Only what SharePoint actually refuses is stripped, plus
    /// the dot (which would read as an extension) and the run-together dashes that would make the
    /// trailing-id parse ambiguous.</para>
    ///
    /// <para>⚠️ §768.16 deleted the previous sanitiser with the note that <i>"a spare sanitiser
    /// sitting here is how the second convention comes back"</i>. This one is not spare — it has
    /// exactly one caller, <see cref="BuildAlias"/>, and the alias is now a deliberate, documented
    /// second file rather than a competing convention.</para>
    /// </remarks>
    public static string SanitiseName(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return string.Empty;

        const string illegal = "\"*:<>?/\\|.";
        var cleaned = new string(fullName.Where(c => illegal.IndexOf(c) < 0).ToArray());

        // Whitespace → single dashes, then collapse any run of dashes. A doubled dash would make
        // "where does the name end and the id begin" ambiguous for TryParse.
        var parts = cleaned.Split(
            new[] { ' ', '\t', '\r', '\n', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);

        return string.Join("-", parts).Trim('-');
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
