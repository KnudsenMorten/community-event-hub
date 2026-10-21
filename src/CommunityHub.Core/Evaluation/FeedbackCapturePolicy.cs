using CommunityHub.Core.Data;
using CommunityHub.Core.Domain.Evaluation;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Evaluation;

/// <summary>
/// §1354 — what a feedback channel asks: the rating always; the two follow-up questions (§1278) and/or a
/// free-text box optionally. Stored as int; the values are part of the data (every response records the
/// policy it was captured under), so never renumber.
/// </summary>
public enum FeedbackCapturePolicy
{
    /// <summary>The four rating buttons only — one tap.</summary>
    RatingOnly = 0,
    /// <summary>Rating + a free-text comment (QR only).</summary>
    RatingFreeText = 1,
    /// <summary>Rating + "What was good?" + "What could improve?" — the kiosk's 3 pages.</summary>
    RatingFollowUps = 2,
    /// <summary>Rating + both follow-ups + free text (QR only) — the QR page as it was before §1354.</summary>
    RatingFollowUpsFreeText = 3,
}

/// <summary>
/// §1354 — the capture policies and how one is chosen. Operator 2026-10-19: *"qr get a) 1 page + free text b) 3
/// pages, no free text c) 3 pages + free text. physical devices can have either 1 or 3 pages. i want the
/// possibility to link different capture feedback-policy per device so we can test what works. i want also a
/// default feedback capture kiosk policy, default feedback capture qr code policy"*.
/// </summary>
/// <remarks>
/// 🔑 Resolution — kiosk: the device's own policy → the edition's default kiosk policy → <see cref="DefaultKiosk"/>.
/// QR: the policy of the PHYSICAL room the session is held in → the edition's default QR policy →
/// <see cref="DefaultQr"/>. A kiosk never asks for free text (no comfortable keyboard on a stand), so a free-text
/// policy is narrowed to its no-free-text twin there (<see cref="ForKiosk"/>).
/// </remarks>
public static class FeedbackCapturePolicies
{
    public const FeedbackCapturePolicy DefaultKiosk = FeedbackCapturePolicy.RatingFollowUps;
    public const FeedbackCapturePolicy DefaultQr = FeedbackCapturePolicy.RatingFollowUpsFreeText;

    public static readonly IReadOnlyList<FeedbackCapturePolicy> KioskChoices =
        new[] { FeedbackCapturePolicy.RatingOnly, FeedbackCapturePolicy.RatingFollowUps };

    public static readonly IReadOnlyList<FeedbackCapturePolicy> QrChoices =
        new[]
        {
            FeedbackCapturePolicy.RatingOnly, FeedbackCapturePolicy.RatingFreeText,
            FeedbackCapturePolicy.RatingFollowUps, FeedbackCapturePolicy.RatingFollowUpsFreeText,
        };

    public static bool HasFollowUps(FeedbackCapturePolicy p) =>
        p is FeedbackCapturePolicy.RatingFollowUps or FeedbackCapturePolicy.RatingFollowUpsFreeText;

    public static bool HasFreeText(FeedbackCapturePolicy p) =>
        p is FeedbackCapturePolicy.RatingFreeText or FeedbackCapturePolicy.RatingFollowUpsFreeText;

    /// <summary>A kiosk asks no free text: a free-text policy becomes its twin without it.</summary>
    public static FeedbackCapturePolicy ForKiosk(FeedbackCapturePolicy p) =>
        HasFollowUps(p) ? FeedbackCapturePolicy.RatingFollowUps : FeedbackCapturePolicy.RatingOnly;

    public static bool IsDefined(int? value) =>
        value is int v && Enum.IsDefined(typeof(FeedbackCapturePolicy), v);

    public static string Label(FeedbackCapturePolicy p) => p switch
    {
        FeedbackCapturePolicy.RatingOnly => "Rating only (1 page)",
        FeedbackCapturePolicy.RatingFreeText => "Rating + free text",
        FeedbackCapturePolicy.RatingFollowUps => "Rating + 2 questions (3 pages)",
        FeedbackCapturePolicy.RatingFollowUpsFreeText => "Rating + 2 questions + free text",
        _ => p.ToString(),
    };

    // ---- resolution ------------------------------------------------------------------------------

    /// <summary>What this kiosk asks now: its own policy, else the edition's kiosk default.</summary>
    public static async Task<FeedbackCapturePolicy> ForDeviceAsync(
        CommunityHubDbContext db, EvaluationDevice device, CancellationToken ct = default)
    {
        if (device.CapturePolicy is { } own) return ForKiosk(own);
        var edition = await db.Events.Where(e => e.Id == device.EventId)
            .Select(e => e.KioskCapturePolicy).FirstOrDefaultAsync(ct);
        return ForKiosk(edition ?? DefaultKiosk);
    }

    /// <summary>
    /// What the QR page asks for a session held in <paramref name="scheduleRoomId"/>: the physical room's QR
    /// policy, else the edition's QR default. A schedule room under several physical rooms (a split pre-day
    /// room) uses their policy only when they agree; otherwise the default, rather than guess.
    /// </summary>
    public static async Task<FeedbackCapturePolicy> ForQrAsync(
        CommunityHubDbContext db, int eventId, int? scheduleRoomId, CancellationToken ct = default)
    {
        if (scheduleRoomId is int roomId)
        {
            var overrides = await db.EvaluationRoomPlacements
                .Where(p => p.EvaluationRoomId == roomId && p.PhysicalRoom.QrCapturePolicy != null)
                .Select(p => p.PhysicalRoom.QrCapturePolicy!.Value)
                .Distinct()
                .ToListAsync(ct);
            if (overrides.Count == 1) return overrides[0];
        }
        var edition = await db.Events.Where(e => e.Id == eventId)
            .Select(e => e.QrCapturePolicy).FirstOrDefaultAsync(ct);
        return edition ?? DefaultQr;
    }
}
