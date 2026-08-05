using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations.DocLibrary;

/// <summary>
/// Startup validation for the document library: say plainly, once, at boot, which key is wrong.
/// </summary>
/// <remarks>
/// <para>🔒 <b>Why this exists at all.</b> A misconfigured folder does not throw. The store returns
/// an empty listing, the caller reads that as "nothing to do", and the feature reports success while
/// doing nothing. §767's sweep ran four production cycles logging <i>"0 track GIF(s) … waiting for a
/// photo"</i> against a folder full of photos, and went inert again after the library was
/// reorganised — both times with no error anywhere. <b>Silent fallback to a default path is how the
/// drift happened</b>, so the answer is to complain at boot, by name.</para>
///
/// <para>⚠️ <b>It logs; it does not kill the host by default.</b> A hard throw would take the whole
/// hub down — including sign-in, the agenda and every page that never touches the library — because
/// one folder is unset. That trade is wrong for an event platform mid-event. <c>throwOnError</c> is
/// available for environments that prefer it, and the log line is unmissable either way.</para>
/// </remarks>
public static class DocLibraryStartupCheck
{
    /// <summary>Validate and report. Returns the problems found (empty ⇒ sound).</summary>
    public static IReadOnlyList<string> Run(
        IDocLibraryPathResolver resolver, ILogger logger, bool throwOnError = false)
    {
        var problems = resolver.Validate();

        if (problems.Count == 0)
        {
            if (resolver.IsConfigured)
            {
                var resolved = resolver.All();
                logger.LogInformation(
                    "DocLibrary: configuration OK — {Active} active path(s) resolve, {Planned} "
                    + "planned, {Overridden} overridden from defaults.",
                    resolved.Count(r => r.Definition.Status == DocLibraryPathStatus.Active),
                    resolved.Count(r => r.Definition.Status == DocLibraryPathStatus.Planned),
                    resolved.Count(r => r.IsOverridden));
            }
            else
            {
                // INERT AND SAID SO. An unconfigured library is a legitimate state (a fresh clone,
                // a test host) — but it must never be mistaken for a working one.
                logger.LogWarning(
                    "DocLibrary: NOT CONFIGURED — every document-library feature is inert. "
                    + "Set {Section}:Enabled, :SiteUrl and :RootFolderPath to activate it.",
                    DocLibraryOptions.SectionName);
            }
            return problems;
        }

        foreach (var p in problems)
            logger.LogError("DocLibrary configuration problem: {Problem}", p);

        logger.LogError(
            "DocLibrary: {Count} configuration problem(s) — the features using those paths will be "
            + "INERT, not broken, so nothing will throw and nothing will be written. Fix the keys "
            + "named above.", problems.Count);

        if (throwOnError)
            throw new InvalidOperationException(
                $"DocLibrary configuration invalid: {string.Join(" | ", problems)}");

        return problems;
    }
}
