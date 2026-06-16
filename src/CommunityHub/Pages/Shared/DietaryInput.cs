using CommunityHub.Core.Domain;

namespace CommunityHub.Pages.Shared;

/// <summary>
/// The bound input for the shared <c>_DietaryFieldset</c> partial (REQUIREMENTS
/// §21 — structured dietary/allergy capture on the Dinner AND Speaker forms).
/// A page model exposes one <c>[BindProperty] public DietaryInput Dietary</c>;
/// the partial renders the diet dropdown + the 14 allergen checkboxes + the
/// free-text "other" box, all bound through this single object so both forms
/// share one markup block.
///
/// Maps to/from the persisted <see cref="DietaryRequirement"/> entity via
/// <see cref="LoadFrom"/> / <see cref="ApplyTo"/>, keeping the form and the
/// storage in lockstep (the checkbox name == the entity property name == the
/// aggregation key).
/// </summary>
public sealed class DietaryInput
{
    public string? DietChoice { get; set; }

    public bool Gluten { get; set; }
    public bool Crustaceans { get; set; }
    public bool Eggs { get; set; }
    public bool Fish { get; set; }
    public bool Peanuts { get; set; }
    public bool Soybeans { get; set; }
    public bool Milk { get; set; }
    public bool TreeNuts { get; set; }
    public bool Celery { get; set; }
    public bool Mustard { get; set; }
    public bool Sesame { get; set; }
    public bool Sulphites { get; set; }
    public bool Lupin { get; set; }
    public bool Molluscs { get; set; }

    public string? OtherAllergens { get; set; }

    /// <summary>Allergen (token, isSet) pairs in display order — drives the checkbox loop.</summary>
    public IEnumerable<(string Token, bool IsSet)> Allergens()
    {
        yield return (nameof(Gluten), Gluten);
        yield return (nameof(Crustaceans), Crustaceans);
        yield return (nameof(Eggs), Eggs);
        yield return (nameof(Fish), Fish);
        yield return (nameof(Peanuts), Peanuts);
        yield return (nameof(Soybeans), Soybeans);
        yield return (nameof(Milk), Milk);
        yield return (nameof(TreeNuts), TreeNuts);
        yield return (nameof(Celery), Celery);
        yield return (nameof(Mustard), Mustard);
        yield return (nameof(Sesame), Sesame);
        yield return (nameof(Sulphites), Sulphites);
        yield return (nameof(Lupin), Lupin);
        yield return (nameof(Molluscs), Molluscs);
    }

    /// <summary>Populate this input from a persisted row (null = nothing on file yet).</summary>
    public void LoadFrom(DietaryRequirement? r)
    {
        if (r is null) return;
        DietChoice = r.DietChoice;
        Gluten = r.Gluten; Crustaceans = r.Crustaceans; Eggs = r.Eggs; Fish = r.Fish;
        Peanuts = r.Peanuts; Soybeans = r.Soybeans; Milk = r.Milk; TreeNuts = r.TreeNuts;
        Celery = r.Celery; Mustard = r.Mustard; Sesame = r.Sesame; Sulphites = r.Sulphites;
        Lupin = r.Lupin; Molluscs = r.Molluscs;
        OtherAllergens = r.OtherAllergens;
    }

    /// <summary>Write this input onto a (new or existing) entity row before save.</summary>
    public void ApplyTo(DietaryRequirement r)
    {
        // Only persist a known diet choice; ignore tampered values.
        r.DietChoice = DietaryRequirement.DietChoices.Contains(DietChoice) ? DietChoice : null;
        r.Gluten = Gluten; r.Crustaceans = Crustaceans; r.Eggs = Eggs; r.Fish = Fish;
        r.Peanuts = Peanuts; r.Soybeans = Soybeans; r.Milk = Milk; r.TreeNuts = TreeNuts;
        r.Celery = Celery; r.Mustard = Mustard; r.Sesame = Sesame; r.Sulphites = Sulphites;
        r.Lupin = Lupin; r.Molluscs = Molluscs;
        r.OtherAllergens = string.IsNullOrWhiteSpace(OtherAllergens) ? null : OtherAllergens.Trim();
    }
}
