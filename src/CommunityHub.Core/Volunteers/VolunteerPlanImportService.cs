using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Volunteers;

/// <summary>The outcome of importing a plan, for the organizer's confirmation screen.</summary>
public sealed record PlanImportResult(
    int BucketsCreated,
    int TasksCreated,
    int AssignmentsLinked,
    int NamesUnmatched,
    int GuidanceFilled,
    IReadOnlyList<string> UnmatchedNames);

/// <summary>
/// Imports a parsed volunteer plan into the DB for an edition: creates Buckets
/// (<see cref="VolunteerCategory"/>) from the distinct responsible teams, then the
/// tasks under a single "Imported plan" subcategory per bucket (the subcategory
/// level is kept as a structural holder — the plan groups by Bucket → Task).
///
/// Behaviour:
///  - Buckets and tasks are upserted by name so re-importing the same plan is
///    idempotent (no duplicate buckets/tasks).
///  - Resource Names are matched to existing volunteer <see cref="Participant"/>
///    rows by FullName (case-insensitive) and linked as real assignments;
///    unmatched names are reported back (and NEVER auto-created — and never put in
///    the repo). The CSV may carry REAL names, but only the dev DB receives them.
///  - Blank Pre-req / Expectations / detailed Description (§151) are filled via
///    <see cref="ITaskGuidanceGenerator"/> (heuristic by default, AI when configured)
///    so every imported task has guidance and a description.
///
/// Importing the real file into the DEV DB is fine (operational data); the file is
/// never copied into the repo.
/// </summary>
public sealed class VolunteerPlanImportService
{
    private readonly CommunityHubDbContext _db;
    private readonly ITaskGuidanceGenerator _guidance;
    private readonly TimeProvider _clock;

    public const string ImportedSubcategoryName = "Imported plan";

    public VolunteerPlanImportService(
        CommunityHubDbContext db, ITaskGuidanceGenerator guidance, TimeProvider clock)
    {
        _db = db;
        _guidance = guidance;
        _clock = clock;
    }

    public async Task<PlanImportResult> ImportAsync(
        int eventId, ParsedPlan plan, bool fillGuidance = true, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        int bucketsCreated = 0, tasksCreated = 0, assignmentsLinked = 0, guidanceFilled = 0;
        var unmatched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Pre-load the edition's volunteers for name matching.
        var volunteersByName = await _db.Participants
            .Where(p => p.EventId == eventId && p.Role == ParticipantRole.Volunteer)
            .ToListAsync(ct);
        var nameLookup = volunteersByName
            .GroupBy(p => p.FullName.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // 1. Upsert buckets (categories) by name.
        var bucketByName = await UpsertBucketsAsync(eventId, plan.Buckets, now, ct);
        bucketsCreated = bucketByName.Values.Count(b => b.WasCreated);

        // 2. Ensure one "Imported plan" subcategory per bucket.
        var subByBucket = await EnsureSubcategoriesAsync(eventId, bucketByName, now, ct);

        // 3. Upsert tasks under their bucket's subcategory.
        foreach (var pt in plan.Tasks)
        {
            var bucket = bucketByName[pt.BucketName].Category;
            var sub = subByBucket[bucket.Id];

            var task = await _db.VolunteerTasks.FirstOrDefaultAsync(
                t => t.EventId == eventId && t.SubcategoryId == sub.Id && t.Title == pt.Title, ct);
            bool isNew = task is null;
            if (task is null)
            {
                task = new VolunteerTask
                {
                    EventId = eventId,
                    SubcategoryId = sub.Id,
                    Title = pt.Title,
                    CreatedAt = now,
                };
                _db.VolunteerTasks.Add(task);
                tasksCreated++;
            }

            task.TimeEnd = pt.TimeEnd;
            task.Status = pt.Status;
            task.Criticality = pt.Criticality;
            task.ResponsibleTeam = pt.ResponsibleTeam;
            task.EldkLeadName = pt.EldkLeadName;
            task.ResourcesNeeded = pt.ResourcesNeeded > 0 ? pt.ResourcesNeeded : pt.ResourceNames.Count;
            task.Prerequisites = pt.Prerequisites;
            task.Expectations = pt.Expectations;
            if (!isNew) task.UpdatedAt = now;

            // Carry the bucket's ELDK lead from the first task that names one.
            if (string.IsNullOrWhiteSpace(bucket.EldkLeadName) && !string.IsNullOrWhiteSpace(pt.EldkLeadName))
                bucket.EldkLeadName = pt.EldkLeadName;

            // Fill missing guidance (heuristic or AI): Pre-req, Expectations, and the
            // §151 detailed Description — all auto-generated from the title when blank.
            if (fillGuidance &&
                (string.IsNullOrWhiteSpace(task.Prerequisites)
                 || string.IsNullOrWhiteSpace(task.Expectations)
                 || string.IsNullOrWhiteSpace(task.Description)))
            {
                var g = await _guidance.GenerateAsync(pt.Title, pt.BucketName, pt.ResponsibleTeam, ct);
                if (string.IsNullOrWhiteSpace(task.Prerequisites) && !string.IsNullOrWhiteSpace(g.Prerequisites))
                    task.Prerequisites = g.Prerequisites;
                if (string.IsNullOrWhiteSpace(task.Expectations) && !string.IsNullOrWhiteSpace(g.Expectations))
                    task.Expectations = g.Expectations;
                if (string.IsNullOrWhiteSpace(task.Description) && !string.IsNullOrWhiteSpace(g.Description))
                    task.Description = g.Description;
                guidanceFilled++;
            }

            await _db.SaveChangesAsync(ct); // ensure task.Id for assignment FK

            // 4. Link resource names → existing volunteers as REAL assignments.
            foreach (var name in pt.ResourceNames)
            {
                if (!nameLookup.TryGetValue(name, out var vol)) { unmatched.Add(name); continue; }
                var already = await _db.VolunteerTaskAssignments.AnyAsync(
                    a => a.TaskId == task.Id && a.ParticipantId == vol.Id, ct);
                if (already) continue;
                _db.VolunteerTaskAssignments.Add(new VolunteerTaskAssignment
                {
                    EventId = eventId,
                    TaskId = task.Id,
                    ParticipantId = vol.Id,
                    AssignedByEmail = "plan-import",
                    CreatedAt = now,
                });
                assignmentsLinked++;
            }
        }

        await _db.SaveChangesAsync(ct);

        return new PlanImportResult(
            bucketsCreated, tasksCreated, assignmentsLinked,
            unmatched.Count, guidanceFilled, unmatched.OrderBy(x => x).ToList());
    }

    private async Task<Dictionary<string, (VolunteerCategory Category, bool WasCreated)>>
        UpsertBucketsAsync(int eventId, IReadOnlyList<string> bucketNames, DateTimeOffset now, CancellationToken ct)
    {
        var existing = await _db.VolunteerCategories
            .Where(c => c.EventId == eventId)
            .ToListAsync(ct);
        var byName = existing.ToDictionary(c => c.Name, c => c, StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, (VolunteerCategory, bool)>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in bucketNames)
        {
            if (byName.TryGetValue(name, out var found))
            {
                result[name] = (found, false);
                continue;
            }
            var cat = new VolunteerCategory { EventId = eventId, Name = name, CreatedAt = now };
            _db.VolunteerCategories.Add(cat);
            result[name] = (cat, true);
        }
        await _db.SaveChangesAsync(ct);
        return result;
    }

    private async Task<Dictionary<int, VolunteerSubcategory>> EnsureSubcategoriesAsync(
        int eventId,
        Dictionary<string, (VolunteerCategory Category, bool WasCreated)> buckets,
        DateTimeOffset now, CancellationToken ct)
    {
        var map = new Dictionary<int, VolunteerSubcategory>();
        foreach (var (cat, _) in buckets.Values)
        {
            var sub = await _db.VolunteerSubcategories.FirstOrDefaultAsync(
                s => s.EventId == eventId && s.CategoryId == cat.Id && s.Name == ImportedSubcategoryName, ct);
            if (sub is null)
            {
                sub = new VolunteerSubcategory
                {
                    EventId = eventId,
                    CategoryId = cat.Id,
                    Name = ImportedSubcategoryName,
                    CreatedAt = now,
                };
                _db.VolunteerSubcategories.Add(sub);
            }
            map[cat.Id] = sub;
        }
        await _db.SaveChangesAsync(ct);
        return map;
    }
}
