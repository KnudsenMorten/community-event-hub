using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Email;

/// <summary>Which activity status a broadcast targets.</summary>
public enum BroadcastStatusFilter
{
    /// <summary>Only participants who can sign in (<c>IsActive == true</c>). Default.</summary>
    ActiveOnly = 0,

    /// <summary>Only deactivated participants (<c>IsActive == false</c>).</summary>
    InactiveOnly = 1,

    /// <summary>Both active and inactive participants.</summary>
    All = 2,
}

/// <summary>One resolved broadcast recipient (deduplicated, ready to mail).</summary>
public sealed record BroadcastRecipient(
    string Email,
    string FirstName,
    ParticipantRole? Role,
    bool IsTestUser);

/// <summary>The audience selection an organizer makes on the broadcast form.</summary>
public sealed class BroadcastAudienceOptions
{
    /// <summary>Participant role groups to include. Empty = no participant roles.</summary>
    public IReadOnlyCollection<ParticipantRole> Roles { get; init; } = Array.Empty<ParticipantRole>();

    /// <summary>Also include reconciled attendees (Zoho), which are not Participants.</summary>
    public bool IncludeAttendees { get; init; }

    /// <summary>Which activity status the participant roles are filtered to.</summary>
    public BroadcastStatusFilter Status { get; init; } = BroadcastStatusFilter.ActiveOnly;

    /// <summary>
    /// When true (the default and the safe choice), participants flagged
    /// <see cref="Participant.IsTestUser"/> are excluded — so a real broadcast
    /// never mails the synthetic go-live test cast. Untick to deliberately mail
    /// the test users (e.g. to dry-run a broadcast against the team).
    /// </summary>
    public bool ExcludeTestUsers { get; init; } = true;
}

/// <summary>
/// Turns an organizer's audience selection (roles + status + test-user toggle,
/// optionally attendees) into the deduplicated recipient list a broadcast will
/// mail. Pure and synchronous: callers pass already-loaded participants and
/// attendees, so this is unit-tested without a database. The page queries the
/// edition's rows and hands them here; the same call produces both the count and
/// the previewed list, so what the organizer sees is exactly what is sent.
/// </summary>
public static class BroadcastAudienceFilter
{
    private static string FirstNameOf(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return "there";
        var first = fullName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return first.Length == 0 ? "there" : first[0];
    }

    private static bool MatchesStatus(bool isActive, BroadcastStatusFilter status) =>
        status switch
        {
            BroadcastStatusFilter.ActiveOnly => isActive,
            BroadcastStatusFilter.InactiveOnly => !isActive,
            _ => true,
        };

    /// <summary>
    /// Resolve the final recipient list. Attendees (when included) are always
    /// treated as active real recipients — they are reconciled from Zoho, carry
    /// no activity or test flag, and are appended after the participant roles.
    /// The result is distinct by email (case-insensitive; a participant wins over
    /// an attendee with the same address) and ordered by email for a stable
    /// preview.
    /// </summary>
    public static IReadOnlyList<BroadcastRecipient> Resolve(
        BroadcastAudienceOptions options,
        IEnumerable<Participant> participants,
        IEnumerable<Attendee>? attendees = null)
    {
        var roleSet = options.Roles as IReadOnlySet<ParticipantRole>
                      ?? new HashSet<ParticipantRole>(options.Roles);

        var recipients = new List<BroadcastRecipient>();

        if (roleSet.Count > 0)
        {
            foreach (var p in participants)
            {
                if (!roleSet.Contains(p.Role)) continue;
                if (!MatchesStatus(p.IsActive, options.Status)) continue;
                if (options.ExcludeTestUsers && p.IsTestUser) continue;
                if (string.IsNullOrWhiteSpace(p.Email)) continue;

                recipients.Add(new BroadcastRecipient(
                    p.Email.Trim(), FirstNameOf(p.FullName), p.Role, p.IsTestUser));
            }
        }

        if (options.IncludeAttendees && attendees is not null)
        {
            foreach (var a in attendees)
            {
                if (string.IsNullOrWhiteSpace(a.Email)) continue;
                recipients.Add(new BroadcastRecipient(
                    a.Email.Trim(),
                    string.IsNullOrWhiteSpace(a.FirstName) ? "there" : a.FirstName.Trim(),
                    Role: null,
                    IsTestUser: false));
            }
        }

        return recipients
            .GroupBy(r => r.Email, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(r => r.Email, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
