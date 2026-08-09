using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// 🔴 §1024 — the Sessionize→CEH deviation mail reports a given disagreement ONCE.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-09: *"lets leave it for now, as it is a good reminder to validate again,
/// but only 1 time mail"*. He kept §999's mail — it is a useful prompt to go and check — but a
/// standing disagreement that CEH is deliberately winning is not news on every import pass.</para>
///
/// <para>🔑 <b>Deliberately NOT §1002's hourly re-send.</b> That one chases a WRONG PUBLIC AGENDA
/// somebody must go and fix. This one describes a state that is CORRECT by design — CEH owns the
/// schedule, Sessionize is simply out of date — so once is right.</para>
/// </remarks>
public sealed class SessionizeDeviationOnceTests
{
    private const int EventId = 1;

    private static async Task<Session> SeedAsync(CommunityHubDbContext db, string room)
    {
        db.Events.Add(new Event
        {
            Id = EventId, CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 10), EndDate = new DateOnly(2027, 2, 10),
        });
        var s = new Session { EventId = EventId, SessionizeId = "sz-1", Title = "Talk A", Room = room };
        db.Sessions.Add(s);
        await db.SaveChangesAsync();
        return s;
    }

    private static SessionizeSession Src(string room) =>
        new("sz-1", "Talk A", "About", room, null, null, null, false, Array.Empty<string>(), null, null, null, null);

    private static SessionImportService NewService(CommunityHubDbContext db) =>
        new(db, ScenarioFixture.Clock);

    private static Task<SessionImportResult> RunAsync(SessionImportService svc, SessionizeSession src) =>
        svc.ImportSessionsAsync(EventId, new[] { src }, Array.Empty<SessionizeSpeaker>(), Array.Empty<string>());

    [Fact]
    public async Task The_same_disagreement_is_reported_once_and_then_stays_quiet()
    {
        using var db = ScenarioFixture.NewDb();
        await SeedAsync(db, "Hall A");
        var svc = NewService(db);

        var first = await RunAsync(svc, Src("Hall B"));
        Assert.Single(first.DeviationsOrEmpty);          // told once…

        var second = await RunAsync(svc, Src("Hall B"));
        Assert.Empty(second.DeviationsOrEmpty);          // …and not again
    }

    [Fact]
    public async Task A_CHANGED_disagreement_is_reported_again()
    {
        // The point of the mail is "go and judge this". A different value is a different judgement.
        using var db = ScenarioFixture.NewDb();
        await SeedAsync(db, "Hall A");
        var svc = NewService(db);

        Assert.Single((await RunAsync(svc, Src("Hall B"))).DeviationsOrEmpty);
        Assert.Empty((await RunAsync(svc, Src("Hall B"))).DeviationsOrEmpty);
        Assert.Single((await RunAsync(svc, Src("Hall C"))).DeviationsOrEmpty);
    }

    /// <summary>
    /// 🔒 A disagreement that goes away CLEARS its stamp, so the same one recurring later is mailed
    /// again rather than swallowed by a stale hash — the §302 self-heal, which is what stops "we
    /// already told you once" from becoming "we will never tell you again".
    /// </summary>
    [Fact]
    public async Task A_resolved_disagreement_is_forgotten_and_can_be_reported_afresh()
    {
        using var db = ScenarioFixture.NewDb();
        await SeedAsync(db, "Hall A");
        var svc = NewService(db);

        Assert.Single((await RunAsync(svc, Src("Hall B"))).DeviationsOrEmpty);
        Assert.Empty((await RunAsync(svc, Src("Hall A"))).DeviationsOrEmpty);   // agrees again
        Assert.Null((await db.Sessions.SingleAsync()).SessionizeDeviationNotifiedHash);
        Assert.Single((await RunAsync(svc, Src("Hall B"))).DeviationsOrEmpty);  // recurs ⇒ told again
    }
}
