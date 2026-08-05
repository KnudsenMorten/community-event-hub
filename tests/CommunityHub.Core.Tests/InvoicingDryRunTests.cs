using CommunityHub.Core.Integrations.Erp;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §788 — the dry-run switch shared by BOTH invoice modules (operator: <i>"dryrun is for both
/// invoice modules"</i>).
/// </summary>
/// <remarks>
/// <para>🔒 The property this pins is not "the default is true" as a matter of taste — it is that
/// <b>the safe state survives every way the setting can be absent or malformed</b>. Every test here
/// is a way a real environment could accidentally start writing invoices: no section at all, an
/// empty section, a blank value, a differently-cased key.</para>
///
/// <para>This is cheap to get wrong in a way nothing else catches: an environment that silently
/// binds <c>DryRun</c> to false does not fail, log, or look different — it just starts sending
/// invoices to customers.</para>
/// </remarks>
public sealed class InvoicingDryRunTests
{
    /// <summary>Exercises the SAME reader both hosts use — not a re-implementation of it.</summary>
    private static InvoicingOptions Bind(Dictionary<string, string?> values) =>
        InvoicingOptions.FromConfiguration(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build());

    /// <summary>
    /// The naive binding, kept ONLY to document the trap this class exists to avoid.
    /// </summary>
    private static InvoicingOptions NaiveBind(Dictionary<string, string?> values)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var options = new InvoicingOptions();
        config.GetSection(InvoicingOptions.SectionName).Bind(options);
        return options;
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("yes")]
    [InlineData("0")]
    [InlineData("off")]
    public void THE_TRAP_plain_Bind_THROWS_on_a_blank_or_unparseable_bool(string raw)
    {
        // 🔴 MEASURED, not assumed — and my first explanation of this was WRONG. I claimed Bind
        // mapped "" to false and would silently start live invoicing; it does not. It THROWS.
        //
        // The real consequence is an outage, not a wrong invoice: this binding runs in both
        // Program.cs files, so a typo'd app setting fails the web container (ValidateOnBuild →
        // staging never answers 200 → the deploy refuses to swap, §786.6) or kills the Functions
        // host on its first tick.
        //
        // Pinned so nobody "simplifies" FromConfiguration back into a .Bind() call.
        var values = new Dictionary<string, string?> { ["Invoicing:DryRun"] = raw };

        Assert.Throws<InvalidOperationException>(() => NaiveBind(values));

        // The reader turns that crash into the safe state.
        Assert.True(Bind(values).DryRun);
    }

    [Fact]
    public void DryRun_defaults_to_TRUE_on_a_bare_object()
    {
        Assert.True(new InvoicingOptions().DryRun);
    }

    [Fact]
    public void An_ABSENT_section_leaves_dry_run_ON()
    {
        // The realistic case: deployed to an environment where nobody has set anything.
        var options = Bind(new Dictionary<string, string?>());

        Assert.True(options.DryRun,
            "An unconfigured environment must NOT create invoices. Binding an absent section must "
            + "leave DryRun true, or a fresh deploy starts invoicing customers on its first tick.");
    }

    [Fact]
    public void An_UNRELATED_config_section_leaves_dry_run_ON()
    {
        var options = Bind(new Dictionary<string, string?>
        {
            ["EconomicErp:Enabled"] = "true",
            ["Invoicing:SomethingElse"] = "1",
        });

        Assert.True(options.DryRun);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("yes")]        // not a bool — must not be read as "go live"
    [InlineData("0")]
    [InlineData("off")]
    public void A_BLANK_OR_UNPARSEABLE_value_leaves_dry_run_ON(string raw)
    {
        var options = Bind(new Dictionary<string, string?> { ["Invoicing:DryRun"] = raw });

        Assert.True(options.DryRun,
            $"'{raw}' is not an explicit false, so dry run must stay ON. Anything else means a "
            + "typo in an app setting starts invoicing customers.");
    }

    [Fact]
    public void Turning_it_OFF_requires_saying_so_explicitly()
    {
        Assert.False(Bind(new Dictionary<string, string?> { ["Invoicing:DryRun"] = "false" }).DryRun);
        Assert.False(Bind(new Dictionary<string, string?> { ["Invoicing:DryRun"] = "False" }).DryRun);
    }

    [Fact]
    public void The_section_name_is_the_one_the_hosts_bind()
    {
        // Both Program.cs files bind InvoicingOptions.SectionName. If this constant changed, the
        // app settings the operator sets in Azure would silently stop being read — and the failure
        // would be "dry run turns itself back on", which looks like a bug in the job.
        Assert.Equal("Invoicing", InvoicingOptions.SectionName);
    }

    // ---------------------------------------------------------------------
    //  The run result must not report invoices that were never created
    // ---------------------------------------------------------------------

    [Fact]
    public void A_dry_run_result_reports_WOULD_create_and_never_Created()
    {
        var result = new WebshopInvoiceRunResult(
            OrdersSeen: 3, AlreadyInvoiced: 1, Created: 0, Skipped: 0,
            Problems: Array.Empty<string>(),
            DryRun: true,
            WouldCreate: new[] { "WebshopOrderId-1 → customer 42", "WebshopOrderId-2 → customer 43" });

        // 🔒 Created must stay 0. It is the number an operator takes at face value, and reporting
        // invoices that do not exist is worse than reporting none.
        Assert.Equal(0, result.Created);
        Assert.Equal(2, result.WouldCreateOrEmpty.Count);
        Assert.True(result.DryRun);
    }

    [Fact]
    public void A_normal_result_exposes_an_empty_would_create_list_rather_than_null()
    {
        // Callers enumerate this on every run; a null here would be a NullReferenceException in the
        // job's logging path, i.e. a crash caused purely by reporting.
        var result = new WebshopInvoiceRunResult(1, 0, 1, 0, Array.Empty<string>());

        Assert.NotNull(result.WouldCreateOrEmpty);
        Assert.Empty(result.WouldCreateOrEmpty);
        Assert.False(result.DryRun);
    }

    [Fact]
    public void An_inactive_result_is_not_a_dry_run()
    {
        // "The job could not run" and "the job ran but wrote nothing on purpose" are different
        // answers to "why is there no invoice", and the job reports them differently.
        var result = WebshopInvoiceRunResult.Inactive("e-conomic is not configured.");

        Assert.False(result.DryRun);
        Assert.Single(result.Problems);
        Assert.Empty(result.WouldCreateOrEmpty);
    }
}
