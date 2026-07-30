namespace CommunityHub.Core.Domain;

/// <summary>
/// A small, FLEET-WIDE (not edition-scoped) health marker for one background job,
/// keyed by a stable <see cref="JobKey"/> (e.g. <c>erp-webshop-reconcile</c>). It
/// tracks how many times the job has failed IN A ROW so a single transient glitch
/// (a backup window, a momentary upstream 5xx) does not page the operator — only a
/// SECOND consecutive failure does (see <c>CommunityHub.Core.Diagnostics.JobFailureTracker</c>).
///
/// One row per job key (unique). Unlike <see cref="SyncRun"/> this is deliberately
/// NOT tied to an <see cref="Event"/>: the reconcile engines run once for the whole
/// fleet, so their health is a single global counter, not a per-edition one. No
/// secrets are ever stored here.
/// </summary>
public class JobHealthMarker
{
    public int Id { get; set; }

    /// <summary>The stable job identifier (kebab-case, e.g. <c>erp-webshop-reconcile</c>).</summary>
    public string JobKey { get; set; } = string.Empty;

    /// <summary>
    /// How many times the job has failed CONSECUTIVELY (reset to 0 on the next
    /// success). The alert gate fires when this reaches the threshold (2).
    /// </summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>When the job last completed successfully (UTC), or null if never.</summary>
    public DateTimeOffset? LastSuccessAt { get; set; }

    /// <summary>When the job last failed (UTC), or null if it has not failed since the last reset.</summary>
    public DateTimeOffset? LastFailureAt { get; set; }

    /// <summary>A short message from the last failure (truncated), for observability.</summary>
    public string? LastError { get; set; }

    /// <summary>
    /// §545(b) INACTIVE — how many times IN A ROW this job has run cleanly while deliberately
    /// doing nothing. Reset to 0 the moment it does real work.
    /// </summary>
    /// <remarks>
    /// <see cref="ConsecutiveFailures"/> could never see the §544 incident: the session push was
    /// switched off by a setting, said so every run for weeks at Information level, and SUCCEEDED
    /// every time — until 1,500 attendees had no agenda. This counter is what makes "green and
    /// doing nothing" a countable, alertable state instead of a log line nobody reads.
    /// </remarks>
    public int ConsecutiveNoOps { get; set; }

    /// <summary>
    /// The reason the job gave for doing nothing on its last run ("feature off", "Zoho disabled",
    /// "no active edition"), or null when it last did work. Written for the operator to read in the
    /// alert, not for a log grep.
    /// </summary>
    public string? LastNoOpReason { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
