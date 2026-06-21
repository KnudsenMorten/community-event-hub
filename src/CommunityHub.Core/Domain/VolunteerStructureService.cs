using CommunityHub.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Domain;

/// <summary>
/// Server-side authority for the volunteer work structure (Category → Subcategory
/// → Task) plus assignments and the help channel. ALL mutations go through here so
/// the permission model is enforced in ONE place, regardless of which page calls
/// it. The page models only resolve the signed-in participant and pass an
/// <see cref="ActorContext"/>; they never trust the client for scope.
///
/// Permission model:
///  - ORGANIZER (the lead / oversight role) = full rights across every category
///    in the edition: create/rename/delete categories, name the lead, APPOINT the
///    supervisor (which elevates a volunteer to category management), manage any
///    subcategory/task, assign volunteers, answer help.
///  - SUPERVISOR (a volunteer appointed on a category) = elevated rights for
///    THAT category ONLY: manage its subcategories/tasks, assign its volunteers,
///    answer its help requests. No rights on categories they do not supervise.
///  - VOLUNTEER = their own assigned tasks (update status) + raise help. No tree
///    edits, no assigning others.
///
/// Authorization failures throw <see cref="VolunteerAccessDeniedException"/> so a
/// caller can map them to 403; "not found / wrong edition" returns null/false.
/// </summary>
public sealed class VolunteerStructureService
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public VolunteerStructureService(CommunityHubDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>The signed-in actor, as the pages know them from the session.</summary>
    public readonly record struct ActorContext(
        int ParticipantId, string Email, ParticipantRole Role, int EventId);

    // =====================================================================
    //  Capability checks (also used by the UIs to show/hide controls).
    // =====================================================================

    /// <summary>True if the actor may manage the tree of <paramref name="categoryId"/>
    /// (subcategories, tasks, assignments, help) — organizer anywhere, or the
    /// appointed supervisor of exactly that category.</summary>
    public async Task<bool> CanManageCategoryAsync(ActorContext actor, int categoryId, CancellationToken ct = default)
    {
        if (actor.Role == ParticipantRole.Organizer) return true;
        if (actor.Role != ParticipantRole.Volunteer) return false;
        // A supervisor manages the bucket — via the legacy single column OR the
        // multi-supervisor join table (Buckets feature). Either grant suffices.
        var legacy = await _db.VolunteerCategories.AnyAsync(
            c => c.Id == categoryId
                 && c.EventId == actor.EventId
                 && c.SupervisorParticipantId == actor.ParticipantId, ct);
        if (legacy) return true;
        return await _db.VolunteerBucketSupervisors.AnyAsync(
            s => s.CategoryId == categoryId
                 && s.EventId == actor.EventId
                 && s.ParticipantId == actor.ParticipantId, ct);
    }

    /// <summary>
    /// True if the participant supervises AT LEAST ONE bucket in the edition
    /// (legacy single-supervisor column OR the multi-supervisor join table). Used
    /// by the nav to show the "Supervisor" dashboard item only to real supervisors
    /// — a cheap indexed existence check, safe to call per request for volunteers.
    /// </summary>
    public async Task<bool> IsSupervisorAsync(int eventId, int participantId, CancellationToken ct = default)
    {
        var legacy = await _db.VolunteerCategories.AnyAsync(
            c => c.EventId == eventId && c.SupervisorParticipantId == participantId, ct);
        if (legacy) return true;
        return await _db.VolunteerBucketSupervisors.AnyAsync(
            s => s.EventId == eventId && s.ParticipantId == participantId, ct);
    }

    private async Task RequireManageCategoryAsync(ActorContext actor, int categoryId, CancellationToken ct)
    {
        if (!await CanManageCategoryAsync(actor, categoryId, ct))
            throw new VolunteerAccessDeniedException(
                $"Participant {actor.ParticipantId} may not manage category {categoryId}.");
    }

    private static void RequireOrganizer(ActorContext actor)
    {
        if (actor.Role != ParticipantRole.Organizer)
            throw new VolunteerAccessDeniedException("Organizer role required.");
    }

    // =====================================================================
    //  Categories (organizer-only).
    // =====================================================================

    public async Task<VolunteerCategory> CreateCategoryAsync(
        ActorContext actor, string name, string? description, CancellationToken ct = default)
    {
        RequireOrganizer(actor);
        name = (name ?? string.Empty).Trim();
        if (name.Length < 2) throw new VolunteerValidationException("Category name is required.");

        var cat = new VolunteerCategory
        {
            EventId = actor.EventId,
            Name = name,
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            CreatedAt = _clock.GetUtcNow(),
        };
        _db.VolunteerCategories.Add(cat);
        await _db.SaveChangesAsync(ct);
        return cat;
    }

    public async Task<bool> RenameCategoryAsync(
        ActorContext actor, int categoryId, string name, string? description, CancellationToken ct = default)
    {
        RequireOrganizer(actor);
        var cat = await _db.VolunteerCategories.FirstOrDefaultAsync(
            c => c.Id == categoryId && c.EventId == actor.EventId, ct);
        if (cat is null) return false;
        name = (name ?? string.Empty).Trim();
        if (name.Length < 2) throw new VolunteerValidationException("Category name is required.");
        cat.Name = name;
        cat.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        cat.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteCategoryAsync(ActorContext actor, int categoryId, CancellationToken ct = default)
    {
        RequireOrganizer(actor);
        var cat = await _db.VolunteerCategories.FirstOrDefaultAsync(
            c => c.Id == categoryId && c.EventId == actor.EventId, ct);
        if (cat is null) return false;
        _db.VolunteerCategories.Remove(cat); // cascades to subcategories/tasks/assignments
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Name the volunteer lead (must be an ORGANIZER in the edition).</summary>
    public async Task<bool> SetLeadAsync(
        ActorContext actor, int categoryId, int? leadParticipantId, CancellationToken ct = default)
    {
        RequireOrganizer(actor);
        var cat = await _db.VolunteerCategories.FirstOrDefaultAsync(
            c => c.Id == categoryId && c.EventId == actor.EventId, ct);
        if (cat is null) return false;

        if (leadParticipantId is not null)
        {
            var lead = await _db.Participants.FirstOrDefaultAsync(
                p => p.Id == leadParticipantId && p.EventId == actor.EventId, ct);
            if (lead is null) throw new VolunteerValidationException("Lead not found in this edition.");
            if (lead.Role != ParticipantRole.Organizer)
                throw new VolunteerValidationException("The volunteer lead must be an organizer.");
        }
        cat.LeadParticipantId = leadParticipantId;
        cat.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// APPOINT (or clear) the supervisor of a category. The supervisor must be a
    /// VOLUNTEER in the edition; this is an organizer action that ELEVATES that
    /// volunteer to category-scoped management rights (the appointment row IS the
    /// grant — no global role change, so they remain a normal volunteer elsewhere).
    /// </summary>
    public async Task<bool> AppointSupervisorAsync(
        ActorContext actor, int categoryId, int? supervisorParticipantId, CancellationToken ct = default)
    {
        RequireOrganizer(actor);
        var cat = await _db.VolunteerCategories.FirstOrDefaultAsync(
            c => c.Id == categoryId && c.EventId == actor.EventId, ct);
        if (cat is null) return false;

        if (supervisorParticipantId is not null)
        {
            var sup = await _db.Participants.FirstOrDefaultAsync(
                p => p.Id == supervisorParticipantId && p.EventId == actor.EventId, ct);
            if (sup is null) throw new VolunteerValidationException("Supervisor not found in this edition.");
            if (sup.Role != ParticipantRole.Volunteer)
                throw new VolunteerValidationException("A supervisor must be a volunteer from the pool.");
        }
        cat.SupervisorParticipantId = supervisorParticipantId;
        cat.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// ADD a supervisor to a Bucket (multi-supervisor model). Idempotent per
    /// (bucket, volunteer); the volunteer must be a <see cref="ParticipantRole.Volunteer"/>
    /// in the edition. This is the additive companion to the legacy single
    /// <see cref="AppointSupervisorAsync"/> — a bucket may have one OR MORE.
    /// </summary>
    public async Task<bool> AddSupervisorAsync(
        ActorContext actor, int categoryId, int supervisorParticipantId, CancellationToken ct = default)
    {
        RequireOrganizer(actor);
        var cat = await _db.VolunteerCategories.FirstOrDefaultAsync(
            c => c.Id == categoryId && c.EventId == actor.EventId, ct);
        if (cat is null) return false;

        var sup = await _db.Participants.FirstOrDefaultAsync(
            p => p.Id == supervisorParticipantId && p.EventId == actor.EventId, ct);
        if (sup is null) throw new VolunteerValidationException("Supervisor not found in this edition.");
        if (sup.Role != ParticipantRole.Volunteer)
            throw new VolunteerValidationException("A supervisor must be a volunteer from the pool.");

        var already = await _db.VolunteerBucketSupervisors.AnyAsync(
            s => s.CategoryId == categoryId && s.ParticipantId == supervisorParticipantId, ct);
        if (already) return true; // idempotent

        _db.VolunteerBucketSupervisors.Add(new VolunteerBucketSupervisor
        {
            EventId = actor.EventId,
            CategoryId = categoryId,
            ParticipantId = supervisorParticipantId,
            AppointedByEmail = actor.Email,
            CreatedAt = _clock.GetUtcNow(),
        });
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Remove a supervisor from a Bucket (multi-supervisor model).</summary>
    public async Task<bool> RemoveSupervisorAsync(
        ActorContext actor, int categoryId, int supervisorParticipantId, CancellationToken ct = default)
    {
        RequireOrganizer(actor);
        var row = await _db.VolunteerBucketSupervisors.FirstOrDefaultAsync(
            s => s.CategoryId == categoryId && s.ParticipantId == supervisorParticipantId
                 && s.EventId == actor.EventId, ct);
        if (row is null) return false;
        _db.VolunteerBucketSupervisors.Remove(row);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>All supervisors of a Bucket — the legacy single supervisor (if set)
    /// plus every multi-supervisor row, de-duplicated. This is the canonical
    /// "who are the go-to people" list the volunteer view shows.</summary>
    public async Task<List<Participant>> LoadSupervisorsAsync(
        int eventId, int categoryId, CancellationToken ct = default)
    {
        var ids = await _db.VolunteerBucketSupervisors
            .Where(s => s.EventId == eventId && s.CategoryId == categoryId)
            .Select(s => s.ParticipantId)
            .ToListAsync(ct);

        var legacy = await _db.VolunteerCategories
            .Where(c => c.Id == categoryId && c.EventId == eventId && c.SupervisorParticipantId != null)
            .Select(c => c.SupervisorParticipantId!.Value)
            .FirstOrDefaultAsync(ct);
        if (legacy != 0) ids.Add(legacy);

        var distinct = ids.Distinct().ToList();
        return await _db.Participants
            .Where(p => p.EventId == eventId && distinct.Contains(p.Id))
            .OrderBy(p => p.FullName)
            .ToListAsync(ct);
    }

    /// <summary>Set (or clear) the Bucket's ELDK lead — the go-to person for the
    /// supervisors (third tier). Free text (a name), organizer-only.</summary>
    public async Task<bool> SetBucketEldkLeadAsync(
        ActorContext actor, int categoryId, string? eldkLeadName, CancellationToken ct = default)
    {
        RequireOrganizer(actor);
        var cat = await _db.VolunteerCategories.FirstOrDefaultAsync(
            c => c.Id == categoryId && c.EventId == actor.EventId, ct);
        if (cat is null) return false;
        cat.EldkLeadName = string.IsNullOrWhiteSpace(eldkLeadName) ? null : eldkLeadName.Trim();
        cat.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return true;
    }

    // =====================================================================
    //  Subcategories (organizer anywhere, supervisor of the parent category).
    // =====================================================================

    public async Task<VolunteerSubcategory> CreateSubcategoryAsync(
        ActorContext actor, int categoryId, string name, string? description, CancellationToken ct = default)
    {
        await RequireManageCategoryAsync(actor, categoryId, ct);
        var cat = await _db.VolunteerCategories.FirstOrDefaultAsync(
            c => c.Id == categoryId && c.EventId == actor.EventId, ct)
            ?? throw new VolunteerValidationException("Category not found in this edition.");

        name = (name ?? string.Empty).Trim();
        if (name.Length < 2) throw new VolunteerValidationException("Subcategory name is required.");

        var sub = new VolunteerSubcategory
        {
            EventId = actor.EventId,
            CategoryId = cat.Id,
            Name = name,
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            CreatedAt = _clock.GetUtcNow(),
        };
        _db.VolunteerSubcategories.Add(sub);
        await _db.SaveChangesAsync(ct);
        return sub;
    }

    public async Task<bool> DeleteSubcategoryAsync(
        ActorContext actor, int subcategoryId, CancellationToken ct = default)
    {
        var sub = await _db.VolunteerSubcategories.FirstOrDefaultAsync(
            s => s.Id == subcategoryId && s.EventId == actor.EventId, ct);
        if (sub is null) return false;
        await RequireManageCategoryAsync(actor, sub.CategoryId, ct);
        _db.VolunteerSubcategories.Remove(sub); // cascades to tasks/assignments
        await _db.SaveChangesAsync(ct);
        return true;
    }

    // =====================================================================
    //  Tasks (organizer anywhere, supervisor of the owning category).
    // =====================================================================

    public async Task<VolunteerTask> CreateTaskAsync(
        ActorContext actor, int subcategoryId, string title, string? description,
        DateOnly? due, string? shift,
        int resourcesNeeded = 0,
        VolunteerTaskCriticality criticality = VolunteerTaskCriticality.Unspecified,
        string? responsibleTeam = null,
        string? eldkLeadName = null,
        string? prerequisites = null,
        string? expectations = null,
        string? instructions = null,
        string? timeEnd = null,
        CancellationToken ct = default)
    {
        var sub = await _db.VolunteerSubcategories.FirstOrDefaultAsync(
            s => s.Id == subcategoryId && s.EventId == actor.EventId, ct)
            ?? throw new VolunteerValidationException("Subcategory not found in this edition.");
        await RequireManageCategoryAsync(actor, sub.CategoryId, ct);

        title = (title ?? string.Empty).Trim();
        if (title.Length < 2) throw new VolunteerValidationException("Task title is required.");
        if (resourcesNeeded < 0) throw new VolunteerValidationException("Resources needed cannot be negative.");

        static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        var task = new VolunteerTask
        {
            EventId = actor.EventId,
            SubcategoryId = sub.Id,
            Title = title,
            Description = Clean(description),
            DueDate = due,
            Shift = Clean(shift),
            Status = VolunteerTaskStatus.Open,
            ResourcesNeeded = resourcesNeeded,
            Criticality = criticality,
            ResponsibleTeam = Clean(responsibleTeam),
            EldkLeadName = Clean(eldkLeadName),
            Prerequisites = Clean(prerequisites),
            Expectations = Clean(expectations),
            Instructions = Clean(instructions),
            TimeEnd = Clean(timeEnd),
            CreatedAt = _clock.GetUtcNow(),
        };
        _db.VolunteerTasks.Add(task);
        await _db.SaveChangesAsync(ct);
        return task;
    }

    /// <summary>
    /// Update the Buckets-era detail fields of a task (resources needed, criticality,
    /// responsible team, ELDK lead, pre-req, expectations, instructions, time end).
    /// Organizer anywhere, or the bucket's supervisor. Null leaves a field cleared;
    /// pass the current value to keep it.
    /// </summary>
    public async Task<bool> UpdateTaskDetailsAsync(
        ActorContext actor, int taskId,
        int? resourcesNeeded = null,
        VolunteerTaskCriticality? criticality = null,
        string? responsibleTeam = null,
        string? eldkLeadName = null,
        string? prerequisites = null,
        string? expectations = null,
        string? instructions = null,
        string? timeEnd = null,
        CancellationToken ct = default)
    {
        var task = await TaskWithCategoryAsync(taskId, actor.EventId, ct);
        if (task is null) return false;
        await RequireManageCategoryAsync(actor, task.Subcategory.CategoryId, ct);

        static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        if (resourcesNeeded is not null)
        {
            if (resourcesNeeded < 0) throw new VolunteerValidationException("Resources needed cannot be negative.");
            task.ResourcesNeeded = resourcesNeeded.Value;
        }
        if (criticality is not null) task.Criticality = criticality.Value;
        task.ResponsibleTeam = Clean(responsibleTeam);
        task.EldkLeadName = Clean(eldkLeadName);
        task.Prerequisites = Clean(prerequisites);
        task.Expectations = Clean(expectations);
        task.Instructions = Clean(instructions);
        task.TimeEnd = Clean(timeEnd);
        task.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Mark a task COMPLETED — the sign-off the ELDK lead (or an organizer) gives.
    /// Sets <see cref="VolunteerTask.Status"/> to Done plus the audit stamp
    /// (<see cref="VolunteerTask.CompletedAt"/> / CompletedByEmail). A plain
    /// volunteer cannot complete a task this way (they update their own progress via
    /// <see cref="SetTaskStatusAsync"/>); completion is a managing-actor action.
    /// </summary>
    public async Task<bool> MarkTaskCompletedByLeadAsync(
        ActorContext actor, int taskId, CancellationToken ct = default)
    {
        var task = await TaskWithCategoryAsync(taskId, actor.EventId, ct);
        if (task is null) return false;
        await RequireManageCategoryAsync(actor, task.Subcategory.CategoryId, ct);

        var now = _clock.GetUtcNow();
        task.Status = VolunteerTaskStatus.Done;
        task.CompletedAt = now;
        task.CompletedByEmail = actor.Email;
        task.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Reopen a previously-completed task (clears the completion stamp).</summary>
    public async Task<bool> ReopenTaskAsync(
        ActorContext actor, int taskId, CancellationToken ct = default)
    {
        var task = await TaskWithCategoryAsync(taskId, actor.EventId, ct);
        if (task is null) return false;
        await RequireManageCategoryAsync(actor, task.Subcategory.CategoryId, ct);

        task.Status = VolunteerTaskStatus.Open;
        task.CompletedAt = null;
        task.CompletedByEmail = null;
        task.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteTaskAsync(ActorContext actor, int taskId, CancellationToken ct = default)
    {
        var task = await TaskWithCategoryAsync(taskId, actor.EventId, ct);
        if (task is null) return false;
        await RequireManageCategoryAsync(actor, task.Subcategory.CategoryId, ct);
        _db.VolunteerTasks.Remove(task);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Update a task's status. A managing actor (organizer / category supervisor)
    /// may set any task's status; a plain VOLUNTEER may only update a task they
    /// are assigned to. Anyone else is denied.
    /// </summary>
    public async Task<bool> SetTaskStatusAsync(
        ActorContext actor, int taskId, VolunteerTaskStatus status, CancellationToken ct = default)
    {
        var task = await TaskWithCategoryAsync(taskId, actor.EventId, ct);
        if (task is null) return false;

        var canManage = await CanManageCategoryAsync(actor, task.Subcategory.CategoryId, ct);
        if (!canManage)
        {
            // Plain volunteer: only their own assigned task.
            if (actor.Role != ParticipantRole.Volunteer)
                throw new VolunteerAccessDeniedException("Not permitted to update this task.");
            var assigned = await _db.VolunteerTaskAssignments.AnyAsync(
                a => a.TaskId == taskId && a.ParticipantId == actor.ParticipantId, ct);
            if (!assigned)
                throw new VolunteerAccessDeniedException("You can only update tasks you are assigned to.");
        }

        task.Status = status;
        task.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return true;
    }

    // =====================================================================
    //  Assignments (organizer anywhere, supervisor of the owning category).
    // =====================================================================

    /// <summary>Link a volunteer to a task. Idempotent — re-assigning the same
    /// volunteer is a no-op (the unique index also guards it).</summary>
    public async Task<bool> AssignVolunteerAsync(
        ActorContext actor, int taskId, int volunteerParticipantId, CancellationToken ct = default)
    {
        var task = await TaskWithCategoryAsync(taskId, actor.EventId, ct);
        if (task is null) return false;
        await RequireManageCategoryAsync(actor, task.Subcategory.CategoryId, ct);

        var vol = await _db.Participants.FirstOrDefaultAsync(
            p => p.Id == volunteerParticipantId && p.EventId == actor.EventId, ct);
        if (vol is null) throw new VolunteerValidationException("Volunteer not found in this edition.");
        if (vol.Role != ParticipantRole.Volunteer)
            throw new VolunteerValidationException("Only volunteers can be assigned to volunteer tasks.");

        var already = await _db.VolunteerTaskAssignments.AnyAsync(
            a => a.TaskId == taskId && a.ParticipantId == volunteerParticipantId, ct);
        if (already) return true; // idempotent

        _db.VolunteerTaskAssignments.Add(new VolunteerTaskAssignment
        {
            EventId = actor.EventId,
            TaskId = taskId,
            ParticipantId = volunteerParticipantId,
            AssignedByEmail = actor.Email,
            CreatedAt = _clock.GetUtcNow(),
        });
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> UnassignVolunteerAsync(
        ActorContext actor, int taskId, int volunteerParticipantId, CancellationToken ct = default)
    {
        var task = await TaskWithCategoryAsync(taskId, actor.EventId, ct);
        if (task is null) return false;
        await RequireManageCategoryAsync(actor, task.Subcategory.CategoryId, ct);

        var row = await _db.VolunteerTaskAssignments.FirstOrDefaultAsync(
            a => a.TaskId == taskId && a.ParticipantId == volunteerParticipantId, ct);
        if (row is null) return false;
        _db.VolunteerTaskAssignments.Remove(row);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    // =====================================================================
    //  Help channel.
    // =====================================================================

    /// <summary>
    /// A volunteer assigned to <paramref name="taskId"/> asks their category's
    /// supervisor for help. Only the assigned volunteer (or a managing actor on
    /// behalf, e.g. testing) may raise it; the request inherits the task's
    /// category so the supervisor's inbox query is a single filter.
    /// </summary>
    public async Task<VolunteerHelpRequest> RaiseHelpAsync(
        ActorContext actor, int taskId, string message, CancellationToken ct = default)
    {
        var task = await TaskWithCategoryAsync(taskId, actor.EventId, ct)
            ?? throw new VolunteerValidationException("Task not found in this edition.");

        var assigned = await _db.VolunteerTaskAssignments.AnyAsync(
            a => a.TaskId == taskId && a.ParticipantId == actor.ParticipantId, ct);
        var canManage = await CanManageCategoryAsync(actor, task.Subcategory.CategoryId, ct);
        if (!assigned && !canManage)
            throw new VolunteerAccessDeniedException(
                "Only a volunteer assigned to the task can ask for help on it.");

        message = (message ?? string.Empty).Trim();
        if (message.Length < 2) throw new VolunteerValidationException("Please describe what you need help with.");

        var req = new VolunteerHelpRequest
        {
            EventId = actor.EventId,
            TaskId = taskId,
            CategoryId = task.Subcategory.CategoryId,
            RequestedByParticipantId = actor.ParticipantId,
            Message = message,
            Status = VolunteerHelpStatus.Open,
            CreatedAt = _clock.GetUtcNow(),
        };
        _db.VolunteerHelpRequests.Add(req);
        await _db.SaveChangesAsync(ct);
        return req;
    }

    /// <summary>
    /// The category's supervisor (or an organizer) answers a help request. Moving
    /// to <see cref="VolunteerHelpStatus.Resolved"/> stamps ResolvedAt as well.
    /// </summary>
    public async Task<bool> AnswerHelpAsync(
        ActorContext actor, int helpRequestId, string response,
        VolunteerHelpStatus newStatus, CancellationToken ct = default)
    {
        var req = await _db.VolunteerHelpRequests.FirstOrDefaultAsync(
            h => h.Id == helpRequestId && h.EventId == actor.EventId, ct);
        if (req is null) return false;
        await RequireManageCategoryAsync(actor, req.CategoryId, ct);

        if (newStatus == VolunteerHelpStatus.Open)
            throw new VolunteerValidationException("An answer must move the request to Answered or Resolved.");

        response = (response ?? string.Empty).Trim();
        if (response.Length < 1) throw new VolunteerValidationException("Please enter a response.");

        var now = _clock.GetUtcNow();
        req.Response = response;
        req.RespondedByEmail = actor.Email;
        req.RespondedAt = now;
        req.Status = newStatus;
        if (newStatus == VolunteerHelpStatus.Resolved) req.ResolvedAt = now;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    // =====================================================================
    //  Read helpers (edition-scoped) used by the UIs.
    // =====================================================================

    /// <summary>The full tree for an edition (organizer view), categories →
    /// subcategories → tasks, with lead/supervisor names resolved.</summary>
    public async Task<List<VolunteerCategory>> LoadTreeAsync(int eventId, CancellationToken ct = default) =>
        await _db.VolunteerCategories
            .Where(c => c.EventId == eventId)
            .Include(c => c.LeadParticipant)
            .Include(c => c.SupervisorParticipant)
            .Include(c => c.Subcategories).ThenInclude(s => s.Tasks)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);

    /// <summary>Categories a volunteer supervises in an edition (for the
    /// supervisor dashboard).</summary>
    public async Task<List<VolunteerCategory>> LoadSupervisedCategoriesAsync(
        int eventId, int participantId, CancellationToken ct = default) =>
        await _db.VolunteerCategories
            .Where(c => c.EventId == eventId && c.SupervisorParticipantId == participantId)
            .Include(c => c.Subcategories).ThenInclude(s => s.Tasks)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);

    /// <summary>The tasks a volunteer is assigned to in an edition, with the
    /// owning subcategory + category loaded for the grouped "My tasks" view.</summary>
    public async Task<List<VolunteerTask>> LoadMyTasksAsync(
        int eventId, int participantId, CancellationToken ct = default)
    {
        var taskIds = await _db.VolunteerTaskAssignments
            .Where(a => a.EventId == eventId && a.ParticipantId == participantId)
            .Select(a => a.TaskId)
            .ToListAsync(ct);

        return await _db.VolunteerTasks
            .Where(t => t.EventId == eventId && taskIds.Contains(t.Id))
            .Include(t => t.Subcategory).ThenInclude(s => s.Category)
            .OrderBy(t => t.Subcategory.Category.Name)
            .ThenBy(t => t.Subcategory.Name)
            .ThenBy(t => t.Title)
            .ToListAsync(ct);
    }

    /// <summary>Help requests in a supervisor's category (their inbox).</summary>
    public async Task<List<VolunteerHelpRequest>> LoadHelpForCategoryAsync(
        int eventId, int categoryId, CancellationToken ct = default) =>
        await _db.VolunteerHelpRequests
            .Where(h => h.EventId == eventId && h.CategoryId == categoryId)
            .Include(h => h.RequestedByParticipant)
            .Include(h => h.Task)
            .OrderBy(h => h.Status)
            .ThenByDescending(h => h.CreatedAt)
            .ToListAsync(ct);

    private Task<VolunteerTask?> TaskWithCategoryAsync(int taskId, int eventId, CancellationToken ct) =>
        _db.VolunteerTasks
            .Include(t => t.Subcategory)
            .FirstOrDefaultAsync(t => t.Id == taskId && t.EventId == eventId, ct);
}

/// <summary>Thrown when the actor lacks the permission for a volunteer-structure
/// mutation. Pages map this to a 403 (Forbid).</summary>
public sealed class VolunteerAccessDeniedException : Exception
{
    public VolunteerAccessDeniedException(string message) : base(message) { }
}

/// <summary>Thrown for bad input (missing name, wrong role for lead/supervisor,
/// unknown id in the edition). Pages map this to a friendly validation message.</summary>
public sealed class VolunteerValidationException : Exception
{
    public VolunteerValidationException(string message) : base(message) { }
}
