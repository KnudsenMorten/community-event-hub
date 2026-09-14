namespace CommunityHub.Core.Integrations;

/// <summary>
/// §1077 stage 5 — the words that go INSIDE the group-photo calendar invite, in the operator's own
/// wording (2026-08-11).
/// </summary>
/// <remarks>
/// <para>🔑 <b>One definition, used by every calendar file we produce</b> — the coordinator's own
/// download and the organizer's e-mailed invite. The same invitation gets forwarded around a company
/// by people who did not receive it from us, so two versions of this text would eventually be
/// circulating in the same building.</para>
///
/// <para>🔴 <b>Three of these sentences are consent, not marketing</b>, and they are why the text is
/// here rather than typed into a template someone can trim: participation is <b>optional for each
/// individual</b>, the photo is <b>not used by us in any way</b>, and it is the <b>company's own</b>
/// to do with as they wish. CEH never mails a company's attendees about the photo (§1077's
/// compliance rule); this invitation is what reaches them, forwarded by their own colleague, so the
/// permissions have to travel with it.</para>
///
/// <para>🔒 <b>Evergreen.</b> The edition and community names are arguments — his sample said
/// "ELDK26" and "ELDK", which are this edition and this community, not the product.</para>
/// </remarks>
public static class GroupPhotoInviteText
{
    /// <summary>Each company's slot length, in the invitation's own words.</summary>
    public const int DefaultMaxMinutes = 5;

    /// <summary>The default meeting point (operator 2026-08-11). Overridable per registration.</summary>
    public const string DefaultMeetingPoint =
        "The meeting point is at the INFO booth near the Expo entrance. "
        + "Look for the sign labeled “VOLUME PACKAGE GROUP PHOTO”.";

    /// <summary>
    /// The invitation body.
    /// </summary>
    /// <param name="eventDisplayName">e.g. "ELDK26" — the edition this is part of.</param>
    /// <param name="communityName">Who is running it, for the "will not use the photo" sentence.</param>
    /// <param name="meetingPoint">Null ⇒ <see cref="DefaultMeetingPoint"/>.</param>
    /// <param name="maxMinutes">The per-company allowance.</param>
    public static string Build(
        string? eventDisplayName,
        string? communityName = null,
        string? meetingPoint = null,
        int maxMinutes = DefaultMaxMinutes)
    {
        var edition = string.IsNullOrWhiteSpace(eventDisplayName) ? "the conference" : eventDisplayName.Trim();
        var community = string.IsNullOrWhiteSpace(communityName) ? "The organizers" : communityName.Trim();
        var where = string.IsNullOrWhiteSpace(meetingPoint) ? DefaultMeetingPoint : meetingPoint.Trim();

        return
            $"As part of the {edition} conference, your company has qualified for a group photo "
            + "session during the event.\n\n"
            + "Your management has accepted this opportunity, and this invitation may be shared with "
            + "any participants who would like to be included in the photo. Participation is "
            + "completely optional for each individual.\n\n"
            + $"{community} will not use the photo in any way. The image is solely for your "
            + "company's own use, and you may decide how and when to use it.\n\n"
            + "Meeting time:\n"
            // 🔴 §1077.8 — "wording must be adjusted as we preassign" · "it is not able for them to
            // choose". His 2026 copy offered "several time slots to choose from (first come, first
            // served)"; the hub now ASSIGNS the slot, so anything inviting a choice would produce a
            // reply asking for a different time that nobody has a way to grant.
            + $"Your time has been assigned and is shown in this invitation. Each company is "
            + $"allocated a maximum of {maxMinutes} minutes, so please arrive on time — preferably "
            + $"{maxMinutes} minutes before your slot.\n\n"
            + "Meeting point:\n"
            + where;
    }
}
