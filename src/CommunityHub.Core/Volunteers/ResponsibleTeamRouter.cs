namespace CommunityHub.Core.Volunteers;

/// <summary>
/// Which allocation queue a <see cref="Domain.VolunteerTask"/> flows into once it
/// has been imported (§150 route-by-Responsible-Team). A task is auto-assigned and
/// queued ONLY when it routes to a real queue; a <see cref="TrackedOnly"/> task is
/// imported and visible but is NEVER auto-assigned and NEVER queued.
/// </summary>
public enum AllocationQueueKind
{
    /// <summary>Routed to the VOLUNTEER draft queue (lead validates → commits).</summary>
    Volunteer = 0,

    /// <summary>Routed to the ORGANIZER draft queue (mirrors the volunteer queue).</summary>
    Organizer = 1,

    /// <summary>Imported and tracked, but NOT auto-assigned and NOT queued. The
    /// default for every team that does not opt into an auto-assign queue.</summary>
    TrackedOnly = 2,
}

/// <summary>
/// Maps a task's <c>ResponsibleTeam</c> band to the allocation queue it belongs in
/// (§150). The mapping is deliberately a SMALL allow-list of teams that opt into an
/// auto-assign queue; every other team — known (BeFree, BC-*, Photo, Video,
/// Speaker-team, Community Reporter, Transporter, Expo-Sponsor, Sponsor, All) or
/// unknown, and the blank band — falls through to <see cref="AllocationQueueKind.TrackedOnly"/>
/// so it is imported but never auto-assigned/queued.
/// </summary>
public static class ResponsibleTeamRouter
{
    /// <summary>The volunteer team whose tasks feed the VOLUNTEER queue.</summary>
    public const string VolunteerTeam = "ELDK-Volunteers";

    /// <summary>The two organizer bands whose tasks feed the ORGANIZER queue.</summary>
    public const string OrganizerTeam = "ELDK";
    public const string OrganizerTeamMok = "ELDK-MOK";

    /// <summary>
    /// Route a Responsible-Team band to its queue. Case-insensitive and
    /// whitespace-trimmed; null/blank and any unrecognised band → TrackedOnly.
    /// </summary>
    public static AllocationQueueKind Route(string? responsibleTeam)
    {
        var team = (responsibleTeam ?? string.Empty).Trim();
        if (team.Length == 0)
            return AllocationQueueKind.TrackedOnly;

        if (team.Equals(VolunteerTeam, StringComparison.OrdinalIgnoreCase))
            return AllocationQueueKind.Volunteer;

        if (team.Equals(OrganizerTeam, StringComparison.OrdinalIgnoreCase)
            || team.Equals(OrganizerTeamMok, StringComparison.OrdinalIgnoreCase))
            return AllocationQueueKind.Organizer;

        // BeFree / BC-* / Photo / Video / Speaker-team / Community Reporter /
        // Transporter / Expo-Sponsor / Sponsor / All / blank / anything unknown.
        return AllocationQueueKind.TrackedOnly;
    }
}
