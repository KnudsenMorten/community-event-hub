namespace CommunityHub.Core.Integrations;

/// <summary>
/// Central TESTMODE configuration (CONTEXT.md - TESTMODE). When
/// <see cref="Enabled"/> is true the integrations perform NO real outbound
/// writes or sends: the Backstage exhibitor sync makes no Zoho calls, and the
/// coordinator notification is routed only to <see cref="TestCoordinatorEmail"/>.
///
/// This lets the whole sponsor/exhibitor sync flow be exercised safely with a
/// known test sponsor before any live credentials or endpoints are wired.
/// </summary>
public sealed class TestModeOptions
{
    public const string SectionName = "TestMode";

    /// <summary>
    /// Master switch. <b>Default STAYS <c>true</c> — do not "fix" this to false.</b>
    ///
    /// <para>§340-D found that <c>TestMode__Enabled</c> was emitted only by
    /// <c>infra/modules/appservice.bicep</c> (the WEB app) and never by
    /// <c>functions.bicep</c> — while the TestMode swaps actually live in the JOBS host
    /// (<c>Jobs/Program.cs</c>: <see cref="IBackstageExhibitorApi"/> and
    /// <c>IEconomicErpClient</c>). Both Functions apps therefore bound THIS default.</para>
    ///
    /// <para>Inverting the default to <c>false</c> looks like the tidy fix and is a TRAP
    /// (operator 2026-07-26: <i>"important, otherwise we will DEV cause everything is
    /// written to zoho, etc again, which will be very critical"</i>). The DEV Functions app
    /// has no setting either, so it is this <c>true</c> default that keeps DEV from writing
    /// to real Zoho / e-conomic. Flip the default and **DEV starts writing to production
    /// third-party systems** the moment the code deploys — before any infra change lands.</para>
    ///
    /// <para>The correct fix is the one applied: emit the setting EXPLICITLY on both
    /// Functions apps (dev <c>true</c> / prod <c>false</c>, see <c>functions.bicep</c>), and
    /// leave this default as the fail-safe for any host nobody configured — where
    /// "write nothing" is the only defensible answer.</para>
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The test sponsor company name used in TESTMODE. Generic default;
    /// the real per-environment value is supplied via <c>TestMode__TestSponsorName</c>.</summary>
    public string TestSponsorName { get; set; } = "Test Sponsor Co";

    /// <summary>The test sponsor's company id (matches a WooCommerce order's _cm_company_id).
    /// Generic default; overridden per environment via <c>TestMode__TestSponsorCompanyId</c>.</summary>
    public string TestSponsorCompanyId { get; set; } = "test-sponsor";

    /// <summary>The test sponsor's contact email. Generic placeholder; the real
    /// per-environment value is supplied via <c>TestMode__TestSponsorEmail</c>.</summary>
    public string TestSponsorEmail { get; set; } = "test-sponsor@example.com";

    /// <summary>
    /// In TESTMODE every coordinator notification goes here only - never to a
    /// real event-coordinator address. Generic placeholder; overridden per
    /// environment via <c>TestMode__TestCoordinatorEmail</c>.
    /// </summary>
    public string TestCoordinatorEmail { get; set; } = "test-coordinator@example.com";
}
