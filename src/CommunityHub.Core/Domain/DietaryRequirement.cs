namespace CommunityHub.Core.Domain;

/// <summary>
/// Structured dietary / allergy capture for catering (REQUIREMENTS §21 Participant
/// [H]). Free-text allergy notes are unusable for a caterer — they cannot be
/// counted or filtered — so this captures the common allergens as discrete,
/// aggregatable flags plus a structured diet choice, with a free-text "other"
/// only for the long tail.
///
/// One row per participant per edition PER <see cref="DietarySurface"/>: a
/// speaker's day-catering needs (collected on the Speaker form) are a different
/// occasion from the Appreciation Dinner (collected on the Dinner form), so the
/// two are kept separate and a participant may have both. Own-row scoped: a
/// participant only ever reads/writes their own (EventId, ParticipantId, Surface)
/// row.
///
/// The legacy free-text <see cref="DinnerSignup.AllergyNotes"/> is kept (and still
/// shown as the "anything else" box) so nothing is lost; this entity is the
/// structured, count-friendly capture alongside it.
/// </summary>
public class DietaryRequirement
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    public int ParticipantId { get; set; }
    public Participant Participant { get; set; } = null!;

    /// <summary>Which form/occasion this row was collected for (Dinner vs Speaker day-catering).</summary>
    public DietarySurface Surface { get; set; }

    // --- Diet choice (mutually-exclusive lifestyle preference) ---------------
    /// <summary>One of <see cref="DietaryRequirement.DietChoices"/>, e.g. "None", "Vegetarian", "Vegan", "Pescatarian", "Halal", "Kosher". Null = not stated.</summary>
    public string? DietChoice { get; set; }

    // --- Common allergens (discrete, aggregatable flags) --------------------
    // These are the EU FIC "14 major allergens" subset most relevant to event
    // catering. Each is its own column so the organizer can COUNT "how many
    // gluten-free meals" without parsing free text.
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

    /// <summary>Free-text for any allergen / requirement not covered by the checkboxes.</summary>
    public string? OtherAllergens { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>The mutually-exclusive diet-lifestyle choices offered on the forms.</summary>
    public static readonly string[] DietChoices =
    {
        "None",
        "Vegetarian",
        "Vegan",
        "Pescatarian",
        "Halal",
        "Kosher",
    };

    /// <summary>
    /// The allergen flags as (token, isSet) pairs in display order. The token is
    /// the property name — used as the form field name AND the aggregation key —
    /// so the view, the model binder and the organizer roll-up all agree.
    /// </summary>
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

    /// <summary>The ordered allergen tokens (property names) shown on the forms.</summary>
    public static readonly string[] AllergenTokens =
    {
        nameof(Gluten), nameof(Crustaceans), nameof(Eggs), nameof(Fish),
        nameof(Peanuts), nameof(Soybeans), nameof(Milk), nameof(TreeNuts),
        nameof(Celery), nameof(Mustard), nameof(Sesame), nameof(Sulphites),
        nameof(Lupin), nameof(Molluscs),
    };

    /// <summary>True if any structured signal was captured (a diet choice, an allergen flag, or free text).</summary>
    public bool HasAny =>
        (!string.IsNullOrWhiteSpace(DietChoice) && DietChoice != "None")
        || Allergens().Any(a => a.IsSet)
        || !string.IsNullOrWhiteSpace(OtherAllergens);
}

/// <summary>The occasion a <see cref="DietaryRequirement"/> row was collected for.</summary>
public enum DietarySurface
{
    /// <summary>Speaker / crew day-catering (collected on /Forms/Speaker).</summary>
    SpeakerCatering = 0,
    /// <summary>The Appreciation Dinner (collected on /Forms/Dinner).</summary>
    Dinner = 1,
}
