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
    /// 🔒 §707.42 — true only when the job RAN NORMALLY and found NOTHING TO DO, which is the one
    /// inactive state worth alerting about. False when a GATE turned the job away.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-07-30, on the *"has now run 300 times in a row WITHOUT DOING ANYTHING"*
    /// mail: <i>"this is too much info - and not relevant"</i>. He is right, and the flaw is
    /// structural rather than editorial: the alert fired because a job was skipped, but the reason it
    /// was skipped is that HE switched the feature off. It reported his own configuration back to him
    /// as an anomaly and then argued with itself about it — *"If that is deliberate, nothing needs
    /// doing."* An alert that opens by conceding it may be pointless should not have been sent, and
    /// one that cannot tell a fault from a setting is one he learns to delete — taking the next real
    /// one with it.</para>
    ///
    /// <para>🔑 The split is semantic, not a threshold tweak: <see cref="ReportInactive"/> means a
    /// gate said no (a feature is off, no active edition, external writes blocked) — a CHOICE, and
    /// never news. <see cref="ReportExamined"/> with zero means every gate passed and the DATA did
    /// not arrive — §545/§585's real case, where nothing is misconfigured and something is wrong.
    /// Only the second raises the alert.</para>
    ///
    /// <para>⚠️ <b>The residual risk, stated rather than hidden:</b> a feature switched off by
    /// ACCIDENT is now never chased. The Jobs page still shows the inactive reason — a state belongs
    /// on a page, not in an alert — and the mitigation if he wants one is a periodic "features
    /// currently OFF" summary, one line per feature, not a mail per job per 100 runs.</para>
    /// </remarks>
    public bool InactiveIsDataStarvation { get; private set; }

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
        InactiveIsDataStarvation = false;
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

        // §707.42 — THIS is the alertable inactive state: nothing is misconfigured and the data
        // stopped arriving. A gate saying no is a setting; this is a symptom.
        InactiveIsDataStarvation = true;
    }
}
