using CommunityHub.Core.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §444 (operator 2026-07-27) — an INACTIVE participant must be TOLD, not walked into the
/// 6-digit code box.
///
/// <para>Sign-in requires <c>IsActive</c> AND <c>LifecycleState == Active</c>. Without this, an
/// inactive person fell through to the neutral <i>"If that email is registered, a code has been
/// sent"</i>, was shown the code field, and waited for a mail that is never generated — the
/// operator's exact report.</para>
///
/// <para>The load-bearing part is the SCOPE: the specific message must fire ONLY for an address
/// the hub actually holds. If it ever fired for an unknown address it would turn the endpoint into
/// an account-enumeration oracle, which is precisely what the neutral reply exists to prevent.
/// These tests pin both halves — told when known, neutral when not — and that no mail is sent
/// either way.</para>
/// </summary>
public sealed class InactiveAccountSignInMessageTests
{
    private sealed class CountingSender : IEmailSender
    {
        public int Sends;
        public Task SendAsync(string to, string s, string h, CancellationToken ct = default)
        { Sends++; return Task.CompletedTask; }
        public Task SendAsync(string to, string s, string h, IReadOnlyCollection<string>? cc, CancellationToken ct = default)
            => SendAsync(to, s, h, ct);
        public Task SendAsync(string to, string s, string h, string t, CancellationToken ct = default)
            => SendAsync(to, s, h, ct);
        public Task SendWithIcsAsync(string to, string s, string h, string ics, string f, CancellationToken ct = default)
            => SendAsync(to, s, h, ct);
        public Task SendWithAttachmentsAsync(string to, string s, string h, IReadOnlyCollection<EmailAttachment> a, CancellationToken ct = default)
            => SendAsync(to, s, h, ct);
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"inactive-signin-{Guid.NewGuid():N}").Options);

    private static async Task<int> SeedEventAsync(CommunityHubDbContext db)
    {
        var e = new Event
        {
            Code = "T27", CommunityName = "C", DisplayName = "T 2027", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        };
        db.Events.Add(e);
        await db.SaveChangesAsync();
        return e.Id;
    }

    private static async Task AddParticipantAsync(
        CommunityHubDbContext db, int eventId, string email,
        bool isActive, ParticipantLifecycleState lifecycle)
    {
        db.Participants.Add(new Participant
        {
            EventId = eventId, Email = email, FullName = "Test Person",
            Role = ParticipantRole.Speaker, IsActive = isActive, LifecycleState = lifecycle,
        });
        await db.SaveChangesAsync();
    }

    private static PinLoginService NewService(CommunityHubDbContext db, IEmailSender sender) =>
        new(db, new PinService(), sender, TimeProvider.System);

    /// <summary>Deactivated (the cancellation switch) — the operator's screenshot case.</summary>
    [Fact]
    public async Task A_deactivated_participant_is_told_instead_of_being_shown_the_code_box()
    {
        using var db = NewDb();
        var ev = await SeedEventAsync(db);
        await AddParticipantAsync(db, ev, "gone@example.test", false, ParticipantLifecycleState.Active);
        var sender = new CountingSender();

        var result = await NewService(db, sender).RequestPinAsync(ev, "gone@example.test");

        Assert.False(result.Accepted);
        Assert.Contains("not active", result.Message);
        Assert.DoesNotContain("a code has been sent", result.Message);
        Assert.Equal(0, sender.Sends);           // and no PIN mail was generated
    }

    /// <summary>Still in the onboarding queue — cannot sign in for a different reason, same dead end.</summary>
    [Fact]
    public async Task A_not_yet_activated_participant_is_told_too()
    {
        using var db = NewDb();
        var ev = await SeedEventAsync(db);
        await AddParticipantAsync(db, ev, "queued@example.test", true, ParticipantLifecycleState.Inactive);
        var sender = new CountingSender();

        var result = await NewService(db, sender).RequestPinAsync(ev, "queued@example.test");

        Assert.False(result.Accepted);
        Assert.Contains("not active", result.Message);
        Assert.Equal(0, sender.Sends);
    }

    /// <summary>
    /// THE GUARD. An address the hub does not hold must still get the neutral answer — otherwise
    /// the endpoint tells a stranger which addresses are registered.
    /// </summary>
    [Fact]
    public async Task An_unknown_address_still_gets_the_neutral_non_enumerating_reply()
    {
        using var db = NewDb();
        var ev = await SeedEventAsync(db);
        await AddParticipantAsync(db, ev, "gone@example.test", false, ParticipantLifecycleState.Active);
        var sender = new CountingSender();

        var result = await NewService(db, sender).RequestPinAsync(ev, "stranger@example.test");

        Assert.True(result.Accepted);
        Assert.Contains("If that email is registered", result.Message);
        Assert.DoesNotContain("not active", result.Message);
        Assert.Equal(0, sender.Sends);
    }

    /// <summary>
    /// An address from ANOTHER edition must not leak either — the check is edition-scoped, the
    /// same way the sign-in lookup is.
    /// </summary>
    [Fact]
    public async Task An_inactive_participant_of_another_edition_gets_the_neutral_reply()
    {
        using var db = NewDb();
        var ev = await SeedEventAsync(db);
        var other = new Event
        {
            Code = "T26", CommunityName = "C", DisplayName = "T 2026", IsActive = false,
            StartDate = new DateOnly(2026, 2, 9), EndDate = new DateOnly(2026, 2, 10),
        };
        db.Events.Add(other);
        await db.SaveChangesAsync();
        await AddParticipantAsync(db, other.Id, "olduser@example.test", false, ParticipantLifecycleState.Active);
        var sender = new CountingSender();

        var result = await NewService(db, sender).RequestPinAsync(ev, "olduser@example.test");

        Assert.True(result.Accepted);
        Assert.Contains("If that email is registered", result.Message);
    }

    /// <summary>An ACTIVE participant is unaffected — a PIN is generated and mailed as before.</summary>
    [Fact]
    public async Task An_active_participant_still_gets_a_code()
    {
        using var db = NewDb();
        var ev = await SeedEventAsync(db);
        await AddParticipantAsync(db, ev, "live@example.test", true, ParticipantLifecycleState.Active);
        var sender = new CountingSender();

        var result = await NewService(db, sender).RequestPinAsync(ev, "live@example.test");

        Assert.True(result.Accepted);
        Assert.Contains("If that email is registered", result.Message);
        Assert.Equal(1, await db.LoginPins.CountAsync());
    }
}
