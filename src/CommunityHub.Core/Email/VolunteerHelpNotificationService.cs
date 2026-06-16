using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Email;

/// <summary>
/// Notifies the right people when a volunteer raises a
/// <see cref="VolunteerHelpRequest"/> against a task (the help channel of the
/// volunteer work structure). A help request routes to the owning category's
/// <b>supervisor</b> (the volunteer appointed to run that category); the
/// category's organizer <b>lead</b> is CC'd for oversight, exactly as the
/// design states ("visible to the supervisor and the category's organizer lead").
///
/// All sending goes through <see cref="ParticipantEmailService"/>, so the
/// effective-address / secondary-CC routing AND the central
/// <see cref="IEmailSender"/> gating (the DEV redirect-all-to and the PROD
/// allowlist) are applied in exactly one place — this service never bypasses
/// them. The CEH DEV redirect therefore applies automatically.
///
/// Notification is <b>best-effort</b>: the caller (the volunteer's "Ask for
/// help" post) must not fail because mail could not be sent — the request is
/// already persisted and visible in the supervisor's in-hub inbox. The page
/// invokes <see cref="NotifySupervisorAsync"/> after the request is saved and
/// swallows mail errors.
/// </summary>
public sealed class VolunteerHelpNotificationService
{
    private const string TemplateName = "volunteer-help-raised";
    private const string Category = "volunteer-help";

    private readonly CommunityHubDbContext _db;
    private readonly ParticipantEmailService _participantEmail;

    public VolunteerHelpNotificationService(
        CommunityHubDbContext db,
        ParticipantEmailService participantEmail)
    {
        _db = db;
        _participantEmail = participantEmail;
    }

    /// <summary>
    /// The recipients a help request resolves to, exposed so the UI/tests can
    /// assert the routing without sending mail. <see cref="SupervisorParticipantId"/>
    /// is null when the category has no supervisor appointed yet (nobody to
    /// notify by mail — the request still sits in the organizer-lead oversight
    /// view). <see cref="LeadParticipantId"/> is the organizer lead CC'd for
    /// oversight when present.
    /// </summary>
    public readonly record struct HelpRecipients(
        int? SupervisorParticipantId, int? LeadParticipantId);

    /// <summary>
    /// Resolve who a help request notifies: the owning category's supervisor
    /// (primary) and its organizer lead (oversight CC). Returns an empty result
    /// if the request is not found in the edition.
    /// </summary>
    public async Task<HelpRecipients> ResolveRecipientsAsync(
        int eventId, int helpRequestId, CancellationToken ct = default)
    {
        var row = await _db.VolunteerHelpRequests
            .Where(h => h.Id == helpRequestId && h.EventId == eventId)
            .Select(h => new
            {
                h.Category.SupervisorParticipantId,
                h.Category.LeadParticipantId,
            })
            .FirstOrDefaultAsync(ct);

        return row is null
            ? default
            : new HelpRecipients(row.SupervisorParticipantId, row.LeadParticipantId);
    }

    /// <summary>
    /// Send the "a volunteer needs help" email to the category's supervisor,
    /// CC-routing nothing extra here (the lead is sent their own copy so the
    /// per-person effective-address/secondary-CC routing is correct for each).
    /// Returns the number of people emailed (0–2). Does nothing if the request
    /// is unknown or the category has no supervisor appointed.
    /// </summary>
    public async Task<int> NotifySupervisorAsync(
        int eventId, int helpRequestId, CancellationToken ct = default)
    {
        var req = await _db.VolunteerHelpRequests
            .Where(h => h.Id == helpRequestId && h.EventId == eventId)
            .Select(h => new
            {
                h.Message,
                TaskTitle = h.Task.Title,
                CategoryName = h.Category.Name,
                h.Category.SupervisorParticipantId,
                h.Category.LeadParticipantId,
                RequesterName = h.RequestedByParticipant.FullName,
                RequesterEmail = h.RequestedByParticipant.Email,
            })
            .FirstOrDefaultAsync(ct);
        if (req is null) return 0;

        var requesterDisplay = string.IsNullOrWhiteSpace(req.RequesterName)
            ? req.RequesterEmail
            : req.RequesterName;

        var tokens = new Dictionary<string, string>
        {
            ["volunteerName"] = requesterDisplay,
            ["categoryName"] = req.CategoryName,
            ["taskTitle"] = req.TaskTitle,
            ["helpMessage"] = req.Message,
        };

        var sent = 0;

        // Primary: the supervisor who runs the category.
        if (req.SupervisorParticipantId is int supId)
        {
            var to = await _participantEmail.SendTemplateToParticipantAsync(
                eventId, supId, TemplateName, Category, tokens, ct);
            if (to is not null) sent++;
        }

        // Oversight: the organizer lead (only if distinct from the supervisor).
        if (req.LeadParticipantId is int leadId
            && leadId != req.SupervisorParticipantId)
        {
            var to = await _participantEmail.SendTemplateToParticipantAsync(
                eventId, leadId, TemplateName, Category, tokens, ct);
            if (to is not null) sent++;
        }

        return sent;
    }
}
