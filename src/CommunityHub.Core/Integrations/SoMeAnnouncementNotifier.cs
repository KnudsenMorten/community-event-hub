using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Reminders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations;

/// <summary>Which of the two sends a message is — part of the ledger key, so they cannot collapse.</summary>
public enum SoMeAnnouncementStage
{
    /// <summary>The post is approved and dated.</summary>
    Scheduled,

    /// <summary>It publishes within 24 hours.</summary>
    DayBefore,
}

/// <summary>What one notification pass did.</summary>
public sealed record SoMeAnnouncementNotifyResult(int Posts, int Recipients, int Sent)
{
    public override string ToString() => $"{Posts} post(s), {Recipients} recipient(s), {Sent} mailed";
}

/// <summary>
/// §1060(b) — TELL THE PERSON THE POST IS ABOUT, TWICE: when it is scheduled, and the day before.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-11: <i>"i want to have an email sent out for speaker sessions (not speaker
/// tracks) and sponsor individual announcements (not sponsor category) when announcement is
/// scheduled and 1 day before it will published"</i> · <i>"mails also to speaker and sponsor"</i> ·
/// <i>"all event coordinators and signers receive mail"</i> · <i>"must include button to some page.
/// button has magic link"</i>.</para>
///
/// <para>🛑 <b>A NEW template, not the Help-Promote one.</b> He was explicit: <i>"that template is
/// different and separate"</i>. `speaker-graphics-ready` says <i>your artwork is ready, go promote
/// it</i>; this says <i>ELDK is announcing you, here is when</i>. Different sender intent, different
/// action. ⚠️ They WILL land in the same minute — a graphic appearing both fires that mail and makes
/// this post eligible (§1060(g)) — and that is accepted, not a bug to merge away.</para>
///
/// <para>🔒 <b>Idempotency is the ledger, keyed per POST per STAGE.</b> Not per participant: that is
/// §664's exact defect, where <c>graphics-ready:{participantId}</c> meant "told once, ever" and a
/// speaker who gained a second graphic could never be told again. The symptom was silence.</para>
/// </remarks>
public sealed class SoMeAnnouncementNotifier
{
    /// <summary>Ring + on/off travel with the message (§330), so the audience matches the switch.</summary>
    public const string FeatureKey = "some-scheduling";

    public const string ReminderType = "some-announcement";

    private readonly CommunityHubDbContext _db;
    private readonly ReminderEngine _engine;
    private readonly EmailTemplateProvider _templates;
    private readonly TimeProvider _clock;
    private readonly ILogger<SoMeAnnouncementNotifier>? _log;

    public SoMeAnnouncementNotifier(
        CommunityHubDbContext db, ReminderEngine engine, EmailTemplateProvider templates,
        TimeProvider? clock = null, ILogger<SoMeAnnouncementNotifier>? log = null)
    {
        _db = db;
        _engine = engine;
        _templates = templates;
        _clock = clock ?? TimeProvider.System;
        _log = log;
    }

    /// <summary>The ledger key — post AND stage, so the two sends never collapse into one.</summary>
    public static string OccasionKeyFor(int postId, SoMeAnnouncementStage stage) =>
        $"some-announcement:{postId}:{(stage == SoMeAnnouncementStage.Scheduled ? "scheduled" : "day-before")}";

    public async Task<SoMeAnnouncementNotifyResult> RunAsync(int eventId, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();

        // 🔒 APPROVED and still to come. An approved post is `IsActive` — SoMePostStatus has no
        // "Approved" value, only Queued/Published/Failed, and reading it as a status yields an
        // empty set every run (the §1060(j) trap, written down there).
        var posts = await _db.SoMePosts
            .Where(p => p.EventId == eventId
                        && !p.IsDeleted
                        && p.IsActive
                        && p.Status == SoMePostStatus.Queued
                        && p.ScheduledAtUtc > now
                        && p.SubjectKey != null)
            .ToListAsync(ct);

        var due = new List<ReminderMessage>();
        var recipientCount = 0;

        foreach (var post in posts)
        {
            var audience = await AudienceForAsync(post, eventId, ct);
            if (audience.Count == 0) continue;   // tracks and tiers — nobody, by his scope

            recipientCount += audience.Count;

            // Stage 1 is due as soon as the post is approved and dated; stage 2 inside 24 hours.
            // Both are offered every pass and the LEDGER decides — a post approved two days out
            // gets stage 1 now and stage 2 tomorrow, and neither repeats.
            var stages = new List<SoMeAnnouncementStage> { SoMeAnnouncementStage.Scheduled };
            if (post.ScheduledAtUtc - now <= TimeSpan.FromHours(24))
            {
                stages.Add(SoMeAnnouncementStage.DayBefore);
            }

            foreach (var stage in stages)
            {
                foreach (var r in audience)
                {
                    due.Add(Build(post, r, stage, eventId));
                }
            }
        }

        var sent = await _engine.SendDueAsync(eventId, due, ct);

        var result = new SoMeAnnouncementNotifyResult(posts.Count, recipientCount, sent);
        _log?.LogInformation("SoMe announcement notices: {Result}.", result);
        return result;
    }

    /// <summary>One person to tell, and what their page is called.</summary>
    private sealed record Recipient(
        int ParticipantId, string Email, string? FullName, string Path, string? LinkedInStatus);

    /// <summary>
    /// Who hears about this post — by TYPE, exactly as he scoped it.
    /// </summary>
    /// <remarks>
    /// 🛑 Tracks and sponsor CATEGORY posts return nobody: <i>"not speaker tracks"</i>, <i>"not
    /// sponsor category"</i>. A tier post is about several companies at once and has no single
    /// subject to address.
    /// </remarks>
    private async Task<List<Recipient>> AudienceForAsync(SoMePost post, int eventId, CancellationToken ct)
    {
        var people = new List<Recipient>();

        // --- a sponsor's own session: the linked speaker AND the company's contacts -------------
        if (SoMeSponsorSessionKey.TryParse(post.SubjectKey, out var sponsorSessionId))
        {
            var ss = await _db.SponsorSessions
                .Where(s => s.Id == sponsorSessionId && s.EventId == eventId)
                .Select(s => new
                {
                    s.SponsorCompanyId,
                    SpeakerIds = s.Speakers.Where(x => x.ParticipantId != null)
                        .Select(x => x.ParticipantId!.Value).ToList(),
                })
                .FirstOrDefaultAsync(ct);

            if (ss is null) return people;

            people.AddRange(await SpeakersAsync(ss.SpeakerIds, ct));
            people.AddRange(await SponsorContactsAsync(ss.SponsorCompanyId, eventId, ct));
            return Dedupe(people);
        }

        // --- a speaker session ------------------------------------------------------------------
        if (post.TemplateKind == SoMeTemplateKind.Session
            && post.SubjectKey!.StartsWith("session:", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(post.SubjectKey["session:".Length..], out var sessionId))
        {
            var speakerIds = await _db.SessionSpeakers
                .Where(x => x.SessionId == sessionId)
                .Select(x => x.ParticipantId)
                .ToListAsync(ct);

            return Dedupe(await SpeakersAsync(speakerIds, ct));
        }

        // --- an individual sponsor post ---------------------------------------------------------
        if (post.TemplateKind == SoMeTemplateKind.Sponsor
            && post.SubjectKey!.StartsWith("sponsor:", StringComparison.OrdinalIgnoreCase))
        {
            var companyId = post.SubjectKey["sponsor:".Length..];
            return Dedupe(await SponsorContactsAsync(companyId, eventId, ct));
        }

        return people;   // track, tier, or anything new — nobody, until he says otherwise
    }

    private async Task<List<Recipient>> SpeakersAsync(List<int> participantIds, CancellationToken ct) =>
        participantIds.Count == 0
            ? []
            : await _db.Participants
                .Where(p => participantIds.Contains(p.Id) && p.Email != null && p.Email != "")
                .Select(p => new Recipient(
                    p.Id, p.Email, p.FullName, "/Speaker/Announcements", p.LinkedInPersonUrnStatus))
                .ToListAsync(ct);

    /// <summary>
    /// 🔴 Every contact who is a signer OR an event coordinator — operator: <i>"all event
    /// coordinators and signers receive mail"</i>.
    /// ⚠️ <b>OR, then de-duplicated.</b> The domain doc is explicit that a contact can be BOTH, and
    /// the naive read of two role flags is two rows — which mails the same person the same
    /// announcement twice. (§867.1's shape: two conditions on ONE row.)
    /// </summary>
    private async Task<List<Recipient>> SponsorContactsAsync(
        string? companyId, int eventId, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(companyId)
            ? []
            : await _db.Participants
                .Where(p => p.EventId == eventId
                            && p.SponsorCompanyId == companyId
                            && p.IsActive
                            && (p.IsSigner || p.IsEventCoordinator)
                            && p.Email != null && p.Email != "")
                .Select(p => new Recipient(
                    p.Id, p.Email, p.FullName, "/Sponsor/Announcements", p.LinkedInPersonUrnStatus))
                .ToListAsync(ct);

    private static List<Recipient> Dedupe(List<Recipient> people) =>
        people
            .GroupBy(p => p.ParticipantId)
            .Select(g => g.First())
            .ToList();

    private ReminderMessage Build(
        SoMePost post, Recipient r, SoMeAnnouncementStage stage, int eventId)
    {
        var when = SoMeDisplayTime.ToDanish(post.ScheduledAtUtc);
        var isDayBefore = stage == SoMeAnnouncementStage.DayBefore;

        var tokens = _templates.NewTokenSet(r.ParticipantId);
        tokens["firstName"] = string.IsNullOrWhiteSpace(r.FullName) ? "there" : r.FullName!.Split(' ')[0];
        tokens["announcementsPath"] = r.Path;
        tokens["publishWhen"] = when.ToString("dddd dd MMM yyyy 'at' HH:mm");
        tokens["announcementSubject"] = SubjectLabel(post);
        tokens["subjectTail"] = isDayBefore
            ? "your announcement publishes tomorrow"
            : "your announcement is scheduled";
        tokens["leadParagraph"] = isDayBefore
            ? "A quick heads-up — the social-media post we have planned about you goes out tomorrow."
            : "We've scheduled a social-media post about you on the Experts Live Denmark channels.";
        tokens["followBlock"] = FollowBlock(r.LinkedInStatus);

        var rendered = _templates.Render("some-announcement", tokens);

        return new ReminderMessage(
            RecipientEmail: r.Email,
            ReminderType: ReminderType,
            OccasionKey: OccasionKeyFor(post.Id, stage),
            Subject: rendered.Subject,
            HtmlBody: rendered.HtmlBody,
            ParticipantId: r.ParticipantId,
            RecipientName: r.FullName,
            FeatureKey: FeatureKey,
            MailKey: "some-announcement");
    }

    private static string SubjectLabel(SoMePost post) =>
        post.TemplateKind == SoMeTemplateKind.Sponsor
            ? "A post about your company"
            : "A post about your session";

    /// <summary>
    /// §1060(f) — the follow line, from the mention precheck.
    /// </summary>
    /// <remarks>
    /// 🔴 <b>THREE outcomes from FIVE statuses, and the third is the point.</b> Only
    /// <c>Resolved</c> may thank them for following; only <c>NotAFollower</c> may say they do not.
    /// <c>Ambiguous</c>, <c>KeywordUnusable</c> and <c>LookupFailed</c> mean the lookup did not
    /// answer — telling a real follower they do not follow us, because a sweep was throttled, is the
    /// exact failure §858.16h shaped this data to prevent.
    /// </remarks>
    internal static string FollowBlock(string? status)
    {
        const string invite =
            "<p style=\"margin:0 0 16px;\">Being mentioned is optional — if you'd like us to tag you, "
            + "follow the <a href=\"https://www.linkedin.com/company/experts-live-denmark/\">Experts "
            + "Live Denmark</a> page on LinkedIn and we'll include you in posts about you.</p>";

        return status switch
        {
            "Resolved" =>
                "<p style=\"margin:0 0 16px;\">You'll be tagged in this post — thanks for following "
                + "our LinkedIn page.</p>",

            "NotAFollower" => invite,

            // Ambiguous / KeywordUnusable / LookupFailed / null — we do not know, so we do not claim.
            _ => invite,
        };
    }
}
