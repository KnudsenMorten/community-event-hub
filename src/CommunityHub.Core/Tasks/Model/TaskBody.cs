namespace CommunityHub.Core.Tasks.Model;

/// <summary>
/// Something the parser could not accept. Collected rather than thrown.
/// </summary>
/// <remarks>
/// 🔒 §682 is the scar: one over-long paragraph in <c>sponsor.eldk27.json</c> threw during the
/// WooCommerce pull and took the WHOLE sponsor order sync down with it. Authored content must never
/// be able to break a runtime path — so a malformed body degrades to "that bit renders as nothing"
/// and reports WHY, while <c>TaskBodyCatalogTests</c> turns the same diagnostic into a BUILD
/// FAILURE. Loud at build time, harmless at runtime.
/// </remarks>
/// <param name="Line">1-based line number in the authored source.</param>
/// <param name="Message">What is wrong, in terms an author can act on.</param>
public sealed record TaskBodyDiagnostic(int Line, string Message)
{
    public override string ToString() => $"line {Line}: {Message}";
}

/// <summary>
/// A parsed task body — the blocks, plus anything the parser could not accept.
/// </summary>
public sealed record TaskBody(
    IReadOnlyList<TaskBlock> Blocks,
    IReadOnlyList<TaskBodyDiagnostic> Diagnostics)
{
    /// <summary>An empty body — a missing file renders as nothing, never as an exception.</summary>
    public static readonly TaskBody Empty =
        new(Array.Empty<TaskBlock>(), Array.Empty<TaskBodyDiagnostic>());

    public bool IsValid => Diagnostics.Count == 0;
}
