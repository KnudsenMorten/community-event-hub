using System.Reflection;
using PdfSharp.Fonts;

namespace CommunityHub.Core.Evaluation;

/// <summary>
/// §750.7 — supplies the evaluation report's fonts from EMBEDDED resources instead of from the
/// operating system.
/// </summary>
/// <remarks>
/// <para>🔴 <b>Why this exists.</b> PdfSharpCore's default resolver asks the OS for a font family.
/// An Azure Linux container has none, so every render on PROD threw
/// <c>System.IO.FileNotFoundException: No Fonts installed on this device!</c> — three of them, one
/// per session, the first time the publish job had real data to work with. The report had therefore
/// never been able to render anywhere but a developer's machine.</para>
///
/// <para>🔑 <b>Why the tests could not catch it.</b> They run on Windows, where Arial exists. The
/// failure lives entirely in the gap between the dev box and the deployed container, which is the
/// same shape as the §744.1 cascade-path trap: real infrastructure behaves differently from the
/// substitute the tests use, so only a deployed render proves it.</para>
///
/// <para>🔒 <b>Embedding removes the OS from the path.</b> The bytes travel inside the assembly, so
/// the report renders identically on a laptop, in the web app and in the Functions host — and a
/// future base-image change cannot silently take the fonts away again.</para>
///
/// <para>Open Sans, SIL OFL 1.1 (redistribution permitted; the licence ships beside the files as
/// <c>OFL-OpenSans.txt</c>). Chosen over DejaVu for size — 147 KB per face against 757 KB — since
/// this is carried in every deployment of Core.</para>
/// </remarks>
public sealed class EmbeddedFontResolver : IFontResolver
{
    /// <summary>The one family name the report asks for. Anything else still resolves to it.</summary>
    public const string FamilyName = "Open Sans";

    private const string RegularKey = "OpenSans#Regular";
    private const string BoldKey = "OpenSans#Bold";

    private static readonly Assembly Owner = typeof(EmbeddedFontResolver).Assembly;

    /// <inheritdoc />
    public string DefaultFontName => FamilyName;

    /// <summary>
    /// 🔒 Installed ONCE, and idempotently. <c>GlobalFontSettings.FontResolver</c> is process-wide
    /// static: assigning it twice is harmless but assigning it from two threads mid-render is not,
    /// so every entry point calls this rather than setting the property itself.
    /// </summary>
    public static void EnsureInstalled()
    {
        if (GlobalFontSettings.FontResolver is EmbeddedFontResolver) return;
        lock (Owner)
        {
            // 🔴 ASSIGNED, not `??=`. The first version used `??=` and PROD kept throwing
            // "No Fonts installed on this device!" AFTER the fix shipped — because PdfSharpCore had
            // already put its own OS-reading resolver in the slot, so the null-coalescing assignment
            // silently did nothing and the container still had no fonts to find.
            // ⇒ Whatever is there, ours replaces it. There is no case where deferring to a resolver
            // that cannot see a font file is the better outcome.
            if (GlobalFontSettings.FontResolver is not EmbeddedFontResolver)
            {
                GlobalFontSettings.FontResolver = new EmbeddedFontResolver();
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 🔑 <b>Every request resolves</b>, whatever family was asked for. Returning null here is what
    /// produced the production crash in the first place, and a report that renders in a slightly
    /// wrong face is enormously better than one that throws on the send path.
    /// </remarks>
    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic) =>
        new(isBold ? BoldKey : RegularKey);

    /// <summary>
    /// §750.8 — the Experts Live Denmark wordmark, embedded beside the fonts.
    /// </summary>
    /// <remarks>
    /// 🔑 Returns null rather than throwing when it is missing. The report is a speaker's record of
    /// their session; losing it because a decorative asset could not be read would be the wrong
    /// trade, so the caller draws the mark if it is there and carries on if it is not.
    /// </remarks>
    public static byte[]? LogoBytes()
    {
        using var stream = Owner.GetManifestResourceStream(
            "CommunityHub.Core.Evaluation.Fonts.logo-eldk.png");
        if (stream is null) return null;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    /// <inheritdoc />
    public byte[]? GetFont(string faceName)
    {
        var resource = faceName == BoldKey
            ? "CommunityHub.Core.Evaluation.Fonts.OpenSans-Bold.ttf"
            : "CommunityHub.Core.Evaluation.Fonts.OpenSans-Regular.ttf";

        using var stream = Owner.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException(
                $"Embedded font '{resource}' is missing from {Owner.GetName().Name}. It is declared "
                + "as an EmbeddedResource in CommunityHub.Core.csproj — if that entry was removed, "
                + "every evaluation report will fail to render on Linux.");

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
