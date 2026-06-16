namespace CommunityHub.Core.Organizer;

/// <summary>
/// How an organizer action turned out — the honest three-way outcome behind the
/// success/failure confirmation (REQUIREMENTS §21 organizer "Success/failure
/// confirmation on QR provisioning + all send actions").
/// </summary>
public enum ActionOutcome
{
    /// <summary>The action did real work (a send went out / a QR was stored).</summary>
    Succeeded,

    /// <summary>
    /// The action completed without an error but did NOTHING — a no-op. e.g. a
    /// send that was allowlist-dropped / had zero eligible recipients, or a
    /// provisioning skipped because the seam is not wired. This is NOT a success
    /// and must never be reported as one.
    /// </summary>
    NoOp,

    /// <summary>The action failed (an exception / partial failure with a reason).</summary>
    Failed,
}

/// <summary>
/// The visual kind the shared <c>_Flash</c> toast renders. Maps from
/// <see cref="ActionOutcome"/>: a real success is <c>success</c>; a hard failure
/// is <c>error</c>; a no-op (dropped / nothing-to-do) is <c>info</c> — visibly
/// distinct from a green "done", so the organizer is never told a no-op succeeded.
/// </summary>
public enum FlashKind
{
    Success,
    Info,
    Error,
}

/// <summary>
/// The shaped, honest result of an organizer send / provisioning action, ready to
/// hand to the shared <c>_Flash</c> toast. <see cref="Message"/> is the composed
/// confirmation line; <see cref="Kind"/> drives the toast colour/role. This type
/// carries the structured facts too (<see cref="At"/>, <see cref="Count"/>,
/// <see cref="Url"/>, <see cref="Reason"/>) so a caller or a test can assert the
/// real outcome rather than re-parse the text.
/// </summary>
public sealed record ActionResultSummary
{
    /// <summary>The honest outcome.</summary>
    public required ActionOutcome Outcome { get; init; }

    /// <summary>The toast kind derived from <see cref="Outcome"/>.</summary>
    public FlashKind Kind { get; init; }

    /// <summary>The composed, human-friendly confirmation line.</summary>
    public required string Message { get; init; }

    /// <summary>When the action happened (success path) — the "done at &lt;time&gt;".</summary>
    public DateTimeOffset? At { get; init; }

    /// <summary>Recipient / affected-row count where one applies (send actions).</summary>
    public int? Count { get; init; }

    /// <summary>The stored URL where one applies (QR provisioning).</summary>
    public string? Url { get; init; }

    /// <summary>The failure / no-op reason where one applies.</summary>
    public string? Reason { get; init; }

    /// <summary>Convenience: the toast kind as the string the <c>_Flash</c> partial expects.</summary>
    public string FlashKindString => Kind switch
    {
        FlashKind.Success => "success",
        FlashKind.Error => "error",
        _ => "info",
    };

    /// <summary>True only for a genuine success — never for a no-op or failure.</summary>
    public bool IsSuccess => Outcome == ActionOutcome.Succeeded;
}

/// <summary>
/// PURE, side-effect-free shaping of an organizer action's outcome into an honest
/// <see cref="ActionResultSummary"/> for the shared confirmation toast
/// (REQUIREMENTS §21). Every organizer SEND and the QR PROVISIONING action funnel
/// their native result records (recipient count, stored URL, drop/skip reason)
/// through here so the confirmation reflects the REAL outcome:
///   - a real send shows "Sent at &lt;time&gt; — N recipient(s).",
///   - a real provisioning shows "Provisioned at &lt;time&gt; — stored at &lt;url&gt;.",
///   - a send that reached NOBODY (allowlist-dropped / zero eligible) is reported
///     as a no-op with the reason — NOT as a success,
///   - a failure carries the reason.
///
/// No DB, no clock, no I/O — fully unit-testable. The format strings keep the
/// localizable shape in one place; callers pass an already-localized
/// <c>formats</c> bundle (or the built-in English defaults) so the same shaping
/// serves en + da-DK.
/// </summary>
public static class ActionResultSummarizer
{
    /// <summary>
    /// The localizable format strings the summarizer composes. Each is a
    /// <see cref="string.Format(string, object?[])"/> template; defaults are
    /// English. A page passes its <c>@Localizer</c>-resolved values so the toast
    /// is localized. Tokens:
    ///   <c>SentFormat</c>: {0}=time, {1}=count.
    ///   <c>ProvisionedFormat</c>: {0}=time, {1}=url.
    ///   <c>ProvisionedNoUrlFormat</c>: {0}=time.
    ///   <c>NoOpFormat</c> / <c>FailedFormat</c>: {0}=reason.
    /// </summary>
    public sealed record Formats(
        string SentFormat,
        string ProvisionedFormat,
        string ProvisionedNoUrlFormat,
        string NoOpFormat,
        string FailedFormat)
    {
        /// <summary>English defaults (used when a page passes no localized bundle).</summary>
        public static readonly Formats Default = new(
            SentFormat: "Sent at {0} — {1} recipient(s).",
            ProvisionedFormat: "Provisioning done at {0} — stored at {1}.",
            ProvisionedNoUrlFormat: "Provisioning done at {0}.",
            NoOpFormat: "Nothing was sent: {0}",
            FailedFormat: "Action failed: {0}");
    }

    /// <summary>The time format used inside the composed confirmation lines.</summary>
    private const string TimeFormat = "yyyy-MM-dd HH:mm 'UTC'";

    private static string FormatTime(DateTimeOffset at) =>
        at.ToUniversalTime().ToString(TimeFormat, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Shape a SEND action's outcome. <paramref name="anySent"/> is the honest
    /// success gate: when false (allowlist-dropped, zero eligible recipients, …)
    /// the result is a <see cref="ActionOutcome.NoOp"/> carrying
    /// <paramref name="reason"/> — never a success. <paramref name="recipientCount"/>
    /// is the number actually sent. <paramref name="failed"/> &gt; 0 with some sent
    /// still reports success but appends the partial-failure note via
    /// <paramref name="reason"/>; <paramref name="failed"/> &gt; 0 with none sent is a failure.
    /// </summary>
    public static ActionResultSummary ForSend(
        bool anySent,
        int recipientCount,
        DateTimeOffset at,
        string? reason = null,
        int failed = 0,
        Formats? formats = null)
    {
        var f = formats ?? Formats.Default;

        if (anySent && recipientCount > 0)
        {
            var msg = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                f.SentFormat, FormatTime(at), recipientCount);
            if (failed > 0 && !string.IsNullOrWhiteSpace(reason))
            {
                // A partial run: real sends went out (success) but say so honestly.
                msg = msg + " " + reason.Trim();
            }
            return new ActionResultSummary
            {
                Outcome = ActionOutcome.Succeeded,
                Kind = FlashKind.Success,
                Message = msg,
                At = at,
                Count = recipientCount,
            };
        }

        // Nothing sent. A failure (something errored) vs a clean no-op (dropped /
        // zero eligible) is distinguished by whether anything failed.
        if (failed > 0)
        {
            return Failure(reason, f);
        }

        return NoOp(reason, f);
    }

    /// <summary>
    /// Shape a QR-PROVISIONING outcome. <paramref name="provisioned"/> is the honest
    /// success gate; when false (seam not wired, missing room, …) the result is a
    /// <see cref="ActionOutcome.NoOp"/> carrying <paramref name="reason"/> — never a
    /// success. On success the stored <paramref name="url"/> is shown (or the
    /// no-URL variant when none was returned).
    /// </summary>
    public static ActionResultSummary ForProvision(
        bool provisioned,
        DateTimeOffset at,
        string? url = null,
        string? reason = null,
        Formats? formats = null)
    {
        var f = formats ?? Formats.Default;

        if (provisioned)
        {
            var msg = string.IsNullOrWhiteSpace(url)
                ? string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    f.ProvisionedNoUrlFormat, FormatTime(at))
                : string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    f.ProvisionedFormat, FormatTime(at), url.Trim());
            return new ActionResultSummary
            {
                Outcome = ActionOutcome.Succeeded,
                Kind = FlashKind.Success,
                Message = msg,
                At = at,
                Url = string.IsNullOrWhiteSpace(url) ? null : url.Trim(),
            };
        }

        return NoOp(reason, f);
    }

    /// <summary>Shape an explicit failure (an exception was caught) with its reason.</summary>
    public static ActionResultSummary Failure(string? reason, Formats? formats = null)
    {
        var f = formats ?? Formats.Default;
        var clean = string.IsNullOrWhiteSpace(reason) ? "An unexpected error occurred." : reason.Trim();
        return new ActionResultSummary
        {
            Outcome = ActionOutcome.Failed,
            Kind = FlashKind.Error,
            Message = string.Format(System.Globalization.CultureInfo.CurrentCulture, f.FailedFormat, clean),
            Reason = clean,
        };
    }

    /// <summary>Shape a clean no-op (nothing happened, but nothing errored) with its reason.</summary>
    public static ActionResultSummary NoOp(string? reason, Formats? formats = null)
    {
        var f = formats ?? Formats.Default;
        var clean = string.IsNullOrWhiteSpace(reason) ? "there was nothing to do." : reason.Trim();
        return new ActionResultSummary
        {
            Outcome = ActionOutcome.NoOp,
            Kind = FlashKind.Info,
            Message = string.Format(System.Globalization.CultureInfo.CurrentCulture, f.NoOpFormat, clean),
            Reason = clean,
        };
    }
}
