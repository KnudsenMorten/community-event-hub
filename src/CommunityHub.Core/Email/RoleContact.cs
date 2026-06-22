using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Email;

/// <summary>
/// Resolves the per-role "who do I contact about this?" organizer lead from the
/// edition config <c>placeholders</c> map (event.&lt;edition&gt;.json). The names
/// and addresses live ONLY in that config file (which is denylisted from the
/// public publish); the code carries ONLY the placeholder KEYS, never a real
/// name or email. A missing/blank key falls back to the support email for the
/// address and a blank string for the name, so a community that does not set
/// these keys simply gets the support-email fallback with no broken markup.
/// </summary>
public static class RoleContact
{
    private const string DefaultSupportEmail = "info@expertslive.dk";

    /// <summary>
    /// The (name, email) of the organizer lead for <paramref name="role"/>, read
    /// from <paramref name="placeholders"/> by KEY. Speaker / master-class speaker
    /// → speakerContact*, Volunteer → volunteerContact*, Sponsor → sponsorContact*.
    /// Any other role (or a missing/blank key) yields ("", supportEmail).
    /// </summary>
    public static (string Name, string Email) For(
        ParticipantRole role,
        IReadOnlyDictionary<string, string> placeholders,
        string supportEmail)
    {
        var fallbackEmail = string.IsNullOrWhiteSpace(supportEmail)
            ? DefaultSupportEmail
            : supportEmail;

        var (nameKey, emailKey) = role switch
        {
            ParticipantRole.Speaker
                => ("speakerContactName", "speakerContactEmail"),
            ParticipantRole.Volunteer
                => ("volunteerContactName", "volunteerContactEmail"),
            ParticipantRole.Sponsor
                => ("sponsorContactName", "sponsorContactEmail"),
            _ => (string.Empty, string.Empty),
        };

        if (nameKey.Length == 0)
        {
            // No mapped role contact — support email only.
            return (string.Empty, fallbackEmail);
        }

        var name = Lookup(placeholders, nameKey);
        var email = Lookup(placeholders, emailKey);

        return (
            name, // blank when the key is missing/blank
            string.IsNullOrWhiteSpace(email) ? fallbackEmail : email);
    }

    /// <summary>
    /// Writes the contact tokens for <paramref name="role"/> into
    /// <paramref name="tokens"/>: <c>contactName</c>, <c>contactEmail</c> and
    /// <c>supportEmail</c>. Render-blank-safe — an unset name becomes "" and the
    /// email falls back to the support address.
    /// </summary>
    public static void AddTo(
        IDictionary<string, string> tokens,
        ParticipantRole role,
        IReadOnlyDictionary<string, string> placeholders,
        string supportEmail)
    {
        var (name, email) = For(role, placeholders, supportEmail);
        var fallbackEmail = string.IsNullOrWhiteSpace(supportEmail)
            ? DefaultSupportEmail
            : supportEmail;

        tokens["contactName"] = name;
        tokens["contactEmail"] = email;
        tokens["supportEmail"] = fallbackEmail;
    }

    private static string Lookup(
        IReadOnlyDictionary<string, string> placeholders, string key)
    {
        if (placeholders is not null
            && placeholders.TryGetValue(key, out var v)
            && !string.IsNullOrWhiteSpace(v))
        {
            return v;
        }
        return string.Empty;
    }
}
