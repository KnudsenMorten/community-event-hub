using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §861 — the organizer credit is composed at publish time and never stored.
/// </summary>
/// <remarks>
/// 🔴 These exist because the two-field model already caused a LIVE defect: the editor loaded the
/// fully composed post and saved it back, so posts 383 and 495 froze the credit as literal text
/// (§861.3). The names were never the risk — he ruled they will not change. The risk is that a
/// frozen credit can never be UPGRADED TO @-MENTIONS (§858), silently opting those posts out.
/// </remarks>
public class SoMePostCreditTests
{
    private const string Credits =
        "Morten Waltorp Knudsen [MVP] | Martin Byskov | Morten Leth Hedegaard | Kent Agerlund";

    [Fact]
    public void Compose_appends_the_credit_block()
    {
        var composed = SoMePostCredit.Compose("The post body.", "ELDK27", Credits);

        Assert.Equal("The post body.\n\nELDK27 Organizers:\n" + Credits, composed);
    }

    [Fact]
    public void Compose_without_credits_leaves_no_dangling_label()
    {
        // A post must never publish with "ELDK27 Organizers:" and nothing under it.
        Assert.Equal("Body.", SoMePostCredit.Compose("Body.", "ELDK27", null));
        Assert.Equal("Body.", SoMePostCredit.Compose("Body.", "ELDK27", "   "));
    }

    [Fact]
    public void Strip_removes_a_trailing_credit_block()
    {
        var stored = "The post body.\n\nELDK27 Organizers:\n" + Credits;

        Assert.True(SoMePostCredit.TryStrip(stored, "ELDK27", out var body));
        Assert.Equal("The post body.", body);
    }

    [Fact]
    public void Strip_and_compose_round_trip()
    {
        const string body = "Line one.\n\nLine two with #ELDK27 tags.";

        var composed = SoMePostCredit.Compose(body, "ELDK27", Credits);
        Assert.True(SoMePostCredit.TryStrip(composed, "ELDK27", out var back));

        Assert.Equal(body, back);
    }

    [Fact]
    public void Strip_reports_false_when_there_is_no_credit()
    {
        // §854 — exclude AND report. The migration must be able to NAME the rows it did not change
        // rather than assume it handled them.
        Assert.False(SoMePostCredit.TryStrip("A post with no credit at all.", "ELDK27", out var body));
        Assert.Equal("A post with no credit at all.", body);
    }

    [Fact]
    public void Strip_leaves_the_label_alone_when_it_is_mid_post()
    {
        // 🔒 If he WRITES about the organizers in the body, that is his prose, not the frame.
        // A blank line after the label means the post continues, so it is not the trailing block.
        const string prose =
            "ELDK27 Organizers:\nsomeone\n\nAnd then the post carries on for another paragraph.";

        Assert.False(SoMePostCredit.TryStrip(prose, "ELDK27", out var body));
        Assert.Equal(prose, body);
    }

    [Fact]
    public void Strip_survives_a_hand_edited_credit()
    {
        // 🔴 THE CASE THAT KILLED AN EXACT-MATCH IMPLEMENTATION. Post 495 was hand-edited around
        // the credit, so comparing against the CURRENT composed suffix would refuse to strip it and
        // leave the footer frozen forever — the exact state §861 exists to remove.
        var stored = "Body.\n\nELDK27 Organizers:\nMorten Waltorp Knudsen | Someone Else Entirely";

        Assert.True(SoMePostCredit.TryStrip(stored, "ELDK27", out var body));
        Assert.Equal("Body.", body);
    }

    [Fact]
    public void Composing_a_stripped_legacy_post_restores_the_CURRENT_credit()
    {
        // The whole point: an old post that froze a stale credit picks up today's one — which is
        // how tomorrow's @-mention upgrade (§858) reaches posts he has already edited.
        var frozen = "Body.\n\nELDK27 Organizers:\nOld Name Only";

        Assert.True(SoMePostCredit.TryStrip(frozen, "ELDK27", out var body));
        var recomposed = SoMePostCredit.Compose(body, "ELDK27", Credits);

        Assert.Equal("Body.\n\nELDK27 Organizers:\n" + Credits, recomposed);
        Assert.DoesNotContain("Old Name Only", recomposed);
    }
}
