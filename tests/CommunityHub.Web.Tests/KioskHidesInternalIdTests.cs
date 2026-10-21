using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §1303 — the kiosk never SHOWS its internal <c>KIOSK-…</c> identifier.
/// </summary>
/// <remarks>
/// Operator 2026-09-25: <i>"i dont see a need to also have a kiosk-&lt;id&gt; for the device … it is also
/// mentioned below on the webfrontend, remove that"</i>. The identifier stays — it is the device's key
/// until a name exists, exactly as a built unit's serial — but the status bar shows room · name ·
/// connection, and the approval wait shows the name the organiser typed. The page has no render
/// harness; these pin the script, as <see cref="KioskHomeScreenIdentityTests"/> does.
/// </remarks>
public sealed class KioskHidesInternalIdTests
{
    private static string Markup()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "CommunityHub", "Pages", "Eval", "Index.cshtml");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Eval/Index.cshtml not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Status_bar_has_no_serial_slot()
    {
        var m = Markup();
        Assert.DoesNotContain("stSerial", m);
        Assert.Contains("<span id=\"stDevice\"></span>", m);
    }

    [Fact]
    public void Approval_wait_shows_the_typed_name_not_the_identifier()
    {
        var m = Markup();
        Assert.DoesNotContain("\"Waiting for approval — \" + mySerial()", m);
        Assert.Contains("\"Waiting for approval\" + (name ? \" — \" + name : \"\")", m);
    }

    [Fact]
    public void Status_bar_says_Managed_by_Intune_for_Intune_named_devices_only()
    {
        var m = Markup();
        Assert.Contains("<span id=\"stIntune\" hidden>Managed by Intune</span>", m);
        Assert.Contains("document.getElementById(\"stIntune\").hidden = !/^eldk-/i.test(name);", m);
    }

    [Fact]
    public void Setup_is_behind_the_organizer_PIN_and_offers_one_tap_Intune_enrolment()
    {
        var m = Markup();
        // Every way into setup goes through openSetup(), which shows the PIN lock first.
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(m, "setup\\.classList\\.add\\(\"on\"\\)").Count);
        Assert.Contains("lockUi(orgPin === null);", m);
        Assert.Contains("/evaluation/v1/kiosk/unlock", m);
        Assert.Contains("/evaluation/v1/kiosk/managed-enrol", m);
        // The PIN is not in the page: the server decides.
        Assert.Contains("<input id=\"pin\" type=\"password\"", m);
    }

    [Fact]
    public void Status_bar_stays_visible_over_the_follow_up_pages_and_the_idle_screen()
    {
        var m = Markup();
        // Above the full-screen follow-up layer (z-index 8), below the setup dialog (10).
        Assert.Contains("opacity: .8; white-space: nowrap; position: relative; z-index: 9; }", m);
        Assert.Contains("padding: 2vh 2.4vw 5.5vh; flex-direction: column; }", m);
        // The idle header cannot push the bar off the bottom.
        Assert.Contains("min-height: 0; overflow: hidden; }", m);
    }

    [Fact]
    public void A_deleted_device_forgets_itself_and_opens_setup_at_the_next_refresh()
    {
        var m = Markup();
        Assert.Contains("if (r.status === 410) { forgetDevice(); return null; }", m);
        Assert.Contains("localStorage.removeItem(K_DEV); localStorage.removeItem(\"ceh.eval.enrol\");", m);
        Assert.Contains("openSetup();\n    }", m.Replace("\r\n", "\n"));
    }

    [Fact]
    public void No_room_linked_says_so_instead_of_no_more_sessions()
    {
        var m = Markup();
        Assert.Contains("if (!b.room) {", m);
        Assert.Contains("\"No room linked — link this tablet to a room under Organizer → Evaluation devices\"", m);
    }

    [Fact]
    public void Attendee_facing_text_says_tablet_not_iPad()
    {
        var m = Markup();
        Assert.DoesNotContain("saved on this iPad", m);
        Assert.DoesNotContain("This iPad is live", m);
    }
}
