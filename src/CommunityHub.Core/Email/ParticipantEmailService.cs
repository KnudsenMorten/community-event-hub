using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Email;

/// <summary>
/// Sends a branded template to one participant, resolving the routing every
/// participant-addressed mail must use:
///   - <b>primary To</b> = the effective address (speaker
///     <see cref="SpeakerProfile.ContactEmailOverride"/> ?? identity
///     <see cref="Participant.Email"/>);
///   - <b>CC</b> = the participant's optional <see cref="Participant.SecondaryEmail"/>
///     (10a-5), additive and distinct from the override.
/// It also sets the ambient <see cref="EmailContext"/> so the
/// <see cref="LoggingEmailSender"/> records a rich audit row, then sends.
///
/// This is the single seam used by the manual individual re-send (10a-2), the
/// onboarding set (10a-1) and the step-reset reminder (10a-6), so the
/// To/CC/logging rules live in exactly one place.
/// </summary>
public sealed class ParticipantEmailService
{
    private readonly CommunityHubDbContext _db;
    private readonly EmailTemplateProvider _templates;
    private readonly IEmailSender _emailSender;
    private readonly IEmailContextAccessor _context;

    public ParticipantEmailService(
        CommunityHubDbContext db,
        EmailTemplateProvider templates,
        IEmailSender emailSender,
        IEmailContextAccessor context)
    {
        _db = db;
        _templates = templates;
        _emailSender = emailSender;
        _context = context;
    }

    /// <summary>Resolve the effective To + alternate-address CC for a participant.
    /// §422: the CC is the RESOLVED alternate — organizer-set <c>SecondaryEmail</c> first, then
    /// the participant's own <c>AlternateEmail</c>. Before this, someone could add their
    /// alternate address on their own profile and no mail code read it, so nothing was ever
    /// copied there while the tasks banner promised it would be.</summary>
    public static (string toEmail, IReadOnlyCollection<string> cc) ResolveRouting(
        string identityEmail, string? speakerOverride, string? secondaryEmail,
        string? alternateEmail = null)
    {
        var to = SpeakerProfile.EffectiveEmailFor(identityEmail, speakerOverride);
        var cc = Participants.AlternateEmailPolicy.CcListFor(secondaryEmail, alternateEmail);
        return (to, cc);
    }

    /// <summary>
    /// Render <paramref name="templateName"/> with <paramref name="extraTokens"/>
    /// (branding tokens are added automatically) and send it to the participant —
    /// effective To + secondary CC, logged under <paramref name="category"/>.
    /// Returns the address the mail was addressed to, or null if the participant
    /// was not found in the edition. NOT idempotency-gated — callers that need a
    /// dedup gate (onboarding) check the ledger themselves.
    /// </summary>
    public async Task<string?> SendTemplateToParticipantAsync(
        int eventId,
        int participantId,
        string templateName,
        string category,
        IReadOnlyDictionary<string, string>? extraTokens = null,
        CancellationToken ct = default)
    {
        var p = await _db.Participants
            .Include(x => x.Event)
            .FirstOrDefaultAsync(x => x.Id == participantId && x.EventId == eventId, ct);
        if (p is null) return null;

        // §253 G9 BACKSTOP: never mail a DEACTIVATED participant through this shared
        // seam (onboarding set, step-reset, digests, manual re-send). Deactivation
        // means "no more prompts"; callers already treat null as skipped/not-found.
        if (!p.IsActive) return null;

        var speakerOverride = await _db.SpeakerProfiles
            .Where(sp => sp.ParticipantId == p.Id)
            .Select(sp => sp.ContactEmailOverride)
            .FirstOrDefaultAsync(ct);
        var (toEmail, cc) = ResolveRouting(p.Email, speakerOverride, p.SecondaryEmail, p.AlternateEmail);

        var firstName = string.IsNullOrWhiteSpace(p.FullName)
            ? "there"
            : p.FullName.Split(' ')[0];

        // §169: pass the participant so the hub CTA (hubUrl) becomes their personal
        // auto-login magic-link. This is the shared per-participant seam (onboarding,
        // step-reset, volunteer-help, speaker-question digest, manual re-send), so
        // every email through it carries the recipient's sign-in link.
        var tokens = _templates.NewTokenSet(p.Id);
        tokens["firstName"] = firstName;
        tokens["communityName"] = p.Event?.CommunityName ?? string.Empty;
        tokens["eventDisplayName"] = p.Event?.DisplayName ?? string.Empty;
        tokens["roleName"] = FriendlyRoleName(p.Role);
        if (extraTokens is not null)
        {
            foreach (var kv in extraTokens) tokens[kv.Key] = kv.Value;
        }

        var rendered = _templates.Render(templateName, tokens);

        // §326ca (operator 2026-07-25: "each of the emails should have a ring-gate attached to
        // any emails being sent"). This shared seam sends MANY different templates — the
        // onboarding set, step-reset, digests, manual re-sends — and passed no FeatureKey, so
        // every one of them was governed only by the global kill switch, never by its own
        // feature's ring. The template already names the owning feature, so derive the key
        // instead of asking each caller to remember it. FeatureKeyFor falls back to
        // "outbound-email" (the transport) for an unmapped template — never LOOSER than the
        // old behaviour, and correctly ring-gated the moment the template is in the catalog.
        var featureKey = EmailTemplateCatalog.FeatureKeyFor(templateName);

        using (_context.Set(new EmailContext(
            category, eventId, p.Id, p.FullName, templateName, FeatureKey: featureKey)))
        {
            await _emailSender.SendAsync(toEmail, rendered.Subject, rendered.HtmlBody, cc, ct);
        }
        return toEmail;
    }

    private static string FriendlyRoleName(ParticipantRole role) => role switch
    {
        ParticipantRole.Organizer => "organizer",
        ParticipantRole.Speaker => "speaker",
        ParticipantRole.Volunteer => "volunteer",
        ParticipantRole.Sponsor => "sponsor contact",
        ParticipantRole.Attendee => "attendee",
        ParticipantRole.Media => "media crew",
        ParticipantRole.EventPartner => "event partner",
        _ => "participant",
    };
}
