using System;
using System.IO;
using System.Linq;
using System.Reflection;
using CommunityHub.Forms;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// Guards the whole class of bug behind the Volunteer-onboarding HTTP 500 (REQUIREMENTS §148):
/// the inline wizard host renders at <c>Pages/Forms/Wizard.cshtml</c> and drops each step's
/// <see cref="IWizardStepHandler.PartialName"/> via <c>&lt;partial&gt;</c>. Razor resolves a BARE
/// partial name only from the executing view's directory, its ANCESTORS, and Shared — so a partial
/// that lives in a SIBLING folder (e.g. <c>Pages/Volunteer/_AvailabilityFields.cshtml</c>) is
/// unresolvable from the host and throws at runtime. The Speaker handlers avoid this by qualifying
/// their <c>/Pages/Speaker/...</c> paths; <c>VolunteerAvailabilityStepHandler</c> originally did not.
///
/// <para>Unit tests over the handlers don't render, so this slipped past CI and only the live GUI
/// sweep caught it. This static test reflects EVERY handler and proves its PartialName is
/// genuinely resolvable from the host: either a rooted application path whose file exists, or a
/// bare name whose file sits where Razor will actually find it from <c>Pages/Forms/</c>.</para>
/// </summary>
public sealed class WizardPartialResolutionTests
{
    private static string PagesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "CommunityHub", "Pages");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate src/CommunityHub/Pages from " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Every_wizard_step_handler_partial_resolves_from_the_Forms_host()
    {
        var pages = PagesDir();
        var handlerType = typeof(IWizardStepHandler);

        var handlers = handlerType.Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false } && handlerType.IsAssignableFrom(t))
            .ToList();

        Assert.NotEmpty(handlers);   // sanity: discovery actually found the handlers

        foreach (var t in handlers)
        {
            var handler = (IWizardStepHandler)FormatterServices_CreateUninitialized(t);
            var partial = handler.PartialName;
            Assert.False(string.IsNullOrWhiteSpace(partial), $"{t.Name}.PartialName is blank");

            string file;
            if (partial.StartsWith("/", StringComparison.Ordinal))
            {
                // Rooted application path: "/Pages/Foo/_Bar.cshtml" → src/CommunityHub/Pages/Foo/_Bar.cshtml
                Assert.StartsWith("/Pages/", partial);
                var rel = partial.Substring("/Pages/".Length).Replace('/', Path.DirectorySeparatorChar);
                file = Path.Combine(pages, rel);
            }
            else
            {
                // BARE name: Razor resolves it from the host dir (Pages/Forms) + ancestors + Shared.
                // It MUST therefore live in Pages/Forms/ (same dir) or Pages/ (ancestor) — NOT in a
                // sibling like Pages/Volunteer/ or Pages/Speaker/. Require the safe co-located spot.
                var name = partial.EndsWith(".cshtml", StringComparison.Ordinal) ? partial : partial + ".cshtml";
                var inForms = Path.Combine(pages, "Forms", name);
                var inRoot = Path.Combine(pages, name);
                Assert.True(File.Exists(inForms) || File.Exists(inRoot),
                    $"{t.Name}.PartialName '{partial}' is a BARE name but its partial is not in " +
                    $"Pages/Forms/ or Pages/ — it will NOT resolve from the wizard host (Pages/Forms/" +
                    $"Wizard.cshtml) and throws HTTP 500. Qualify it to its real path, e.g. " +
                    $"'/Pages/<Folder>/{name}'.");
                continue;
            }

            Assert.True(File.Exists(file),
                $"{t.Name}.PartialName '{partial}' points at a partial that does not exist on disk " +
                $"({file}).");
        }
    }

    // Handlers' ctors take services; we only need PartialName (a pure expression), so build the
    // instance without running a ctor.
    private static object FormatterServices_CreateUninitialized(Type t) =>
        System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(t);
}
