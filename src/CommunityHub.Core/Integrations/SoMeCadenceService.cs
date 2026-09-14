using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Integrations;

/// <summary>One type's effective cadence — the override when set, else the shipped default.</summary>
public sealed record SoMeCadence(SoMeTemplateKind Kind, bool Enabled, int Occurrences, bool IsDefault);

/// <summary>
/// §842.2 — reads and writes the per-type frequency, defaulting to the shipped §824.1 numbers.
/// </summary>
/// <remarks>
/// 🔒 The defaults live HERE and in one place, so "what does this edition do if nobody has touched
/// the page?" has a single answer. They are the counts §824.1 specified and that shipped hardcoded.
/// </remarks>
public sealed class SoMeCadenceService
{
    private readonly CommunityHubDbContext _db;

    public SoMeCadenceService(CommunityHubDbContext db) => _db = db;

    /// <summary>The four types this governs. Type 5 is excluded — its dates come from his deck.</summary>
    public static readonly IReadOnlyList<SoMeTemplateKind> GovernedKinds = new[]
    {
        SoMeTemplateKind.SpeakerTracks,
        SoMeTemplateKind.Session,
        SoMeTemplateKind.SponsorCategory,
        SoMeTemplateKind.Sponsor,
    };

    /// <summary>§824.1's shipped counts — what applies until he changes one.</summary>
    public static int DefaultOccurrences(SoMeTemplateKind kind) => kind switch
    {
        // §1185 — operator 2026-09-12: *"speaker tracks must have 3 rounds in the some post"*.
        // ⚠️ Only the DEFAULT: `Times()` reads a saved cadence row first, so an edition whose
        // posting-frequency page says 2 keeps 2 and its round-3 date governs nothing.
        SoMeTemplateKind.SpeakerTracks => 3,
        SoMeTemplateKind.Session => 1,
        SoMeTemplateKind.SponsorCategory => 2,
        SoMeTemplateKind.Sponsor => 2,
        _ => 1,
    };

    public static string Label(SoMeTemplateKind kind) => kind switch
    {
        SoMeTemplateKind.SpeakerTracks => "Type 1 — one post per track",
        SoMeTemplateKind.Session => "Type 2 — one post per session",
        SoMeTemplateKind.SponsorCategory => "Type 3 — one post per sponsor tier",
        SoMeTemplateKind.Sponsor => "Type 4 — one post per sponsor",
        SoMeTemplateKind.EventPost => "Type 5 — your own event posts",
        _ => kind.ToString(),
    };

    /// <summary>Every governed type's effective cadence, in display order.</summary>
    public async Task<IReadOnlyList<SoMeCadence>> GetAllAsync(int eventId, CancellationToken ct = default)
    {
        var rows = await _db.SoMeCadenceSettings
            .Where(s => s.EventId == eventId)
            .AsNoTracking()
            .ToDictionaryAsync(s => s.Kind, ct);

        return GovernedKinds
            .Select(k => rows.TryGetValue(k, out var r)
                ? new SoMeCadence(k, r.Enabled, Math.Max(0, r.Occurrences), IsDefault: false)
                : new SoMeCadence(k, true, DefaultOccurrences(k), IsDefault: true))
            .ToList();
    }

    /// <summary>
    /// 🔴 §842.5 — SPONSORS ARE A LEGAL OBLIGATION: <b>every sponsor must be announced twice.</b>
    /// Operator 2026-08-05: <i>"and for sponsor i have legal obligations so all must come 2 times"</i>.
    /// </summary>
    public const int SponsorLegalMinimum = 2;

    /// <summary>
    /// True when this type carries the §842.5 contractual minimum. Sponsor CATEGORY posts are
    /// included: a tier post is how the smaller sponsors get their named mention.
    /// </summary>
    public static bool IsLegallyObligated(SoMeTemplateKind kind) =>
        kind is SoMeTemplateKind.Sponsor or SoMeTemplateKind.SponsorCategory;

    /// <summary>
    /// Why a proposed setting would breach §842.5, or null when it is fine. Returned rather than
    /// thrown so the page can show it as a refusal in his words.
    /// </summary>
    public static string? LegalProblemWith(SoMeTemplateKind kind, bool enabled, int occurrences)
    {
        if (!IsLegallyObligated(kind)) return null;

        if (!enabled)
        {
            return $"{Label(kind)} cannot be turned off: every sponsor must be announced "
                   + $"{SponsorLegalMinimum} times, and that is a contractual obligation, not a preference.";
        }

        if (occurrences < SponsorLegalMinimum)
        {
            return $"{Label(kind)} cannot run fewer than {SponsorLegalMinimum} times per sponsor — "
                   + "it is a contractual obligation. Lowering it would breach every sponsor "
                   + "agreement at once.";
        }

        return null;
    }

    /// <summary>
    /// Upserts one type's setting. Absent rows are created; existing ones updated.
    /// 🔒 REFUSES a change that would breach §842.5 — the guard is here, in the service, so no page
    /// or job can route around it.
    /// </summary>
    public async Task SaveAsync(
        int eventId, SoMeTemplateKind kind, bool enabled, int occurrences,
        string? byEmail, CancellationToken ct = default)
    {
        if (LegalProblemWith(kind, enabled, occurrences) is { } problem)
        {
            throw new InvalidOperationException(problem);
        }

        // Clamped, not trusted: a negative count would make the planner's loop meaningless, and an
        // absurd one would flood the queue before anyone noticed.
        occurrences = Math.Clamp(occurrences, 0, 12);

        var row = await _db.SoMeCadenceSettings
            .FirstOrDefaultAsync(s => s.EventId == eventId && s.Kind == kind, ct);

        if (row is null)
        {
            row = new SoMeCadenceSetting { EventId = eventId, Kind = kind };
            _db.SoMeCadenceSettings.Add(row);
        }

        row.Enabled = enabled;
        row.Occurrences = occurrences;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        row.LastUpdatedByEmail = byEmail;

        await _db.SaveChangesAsync(ct);
    }
}
