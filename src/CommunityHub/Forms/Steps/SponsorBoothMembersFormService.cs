using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Forms.Steps;

/// <summary>
/// §297 — render + edit model for the inline sponsor Booth-members step. Shows the current members
/// and up to three add rows (name + email + role). Bound with an EMPTY prefix by the wizard host.
/// </summary>
public sealed class SponsorBoothMembersModel
{
    public string? Name1 { get; set; } public string? Email1 { get; set; } public BoothMemberRole Role1 { get; set; }
    public string? Name2 { get; set; } public string? Email2 { get; set; } public BoothMemberRole Role2 { get; set; }
    public string? Name3 { get; set; } public string? Email3 { get; set; } public BoothMemberRole Role3 { get; set; }

    /// <summary>Display-only: the booth members already saved.</summary>
    public List<Member> CurrentMembers { get; set; } = new();
    public sealed record Member(string Name, string Email, string Role);
}

/// <summary>
/// §297 — shared submit-service for the inline sponsor Booth-members step. Previously this step had
/// no inline handler, so the wizard fell back to a link that took the sponsor OUT of the wizard and
/// showed nothing. Now it renders the current members INLINE and lets the sponsor add more without
/// leaving. Persists to <see cref="SponsorBoothMember"/> (hub) with <c>SyncedToZoho=false</c> so a
/// later sync pushes them (the standalone Company Details page still owns edit/delete + the Zoho
/// auto-sync). Self-registers via <see cref="IWizardFormService"/>.
/// </summary>
public sealed class SponsorBoothMembersFormService : IWizardFormService
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public SponsorBoothMembersFormService(CommunityHubDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    private Task<string?> CompanyIdAsync(int participantId, CancellationToken ct) =>
        _db.Participants.Where(p => p.Id == participantId)
            .Select(p => p.SponsorCompanyId).FirstOrDefaultAsync(ct);

    public async Task<SponsorBoothMembersModel> LoadAsync(int eventId, int participantId, CancellationToken ct)
    {
        var model = new SponsorBoothMembersModel();
        var companyId = await CompanyIdAsync(participantId, ct);
        if (companyId is null) return model;
        model.CurrentMembers = await CurrentAsync(eventId, companyId, ct);
        return model;
    }

    private async Task<List<SponsorBoothMembersModel.Member>> CurrentAsync(int eventId, string companyId, CancellationToken ct) =>
        (await _db.SponsorBoothMembers.AsNoTracking()
            .Where(m => m.EventId == eventId && m.SponsorCompanyId == companyId && m.DeletedAt == null)
            .OrderBy(m => m.FirstName).ThenBy(m => m.LastName)
            .Select(m => new { m.FirstName, m.LastName, m.Email, m.Role })
            .ToListAsync(ct))
        .Select(m => new SponsorBoothMembersModel.Member($"{m.FirstName} {m.LastName}".Trim(), m.Email, m.Role.ToString()))
        .ToList();

    public async Task<WizardStepOutcome> SaveAsync(
        SponsorBoothMembersModel model, int eventId, int participantId, string email,
        ModelStateDictionary modelState, CancellationToken ct)
    {
        var companyId = await CompanyIdAsync(participantId, ct);
        if (companyId is null) return WizardStepOutcome.NotRelevant;

        var rows = new[]
        {
            (Name: Trim(model.Name1), Email: NormEmail(model.Email1), Role: model.Role1),
            (Name: Trim(model.Name2), Email: NormEmail(model.Email2), Role: model.Role2),
            (Name: Trim(model.Name3), Email: NormEmail(model.Email3), Role: model.Role3),
        };

        var added = 0;
        for (var i = 0; i < rows.Length; i++)
        {
            var (name, em, role) = rows[i];
            if (name is null && em is null) continue;           // empty row
            if (name is null || em is null || !LooksLikeEmail(em))
            {
                modelState.AddModelError(string.Empty, $"Member {i + 1}: enter both a name and a valid email (or leave both blank).");
                continue;
            }
            var (first, last) = SplitName(name);
            var now = _clock.GetUtcNow();
            var existing = await _db.SponsorBoothMembers.FirstOrDefaultAsync(
                m => m.EventId == eventId && m.SponsorCompanyId == companyId && m.Email == em, ct);
            if (existing is { DeletedAt: null }) continue;      // already a member — skip silently
            if (existing is not null)
            {
                existing.DeletedAt = null;                       // revive a removed member
                existing.FirstName = first; existing.LastName = last ?? ""; existing.Role = role;
                existing.UpdatedAt = now; existing.SyncedToZoho = false;
            }
            else
            {
                _db.SponsorBoothMembers.Add(new SponsorBoothMember
                {
                    EventId = eventId, SponsorCompanyId = companyId,
                    FirstName = first, LastName = last ?? "", Email = em, Role = role, SyncedToZoho = false,
                });
            }
            added++;
        }

        if (added > 0) await _db.SaveChangesAsync(ct);
        model.CurrentMembers = await CurrentAsync(eventId, companyId, ct);

        if (!modelState.IsValid) return WizardStepOutcome.Invalid;

        // §297: the step completes only once there is at least one booth member on file.
        if (model.CurrentMembers.Count == 0)
        {
            modelState.AddModelError(string.Empty, "Please add at least one booth member (the people staffing your booth).");
            return WizardStepOutcome.Invalid;
        }

        // Clear the add rows so a re-render shows empty inputs (the members moved to the list).
        model.Name1 = model.Email1 = model.Name2 = model.Email2 = model.Name3 = model.Email3 = null;
        return WizardStepOutcome.Advance;
    }

    private static string? Trim(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    private static string? NormEmail(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    private static bool LooksLikeEmail(string e) { var at = e.IndexOf('@'); return at > 0 && at < e.Length - 1 && !e.Contains(' '); }
    private static (string First, string? Last) SplitName(string name)
    {
        var parts = name.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length switch { 0 => (name, null), 1 => (parts[0], null), _ => (parts[0], parts[1]) };
    }
}
