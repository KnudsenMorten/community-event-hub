using System.Text;

namespace CommunityHub.Core.Volunteers;

/// <summary>
/// The always-on, no-dependency fallback for <see cref="ITaskGuidanceGenerator"/>.
/// Derives a sensible Pre-req + Expectations + a detailed Description from keywords
/// in the task title — no network, no secret, deterministic (so tests can assert it).
/// It is intentionally generic: the organizer edits the result, and the AI provider
/// replaces it when a key is configured. The §151 Description reuses the same
/// keyword-derived prereq/expectation so it never needs a separate rule table.
/// </summary>
public sealed class HeuristicTaskGuidanceGenerator : ITaskGuidanceGenerator
{
    public bool IsAiBacked => false;

    // Keyword → (prerequisite hint, expectation hint). First match wins; order is
    // most-specific first. Lower-cased title is matched with Contains.
    private static readonly (string Keyword, string Prereq, string Expectation)[] Rules =
    {
        ("print",      "Artwork/files are final and a working printer with stock is available.",
                       "All items are printed, counted, and set aside for handover."),
        ("badge",      "The attendee/volunteer list is finalized and the printer is loaded.",
                       "Every badge is printed correctly and sorted for pickup."),
        ("pack",       "All items to be packed are on hand and a packing station is set up.",
                       "Everything is packed, counted, and staged for distribution."),
        ("setup",      "The area is cleared and all equipment/materials have arrived on site.",
                       "The area is fully set up, tested, and ready for use."),
        ("mount",      "Mounting hardware and the items to mount are on site.",
                       "All items are mounted securely and in the agreed position."),
        ("test",       "The system/equipment is installed and powered.",
                       "The system is verified working and any issues are logged."),
        ("a/v",        "A/V gear is delivered and cabling is available.",
                       "Audio/video is connected, tested, and confirmed working."),
        ("deliver",    "Pickup location and destination are confirmed.",
                       "Items are delivered to the correct place and receipt is confirmed."),
        ("charge",     "Chargers and a power source are available.",
                       "All devices are fully charged and ready to deploy."),
        ("clean",      "Cleaning supplies are available and the area is clear of people.",
                       "The area is clean and ready for the next use."),
        ("teardown",   "The session/area has ended and is clear of attendees.",
                       "Everything is packed down, sorted, and the venue is restored."),
        ("check-in",   "The check-in system and lanes are set up and staffed.",
                       "Attendees are checked in smoothly with no queue build-up."),
        ("validate",   "Source data/list is available to validate against.",
                       "All entries are validated and discrepancies are flagged."),
        ("coordinate", "Relevant contacts and the plan are known.",
                       "All parties are aligned and the hand-off is agreed."),
    };

    public Task<TaskGuidance> GenerateAsync(
        string taskTitle,
        string? bucketName = null,
        string? responsibleTeam = null,
        CancellationToken ct = default)
    {
        var title = (taskTitle ?? string.Empty).Trim();
        if (title.Length == 0)
            return Task.FromResult(TaskGuidance.Empty);

        var lower = title.ToLowerInvariant();
        foreach (var (keyword, prereq, expectation) in Rules)
        {
            if (lower.Contains(keyword))
            {
                var decoratedPrereq = Decorate(prereq, bucketName, responsibleTeam);
                return Task.FromResult(new TaskGuidance(
                    decoratedPrereq,
                    expectation,
                    BuildDescription(title, decoratedPrereq, expectation)));
            }
        }

        // Generic fallback when no keyword matches.
        var genericPrereq = Decorate(
            "Everything needed to start the task is available and the responsible people are briefed.",
            bucketName, responsibleTeam);
        var genericExpectation =
            $"\"{title}\" is completed to the agreed standard and the supervisor is informed.";
        return Task.FromResult(new TaskGuidance(
            genericPrereq,
            genericExpectation,
            BuildDescription(title, genericPrereq, genericExpectation)));
    }

    /// <summary>
    /// Compose a detailed, human-readable Description (§151) from the task title and
    /// the already-derived prereq/expectation. Reuses the keyword logic above rather
    /// than maintaining a second rule table, so the Description always aligns with the
    /// Pre-req/Expectations shown alongside it. Always non-empty for a non-blank title.
    /// </summary>
    private static string BuildDescription(string title, string prereq, string expectation)
    {
        var sb = new StringBuilder();
        sb.Append("Task: ").Append(title).Append('.');
        if (!string.IsNullOrWhiteSpace(expectation))
            sb.Append(" Goal: ").Append(expectation);
        if (!string.IsNullOrWhiteSpace(prereq))
            sb.Append(" Before you start: ").Append(prereq);
        return sb.ToString();
    }

    private static string Decorate(string prereq, string? bucketName, string? team)
    {
        if (string.IsNullOrWhiteSpace(team) && string.IsNullOrWhiteSpace(bucketName))
            return prereq;

        var sb = new StringBuilder(prereq);
        var owner = !string.IsNullOrWhiteSpace(team) ? team : bucketName;
        sb.Append(' ').Append($"Owned by the {owner} team.");
        return sb.ToString();
    }
}
