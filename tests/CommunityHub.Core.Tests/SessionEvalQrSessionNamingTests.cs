using CommunityHub.Core.Evaluation;
using CommunityHub.Core.Integrations.Graphics;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §749.2 — the stored QR files are keyed on the SESSION, not the room. Operator 2026-07-31:
/// <i>"current is linked to room name but now we use sessionname"</i>.
/// </summary>
/// <remarks>
/// <para>§748 had already made the QR a property of the session rather than the room (<i>"1 per
/// session"</i>) — the room-name lookup was the last piece still describing the retired model.</para>
///
/// <para>🔒 The parser is deliberately strict, and most of these facts pin the REFUSALS. A loose rule
/// that bound a printed code to the wrong session would be invisible until the day of the event, and
/// a QR opening someone else's feedback form is worse than one that does not resolve at all.</para>
///
/// <para>🔑 The two halves are asserted against EACH OTHER: the name the generator writes must be the
/// name the reader parses. A test that only checked the parser against hand-typed strings would keep
/// passing if the generator's format changed.</para>
/// </remarks>
public sealed class SessionEvalQrSessionNamingTests
{
    // ---- the round trip: generator ↔ parser ----------------------------------------------------

    /// <summary>
    /// 🔑 The headline. Whatever <see cref="SessionQrCodeService.FileName"/> produces must parse back
    /// to the same session id — that pairing IS the naming contract, and it is what breaks silently
    /// if either side is changed alone.
    /// </summary>
    [Theory]
    [InlineData(17, "Keeping identity boring")]
    [InlineData(3, "AI, security & everything else!")]
    [InlineData(9001, "Sal A — keynote")]
    [InlineData(42, null)]
    [InlineData(8, "   ")]
    public void A_generated_file_name_parses_back_to_its_own_session_id(int id, string? title)
    {
        var name = SessionQrCodeService.FileName(id, title, "png");

        Assert.Equal(id, SessionEvalsQrService.SessionIdFromFileName(name));
    }

    [Fact]
    public void A_title_that_slugs_to_nothing_still_carries_a_parseable_id()
    {
        // The generator falls back to "session-{id}-qr.png" — the id must survive that branch too,
        // which is the one a punctuation-only title takes.
        var name = SessionQrCodeService.FileName(55, "!!! ???", "png");

        Assert.Equal("session-55-qr.png", name);
        Assert.Equal(55, SessionEvalsQrService.SessionIdFromFileName(name));
    }

    // ---- refusing correctly --------------------------------------------------------------------

    /// <summary>
    /// 🔒 The legacy per-ROOM files (§124) are still sitting in the folder. They must match NOTHING.
    /// A room-name fallback would hand a speaker the code for whoever else used their room.
    /// </summary>
    [Theory]
    [InlineData("Room-16-Floor-1-Device13.png")]
    [InlineData("Sal A.png")]
    [InlineData("room-1-qr.png")]
    public void A_legacy_room_named_file_yields_NO_session_id(string legacy)
    {
        Assert.Null(SessionEvalsQrService.SessionIdFromFileName(legacy));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("session-.png")]          // no digits at all
    [InlineData("session-abc-qr.png")]    // not a number
    [InlineData("sessions-12-qr.png")]    // a different prefix
    [InlineData("12-qr.png")]             // bare id, no prefix — too loose to accept
    public void A_name_that_is_not_the_generator_s_format_yields_NO_session_id(string? name)
    {
        Assert.Null(SessionEvalsQrService.SessionIdFromFileName(name));
    }

    /// <summary>
    /// 🔒 The id must END its segment. Accepting a prefix match would file session 12's QR under
    /// session 1 — the kind of off-by-one that only shows up once ids reach two digits.
    /// </summary>
    [Fact]
    public void An_id_glued_to_other_characters_is_refused_not_truncated()
    {
        Assert.Null(SessionEvalsQrService.SessionIdFromFileName("session-12x-qr.png"));
        Assert.Equal(12, SessionEvalsQrService.SessionIdFromFileName("session-12-qr.png"));
        Assert.Equal(1, SessionEvalsQrService.SessionIdFromFileName("session-1-qr.png"));
    }

    [Fact]
    public void The_prefix_match_is_case_insensitive_because_a_store_may_normalise_it()
    {
        Assert.Equal(7, SessionEvalsQrService.SessionIdFromFileName("SESSION-7-QR.PNG"));
    }

    /// <summary>
    /// Two sessions in the SAME room get DIFFERENT file names — the whole point of the change, and
    /// the case the old room-keyed naming collapsed into one file.
    /// </summary>
    [Fact]
    public void Two_sessions_in_one_room_no_longer_collide()
    {
        var morning = SessionQrCodeService.FileName(1, "Morning talk", "png");
        var afternoon = SessionQrCodeService.FileName(2, "Afternoon talk", "png");

        Assert.NotEqual(morning, afternoon);
        Assert.Equal(1, SessionEvalsQrService.SessionIdFromFileName(morning));
        Assert.Equal(2, SessionEvalsQrService.SessionIdFromFileName(afternoon));
    }
}
