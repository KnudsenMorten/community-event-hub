using Xunit;

namespace CommunityHub.Web.Tests.Organizer;

/// <summary>
/// §1267 — the calculated "Ends" readout on the session edit form follows the start AS IT IS TYPED.
/// </summary>
/// <remarks>
/// Operator 2026-09-21: <i>"when i edit time, the calculated value is not updated until i click save
/// button"</i>. The readout listened only for <c>change</c>, which a datetime-local box fires when
/// the value is committed, not while typing. The save was always right (the server derives the end —
/// <c>SessionEditAllFieldsTests</c>); the screen showed a stale end in the meantime. No render
/// harness exists for the page, so the script's subscription is pinned in the markup.
/// </remarks>
public sealed class SessionEditEndsReadoutTests
{
    private static string Markup()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "CommunityHub", "Pages", "Organizer", "Sessions.cshtml");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Sessions.cshtml not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void The_ends_readout_recalculates_on_input_not_only_on_change()
    {
        var markup = Markup();
        var start = markup.IndexOf("getElementById('EditEndsAt_", StringComparison.Ordinal);
        Assert.True(start >= 0, "The Ends readout script was restructured — update this test.");
        var script = markup[start..markup.IndexOf("</script>", start, StringComparison.Ordinal)];

        Assert.Contains("'input'", script);
        Assert.Contains("s.addEventListener(ev, calc)", script);
        Assert.Contains("l.addEventListener(ev, calc)", script);
    }
}
