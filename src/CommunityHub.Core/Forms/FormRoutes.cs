namespace CommunityHub.Core.Forms;

/// <summary>
/// 🔒 §708.2a — THE canonical route of every self-service form. ONE place, reused by every surface.
/// </summary>
/// <remarks>
/// <para><b>The operator's decision</b> (§708.2a): <i>"register/update stays as a quicklink but make
/// it as smart as possible to reuse links or services"</i>. §708.1 had established that the CODE is
/// already shared — one service, one <c>_*Fields</c> partial, N hosts — and §708.2 found that what is
/// duplicated is the ENTRANCES. For the hotel form alone there were THREE: the Register/Update menu
/// entry, the Get Started wizard step, and an <b>absolute, edition-specific URL hardcoded in
/// <c>config/speaker-deadlines.eldk27.json</c></b>.</para>
///
/// <para>🔑 <b>The acceptance test he set:</b> <i>"if changing a form's route requires editing more
/// than one place it is not smart enough yet"</i>. That is what this class exists to satisfy — and it
/// is why the routes are not simply left as string literals in <c>NavBuilder</c>: they were, and the
/// third copy drifted into a config file where no compiler could see it.</para>
///
/// <para><b>What is NOT a second route.</b> A surface is either a HOST or a LINK to the canonical
/// route — never a third thing (§708.1):</para>
/// <list type="bullet">
///   <item><description>the Get Started wizard STEP is a HOST — it renders the same fields partial
///     against the same service, so it is not a competing route;</description></item>
///   <item><description>a task with the form EMBEDDED needs no link at all — the form is simply
///     there;</description></item>
///   <item><description>a task body that must point somewhere names the key here, never a
///     path.</description></item>
/// </list>
/// </remarks>
public static class FormRoutes
{
    /// <summary>Hotel booking (<c>hotel</c>).</summary>
    public const string Hotel = "/Forms/Hotel";

    /// <summary>Appreciation dinner sign-up (<c>dinner</c>).</summary>
    public const string Dinner = "/Forms/Dinner";

    /// <summary>Pre-day lunch registration (<c>lunch</c>).</summary>
    public const string Lunch = "/Forms/Lunch";

    /// <summary>Swag / speaker gift preferences (<c>swag</c>).</summary>
    public const string Swag = "/Forms/Swag";

    /// <summary>Travel reimbursement claim (<c>travel</c>).</summary>
    public const string Travel = "/Forms/Travel";

    /// <summary>
    /// The canonical route for a form STEP KEY — the same keys
    /// <c>TaskCompletion.Form</c> and the wizard step plan already use.
    /// </summary>
    /// <remarks>
    /// 🔒 <b>An unknown key answers <c>null</c>, never a guess.</b> §651 is the precedent: a form key
    /// was assumed to be a wizard route, the step was not in the plan, and the sponsor silently
    /// landed on an unrelated form. A caller that gets null must render no button rather than a
    /// button to somewhere plausible — <c>SpeakerTaskPlaceholderBuilder</c> reports the miss so an
    /// unresolved key is loud (§688.4: a button with no destination is a defect, not a value).
    /// </remarks>
    public static string? For(string? formKey) =>
        (formKey ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "hotel" => Hotel,
            "dinner" => Dinner,
            "lunch" => Lunch,
            "swag" => Swag,
            "travel" => Travel,
            _ => null,
        };
}
