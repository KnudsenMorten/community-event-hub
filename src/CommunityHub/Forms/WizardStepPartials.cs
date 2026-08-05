namespace CommunityHub.Forms;

/// <summary>
/// §708.10a — where a step handler's <see cref="IWizardStepHandler.PartialName"/> actually lives.
/// </summary>
/// <remarks>
/// <para>🔴 <b>This exists because of a live break.</b> Most handlers return a BARE partial name
/// (<c>_HotelFields</c>) and those partials sit in <c>/Pages/Forms/</c>, next to the wizard. Razor
/// resolves a bare name against the CURRENT page's folder, then <c>/Pages/</c>, then
/// <c>/Pages/Shared/</c> — it never looks in <c>/Pages/Forms/</c>. So the moment a SECOND host
/// outside that folder rendered one (the <c>/Tasks</c> row, §708.10), every bare name threw
/// <i>"The partial view '_HotelFields' was not found"</i> and took the whole task list down with it —
/// for volunteers, media, event partners AND organizers.</para>
///
/// <para>🔒 <b>Both shapes are real and must both keep working.</b> Two handlers already return a
/// FULL path (<c>/Pages/Speaker/_DetailsFields.cshtml</c>,
/// <c>/Pages/Volunteer/_AvailabilityFields.cshtml</c>) precisely because their partials are not in
/// the wizard's folder. A path passes through untouched; a bare name is qualified.</para>
///
/// <para>⚠️ <b>Why a function and not an inline expression in the row.</b> Razor markup is not
/// reachable from a test, and this is exactly the rule that was wrong in production. As a plain
/// static it is pinned by <c>WizardStepPartialResolutionTests</c>, which resolves EVERY declared
/// partial name against the files on disk.</para>
/// </remarks>
public static class WizardStepPartials
{
    /// <summary>The folder holding the bare-named fields partials (the wizard's own folder).</summary>
    public const string FieldsFolder = "/Pages/Forms";

    /// <summary>
    /// The renderable partial path for <paramref name="partialName"/> — unchanged when it is already
    /// a path, qualified to <see cref="FieldsFolder"/> when it is a bare name.
    /// </summary>
    public static string PathFor(string partialName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partialName);

        return partialName.Contains('/', StringComparison.Ordinal)
            ? partialName
            : $"{FieldsFolder}/{partialName}.cshtml";
    }
}
