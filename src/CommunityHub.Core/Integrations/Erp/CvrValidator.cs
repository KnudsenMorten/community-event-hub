using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations.Erp;

/// <summary>
/// Danish CVR validator: 8 digits + a modulus-11 checksum, computed entirely
/// offline. Optionally consults an <see cref="IExternalCvrLookup"/> when one is
/// available + enabled (◻ until live registry creds are wired).
///
/// Modulus-11 for CVR: weights 2,7,6,5,4,3,2 over the first seven digits; the
/// weighted sum mod 11 gives a remainder <c>r</c>; the check digit is
/// <c>11 - r</c> (with the standard rules: r==0 → check 0; r==1 is invalid for
/// CVR because it would need a two-digit check). The eighth digit must equal
/// that check digit.
/// </summary>
public sealed class CvrValidator : ICvrValidator
{
    private static readonly int[] Weights = { 2, 7, 6, 5, 4, 3, 2 };

    private readonly IExternalCvrLookup? _lookup;
    private readonly ILogger<CvrValidator>? _log;

    public CvrValidator(IExternalCvrLookup? lookup = null, ILogger<CvrValidator>? log = null)
    {
        _lookup = lookup;
        _log = log;
    }

    public async Task<CvrValidationResult> ValidateAsync(string? rawCvr, CancellationToken ct = default)
    {
        var normalized = Normalize(rawCvr);

        if (normalized.Length == 0)
        {
            return new CvrValidationResult(false, string.Empty, "empty", RegistryChecked: false);
        }

        if (normalized.Length != 8)
        {
            return new CvrValidationResult(false, normalized, "wrong-length", RegistryChecked: false);
        }

        if (!PassesModulus11(normalized))
        {
            return new CvrValidationResult(false, normalized, "checksum", RegistryChecked: false);
        }

        // Offline gate passed. Consult the external register only if it is wired.
        if (_lookup is { CanLookup: true })
        {
            bool exists;
            try
            {
                exists = await _lookup.ExistsAndActiveAsync(normalized, ct);
            }
            catch (Exception ex)
            {
                // A registry outage must not block sponsor creation: the offline
                // gate already passed. Report registry-unchecked rather than failing.
                _log?.LogWarning(ex, "CVR registry lookup failed for {Cvr}; falling back to offline gate.", normalized);
                return new CvrValidationResult(true, normalized, Reason: null, RegistryChecked: false);
            }

            return exists
                ? new CvrValidationResult(true, normalized, Reason: null, RegistryChecked: true)
                : new CvrValidationResult(false, normalized, "not-in-register", RegistryChecked: true);
        }

        return new CvrValidationResult(true, normalized, Reason: null, RegistryChecked: false);
    }

    /// <summary>Strip spaces, dots, a leading "DK" VAT prefix, etc. — keep digits only.</summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            if (char.IsDigit(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Modulus-11 checksum gate for an already-normalized 8-digit CVR.</summary>
    public static bool PassesModulus11(string normalized8)
    {
        if (normalized8.Length != 8) return false;

        var sum = 0;
        for (var i = 0; i < 7; i++)
        {
            sum += (normalized8[i] - '0') * Weights[i];
        }

        var remainder = sum % 11;
        // remainder == 1 would require a two-digit check digit → invalid for CVR.
        if (remainder == 1) return false;

        var check = remainder == 0 ? 0 : 11 - remainder;
        return (normalized8[7] - '0') == check;
    }
}
