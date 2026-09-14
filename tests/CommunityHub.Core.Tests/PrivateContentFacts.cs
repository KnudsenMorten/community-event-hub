using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// 2026-09-14 — tells a test run in the upstream private repository apart from one in the public
/// template (or a community's fork of it).
/// </summary>
/// <remarks>
/// <para>The public template ships a NEUTRAL default <c>config/</c> set, marked by
/// <c>config/PUBLIC-TEMPLATE.md</c>, and none of the maintainers' internal documents. Almost every
/// content test is structural and runs against whichever set is present. A few pin the upstream
/// conference's OWN wording or internal docs; those use <see cref="PrivateContentFactAttribute"/> /
/// <see cref="PrivateContentTheoryAttribute"/> and are skipped — visibly, with a reason — only where
/// that content genuinely is not there.</para>
///
/// <para>🔒 The switch is a file that EXISTS in the template, never a file that is merely missing:
/// a deleted or moved private file must keep failing loudly in the private repository.</para>
/// </remarks>
internal static class PrivateContent
{
    public const string TemplateSkipReason =
        "Pins the upstream conference's own content; this checkout carries the public default set "
        + "(config/PUBLIC-TEMPLATE.md).";

    public const string InternalDocsSkipReason =
        "Checks the maintainers' internal documents, which are not part of the public template.";

    public static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CommunityHub.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>True when <c>config/</c> is the public template's default set.</summary>
    public static bool IsPublicTemplate =>
        RepoRoot() is { } root && File.Exists(Path.Combine(root, "config", "PUBLIC-TEMPLATE.md"));

    /// <summary>True when the maintainers' internal docs are absent (the public template ships none).</summary>
    public static bool InternalDocsAbsent =>
        IsPublicTemplate && RepoRoot() is { } root && !File.Exists(Path.Combine(root, "docs", "TESTS.md"));
}

/// <summary>A fact that pins the upstream conference's own content — skipped on the public default set.</summary>
public sealed class PrivateContentFactAttribute : FactAttribute
{
    public PrivateContentFactAttribute()
    {
        if (PrivateContent.IsPublicTemplate) Skip = PrivateContent.TemplateSkipReason;
    }
}

/// <summary>A theory that pins the upstream conference's own content — skipped on the public default set.</summary>
public sealed class PrivateContentTheoryAttribute : TheoryAttribute
{
    public PrivateContentTheoryAttribute()
    {
        if (PrivateContent.IsPublicTemplate) Skip = PrivateContent.TemplateSkipReason;
    }
}

/// <summary>A fact over the maintainers' internal documents — skipped in the public template.</summary>
public sealed class InternalDocsFactAttribute : FactAttribute
{
    public InternalDocsFactAttribute()
    {
        if (PrivateContent.InternalDocsAbsent) Skip = PrivateContent.InternalDocsSkipReason;
    }
}
