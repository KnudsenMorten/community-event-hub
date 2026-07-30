namespace CommunityHub.Core.Email;

/// <summary>
/// §650 — what a failed send's raw error MEANS, in words the operator can act on.
/// </summary>
/// <remarks>
/// <para>Operator 2026-07-29: *"i still dont understand WHY an email failed … i need a way to
/// understand WHY it failed like unknown recipient or similar. can you include that"*.</para>
///
/// <para><b>Why classify at DISPLAY time rather than storing a new column.</b> Every failure ever
/// recorded already has its raw text in <c>EmailLog.Error</c>. Classifying on read means the
/// existing rows — the ones he is looking at right now — gain their reason immediately, with no
/// migration and no backfill. A stored column would only help mail that fails in future.</para>
///
/// <para>🔒 <b>The distinction that matters is ACTIONABLE vs NOT.</b> "Bad address" is something he
/// fixes; "throttled" and "timeout" fix themselves and just need re-sending; "rejected by the
/// recipient's server" needs a conversation with the person. Lumping them under "Failed" is what
/// made the page useless.</para>
/// </remarks>
public enum EmailFailureKind
{
    /// <summary>Not a failure, or nothing recorded.</summary>
    None = 0,
    /// <summary>The address does not exist / was rejected outright. Needs a corrected address.</summary>
    BadAddress = 1,
    /// <summary>The mailbox exists but refused this message (full, policy, spam rules).</summary>
    RecipientRejected = 2,
    /// <summary>Brevo rate-limited us. Self-correcting — just re-send.</summary>
    Throttled = 3,
    /// <summary>Network/timeout, or the app shut down mid-send. Self-correcting — re-send.</summary>
    Temporary = 4,
    /// <summary>Our own credential or relay configuration is wrong. Nothing will send until fixed.</summary>
    Configuration = 5,
    /// <summary>Deliberately not sent — a ring gate or the kill switch. NOT a failure at all.</summary>
    DeliberatelyNotSent = 6,
    /// <summary>Recorded, but we cannot tell. Shown verbatim rather than guessed at.</summary>
    Unknown = 99,
}

/// <summary>§650 — turns a raw send error into a reason and a next step.</summary>
public static class EmailFailureReason
{
    /// <summary>The classified reason plus the sentence shown to the operator.</summary>
    public sealed record Result(EmailFailureKind Kind, string Summary, string WhatToDo)
    {
        /// <summary>True when re-sending unchanged could plausibly work.</summary>
        public bool WorthResending =>
            Kind is EmailFailureKind.Throttled or EmailFailureKind.Temporary
                 or EmailFailureKind.RecipientRejected;
    }

    private static readonly Result NoneResult =
        new(EmailFailureKind.None, "", "");

    /// <summary>Classify one <c>EmailLog.Error</c>.</summary>
    public static Result Classify(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return NoneResult;
        var e = error.Trim();

        // Not a failure: the ring gate and the kill switch record themselves here too, and counting
        // them as failures is what buried the real ones (§644.2).
        if (Has(e, "ring-dropped") || Has(e, "outside the released ring") || Has(e, "kill switch"))
        {
            return new(EmailFailureKind.DeliberatelyNotSent,
                "Not sent on purpose — the recipient is outside the released ring.",
                "Nothing to fix. Widen the ring when you are ready for them to receive it.");
        }

        // 550/5.1.1 — the address does not exist. The one case that needs HIM, not a retry.
        if (Has(e, "5.1.1") || Has(e, "550") || Has(e, "does not exist")
            || Has(e, "no such user") || Has(e, "unknown recipient") || Has(e, "recipient not found")
            || Has(e, "address rejected") || Has(e, "invalid recipient"))
        {
            return new(EmailFailureKind.BadAddress,
                "The address does not exist — the recipient's mail server rejected it outright.",
                "Correct the e-mail address on the person's record, then re-send. Retrying the same "
                + "address will keep failing and hurts our sending reputation.");
        }

        // 552/5.2.2 mailbox full, 554 policy/spam rejection.
        if (Has(e, "mailbox full") || Has(e, "5.2.2") || Has(e, "quota")
            || Has(e, "552") || Has(e, "554") || Has(e, "blocked") || Has(e, "spam"))
        {
            return new(EmailFailureKind.RecipientRejected,
                "Their mail server accepted the address but refused the message (full mailbox, or a spam/policy rule).",
                "Usually temporary. Re-send later; if it keeps happening, contact the person another way.");
        }

        // Brevo's rate limits (§219) — the HTTP 429 analogue over SMTP.
        if (Has(e, "429") || Has(e, "too many") || Has(e, "rate limit")
            || Has(e, "421") || Has(e, "450") || Has(e, "throttl"))
        {
            return new(EmailFailureKind.Throttled,
                "We were sending too fast and Brevo throttled us.",
                "Self-correcting — just re-send. If it happens in bulk, the send pacer needs slowing.");
        }

        // Our own credential — nothing will send at all until it is fixed (§635).
        if (Has(e, "535") || Has(e, "authentication") || Has(e, "not authenticated")
            || Has(e, "5.7.8") || Has(e, "credential"))
        {
            return new(EmailFailureKind.Configuration,
                "Brevo rejected OUR credentials — no mail is going out at all.",
                "The Brevo SMTP key needs reissuing. This cannot be fixed by re-sending.");
        }

        // Timeouts and shutdown-mid-send. "The operation was canceled" is the shape a deploy
        // produces, and it is what the operator was looking at when he asked this question.
        if (Has(e, "canceled") || Has(e, "cancelled") || Has(e, "timed out") || Has(e, "timeout")
            || Has(e, "connection") || Has(e, "socket") || Has(e, "network"))
        {
            return new(EmailFailureKind.Temporary,
                "The send was interrupted — a timeout, or the app restarted mid-send. The message was never delivered to Brevo.",
                "Nothing is wrong with the address. Re-send it.");
        }

        return new(EmailFailureKind.Unknown,
            "Failed for a reason we could not classify.",
            "The raw error is shown below; re-sending is usually safe to try.");
    }

    private static bool Has(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
