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

    /// <summary>Master switch. Default true - safe until explicitly disabled.</summary>
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
