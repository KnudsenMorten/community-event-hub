namespace CommunityHub.Core.Domain;

/// <summary>
/// The swag size catalog (polo / jacket) shared by the self-service swag form
/// and the organizer "modify on behalf" page, so the offered options never
/// drift between the two write paths. The last entry of each list is the
/// "I don't want one" sentinel.
/// </summary>
public static class SwagOptions
{
    public const string NoPoloLabel = "I wear my own clothes";
    public const string NoJacketLabel = "I don't want a jacket";

    public static readonly string[] PoloSizes =
    {
        "XS (men)", "S (men)", "M (men)", "L (men)",
        "XL (men)", "XXL (men)", "3XL (men)", "4XL (men)",
        "XS (women)", "S (women)", "M (women)", "L (women)",
        "XL (women)", "XXL (women)", "3XL (women)", "4XL (women)",
        NoPoloLabel,
    };

    public static readonly string[] JacketSizes =
    {
        "XS (men)", "S (men)", "M (men)", "L (men)",
        "XL (men)", "XXL (men)", "3XL (men)", "4XL (men)",
        "XS (women)", "S (women)", "M (women)", "L (women)",
        "XL (women)", "XXL (women)", "3XL (women)", "4XL (women)",
        NoJacketLabel,
    };
}
