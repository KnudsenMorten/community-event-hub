using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Reminders;

/// <summary>
/// One self-service form whose submission a participant might need to change
/// after the edition lock date has passed (when the form itself is read-only).
/// </summary>
public enum FormTopic
{
    General = 0,
    Hotel = 1,
    Dinner = 2,
    Lunch = 3,
    Swag = 4,
    Travel = 5,
    Speaker = 6,
}

/// <summary>The outcome of a participant's "request a change" submission.</summary>
public sealed record ChangeRequestResult(bool Accepted, string? FailureReason)
{
    public static ChangeRequestResult Ok() => new(true, null);
    public static ChangeRequestResult Fail(string reason) => new(false, reason);
}

/// <summary>
/// Lets a participant request a change to an <b>already-submitted, now
/// deadline-locked</b> form (REQUIREMENTS §21 Participant "Edit-after-submit /
/// request-change path once a form is deadline-locked"). Once the edition lock
/// date passes the Forms/* pages go read-only, which used to be a dead end —
/// the participant could see their data but had no way to ask for a correction.
///
/// This service turns that dead end into a self-service hand-off: the participant
/// types what they need changed, and the request lands as an
/// <see cref="OrganizerActionItem"/> of type
/// <see cref="OrganizerActionItemService.TypeChangeRequestedPrefix"/> on the EXISTING
/// organizer Action Queue — the same queue that already drains late hotel/dinner
/// edits. No new table, no email send (the queue is the hand-off; organizers
/// reach out through the existing comms tooling).
///
/// Idempotent per (event, participant, topic): re-submitting for the same form
/// refreshes the one open request rather than spamming the queue. A pure,
/// constructor-injected service (EF context + clock) — unit-tested with EF Core
/// InMemory.
/// </summary>
public sealed class FormChangeRequestService
{
    /// <summary>Hard cap on the free-text request, mirroring other public free-text inputs.</summary>
    public const int MaxMessageLength = 1000;

    private readonly CommunityHubDbContext _db;
    private readonly OrganizerActionItemService _actions;

    public FormChangeRequestService(
        CommunityHubDbContext db, OrganizerActionItemService actions)
    {
        _db = db;
        _actions = actions;
    }

    /// <summary>Stable, human-friendly label for a form topic (used in the queue summary).</summary>
    public static string TopicLabel(FormTopic topic) => topic switch
    {
        FormTopic.Hotel   => "Hotel",
        FormTopic.Dinner  => "Appreciation dinner",
        FormTopic.Lunch   => "Lunch",
        FormTopic.Swag    => "Swag",
        FormTopic.Travel  => "Travel claim",
        FormTopic.Speaker => "Speaker info",
        _                 => "General",
    };

    /// <summary>Parse a route/query topic token (case-insensitive); unknown ⇒ General.</summary>
    public static FormTopic ParseTopic(string? token) =>
        Enum.TryParse<FormTopic>((token ?? string.Empty).Trim(), ignoreCase: true, out var t)
            ? t
            : FormTopic.General;

    /// <summary>
    /// The per-topic action-item type code (e.g. "change-requested:Hotel"). One
    /// open row per (event, participant, topic) — a different form gets its own
    /// row, while <see cref="OrganizerActionItemService.LabelFor"/> recognises the
    /// whole "change-requested" family.
    /// </summary>
    public static string TypeFor(FormTopic topic) =>
        $"{OrganizerActionItemService.TypeChangeRequestedPrefix}:{topic}";

    /// <summary>
    /// Submit a change request for one form. Validates the message, confirms the
    /// participant belongs to the event, and upserts a single open action-queue
    /// item per (event, participant, topic). Returns a failure (nothing written)
    /// for a blank/oversized message or a participant outside the edition.
    /// </summary>
    public async Task<ChangeRequestResult> SubmitAsync(
        int eventId, int participantId, FormTopic topic, string? message,
        CancellationToken ct = default)
    {
        var trimmed = (message ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return ChangeRequestResult.Fail("empty");
        }
        if (trimmed.Length > MaxMessageLength)
        {
            return ChangeRequestResult.Fail("too-long");
        }

        // Confirm the participant is real and in THIS edition — never let a
        // crafted id raise an item against someone else's edition.
        var belongs = await _db.Participants.AnyAsync(
            p => p.Id == participantId && p.EventId == eventId, ct);
        if (!belongs)
        {
            return ChangeRequestResult.Fail("unknown-participant");
        }

        var summary = $"{TopicLabel(topic)} change requested: {trimmed}";
        await _actions.UpsertOpenAsync(
            eventId, TypeFor(topic), participantId, summary, ct);

        return ChangeRequestResult.Ok();
    }

    /// <summary>
    /// The participant's own OPEN change requests for this edition (so the page
    /// can show "you already asked for this", newest first). Scoped to the
    /// participant — never another person's requests.
    /// </summary>
    public async Task<IReadOnlyList<OrganizerActionItem>> GetOpenForParticipantAsync(
        int eventId, int participantId, CancellationToken ct = default)
    {
        // Match the whole change-request family (any per-topic suffix). StartsWith
        // is SQL-translatable (LIKE 'change-requested:%').
        var prefix = OrganizerActionItemService.TypeChangeRequestedPrefix + ":";
        return await _db.OrganizerActionItems
            .Where(a => a.EventId == eventId
                        && a.ParticipantId == participantId
                        && a.Type.StartsWith(prefix)
                        && a.ResolvedAt == null)
            .OrderByDescending(a => a.UpdatedAt ?? a.CreatedAt)
            .ToListAsync(ct);
    }
}
