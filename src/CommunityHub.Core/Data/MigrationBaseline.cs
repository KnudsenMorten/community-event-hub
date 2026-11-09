using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Data;

/// <summary>
/// §1389 — the EF migrations were SQUASHED into one baseline on 2026-11-08 (operator: <i>"Yes, at a quiet time"</i> · <i>"approved
/// to start"</i>): the 299 old migrations, with 2.0 M lines of Designer snapshots, made every test and deploy build compile
/// ~190 s of migration history. The baseline creates today's schema from scratch.
/// </summary>
/// <remarks>
/// <para>🔑 <b>An existing database already HAS that schema</b> — it ran the old migrations. So before <c>Migrate()</c>, a
/// database whose history contains the LAST old migration gets one history row saying the baseline is applied, and then
/// migrates as usual. Nothing else is written; no table or row of data changes.</para>
/// <para>🔒 <b>A database that is BEHIND</b> (some old migrations, but not the last) cannot be adopted — its schema is not
/// the baseline's. Startup refuses with a clear message instead of guessing: deploy the last pre-squash build
/// (<c>4d42f679</c>; tag <c>public-v1.0.0</c> on the public edition) to it first.</para>
/// </remarks>
public static class MigrationBaseline
{
    /// <summary>The last migration that existed before the squash (applied in DEV and PROD on 2026-11-08).</summary>
    public const string LastSquashedMigration = "20261108200000_MailCampaignReleasePlan";

    /// <summary>The baseline migration's id — must equal its <c>[Migration]</c> attribute (a test checks).</summary>
    public const string BaselineMigration = "20261108210000_Baseline";

    public enum Action { Nothing, Adopt, Behind }

    /// <summary>The pure decision, from the database's migration history (null = no history table yet).</summary>
    public static Action Decide(IReadOnlyCollection<string>? history)
    {
        if (history is null || history.Count == 0) return Action.Nothing;          // empty database: the baseline builds it
        if (history.Contains(BaselineMigration)) return Action.Nothing;            // already adopted
        if (history.Contains(LastSquashedMigration)) return Action.Adopt;          // a fully migrated pre-squash database
        return Action.Behind;                                                      // partially migrated: refuse
    }

    /// <summary>Apply the decision on a SQL Server database. Call right before <c>Migrate()</c>.</summary>
    public static void AdoptIfNeeded(CommunityHubDbContext db, ILogger log)
    {
        if (!db.Database.IsSqlServer()) return;

        var exists = db.Database.SqlQueryRaw<int>(
            "SELECT CASE WHEN OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL THEN 0 ELSE 1 END AS [Value]").AsEnumerable().First() == 1;
        var history = exists
            ? db.Database.SqlQueryRaw<string>("SELECT [MigrationId] AS [Value] FROM [__EFMigrationsHistory]").ToList()
            : null;

        switch (Decide(history))
        {
            case Action.Adopt:
                var version = typeof(DbContext).Assembly.GetName().Version?.ToString(3) ?? "10.0.0";
                db.Database.ExecuteSqlRaw(
                    "IF NOT EXISTS (SELECT 1 FROM [__EFMigrationsHistory] WHERE [MigrationId] = {0}) "
                    + "INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion]) VALUES ({0}, {1})",
                    BaselineMigration, version);
                log.LogWarning("§1389 migration baseline adopted: {Baseline} recorded as applied (the database already had {Last}). No schema change.",
                    BaselineMigration, LastSquashedMigration);
                break;
            case Action.Behind:
                throw new InvalidOperationException(
                    $"§1389: this database's migration history stops before {LastSquashedMigration}, so it cannot adopt the baseline "
                    + $"{BaselineMigration}. Deploy the last pre-squash build to it first, then this one: public edition tag "
                    + "public-v1.0.0 (upstream private commit 4d42f679). See docs/FORKING.md, 'Upgrading to public-v2.0.0'.");
        }
    }
}
