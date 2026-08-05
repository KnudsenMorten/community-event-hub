using System.Text.RegularExpressions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §783.8 / §783.13 — <b>producing an artefact and telling somebody about it are different
/// decisions.</b>
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-03, stating the rule: <i>"qr code publishing is a backend service which
/// must not be gated; only the actual notification of the release of the qr code must be
/// gated"</i>.</para>
///
/// <para>🔒 <b>Why it needs pinning rather than a comment.</b> The QR publish sits in
/// <c>EvaluationReportPublishJob</c>, immediately ABOVE that job's <c>session-eval-email</c> feature
/// gate — and the single most natural edit anyone will ever make to this file is to move a new
/// QR-related line down beside the rest of the publishing, or to wrap the whole body in the gate
/// "for consistency". Either would silently stop the codes being produced.</para>
///
/// <para>⚠️ The consequence is invisible until it is expensive: a switch meaning <i>"don't e-mail
/// speakers their results yet"</i> would also mean <i>"don't build the QR"</i>, and nobody discovers
/// that a code was never generated until the morning it is supposed to be on a wall. §783.8 already
/// cost six months of an uncalled <c>PublishQrCodesAsync</c>; this is the same failure with a switch
/// in front of it instead of a missing call.</para>
///
/// <para>A source-order assertion, in the same spirit as <c>JobCadenceWordsMatchCronTests</c> and
/// <c>EmailTemplateLinkTests</c>: mechanical, and it fails the BUILD rather than the event.</para>
/// </remarks>
public sealed class QrPublishIsNotGatedTests
{
    private static string JobSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;
        Assert.NotNull(dir);

        var path = Path.Combine(
            dir!.FullName, "src", "CommunityHub.Jobs", "EvaluationReportPublishJob.cs");
        Assert.True(File.Exists(path), $"Job source not found: {path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void The_QR_publish_runs_BEFORE_the_results_email_feature_gate()
    {
        var src = JobSource();

        var publish = src.IndexOf("PublishQrCodesAsync(", StringComparison.Ordinal);
        var gate = src.IndexOf("IsFeatureEnabledAsync(", StringComparison.Ordinal);

        Assert.True(publish >= 0, "The job no longer publishes QR codes at all (§783.8).");
        Assert.True(gate >= 0, "The results-email feature gate has gone — check this test still means something.");

        Assert.True(
            publish < gate,
            "§783.13 — QR publishing must run BEFORE the 'session-eval-email' gate. A QR is an INPUT "
            + "to collecting feedback (it is printed and put on a wall before the session runs), not "
            + "a result of it. Operator: \"qr code publishing is a backend service which must not be "
            + "gated; only the actual notification of the release of the qr code must be gated.\"");
    }

    [Fact]
    public void The_QR_publish_is_not_itself_wrapped_in_a_feature_check()
    {
        var src = JobSource();

        // Everything from the start of the QR helper to the end of the file. The helper may check
        // that a FOLDER is configured (there is nowhere to write without one) and that a hub URL
        // exists (it gets printed) — but it must never consult the feature gate.
        var helper = src.IndexOf(
            "private async Task PublishQrCodesAsync(", StringComparison.Ordinal);
        Assert.True(helper >= 0, "The §783.8 QR publish helper has been renamed or removed.");

        var body = src[helper..];

        Assert.DoesNotContain("IsFeatureEnabledAsync", body, StringComparison.Ordinal);
        Assert.DoesNotContain("_gate.", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Publishing_a_QR_notifies_NOBODY_and_the_speaker_least_of_all()
    {
        // §783.13b — operator: "notification of the qr code pblishing is gated and speaker must not
        // be notified".
        //
        // 🔒 A QR is an ORGANIZER'S artefact — printed, put on a wall. The speaker has nothing to do
        // with it, and a "your QR code is ready" mail would arrive months before it means anything
        // FROM THE SAME SERVICE that sends "your evaluation results are ready" — a mail speakers
        // genuinely wait for. Diluting that channel is the real cost, which is why this is a
        // never-do rather than a switch.
        var src = JobSource();
        var helper = src.IndexOf(
            "private async Task PublishQrCodesAsync(", StringComparison.Ordinal);
        Assert.True(helper >= 0, "The §783.8 QR publish helper has been renamed or removed.");

        var body = src[helper..];

        // ⚠️ NOT a bare "_email" check: the helper legitimately reads `_emailOptions.HubUrl` for the
        // URL it encodes. Match the SENDING verbs instead, or this test fails on a config read.
        foreach (var forbidden in new[] { "NotifyAsync", "SendAsync", "MailService", "_email." })
        {
            Assert.False(
                body.Contains(forbidden, StringComparison.Ordinal),
                $"§783.13b — the QR publish path must send NOTHING, but it references "
                + $"'{forbidden}'. Publishing a QR notifies nobody; the speaker is never told.");
        }
    }

    [Fact]
    public void A_printed_code_never_encodes_a_guessed_hostname()
    {
        // 🔒 Not a gate, but the other way this goes irreversibly wrong: the URL inside a QR is
        // PRINTED. Falling back to the request host would put a staging hostname onto paper, and
        // nothing downstream would ever catch it. The job must read the configured hub URL.
        var src = JobSource();
        var helper = src.IndexOf(
            "private async Task PublishQrCodesAsync(", StringComparison.Ordinal);
        var body = src[helper..];

        Assert.Contains("HubUrl", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Request.Host", body, StringComparison.Ordinal);
    }
}
