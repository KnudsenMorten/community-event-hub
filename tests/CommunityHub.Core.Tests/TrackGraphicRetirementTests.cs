using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Graphics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// 🔴 §1214 — A GRAPHIC MUST NOT OUTLIVE THE THING IT DEPICTS.
///
/// <para>Two gaps recorded in REQUIREMENTS and fixed together because they are the same shape: a
/// picture that is still handed out while its subject has moved on.</para>
///
/// <para><b>(a)</b> The sweep builds from the sessions that EXIST, so a track whose last session was
/// deleted, retracked or excluded (§1178) stops appearing in the query — the loop never sees it,
/// nothing retires the asset, and <c>SoMeSubjectGraphic</c> keeps attaching the old GIF to that
/// track's posts. The company page then shows speakers who are not in that track any more.</para>
///
/// <para><b>(b)</b> An organizer-overridden graphic is never rebuilt, which is CORRECT — his artwork
/// is his — but the line-up can change afterwards and nothing said so.</para>
/// </summary>
public sealed class TrackGraphicRetirementTests
{
    private const int EventId = 1;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"track-gfx-{Guid.NewGuid():N}").Options);

    private static SoMeBundleBuildService NewService(CommunityHubDbContext db) =>
        new(db,
            graphics: null!,   // never reached: these drive the reconcile pass directly
            store: null!,
            options: Options.Create(new GraphicsSharePointOptions()),
            paths: null!,
            log: NullLogger<SoMeBundleBuildService>.Instance);

    private static GraphicAsset TrackAsset(
        string slug, GraphicAssetStatus status = GraphicAssetStatus.Released,
        bool overridden = false, string? hash = "hash-v1") => new()
    {
        EventId = EventId,
        Type = GraphicAssetType.TrackBundle,
        StableKey = slug,
        FileName = $"track-{slug}.gif",
        Status = status,
        IsOrganizerOverridden = overridden,
        InputHash = hash,
    };

    private static IReadOnlySet<string> Live(params string[] slugs) =>
        slugs.ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, string> Hashes(params (string Slug, string Hash)[] pairs) =>
        pairs.ToDictionary(p => p.Slug, p => p.Hash, StringComparer.OrdinalIgnoreCase);

    /// <summary>🔴 (a) The reported gap: the track is gone, so its graphic stops being published.</summary>
    [Fact]
    public async Task A_track_with_no_sessions_left_has_its_graphic_un_released()
    {
        using var db = NewDb();
        db.GraphicAssets.Add(TrackAsset("security"));
        await db.SaveChangesAsync();

        var (retired, stale) = await NewService(db)
            .ReconcileTrackGraphicsAsync(EventId, Live("azure"), Hashes(), default);

        Assert.Equal(1, retired);
        Assert.Empty(stale);

        var after = await db.GraphicAssets.AsNoTracking().FirstAsync();
        Assert.Equal(GraphicAssetStatus.Generated, after.Status);

        // 🔒 Un-released, NOT destroyed: the file and the row survive, so the track coming back
        // releases it again rather than costing a re-render of artwork that still exists.
        Assert.Equal("track-security.gif", after.FileName);
    }

    /// <summary>A track that still has sessions is left exactly as it is.</summary>
    [Fact]
    public async Task A_live_track_keeps_its_released_graphic()
    {
        using var db = NewDb();
        db.GraphicAssets.Add(TrackAsset("security"));
        await db.SaveChangesAsync();

        var (retired, _) = await NewService(db)
            .ReconcileTrackGraphicsAsync(EventId, Live("security"), Hashes(("security", "hash-v1")), default);

        Assert.Equal(0, retired);
        Assert.Equal(
            GraphicAssetStatus.Released,
            (await db.GraphicAssets.AsNoTracking().FirstAsync()).Status);
    }

    /// <summary>⚠️ It is idempotent — an already-retired graphic is not retired again every sweep.</summary>
    [Fact]
    public async Task A_second_sweep_retires_nothing_more()
    {
        using var db = NewDb();
        db.GraphicAssets.Add(TrackAsset("security"));
        await db.SaveChangesAsync();

        var svc = NewService(db);
        await svc.ReconcileTrackGraphicsAsync(EventId, Live(), Hashes(), default);
        var (again, _) = await svc.ReconcileTrackGraphicsAsync(EventId, Live(), Hashes(), default);

        Assert.Equal(0, again);
    }

    /// <summary>
    /// 🛑 (b) HIS OWN ARTWORK IS NEVER UN-RELEASED, even for a track that has vanished. Reversing
    /// a decision he made by hand is his call, not a sweep's.
    /// </summary>
    [Fact]
    public async Task An_organizer_overridden_graphic_is_never_retired()
    {
        using var db = NewDb();
        db.GraphicAssets.Add(TrackAsset("security", overridden: true));
        await db.SaveChangesAsync();

        var (retired, _) = await NewService(db)
            .ReconcileTrackGraphicsAsync(EventId, Live(), Hashes(), default);

        Assert.Equal(0, retired);
        Assert.Equal(
            GraphicAssetStatus.Released,
            (await db.GraphicAssets.AsNoTracking().FirstAsync()).Status);
    }

    /// <summary>🔴 (b) The second gap: his artwork no longer matches its line-up, and it SAYS so.</summary>
    [Fact]
    public async Task An_overridden_graphic_whose_line_up_changed_is_reported()
    {
        using var db = NewDb();
        db.GraphicAssets.Add(TrackAsset("security", overridden: true, hash: "hash-v1"));
        await db.SaveChangesAsync();

        var (retired, stale) = await NewService(db).ReconcileTrackGraphicsAsync(
            EventId, Live("security"), Hashes(("security", "hash-v2-new-speaker")), default);

        // Reported, never rebuilt — overwriting the work he did by hand is worse than a stale file.
        Assert.Equal(0, retired);
        Assert.Contains("security", stale);
    }

    /// <summary>…and an overridden graphic that still matches is silent.</summary>
    [Fact]
    public async Task An_overridden_graphic_that_still_matches_is_not_reported()
    {
        using var db = NewDb();
        db.GraphicAssets.Add(TrackAsset("security", overridden: true, hash: "hash-v1"));
        await db.SaveChangesAsync();

        var (_, stale) = await NewService(db).ReconcileTrackGraphicsAsync(
            EventId, Live("security"), Hashes(("security", "hash-v1")), default);

        Assert.Empty(stale);
    }

    /// <summary>
    /// ⚠️ An asset with NO recorded hash is not reported. It predates the hash column, so a
    /// mismatch would be an artefact of the upgrade rather than a line-up change — and a false
    /// alarm on his own artwork is exactly the kind of noise that gets a warning ignored.
    /// </summary>
    [Fact]
    public async Task An_overridden_graphic_with_no_recorded_hash_is_not_reported()
    {
        using var db = NewDb();
        db.GraphicAssets.Add(TrackAsset("security", overridden: true, hash: null));
        await db.SaveChangesAsync();

        var (_, stale) = await NewService(db).ReconcileTrackGraphicsAsync(
            EventId, Live("security"), Hashes(("security", "hash-v2")), default);

        Assert.Empty(stale);
    }

    /// <summary>🔒 Another edition's graphics are never touched.</summary>
    [Fact]
    public async Task A_different_edition_is_left_alone()
    {
        using var db = NewDb();
        var other = TrackAsset("security");
        other.EventId = 99;
        db.GraphicAssets.Add(other);
        await db.SaveChangesAsync();

        var (retired, _) = await NewService(db)
            .ReconcileTrackGraphicsAsync(EventId, Live(), Hashes(), default);

        Assert.Equal(0, retired);
        Assert.Equal(
            GraphicAssetStatus.Released,
            (await db.GraphicAssets.AsNoTracking().FirstAsync()).Status);
    }
}
