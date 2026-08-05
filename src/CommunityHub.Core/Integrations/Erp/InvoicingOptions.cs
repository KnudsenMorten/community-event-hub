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

        return options;
    }
}
