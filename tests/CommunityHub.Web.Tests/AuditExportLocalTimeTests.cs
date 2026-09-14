using CommunityHub.Core.Domain;
using CommunityHub.Pages.Organizer;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §1136a — the audit EXPORT carries LOCAL time as well as UTC.
///
/// <para>Operator 2026-08-25: <i>"export can also be in local time, please"</i>, following §1136
/// which made the page itself default to local time. The column is named for its ROLE ("the local
/// reading") rather than the city that defines it — operator: <i>"i dont like the (Copenhagen) or
/// OccuredCopenhagen - change to Local instead of Copenhagen"</i>.</para>
///
/// <para>🔒 <b>UTC is KEPT, not replaced</b>, and that is the point of these tests. An export is
/// reconciled against older exports, so silently shifting the values under the existing
/// <c>OccurredUtc</c> header would make every historic file disagree with every new one with nothing
/// in either to explain why. A timezone is only safe when it is LABELLED.</para>
/// </summary>
public sealed class AuditExportLocalTimeTests
{
    private static AuditEntry Row(string utc) => new()
    {
        OccurredUtc = DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture),
        Category = AuditCategory.Auth,
        Action = "SignIn",
        ActorEmail = "someone@example.org",
        Outcome = AuditOutcome.Success,
        Source = AuditSource.Web,
    };

    private static string[] Lines(string csv) =>
        csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void Both_columns_are_present_and_LOCAL_leads()
    {
        var csv = AuditTrailModel.BuildCsv(new[] { Row("2026-08-25T14:41:24Z") });

        Assert.StartsWith("OccurredLocal,OccurredUtc,Category,", Lines(csv)[0]);
    }

    [Fact]
    public void Summer_row_carries_both_readings()
    {
        var csv = AuditTrailModel.BuildCsv(new[] { Row("2026-08-25T14:41:24Z") });
        var data = Lines(csv)[1];

        Assert.Contains("2026-08-25 16:41:24", data);   // Local, UTC+2
        Assert.Contains("2026-08-25 14:41:24", data);   // UTC, unchanged
    }

    [Fact]
    public void Winter_row_uses_the_right_offset()
    {
        // 🔴 A hard-coded "+2" would be wrong for five months of the year.
        var csv = AuditTrailModel.BuildCsv(new[] { Row("2026-01-15T09:00:00Z") });
        var data = Lines(csv)[1];

        Assert.Contains("2026-01-15 10:00:00", data);   // Local, UTC+1
        Assert.Contains("2026-01-15 09:00:00", data);   // UTC
    }

    [Fact]
    public void The_UTC_column_still_holds_exactly_what_it_always_did()
    {
        // 🔒 The reconciliation guarantee: same header name, same value as before §1136a.
        var csv = AuditTrailModel.BuildCsv(new[] { Row("2027-02-10T05:40:00Z") });
        var cells = Lines(csv)[1].Split(',');

        Assert.Equal("\"2027-02-10 06:40:00\"", cells[0]);   // Local (Europe/Copenhagen)
        Assert.Equal("\"2027-02-10 05:40:00\"", cells[1]);   // UTC — untouched
    }

    [Fact]
    public void Every_other_column_still_follows_in_the_same_order()
    {
        // ⚠️ The local column was INSERTED first, so everything shifts by one. This pins the rest of the
        // contract so a future edit cannot quietly drop or reorder a column while nobody is looking.
        var header = Lines(AuditTrailModel.BuildCsv(new[] { Row("2026-08-25T14:41:24Z") }))[0].TrimEnd('\r');

        Assert.Equal(
            "OccurredLocal,OccurredUtc,Category,Action,Actor,OnBehalfOf,Role,Outcome,Source,"
            + "TargetType,TargetId,Summary,Detail,Path",
            header);
    }

    [Fact]
    public void A_row_that_crosses_midnight_reports_the_local_DAY()
    {
        // 23:30 UTC is the next day in Copenhagen — the export must say so, or a reader filtering by
        // date in Excel silently loses the row.
        var csv = AuditTrailModel.BuildCsv(new[] { Row("2026-08-25T23:30:00Z") });
        var cells = Lines(csv)[1].Split(',');

        Assert.Equal("\"2026-08-26 01:30:00\"", cells[0]);
    }
}
