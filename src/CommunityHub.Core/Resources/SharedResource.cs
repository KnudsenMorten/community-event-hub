namespace CommunityHub.Core.Resources;

/// <summary>
/// Marker type for the app-wide shared localization resource. ASP.NET Core's
/// <c>IStringLocalizer&lt;SharedResource&gt;</c> resolves strings from the
/// matching <c>SharedResource.resx</c> (English, the default/invariant
/// resource). The hub is English-only; to add a language, drop a matching
/// <c>SharedResource.&lt;culture&gt;.resx</c> satellite beside it.
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
/// Participant-facing copy is externalized here in English. No schema/DB
/// involvement — resources + markup only.
/// </summary>
public sealed class SharedResource
{
}
