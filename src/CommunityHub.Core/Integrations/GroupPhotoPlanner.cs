using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Integrations;

/// <summary>One of the slots an organizer has defined, offered to the planner.</summary>
/// <param name="SlotId">The stored slot.</param>
/// <param name="StartUtc">When it happens.</param>
/// <param name="IsPreDay">
/// True for a slot on the pre-day. ⚠️ Decisive: a company whose people all hold 1-day tickets can
/// never take one, because they are not at the venue that day.
/// </param>
public sealed record GroupPhotoSlotOption(int SlotId, DateTimeOffset StartUtc, bool IsPreDay);

/// <summary>One company waiting for a slot, with the only facts the planner needs about it.</summary>
/// <param name="RegistrationId">The group-photo registration.</param>
/// <param name="CompanyName">For a stable, explainable order.</param>
/// <param name="CanDoPreDay">
/// False when every one of their people holds a 1-day ticket. ⚠️ A hard constraint, not a
/// preference: a pre-day slot would photograph an empty space.
/// </param>
/// <param name="PinnedSlotId">A slot they have already been TOLD about. The planner works around it.</param>
public sealed record GroupPhotoCandidate(
    int RegistrationId, string CompanyName, bool CanDoPreDay, int? PinnedSlotId);

/// <summary>One proposed assignment: this company, in this slot.</summary>
public sealed record GroupPhotoAssignment(
    int RegistrationId, string CompanyName, int SlotId, DateTimeOffset StartUtc,
    bool WasAlreadyPublished);

/// <summary>
/// The plan: who goes when, which slots are still free, and — just as importantly — who did NOT fit.
/// </summary>
/// <param name="Unplaced">
/// 🔴 Companies with no slot, and why. <b>Never silently dropped.</b> A planner that quietly places
/// 23 of 25 companies is worse than one that refuses: two coordinators would simply never hear from
/// us, and nobody would find out until the day.
/// </param>
public sealed record GroupPhotoPlan(
    IReadOnlyList<GroupPhotoAssignment> Assignments,
    IReadOnlyList<(string CompanyName, string Reason)> Unplaced,
    IReadOnlyList<GroupPhotoSlotOption> UnusedSlots);

/// <summary>
/// §1077 stage 5 — <b>THE PLANNER</b>: fits companies into the slots an organizer has defined,
/// <i>"spread over pre-day and main day, decided by which ticket types the company bought"</i>
/// (operator 2026-08-11).
/// </summary>
/// <remarks>
/// <para>🔑 <b>It CHOOSES from real slots; it does not invent times.</b> Operator: <i>"i will give
/// you specific timeslot to choose from. 25 timeslots"</i>. A generated grid would have produced
/// tidy times that collide with the keynote, the breaks and the lunch queue — the slots are a fact
/// about the running order, and only the organizers know them.</para>
///
/// <para>🔑 <b>Pure and deterministic.</b> No database, no clock, no randomness: the same input
/// always gives the same plan. That is what makes it safe to press "propose" repeatedly while
/// arguing about the result.</para>
///
/// <para>🔴 <b>Two rules do the real work, and both come from the requirement:</b></para>
/// <list type="number">
///   <item><b>1-day-only companies can only take a MAIN-DAY slot.</b> Their people are not at the
///   venue on the pre-day. A constraint, applied before anything else.</item>
///   <item><b>Companies that CAN do the pre-day are placed there first.</b> Main-day slots are the
///   scarce resource — everyone is present, and the 1-day-only companies have nowhere else to go —
///   so spending one on a company that could have been photographed the day before is exactly the
///   decision that makes the plan run out at the end.</item>
/// </list>
///
/// <para>🔒 <b>A published slot is PINNED.</b> Adding a company in January must never move a time
/// eleven people already have in their calendars.</para>
/// </remarks>
public static class GroupPhotoPlanner
{
    public static GroupPhotoPlan Plan(
        IReadOnlyList<GroupPhotoCandidate> candidates,
        IReadOnlyList<GroupPhotoSlotOption> slots)
    {
        var assignments = new List<GroupPhotoAssignment>();
        var unplaced = new List<(string, string)>();

        var byId = slots.ToDictionary(s => s.SlotId);
        var taken = new HashSet<int>();

        // Pinned first: they own their slot, and everything else is arranged around them.
        foreach (var pinned in candidates.Where(c => c.PinnedSlotId is not null))
        {
            if (!byId.TryGetValue(pinned.PinnedSlotId!.Value, out var slot))
            {
                // ⚠️ Their slot has been DELETED since they were told about it. Silence here would
                // leave a company holding a calendar entry for a time that no longer exists.
                unplaced.Add((pinned.CompanyName,
                    "They were given a slot that has since been removed — restore it, or give them "
                    + "a new time and tell them it has moved."));
                continue;
            }
            taken.Add(slot.SlotId);
            assignments.Add(new GroupPhotoAssignment(
                pinned.RegistrationId, pinned.CompanyName, slot.SlotId, slot.StartUtc,
                WasAlreadyPublished: true));
        }

        var free = slots.Where(s => !taken.Contains(s.SlotId)).OrderBy(s => s.StartUtc).ToList();
        var preDay = free.Where(s => s.IsPreDay).ToList();
        var mainDay = free.Where(s => !s.IsPreDay).ToList();

        // 🔑 A STABLE, EXPLAINABLE ORDER: main-day-only companies first — they have one option and
        // must not be crowded out by a company that had two — then alphabetically, so the same
        // input always produces the same plan and an organizer can predict it.
        var toPlace = candidates
            .Where(c => c.PinnedSlotId is null)
            .OrderBy(c => c.CanDoPreDay)
            .ThenBy(c => c.CompanyName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var c in toPlace.Where(c => !c.CanDoPreDay))
        {
            if (mainDay.Count == 0)
            {
                unplaced.Add((c.CompanyName,
                    "No main-day slot left, and all of their people hold 1-day tickets — they are "
                    + "not at the venue on the pre-day."));
                continue;
            }
            assignments.Add(Take(mainDay, c));
        }

        foreach (var c in toPlace.Where(c => c.CanDoPreDay))
        {
            if (preDay.Count > 0) { assignments.Add(Take(preDay, c)); continue; }
            if (mainDay.Count > 0) { assignments.Add(Take(mainDay, c)); continue; }
            unplaced.Add((c.CompanyName, "No slot left on either day."));
        }

        var used = assignments.Select(a => a.SlotId).ToHashSet();
        return new GroupPhotoPlan(
            assignments.OrderBy(a => a.StartUtc).ToList(),
            unplaced,
            slots.Where(s => !used.Contains(s.SlotId)).OrderBy(s => s.StartUtc).ToList());
    }

    private static GroupPhotoAssignment Take(List<GroupPhotoSlotOption> pool, GroupPhotoCandidate c)
    {
        var slot = pool[0];
        pool.RemoveAt(0);
        return new GroupPhotoAssignment(
            c.RegistrationId, c.CompanyName, slot.SlotId, slot.StartUtc, WasAlreadyPublished: false);
    }
}
