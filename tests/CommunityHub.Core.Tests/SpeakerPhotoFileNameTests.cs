using CommunityHub.Core.Integrations.Graphics;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §767 / §768.16 — pins the speaker-photo naming convention, <c>speaker-photo-{ParticipantId}</c>,
/// from BOTH ends: what the writers build, and what a reader may still meet in the folder.
/// </summary>
/// <remarks>
/// 🔒 <b>This test exists because its absence shipped a silent failure.</b> The §767 sweep assumed the
/// §165 designer shape <c>Firstname-Lastname.jpg</c>, ran green in production for four scheduled runs,
/// and matched NOTHING — no error, no exception, just "0 track GIF(s)". The suite was green
/// throughout, because nothing in it had ever stated what the folder is actually called.
///
/// <para>🔒 <b>The legacy names below are NOT leftovers to delete.</b> Operator 2026-08-02 —
/// <i>"use id only (not speaker name and id)"</i> — changed what is WRITTEN; a sponsor-uploaded photo
/// is never re-archived (§764.1), so those files keep the old name indefinitely and must keep
/// parsing. They are the shapes verified in the live folder on 2026-08-01.</para>
/// </remarks>
public sealed class SpeakerPhotoFileNameTests
{
    [Theory]
    // §768.16 — the CURRENT convention: the id is the whole body.
    [InlineData("speaker-photo-73", 73, "")]
    [InlineData("speaker-photo-100", 100, "")]
    // LEGACY, still in the folder: the ordinary case as the archive used to write it.
    [InlineData("speaker-photo-Morten-Knudsen-73", 73, "Morten-Knudsen")]
    [InlineData("speaker-photo-Dan-Toft-54", 54, "Dan-Toft")]
    // Diacritics survive both writers' sanitisers and must survive the parse.
    [InlineData("speaker-photo-Jörgen-Nilsson-39", 39, "Jörgen-Nilsson")]
    [InlineData("speaker-photo-Morten-Bøtkjær-Nilsen-53", 53, "Morten-Bøtkjær-Nilsen")]
    // A THREE-part name: only the trailing segment is the id.
    [InlineData("speaker-photo-Morten-Leth-Hedegaard-10", 10, "Morten-Leth-Hedegaard")]
    // A name that CONTAINS digits — the exact file that would break a naive "first number wins".
    [InlineData("speaker-photo-2linkit-speaker-firstname-lastname-100", 100,
        "2linkit-speaker-firstname-lastname")]
    // The sponsor form lower-cases and leaves repeated dashes; that shape must parse too.
    [InlineData("speaker-photo-anna--berg-12", 12, "anna--berg")]
    public void Parses_the_archived_shape(string bare, int expectedId, string expectedName)
    {
        Assert.True(SpeakerPhotoFileName.TryParse(bare, out var id, out var name));
        Assert.Equal(expectedId, id);
        Assert.Equal(expectedName, name);
    }

    [Theory]
    // A HUMAN's file under the §165 designer convention — not archive-named, matched by NAME instead.
    [InlineData("Morten-Knudsen")]
    [InlineData("")]
    [InlineData("speaker-photo-")]
    // Prefix present but no trailing id: must NOT be read as an id-keyed file.
    [InlineData("speaker-photo-Morten-Knudsen")]
    // A zero / negative id is not a participant.
    [InlineData("speaker-photo-Morten-Knudsen-0")]
    [InlineData("speaker-photo-0")]
    [InlineData("speaker-photo--73")]      // an empty name part is neither shape
    public void Rejects_everything_else(string bare)
    {
        Assert.False(SpeakerPhotoFileName.TryParse(bare, out var id, out var name));
        Assert.Equal(0, id);
        Assert.Equal(string.Empty, name);
    }

    /// <summary>
    /// 🔒 §768.16 — the BUILD side. Before this, the convention was parsed in one place and FORMATTED
    /// in two, which is how the writers and the registry came to disagree in the first place.
    /// </summary>
    [Theory]
    [InlineData(73, ".jpg", "speaker-photo-73.jpg")]
    [InlineData(61, "png", "speaker-photo-61.png")]      // a missing dot is added
    [InlineData(42, ".JPG", "speaker-photo-42.jpg")]     // and the extension is lower-cased
    [InlineData(7, "", "speaker-photo-7")]               // no extension ⇒ no trailing dot
    public void Build_names_a_photo_by_id_alone(int id, string ext, string expected)
    {
        Assert.Equal(expected, SpeakerPhotoFileName.Build(id, ext));
    }

    /// <summary>The round trip the old design could not state: what is written is what is read.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(73)]
    [InlineData(2026)]
    public void Build_round_trips_through_TryParse_with_NO_name_part(int id)
    {
        var built = SpeakerPhotoFileName.Build(id, ".jpg");

        Assert.True(SpeakerPhotoFileName.TryParse(built[..built.LastIndexOf('.')], out var parsed, out var name));
        Assert.Equal(id, parsed);
        Assert.Equal(string.Empty, name);           // 🔒 the name is GONE, not merely unused
        Assert.True(SpeakerPhotoFileName.IsCurrentConvention(built));
    }

    /// <summary>
    /// What the archive asks before deciding a photo needs no work: is the stored file already on the
    /// current convention? A legacy name must answer NO, or it is frozen for ever.
    /// </summary>
    [Theory]
    [InlineData("speaker-photo-73.jpg", true)]
    [InlineData("Speakers/Photos/speaker-photo-73.jpg", true)]      // a stored path, not just a leaf
    [InlineData("speaker-photo-73", true)]                          // extensionless
    [InlineData("speaker-photo-Morten-Knudsen-73.jpg", false)]      // legacy ⇒ re-archive once
    [InlineData("Morten-Knudsen.jpg", false)]                       // a human's §165 file
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsCurrentConvention_answers_the_re_archive_question(string? path, bool expected)
    {
        Assert.Equal(expected, SpeakerPhotoFileName.IsCurrentConvention(path));
    }
}
