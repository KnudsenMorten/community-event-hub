using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// §841 — THE POST EDITOR: one post at a time, walked with previous/next.
///
/// <para>Operator 2026-08-05: <i>"i need a way to change linked picture for a post in an easy way
/// where i can preview pic and link it. i need to easily scroll through all posts with previus,next,
/// edit,save and chg posting schedule"</i>. With 112 posts planned (§840.2) the queue's flat list is
/// the wrong tool — this is the one-post-at-a-time view for working through them.</para>
///
/// <para>🔒 <b>Editing writes <see cref="SoMePost.ManualTextOverride"/>, never <c>AutoText</c></b>
/// (§19: the override is what publishes). That is also what protects the edit: the scheduler adds
/// and never recomposes (§824.21a), and the override survives regardless.</para>
///
/// <para>🔒 <b>Approval and schedule are separate decisions.</b> Saving text or moving a date must
/// never flip <see cref="SoMePost.IsActive"/> — that field is the entire "nothing publishes until a
/// human says so" model, and this page deliberately does not touch it.</para>
/// </summary>
[Authorize]
public class SoMePostEditorModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly CommunityHubDbContext _db;
    private readonly SoMeGraphicLibrary _graphics;

    private readonly SoMeApprovalGate _approvalGate;

    public SoMePostEditorModel(
        ICurrentParticipantAccessor participant,
        CommunityHubDbContext db,
        SoMeGraphicLibrary graphics,
        SoMeApprovalGate approvalGate,
        SoMePostComposer composer)
    {
        _participant = participant;
        _db = db;
        _graphics = graphics;
        _approvalGate = approvalGate;
        _composer = composer;
    }

    private readonly SoMePostComposer _composer;

    /// <summary>
    /// §865.2(5) — the post split into his words and its variables, so the preview can colour them
    /// apart while showing today's REAL values.
    /// </summary>
    public IReadOnlyList<SoMePostComposer.Segment> PreviewSegments { get; private set; } =
        Array.Empty<SoMePostComposer.Segment>();

    /// <summary>The tokens he can insert into the free-style text for THIS post type.</summary>
    public IReadOnlyList<string> AvailableTokens { get; private set; } = Array.Empty<string>();

    /// <summary>True when the body places the credit itself, so it is not appended a second time.</summary>
    public bool BodyPlacesCredit { get; private set; }

    public bool AccessDenied { get; private set; }
    public string? Message { get; private set; }
    public bool MessageIsError { get; private set; }

    public SoMePost? Post { get; private set; }

    /// <summary>§850.2 — include posts that cannot be approved yet. Off by default.</summary>
    public bool IncludeIneligible { get; private set; }

    /// <summary>How many posts the filter is hiding. ⚠️ Always shown — hidden must not mean forgotten.</summary>
    public int HiddenCount { get; private set; }

    /// <summary>Why THIS post cannot be approved yet, or null. Shown on the post itself.</summary>
    public string? BlockedReason { get; private set; }

    /// <summary>§851.2 — show only this post type, or all when null.</summary>
    public SoMeTemplateKind? KindFilter { get; private set; }

    /// <summary>
    /// §872 — the three states he asked to filter by, IN HIS WORDS.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-05: <i>"filter and see scheduled vs. planned vs. published (add the
    /// wording so it says Planned (not approved), Scheduled (approved) and Published"</i>.</para>
    ///
    /// <para>🔑 <b>His wording DEFINES the axis: approval.</b> Planned = not approved,
    /// Scheduled = approved, Published = already on LinkedIn. ⚠️ That is NOT the same axis as
    /// <c>SoMePostPlanState</c> (§848.2 — whether the PLANNER or he owns the slot), and the header
    /// badge used to read from that one. Two different meanings of "Scheduled" on one page is
    /// exactly the confusion worth removing, so the badge now follows THIS definition and the
    /// planner-lock is shown as its own separate tag — nothing is lost, and the page agrees with
    /// itself.</para>
    /// </remarks>
    public enum SoMePostState { Planned, Scheduled, Published }

    /// <summary>Which state the walk is showing. 🔒 Defaults to Planned — his instruction.</summary>
    public SoMePostState StateFilter { get; private set; } = SoMePostState.Planned;

    /// <summary>How many posts sit in each state, counted BEFORE filtering (§851.2's rule).</summary>
    public IReadOnlyDictionary<SoMePostState, int> CountByState { get; private set; } =
        new Dictionary<SoMePostState, int>();

    public static string StateLabel(SoMePostState s) => s switch
    {
        SoMePostState.Planned => "Planned (not approved)",
        SoMePostState.Scheduled => "Scheduled (approved)",
        _ => "Published",
    };

    /// <summary>How many posts of each type exist, so the filter says what it will show.</summary>
    public IReadOnlyDictionary<SoMeTemplateKind?, int> CountByKind { get; private set; } =
        new Dictionary<SoMeTemplateKind?, int>();

    /// <summary>Where this post sits in the queue — "12 of 112".</summary>
    public int Position { get; private set; }
    public int Total { get; private set; }

    public int? PreviousId { get; private set; }
    public int? NextId { get; private set; }

    /// <summary>§861 — the edition code, so the credit block can be composed and recognised.</summary>
    public string? EditionCode { get; private set; }

    /// <summary>
    /// §861 — the organizer credit exactly as it will publish, shown READ-ONLY under the text box.
    /// </summary>
    /// <remarks>
    /// 🔒 He asked for the credit to become a variable (*"you must replace it with variables"*), so
    /// it is displayed rather than edited. Showing it matters: it is part of the post, and hiding it
    /// entirely would make the editor lie about what publishes.
    /// </remarks>
    public string? CreditPreview { get; private set; }

    /// <summary>What the picker offers. Empty when the library is not wired — text still edits.</summary>
    public IReadOnlyList<SoMeGraphicRef> Graphics { get; private set; } = Array.Empty<SoMeGraphicRef>();

    public bool GraphicsAvailable => _graphics.CanRead;

    [BindProperty] public int PostId { get; set; }
    [BindProperty] public string? EditText { get; set; }
    [BindProperty] public string? ImageRef { get; set; }
    [BindProperty] public DateTime? ScheduledAt { get; set; }

    /// <summary>§844.3 — graphic or video for THIS post.</summary>
    [BindProperty] public SoMePostMediaKind MediaKind { get; set; }

    /// <summary>Whether video is offered at all for this post's type (§844.2).</summary>
    public bool VideoSupported { get; private set; }

    /// <param name="media">
    /// §844.3 — lets him PREVIEW the other medium's library without saving first. Nothing is stored
    /// by looking: the post keeps its medium until Save.
    /// </param>
    /// <param name="includeIneligible">
    /// §850.2 — bring back the posts that cannot be approved yet. Off by default, because with 14 of
    /// 15 sponsors missing their social-media text (§850.1) a full list is mostly dead entries.
    /// </param>
    /// <param name="kind">
    /// §851.2 — work on one post TYPE at a time. Distinct from <paramref name="includeIneligible"/>:
    /// that asks "can I act on this?", this asks "is this the kind of thing I am working on now?".
    /// With speakers gated until 7 Sep (§851), event posts are the only type worth opening today.
    /// </param>
    public async Task<IActionResult> OnGetAsync(
        int? id, SoMePostMediaKind? media, bool includeIneligible,
        SoMeTemplateKind? kind, SoMePostState? state, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        IncludeIneligible = includeIneligible;
        KindFilter = kind;
        // §872 — default PLANNED when nothing is asked for, which is his stated default.
        StateFilter = state ?? SoMePostState.Planned;
        await LoadAsync(me.EventId, id, ct, media);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var post = await _db.SoMePosts
            .FirstOrDefaultAsync(p => p.Id == PostId && p.EventId == me.EventId, ct);

        if (post is null)
        {
            Message = "That post no longer exists.";
            MessageIsError = true;
            await LoadAsync(me.EventId, null, ct);
            return Page();
        }

        // His edit, not AutoText. Blank clears it and the composed text takes over again — which is
        // how he undoes an edit without needing a "revert" button.
        //
        // 🔒 §861 — STORE THE BODY ONLY. The credit is appended at publish time, so it must never
        // come back in here. Stripped defensively: the box does not contain it, but a paste from an
        // older post (or a legacy row opened before this shipped) would otherwise re-freeze it and
        // silently opt the post out of the §858 mention upgrade.
        var editionCode = await _db.Events
            .Where(e => e.Id == post.EventId).Select(e => e.Code).FirstOrDefaultAsync(ct);
        var text = (EditText ?? string.Empty).Trim();
        if (SoMePostCredit.TryStrip(text, editionCode, out var bodyOnly)) text = bodyOnly;
        post.ManualTextOverride = text.Length == 0 ? null : text;

        // A bare file name in the matching library folder, or blank for a text-only post.
        var img = (ImageRef ?? string.Empty).Trim();
        post.ImageRef = img.Length == 0 ? null : img;

        // §844.3 — the medium is a stored DECISION, never sniffed from the extension. Refused for a
        // type that has no video library (§844.2: video is for event, session and sponsor posts
        // only), so the post cannot end up pointing at a folder that does not exist.
        post.MediaKind = MediaKind == SoMePostMediaKind.Video
                         && SoMeGraphicLibrary.SupportsVideo(post.TemplateKind)
            ? SoMePostMediaKind.Video
            : SoMePostMediaKind.Graphic;

        if (ScheduledAt is { } when)
        {
            // 🔒 §844.5 — the input is DANISH wall-clock (that is what he sees and types), so it is
            // converted back to the UTC instant we store. Treating it as UTC would silently move
            // every edited post by an hour or two, and the drift would look like a scheduling bug.
            post.ScheduledAtUtc = SoMeDisplayTime.FromInput(when);
        }

        post.UpdatedAt = DateTimeOffset.UtcNow;
        post.LastUpdatedByEmail = me.Email;

        // ⚠️ IsActive is deliberately NOT touched here. Approving is its own act, in the queue.
        await _db.SaveChangesAsync(ct);

        Message = "Saved.";
        await LoadAsync(me.EventId, PostId, ct);
        return Page();
    }

    /// <summary>
    /// §848.2 — ACCEPT this proposal, or hand it back to the planner.
    /// </summary>
    /// <remarks>
    /// 🔒 Accepting LOCKS the post: the planner never re-plans, moves or removes a Scheduled post
    /// again. Handing it back makes it a proposal once more, and the next run may move it.
    ///
    /// ⚠️ Accepting is NOT approving-to-publish. <c>IsActive</c> remains the publish gate, so a post
    /// can be accepted (its slot is settled) and still switched off. Two different questions.
    /// </remarks>
    public async Task<IActionResult> OnPostSetPlanStateAsync(
        int id, SoMePostPlanState state, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var post = await _db.SoMePosts
            .FirstOrDefaultAsync(p => p.Id == id && p.EventId == me.EventId, ct);

        if (post is null)
        {
            Message = "That post no longer exists.";
            MessageIsError = true;
        }
        else
        {
            post.PlanState = state;
            post.UpdatedAt = DateTimeOffset.UtcNow;
            post.LastUpdatedByEmail = me.Email;
            await _db.SaveChangesAsync(ct);

            Message = state == SoMePostPlanState.Scheduled
                ? "Accepted. This post is locked — the planner will not move it again."
                : "Handed back to the planner. It may be moved or replaced on the next run.";
        }

        await LoadAsync(me.EventId, id, ct);
        return Page();
    }

    /// <summary>
    /// §853 — DELETE this post (or restore it).
    /// </summary>
    /// <remarks>
    /// <para>🔴 A SOFT delete. The row survives as the record that says "do not propose this again":
    /// since §848.2 the planner re-plans its own proposals, so a hard delete would reappear on the
    /// next tick. Restoring clears it and the subject returns to the campaign.</para>
    ///
    /// <para>⚠️ Deleting a PUBLISHED post removes it from CEH only — it is already on LinkedIn and
    /// nothing here retracts it. The page says so before he does it.</para>
    /// </remarks>
    public async Task<IActionResult> OnPostDeleteAsync(int id, bool restore, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var post = await _db.SoMePosts
            .FirstOrDefaultAsync(p => p.Id == id && p.EventId == me.EventId, ct);

        if (post is null)
        {
            Message = "That post no longer exists.";
            MessageIsError = true;
            await LoadAsync(me.EventId, null, ct);
            return Page();
        }

        post.IsDeleted = !restore;
        post.DeletedAt = restore ? null : DateTimeOffset.UtcNow;
        post.DeletedByEmail = restore ? null : me.Email;

        if (!restore)
        {
            // A deleted post must never publish, whatever it was before.
            post.IsActive = false;
        }

        post.UpdatedAt = DateTimeOffset.UtcNow;
        post.LastUpdatedByEmail = me.Email;
        await _db.SaveChangesAsync(ct);

        if (restore)
        {
            Message = "Restored. The planner will include this post again.";
        }
        else
        {
            Message = post.Status == SoMePostStatus.Published
                ? "Deleted from CEH. ⚠️ This post is already on LinkedIn — deleting it here does "
                  + "NOT remove it from the company page."
                : "Deleted. The planner will not propose this one again.";

            // 🔴 §842.5 — deleting a sponsor post can put that sponsor below their two contractual
            // announcements. His call, but never a silent one.
            if (post.TemplateKind == SoMeTemplateKind.Sponsor && post.SubjectKey is { } key)
            {
                var remaining = await _db.SoMePosts.CountAsync(
                    p => p.EventId == me.EventId && p.SubjectKey == key && !p.IsDeleted, ct);

                if (remaining < 2)
                {
                    Message += $" ⚠️ This sponsor now has {remaining} announcement(s). Every sponsor "
                             + "must be announced twice — that is a contractual obligation.";
                    MessageIsError = true;
                }
            }
        }

        await LoadAsync(me.EventId, id, ct);
        return Page();
    }

    private async Task LoadAsync(
        int eventId, int? id, CancellationToken ct, SoMePostMediaKind? mediaOverride = null)
    {
        // §861 — the credit is composed from settings, never stored on the post.
        EditionCode = await _db.Events
            .Where(e => e.Id == eventId).Select(e => e.Code).FirstOrDefaultAsync(ct);
        var credits = await _db.SoMeSettings
            .Where(s => s.EventId == eventId).Select(s => s.OrganizerCredits).FirstOrDefaultAsync(ct);
        CreditPreview = SoMePostCredit.Suffix(EditionCode, credits).TrimStart('\n');

        // The walk order IS the campaign order, so previous/next moves through time — the way he
        // reads the calendar. Id breaks ties so the order is stable across loads.
        // §853 — deleted posts are gone from the walk. The row survives only to stop the planner
        // re-proposing the subject; it is not something he steps through.
        var all = await _db.SoMePosts
            .Where(p => p.EventId == eventId && !p.IsDeleted)
            .OrderBy(p => p.ScheduledAtUtc)
            .ThenBy(p => p.Id)
            .Select(p => new
            {
                p.Id, p.SponsorCompanyId, p.TemplateKind, p.SubjectKey,
                // §872 — the state filter reads these two, so they come back in the same query
                // rather than a second pass over 84 rows.
                p.IsActive, p.Status,
            })
            .ToListAsync(ct);

        // §850.2 — hide what he cannot act on. Operator: "it doesn't make sense to approve a post
        // that is not ready". One query for the whole set, not one per post.
        var blockedCompanies = await _approvalGate.BlockedSponsorCompanyIdsAsync(eventId, ct);

        // 🔴 §865.4 — the TIER set too. Filtering on company ids alone silently passed every Type 3
        // tier post, which is the bug he reported on #540: three Silver sponsors owing their text,
        // and the post shown as ready with "Nothing is hidden" beside it.
        var blockedTiers = await _approvalGate.BlockedTiersAsync(eventId, ct);

        bool IsBlocked(string? sponsorCompanyId, SoMeTemplateKind? kind, string? subjectKey)
        {
            var probe = new SoMePost
            {
                SponsorCompanyId = sponsorCompanyId, TemplateKind = kind, SubjectKey = subjectKey,
            };
            return SoMeApprovalGate.IsBlockedBy(probe, blockedCompanies, blockedTiers);
        }

        // §851.2 — how many of each type exist, counted BEFORE any filter so the picker can say what
        // choosing it would show.
        CountByKind = all
            .GroupBy(p => p.TemplateKind)
            .ToDictionary(g => g.Key, g => g.Count());

        // §872 — how many are in each STATE, also counted before filtering, same reasoning.
        static SoMePostState StateOf(bool isActive, SoMePostStatus status) =>
            status == SoMePostStatus.Published ? SoMePostState.Published
            : isActive ? SoMePostState.Scheduled
            : SoMePostState.Planned;

        CountByState = all
            .GroupBy(p => StateOf(p.IsActive, p.Status))
            .ToDictionary(g => g.Key, g => g.Count());

        if (KindFilter is { } onlyKind)
        {
            all = all.Where(p => p.TemplateKind == onlyKind).ToList();
        }

        // 🔒 §872 — operator 2026-08-05: "filter and see scheduled vs. planned vs. published …
        // default i want to see only planned". DEFAULT IS PLANNED: the un-approved posts are the
        // ones he still has work to do on, and 84 posts of which most are already settled is a walk
        // through other people's finished business.
        all = all.Where(p => StateOf(p.IsActive, p.Status) == StateFilter).ToList();

        var eligible = all
            .Where(p => !IsBlocked(p.SponsorCompanyId, p.TemplateKind, p.SubjectKey))
            .ToList();

        // ⚠️ ALWAYS counted, even when hidden — hidden must never mean forgotten (§833/§839).
        HiddenCount = all.Count - eligible.Count;

        var visible = IncludeIneligible ? all : eligible;

        // 🔒 If he arrived on a post that the filter would hide (a link, a bookmark, or the post he
        // was just editing), show it rather than bouncing him somewhere else without explanation.
        if (id is { } wanted && visible.All(p => p.Id != wanted) && all.Any(p => p.Id == wanted))
        {
            visible = all;
            IncludeIneligible = true;
        }

        var ids = visible.Select(p => p.Id).ToList();

        Total = ids.Count;
        if (Total == 0) return;

        var currentId = id is not null && ids.Contains(id.Value) ? id.Value : ids[0];
        var index = ids.IndexOf(currentId);

        Position = index + 1;
        PreviousId = index > 0 ? ids[index - 1] : null;
        NextId = index < ids.Count - 1 ? ids[index + 1] : null;

        Post = await _db.SoMePosts.FirstOrDefaultAsync(p => p.Id == currentId, ct);

        if (Post is not null)
        {
            PostId = Post.Id;

            // 🔒 §861 — HE EDITS THE BODY ONLY. The organizer credit is deliberately NOT in this
            // box: a credit he can edit is a credit that FREEZES (§861.3 — posts 383 and 495 did
            // exactly that) and would then miss the §858 mention upgrade he is expecting.
            //
            // ⚠️ Legacy rows still have it baked in, so it is stripped on the way IN as well as by
            // the migration — otherwise the first person to open an un-migrated post would re-save
            // the frozen copy and put it straight back.
            EditText = SoMePostCredit.TryStrip(Post.EffectiveText, EditionCode, out var strippedBody)
                ? strippedBody
                : Post.EffectiveText;
            ImageRef = Post.ImageRef;

            // §865.2(5) — resolve the body's {tokens} NOW so the preview shows real values, while
            // still marking which runs are variables. Same resolver the publisher uses (§864), so
            // what he reads here is what will go out.
            var values = await _composer.ValuesForAsync(Post, ct);
            AvailableTokens = SoMePostComposer.TokensFor(Post.TemplateKind);
            BodyPlacesCredit =
                (EditText ?? string.Empty).Contains("{Organizers}", StringComparison.OrdinalIgnoreCase)
                || (EditText ?? string.Empty).Contains("{OrganizerLinkedInUrls}", StringComparison.OrdinalIgnoreCase);

            // 🔴 §872.1 — APPEND THE CREDIT AS A **TOKEN**, NOT AS RESOLVED TEXT.
            // Operator 2026-08-05: "the organizers variable is not colored as a variable". He was
            // right: composing it with SoMePostCredit.Compose() produced literal words, so
            // Segments() correctly classified it as HIS text and left it uncoloured — the preview
            // then lied about which parts update themselves, which is the one job the colour has.
            var previewBody = BodyPlacesCredit
                ? EditText
                : (EditText ?? string.Empty).TrimEnd()
                  + (string.IsNullOrWhiteSpace(credits)
                      ? string.Empty
                      : "\n\n{EditionCode} Organizers:\n{Organizers}");
            PreviewSegments = SoMePostComposer.Segments(previewBody, values);
            // §844.5 — shown and edited in DANISH wall-clock; stored as UTC.
            ScheduledAt = SoMeDisplayTime.ToDanish(Post.ScheduledAtUtc).DateTime;
            VideoSupported = SoMeGraphicLibrary.SupportsVideo(Post.TemplateKind);

            // §850 — why this one cannot be approved, shown on the post rather than only on refusal.
            BlockedReason = await _approvalGate.BlockedReasonAsync(Post, ct);

            // A requested medium wins for THIS view (so he can browse videos before committing),
            // but only when the type actually has one.
            MediaKind = mediaOverride is SoMePostMediaKind.Video && VideoSupported
                ? SoMePostMediaKind.Video
                : mediaOverride ?? Post.MediaKind;
        }

        // §844.3 — the gallery follows the post's TYPE and its current MEDIUM, so switching to video
        // shows videos rather than the graphics folder.
        Graphics = await _graphics.ListAsync(Post?.TemplateKind, MediaKind, ct);
    }
}
