namespace CommunityHub.Core.Integrations;

/// <summary>
/// §553 — what we know about a stored external id, in THREE states rather than two.
/// </summary>
public enum ExternalLinkState
{
    /// <summary>We could not find out. NEVER clear a link on this.</summary>
    Unknown = 0,

    /// <summary>A successful, complete read found the record. Leave the link alone.</summary>
    Exists = 1,

    /// <summary>A successful, complete read did NOT contain it — the record was deleted remotely.</summary>
    Gone = 2,
}

/// <summary>The probe outcome plus a human reason, for logging and alerting.</summary>
public readonly record struct ExternalLinkResult(ExternalLinkState State, string? Detail)
{
    public bool IsGone => State == ExternalLinkState.Gone;
    public bool IsUnknown => State == ExternalLinkState.Unknown;
}

/// <summary>
/// §553 — ONE rule for "is the thing our stored id points at still there?", shared by every
/// comparison against an external system.
///
/// <para><b>Why this exists.</b> The self-heal was implemented separately at each call site —
/// sessions, speakers, QR codes, graphics — and each made its own judgement about what a failed
/// read meant. Operator 2026-07-28: <i>"i told you to include self-heal to detect that this
/// backstage id doesn't exist anymore. we have that multiple places in the code. why don't you make
/// it consitent across any comparison against zoho. i do not accept workarounds manually"</i>.</para>
///
/// <para><b>The rule that matters.</b> "Not found" and "could not look" are NOT the same thing, and
/// conflating them is how a self-heal turns into data loss: an outage, an expired token or a
/// missing API scope would look exactly like "everything was deleted", and the healer would
/// helpfully unlink — or duplicate — the entire agenda. So a link is cleared ONLY on
/// <see cref="ExternalLinkState.Gone"/>, which requires a read that SUCCEEDED and was COMPLETE.
/// Everything else is <see cref="ExternalLinkState.Unknown"/> and changes nothing.</para>
///
/// <para><b>Unknown must be LOUD.</b> The session self-heal used to sit inside a bare
/// <c>catch { }</c>, so a read that failed on every single run for weeks was indistinguishable from
/// one that found nothing to do — which is exactly how nine sessions stayed invisibly unlinked
/// while the job reported success. Callers must surface an Unknown, not swallow it.</para>
/// </summary>
public static class ExternalLinkProbe
{
    /// <summary>
    /// Probe one stored id against a live id set.
    /// </summary>
    /// <param name="liveIds">
    /// The ids the remote system currently has. <c>null</c> means the read FAILED — never treat
    /// that as "nothing exists".
    /// </param>
    /// <param name="storedId">The id we have recorded locally, if any.</param>
    /// <param name="readFailure">Why the read failed, when it did — carried into the result.</param>
    /// <param name="allowEmptyLiveSet">
    /// Whether an EMPTY live set may be believed. Defaults to <c>false</c>, because for most
    /// endpoints an empty page is indistinguishable from a silently-failed one, and believing it
    /// would unlink everything at once. Pass <c>true</c> only where the reader is strict enough
    /// that "genuinely zero records" is provable.
    /// </param>
    public static ExternalLinkResult Probe(
        IReadOnlySet<string>? liveIds,
        string? storedId,
        string? readFailure = null,
        bool allowEmptyLiveSet = false)
    {
        if (string.IsNullOrWhiteSpace(storedId))
            return new ExternalLinkResult(ExternalLinkState.Unknown, "no stored id — nothing to probe");

        if (liveIds is null)
            return new ExternalLinkResult(ExternalLinkState.Unknown,
                readFailure ?? "the remote read failed — link left untouched");

        if (liveIds.Count == 0 && !allowEmptyLiveSet)
            return new ExternalLinkResult(ExternalLinkState.Unknown,
                "the remote read returned NOTHING, which cannot be told apart from a failed read — "
                + "link left untouched");

        return liveIds.Contains(storedId)
            ? new ExternalLinkResult(ExternalLinkState.Exists, null)
            : new ExternalLinkResult(ExternalLinkState.Gone,
                $"remote record {storedId} no longer exists — it was deleted there");
    }

    /// <summary>
    /// §553 — the same rule for a PER-RECORD lookup, where the remote system answered about one id
    /// rather than handing us the whole list.
    ///
    /// <para>This is the path that does NOT need a bulk-list permission, which matters: the session
    /// self-heal was blocked entirely because the bulk agenda read needs a scope the token does not
    /// have, while per-record calls worked fine the whole time.</para>
    /// </summary>
    /// <param name="foundExplicitly">True when the remote returned the record.</param>
    /// <param name="notFoundExplicitly">
    /// True ONLY on a definite "no such record" answer (an authenticated 404 for that id) — never
    /// on a timeout, a 401/403, a 5xx or a transport error.
    /// </param>
    public static ExternalLinkResult ProbeOne(
        bool foundExplicitly, bool notFoundExplicitly, string? detail = null)
    {
        if (foundExplicitly) return new ExternalLinkResult(ExternalLinkState.Exists, detail);
        if (notFoundExplicitly) return new ExternalLinkResult(ExternalLinkState.Gone,
            detail ?? "the remote returned 'not found' for this id — it was deleted there");
        return new ExternalLinkResult(ExternalLinkState.Unknown,
            detail ?? "the remote did not give a definite answer — link left untouched");
    }
}
