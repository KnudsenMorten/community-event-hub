using CommunityHub.Core.Integrations.Sessions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// 🔴 §1246 — a failed speaker push must produce a line the organizer can FORWARD TO THE SPEAKER.
/// </summary>
/// <remarks>
/// <para>Operator 2026-09-20: *"send correct error message so we can escalate to speakers"*. Three
/// of four failures carried Zoho's own text — *"Sorry, an unexpected error has occurred. Please try
/// again later."* — which names no field, implies the fault is transient when it is permanent, and
/// cannot be forwarded to anybody.</para>
///
/// <para>✅ The causes were measured, not guessed: on a 77-speaker PROD push every speaker whose
/// LinkedIn/Twitter URL carried a <c>?</c> query string or an <c>@</c> in the path FAILED, and every
/// speaker without one SUCCEEDED. No counter-examples either way.</para>
/// </remarks>
public sealed class SpeakerPushDiagnosticsTests
{
    [Theory]
    // The three real "unexpected error" failures.
    [InlineData(null, "https://twitter.com/cbrhh?s=09")]
    [InlineData(null, "https://twitter.com/paulismama?lang=de")]
    [InlineData(null, "https://twitter.com/@ErlendRushfeldt")]
    public void A_malformed_social_url_is_named(string? linkedIn, string? twitter)
    {
        var why = SpeakerPushDiagnostics.Explain(linkedIn, twitter);

        Assert.NotNull(why);
        // It must say WHICH field and WHAT to type instead — that is what makes it forwardable.
        Assert.Contains("X / Twitter", why);
        Assert.Contains("Ask the speaker to change it in Sessionize", why);
    }

    [Fact]
    public void An_over_long_linkedin_url_is_explained_by_its_CAUSE_not_just_its_length()
    {
        // 🔑 The 127-character URL is over the limit ONLY because of an `?utm_source=share…` suffix
        // pasted from the mobile app. Reporting the length alone would send the speaker hunting for
        // a shorter profile; naming the query string tells them to delete the tracking noise.
        const string url = "https://www.linkedin.com/in/sourabhmhaisekar?utm_source=share"
                         + "&utm_campaign=share_via&utm_content=profile&utm_medium=android_app";

        var why = SpeakerPushDiagnostics.Explain(url, null);

        Assert.NotNull(why);
        Assert.Contains("LinkedIn", why);
        Assert.Contains("tracking query string", why);
        Assert.Contains("127 characters", why);
        Assert.Contains("https://www.linkedin.com/in/sourabhmhaisekar", why);
    }

    [Theory]
    [InlineData("https://www.linkedin.com/in/christopherbrumm", "https://twitter.com/cbrhh")]
    [InlineData(null, null)]
    [InlineData("https://linkedin.com/in/someone", null)]
    public void A_clean_profile_produces_NO_explanation(string? linkedIn, string? twitter)
    {
        // 🔒 NULL, not a guess. An explanation that is confidently wrong is worse than Zoho's own
        // text, because the organizer would forward it and ask a speaker to change something fine.
        Assert.Null(SpeakerPushDiagnostics.Explain(linkedIn, twitter));
    }

    [Theory]
    [InlineData("https://twitter.com/cbrhh?s=09", "https://twitter.com/cbrhh")]
    [InlineData("https://twitter.com/@ErlendRushfeldt", "https://twitter.com/ErlendRushfeldt")]
    [InlineData("https://www.linkedin.com/in/x?utm_source=share", "https://www.linkedin.com/in/x")]
    [InlineData("https://www.linkedin.com/in/sandra-kiel/", "https://www.linkedin.com/in/sandra-kiel")]
    public void Clean_strips_tracking_and_a_stray_at(string input, string expected)
    {
        Assert.Equal(expected, SpeakerPushDiagnostics.Clean(input));
    }

    [Fact]
    public void Clean_leaves_the_host_alone()
    {
        // ⚠️ The '@' rule applies to the PATH only. Stripping it from a host would mangle a URL
        // rather than fix one — and this helper's output is shown to a speaker as "type this".
        Assert.Equal("https://twitter.com", SpeakerPushDiagnostics.Clean("https://twitter.com"));
    }
}
