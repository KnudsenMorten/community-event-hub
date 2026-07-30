using CommunityHub.Core.Email;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §180 — the edition code must be a POSTFIX, exactly ONCE, at the END of EVERY
/// outbound subject (operator asked many times: "[ELDK27] must be a postfix, not a
/// prefix"). <see cref="BrevoEmailSender.NormalizeSubject"/> is the single send
/// chokepoint that guarantees it for templated AND inline-built emails. It strips any
/// existing "[CODE]" token (prefix/inline/postfix), collapses whitespace, appends one
/// " [CODE]" at the end, is idempotent, and preserves a leading "[EXT]" gateway tag.
/// </summary>
public sealed class BrevoEmailSenderSubjectNormalizationTests
{
    private const string Code = "ELDK27";

    [Fact]
    public void No_code_subject_gets_the_postfix()
        => Assert.Equal("Your evaluation is ready [ELDK27]",
            BrevoEmailSender.NormalizeSubject("Your evaluation is ready", Code));

    [Fact]
    public void A_prefix_code_is_moved_to_the_end()
        => Assert.Equal("Your evaluation is ready [ELDK27]",
            BrevoEmailSender.NormalizeSubject("[ELDK27] Your evaluation is ready", Code));

    [Fact]
    public void An_already_postfixed_subject_is_unchanged()
        => Assert.Equal("Your evaluation is ready [ELDK27]",
            BrevoEmailSender.NormalizeSubject("Your evaluation is ready [ELDK27]", Code));

    [Fact]
    public void The_code_is_never_doubled()
        => Assert.Equal("Hi [ELDK27]",
            BrevoEmailSender.NormalizeSubject("[ELDK27] Hi [ELDK27]", Code));

    [Fact]
    public void An_inline_code_is_pulled_out_to_the_end()
        => Assert.Equal("Alpha Beta [ELDK27]",
            BrevoEmailSender.NormalizeSubject("Alpha [ELDK27] Beta", Code));

    [Fact]
    public void Whitespace_left_by_a_removed_tag_is_collapsed()
        => Assert.Equal("A B [ELDK27]",
            BrevoEmailSender.NormalizeSubject("A   [ELDK27]   B", Code));

    [Fact]
    public void An_empty_subject_becomes_just_the_tag()
        => Assert.Equal("[ELDK27]", BrevoEmailSender.NormalizeSubject("", Code));

    [Fact]
    public void A_blank_event_code_falls_back_to_ELDK27()
        => Assert.Equal("Hello [ELDK27]", BrevoEmailSender.NormalizeSubject("Hello", "  "));

    [Fact]
    public void A_custom_event_code_is_honoured()
        => Assert.Equal("Hello [ELDK28]",
            BrevoEmailSender.NormalizeSubject("[ELDK28] Hello", "ELDK28"));
}
