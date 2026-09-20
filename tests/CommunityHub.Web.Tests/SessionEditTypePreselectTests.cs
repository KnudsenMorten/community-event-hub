using System.Runtime.CompilerServices;
using CommunityHub.Core.Domain;
using CommunityHub.Pages.Organizer;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// 🔴 §1232 — THE SESSION EDIT FORM MUST PRE-SELECT THE ROW'S OWN TYPE.
///
/// <para>The per-row Type dropdown used the shared option list, which selects nothing, so the browser
/// showed the first option — Keynote, enum value 0. Saving the form for ANY other reason posted
/// Keynote and stamped <c>TypeIsManualOverride</c>, so no import corrected it. Measured in PROD
/// 2026-09-15: a Master Class became a Keynote, and the Zoho diff mail asked for
/// PRESENTATION → KEYNOTE.</para>
/// </summary>
public sealed class SessionEditTypePreselectTests
{
    // TypeOptionsFor touches no injected service, so the page model needs no wiring here.
    private static SessionsModel Page() =>
        (SessionsModel)RuntimeHelpers.GetUninitializedObject(typeof(SessionsModel));

    [Theory]
    [InlineData(SessionType.MasterClass)]
    [InlineData(SessionType.TechnicalSession)]
    [InlineData(SessionType.Welcome)]
    public void The_rows_current_type_is_the_selected_option(SessionType current)
    {
        var options = Page().TypeOptionsFor(current).ToList();

        var selected = Assert.Single(options, o => o.Selected);
        Assert.Equal(current.ToString(), selected.Value);
    }

    /// <summary>🔴 The regression itself: the edit form must not fall back to the shared, unselected list.</summary>
    [Fact]
    public void The_edit_form_binds_the_preselected_list_for_each_row()
    {
        var view = File.ReadAllText(Path.Combine(
            FindDir("src", "CommunityHub", "Pages"), "Organizer", "Sessions.cshtml"));

        Assert.Contains(
            "name=\"EditType\" asp-items=\"Model.TypeOptionsFor(r.Type)\"", view, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "name=\"EditType\" asp-items=\"Model.TypeOptions\"", view, StringComparison.Ordinal);
    }

    private static string FindDir(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, Path.Combine(parts));
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(Path.Combine(parts));
    }
}
