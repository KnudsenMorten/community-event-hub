using CommunityHub.Core.Data;
using CommunityHub.Core.Domain.Evaluation;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Evaluation;

/// <summary>
/// §1355 — what is drawn on the four rating buttons besides their colour and word. Presentation only
/// (brief §2.2): the stored rating stays 1–4 whatever symbol was shown. Flags; one or two at once.
/// Recorded on every response, so the values are DATA — never renumber.
/// </summary>
[Flags]
public enum RatingSymbols
{
    /// <summary>The drawn faces of the built box — the look before §1355.</summary>
    Faces = 1,
    /// <summary>Stars, one to four filled.</summary>
    Stars = 2,
    /// <summary>The rating as a number, 1–4.</summary>
    Points = 4,
}

/// <summary>
/// §1355 — the symbol choices and how one is chosen. Operator 2026-10-19: *"option to control the symbols show on
/// page 1 … faces, stars (1-4), points (1-4). People can select max 2. you must adjust the layout"* — decided:
/// up to 2 symbol TYPES per button, set like the capture policies (default per channel, override per tablet /
/// per physical room for QR), recorded on each response.
/// </summary>
public static class RatingSymbolChoices
{
    public const RatingSymbols Default = RatingSymbols.Faces;
    public const int MaxCombined = 2;

    /// <summary>Every allowed combination, in the order an organizer is offered them.</summary>
    public static readonly IReadOnlyList<RatingSymbols> All = new[]
    {
        RatingSymbols.Faces, RatingSymbols.Stars, RatingSymbols.Points,
        RatingSymbols.Faces | RatingSymbols.Stars, RatingSymbols.Faces | RatingSymbols.Points,
        RatingSymbols.Stars | RatingSymbols.Points,
    };

    /// <summary>One or two of the known flags — nothing else is valid.</summary>
    public static bool IsValid(int? value)
    {
        if (value is not int v || v <= 0 || (v & ~7) != 0) return false;
        var bits = (v & 1) + ((v >> 1) & 1) + ((v >> 2) & 1);
        return bits is >= 1 and <= MaxCombined;
    }

    public static string Label(RatingSymbols s) => string.Join(" + ", new[]
    {
        s.HasFlag(RatingSymbols.Faces) ? "Faces" : null,
        s.HasFlag(RatingSymbols.Stars) ? "Stars" : null,
        s.HasFlag(RatingSymbols.Points) ? "Points" : null,
    }.Where(x => x is not null));

    /// <summary>What this kiosk shows: its own choice, else the edition's kiosk default, else faces.</summary>
    public static async Task<RatingSymbols> ForDeviceAsync(CommunityHubDbContext db, EvaluationDevice device, CancellationToken ct = default)
    {
        if (device.RatingSymbols is { } own && IsValid((int)own)) return own;
        var edition = await db.Events.Where(e => e.Id == device.EventId).Select(e => e.KioskRatingSymbols).FirstOrDefaultAsync(ct);
        return edition is { } e && IsValid((int)e) ? e : Default;
    }

    /// <summary>
    /// What the QR page shows for a session in <paramref name="scheduleRoomId"/>: the physical room's choice when
    /// all its physical rooms agree, else the edition's QR default, else faces — the same rule as the QR policy.
    /// </summary>
    public static async Task<RatingSymbols> ForQrAsync(CommunityHubDbContext db, int eventId, int? scheduleRoomId, CancellationToken ct = default)
    {
        if (scheduleRoomId is int roomId)
        {
            var overrides = await db.EvaluationRoomPlacements
                .Where(p => p.EvaluationRoomId == roomId && p.PhysicalRoom.QrRatingSymbols != null)
                .Select(p => p.PhysicalRoom.QrRatingSymbols!.Value)
                .Distinct()
                .ToListAsync(ct);
            if (overrides.Count == 1 && IsValid((int)overrides[0])) return overrides[0];
        }
        var edition = await db.Events.Where(e => e.Id == eventId).Select(e => e.QrRatingSymbols).FirstOrDefaultAsync(ct);
        return edition is { } e && IsValid((int)e) ? e : Default;
    }

    /// <summary>The stars for a rating: filled for the rating, hollow for the rest — four positions always.</summary>
    public static string Stars(int rating) =>
        new string('★', Math.Clamp(rating, 0, 4)) + new string('☆', 4 - Math.Clamp(rating, 0, 4));
}
