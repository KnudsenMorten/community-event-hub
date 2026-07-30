using System.Runtime.CompilerServices;
using CommunityHub.Forms;
using CommunityHub.Forms.Steps;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// Guard for the whole class of bug behind the §293 speaker-details regression: the wizard host
/// (<c>WizardModel</c>) keys EVERY discovered <see cref="IWizardStepHandler"/> into ONE dictionary
/// by <c>Handler.Key</c>, shared across ALL roles. Two handlers with the same Key silently collide —
/// in Release "last wins", so the sponsor company-branding handler (Key "details") hijacked the
/// SPEAKER details step and buried the Sessionize-synced fields. The DEBUG duplicate-key throw in
/// WizardModel only fires when BOTH handlers are in the injected set, which unit tests don't do — so
/// it slipped to prod. This test reflects the ACTUAL registered handler set (the same types
/// Program.cs auto-registers) and fails loudly on any duplicate key, so it can never ship again.
/// </summary>
public class WizardHandlerKeyUniquenessTests
{
    [Fact]
    public void Every_wizard_step_handler_key_is_globally_unique()
    {
        // Same discovery Program.cs uses: every concrete IWizardStepHandler in the web assembly.
        var handlerTypes = typeof(SpeakerDetailsStepHandler).Assembly
            .GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false, IsClass: true }
                        && typeof(IWizardStepHandler).IsAssignableFrom(t))
            .ToList();

        Assert.NotEmpty(handlerTypes);

        // Read Key WITHOUT running constructors (handlers take DI'd form-service deps): every Key is
        // a constant getter, so an uninitialized instance yields it safely.
        var keyed = handlerTypes
            .Select(t => (Type: t, Key: ((IWizardStepHandler)RuntimeHelpers.GetUninitializedObject(t)).Key))
            .ToList();

        var duplicates = keyed
            .GroupBy(x => x.Key, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => $"  '{g.Key}' <- {string.Join(", ", g.Select(x => x.Type.Name))}")
            .ToList();

        Assert.True(duplicates.Count == 0,
            "Wizard step handler keys must be globally unique (they share ONE key->handler map across "
            + "all roles; a collision silently hijacks the step in Release). Duplicates:\n"
            + string.Join("\n", duplicates));
    }
}
