using System.Text.RegularExpressions;
using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Assistant;

/// <summary>
/// Pure, dependency-free intent detector for the AiHelper INTAKE flow (REQUIREMENTS §137).
/// Classifies a user's message as a BUG/ERROR report, a FEATURE request, or neither, using
/// the configurable keyword set in <see cref="FeedbackIntakeOptions"/>.
///
/// Matching is CASE-INSENSITIVE and WORD-BOUNDARY anchored so a keyword only fires as a
/// whole word/phrase ("bug" matches "found a bug", not "debugger"; "fejl" matches but not
/// "fejlfri" — actually \b handles the boundary). Bug takes precedence over feature when a
/// message trips both (a reported breakage is the more urgent signal). Question routing is
/// NOT keyword-driven — it is an explicit user action handled by the service.
/// </summary>
public sealed class FeedbackIntakeDetector
{
    private readonly Regex[] _bug;
    private readonly Regex[] _feature;

    public FeedbackIntakeDetector(FeedbackIntakeOptions options)
    {
        _bug = Compile(options.BugKeywords);
        _feature = Compile(options.FeatureKeywords);
    }

    private static Regex[] Compile(IEnumerable<string>? keywords) =>
        (keywords ?? Array.Empty<string>())
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => new Regex(
                $@"\b{Regex.Escape(k.Trim())}\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled))
            .ToArray();

    /// <summary>
    /// The kind this message reports, or null when it is an ordinary question/statement.
    /// </summary>
    public FeedbackKind? Detect(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        if (_bug.Any(r => r.IsMatch(message))) return FeedbackKind.Bug;
        if (_feature.Any(r => r.IsMatch(message))) return FeedbackKind.Feature;
        return null;
    }
}
