namespace CommunityHub.Core.Integrations.Erp;

/// <summary>The result of validating a company tax-id (CVR).</summary>
/// <param name="IsValid">True when the value passes every applied check.</param>
/// <param name="Normalized">The digits-only normalized value (empty when not parseable).</param>
/// <param name="Reason">A short machine-friendly reason when invalid (e.g. "empty", "wrong-length", "checksum").</param>
/// <param name="RegistryChecked">True if an external registry lookup was performed (vs. format/checksum only).</param>
public sealed record CvrValidationResult(
    bool IsValid,
    string Normalized,
    string? Reason,
    bool RegistryChecked);

/// <summary>
/// Validates a company tax-id (Danish CVR) at sponsor-create time
/// (REQUIREMENTS §7a — "Company tax-ID (CVR) validation on sponsor create").
///
/// The format + checksum gate is <b>pure offline logic</b> (Danish CVR is an
/// 8-digit modulus-11 number) and is always applied. An optional external
/// registry lookup (does this CVR exist + is it active in the CVR register) is
/// behind <see cref="IExternalCvrLookup"/>; that lookup needs an operator
/// endpoint/key and is flagged ◻ until wired.
/// </summary>
public interface ICvrValidator
{
    /// <summary>
    /// Validate a raw CVR string. Always runs the format + modulus-11 checksum
    /// gate; additionally consults <see cref="IExternalCvrLookup"/> when it is
    /// available + enabled.
    /// </summary>
    Task<CvrValidationResult> ValidateAsync(string? rawCvr, CancellationToken ct = default);
}

/// <summary>
/// External CVR registry lookup seam (e.g. the Danish CVR / Virk register).
/// The live implementation needs an operator endpoint + key (config holds the
/// secret NAME only) and is not wired in this repo — flagged ◻. A null/disabled
/// lookup means <see cref="ICvrValidator"/> applies the offline gate only.
/// </summary>
public interface IExternalCvrLookup
{
    /// <summary>Whether a live registry lookup can be performed.</summary>
    bool CanLookup { get; }

    /// <summary>
    /// True if the (already format-valid, normalized) CVR exists + is active in
    /// the external register. Only called when <see cref="CanLookup"/> is true.
    /// </summary>
    Task<bool> ExistsAndActiveAsync(string normalizedCvr, CancellationToken ct);
}
