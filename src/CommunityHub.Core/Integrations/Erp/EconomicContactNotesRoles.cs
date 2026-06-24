using System.Text.RegularExpressions;

namespace CommunityHub.Core.Integrations.Erp;

/// <summary>
/// Codec for the e-conomic contact ROLE convention stored in the contact's
/// <c>notes</c> field — e-conomic is the master for a contact's role. Format
/// mirrors the legacy billing scripts + <see cref="EconomicRoleClient"/>:
/// <c>Role:1,2</c> where <b>1 = Signer</b>, <b>2 = Event Coordinator</b>
/// (a person can hold both). Any other free text in notes is preserved.
/// </summary>
public static class EconomicContactNotesRoles
{
    public const int SignerRoleId = 1;
    public const int EventCoordinatorRoleId = 2;

    private static readonly Regex RolePattern =
        new(@"Role:\s*([0-9,\s]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Parse the role ids out of a notes string (empty when none).</summary>
    public static IReadOnlySet<int> Parse(string? notes)
    {
        var set = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(notes)) return set;
        var m = RolePattern.Match(notes);
        if (!m.Success) return set;
        foreach (var part in m.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(part, out var n)) set.Add(n);
        return set;
    }

    public static bool IsSigner(string? notes) => Parse(notes).Contains(SignerRoleId);
    public static bool IsEventCoordinator(string? notes) => Parse(notes).Contains(EventCoordinatorRoleId);

    /// <summary>
    /// Return <paramref name="existingNotes"/> with its <c>Role:…</c> segment replaced
    /// by the given roles (preserving any other text). Output e.g. <c>Role:1,2</c>.
    /// When neither role is set, the <c>Role:…</c> segment is removed entirely.
    /// </summary>
    public static string WithRoles(string? existingNotes, bool signer, bool eventCoordinator)
    {
        // Strip any existing Role:… segment + tidy leftover separators/space.
        var rest = RolePattern.Replace(existingNotes ?? string.Empty, string.Empty)
            .Trim().Trim(';', ',').Trim();

        var ids = new List<int>();
        if (signer) ids.Add(SignerRoleId);
        if (eventCoordinator) ids.Add(EventCoordinatorRoleId);
        if (ids.Count == 0) return rest;

        var role = "Role:" + string.Join(",", ids);
        return string.IsNullOrWhiteSpace(rest) ? role : $"{rest}; {role}";
    }
}
