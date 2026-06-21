namespace CommunityHub.Core.Domain;

/// <summary>
/// The single source of truth for mapping a <see cref="ScheduleEntry.Roles"/> CSV
/// (role keywords) to participant roles — used by the Key-dates display, the iCal
/// feed and the organizer editor so role visibility is identical everywhere.
/// Keywords: organizer, volunteer, speaker (incl. master-class), media (video/camera),
/// attendee, sponsor, all (everyone). Empty = everyone.
/// </summary>
public static class ScheduleRoles
{
    /// <summary>The keywords offered in the organizer editor, in display order.</summary>
    public static readonly string[] Keywords =
        { "all", "organizer", "volunteer", "speaker", "media", "attendee", "sponsor" };

    public static IReadOnlyList<string> Parse(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? Array.Empty<string>()
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .Select(s => s.ToLowerInvariant()).Distinct().ToArray();

    /// <summary>The role keyword a participant's role maps to ("" for none).</summary>
    public static string ViewerKeyword(ParticipantRole role) => role switch
    {
        ParticipantRole.Organizer => "organizer",
        ParticipantRole.Volunteer => "volunteer",
        ParticipantRole.Speaker or ParticipantRole.MasterclassSpeaker => "speaker",
        ParticipantRole.Video or ParticipantRole.Camera => "media",
        ParticipantRole.Attendee => "attendee",
        ParticipantRole.Sponsor => "sponsor",
        _ => "",
    };

    /// <summary>True if an entry tagged <paramref name="csv"/> applies to <paramref name="role"/>.</summary>
    public static bool Applies(string? csv, ParticipantRole? role)
    {
        var roles = Parse(csv);
        if (roles.Count == 0 || roles.Contains("all")) return true;
        if (role is null) return true;   // public / fallback: show everything
        return roles.Contains(ViewerKeyword(role.Value));
    }

    /// <summary>A human "who" label, e.g. "organizers + volunteers" or "everyone".</summary>
    public static string WhoLabel(string? csv)
    {
        var roles = Parse(csv);
        if (roles.Count == 0 || roles.Contains("all")) return "everyone";
        static string Lbl(string k) => k switch
        {
            "organizer" => "organizers", "volunteer" => "volunteers", "speaker" => "speakers",
            "media" => "media", "attendee" => "attendees", "sponsor" => "sponsors", _ => k,
        };
        return string.Join(" + ", roles.Select(Lbl));
    }
}
