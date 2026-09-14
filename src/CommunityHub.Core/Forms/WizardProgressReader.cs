using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Forms;

/// <summary>
/// §1085 — one participant's Get Started progress, normalised across the four wizard services.
/// </summary>
/// <param name="HasWizard">
/// False when this person has no wizard to finish at all — today that is exactly one case: a sponsor
/// contact with no company link, whose checklist is company state they do not have. Such a row must
/// never be reported as 0% (which reads as "has done nothing"); it has no denominator.
/// </param>
/// <param name="EvaluableCount">
/// The steps whose state the hub can actually determine — the §1081 denominator. A sponsor step with
/// <c>Done == null</c> (Company Manager off, no ERP customer number) is shown but never counted, or
/// 100% would be unreachable for that company for ever.
/// </param>
/// <param name="TitlePrefix">
/// The resx prefix its step keys resolve under (<c>SpeakerWiz.Step.</c> / <c>RoleWiz.Step.</c> /
/// <c>SponsorWiz.Step.</c>). 🔑 Keys and a prefix, never sentences: this assembly ships no English —
/// the hub is en + da-DK and the caller localises.
/// </param>
public sealed record WizardProgress(
    bool HasWizard,
    IReadOnlyList<string> OpenKeys,
    IReadOnlyList<string> DoneKeys,
    int EvaluableCount,
    string TitlePrefix)
{
    /// <summary>Nothing to finish — the empty answer for a role/person with no wizard.</summary>
    public static readonly WizardProgress None =
        new(false, Array.Empty<string>(), Array.Empty<string>(), 0, string.Empty);

    public int DoneCount => DoneKeys.Count;

    /// <summary>
    /// The same arithmetic the wizard pages render (<c>DoneCount / EvaluableSteps</c>), so a board
    /// reading this can never disagree with the participant's own progress bar.
    /// </summary>
    public int Percent =>
        EvaluableCount == 0 ? 0 : (int)Math.Round(100.0 * DoneCount / EvaluableCount);

    /// <summary>100% — the same rule the four wizard views use for <c>AllDone</c>.</summary>
    public bool AllDone => HasWizard && EvaluableCount > 0 && DoneCount >= EvaluableCount;
}

/// <summary>
/// §1085 — <b>THE ONE PLACE THAT MAPS A ROLE TO ITS WIZARD.</b>
/// </summary>
/// <remarks>
/// <para>🔑 <b>Why this exists.</b> All seven roles already have a Get Started wizard with a
/// completion percent (<see cref="SpeakerWizardService"/>, <see cref="AttendeeWizardService"/>,
/// <see cref="RoleWizardService"/> for volunteer/media/partner/organizer, and
/// <see cref="SponsorWizardService"/>) — so <i>"what is this person's status?"</i> has had one answer
/// for every role all along. What was missing was somewhere to ask it. The four services return four
/// unrelated step records with no shared interface, so every caller wrote the same
/// <c>switch (role)</c> by hand: <see cref="Reminders.GetStartedCompletionSweep"/> and
/// <see cref="Reminders.GetStartedDigestBuilder"/> both carry one, and the §1085 status board would
/// have been the third.</para>
///
/// <para>🔒 <b>It READS the wizard services; it never re-derives completion.</b> That is the whole
/// reason the digest never disagrees with what a participant sees on their own page, and re-deriving
/// is precisely how §1081 produced two boards the operator could not trust. A second opinion about
/// completion is a bug even when the arithmetic is right, because now there are two answers.</para>
///
/// <para>⚠️ <b>The digest deliberately keeps its own switch.</b> It needs more than progress — per
/// company it also reads which content FIELDS are blank and who completed each done step, which is
/// sponsor-specific and would make this type's return value a union of things no other caller wants.
/// This reader covers the two callers that ask the plain question.</para>
///
/// <para>🔴 <b>A build is several queries per person.</b> The operator's objection when
/// <see cref="Reminders.GetStartedCompletionSweep"/> first shipped (<i>"but dont impact performance
/// necessary with this against sql"</i>) applies to every caller of this type: ask it for a bounded
/// set, never for everybody on every page load.</para>
/// </remarks>
public sealed class WizardProgressReader
{
    private readonly CommunityHubDbContext _db;
    private readonly SpeakerWizardService _speaker;
    private readonly RoleWizardService _role;
    private readonly AttendeeWizardService _attendee;
    private readonly SponsorWizardService _sponsor;

    public WizardProgressReader(
        CommunityHubDbContext db,
        SpeakerWizardService speaker,
        RoleWizardService role,
        AttendeeWizardService attendee,
        SponsorWizardService sponsor)
    {
        _db = db;
        _speaker = speaker;
        _role = role;
        _attendee = attendee;
        _sponsor = sponsor;
    }

    /// <summary>
    /// This participant's wizard progress, from the wizard service their role renders.
    /// Returns <see cref="WizardProgress.None"/> when there is nothing for them to finish.
    /// </summary>
    public async Task<WizardProgress> ReadAsync(
        int eventId, int participantId, ParticipantRole role, CancellationToken ct = default)
    {
        switch (role)
        {
            case ParticipantRole.Speaker:
            {
                var v = await _speaker.BuildAsync(eventId, participantId, ct);
                return Of(v.Steps.Select(s => (s.Key, (bool?)s.Done)), "SpeakerWiz.Step.");
            }

            case ParticipantRole.Attendee:
            {
                var v = await _attendee.BuildAsync(eventId, participantId, ct);
                return Of(v.Steps.Select(s => (s.Key, (bool?)s.Done)), "RoleWiz.Step.");
            }

            case ParticipantRole.Sponsor:
            {
                // Company-scoped: without a company link there is no checklist to finish. The
                // service answers null for exactly that case, so the null is the whole test.
                var v = await _sponsor.BuildAsync(eventId, participantId, ct);
                if (v is null) return WizardProgress.None;
                return Of(v.Steps.Select(s => (s.Key, s.Done)), "SponsorWiz.Step.");
            }

            default:
            {
                if (!RoleWizardService.Handles(role)) return WizardProgress.None;
                var v = await _role.BuildAsync(eventId, participantId, ct);
                return Of(v.Steps.Select(s => (s.Key, (bool?)s.Done)), "RoleWiz.Step.");
            }
        }
    }

    /// <summary>
    /// This participant's role, then their progress — for callers holding only an id.
    /// An id that matches nobody in this edition has no wizard.
    /// </summary>
    public async Task<WizardProgress> ReadAsync(
        int eventId, int participantId, CancellationToken ct = default)
    {
        var role = await _db.Participants.AsNoTracking()
            .Where(p => p.Id == participantId && p.EventId == eventId)
            .Select(p => (ParticipantRole?)p.Role)
            .FirstOrDefaultAsync(ct);
        return role is null
            ? WizardProgress.None
            : await ReadAsync(eventId, participantId, role.Value, ct);
    }

    /// <summary>
    /// Split the steps into open / done, counting a step we cannot evaluate (<c>Done == null</c>) as
    /// NEITHER — §1081's rule, and the behaviour the digest already had: a step nobody can be
    /// required to finish must not be presented as an obligation, and must not sit in a denominator
    /// it can never leave.
    /// </summary>
    private static WizardProgress Of(
        IEnumerable<(string Key, bool? Done)> steps, string titlePrefix)
    {
        var list = steps.ToList();
        return new WizardProgress(
            HasWizard: true,
            OpenKeys: list.Where(s => s.Done == false).Select(s => s.Key).ToList(),
            DoneKeys: list.Where(s => s.Done == true).Select(s => s.Key).ToList(),
            EvaluableCount: list.Count(s => s.Done != null),
            TitlePrefix: titlePrefix);
    }
}
