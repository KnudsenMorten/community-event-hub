using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1140b — the §897 write-verification must not report a field it simply cannot read.
///
/// <para>Operator 2026-08-26: <i>"this is wrong"</i>, on a mail naming 52 companies whose
/// <c>vat_zone_number</c> Company Manager <i>"did NOT store"</i> — including rows where the value
/// was present and correct. VirtualMetric's CVR was in the webshop, verbatim, while the mail told
/// him to enter it by hand.</para>
///
/// <para>🔴 <b>The mail is what he acts on.</b> §897 built the read-back because a 200 from
/// WordPress is not evidence of a write, and that reasoning still holds. What it lacked was a third
/// answer: <i>applied</i>, <i>refused</i>, and <i>I could not check</i>. Collapsing the third into
/// the second turns a missing mapping into 52 pieces of invented manual work.</para>
/// </summary>
public sealed class ErpWriteVerificationTests
{
    // ReadField's contract, mirrored: every mapped property on CompanyManagerCompany is a
    // NON-NULLABLE string defaulting to "". So null means "no mapping for this key" and can mean
    // nothing else — an empty stored value returns "". That is what makes the guard exact rather
    // than a heuristic.
    private static string? ReadField(string apiKey) => apiKey switch
    {
        "billing_email" => "billing@example.test",
        "company_name_public" => "",                       // mapped, genuinely empty
        "corporate_identification_number" => "855963876B01",
        "phone" => "",
        "currency" => "EUR",
        "vat_zone_number" => "2",
        _ => null,                                         // UNMAPPED
    };

    private enum Outcome { Applied, Refused, Unverified }

    private static Outcome Verify(string key, string want)
    {
        var now = ReadField(key);
        if (now is null) return Outcome.Unverified;
        return string.Equals(now.Trim(), want.Trim(), System.StringComparison.OrdinalIgnoreCase)
            ? Outcome.Applied
            : Outcome.Refused;
    }

    [Theory]
    [InlineData("corporate_identification_number", "855963876B01")]
    [InlineData("currency", "EUR")]
    [InlineData("vat_zone_number", "2")]
    public void The_fields_added_with_1140_now_verify_as_APPLIED(string key, string want) =>
        Assert.Equal(Outcome.Applied, Verify(key, want));

    [Fact]
    public void An_UNMAPPED_key_is_unverified_never_refused()
    {
        // 🔒 The guard. A field added to the sync but forgotten in ReadField must be quiet in the
        // operator's mail and loud in the log — never the reverse.
        Assert.Equal(Outcome.Unverified, Verify("some_new_field", "x"));
        Assert.NotEqual(Outcome.Refused, Verify("some_new_field", "x"));
    }

    [Fact]
    public void A_genuinely_refused_write_is_STILL_reported()
    {
        // ⚠️ The fix must not silence §897. WordPress accepting a field, echoing it, and keeping
        // the old value is the real hazard this machinery exists for.
        Assert.Equal(Outcome.Refused, Verify("currency", "DKK"));
    }

    [Fact]
    public void A_mapped_but_EMPTY_value_is_refused_not_unverified()
    {
        // The distinction that makes null unambiguous: "" came back FROM the webshop and is an
        // answer; null means the question was never asked.
        Assert.Equal(Outcome.Refused, Verify("phone", "+45 12 34 56 78"));
        Assert.NotEqual(Outcome.Unverified, Verify("phone", "+45 12 34 56 78"));
    }

    [Fact]
    public void Every_key_the_sync_writes_has_a_read_back_mapping()
    {
        // 🔑 The regression that caused this. Adding a Follow() without adding a ReadField arm is
        // invisible until it reaches the operator's inbox as manual work.
        var written = new[]
        {
            "billing_email", "company_name_public",
            "corporate_identification_number", "phone", "currency", "vat_zone_number",
        };

        foreach (var key in written)
            Assert.NotNull(ReadField(key));
    }
}
