namespace CommunityHub.Core.Integrations.Erp;

/// <summary>
/// Today's FX rate lookup seam (REQUIREMENTS §7a — "Currency check on order
/// creation (today's FX)"). The live provider needs an operator endpoint
/// (e.g. an FX/central-bank rates API; config holds the secret NAME only) and
/// is flagged ◻ until wired. A null/disabled provider means the order-creation
/// currency check is a same-currency / known-currency gate only (no conversion).
/// </summary>
public interface IFxRateProvider
{
    /// <summary>Whether a live rate can be fetched.</summary>
    bool CanQuote { get; }

    /// <summary>
    /// The number of units of <paramref name="quoteCurrency"/> per one unit of
    /// <paramref name="baseCurrency"/> for today, or null when it cannot be
    /// resolved. Returns 1 when the currencies are equal. Only called when
    /// <see cref="CanQuote"/> is true.
    /// </summary>
    Task<decimal?> GetRateAsync(string baseCurrency, string quoteCurrency, CancellationToken ct);
}
