namespace CommunityHub.Core.Diagnostics;

/// <summary>
/// §545(b) INACTIVE — a per-run scratchpad where a job says <i>"I ran, and I deliberately did
/// nothing, and here is why"</i>. Read by the worker middleware after a clean run.
/// </summary>
/// <remarks>
/// <para><b>The incident this exists for, verbatim (§545).</b> The session push was switched off by
/// a setting and said so <b>every run for weeks</b> — *"session sync direction is stage 1 … CEH→Zoho
/// push inactive"* — but only at Information level, in App Insights, which he cannot reach. From his
/// side the Jobs page showed a healthy job with a recent "last run", so **the system looked fine
/// while doing nothing**, until 1,500 attendees had no agenda. Nothing alerted, because nothing had
/// failed.</para>
///
/// <para><b>Why a REPORTER and not log-scraping.</b> The reason is already written at every
/// short-circuit; what is missing is that it is written to a place nobody reads. Parsing App
/// Insights text would be a second, fragile source of truth. A job saying so explicitly is one line
/// at the site that already logs it, and it survives the log retention window.</para>
///
/// <para>🔒 <b>Silence means UNKNOWN, never "healthy" and never "inactive".</b> A job that reports
/// nothing is left alone entirely — §624's restraint rule. Only jobs that have been instrumented can
/// ever raise an INACTIVE alert, so rolling this out job by job can never produce a false alarm for
/// a job nobody has looked at yet.</para>
/// </remarks>
public sealed class JobActivityReporter
{
    /// <summary>The reason this run did nothing, or null when the job did work / said nothing.</summary>
    public string? InactiveReason { get; private set; }

    /// <summary>True once the job has explicitly reported either outcome.</summary>
    public bool Reported { get; private set; }

    /// <summary>
    /// The job ran but deliberately did nothing — a feature switch off, a sync direction that
    /// excludes it, external writes blocked, no active edition. <paramref name="reason"/> is what
    /// the operator will read in the alert, so write it for him, not for a log grep.
    /// </summary>
    public void ReportInactive(string reason)
    {
        // FIRST reason wins: it is the outermost gate, and therefore the real one. A later gate
        // reporting "nothing to do" would otherwise mask "the feature is switched off".
        if (InactiveReason is null) InactiveReason = reason;
        Reported = true;
    }

    /// <summary>
    /// The job did real work. Resets any inactive reason — a run that got past the gates and
    /// processed something is not inactive, whatever an inner branch reported.
    /// </summary>
    public void ReportWork()
    {
        InactiveReason = null;
        Reported = true;
    }

    /// <summary>
    /// §545 NO-OP STREAK — the job got through every gate, looked, and found
    /// <paramref name="examined"/> records. Zero counts as a no-op.
    /// </summary>
    /// <param name="examined">How many records this pass actually examined.</param>
    /// <param name="what">What was being counted, in the operator's words — e.g. "orders in Zoho".</param>
    /// <remarks>
    /// <para>§545: *"a sync that examines 0 records for N consecutive runs when it normally sees
    /// some (e.g. the ERP reconcile finding no companies, the Zoho mirror pulling nothing)"*. This
    /// is a different failure from <see cref="ReportInactive"/>: nothing turned the job away, so
    /// every gate says healthy — the data simply stopped arriving. §585 was exactly this shape
    /// (`halls` missing from the paging reject list, so every read came back EMPTY and no session
    /// could get a room; no error, no failure count).</para>
    ///
    /// <para>🔒 <b>Only call this where ZERO is genuinely abnormal.</b> For most jobs an empty pass
    /// is the normal case — a reminder job with nothing due examines nothing every single run, and
    /// counting that as a no-op streak would alert about a system working perfectly. §545's own
    /// wording is the test: *"when it normally sees some"*. Everywhere else, keep
    /// <see cref="ReportWork()"/>, whose silence means UNKNOWN and is never flagged.</para>
    /// </remarks>
    public void ReportExamined(int examined, string what)
    {
        if (examined > 0) { ReportWork(); return; }

        ReportInactive(
            $"Ran normally but found NO {what} at all — every gate passed, so nothing is switched "
            + "off; the data itself is not arriving.");
    }
}
