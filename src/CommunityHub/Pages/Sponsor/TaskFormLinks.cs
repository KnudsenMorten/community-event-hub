namespace CommunityHub.Pages.Sponsor;

/// <summary>
/// §651 — where a form-backed task's button actually goes.
/// </summary>
/// <remarks>
/// <para>🔴 <b>Why this indirection exists.</b> §648 sent the task straight to
/// <c>/Forms/Wizard?step={key}</c>, on the strength of a §495 comment promising the deep link "still
/// works". It did not: §495 had removed the session step from the sponsor wizard's step PLAN, and
/// the wizard host can only select a step that is in the plan — so the sponsor landed on an
/// unrelated form. Operator: *"bug: it takes to wrong form in get started"*.</para>
///
/// <para>A form key is therefore NOT automatically a wizard route. This maps each key to the page
/// that really hosts it, so the two can never be assumed equal again — and an unmapped key falls
/// back to the sponsor's task list rather than to a wizard step that may not exist.</para>
/// </remarks>
public static class TaskFormLinks
{
    /// <summary>The page that hosts the form for <paramref name="formStepKey"/>.</summary>
    public static string PageFor(string? formStepKey) => (formStepKey ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        // The sponsor speaking-session form (§292/§356), on its own page since §495 took it out of
        // Get Started.
        "session" => "/Sponsor/SessionForm",

        // §688.12 — the friendly CONTACTS form (signers + event coordinators), which the operator
        // pointed at directly: /Forms/Wizard?step=contacts. It replaces sending people to the raw
        // editor on Company Details.
        //
        // 🔒 VERIFIED IN THE PLAN before linking, which is the whole point of this class.
        // `SponsorWizardService:145` adds "contacts" to the sponsor step plan, so the deep link
        // resolves. Contrast `booth-members`: §474 deliberately REMOVED it from the plan (a sponsor
        // onboarding ~6 months out cannot name booth staff yet), so deep-linking THAT would fall
        // through to an unrelated step — which is §651 exactly, and would have been its second
        // occurrence. A form key is not automatically a wizard route; this one was checked.
        "contacts" => "/Forms/Wizard?step=contacts",

        // 🔒 Unknown key ⇒ somewhere real and harmless. Guessing a wizard route is what broke this.
        _ => "/Sponsor/Tasks",
    };

    /// <summary>True when we know a real page for this key.</summary>
    public static bool IsKnown(string? formStepKey) =>
        !string.IsNullOrWhiteSpace(formStepKey)
        && PageFor(formStepKey) != "/Sponsor/Tasks";
}
