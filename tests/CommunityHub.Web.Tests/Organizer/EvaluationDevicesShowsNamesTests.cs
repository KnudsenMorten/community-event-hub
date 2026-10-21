using Xunit;

namespace CommunityHub.Web.Tests.Organizer;

/// <summary>
/// §1266 — the name typed on the iPad at enrollment must be VISIBLE in the approval queue.
/// </summary>
/// <remarks>
/// Operator 2026-09-21, the second report: <i>"it is stil not showing the name i used in the
/// enrollment"</i> · <i>"now this is 2nd time i write about this bug"</i>. §1261 carried the name onto
/// the device minted AFTER approval, but the "Waiting for approval" table — the first place an
/// organizer looks, and the only place that tells two waiting iPads apart — never rendered it. The
/// page has no render harness, so this pins the markup directly: each table that lists a request or
/// a device must print its name.
/// </remarks>
public sealed class EvaluationDevicesShowsNamesTests
{
    private static string Markup()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "CommunityHub", "Pages", "Organizer",
                "EvaluationDevices.cshtml");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("EvaluationDevices.cshtml not found above " + AppContext.BaseDirectory);
    }

    private static string Section(string markup, string loopHeader)
    {
        var start = markup.IndexOf(loopHeader, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{loopHeader}' not found — the table was restructured; update this test.");
        var end = markup.IndexOf("</tr>", start, StringComparison.Ordinal);
        return markup[start..end];
    }

    [Fact]
    public void The_approval_queue_shows_the_name_typed_at_enrollment() =>
        Assert.Contains("@r.Note", Section(Markup(), "@foreach (var r in Model.Pending)"));

    [Fact]
    public void The_telemetry_table_shows_the_device_name() =>
        Assert.Contains("DisplayName(row.Device)", Section(Markup(), "@foreach (var row in Model.TelemetryRows)"));

    [Fact]
    public void The_device_list_leads_with_the_display_name() =>
        Assert.Contains("DisplayName(d)", Section(Markup(), "@foreach (var d in Model.Devices)"));

    // ---- §1277 — delete only where it works; revoked devices leave the list ------------------

    [Fact]
    public void Delete_is_offered_only_for_a_device_without_feedback()
    {
        var row = Section(Markup(), "@foreach (var d in Model.Devices)");
        var guard = row.IndexOf("ResponseCounts.TryGetValue(d.SerialNumber, out var held)", StringComparison.Ordinal);
        var delete = row.IndexOf("asp-page-handler=\"Delete\"", StringComparison.Ordinal);
        Assert.True(guard >= 0 && delete > guard, "The Delete form must sit in the no-feedback branch.");
    }

    [Fact]
    public void A_revoked_device_is_hidden_from_the_list_by_default()
    {
        var m = Markup();
        Assert.Contains("hidden=\"@(!d.IsActive)\"", m);
        Assert.Contains("Show revoked (@revokedCount)", m);
    }

    // ---- §1266 — the display name itself ----------------------------------------------------

    [Theory]
    [InlineData("Room 16 stand", "Room 16 stand")]
    [InlineData(null, "KIOSK-DA7BCF9411")]
    [InlineData("  ", "KIOSK-DA7BCF9411")]
    [InlineData("Serial KIOSK-DA7BCF9411", "KIOSK-DA7BCF9411")]   // the old generated placeholder is not a name
    public void The_display_name_is_the_custom_name_else_the_serial(string? label, string expected)
    {
        var d = new CommunityHub.Core.Domain.Evaluation.EvaluationDevice
        {
            SerialNumber = "KIOSK-DA7BCF9411", Label = label,
        };
        Assert.Equal(expected, CommunityHub.Pages.Organizer.EvaluationDevicesModel.DisplayName(d));
    }

    [Fact]
    public void Status_counts_the_kiosks_own_check_in_not_only_the_heartbeat()
    {
        // The 60-second session refresh is a sign of life too; an open iPad must not read "late".
        var heartbeat = new DateTimeOffset(2026, 9, 21, 8, 41, 0, TimeSpan.Zero);
        var refresh = heartbeat.AddMinutes(50);
        var d = new CommunityHub.Core.Domain.Evaluation.EvaluationDevice
        {
            SerialNumber = "KIOSK-1", LastSeenAt = heartbeat, KioskLastSeenAt = refresh,
        };
        Assert.Equal(refresh, CommunityHub.Pages.Organizer.EvaluationDevicesModel.LastContact(d));
    }
}
