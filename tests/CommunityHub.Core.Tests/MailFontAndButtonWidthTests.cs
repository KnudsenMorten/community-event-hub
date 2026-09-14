using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Organizer;
using CommunityHub.Core.Settings;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1089 — the speaker-approval mail arrived in Times New Roman with its buttons cut off
/// (operator 2026-08-19: <i>"email looks hard to read + font"</i>). Two independent defects in one
/// screenshot, pinned here so neither can come back quietly.
/// </summary>
public sealed class MailFontAndButtonWidthTests
{
    private const string FontMarker = "font-family:Aptos";

    // ---------------------------------------------------------------------
    //  1. The font — a substring test standing in for a semantic question
    // ---------------------------------------------------------------------

    /// <summary>
    /// 🔴 <b>THE REGRESSION, EXACTLY.</b> <c>ApplyDefaultFont</c> skipped any body containing the
    /// string <c>font-family</c>, meaning to skip mails that style themselves. A mail whose PROSE
    /// carries no font, but which contains one BUTTON (and every button styles its own label),
    /// matched — so the wrapper was skipped for the whole document and every paragraph fell back to
    /// the client's serif default.
    /// <para>🔑 The tell was in the screenshot: buttons sans-serif, prose serif, in one mail. One
    /// styled element deep in the body had opted the entire document out.</para>
    /// </summary>
    [Fact]
    public void A_body_whose_only_font_declaration_is_inside_a_button_is_still_wrapped()
    {
        // Prose with NO font of its own, plus a button that has one — the real shape of the mail.
        var body =
            "<p>1 speaker(s) need approval before they can flow to Zoho Backstage.</p>"
            + "<a href=\"https://example.test\" style=\"font-family:Aptos,'Segoe UI',Arial,sans-serif;\">"
            + "Approve all 1 as Community</a>";

        var wrapped = BrevoEmailSender.ApplyDefaultFont(body);

        Assert.StartsWith("<div style=\"font-family:Aptos", wrapped, System.StringComparison.Ordinal);
        Assert.EndsWith("</div>", wrapped, System.StringComparison.Ordinal);
        Assert.Contains("<p>1 speaker(s) need approval", wrapped, System.StringComparison.Ordinal);
        // 🔒 The button keeps its own declaration — the wrapper is a floor, never an override.
        Assert.Contains("Approve all 1 as Community", wrapped, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// A self-styling body still renders as its author intended: the wrapper is added, and the
    /// author's own inline font sits INSIDE it, where CSS gives the descendant priority.
    /// </summary>
    [Fact]
    public void A_self_styled_body_keeps_its_own_font_inside_the_wrapper()
    {
        var body = "<div style=\"font-family:Georgia,serif;\">Deliberately serif.</div>";

        var wrapped = BrevoEmailSender.ApplyDefaultFont(body);

        Assert.StartsWith("<div style=\"font-family:Aptos", wrapped, System.StringComparison.Ordinal);
        Assert.Contains("font-family:Georgia,serif;", wrapped, System.StringComparison.Ordinal);
        Assert.True(
            wrapped.IndexOf("font-family:Aptos", System.StringComparison.Ordinal)
            < wrapped.IndexOf("font-family:Georgia", System.StringComparison.Ordinal),
            "the default must be the OUTER declaration, or it would override the author's choice");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_body_is_returned_untouched(string? body)
        => Assert.Equal(body ?? string.Empty, BrevoEmailSender.ApplyDefaultFont(body));

    // ---------------------------------------------------------------------
    //  2. The button width — a guess about a font we do not control
    // ---------------------------------------------------------------------

    /// <summary>
    /// 🔴 The photographed button read <i>"Approve all 1 as"</i> — Word wrapped the label inside a
    /// <c>roundrect</c> of fixed <c>height:44px</c> and clipped the second line, so the button no
    /// longer said which category it would apply.
    /// <para>🔑 Pinned as a comparison against the OLD formula rather than an exact pixel count: the
    /// true width depends on whichever font Word falls back to, which no test can know. What is
    /// worth asserting is that the estimate is now generous — the asymmetry is the whole point, since
    /// too wide is cosmetic and too narrow hides what the button does.</para>
    /// </summary>
    [Fact]
    public void The_longest_real_label_gets_comfortably_more_width_than_the_old_formula()
    {
        const string longest = "Approve all 12 as Community";

        var old = System.Math.Clamp((longest.Length * 9) + 48, 200, 440);
        var now = MailButtonMetrics.WidthPx(longest);

        Assert.True(now > old, $"expected headroom over the clipping formula; old={old} now={now}");
        Assert.True(now >= longest.Length * 12, $"expected ≥12px per character, got {now}");
        Assert.True(now <= MailButtonMetrics.MaxWidthPx, "must stay inside the mail column");
    }

    [Fact]
    public void Width_is_clamped_at_both_ends_so_a_button_is_never_a_sliver_or_wider_than_the_column()
    {
        Assert.Equal(MailButtonMetrics.MinWidthPx, MailButtonMetrics.WidthPx("Go"));
        Assert.Equal(MailButtonMetrics.MinWidthPx, MailButtonMetrics.WidthPx(null));
        Assert.Equal(MailButtonMetrics.MaxWidthPx, MailButtonMetrics.WidthPx(new string('x', 400)));
    }

    /// <summary>
    /// 🔒 The rendered button must carry <c>white-space:nowrap</c>. Width headroom is the only
    /// defence available in the VML/Word path, but every modern client honours nowrap — so on those
    /// the label physically cannot wrap, whatever the width estimate does.
    /// </summary>
    [Fact]
    public void The_speaker_approval_buttons_are_wide_and_cannot_wrap()
    {
        var pending = new SpeakerApprovalService.PendingResult(
            Ring.Broad, FeatureEnabled: true,
            new[]
            {
                new SpeakerApprovalService.PendingSpeaker(
                    ParticipantId: 1, Email: "ada@example.test", FullName: "Ada Lovelace",
                    Ring: Ring.Broad, Category: null, IsActive: false,
                    Lifecycle: ParticipantLifecycleState.Active,
                    Blockers: new[] { "no speaker category" }),
            });

        var html = SpeakerApprovalService.BuildPendingMailHtml(pending, "https://hub.example.test");

        Assert.NotNull(html);
        Assert.Contains("white-space:nowrap", html!, System.StringComparison.Ordinal);
        // The label the operator saw truncated must be present in full.
        Assert.Contains("Approve all 1 as Community", html, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// 🔴 §1092 — the Guest rule must appear in the mail, <b>above the one-click buttons</b>.
    /// </summary>
    /// <remarks>
    /// Operator 2026-08-19: Microsoft speakers and invited guests have individual ELDK agreements
    /// covering hotel and travel, and the <c>Guest</c> category is what applies them. The three §877
    /// buttons approve the ENTIRE queue at once and <c>Community</c> is the first of them — so a
    /// speaker in that group swept into Community loses their cover with no signal.
    /// <para>🔑 The position is asserted, not just the presence: a warning printed after the button
    /// it warns about has already lost.</para>
    /// </remarks>
    [Fact]
    public void The_guest_category_rule_appears_before_the_one_click_buttons()
    {
        var pending = new SpeakerApprovalService.PendingResult(
            Ring.Broad, FeatureEnabled: true,
            new[]
            {
                new SpeakerApprovalService.PendingSpeaker(
                    ParticipantId: 1, Email: "ada@example.test", FullName: "Ada Lovelace",
                    Ring: Ring.Broad, Category: null, IsActive: false,
                    Lifecycle: ParticipantLifecycleState.Active,
                    Blockers: new[] { "no speaker category" }),
            });

        var html = SpeakerApprovalService.BuildPendingMailHtml(pending, "https://hub.example.test");

        Assert.NotNull(html);
        Assert.Contains("hotel and travel", html!, System.StringComparison.OrdinalIgnoreCase);

        var note = html.IndexOf("must be set to", System.StringComparison.OrdinalIgnoreCase);
        var firstButton = html.IndexOf("Approve all 1 as Community", System.StringComparison.Ordinal);
        Assert.True(note >= 0, "the Guest rule must be in the mail");
        Assert.True(
            note < firstButton,
            "the rule must come BEFORE the buttons it is warning about, not after them");
    }
}
