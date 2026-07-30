namespace CommunityHub.Core.Participants;

/// <summary>
/// §410 — which tasks belong on the wizard's "Your tasks &amp; deadlines" step (§400): the ones that
/// live OUTSIDE Get Started.
///
/// <para><b>Why this exists.</b> §173e mirrors every wizard STEP with a matching task, so the
/// participant checklist legitimately contains both kinds. §400 then built its step from that
/// checklist unfiltered — and for a role whose only tasks ARE wizard mirrors (an attendee has
/// exactly <c>party-form</c> and <c>masterclass-form</c>) the step listed the person's own wizard
/// steps back at them, one screen after they had filled them in. The operator hit it immediately:
/// <i>"for any roles, that does NOT have any tasks outside the get started, you must remove the
/// step 3 'your tasks &amp; deadlines'"</i>.</para>
///
/// <para><b>Why an EXCLUSION list rather than a role list.</b> His own conclusion was "only speakers,
/// organizers, sponsors" — correct today, and it would go stale the first time a volunteer or media
/// deadline is added. Keying on the DATA means the step appears exactly when the person genuinely
/// has something outside the wizard, whoever they are, with nothing to remember to update.</para>
///
/// <para>Verified against production (2026-07-27): the prefixes in use split cleanly into
/// wizard-owned — <c>party-form</c>, <c>masterclass-form</c>, <c>hotel-form</c>, <c>dinner-form</c>,
/// <c>lunch-form</c>, <c>swag-form</c>, <c>volunteer-form</c>, <c>accept</c>, <c>profile</c>,
/// <c>signal</c>, <c>availability</c>, <c>speaker-details</c>, <c>travel</c> — and genuinely
/// outside it: <c>speakerdl</c> (149 rows, all dated), <c>sponsor</c> (110, all dated),
/// <c>woo</c>.</para>
/// </summary>
public static class OutsideWizardTasks
{
    /// <summary>
    /// SourceKey prefixes OWNED by a Get-Started wizard step. A task carrying one of these is the
    /// step's own mirror, so showing it on the deadlines step would duplicate the wizard.
    ///
    /// <para><c>travel</c> is here deliberately: the travel form IS a wizard step (§399 gates it by
    /// country), so its task is a mirror like the rest — even though its key is
    /// <c>travel:submit-ticket-invoice</c> rather than <c>travel-form</c>.</para>
    /// </summary>
    private static readonly string[] WizardOwnedPrefixes =
    {
        "party-form", "masterclass-form", "hotel-form", "dinner-form", "lunch-form",
        "swag-form", "volunteer-form", "accept", "profile", "signal", "availability",
        "speaker-details", "speaker-form", "travel",
    };

    /// <summary>
    /// True when this task lives OUTSIDE the Get-Started wizard and therefore belongs on the
    /// deadlines step.
    ///
    /// <para>A null/blank SourceKey counts as OUTSIDE. That direction is deliberate: an unkeyed task
    /// is almost certainly hand-created by an organizer for one person, and hiding a real obligation
    /// is a worse failure than showing one row too many — the person can act on a duplicate, but
    /// cannot act on something they never saw.</para>
    /// </summary>
    public static bool IsOutsideWizard(string? sourceKey)
    {
        if (string.IsNullOrWhiteSpace(sourceKey)) return true;

        // Prefix match up to the first ':' so "hotel-form:42" and "travel:submit-ticket-invoice"
        // are both recognised without matching a longer key that merely starts the same way.
        var head = sourceKey.Split(':', 2)[0];
        foreach (var owned in WizardOwnedPrefixes)
        {
            if (string.Equals(head, owned, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }
}
