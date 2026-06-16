namespace CommunityHub.Core.Resources;

/// <summary>
/// Marker type for the app-wide shared localization resource. ASP.NET Core's
/// <c>IStringLocalizer&lt;SharedResource&gt;</c> resolves strings from the
/// matching <c>SharedResource.resx</c> (English, the default/invariant fallback)
/// and per-culture satellites such as <c>SharedResource.da-DK.resx</c>.
///
/// This marker lives in the <c>CommunityHub.Core.Resources</c> namespace and the
/// .resx sit beside it under <c>Resources/</c>, so the embedded resource base
/// name already equals the full type name. Wire localization with an EMPTY
/// <c>ResourcesPath</c> (the default <c>AddLocalization()</c>) — a non-empty path
/// would be prefixed a second time and every lookup would miss.
///
/// A single shared resource (rather than one .resx per page) keeps the
/// participant-facing copy in one place — most strings (nav, buttons, status
/// banners) repeat across the role hubs, so per-page files would duplicate them.
/// Pages reference keys via the <c>Localizer</c> injected in <c>_ViewImports</c>.
///
/// i18n is a meaningful first slice: the high-traffic participant pages
/// (Login/PIN, role hub, My tasks, My Event, Speaker hub) are externalized here
/// with English + Danish; deeper pages remain English-only and are tracked as a
/// ◻ follow-up in REQUIREMENTS. No schema/DB involvement — resources + markup
/// only.
/// </summary>
public sealed class SharedResource
{
}
