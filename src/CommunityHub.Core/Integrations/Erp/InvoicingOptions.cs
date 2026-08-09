using Microsoft.Extensions.Configuration;

namespace CommunityHub.Core.Integrations.Erp;

/// <summary>
/// §788 — settings shared by BOTH invoice modules: webshop orders (§786) and coupon tickets (§787).
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-04: <i>"dryrun is for both invoice modules"</i>. One setting, so a person
/// deciding "may CEH create invoices?" answers it once. Two near-identical flags is how one gets
/// flipped and the other forgotten.</para>
///
/// <para>🔒 <b>DryRun DEFAULTS TO TRUE</b>, following <c>LinkedIn:DryRun</c> (§324). The default is
/// the safe state on purpose: a fresh environment where nobody has set the flag must hold every
/// write rather than start invoicing. Going live is an explicit act.</para>
///
/// <para>⚠️ Dry run is NOT the same question as the feature switch, and both must be satisfied
/// before an invoice is created:</para>
/// <list type="number">
///   <item>the FEATURE key — does this job run at all;</item>
///   <item><see cref="DryRun"/> — may a run that DID happen actually write;</item>
///   <item><c>Integrations:AllowExternalWrites</c> (§340-H) — may this host reach a third party.</item>
/// </list>
/// <para>Each one produces "no invoices appeared", so every job here reports WHICH is holding it.
/// That is [[ceh-two-switch-trap]] handled deliberately rather than discovered.</para>
/// </remarks>
public sealed class InvoicingOptions
{
    public const string SectionName = "Invoicing";

    /// <summary>
    /// True ⇒ compose everything and report what WOULD be created, but write nothing to e-conomic.
    /// 🔒 Defaults TRUE — see the remarks.
    /// </summary>
    public bool DryRun { get; set; } = true;

    /// <summary>
    /// §1013c — the unit price (DKK, ex VAT) the prepaid-pool form PREFILLS. Operator 2026-08-09:
    /// *"unit price is DKK 3000"*.
    /// </summary>
    /// <remarks>
    /// <para>🔒 <b>A PREFILL, not a rule.</b> The field stays editable and the typed value is what
    /// is billed — §992 chose a typed price deliberately, because a prepaid pool exists before any
    /// claim, so there is no claim to read a price off, and deriving one from another partner's
    /// past claims would bill this partner at that partner's negotiated rate. This only stops him
    /// retyping the standard price every time.</para>
    ///
    /// <para>⚠️ <b>Zero disables the prefill</b> (the box opens empty, as it did before), so this
    /// can be switched off with a setting rather than a deploy if a future edition prices
    /// differently. It is config for the same reason the track map is: a price is an edition fact.</para>
    /// </remarks>
    public decimal DefaultPrepaidUnitPriceDkk { get; set; } = 3000m;

    /// <summary>
    /// 🔴 §1016d — how many days must pass before a coupon's accumulated claims are invoiced again.
    /// Operator 2026-08-09: *"you must batch them to every 2 weeks"*. Default **14**.
    /// </summary>
    /// <remarks>
    /// <para>🔑 <b>This paces the INVOICE, not the job.</b> The job must keep running often — it is
    /// also what notices unmapped coupons and chases them — so slowing the schedule would delay
    /// those too. The cadence therefore lives in the data (each coupon's own
    /// <c>LastInvoicedAt</c>), which also means two partners' billing periods run on their own
    /// clocks rather than all landing on whichever day the job happened to start.</para>
    ///
    /// <para>⚠️ <b>0 restores per-pass invoicing</b> (the old behaviour), so the batching can be
    /// switched off with a setting if a period ever has to be closed early.</para>
    /// </remarks>
    public int CouponInvoiceIntervalDays { get; set; } = 14;

    /// <summary>
    /// §1016c — the "early bird" terms quoted in the prepaid claim-invite mail's *extend* paragraph.
    /// Operator 2026-08-09: *"(DKK 3000/EURO390)"*, and *"config, defaulting to 3000 / 390"*.
    /// </summary>
    /// <remarks>
    /// 🔑 Config rather than literals because these are EDITION facts — next year's prices are a
    /// settings change, not a release. The EUR figure is a NEGOTIATED round number, not a live
    /// conversion of the DKK one: it is what the partner is promised, so it must not move with an
    /// exchange rate between the mail and the invoice.
    /// </remarks>
    public decimal ExtendTermsPriceDkk { get; set; } = 3000m;

    /// <inheritdoc cref="ExtendTermsPriceDkk"/>
    public decimal ExtendTermsPriceEur { get; set; } = 390m;

    /// <summary>
    /// Reads the options SAFELY. 🔒 Use this, never <c>section.Bind(new InvoicingOptions())</c>.
    /// </summary>
    /// <remarks>
    /// 🔴 <b>Why this exists, stated from MEASURED behaviour</b> (the first version of this comment
    /// asserted the wrong failure mode, so the values are recorded rather than reasoned about):
    /// <c>IConfiguration.Bind</c> <b>THROWS <see cref="InvalidOperationException"/></b> for a
    /// <see cref="bool"/> whose configured value is blank, whitespace, or anything that is not
    /// <c>true</c>/<c>false</c> — <c>""</c>, <c>"   "</c>, <c>"yes"</c>, <c>"0"</c>, <c>"off"</c>
    /// were all confirmed to throw. Only the literal <c>"true"</c>/<c>"false"</c> bind.
    ///
    /// <para>⚠️ So the danger is not a silent flip to live invoicing — it is that a TYPO in an app
    /// setting takes the host down at startup. In the web host that means the container fails to
    /// build, the staging slot never answers 200 and the deploy refuses to swap (exactly §786.6);
    /// in the Functions host it fails on the first tick.</para>
    ///
    /// <para>This reader turns that crash into the SAFE state instead: dry run stays ON unless the
    /// value explicitly and parseably says <c>false</c>. Absent, blank, whitespace and unparseable
    /// all keep invoicing held — a typo costs a held invoice, never a wrong one and never an
    /// outage.</para>
    /// </remarks>
    public static InvoicingOptions FromConfiguration(IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var options = new InvoicingOptions();
        var raw = config.GetSection(SectionName)["DryRun"];

        if (!string.IsNullOrWhiteSpace(raw) && bool.TryParse(raw.Trim(), out var parsed))
        {
            options.DryRun = parsed;
        }

        // §1013c — read the SAME defensive way as DryRun, and for the same reason: a typo'd app
        // setting must leave the shipped default in place, never take the host down at startup.
        // (Invariant culture — an app setting is not written in the reader's locale.)
        var price = config.GetSection(SectionName)["DefaultPrepaidUnitPriceDkk"];
        if (!string.IsNullOrWhiteSpace(price)
            && decimal.TryParse(price.Trim(), System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var dkk)
            && dkk >= 0m)
        {
            options.DefaultPrepaidUnitPriceDkk = dkk;
        }

        // §1016d — same defensive read. A malformed value keeps the fortnightly default rather
        // than either failing the host or silently reverting to invoice-per-claim.
        var days = config.GetSection(SectionName)["CouponInvoiceIntervalDays"];
        if (!string.IsNullOrWhiteSpace(days)
            && int.TryParse(days.Trim(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var d)
            && d >= 0)
        {
            options.CouponInvoiceIntervalDays = d;
        }

        // §1016c — the extend-terms prices, read the same defensive way.
        options.ExtendTermsPriceDkk =
            Money(config, "ExtendTermsPriceDkk") ?? options.ExtendTermsPriceDkk;
        options.ExtendTermsPriceEur =
            Money(config, "ExtendTermsPriceEur") ?? options.ExtendTermsPriceEur;

        return options;

        static decimal? Money(IConfiguration cfg, string key)
        {
            var raw = cfg.GetSection(SectionName)[key];
            return !string.IsNullOrWhiteSpace(raw)
                   && decimal.TryParse(raw.Trim(), System.Globalization.NumberStyles.Number,
                       System.Globalization.CultureInfo.InvariantCulture, out var v)
                   && v >= 0m
                ? v : null;
        }
    }
}
