using CommunityHub.Core.Organizer;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Tests for the pure <see cref="ActionResultSummarizer"/> that shapes an honest
/// success / no-op / failure confirmation for organizer SEND + QR PROVISIONING
/// actions (REQUIREMENTS §21 "Success/failure confirmation on QR provisioning +
/// all send actions"). Pure + offline — no DB, clock or I/O.
///
/// The contract asserted here:
///   - a real SEND carries a timestamp + the recipient COUNT,
///   - a real PROVISION carries a timestamp + the stored URL,
///   - a FAILURE carries the reason (and is NOT a success),
///   - a DROPPED / no-op send is reported AS such — never as a success.
/// </summary>
public sealed class ActionResultSummarizerTests
{
    private static readonly DateTimeOffset At =
        new(2026, 6, 16, 9, 31, 0, TimeSpan.Zero);

    // ---- SEND: real success carries timestamp + recipient count ----------------

    [Fact]
    public void Send_success_is_a_success_with_timestamp_and_recipient_count()
    {
        var r = ActionResultSummarizer.ForSend(anySent: true, recipientCount: 5, at: At);

        Assert.Equal(ActionOutcome.Succeeded, r.Outcome);
        Assert.True(r.IsSuccess);
        Assert.Equal(FlashKind.Success, r.Kind);
        Assert.Equal("success", r.FlashKindString);
        Assert.Equal(At, r.At);
        Assert.Equal(5, r.Count);
        // The composed line carries BOTH the time and the count.
        Assert.Contains("2026-06-16 09:31 UTC", r.Message);
        Assert.Contains("5", r.Message);
    }

    // ---- SEND: a dropped / zero-recipient send is a no-op, NOT a success --------

    [Fact]
    public void Send_that_reached_nobody_is_a_noop_not_a_success()
    {
        var r = ActionResultSummarizer.ForSend(
            anySent: false, recipientCount: 0, at: At,
            reason: "all recipients were dropped by the allowlist.");

        Assert.Equal(ActionOutcome.NoOp, r.Outcome);
        Assert.False(r.IsSuccess);
        Assert.NotEqual(FlashKind.Success, r.Kind);
        Assert.Equal(FlashKind.Info, r.Kind);
        Assert.Equal("info", r.FlashKindString);
        Assert.Contains("allowlist", r.Message);
        Assert.Equal("all recipients were dropped by the allowlist.", r.Reason);
    }

    [Fact]
    public void Send_with_anySent_true_but_zero_count_is_still_a_noop()
    {
        // Defensive: a truthy flag with no actual recipients must NOT claim success.
        var r = ActionResultSummarizer.ForSend(
            anySent: true, recipientCount: 0, at: At, reason: "no eligible recipients.");

        Assert.Equal(ActionOutcome.NoOp, r.Outcome);
        Assert.False(r.IsSuccess);
    }

    // ---- SEND: hard failure carries the reason ---------------------------------

    [Fact]
    public void Send_with_no_success_and_failures_is_a_failure_carrying_the_reason()
    {
        var r = ActionResultSummarizer.ForSend(
            anySent: false, recipientCount: 0, at: At,
            reason: "SMTP refused the connection.", failed: 3);

        Assert.Equal(ActionOutcome.Failed, r.Outcome);
        Assert.False(r.IsSuccess);
        Assert.Equal(FlashKind.Error, r.Kind);
        Assert.Equal("error", r.FlashKindString);
        Assert.Contains("SMTP refused the connection.", r.Message);
        Assert.Equal("SMTP refused the connection.", r.Reason);
    }

    [Fact]
    public void Send_partial_failure_with_some_sent_is_success_but_notes_the_failures()
    {
        var r = ActionResultSummarizer.ForSend(
            anySent: true, recipientCount: 4, at: At,
            reason: "(1 failed.)", failed: 1);

        // Real mail went out, so it is a success — but the failure is surfaced.
        Assert.Equal(ActionOutcome.Succeeded, r.Outcome);
        Assert.Equal(4, r.Count);
        Assert.Contains("4", r.Message);
        Assert.Contains("1 failed", r.Message);
    }

    // ---- PROVISION: real success carries timestamp + stored URL ----------------

    [Fact]
    public void Provision_success_is_a_success_with_timestamp_and_stored_url()
    {
        const string url = "https://example.sharepoint.test/qr/room-1.png";
        var r = ActionResultSummarizer.ForProvision(provisioned: true, at: At, url: url);

        Assert.Equal(ActionOutcome.Succeeded, r.Outcome);
        Assert.True(r.IsSuccess);
        Assert.Equal(FlashKind.Success, r.Kind);
        Assert.Equal(At, r.At);
        Assert.Equal(url, r.Url);
        Assert.Contains("2026-06-16 09:31 UTC", r.Message);
        Assert.Contains(url, r.Message);
    }

    [Fact]
    public void Provision_success_without_a_url_still_reports_done_at_time()
    {
        var r = ActionResultSummarizer.ForProvision(provisioned: true, at: At, url: null);

        Assert.Equal(ActionOutcome.Succeeded, r.Outcome);
        Assert.Null(r.Url);
        Assert.Contains("2026-06-16 09:31 UTC", r.Message);
    }

    // ---- PROVISION: not-wired / failed provisioning is a no-op, NOT a success ---

    [Fact]
    public void Provision_not_configured_is_a_noop_not_a_success()
    {
        var r = ActionResultSummarizer.ForProvision(
            provisioned: false, at: At,
            reason: "Room-QR storage is not configured. No QR was generated.");

        Assert.Equal(ActionOutcome.NoOp, r.Outcome);
        Assert.False(r.IsSuccess);
        Assert.NotEqual(FlashKind.Success, r.Kind);
        Assert.Equal(FlashKind.Info, r.Kind);
        Assert.Contains("not configured", r.Message);
        Assert.Null(r.Url);
    }

    // ---- Localized format bundle threads through -------------------------------

    [Fact]
    public void A_localized_format_bundle_is_used_when_supplied()
    {
        var da = new ActionResultSummarizer.Formats(
            SentFormat: "Sendt kl. {0} — {1} modtager(e).",
            ProvisionedFormat: "Klargøring fuldført kl. {0} — gemt på {1}.",
            ProvisionedNoUrlFormat: "Klargøring fuldført kl. {0}.",
            NoOpFormat: "Intet blev sendt: {0}",
            FailedFormat: "Handlingen mislykkedes: {0}");

        var sent = ActionResultSummarizer.ForSend(true, 2, At, formats: da);
        Assert.Contains("Sendt kl.", sent.Message);
        Assert.Contains("2", sent.Message);

        var failed = ActionResultSummarizer.Failure("boom", da);
        Assert.Contains("Handlingen mislykkedes:", failed.Message);
    }

    // ---- Direct failure / no-op shapers ---------------------------------------

    [Fact]
    public void Failure_defaults_to_a_generic_reason_when_none_supplied()
    {
        var r = ActionResultSummarizer.Failure(reason: null);
        Assert.Equal(ActionOutcome.Failed, r.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(r.Reason));
        Assert.False(string.IsNullOrWhiteSpace(r.Message));
    }

    [Fact]
    public void NoOp_is_never_a_success()
    {
        var r = ActionResultSummarizer.NoOp("nothing eligible.");
        Assert.Equal(ActionOutcome.NoOp, r.Outcome);
        Assert.False(r.IsSuccess);
        Assert.Equal(FlashKind.Info, r.Kind);
    }
}
