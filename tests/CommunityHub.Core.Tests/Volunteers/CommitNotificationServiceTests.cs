using System.Net.Mail;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Settings;
using CommunityHub.Core.Volunteers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests.Volunteers;

/// <summary>
/// §150 commit notifications: <see cref="CommitNotificationService"/> is the SOLE
/// assignment-email emitter. Proves:
///   • exactly ONE batched summary email per affected person, listing their FINAL
///     committed assignment set (duplicate ids collapse to one mail);
///   • the per-person <see cref="EmailContext"/> carries the committing queue's
///     FeatureKey + EventId + ParticipantId so the ring gate / kill switch apply;
///   • the committing actor is never mailed about their own action;
///   • via the REAL ring-gated <see cref="BrevoEmailSender"/>, an OUT-OF-RING person
///     is dropped by the FeatureKey gate while the in-ring person still sends;
///   • the global kill switch hard-stops every send.
/// </summary>
public sealed class CommitNotificationServiceTests
{
    private const int EventId = 11;
    // Deliberately OUTSIDE the in-memory auto-id range (which starts at 1) so the
    // actor id never accidentally collides with a seeded volunteer's generated id.
    private const int OrganizerId = 9999;
    private const string QueueFeatureKey = VolunteerAllocationService.FeatureKey; // "volunteer-allocation"

    private static VolunteerStructureService.ActorContext Organizer() =>
        new(OrganizerId, "org@example.test", ParticipantRole.Organizer, EventId);

    private static CommunityHubDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase(name).Options);

    // ---- a plain recording double (no gating) for the batching/context asserts ----

    private sealed class RecordingSender : IEmailSender
    {
        private readonly IEmailContextAccessor _ctx;
        public RecordingSender(IEmailContextAccessor ctx) => _ctx = ctx;

        public readonly List<(string To, string Subject, string Html, EmailContext? Ctx)> Sent = new();

        public Task SendAsync(string to, string s, string h, CancellationToken ct = default)
        {
            Sent.Add((to, s, h, _ctx.Current));
            return Task.CompletedTask;
        }
        public Task SendAsync(string to, string s, string h, IReadOnlyCollection<string>? cc, CancellationToken ct = default)
            => SendAsync(to, s, h, ct);
        public Task SendAsync(string to, string s, string h, string t, CancellationToken ct = default)
            => SendAsync(to, s, h, ct);
        public Task SendWithIcsAsync(string to, string s, string h, string ics, string fn, CancellationToken ct = default)
            => SendAsync(to, s, h, ct);
        public Task SendWithAttachmentsAsync(string to, string s, string h, IReadOnlyCollection<EmailAttachment> a, CancellationToken ct = default)
            => SendAsync(to, s, h, ct);
    }

    private static int AddVolunteer(CommunityHubDbContext db, string email, Ring ring = Ring.Broad)
    {
        var p = new Participant
        {
            EventId = EventId, Email = email.ToLowerInvariant(), FullName = email,
            Role = ParticipantRole.Volunteer, IsActive = true, Ring = ring,
        };
        db.Participants.Add(p);
        db.SaveChanges();
        return p.Id;
    }

    private static int AddTask(CommunityHubDbContext db, string title)
    {
        var t = new VolunteerTask { EventId = EventId, Title = title, ResourcesNeeded = 1 };
        db.VolunteerTasks.Add(t);
        db.SaveChanges();
        return t.Id;
    }

    private static void Assign(CommunityHubDbContext db, int taskId, int volunteerId)
    {
        db.VolunteerTaskAssignments.Add(new VolunteerTaskAssignment
        {
            EventId = EventId, TaskId = taskId, ParticipantId = volunteerId,
            AssignedByEmail = "org@example.test",
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task Sends_one_batched_summary_per_affected_person_with_their_final_assignments()
    {
        using var db = NewDb($"commit-notify-{Guid.NewGuid():N}");
        db.Events.Add(new Event { Id = EventId, CommunityName = "C", DisplayName = "C27", Code = "C27", IsActive = true });
        db.SaveChanges();

        var v1 = AddVolunteer(db, "v1@x.test");
        var v2 = AddVolunteer(db, "v2@x.test");
        var registration = AddTask(db, "Registration desk");
        var wardrobe = AddTask(db, "Wardrobe");
        var av = AddTask(db, "AV support");

        // v1 ends the commit with TWO assignments, v2 with one.
        Assign(db, registration, v1);
        Assign(db, wardrobe, v1);
        Assign(db, av, v2);

        var ctx = new EmailContextAccessor();
        var sender = new RecordingSender(ctx);
        var svc = new CommitNotificationService(db, sender, ctx);

        // Duplicate v1 id must collapse to a single mail.
        await svc.NotifyCommittedAsync(Organizer(), EventId, new[] { v1, v1, v2 }, QueueFeatureKey);

        Assert.Equal(2, sender.Sent.Count);

        var toV1 = Assert.Single(sender.Sent, m => m.To == "v1@x.test");
        Assert.Contains("Registration desk", toV1.Html);
        Assert.Contains("Wardrobe", toV1.Html);
        Assert.DoesNotContain("AV support", toV1.Html);

        var toV2 = Assert.Single(sender.Sent, m => m.To == "v2@x.test");
        Assert.Contains("AV support", toV2.Html);
        Assert.DoesNotContain("Registration desk", toV2.Html);

        // Every send carried the queue FeatureKey + edition + the recipient's id so the
        // sender ring gate can scope it.
        foreach (var m in sender.Sent)
        {
            Assert.NotNull(m.Ctx);
            Assert.Equal(QueueFeatureKey, m.Ctx!.FeatureKey);
            Assert.Equal(EventId, m.Ctx.EventId);
            Assert.False(m.Ctx.RingExempt); // commit mail is ring-governed, never exempt
        }
        Assert.Contains(sender.Sent, m => m.Ctx!.ParticipantId == v1);
        Assert.Contains(sender.Sent, m => m.Ctx!.ParticipantId == v2);
    }

    [Fact]
    public async Task Never_mails_the_committing_actor_about_their_own_action()
    {
        using var db = NewDb($"commit-notify-{Guid.NewGuid():N}");
        db.Events.Add(new Event { Id = EventId, CommunityName = "C", DisplayName = "C27", Code = "C27", IsActive = true });
        db.SaveChanges();

        // The actor (organizer) happens to also hold an assignment; they still must not
        // receive a summary of the commit they themselves performed.
        var actorPid = OrganizerId;
        db.Participants.Add(new Participant
        {
            Id = actorPid, EventId = EventId, Email = "org@example.test", FullName = "Org",
            Role = ParticipantRole.Organizer, IsActive = true, Ring = Ring.Broad,
        });
        db.SaveChanges();
        var t = AddTask(db, "Setup");
        Assign(db, t, actorPid);

        var ctx = new EmailContextAccessor();
        var sender = new RecordingSender(ctx);
        var svc = new CommitNotificationService(db, sender, ctx);

        var issued = await svc.NotifyCommittedAsync(Organizer(), EventId, new[] { actorPid }, QueueFeatureKey);

        Assert.Equal(0, issued);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task Empty_or_no_recipients_sends_nothing()
    {
        using var db = NewDb($"commit-notify-{Guid.NewGuid():N}");
        var ctx = new EmailContextAccessor();
        var sender = new RecordingSender(ctx);
        var svc = new CommitNotificationService(db, sender, ctx);

        Assert.Equal(0, await svc.NotifyCommittedAsync(Organizer(), EventId, Array.Empty<int>(), QueueFeatureKey));
        Assert.Empty(sender.Sent);
    }

    // ---- the REAL ring-gated sender: FeatureKey drives the drop -------------------

    [Fact]
    public async Task Out_of_ring_person_is_dropped_by_the_feature_key_ring_gate()
    {
        using var provider = BuildRingProvider();

        // Transport (outbound-email) wide open at Broad; the QUEUE feature released only
        // to Ring1 — so ONLY the EmailContext.FeatureKey tightening can drop a Broad
        // recipient. Seed everything THROUGH the provider so the sender's own scope sees
        // the same in-memory store.
        SetRing(provider, FeatureCatalog.OutboundEmailKey, Ring.Broad);
        SetRing(provider, QueueFeatureKey, Ring.Ring1);

        int inRing, outRing;
        using (var seedScope = provider.CreateScope())
        {
            var seed = seedScope.ServiceProvider.GetRequiredService<CommunityHubDbContext>();
            seed.Events.Add(new Event { Id = EventId, CommunityName = "C", DisplayName = "C27", Code = "C27", IsActive = true });
            seed.SaveChanges();
            inRing = AddVolunteer(seed, "inring@x.test", Ring.Ring1);
            outRing = AddVolunteer(seed, "outring@x.test", Ring.Broad);
            Assign(seed, AddTask(seed, "In-ring task"), inRing);
            Assign(seed, AddTask(seed, "Out-of-ring task"), outRing);
        }

        var ctx = new EmailContextAccessor();
        var sender = new CapturingBrevoSender(
            Options.Create(new EmailOptions { SmtpHost = "smtp.invalid.localhost" }),
            provider.GetRequiredService<IServiceScopeFactory>(), ctx);

        using var svcScope = provider.CreateScope();
        var db = svcScope.ServiceProvider.GetRequiredService<CommunityHubDbContext>();
        var svc = new CommitNotificationService(db, sender, ctx);

        await svc.NotifyCommittedAsync(Organizer(), EventId, new[] { inRing, outRing }, QueueFeatureKey);

        // The Broad recipient is ring-dropped inside the sender; only the Ring1 one sends.
        var msg = Assert.Single(sender.Captured);
        Assert.Equal("inring@x.test", msg.To[0].Address);
    }

    [Fact]
    public async Task Global_kill_switch_hard_stops_every_send()
    {
        using var provider = BuildRingProvider();
        SetRing(provider, FeatureCatalog.OutboundEmailKey, Ring.Broad);
        SetRing(provider, QueueFeatureKey, Ring.Broad);

        int v;
        using (var seedScope = provider.CreateScope())
        {
            var seed = seedScope.ServiceProvider.GetRequiredService<CommunityHubDbContext>();
            seed.Events.Add(new Event { Id = EventId, CommunityName = "C", DisplayName = "C27", Code = "C27", IsActive = true });
            seed.SaveChanges();
            v = AddVolunteer(seed, "inring@x.test", Ring.Ring0); // fully in scope
            Assign(seed, AddTask(seed, "Task"), v);
        }

        var ctx = new EmailContextAccessor();
        var sender = new CapturingBrevoSender(
            Options.Create(new EmailOptions { SmtpHost = "smtp.invalid.localhost", KillSwitch = true }),
            provider.GetRequiredService<IServiceScopeFactory>(), ctx);

        using var svcScope = provider.CreateScope();
        var db = svcScope.ServiceProvider.GetRequiredService<CommunityHubDbContext>();
        var svc = new CommitNotificationService(db, sender, ctx);

        await svc.NotifyCommittedAsync(Organizer(), EventId, new[] { v }, QueueFeatureKey);

        Assert.Empty(sender.Captured);
    }

    // ---- ring-gate harness wiring ------------------------------------------------

    // One provider so the seed scope, the sender's per-send scope and the service's
    // scope all share ONE in-memory store (EF in-memory only shares a store across
    // contexts built from the SAME internal service provider).
    private static ServiceProvider BuildRingProvider()
    {
        // ONE shared in-memory root so the seed scope, the service's scope and the
        // sender's per-send scope all see the SAME store (EF in-memory only shares a
        // store across contexts that share an InMemoryDatabaseRoot).
        var root = new Microsoft.EntityFrameworkCore.Storage.InMemoryDatabaseRoot();
        var name = $"commit-notify-rings-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddDbContext<CommunityHubDbContext>(
            o => o.UseInMemoryDatabase(name, root));
        services.AddScoped<RingResolver>();
        services.AddScoped<FeatureGateService>();
        return services.BuildServiceProvider();
    }

    private static void SetRing(ServiceProvider provider, string featureKey, Ring ring)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommunityHubDbContext>();
        db.FeatureSettings.Add(new FeatureSetting
        {
            EventId = EventId, FeatureKey = featureKey, Enabled = true, ReleasedToRingOverride = ring,
        });
        db.SaveChanges();
    }

    // The production ring gate, with the SMTP dispatch tail replaced by a recorder so
    // the real ShouldRingDrop / kill-switch logic runs without touching the network.
    private sealed class CapturingBrevoSender : BrevoEmailSender
    {
        private readonly List<MailMessage> _captured = new();
        public IReadOnlyList<MailMessage> Captured => _captured;
        public CapturingBrevoSender(IOptions<EmailOptions> o, IServiceScopeFactory s, IEmailContextAccessor e)
            : base(o, s, e, null) { }
        protected override Task DispatchAsync(MailMessage message, CancellationToken ct)
        {
            var snap = new MailMessage();
            foreach (var a in message.To) snap.To.Add(a);
            _captured.Add(snap);
            return Task.CompletedTask;
        }
    }
}
