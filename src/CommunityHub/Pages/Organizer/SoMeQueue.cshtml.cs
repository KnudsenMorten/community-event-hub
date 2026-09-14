using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Organizer curation UI for the LinkedIn company-page SoMe scheduling queue
/// (REQUIREMENTS §19): list scheduled posts (the social-media calendar),
/// fine-tune text, Preview (render the exact post that will publish),
/// Active/Inactive toggle, reschedule, and ad-hoc one-off compose. Organizer-only,
/// mobile-first, a11y.
/// </summary>
[Authorize]
public class SoMeQueueModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly SoMeQueueService _queue;
    private readonly SoMeSubjectLabeller _subjects;
    private readonly TimeProvider _clock;
    private readonly SoMeReadiness _readiness;

    public SoMeQueueModel(
        ICurrentParticipantAccessor participant,
        SoMeQueueService queue,
        SoMeSubjectLabeller subjects,
        TimeProvider clock,
        // §1206 — the shared readiness rule, so this list says the same thing the settings page and
        // the calendar say about the same post.
        SoMeReadiness readiness)
    {
        _participant = participant;
        _queue = queue;
        _subjects = subjects;
        _clock = clock;
        _readiness = readiness;
    }

    public bool AccessDenied { get; private set; }
    public string? Message { get; private set; }

    /// <summary>§1193 — the post the last Activate/Deactivate acted on, so the row can say so.</summary>
    public int? ActionedPostId { get; private set; }

    /// <summary>§1193 — whether that action was REFUSED. Drives the colour and the anchor.</summary>
    public bool ActionFailed { get; private set; }

    public IReadOnlyList<SoMePost> Posts { get; private set; } = Array.Empty<SoMePost>();

    /// <summary>How many posts exist before the filters below are applied.</summary>
    public int TotalPosts { get; private set; }

    /// <summary>
    /// §889 — free-text search over the post BODY. 🔑 This is the one that actually finds a post:
    /// he remembers copy, not ids — *"the post i cannot find has a picture with 500 tickets"*.
    /// </summary>
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }

    /// <summary>§889 — planned / scheduled / published, on the SAME axis as the editor's badge.</summary>
    [BindProperty(SupportsGet = true)] public string? State { get; set; }

    /// <summary>§889 — one post TYPE at a time, the same axis the editor filters on.</summary>
    [BindProperty(SupportsGet = true)] public SoMeTemplateKind? Kind { get; set; }

    /// <summary>
    /// 🔴 §1209 — filter by CATEGORY (Type 1, 2a, 2b, 2c, 3, 4, 5), matching the Type column.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-09-12: <i>"remember to include it in filter as well"</i>, right after the
    /// column started naming 2a/2b/2c. A column and a filter that disagree is worse than either
    /// alone — reading "2a" on a row and then having no way to see the other 2a posts.</para>
    ///
    /// <para>🔒 <c>Kind</c> is KEPT and still applied. It is in bookmarked URLs and in the links other
    /// pages build, and a filter that silently stopped working would look like the queue had lost
    /// posts. The two compose: <c>Kind</c> narrows to a template kind, <c>Category</c> to one of the
    /// three schedules inside it.</para>
    /// </remarks>
    [BindProperty(SupportsGet = true)] public SoMeAnnouncementCategory? Category { get; set; }

    /// <summary>§889 — <c>date</c> (default) · <c>id</c> · <c>type</c> · <c>state</c>.</summary>
    [BindProperty(SupportsGet = true)] public string? Sort { get; set; }

    /// <summary>
    /// §889 — what each post is ABOUT, in words: a track name, a session title, a company.
    /// </summary>
    /// <remarks>
    /// 🔑 <b>This column is what makes types 1–4 findable at all.</b> Their bodies are the shared
    /// template, so a text snippet reads IDENTICALLY for all 16 track posts — the subject is the only
    /// thing that separates them. (The snippet earns its place on Type 5, where the body is his own
    /// unique copy: *"the post i cannot find has a picture with 500 tickets"*.)
    /// <para>Resolved in two batched lookups for the whole page, never one per row.</para>
    /// </remarks>
    /// <summary>
    /// §1168 — posts that will really publish WITHOUT an image, as the dispatcher would decide.
    /// </summary>
    /// <remarks>
    /// ⚠️ NOT <c>SoMePost.IsAwaitingApprovedGraphic</c>, which reads only the ref stamped at planning
    /// time. A graphic released after the post was planned never back-fills that stamp, so the badge
    /// said "no graphic" about posts that publish with one — the operator's 2026-09-03 report.
    /// </remarks>
    public IReadOnlySet<int> AwaitingGraphicPostIds { get; private set; } = new HashSet<int>();

    /// <summary>
    /// 🔴 §1206 — the HELD posts that could not be approved even if he clicked, and why.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-09-12: <i>"show then as not ready, like some graphics missing"</i>.</para>
    ///
    /// <para>⚠️ <b>The queue had one blocker out of a dozen.</b> "no graphic" was the only readiness
    /// signal on the row, so a post waiting on a sponsor's social text, a session's description or an
    /// unlinked co-speaker looked perfectly fine — and pressing Activate on it did nothing visible
    /// (§1193's report, whose real cause was this). Every reason now reaches the row, from the same
    /// <see cref="SoMeReadiness"/> the settings page and the calendar use.</para>
    /// </remarks>
    public IReadOnlyDictionary<int, string> NotReadyReasons { get; private set; } =
        new Dictionary<int, string>();

    /// <summary>How many rows are held back — the page's own count of §918's "19".</summary>
    public int NotReadyCount => NotReadyReasons.Count;

    /// <summary>
    /// §1209 — the master classes in this edition, so a Type 2 row can say 2a rather than "Type 2".
    /// </summary>
    /// <remarks>
    /// 🔑 ONE query for the whole page (§889: "two queries for a whole page, never one per row").
    /// </remarks>
    private IReadOnlySet<int> _masterClassSessionIds = new HashSet<int>();

    /// <summary>
    /// 🔴 §1209 — the post's CATEGORY in his words: Type 1, 2a, 2b, 2c, 3, 4, 5.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-09-12: <i>"can you update the type column to reflect the 2a, 2b, 2c names
    /// instead if type = 2"</i>. The queue printed the TEMPLATE KIND, and three categories share one
    /// kind — so master classes, technical sessions and sponsor speaker sessions were all "Type 2"
    /// while the settings page that governs their (different) dates calls them 2a, 2b and 2c.</para>
    ///
    /// <para>🔑 Same labels as that page, from <see cref="SoMeCategoryRules.Label"/>, so the two
    /// cannot drift apart.</para>
    /// </remarks>
    public string TypeLabel(SoMePost p)
    {
        ArgumentNullException.ThrowIfNull(p);

        if (p.TemplateKind is null) return "—";

        var category = SoMeCategoryRules.CategoryOf(
            p.TemplateKind, p.SubjectKey, _masterClassSessionIds);

        return category is { } c
            ? SoMeCategoryRules.Label(c)
            : SoMeTemplateCatalog.Title(p.TemplateKind.Value);
    }

    public IReadOnlyDictionary<int, string> SubjectLabels { get; private set; } =
        new Dictionary<int, string>();

    /// <summary>A one-line snippet of the body — how he recognises his own posts.</summary>
    public static string Snippet(SoMePost p, int max = 90)
    {
        var text = (p.EffectiveText ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        while (text.Contains("  ", StringComparison.Ordinal))
            text = text.Replace("  ", " ", StringComparison.Ordinal);
        return text.Length <= max ? text : text[..max].TrimEnd() + "…";
    }

    /// <summary>
    /// §889 — the state of one post, in his words. Kept identical to the editor's badge so the two
    /// pages cannot disagree about what a post IS.
    /// </summary>
    public static string StateOf(SoMePost p) =>
        p.Status == SoMePostStatus.Published ? "published"
        : p.IsActive ? "scheduled"
        : "planned";

    /// <summary>The post being previewed (when the Preview handler ran), else null.</summary>
    public SoMePostPreview? Preview { get; private set; }
    public int? PreviewPostId { get; private set; }

    // Edit fields.
    [BindProperty] public int PostId { get; set; }
    // §889 — EditText / EditImageRef / RescheduleAt are gone with their handlers: the editor owns
    // those fields now, and a bound property with no form behind it only invites a second route.
    [BindProperty] public bool SetActive { get; set; }

    private CurrentParticipant? Guard()
    {
        var me = _participant.Current;
        if (me is null) return null;
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return null; }
        return me;
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>
    /// 🔴 §1050 — NEW POST = A BLANK POST, OPENED IN THE EDITOR. ONE SOLUTION, NOT TWO.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-10, after asking for a preview and variables on the compose panel:
    /// <i>"why not make the new post part of the other edit option"</i> · <i>"instead of
    /// reengineering"</i> · <i>"so we have 1 solution"</i>. He is right, and it is the better design
    /// by some distance.</para>
    ///
    /// <para>The Post Editor ALREADY has everything the compose panel was about to grow: the live
    /// post preview, the variable colouring (§865.2(5)) that shows which runs are tokens, the
    /// graphic picker (§844.3), the schedule field, and approve/hold. Building a second preview and
    /// a second variable UI beside the list would have been two implementations of one idea, drifting
    /// apart from the day they shipped — which is the exact complaint §1036 makes about test flags.</para>
    ///
    /// <para>⚠️ This DOES reverse §834.5's <i>"CEH composes NOTHING here: no template, no variables"</i>
    /// — the ad-hoc post gains variables by virtue of being edited in the editor like any other. That
    /// was a deliberate decision then and it is his to change now; recorded rather than silently
    /// inverted.</para>
    ///
    /// <para>🔒 Born HELD and scheduled a day out, per §915.1. A blank post cannot publish: the
    /// dispatcher needs Active, and this is not.</para>
    /// </remarks>
    public async Task<IActionResult> OnPostNewPostAsync(CancellationToken ct)
    {
        var me = Guard();
        if (me is null) return AccessDenied ? Page() : RedirectToPage("/Login");

        // Tomorrow 09:00 UTC — a real slot he can move, not DateTime.MinValue or "now". A default in
        // the past would be a post that reads as overdue the moment it is created.
        var when = new DateTimeOffset(
            DateTime.UtcNow.Date.AddDays(1).AddHours(9), TimeSpan.Zero);

        var post = await _queue.CreateAdHocPostAsync(
            me.EventId, string.Empty, imageRef: null, when, tags: null, byEmail: me.Email, ct);

        // Straight into the editor, on the post just made — the whole point of the consolidation.
        return RedirectToPage("/Organizer/SoMePostEditor", new { id = post.Id });
    }

    // 🔒 §1050 — `OnPostAdHocAsync` IS GONE, and so are its four bound properties and this page's
    // graphics library. It composed a post — text, image and schedule — beside a page whose job is
    // finding one, and the moment it needed a PREVIEW and VARIABLES it was going to become a second
    // editor. "so we have 1 solution": creation now makes a blank post and hands it to the real
    // editor, so nothing here has to know how a post is composed.
    //
    // ⚠️ Removed rather than left unreachable. This page has been here before — the note below
    // records two handlers that outlived their forms, one of which was a live trap. An orphaned
    // POST handler is reachable by URL and by nothing else, which is the worst of both.

    // 🔒 §889 — EDITING AND RESCHEDULING LIVE IN THE EDITOR, and their handlers are gone from here.
    //
    // This page used to carry its own "fine-tune / reschedule" forms per row. With the list view
    // those forms are gone, which left two POST handlers reachable by URL and by nothing else —
    // and the edit one was a live trap: it wrote ManualTextOverride with no §907.2 guard, so it
    // could still turn an unchanged save into a permanent "edit" and strand a post exactly the way
    // #8764 was stranded. Two routes to one action, one of which had the fix and one of which did
    // not, is the shape that produced the bug in the first place. [[ceh-count-the-shared-things]]

    /// <summary>Toggle the Active/Inactive flag (an Inactive post never publishes).</summary>
    public async Task<IActionResult> OnPostToggleActiveAsync(CancellationToken ct)
    {
        var me = Guard();
        if (me is null) return AccessDenied ? Page() : RedirectToPage("/Login");

        // §850 — a refusal carries its REASON, so he is told what to fix rather than merely that
        // something is not allowed.
        var problem = await _queue.TrySetActiveAsync(me.EventId, PostId, SetActive, me.Email, ct);
        Message = problem
            ?? (SetActive ? "Post activated." : "Post deactivated (won't publish).");

        // 🔴 §1193 — AND IT IS SHOWN ON THE ROW, not only at the top of the page.
        //
        // Operator 2026-09-12: *"when i click activate nothing happens"* — on post #320454, six rows
        // into a 54-row list. The refusal was generated correctly and rendered in the page-level
        // message at the very TOP, which was scrolled off screen. He worked out the real reason
        // himself from elsewhere, which is the tell: the button looked broken.
        //
        // 🔑 This is §887.2 exactly, in a second place. That fixed the editor's date save for the
        // same reason — *"saving from down here looked like nothing happened at all"* — and the
        // lesson did not travel to the queue, where the distance between control and message is
        // larger still.
        ActionedPostId = PostId;
        ActionFailed = problem is not null;
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>Render the exact post that will publish (the Preview button).</summary>
    public async Task<IActionResult> OnPostPreviewAsync(CancellationToken ct)
    {
        var me = Guard();
        if (me is null) return AccessDenied ? Page() : RedirectToPage("/Login");

        Preview = await _queue.PreviewAsync(me.EventId, PostId, ct);
        PreviewPostId = PostId;
        if (Preview is null) Message = "Post not found.";
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    private async Task LoadAsync(int eventId, CancellationToken ct)
    {
        // 🔒 §853 — a DELETED post is a tombstone that stops the planner re-proposing it. It is not
        // queue content, and listing it puts a post he deleted back in front of him.
        var all = (await _queue.ListAsync(eventId, ct))
            .Where(p => !p.IsDeleted)
            .ToList();
        TotalPosts = all.Count;

        SubjectLabels = await _subjects.LabelsForAsync(eventId, all, ct);
        AwaitingGraphicPostIds = await _queue.AwaitingGraphicAsync(eventId, all, ct);

        // 🔴 §1206 — asked for the WHOLE edition, not the current page, so the summary line can say
        // how many are held back in total rather than how many happen to be in view. Only held posts
        // are asked about (see SoMeReadiness), which is a few dozen rows.
        NotReadyReasons = await _readiness.HeldReasonsAsync(all, ct);

        // §1209 — one query, so the Type column can tell 2a from 2b.
        _masterClassSessionIds = (await _queue.MasterClassSessionIdsAsync(eventId, ct)).ToHashSet();

        // §889 — search the BODY, because that is how he identifies a post. Case-insensitive and
        // substring: "500 tickets" should find it without him recalling the exact wording.
        // 🔑 The SUBJECT is searched too, so "azure" finds the Azure track posts — their bodies are
        // the shared template and contain the word nowhere.
        if (!string.IsNullOrWhiteSpace(Q))
        {
            var q = Q.Trim();
            all = all.Where(p =>
                    (p.EffectiveText ?? string.Empty).Contains(q, StringComparison.OrdinalIgnoreCase)
                    || (SubjectLabels.GetValueOrDefault(p.Id) ?? string.Empty)
                        .Contains(q, StringComparison.OrdinalIgnoreCase)
                    || p.Id.ToString().Contains(q, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (!string.IsNullOrWhiteSpace(State) && !State.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            all = all.Where(p => StateOf(p).Equals(State, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (Kind is not null)
        {
            all = all.Where(p => p.TemplateKind == Kind).ToList();
        }

        // 🔴 §1209 — and by CATEGORY, the same names the Type column prints. Applied AFTER the
        // master-class ids are loaded, or 2a and 2b could not be told apart.
        if (Category is not null)
        {
            all = all
                .Where(p => SoMeCategoryRules.CategoryOf(
                    p.TemplateKind, p.SubjectKey, _masterClassSessionIds) == Category)
                .ToList();
        }

        // §889 — sortable. Date is the default because the queue IS a calendar; the others exist so
        // he can group a type together or bring the un-approved ones to the top.
        all = (Sort ?? "date").ToLowerInvariant() switch
        {
            "id"    => all.OrderByDescending(p => p.Id).ToList(),
            "type"  => all.OrderBy(p => p.TemplateKind).ThenBy(p => p.ScheduledAtUtc).ToList(),
            "state" => all.OrderBy(p => StateOf(p)).ThenBy(p => p.ScheduledAtUtc).ToList(),
            // 🔴 §1169 — THE NEXT POST FIRST, not the start of the campaign. Operator 2026-09-03:
            // *"i also dont want to show all published at the top, but the next one"*. Plain
            // ascending opened the queue on August, with the thing he had to act on below the fold.
            // Upcoming nearest-first, then history newest-first — the same split §936 already made
            // in the editor, here applied to a list holding both.
            _       => SoMeQueueOrder.NextFirst(all, _clock.GetUtcNow()).ToList(),
        };

        Posts = all;
    }
}
