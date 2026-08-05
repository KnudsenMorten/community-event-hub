using System.Security.Claims;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Settings;
using CommunityHub.Forms.Steps;
using CommunityHub.Pages.Speaker;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// Speaker "Details" page. §195: there is now ONE Save button (OnPostSaveAsync) that
/// BOTH persists to SQL AND syncs to Zoho. Proves:
///   • the single Save persists to SQL AND pushes to Backstage/Zoho, and
///   • it only re-emails the organizers' manual-update alert when a Zoho-relevant field
///     actually changed (dedupe — it must not fire on an unchanged re-save), and
///   • the sync is fail-safe: if the Zoho writer throws, the SQL save is KEPT and a
///     non-fatal warning is surfaced (the save is never lost).
/// FAKE names only.
/// </summary>
public sealed class SpeakerDetailsSaveDedupeTests
{
    private const int EventId = 71;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"spkdetails-{Guid.NewGuid():N}")
            .Options);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-06-26T10:00:00Z");
    }

    /// <summary>Records every email so the test can count the manual-update alerts
    /// and inspect the operator-2026-07-24 field-changes mail body.</summary>
    private sealed class RecordingEmailSender : IEmailSender
    {
        public List<string> To { get; } = new();
        public List<(string To, string Subject, string Html)> Messages { get; } = new();
        private Task Rec(string to, string s, string h) { To.Add(to); Messages.Add((to, s, h)); return Task.CompletedTask; }
        public Task SendAsync(string to, string s, string h, CancellationToken ct = default) => Rec(to, s, h);
        public Task SendAsync(string to, string s, string h, IReadOnlyCollection<string>? cc, CancellationToken ct = default) => Rec(to, s, h);
        public Task SendAsync(string to, string s, string h, string t, CancellationToken ct = default) => Rec(to, s, h);
        public Task SendWithIcsAsync(string to, string s, string h, string ics, string f, CancellationToken ct = default) => Rec(to, s, h);
        public Task SendWithAttachmentsAsync(string to, string s, string h, IReadOnlyCollection<EmailAttachment> a, CancellationToken ct = default) => Rec(to, s, h);
    }

    /// <summary>A live Backstage writer that always reports the speaker already exists.</summary>
    private sealed class ExistsBlockedApi : IBackstageSpeakerBioApi
    {
        public bool CanWrite => true;
        public Task<BackstageSpeakerUpsertResult> UpsertSpeakerBioAsync(SpeakerBioRecord record, CancellationToken ct) =>
            Task.FromResult(new BackstageSpeakerUpsertResult(BackstageSpeakerAction.ExistsBlocked, "fake-id"));
    }

    /// <summary>A live Backstage writer that always throws — to prove the §195 fail-safe.</summary>
    private sealed class ThrowingApi : IBackstageSpeakerBioApi
    {
        public bool CanWrite => true;
        public Task<BackstageSpeakerUpsertResult> UpsertSpeakerBioAsync(SpeakerBioRecord record, CancellationToken ct) =>
            throw new InvalidOperationException("Zoho is down.");
    }

    private sealed class HttpContextAccessorOver(HttpContext ctx) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => ctx; set { } }
    }

    private static ClaimsPrincipal Session(Participant p) =>
        new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, p.Id.ToString()),
            new Claim(ClaimTypes.Email, p.Email),
            new Claim(ClaimTypes.Name, p.FullName),
            new Claim(ClaimTypes.Role, p.Role.ToString()),
            new Claim("EventId", p.EventId.ToString()),
        }, CookieAuthenticationDefaults.AuthenticationScheme));

    private static async Task<Participant> SeedSpeakerAsync(CommunityHubDbContext db)
    {
        db.Events.Add(new Event
        {
            Id = EventId, Code = "SD27", CommunityName = "C", DisplayName = "SD 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        });
        var p = new Participant
        {
            EventId = EventId, FullName = "Sven Speaker", Email = "sven@example.test",
            Role = ParticipantRole.Speaker, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();

        db.SpeakerProfiles.Add(new SpeakerProfile
        {
            EventId = EventId, ParticipantId = p.Id, Biography = "Old bio.",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return p;
    }

    private static async Task EnableSyncAsync(CommunityHubDbContext db)
    {
        var settings = new FeatureSettingsService(db, TimeProvider.System);
        await settings.SetEnabledAsync(EventId, "backstage-speaker-sync", true, "org@expertslive.dk");
        await settings.SetReleasedRingAsync(EventId, "backstage-speaker-sync", Ring.Broad, "org@expertslive.dk");
    }

    // REQUIREMENTS §148/§195: the page is a thin shell over SpeakerDetailsFormService (the inline
    // wizard step calls the SAME persist service). The single Save persists AND syncs on the PAGE,
    // so we drive the real page handler with the bio posted through a model-binding request.
    private static (DetailsModel model, RecordingEmailSender email) NewModel(
        CommunityHubDbContext db, Participant p, string biography,
        IBackstageSpeakerBioApi? api = null)
    {
        // §306: accreditation + country + gender are MANDATORY — every save must carry them.
        var http = WizardBindingHarness.PostContext(Session(p),
            new Dictionary<string, string?>
            {
                ["Biography"] = biography,
                ["Country"] = "DK",
                ["Gender"] = "Male",
                ["SelectedAccreditations"] = "Microsoft MVP",
            });
        var accessor = new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http));
        var email = new RecordingEmailSender();
        var sync = new SpeakerBioBackstageSyncService(
            db, api ?? new ExistsBlockedApi(),
            Options.Create(new BackstageSpeakerBioSyncOptions { Enabled = true }),
            email, new FeatureGateService(db), new RingResolver(db));
        // Wire the operator-2026-07-24 field-changes notifier over the SAME recorder —
        // it only mails when the speaker already has a BackstageSpeakerId.
        var alerts = new EngineAlertSender(
            email, new EmailContextAccessor(), TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<EngineAlertSender>.Instance);
        var form = new SpeakerDetailsFormService(db, new FixedClock(), new ZohoChangeNotifier(alerts));
        var model = new DetailsModel(accessor, form, sync).Bind(http);
        return (model, email);
    }

    [Fact]
    public async Task Save_persists_to_sql_and_syncs_to_zoho()
    {
        using var db = NewDb();
        var p = await SeedSpeakerAsync(db);
        await EnableSyncAsync(db);

        // §195: the single Save BOTH persists to SQL AND syncs. A real bio change → the
        // organizers' manual-update alert fires once (the ExistsBlocked sync path).
        var (model, email) = NewModel(db, p, "A freshly edited bio.");

        var result = await model.OnPostSaveAsync(default);

        Assert.IsType<PageResult>(result);
        var saved = await db.SpeakerProfiles.SingleAsync();
        Assert.Equal("A freshly edited bio.", saved.Biography);   // persisted to SQL
        Assert.Single(email.To);                                  // synced → organizers alerted once
        Assert.Equal(SpeakerBioBackstageSyncService.AlertEmail, email.To[0]);
    }

    [Fact]
    public async Task Save_emails_once_then_dedupes_unchanged_resave()
    {
        using var db = NewDb();
        var p = await SeedSpeakerAsync(db);
        await EnableSyncAsync(db);

        // 1) Save WITH a real bio change → organizers are emailed once.
        var (m1, email) = NewModel(db, p, "Brand new bio for sync.");
        await m1.OnPostSaveAsync(default);
        Assert.Single(email.To);
        Assert.Equal(SpeakerBioBackstageSyncService.AlertEmail, email.To[0]);

        // 2) Save again with NO change (same persisted bio) → no new email.
        var (m2, email2) = NewModel(db, p, "Brand new bio for sync.");   // identical to what is now stored
        await m2.OnPostSaveAsync(default);
        Assert.Empty(email2.To);   // dedupe: organizers NOT re-notified on an unchanged save
    }

    [Fact]
    public async Task Save_is_failsafe_keeps_sql_save_when_zoho_throws()
    {
        using var db = NewDb();
        var p = await SeedSpeakerAsync(db);
        await EnableSyncAsync(db);

        // §195 fail-safe: the Zoho writer throws, but the SQL save must still be kept and a
        // non-fatal warning surfaced (IsError) rather than the save being lost.
        var (model, _) = NewModel(db, p, "Bio that survives a Zoho outage.", new ThrowingApi());

        var result = await model.OnPostSaveAsync(default);

        Assert.IsType<PageResult>(result);
        var saved = await db.SpeakerProfiles.SingleAsync();
        Assert.Equal("Bio that survives a Zoho outage.", saved.Biography);   // SQL save kept
        Assert.True(model.IsError);                                          // non-fatal warning surfaced
    }

    [Fact]
    public async Task Save_requires_accreditation_country_and_gender()
    {
        // §306 (operator 2026-07-24): "the 3 fields should be mandatory, so something is
        // chosen for all 3" — a save missing Microsoft accreditation / Country / Gender
        // is rejected in the SHARED persist path (wizard + standalone page alike) and
        // nothing is written.
        using var db = NewDb();
        var p = await SeedSpeakerAsync(db);
        await EnableSyncAsync(db);

        var http = WizardBindingHarness.PostContext(Session(p),
            new Dictionary<string, string?> { ["Biography"] = "Only a bio, nothing else." });
        var accessor = new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http));
        var email = new RecordingEmailSender();
        var sync = new SpeakerBioBackstageSyncService(
            db, new ExistsBlockedApi(),
            Options.Create(new BackstageSpeakerBioSyncOptions { Enabled = true }),
            email, new FeatureGateService(db), new RingResolver(db));
        var model = new DetailsModel(accessor,
            new SpeakerDetailsFormService(db, new FixedClock()), sync).Bind(http);

        await model.OnPostSaveAsync(default);

        var saved = await db.SpeakerProfiles.SingleAsync();
        Assert.Equal("Old bio.", saved.Biography);   // NOT persisted — validation blocked it
        Assert.Empty(email.To);                      // and no sync/alert fired
        Assert.False(model.ModelState.IsValid);
        Assert.True(model.ModelState.ContainsKey("SelectedAccreditations"));
        Assert.True(model.ModelState.ContainsKey("Country"));
        Assert.True(model.ModelState.ContainsKey("Gender"));
    }

    [Fact]
    public async Task Save_mails_the_field_changes_to_ops_when_speaker_already_in_backstage()
    {
        // Operator 2026-07-24: a speaker who ALREADY exists in Backstage edits their
        // bio/profile → info@expertslive.dk gets ONE mail WITH the concrete changes
        // (create-only API ⇒ the operator applies them manually). The old generic
        // "needs a manual update" alert is suppressed for that save (no double mail).
        using var db = NewDb();
        var p = await SeedSpeakerAsync(db);
        var profile = await db.SpeakerProfiles.SingleAsync();
        profile.BackstageSpeakerId = "bs-77";
        await db.SaveChangesAsync();
        await EnableSyncAsync(db);

        var (model, email) = NewModel(db, p, "Changed bio for the ops mail.");
        await model.OnPostSaveAsync(default);

        var m = Assert.Single(email.Messages);            // exactly ONE mail, not two
        // §736 (operator 2026-07-31: "only alert mails goes to mok@expertslive.dk") — a
        // publish/delete notice is a JOB anyone can pick up, not an alert, so it goes to the shared
        // ops inbox. 🔑 The comment at the top of this test already said "info@expertslive.dk gets
        // ONE mail" — that was the 2026-07-24 intent; §493 later moved the CONSTANT to mok@ and left
        // the comment stale. Assertion and comment agree again.
        Assert.Equal(ZohoChangeNotifier.ActionableRecipient, m.To);
        Assert.Contains("ACTION NEEDED", m.Html);
        Assert.Contains("bs-77", m.Html);                 // the existing Backstage speaker id
        // §302: the mail speaks in ZOHO GUI field names — the bio is "Description" in
        // the Backstage Edit Speaker panel, never the CEH name.
        Assert.Contains("Description", m.Html);
        Assert.DoesNotContain("Biography", m.Html);
        Assert.Contains("Old bio.", m.Html);              // ...its old value...
        Assert.Contains("Changed bio for the ops mail.", m.Html);   // ...and the new value
    }
}
