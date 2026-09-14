using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Tasks;

/// <summary>
/// §1081 — THE ONE ANSWER to <i>"which tasks is this person responsible for?"</i>.
/// </summary>
/// <remarks>
/// <para>🔴 <b>Why this exists.</b> A task reaches a person two ways: it is <b>assigned</b> to them,
/// or it belongs to their <b>sponsor company</b> (<c>AssignedParticipantId = NULL</c> with
/// <c>SponsorCompanyId</c> set — one row per company, so whoever gets to it first completes it for
/// the team). Every surface that shows or chases somebody's obligations must ask for BOTH, and the
/// predicate was hand-written in four places:</para>
/// <list type="bullet">
///   <item><see cref="Participants.ParticipantChecklistBuilder"/> — the hub checklist</item>
///   <item><c>DeadlinesFormService</c> — twice, the Get Started deadlines step</item>
///   <item><c>WebAiHelperOwnDataProvider</c> — what the assistant may say about you</item>
/// </list>
///
/// <para>🔴 <b>And the fifth place forgot it.</b> <see cref="Reminders.TaskReminderBuilder"/> filtered
/// on <c>AssignedParticipantId != null</c>, so company rows could never match: measured on prod
/// 2026-08-13, <b>131</b> open dated sponsor company tasks were chased by nothing, and the
/// coordinator fan-out written for exactly that case had never run. The hub checklist carries a
/// comment about the same bug being fixed there earlier (*"the Hub no longer says 'all complete'
/// while /Sponsor/Tasks shows pending work"*) — so this class of miss has now bitten at least
/// twice.</para>
///
/// <para>🔑 <b>This is §1081 stage 4's GOAL without stage 4's cost.</b> That stage proposed an
/// explicit <c>Scope</c> column (Personal / Company / Team) so *"the assignee-based queries that keep
/// missing shared rows become impossible to write by accident"*. The column needs a migration, a
/// backfill and a pass over every task query; a shared predicate removes the same footgun by making
/// the correct query the easy one to write. ⚠️ It does NOT replace an explicit scope if a THIRD
/// sharing model ever appears (volunteer teams, organizer groups) — at that point the column earns
/// its keep. Until then this is the cheaper half of the same idea.</para>
///
/// <para>⚠️ <b>Deliberately NOT applied to the personal-task SEED paths</b> (Hotel, Lunch, Dinner,
/// Swag, Travel, Signal, the volunteer wizard). Those create and find a task that is by nature one
/// person's — a company-scoped hotel booking is not a thing — and widening them would let one
/// contact's saved answer resolve to a colleague's row.</para>
/// </remarks>
public static class ParticipantTaskQueries
{
    /// <summary>
    /// The tasks this participant is responsible for: their own assigned rows, plus their sponsor
    /// company's shared rows when they belong to one.
    /// </summary>
    /// <param name="sponsorCompanyId">
    /// The participant's <see cref="Participant.SponsorCompanyId"/>, or null for a non-sponsor —
    /// in which case only assigned rows match. 🔒 The null check is INSIDE the predicate on purpose:
    /// without it a non-sponsor would match every task whose <c>SponsorCompanyId</c> is also null,
    /// i.e. every other participant's personal task in the edition.
    /// </param>
    public static IQueryable<ParticipantTask> VisibleTo(
        this IQueryable<ParticipantTask> tasks, int participantId, string? sponsorCompanyId) =>
        tasks.Where(t => t.AssignedParticipantId == participantId
                         || (sponsorCompanyId != null && t.SponsorCompanyId == sponsorCompanyId));
}
