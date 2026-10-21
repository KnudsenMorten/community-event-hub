using CommunityHub.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Organizer;

/// <summary>
/// §1312 — link several participant rows as ONE human from the participants list, with the primary picked automatically
/// (operator 2026-09-27: "i dont like the link as duplicate is under a person … easier to have a link button where i made
/// the view" · "link selected as one person").
/// </summary>
/// <remarks>
/// The primary is the row every other selected row can be linked to WITHOUT losing anything (<see cref="DuplicateLinkGuard"/>):
/// tried in order of age (oldest first), the first that works wins. Nothing fits ⇒ refuse and say why — the same "refuse
/// rather than guess" rule as the per-row link. No chains: a selected row that is already the primary of an UNselected row,
/// or the duplicate of an unselected row, stops the link. Rows already linked to the chosen primary are left as they are.
/// </remarks>
public static class SamePersonLinker
{
    public sealed record Outcome(bool Ok, string Message, int? PrimaryId = null, int Linked = 0);

    public static async Task<Outcome> LinkAsync(CommunityHubDbContext db, int eventId, IEnumerable<int> selectedIds, CancellationToken ct = default)
    {
        var ids = selectedIds.Where(i => i > 0).Distinct().ToList();
        if (ids.Count < 2) return new(false, "Tick at least two rows that are the same person.");

        var rows = await db.Participants.Where(p => p.EventId == eventId && ids.Contains(p.Id)).ToListAsync(ct);
        if (rows.Count != ids.Count) return new(false, "One of the ticked rows is not in this edition.");

        foreach (var r in rows.Where(r => r.SamePersonAsId is int to && !ids.Contains(to)))
            return new(false, $"#{r.Id} ({r.FullName}) is already linked to #{r.SamePersonAsId}, which is not ticked — tick it too, or unlink #{r.Id} first.");
        var outside = await db.Participants
            .Where(p => p.SamePersonAsId != null && ids.Contains(p.SamePersonAsId.Value) && !ids.Contains(p.Id))
            .Select(p => new { p.Id, To = p.SamePersonAsId!.Value }).FirstOrDefaultAsync(ct);
        if (outside is not null)
            return new(false, $"#{outside.To} is already the primary for #{outside.Id}, which is not ticked — tick it too.");

        var reasons = new List<string>();
        foreach (var candidate in rows.OrderBy(r => r.Id))
        {
            var blocked = false;
            foreach (var other in rows.Where(r => r.Id != candidate.Id))
            {
                var lost = await DuplicateLinkGuard.WouldLoseAsync(db, eventId, other.Id, candidate.Id, ct);
                if (lost.Count == 0) continue;
                reasons.Add($"#{candidate.Id} ({candidate.FullName}) as primary would drop #{other.Id}'s "
                            + string.Join(", ", lost.Select(l => l.Label)));
                blocked = true;
                break;
            }
            if (blocked) continue;

            // Every other ticked row points at the chosen primary — including one that pointed at another ticked row, so
            // there are no chains; the primary represents itself.
            candidate.SamePersonAsId = null;
            var others = rows.Where(r => r.Id != candidate.Id).ToList();
            var linked = others.Count(r => r.SamePersonAsId != candidate.Id);
            foreach (var other in others) other.SamePersonAsId = candidate.Id;
            await db.SaveChangesAsync(ct);
            return new(true,
                $"Linked as one person: #{candidate.Id} ({candidate.FullName}) is the primary; "
                + string.Join(", ", rows.Where(r => r.Id != candidate.Id).Select(r => $"#{r.Id} ({r.FullName})"))
                + " now count as that person. Hotel, swag, lunch, dinner and party count the primary only; everyone can still sign in.",
                candidate.Id, linked);
        }
        return new(false, "No ticked row can be the primary without dropping something another row holds: "
                          + string.Join("; ", reasons) + ". Move it to one row first, or link by hand.");
    }
}
