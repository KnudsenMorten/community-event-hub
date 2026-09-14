using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Volunteers;

/// <summary>
/// §1146 — builds the volunteer availability overview: one row per volunteer, a green/red cell per
/// half-day, and a head-count under every column.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-28: <i>"i need to have an overview view … primarely to preselect people
/// based on their availability … and show the amount below so we have a number - also make this
/// overview available under /Organizer/Volunteers"</i>.</para>
///
/// <para>🔒 <b>ONE builder for BOTH pages.</b> Two pages rendering the same grid from two queries is
/// how §759's "10 awaiting review" mail ended up disagreeing with the queue it described. The
/// pre-selection queue passes the ids it is showing; the Volunteers hub passes null for "every
/// volunteer in the edition". Same rows, same colours, same totals, one definition.</para>
/// </remarks>
public sealed class VolunteerAvailabilityOverviewService
{
    private readonly CommunityHubDbContext _db;

    public VolunteerAvailabilityOverviewService(CommunityHubDbContext db) => _db = db;

    /// <summary>One volunteer's line in the grid.</summary>
    public sealed record Row(
        int ParticipantId,
        string FullName,
        string? Email,
        ParticipantLifecycleState LifecycleState,
        IReadOnlyList<VolunteerAvailabilityGrid.DayCells> Days,
        /// <summary>Their own free-text notes, per day, for the ones who wrote something.</summary>
        IReadOnlyList<string?> Notes)
    {
        /// <summary>How many half-day slots this person is available for, across the edition.</summary>
        public int AvailableHalves =>
            Days.Count(d => d.MorningCounts) + Days.Count(d => d.AfternoonCounts);
    }

    /// <summary>The per-column head-counts he asked for.</summary>
    public sealed record DayTotals(DateOnly Day, int Morning, int Afternoon, int EveningOnly);

    /// <summary>
    /// §1156 — one block of the grid: the people in a given state, with their OWN per-shift counts.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-31: <i>"i need to have all preselected grouped together and then not
    /// preselected for easier overview. We still need to see total at the button."</i></para>
    ///
    /// <para>🔑 <b>The subtotal is the point, not the grouping.</b> He preselects from this page, so
    /// the question he is actually asking is "do the people I have already picked cover this
    /// shift?" — and a single combined total cannot answer it. The group counts say what the
    /// shortlist gives him; the grand total says what the whole pool could.</para>
    /// </remarks>
    public sealed record Group(
        string Label,
        IReadOnlyList<Row> Rows,
        IReadOnlyList<DayTotals> Totals);

    /// <summary>The whole grid.</summary>
    /// <param name="Days">The event days that anybody has answered for, in order.</param>
    /// <param name="Rows">Every row, in one flat list (kept so existing readers are unchanged).</param>
    /// <param name="Totals">The grand totals across every group.</param>
    /// <param name="Groups">§1156 — the same rows, split by state, each with its own totals.</param>
    public sealed record Overview(
        IReadOnlyList<DateOnly> Days,
        IReadOnlyList<Row> Rows,
        IReadOnlyList<DayTotals> Totals,
        IReadOnlyList<Group>? Groups = null)
    {
        public bool IsEmpty => Rows.Count == 0 || Days.Count == 0;
    }

    /// <summary>
    /// §1146b — who has consented to being featured publicly, by participant id.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-28: <i>"include email + phone + Some publish accept (yes/no)"</i>.</para>
    ///
    /// <para>🔒 <b>Only TRUE consents are returned; the caller reads a miss as NO.</b>
    /// <c>ProfileConsent</c> defaults false and the row exists only once someone has been through
    /// the form, so "no row" and "said no" mean the same thing here and must not be told apart into
    /// something that renders as blank. Featuring a volunteer's name and photo because a row was
    /// absent is not a mistake that can be taken back.</para>
    /// </remarks>
    /// <summary>What the volunteer submitted on the sign-up form, for the queue's columns.</summary>
    /// <param name="ProfileConsent">
    /// 🔒 False also when no row exists. The two are the same answer — the flag defaults false and
    /// the row appears only once someone has been through the form — and telling them apart into
    /// something that renders as "unknown" would invite publishing a name and photo on the strength
    /// of a missing row.
    /// </param>
    /// <param name="PhotoUrl">The SharePoint web URL of their uploaded picture, or null.</param>
    public sealed record SignupDetails(bool ProfileConsent, string? PhotoUrl, string? LinkedInUrl);

    /// <summary>
    /// §1146b — the sign-up answers the pre-selection queue shows beside each person.
    /// </summary>
    /// <remarks>
    /// Operator 2026-08-28: <i>"include email + phone + Some publish accept (yes/no)"</i>, then
    /// <i>"can we also get a view picture button"</i>. One query for all of it: these come from the
    /// same row, and fetching them separately would be three round trips for one screen.
    /// </remarks>
    public async Task<IReadOnlyDictionary<int, SignupDetails>> SignupDetailsAsync(
        int eventId, IReadOnlyCollection<int> participantIds, CancellationToken ct = default)
    {
        if (participantIds.Count == 0) return new Dictionary<int, SignupDetails>();

        var ids = participantIds.ToHashSet();
        var rows = await _db.VolunteerAvailabilities
            .Where(v => v.EventId == eventId && ids.Contains(v.ParticipantId))
            .Select(v => new { v.ParticipantId, v.ProfileConsent, v.PhotoUrl, v.LinkedInUrl })
            .ToListAsync(ct);

        return rows.ToDictionary(
            r => r.ParticipantId,
            r => new SignupDetails(r.ProfileConsent, r.PhotoUrl, r.LinkedInUrl));
    }

    /// <summary>
    /// Build the grid for an edition, optionally narrowed to a specific set of volunteers.
    /// </summary>
    /// <param name="participantIds">
    /// The rows to include, or null for every volunteer in the edition. The pre-selection queue
    /// passes what it is already listing so the grid underneath cannot describe a different
    /// population from the table above it.
    /// </param>
    public async Task<Overview> BuildAsync(
        int eventId, IReadOnlyCollection<int>? participantIds = null, CancellationToken ct = default)
    {
        var volunteers = _db.Participants
            .Where(p => p.EventId == eventId && p.Role == ParticipantRole.Volunteer);

        if (participantIds is not null)
        {
            if (participantIds.Count == 0)
                return new Overview([], [], []);

            var ids = participantIds.ToHashSet();
            volunteers = volunteers.Where(p => ids.Contains(p.Id));
        }

        var people = await volunteers
            .Select(p => new { p.Id, p.FullName, p.Email, p.LifecycleState })
            .ToListAsync(ct);

        if (people.Count == 0) return new Overview([], [], []);

        var peopleIds = people.Select(p => p.Id).ToHashSet();

        var answers = await _db.VolunteerDayAvailabilities
            .Where(a => a.EventId == eventId && peopleIds.Contains(a.ParticipantId))
            .Select(a => new { a.ParticipantId, a.Day, a.Level, a.Note })
            .ToListAsync(ct);

        // 🔑 The COLUMNS are the days people have actually answered for, not a hard-coded set. The
        // curated ELDK27 days live in VolunteerDayOptions; inventing the axis here would be a second
        // opinion about which days exist, and would silently show nothing for another edition.
        var days = answers.Select(a => a.Day).Distinct().OrderBy(d => d).ToList();
        if (days.Count == 0) return new Overview([], [], []);

        var byPerson = answers
            .GroupBy(a => a.ParticipantId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(x => x.Day, x => (x.Level, x.Note)));

        var rows = new List<Row>(people.Count);
        foreach (var person in people)
        {
            var cells = new List<VolunteerAvailabilityGrid.DayCells>(days.Count);
            var notes = new List<string?>(days.Count);

            byPerson.TryGetValue(person.Id, out var mine);

            foreach (var day in days)
            {
                if (mine is not null && mine.TryGetValue(day, out var answer))
                {
                    cells.Add(VolunteerAvailabilityGrid.CellsFor(day, answer.Level, answer.Note));
                    notes.Add(VolunteerDayOptions.StripSlot(answer.Note));
                }
                else
                {
                    // No row for this day ⇒ unanswered, which is NOT a refusal.
                    cells.Add(VolunteerAvailabilityGrid.CellsFor(
                        day, VolunteerAvailabilityLevel.Blocked, null, hasAnswer: false));
                    notes.Add(null);
                }
            }

            rows.Add(new Row(
                person.Id, person.FullName ?? "(no name)", person.Email,
                person.LifecycleState, cells, notes));
        }

        // Most-available first: the people he can actually place are the ones he wants to see.
        rows = rows
            .OrderByDescending(r => r.AvailableHalves)
            .ThenBy(r => r.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // §1156 — ONE totalling rule, applied to the whole set and to each group. A second copy for
        // the subtotals is how a footer ends up disagreeing with the rows above it (§759).
        List<DayTotals> Count(IReadOnlyList<Row> of)
        {
            var t = new List<DayTotals>(days.Count);
            for (var i = 0; i < days.Count; i++)
            {
                var index = i;
                t.Add(new DayTotals(
                    days[i],
                    of.Count(r => r.Days[index].MorningCounts),
                    of.Count(r => r.Days[index].AfternoonCounts),
                    of.Count(r => r.Days[index].Afternoon == VolunteerAvailabilityGrid.Cell.EveningOnly)));
            }
            return t;
        }

        var totals = Count(rows);

        // 🔑 Preselected FIRST — his shortlist is what he is reasoning about. Onboarded people are
        // already confirmed and appear only on the all-volunteers page; the queue never shows them,
        // so that block is simply absent there rather than an empty heading.
        //
        // 🔒 An EMPTY group is dropped, not rendered blank: a heading with nothing under it reads as
        // a bug on a page that is scanned quickly.
        var groups = new List<Group>();
        void AddGroup(string label, Func<Row, bool> pick)
        {
            var of = rows.Where(pick).ToList();
            if (of.Count > 0) groups.Add(new Group($"{label} ({of.Count})", of, Count(of)));
        }

        AddGroup("Preselected", r => r.LifecycleState == ParticipantLifecycleState.Preselected);
        AddGroup("Onboarded / confirmed", r => r.LifecycleState == ParticipantLifecycleState.Active);
        AddGroup("Not preselected", r => r.LifecycleState == ParticipantLifecycleState.Inactive);

        return new Overview(days, rows, totals, groups);
    }
}
