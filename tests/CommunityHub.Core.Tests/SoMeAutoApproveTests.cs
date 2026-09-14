using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §918/§1030 — AUTO-APPROVAL, AND THE THREE GUARDS IT MUST NOT BREAK.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-06: <i>"as they run from the templates, i see no reason why we should not
/// auto-approve them, concerns?"</i> — with 79 posts held and 1 approved, clicking each was not a
/// workflow. The concerns were about HOW, and each is a test here.</para>
///
/// <para>🔴 <b>§1030 (2026-08-10) INVERTED guard 1, and these tests were rewritten with it.</b> They
/// are not stale tests that were deleted — each one described the OLD comparison correctly, so each
/// was re-pointed at the new window. The rule is now: approve only when
/// <c>now &lt; ScheduledAtUtc &lt;= now + leadDays</c> — <i>"become auto-approved when it reaches the
/// time"</i>. The old code approved everything MORE than the lead away, which had auto-approved 29
/// posts 24–169 days out while the imminent ones waited for a click.</para>
///
/// <para>🔒 The past-dated half of the guard survived the inversion and is not optional: §889.1 saw
/// him approve #495 whose 09:00 slot had already passed, and it published <b>thirty seconds
/// later</b>. Approving an overdue post is indistinguishable from publishing it.</para>
///
/// <para>⚠️ Every fixture below is scheduled <b>inside</b> the window on purpose. Under the old rule
/// they sat 30 days out; after the inversion that is "not yet due", which would have made the
/// switched-off / edited / Type-5 / published / blocked tests pass for the wrong reason — green
/// without ever reaching the guard they name.</para>
/// </remarks>
public sealed class SoMeAutoApproveTests
{
    private const int EventId = 1;
    private static readonly DateTimeOffset Now = new(2026, 8, 6, 9, 0, 0, TimeSpan.Zero);

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"autoapprove-{Guid.NewGuid():N}").Options);

    private static SoMeAutoApproveService NewService(
        CommunityHubDbContext db, CommunityHub.Core.Email.IEmailSender? email = null) =>
        new(db, new SoMeApprovalGate(db), new FixedClock(Now), log: null, email: email);

    /// <summary>Records what was sent, and can be told to fail — the §1060(i) notice must not be
    /// able to undo an approval that already happened.</summary>
    private sealed class RecordingEmail(bool throws = false) : CommunityHub.Core.Email.IEmailSender
    {
        public List<(string To, string Subject, string Body)> Sent { get; } = [];

        public Task SendAsync(string to, string s, string h, CancellationToken ct = default)
        {
            if (throws) throw new InvalidOperationException("smtp down");
            Sent.Add((to, s, h));
            return Task.CompletedTask;
        }

        public Task SendAsync(string to, string s, string h, IReadOnlyCollection<string>? cc, CancellationToken ct = default) => SendAsync(to, s, h, ct);
        public Task SendAsync(string to, string s, string h, string t, CancellationToken ct = default) => SendAsync(to, s, h, ct);
        public Task SendWithIcsAsync(string to, string s, string h, string ics, string fn, CancellationToken ct = default) => SendAsync(to, s, h, ct);
        public Task SendWithAttachmentsAsync(string to, string s, string h, IReadOnlyCollection<CommunityHub.Core.Email.EmailAttachment> a, CancellationToken ct = default) => SendAsync(to, s, h, ct);
    }

    // ---- §1060(i) — the notice to info@ ------------------------------------

    /// <summary>
    /// Operator 2026-08-11: <i>"organizers info mail must get email when approved auto so we can
    /// overrule the date"</i> — ONE mail per approved post, to a hard-coded address.
    /// 🔒 The recipient is asserted as a literal: he answered <i>"you do not chk for any some
    /// settings"</i>, so there must be nothing configurable to drift.
    /// </summary>
    [Fact]
    public async Task Each_auto_approved_post_notifies_the_info_mailbox_with_its_date()
    {
        using var db = NewDb();
        await SeedAsync(db);
        db.SoMePosts.Add(WithGraphic(db, Planned(Now.AddDays(3))));
        db.SoMePosts.Add(WithGraphic(db, Planned(Now.AddDays(9))));
        await db.SaveChangesAsync();

        var email = new RecordingEmail();
        var result = await NewService(db, email).RunAsync(EventId);

        Assert.Equal(2, result.Approved);
        Assert.Equal(2, email.Sent.Count);
        Assert.All(email.Sent, m => Assert.Equal("info@expertslive.dk", m.To));
        // The date is the whole point — it is what he is being given the chance to overrule.
        Assert.All(email.Sent, m => Assert.Contains("Publishes:", m.Body));
    }

    /// <summary>🔒 Nothing approved ⇒ nothing sent. A "nothing happened" mail trains him to ignore
    /// the one that matters (§1041b).</summary>
    [Fact]
    public async Task A_sweep_that_approves_nothing_sends_nothing()
    {
        using var db = NewDb();
        await SeedAsync(db);
        db.SoMePosts.Add(Planned(Now.AddDays(3)));   // no graphic ⇒ blocked
        await db.SaveChangesAsync();

        var email = new RecordingEmail();
        var result = await NewService(db, email).RunAsync(EventId);

        Assert.Equal(0, result.Approved);
        Assert.Empty(email.Sent);
    }

    /// <summary>
    /// 🔴 THE NOTICE MUST BE RING-EXEMPT, AND THIS TEST EXISTS BECAUSE PROD PROVED IT.
    /// </summary>
    /// <remarks>
    /// <para>Shipped without it 2026-08-11 and measured minutes later in PROD: <b>all 19 notices were
    /// dropped</b> — <c>Email RING-DROP (unknown recipient): info@expertslive.dk</c>, nineteen times —
    /// while the approvals themselves succeeded. <c>info@</c> is an organizer MAILBOX, not a
    /// participant row, and the per-recipient ring gate fails closed on an address it cannot find.
    /// That gate is right; the send simply has to declare itself ops mail.</para>
    ///
    /// <para>🔒 The §335 shape at its purest: the approvals worked, the log looked healthy, and the
    /// only symptom was an inbox that stayed empty. Nothing in the suite could have caught it,
    /// because nothing asserted the CONTEXT the mail was sent under — only that it was sent.</para>
    ///
    /// <para>⚠️ Speaker and sponsor announcements are deliberately NOT exempt (operator 2026-08-11:
    /// <i>"emails for speakers + sponsors goes out with respect of ring-gate"</i>). The exemption is
    /// for the organizer mailbox alone.</para>
    /// </remarks>
    [Fact]
    public async Task The_info_notice_is_sent_as_ring_exempt_ops_mail()
    {
        using var db = NewDb();
        await SeedAsync(db);
        db.SoMePosts.Add(WithGraphic(db, Planned(Now.AddDays(3))));
        await db.SaveChangesAsync();

        var ctx = new CommunityHub.Core.Email.EmailContextAccessor();
        var email = new ContextCapturingEmail(ctx);

        await new SoMeAutoApproveService(
            db, new SoMeApprovalGate(db), new FixedClock(Now), log: null, email: email, ctx: ctx)
            .RunAsync(EventId);

        Assert.True(email.SeenRingExempt, "the notice must be sent with EmailContext.RingExempt");
    }

    /// <summary>Reads the ambient EmailContext AT SEND TIME — the only moment it is set.</summary>
    private sealed class ContextCapturingEmail(CommunityHub.Core.Email.IEmailContextAccessor ctx)
        : CommunityHub.Core.Email.IEmailSender
    {
        public bool SeenRingExempt { get; private set; }

        public Task SendAsync(string to, string s, string h, CancellationToken ct = default)
        {
            SeenRingExempt = ctx.Current?.RingExempt == true;
            return Task.CompletedTask;
        }

        public Task SendAsync(string to, string s, string h, IReadOnlyCollection<string>? cc, CancellationToken ct = default) => SendAsync(to, s, h, ct);
        public Task SendAsync(string to, string s, string h, string t, CancellationToken ct = default) => SendAsync(to, s, h, ct);
        public Task SendWithIcsAsync(string to, string s, string h, string ics, string fn, CancellationToken ct = default) => SendAsync(to, s, h, ct);
        public Task SendWithAttachmentsAsync(string to, string s, string h, IReadOnlyCollection<CommunityHub.Core.Email.EmailAttachment> a, CancellationToken ct = default) => SendAsync(to, s, h, ct);
    }

    /// <summary>
    /// 🔴 The approval is the FACT; the notice is a report of it. A failing mail server must not
    /// leave a post unapproved — which would be a silent, self-inflicted outage of the campaign.
    /// </summary>
    [Fact]
    public async Task A_failing_notice_does_not_undo_the_approval()
    {
        using var db = NewDb();
        await SeedAsync(db);
        db.SoMePosts.Add(WithGraphic(db, Planned(Now.AddDays(3))));
        await db.SaveChangesAsync();

        var result = await NewService(db, new RecordingEmail(throws: true)).RunAsync(EventId);

        Assert.Equal(1, result.Approved);
        Assert.True((await db.SoMePosts.FirstAsync()).IsActive);
    }

    private static async Task SeedAsync(
        CommunityHubDbContext db, bool enabled = true, int leadDays = 7)
    {
        db.Events.Add(new Event
        {
            Id = EventId, Code = "ELDK27", CommunityName = "ELDK", DisplayName = "ELDK 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        });
        db.SoMeSettings.Add(new SoMeSettings
        {
            EventId = EventId, Enabled = true,
            AutoApproveEnabled = enabled, AutoApproveLeadDays = leadDays,
        });
        await db.SaveChangesAsync();
    }

    private static SoMePost Planned(DateTimeOffset when, SoMeTemplateKind kind = SoMeTemplateKind.SpeakerTracks) =>
        new()
        {
            EventId = EventId, TemplateKind = kind, SubjectKey = $"track:T{when.Ticks}",
            Occurrence = 1, ScheduledAtUtc = when, Status = SoMePostStatus.Queued,
            IsActive = false, AutoGenerated = true, AutoText = "✨ {TrackName} ✨",
        };

    /// <summary>
    /// §1060(g) — give a planned post's subject a RELEASED graphic, because "no graphic" is now a
    /// hard block and an approvable post must therefore have one.
    /// </summary>
    /// <remarks>
    /// ⚠️ Only the tests that assert a post IS approved need this. The ones asserting it is NOT
    /// approved would pass either way — which is exactly why the graphic must be added deliberately
    /// rather than to every fixture: a blanket fixture would let a genuinely broken window rule hide
    /// behind the graphic rule, and both tests would still be green.
    /// 🔑 The asset's StableKey is SLUGGED, because <c>SoMeSubjectGraphic.Lookup</c> slugs the post's
    /// subject key to meet it — the one asymmetry that class exists to reconcile.
    /// </remarks>
    private static SoMePost WithGraphic(CommunityHub.Core.Data.CommunityHubDbContext db, SoMePost post)
    {
        var name = post.SubjectKey!["track:".Length..];
        db.GraphicAssets.Add(new GraphicAsset
        {
            EventId = EventId, Status = GraphicAssetStatus.Released,
            Type = GraphicAssetType.TrackBundle,
            StableKey = $"track:{SoMeSubjectGraphic.Slug(name)}",
            FileName = $"track-{SoMeSubjectGraphic.Slug(name)}.png",
        });
        return post;
    }

    [Fact]
    public async Task It_does_nothing_at_all_until_he_switches_it_on()
    {
        using var db = NewDb();
        await SeedAsync(db, enabled: false);
        db.SoMePosts.Add(Planned(Now.AddDays(3)));   // due — the switch is the only thing stopping it
        await db.SaveChangesAsync();

        var result = await NewService(db).RunAsync(EventId);

        Assert.Equal(0, result.Approved);
        Assert.All(await db.SoMePosts.ToListAsync(), p => Assert.False(p.IsActive));
    }

    /// <summary>🔑 §1030 — the post has reached its time. This is when a rule may approve it.</summary>
    [Fact]
    public async Task A_post_due_inside_the_window_is_approved()
    {
        using var db = NewDb();
        await SeedAsync(db, leadDays: 7);
        db.SoMePosts.Add(WithGraphic(db, Planned(Now.AddDays(3))));
        await db.SaveChangesAsync();

        var result = await NewService(db).RunAsync(EventId);

        Assert.Equal(1, result.Approved);
        Assert.True((await db.SoMePosts.FirstAsync()).IsActive);
    }

    /// <summary>
    /// 🔴 §1068 — an overdue post IS switched on now, but only when it is otherwise eligible.
    /// </summary>
    /// <remarks>
    /// ⚠️ The fixture here has NO graphic on purpose, so this pins the half that still holds: being
    /// overdue no longer blocks anything, and the ELIGIBILITY gate is doing the refusing. Without
    /// that distinction, "overdue posts are approved" could be read as "overdue posts skip the
    /// gates", which is the one reading that would put a half-written post on LinkedIn.
    /// </remarks>
    [Fact]
    public async Task An_overdue_post_is_still_refused_when_it_is_not_ELIGIBLE()
    {
        using var db = NewDb();
        await SeedAsync(db);
        db.SoMePosts.Add(Planned(Now.AddHours(-1)));   // overdue AND has no graphic
        await db.SaveChangesAsync();

        var result = await NewService(db).RunAsync(EventId);

        Assert.Equal(0, result.Approved);
        Assert.Equal(1, result.Blocked);               // blocked by the GATE, not by its date
        Assert.False((await db.SoMePosts.FirstAsync()).IsActive);
    }

    /// <summary>
    /// 🔴 §1060 — THE WINDOW IS RETIRED, AND THIS IS THE TEST THAT SAYS SO.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-11: <i>"drop the lead time req earlier made"</i> · <i>"approve when
    /// eligible"</i>. ⚠️ This test previously asserted the EXACT OPPOSITE — that a post 30 days out
    /// stays planned — and it was correct when written (§1030). It is re-pointed rather than
    /// deleted, because the edge it marks is still real: it is now the edge that must NOT hold.</para>
    /// </remarks>
    [Fact]
    public async Task A_post_months_away_is_approved_because_eligibility_is_the_only_condition()
    {
        using var db = NewDb();
        await SeedAsync(db, leadDays: 7);
        db.SoMePosts.Add(WithGraphic(db, Planned(Now.AddDays(30))));   // far outside any old window
        await db.SaveChangesAsync();

        var result = await NewService(db).RunAsync(EventId);

        Assert.Equal(1, result.Approved);
        Assert.Equal(0, result.NotYetDue);
        Assert.True((await db.SoMePosts.FirstAsync()).IsActive);
    }

    /// <summary>
    /// 🔒 <c>SoMeSettings.AutoApproveLeadDays</c> still EXISTS on the row — so this pins that nothing
    /// reads it any more. Without this, a future edit could quietly reinstate the window from a value
    /// still sitting in the database, and every other test here would stay green.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(365)]
    public async Task The_retired_lead_days_setting_no_longer_changes_anything(int leadDays)
    {
        using var db = NewDb();
        await SeedAsync(db, leadDays: leadDays);
        db.SoMePosts.Add(WithGraphic(db, Planned(Now.AddDays(90))));
        await db.SaveChangesAsync();

        var result = await NewService(db).RunAsync(EventId);

        Assert.Equal(1, result.Approved);
    }

    /// <summary>
    /// 🔴 §1068 — A BACKDATED POST THAT NEVER PUBLISHED IS APPROVED TOO. NO DATE CONDITION REMAINS.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-11: <i>"if a post has NOT been published yet and points to the past,
    /// then it must approve and publish it, very important that the guard picks that up"</i>.</para>
    ///
    /// <para>⚠️ This test asserted the OPPOSITE an hour earlier, and the reasoning it carried
    /// (§889.1 — approving an overdue post publishes it on the next tick) was true then and is still
    /// true. What changed is whether that is a hazard or the intent: it is now the INTENT. The
    /// hazard §889.1 described was a rule publishing something UNREVIEWED, and eligibility is now the
    /// review.</para>
    ///
    /// <para>🔑 The cost of the old behaviour was the reason to drop it: a post blocked on a missing
    /// abstract while its date slid past would have sat planned for ever — never published, never
    /// chased, and invisible. The campaign would quietly lose posts.</para>
    /// </remarks>
    [Fact]
    public async Task A_backdated_post_that_never_published_is_approved_along_with_a_future_one()
    {
        using var db = NewDb();
        await SeedAsync(db, leadDays: 7);
        db.SoMePosts.Add(WithGraphic(db, Planned(Now.AddDays(-30))));     // long overdue
        db.SoMePosts.Add(WithGraphic(db, Planned(Now.AddMinutes(-1))));   // just passed
        db.SoMePosts.Add(WithGraphic(db, Planned(Now.AddMinutes(1))));    // still to come
        await db.SaveChangesAsync();

        var result = await NewService(db).RunAsync(EventId);

        Assert.Equal(3, result.Approved);
        Assert.Equal(0, result.NotYetDue);
        Assert.All(await db.SoMePosts.ToListAsync(), p => Assert.True(p.IsActive));
    }

    /// <summary>
    /// 🔒 …but an ALREADY-PUBLISHED post is still left alone. His rule is scoped to posts that have
    /// NOT published — without this, "no date condition" could be read as "approve everything", and
    /// a republish is the one mistake that cannot be taken back.
    /// </summary>
    [Fact]
    public async Task An_overdue_post_that_ALREADY_published_is_still_untouched()
    {
        using var db = NewDb();
        await SeedAsync(db, leadDays: 7);
        var published = WithGraphic(db, Planned(Now.AddDays(-30)));
        published.Status = SoMePostStatus.Published;
        db.SoMePosts.Add(published);
        await db.SaveChangesAsync();

        var result = await NewService(db).RunAsync(EventId);

        Assert.Equal(0, result.Approved);
        Assert.False((await db.SoMePosts.FirstAsync()).IsActive);
    }

    /// <summary>
    /// 🔒 GUARD 3 — a post carrying a HUMAN's words keeps a human's approval, even though its type
    /// is template-built.
    /// </summary>
    [Fact]
    public async Task A_post_he_edited_still_waits_for_him()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var post = Planned(Now.AddDays(3));   // due — his words are the only thing stopping it
        post.ManualTextOverride = "my own words";
        db.SoMePosts.Add(post);
        await db.SaveChangesAsync();

        var result = await NewService(db).RunAsync(EventId);

        Assert.Equal(0, result.Approved);
        Assert.False((await db.SoMePosts.FirstAsync()).IsActive);
    }

    /// <summary>🔒 GUARD 3 — Type 5 is his own copy from the deck; never auto-approved.</summary>
    [Fact]
    public async Task An_event_post_is_never_auto_approved()
    {
        using var db = NewDb();
        await SeedAsync(db);
        db.SoMePosts.Add(Planned(Now.AddDays(3), SoMeTemplateKind.EventPost));   // due; Type 5 anyway
        await db.SaveChangesAsync();

        var result = await NewService(db).RunAsync(EventId);

        Assert.Equal(0, result.Approved);
    }

    /// <summary>
    /// 🔒 GUARD 2 — the approval gate still applies. A sponsor who has delivered neither their
    /// social text nor a logo is not announced just because a rule was switched on.
    /// </summary>
    [Fact]
    public async Task A_sponsor_with_a_missing_dependency_is_reported_not_approved()
    {
        using var db = NewDb();
        await SeedAsync(db);
        db.SponsorInfos.Add(new SponsorInfo
        {
            EventId = EventId, SponsorCompanyId = "co-9", CompanyName = "Not Ready A/S",
            SponsorPackage = SponsorPackage.Gold,
            SocialMediaIntro = null,          // owes their text
        });
        // 🔑 Due inside the window, so the GATE is what stops it — not the clock.
        var post = Planned(Now.AddDays(3), SoMeTemplateKind.Sponsor);
        post.SubjectKey = "sponsor:co-9";
        post.SponsorCompanyId = "co-9";
        db.SoMePosts.Add(post);
        await db.SaveChangesAsync();

        var result = await NewService(db).RunAsync(EventId);

        Assert.Equal(0, result.Approved);
        Assert.Equal(1, result.Blocked);
        Assert.False((await db.SoMePosts.FirstAsync()).IsActive);
    }

    /// <summary>
    /// 🔒 A ZERO lead used to be floored to one day, because a misconfigured field must not decide
    /// the window. There is no window now, so what this pins is that a zero — the value a
    /// half-filled settings form leaves behind — changes nothing at all. Both posts are eligible,
    /// both are approved.
    /// </summary>
    [Fact]
    public async Task A_zero_lead_is_simply_irrelevant_now()
    {
        using var db = NewDb();
        await SeedAsync(db, leadDays: 0);
        db.SoMePosts.Add(WithGraphic(db, Planned(Now.AddHours(2))));
        db.SoMePosts.Add(WithGraphic(db, Planned(Now.AddDays(2))));
        await db.SaveChangesAsync();

        var result = await NewService(db).RunAsync(EventId);

        Assert.Equal(2, result.Approved);
        Assert.Equal(0, result.NotYetDue);
    }

    [Fact]
    public async Task An_already_published_post_is_left_alone()
    {
        using var db = NewDb();
        await SeedAsync(db);
        // Due inside the window on purpose, so STATUS is what excludes it rather than the clock;
        // the slot then moved out (a re-schedule after publication), which is why the two disagree.
        var post = Planned(Now.AddDays(3));
        post.Status = SoMePostStatus.Published;
        post.PublishedAtUtc = Now.AddDays(-1);
        db.SoMePosts.Add(post);
        await db.SaveChangesAsync();

        Assert.Equal(0, (await NewService(db).RunAsync(EventId)).Approved);
    }
}
