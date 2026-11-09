using System.Security.Cryptography;
using System.Text;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations;

/// <summary>§1379.8 — the outcome of one refresh run, in words fit for a log line or a page banner.</summary>
public sealed record WordPressRefreshRunResult(int Updated, int Unchanged, int NotDue, int Failed, string Message);

/// <summary>
/// §1379.8 — "Refresh weekly": re-renders each FLAGGED linked WordPress post from CEH data and replaces
/// its content in place (same id, same URL), even when published.
/// </summary>
/// <remarks>
/// <para>🔒 §31's draft-only rule is lifted for flagged posts ONLY: every write path here re-checks
/// <see cref="WordPressRefreshPost.RefreshWeekly"/>, so an unflagged row is never written to WordPress.</para>
/// <para>🔒 Gated three ways: the <c>content-studio</c> feature (WordPress BlogPost Content, Settings), the
/// connector being configured (<see cref="IWordPressPublisher.CanWrite"/>), and the narrow
/// <see cref="ExternalSystems.WordPressPostRefresh"/> write ceiling (unset ⇒ off on DEV, on in PROD).</para>
/// <para>An unchanged render (same SHA-256 as the last push) is not re-sent. The weekly cadence is
/// <see cref="RefreshInterval"/> per post, measured from <see cref="WordPressRefreshPost.LastRefreshedAt"/>,
/// which is stamped only when the post is known to be current (pushed, or unchanged) — a failed push
/// retries on the next run rather than waiting a week.</para>
/// </remarks>
public sealed class WordPressPostRefreshService
{
    /// <summary>How often a flagged post is refreshed by the scheduled run.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromDays(7);

    private readonly CommunityHubDbContext _db;
    private readonly WordPressPostDataService _data;
    private readonly IWordPressPublisher _wordpress;
    private readonly IExternalWriteGuard? _writes;
    private readonly FeatureGateService? _gate;
    private readonly ILogger<WordPressPostRefreshService>? _log;

    public WordPressPostRefreshService(
        CommunityHubDbContext db,
        WordPressPostDataService data,
        IWordPressPublisher wordpress,
        IExternalWriteGuard? writes = null,
        FeatureGateService? gate = null,
        ILogger<WordPressPostRefreshService>? log = null)
    {
        _db = db;
        _data = data;
        _wordpress = wordpress;
        _writes = writes;
        _gate = gate;
        _log = log;
    }

    /// <summary>Render a template against the edition's current data. Side-effect-free (the Preview button).</summary>
    public async Task<WordPressPostRender> PreviewAsync(int eventId, string? template, CancellationToken ct = default) =>
        WordPressPostTokenRenderer.Render(template, await _data.LoadAsync(eventId, ct));

    /// <summary>The scheduled run: every flagged post that is due (not refreshed in the last 7 days).</summary>
    public Task<WordPressRefreshRunResult> RefreshFlaggedPostsAsync(
        int eventId, DateTimeOffset now, CancellationToken ct = default) =>
        RunAsync(eventId, now, onlyRowId: null, ct);

    /// <summary>"Refresh now" for one row — ignores the 7-day cadence, still requires the flag.</summary>
    public Task<WordPressRefreshRunResult> RefreshNowAsync(
        int eventId, int rowId, DateTimeOffset now, CancellationToken ct = default) =>
        RunAsync(eventId, now, onlyRowId: rowId, ct);

    private async Task<WordPressRefreshRunResult> RunAsync(
        int eventId, DateTimeOffset now, int? onlyRowId, CancellationToken ct)
    {
        var query = _db.WordPressRefreshPosts
            .Where(p => p.EventId == eventId && p.RefreshWeekly && p.WordPressPostId > 0 && p.BodyTemplate != "");
        if (onlyRowId is int id) query = query.Where(p => p.Id == id);
        var posts = await query.OrderBy(p => p.Id).ToListAsync(ct);

        if (posts.Count == 0)
            return new(0, 0, 0, 0, onlyRowId is null
                ? "No posts are flagged \"Refresh weekly\"."
                : "This post is not flagged \"Refresh weekly\" (or has no post id / template) — nothing sent.");

        var due = onlyRowId is not null
            ? posts
            : posts.Where(p => p.LastRefreshedAt is null || now - p.LastRefreshedAt.Value >= RefreshInterval).ToList();
        var notDue = posts.Count - due.Count;
        if (due.Count == 0)
            return new(0, 0, notDue, 0, $"{notDue} flagged post(s), none due yet (refreshed within 7 days).");

        // Decided BEFORE rendering or writing (the §1059 shape): a run that cannot write must not walk
        // every post and refuse each one.
        var blocked = await BlockedReasonAsync(eventId, ct);
        if (blocked is not null)
        {
            _log?.LogWarning("§1379.8 WordPress refresh skipped: {Reason}", blocked);
            return new(0, 0, notDue, 0, blocked);
        }

        var data = await _data.LoadAsync(eventId, ct);
        int updated = 0, unchanged = 0, failed = 0;

        foreach (var post in due)
        {
            ct.ThrowIfCancellationRequested();
            var render = WordPressPostTokenRenderer.Render(post.BodyTemplate, data);
            var hash = HashOf(render.Html);
            var unknownNote = render.UnknownTokens.Count > 0
                ? $" Unknown token(s) left as typed: {string.Join(", ", render.UnknownTokens)}."
                : string.Empty;

            if (string.Equals(hash, post.LastPushedHash, StringComparison.Ordinal))
            {
                unchanged++;
                post.LastRefreshedAt = now;
                post.LastRefreshResult = Trim($"{now:yyyy-MM-dd HH:mm} — unchanged since the last push, not re-sent.{unknownNote}");
                continue;
            }

            var result = await _wordpress.UpdatePostContentAsync(post.WordPressPostId, render.Html, ct);
            if (result.Created)
            {
                updated++;
                post.LastPushedHash = hash;
                post.LastRefreshedAt = now;
            }
            else
            {
                failed++;
            }
            post.LastRefreshResult = Trim($"{now:yyyy-MM-dd HH:mm} — {result.Message}{unknownNote}");
            _log?.LogInformation("§1379.8 WordPress post {PostId}: {Message}", post.WordPressPostId, result.Message);
        }

        await _db.SaveChangesAsync(ct);
        return new(updated, unchanged, notDue, failed,
            $"{updated} updated, {unchanged} unchanged, {failed} failed, {notDue} not due.");
    }

    /// <summary>Null when this host may write; else why not.</summary>
    private async Task<string?> BlockedReasonAsync(int eventId, CancellationToken ct)
    {
        if (_gate is not null && !await _gate.IsFeatureEnabledAsync(ContentStudioService.FeatureKey, eventId, ct))
            return "WordPress BlogPost Content is turned off for this event. Enable it in Settings first.";
        if (!_wordpress.CanWrite)
            return "WordPress connector is not configured — no post updated.";
        if (_writes is not null && !_writes.IsPermittedInThisEnvironment(ExternalSystems.WordPressPostRefresh))
            return "External writes to WordPress posts are blocked on this host "
                   + "(Integrations:ExternalWrites:WordPressPostRefresh) — no post updated.";
        return null;
    }

    internal static string HashOf(string html) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(html ?? string.Empty)));

    private static string Trim(string s) => s.Length <= 1000 ? s : s[..999] + "…";

    // ---- organizer edits (the page) ---------------------------------------------------------

    public Task<List<WordPressRefreshPost>> ListAsync(int eventId, CancellationToken ct = default) =>
        _db.WordPressRefreshPosts.AsNoTracking()
            .Where(p => p.EventId == eventId)
            .OrderBy(p => p.Label).ThenBy(p => p.WordPressPostId)
            .ToListAsync(ct);

    /// <summary>
    /// Link a WordPress post (new row, <paramref name="rowId"/> null) or edit a linked one. Returns an error
    /// message, or null on success. Editing the template clears the pushed hash so the next refresh sends it.
    /// </summary>
    public async Task<string?> SaveAsync(
        int eventId, int? rowId, long wordPressPostId, string? label, string? bodyTemplate, bool refreshWeekly,
        CancellationToken ct = default)
    {
        if (wordPressPostId <= 0) return "Enter the WordPress post id (the number in wp-admin's post.php?post=… link).";

        var clash = await _db.WordPressRefreshPosts.AnyAsync(
            p => p.EventId == eventId && p.WordPressPostId == wordPressPostId && p.Id != (rowId ?? 0), ct);
        if (clash) return $"WordPress post {wordPressPostId} is already linked.";

        WordPressRefreshPost? row;
        if (rowId is int id)
        {
            row = await _db.WordPressRefreshPosts.FirstOrDefaultAsync(p => p.Id == id && p.EventId == eventId, ct);
            if (row is null) return "That linked post no longer exists.";
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }
        else
        {
            row = new WordPressRefreshPost { EventId = eventId };
            _db.WordPressRefreshPosts.Add(row);
        }

        var template = bodyTemplate ?? string.Empty;
        if (!string.Equals(row.BodyTemplate, template, StringComparison.Ordinal) || row.WordPressPostId != wordPressPostId)
            row.LastPushedHash = null;

        row.WordPressPostId = wordPressPostId;
        row.Label = string.IsNullOrWhiteSpace(label) ? $"WordPress post {wordPressPostId}" : label.Trim();
        if (row.Label.Length > 300) row.Label = row.Label[..300];
        row.BodyTemplate = template;
        row.RefreshWeekly = refreshWeekly;
        await _db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>Unlink (CEH stops refreshing it). The WordPress post itself is untouched.</summary>
    public async Task<bool> DeleteAsync(int eventId, int rowId, CancellationToken ct = default)
    {
        var row = await _db.WordPressRefreshPosts.FirstOrDefaultAsync(p => p.Id == rowId && p.EventId == eventId, ct);
        if (row is null) return false;
        _db.WordPressRefreshPosts.Remove(row);
        await _db.SaveChangesAsync(ct);
        return true;
    }
}
