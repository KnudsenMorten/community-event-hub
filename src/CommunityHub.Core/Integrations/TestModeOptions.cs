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

    /// <summary>The test sponsor company name used in TESTMODE.</summary>
    public string TestSponsorName { get; set; } = "2LINKIT";

    /// <summary>The test sponsor's company id (matches a WooCommerce order's _cm_company_id).</summary>
    public string TestSponsorCompanyId { get; set; } = "test-2linkit";

    /// <summary>The test sponsor's contact email.</summary>
    public string TestSponsorEmail { get; set; } = "mok@2linkit.net";

    /// <summary>
    /// In TESTMODE every coordinator notification goes here only - never to a
    /// real event-coordinator address.
    /// </summary>
    public string TestCoordinatorEmail { get; set; } = "mok@2linkit.net";
}
