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
    // §932 — removed: the credit is an ordinary token, so it needs no property of its own.

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

    /// <summary>§1218 — offered when this post's master class / panel has exactly one linked speaker.</summary>
    public SoMeApprovalGate.SingleSpeakerOverride? SingleSpeaker { get; private set; }

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
    /// <summary>
    /// §1182 — what ELSE is already booked on a given day, so a reschedule is not a guess.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-09-12: <i>"when i reschedule something, it would be great to see in the
    /// date picker if other things have been planned for a date, so i dont overlap"</i>.</para>
    ///
    /// <para>⚠️ <b>The native date picker is browser chrome and cannot be annotated</b> — there is no
    /// API to paint a count onto a day cell in <c>&lt;input type="datetime-local"&gt;</c>. So the
    /// occupancy is shown BESIDE the field instead, updating as he picks: the same answer, in the
    /// only place the platform allows it to be drawn.</para>
    ///
    /// <para>🔑 Built from the rows <c>LoadAsync</c> ALREADY reads for the walk order, so this costs
    /// no extra query. Keyed by Danish local date, because that is the day he is choosing — a UTC
    /// key would put an 09:00 post on the wrong side of midnight twice a year.</para>
    ///
    /// <para>🔒 <b>PUBLISHED posts are included and marked.</b> §1144 treats them as obstacles for
    /// exactly this reason: their slot is spent. Hiding them would show a day as free that is not.</para>
    /// </remarks>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> DayLoad { get; private set; } =
        new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>§842.7/§843.3 — the everyday per-day ceiling, so "2 of 2" means something.</summary>
    public int MaxPostsPerDay { get; private set; } = 2;

    public IReadOnlyDictionary<SoMeTemplateKind?, int> CountByKind { get; private set; } =
        new Dictionary<SoMeTemplateKind?, int>();

    /// <summary>Where this post sits in the queue — "12 of 112".</summary>
    public int Position { get; private set; }
    public int Total { get; private set; }

    public int? PreviousId { get; private set; }
    public int? NextId { get; private set; }

    /// <summary>§861 — the edition code, so the credit block can be composed and recognised.</summary>
    public string? EditionCode { get; private set; }

    // §932 — removed: the credit is part of the BODY now, shown in the text box and the preview
    //   like every other token. A separate read-only display of one variable is the drift the
    //   operator called out: "we have 10+ variables and each of them must be handled the same way".

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
        // 🔴 §932 — THE CREDIT IS STORED WITH THE BODY, LIKE EVERY OTHER TOKEN. Operator
        // 2026-08-07: *"credit must NOT live outside"*.
        //
        // ⚠️ THIS LINE PUBLISHED A POST WITH NO ORGANIZER CREDIT. §861 stripped the credit on save
        // because back then the publisher stapled it on afterwards. §888.3 reversed that on
        // 2026-08-06 07:59 — *"remove the crap you build for organizer and make it as a variable
        // like others"* — and put the token INTO the body, but this strip stayed behind. So from
        // that morning every save quietly deleted the credit out of the stored body while the
        // PREVIEW went on re-appending it for display.
        //
        // ⇒ What he approved and what published were different documents. Post 495, edited 07:44
        // that morning, kept its credit; 6345 and 496, edited at 18:02, lost it, and 6345 went to
        // the company page bare at 07:00 the next day.
        //
        // 🔒 The body is now saved EXACTLY as he wrote it. Preview and publish read the same text
        // through the same resolver, so they cannot disagree again — which is the only durable fix.
        var text = (EditText ?? string.Empty).Trim();

        // 🔴 §907.2 — SAVING WITHOUT CHANGING ANYTHING MUST NOT CREATE AN "EDIT".
        //
        // An override is what makes a post untouchable to the planner (§848.2 skips any row with
        // one). So a Save on an UNCHANGED post silently detaches it from the engine for ever —
        // and on 2026-08-06 that turned a display bug into a stuck row: post 8764 showed
        // "{EventPostBody}" (the §907 regression), he pressed Save, and the plumbing became his
        // "edit". The post was then broken BECAUSE it was protected, and protected BECAUSE it was
        // broken. It could not heal when the planner was fixed, and had to be repaired by hand.
        //
        // 🔒 Identical text ⇒ no override, exactly as if he had cleared the box. An edit is a
        // DIFFERENCE, not a button press.
        var composed = post.AutoText?.Trim() ?? string.Empty;
        var unchanged = string.Equals(text, composed, StringComparison.Ordinal);

        post.ManualTextOverride = text.Length == 0 || unchanged ? null : text;

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

        // ⚠️ IsActive is deliberately NOT touched here. Approving is its own act (§887).
        var changed = _db.ChangeTracker.HasChanges();
        await _db.SaveChangesAsync(ct);

        // 🔒 §887.2 — SAY WHAT WAS SAVED, not just that something was. Operator 2026-08-06: *"make a
        // note when i click save so i can see it actually saved something"*. A bare "Saved." renders
        // at the TOP of a long page, so pressing Save beside the date field appeared to do nothing —
        // the confirmation was real but off-screen. Naming the values makes the save VERIFIABLE:
        // if the date echoed back is not the one he typed, he can see that immediately.
        var savedWhen = SoMeDisplayTime.ToDanish(post.ScheduledAtUtc)
            .ToString("ddd dd MMM yyyy 'at' HH:mm");
        Message = changed
            ? $"Saved at {DateTimeOffset.UtcNow:HH:mm} — posting date & time is now {savedWhen} "
              + $"(Danish time), and the post text was saved ({(post.ManualTextOverride?.Length ?? 0)} characters)."
            : $"Nothing had changed — this post is already saved as {savedWhen} (Danish time).";

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
    /// 🔑 <b>ACCEPTING APPROVES (operator 2026-08-06).</b> It used to settle only the slot, leaving
    /// <c>IsActive</c> — the publish gate — untouched, so an accepted post still read
    /// "PLANNED (not approved)" and the only approve control was on a different page. He reported
    /// both as bugs on the same post: *"i have no approve buton anymore"* and *"this is wrong as it
    /// is now scheduled (approved)"*.
    /// <para>⚠️ §872 split these two axes deliberately and the distinction is real — a slot lock and
    /// a publish gate ARE different questions. But he makes ONE decision while walking the queue, and
    /// a second switch on another page is not a distinction he asked for. So accepting now does both,
    /// and handing back withdraws both.</para>
    /// <para>🔒 The §850 readiness guard still binds: a post that is not ready gets its slot locked
    /// but is NOT approved, and the refusal says why. Locking a date is harmless; publishing a post
    /// whose sponsor has not delivered their text is not.</para>
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

            if (state == SoMePostPlanState.Scheduled)
            {
                // §850 — approve only if it is actually ready. The slot lock is safe either way;
                // the publish gate is not.
                var blocked = await _approvalGate.BlockedReasonAsync(post, ct);
                if (blocked is null)
                {
                    post.IsActive = true;
                    Message = "Accepted and approved — locked, and it will publish at its scheduled time.";
                }
                else
                {
                    // 🔒 Say what is missing. A silent "accepted" on a post that still cannot publish
                    // is precisely the confusion he reported.
                    post.IsActive = false;
                    Message = $"Slot locked, but NOT approved — {blocked}";
                    MessageIsError = true;
                }
            }
            else
            {
                // Handing back withdraws the approval too: it is a proposal again, and a proposal
                // that could still publish would be the same disagreement in reverse.
                post.IsActive = false;
                Message = "Handed back to the planner. It may be moved or replaced on the next run, "
                        + "and it will not publish until you accept it again.";
            }

            post.UpdatedAt = DateTimeOffset.UtcNow;
            post.LastUpdatedByEmail = me.Email;
            await _db.SaveChangesAsync(ct);
        }

        await LoadAsync(me.EventId, id, ct);
        return Page();
    }

    /// <summary>
    /// §1218 — confirm (or un-confirm) that this post's master class / panel has ONE speaker, which
    /// lifts the §1060(m) co-presented blocker for that session.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-09-13: <i>"we need a override button as we actually have one master class
    /// … where there will be only one speaker. add the button in the planner so we can release the
    /// some post"</i>.</para>
    ///
    /// <para>🔒 <b>It confirms; it does not approve.</b> The flag lives on the SESSION (so every post
    /// for it is covered, including ones planned later), and approval stays its own click — the
    /// other gates still get their say.</para>
    /// </remarks>
    public async Task<IActionResult> OnPostConfirmSingleSpeakerAsync(
        int id, bool confirm, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var post = await _db.SoMePosts
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id && p.EventId == me.EventId, ct);

        // Asked through the gate, so the button can only ever act on a session the rule applies to.
        var target = post is null ? null : await _approvalGate.SingleSpeakerOverrideAsync(post, ct);
        var session = target is null
            ? null
            : await _db.Sessions.FirstOrDefaultAsync(
                s => s.Id == target.SessionId && s.EventId == me.EventId, ct);

        if (session is null)
        {
            Message = "That post is not about a master class or panel with exactly one linked speaker, "
                    + "so there is nothing to confirm.";
            MessageIsError = true;
        }
        else
        {
            session.SoMeSingleSpeakerConfirmed = confirm;
            session.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);

            Message = confirm
                ? $"Confirmed — \"{session.Title}\" has one speaker, so it is no longer blocked for "
                  + "that. Approve the post when it is ready; any other blocker still applies."
                : $"Confirmation removed — \"{session.Title}\" is blocked again until a second speaker "
                  + "is linked. Already-approved posts for it will not publish.";
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
    /// <summary>
    /// §913 — DUPLICATE THIS POST. Operator 2026-08-06: *"request for feature duplication post
    /// button"*.
    /// </summary>
    /// <remarks>
    /// <para>🔑 <b>The copy is HIS post from birth, not a planner proposal.</b> A duplicate that
    /// arrived as <c>Proposed</c> would be <b>discarded by the very next planning tick</b> (§848.2
    /// deletes un-accepted proposals) — the one outcome that would make the button look broken.
    /// So it is created <c>Accepted</c>, and with <c>AutoGenerated = false</c>: nobody planned it.</para>
    ///
    /// <para>🔒 <b>It carries no SUBJECT.</b> Keeping the source's <c>SubjectKey</c> would put two
    /// posts on the same (subject, occurrence) — a pair the planner's "have I already planned this?"
    /// lookup counts as one, and §824.2E's placement assumes it is one. A duplicate is therefore an
    /// ad-hoc post in the §834.5 sense: his own words, standing alone.</para>
    ///
    /// <para>⚠️ <b>Which means the copy is FLATTENED, deliberately.</b> With no subject, a variable
    /// like <c>{SessionTitle}</c> has nothing to resolve against and would publish as literal text.
    /// The body is therefore resolved AS IT READS TODAY and stored as words. That is the honest
    /// trade and the reason it is stated in the confirmation message: a duplicate is a snapshot, not
    /// a second live copy.</para>
    ///
    /// <para>🔒 <b>Never published, never approved, never scheduled.</b> Status, publish time and
    /// external id are all left clean — a duplicate of a post that has already gone out is a NEW,
    /// unpublished post, and copying the external id would make CEH believe it had already been
    /// sent. It arrives HELD (§824.8 Q2) with <b>no date</b>, so it cannot publish until he gives it
    /// one.</para>
    /// </remarks>
    public async Task<IActionResult> OnPostDuplicateAsync(int id, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var source = await _db.SoMePosts
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id && p.EventId == me.EventId, ct);

        if (source is null)
        {
            Message = "That post no longer exists.";
            MessageIsError = true;
            await LoadAsync(me.EventId, null, ct);
            return Page();
        }

        // Resolved NOW — see the flattening note above.
        var values = await _composer.ValuesForAsync(source, ct);
        var text = _composer.Resolve(source.EffectiveText, values);

        var copy = new CommunityHub.Core.Domain.SoMePost
        {
            EventId = source.EventId,
            Type = CommunityHub.Core.Domain.SoMePostType.AdHoc,
            // 🔒 No TemplateKind and no SubjectKey: it belongs to nothing, so the planner ignores it.
            ManualTextOverride = text,
            AutoText = string.Empty,
            ImageRef = source.ImageRef,
            MediaKind = source.MediaKind,
            Tags = source.Tags,
            AutoGenerated = false,
            // 🔒 `Scheduled` is the "he has accepted it" state — the planner never re-plans, moves
            // or removes such a post. NOT `Proposed`, which the next tick would delete.
            PlanState = CommunityHub.Core.Domain.SoMePostPlanState.Scheduled,
            IsActive = false,
            Status = CommunityHub.Core.Domain.SoMePostStatus.Queued,
            // ⚠️ Deliberately the SOURCE's slot, so the copy is not lost in the queue — but held,
            // so the date is a starting point he changes rather than a publication he did not ask
            // for. §889.1: a post whose time has passed publishes the moment it is approved.
            ScheduledAtUtc = source.ScheduledAtUtc,
            CreatedAt = DateTimeOffset.UtcNow,
            LastUpdatedByEmail = me.Email,
        };

        _db.SoMePosts.Add(copy);
        await _db.SaveChangesAsync(ct);

        Message = $"Duplicated as post #{copy.Id} — held, and yours to edit. Its text was copied as "
                + "WORDS (the variables are filled in as they read today), because a copy belongs to "
                + "no session or sponsor. Give it a date before approving it.";

        return RedirectToPage(new { id = copy.Id });
    }

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
        // 🔴 §932 — NOTHING SPECIAL IS READ FOR THE CREDIT HERE ANY MORE.
        //
        // Operator 2026-08-07: *"no difference between {organizers} and {speakers}"* — and he is
        // right down to the mentions: `{Organizers}` resolves through the SAME mention pipeline as
        // `{Speakers}` (§888.3), so the one thing that looked like a reason to special-case it never
        // was one. `{Organizers}` is resolved by the ordinary resolver, coloured as a variable by
        // the ordinary Segments(), and published by the ordinary publisher.

        // The walk order IS the campaign order, so previous/next moves through time — the way he
        // reads the calendar. Id breaks ties so the order is stable across loads.
        // §853 — deleted posts are gone from the walk. The row survives only to stop the planner
        // re-proposing the subject; it is not something he steps through.
        //
        // 🔑 §936 — EXCEPT FOR PUBLISHED, WHICH READS NEWEST FIRST. Operator 2026-08-07: *"when i
        // filter on fx to Published, i want the sorting to show the most recent first"*.
        //
        // The two filters answer opposite questions. Planned and Scheduled are about what is COMING:
        // the next thing to deal with is the nearest one, so ascending puts it first. Published is a
        // HISTORY — you look at what just went out, not at what went out in June — so the newest
        // belongs at the top. Same list, reversed, because "first" means something different in each.
        var newestFirst = StateFilter == SoMePostState.Published;

        var ordered = _db.SoMePosts
            .Where(p => p.EventId == eventId && !p.IsDeleted);

        var all = await (newestFirst
                ? ordered.OrderByDescending(p => p.ScheduledAtUtc).ThenByDescending(p => p.Id)
                : ordered.OrderBy(p => p.ScheduledAtUtc).ThenBy(p => p.Id))
            .Select(p => new
            {
                p.Id, p.SponsorCompanyId, p.TemplateKind, p.SubjectKey,
                // §872 — the state filter reads these two, so they come back in the same query
                // rather than a second pass over 84 rows.
                p.IsActive, p.Status,
                // §1169 — needed to RE-order once the state is final: an explicit id can change
                // StateFilter below, and §936's direction depends on it.
                p.ScheduledAtUtc,
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

        // 🔑 §1182 — the day map, from the rows already in hand. The post being EDITED is excluded:
        // it is not an obstacle to itself, and counting it would tell him a day holds one more post
        // than it does the moment he lands on it.
        MaxPostsPerDay = await _db.SoMeSettings
            .Where(s => s.EventId == eventId)
            .Select(s => s.MaxPostsPerDay)
            .FirstOrDefaultAsync(ct) is var cap && cap > 0 ? cap : 2;

        DayLoad = all
            .Where(p => p.Id != id)
            .GroupBy(p => SoMeDisplayTime.ToDanish(p.ScheduledAtUtc).ToString("yyyy-MM-dd"))
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g
                    .OrderBy(p => p.ScheduledAtUtc)
                    .Select(p =>
                    {
                        var time = SoMeDisplayTime.ToDanish(p.ScheduledAtUtc).ToString("HH:mm");
                        var kind = p.TemplateKind is { } k
                            ? SoMeAnnouncementQuery.KindLabelFor(k)
                            : "Written by hand";
                        // The STATE matters as much as the count: a published slot is spent, a held
                        // one may still move.
                        var state = p.Status == SoMePostStatus.Published ? "published"
                            : p.IsActive ? "approved"
                            : "held";
                        return $"{time} · #{p.Id} {kind} ({state})";
                    })
                    .ToList());

        // §851.2 — how many of each type exist, counted BEFORE any filter so the picker can say what
        // choosing it would show.
        //
        // 🔴 §1180 — A POST WITH NO TEMPLATE KIND TOOK THE WHOLE EDITOR DOWN.
        //
        // Operator 2026-09-12: *"duplicating existing publish post givs error"* — HTTP 500 on
        // /Organizer/SoMePostEditor?id=319752, from `ArgumentNullException: Value cannot be null.
        // (Parameter 'key')` right here.
        //
        // 🔑 `Dictionary<TKey,TValue>` REFUSES A NULL KEY, and a nullable-enum key type does not
        // change that — `Dictionary<SoMeTemplateKind?, int>` compiles, accepts a declared null key
        // type, and throws the moment one arrives. `ToDictionary` over a GroupBy that produced a
        // null group therefore blows up, and the type system cannot warn about it.
        //
        // ⚠️ **The blast radius is the point.** `all` is EVERY post in the edition, so ONE post with
        // no kind 500s the editor for EVERY post — not just its own. Two ordinary actions create
        // one: Duplicate (§1050: *"No TemplateKind and no SubjectKey: it belongs to nothing"*) and
        // the New-post button, whose ad-hoc row sets `Type` and leaves `TemplateKind` null. The
        // editor has been one ad-hoc post away from total failure since both shipped.
        //
        // 🔒 Dropped rather than bucketed: the picker below enumerates the FIVE real kinds and never
        // asks for null, so the null group had no reader and existed only to break the dictionary.
        CountByKind = all
            .Where(p => p.TemplateKind is not null)
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
        // 🔴 §1169 — AN EXPLICIT id BEATS THE STATE FILTER. Operator 2026-09-03, clicking Edit on a
        // PUBLISHED post in the queue: *"it does not open the actual"*.
        //
        // The queue's Edit link carries only the id, and this page defaults to Planned (§872). So a
        // published post was filtered out of the walk below, the id matched nothing, and the walk
        // fell back to ids[0] — the first PLANNED post. He pressed Edit on one row and got another,
        // with nothing saying why.
        //
        // 🔑 The escape hatch further down already covers exactly this for ELIGIBILITY ("show it
        // rather than bouncing him somewhere else without explanation"). The STATE filter simply
        // was not part of it, because it is applied here — before that hatch can see the post.
        //
        // ⇒ When he names a post, the filter follows the post rather than the post being discarded
        // by the filter.
        if (id is { } wantedId)
        {
            var wantedState = all
                .Where(p => p.Id == wantedId)
                .Select(p => (SoMePostState?)StateOf(p.IsActive, p.Status))
                .FirstOrDefault();

            if (wantedState is { } s && s != StateFilter) StateFilter = s;
        }

        all = all.Where(p => StateOf(p.IsActive, p.Status) == StateFilter).ToList();

        // 🔒 §1169 — RE-ORDER, because the direction was chosen from the state filter BEFORE the
        // line above could change it. §936: Planned and Scheduled are about what is coming (nearest
        // first); Published is a history (newest first). Landing on a published post while the walk
        // still ran oldest-first would step him backwards through June.
        var finalNewestFirst = StateFilter == SoMePostState.Published;
        if (finalNewestFirst != newestFirst)
        {
            all = (finalNewestFirst
                    ? all.OrderByDescending(p => p.ScheduledAtUtc).ThenByDescending(p => p.Id)
                    : all.OrderBy(p => p.ScheduledAtUtc).ThenBy(p => p.Id))
                .ToList();
        }

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

            // 🔴 §932 — THE BOX SHOWS THE WHOLE BODY, credit token included. §861 hid it here to
            // stop it FREEZING as literal words; §888.3 solved that properly by making it a token,
            // so hiding it now only guarantees that saving deletes it.
            //
            // 🔑 §861.3's real worry is answered by the TOKEN, not by concealment: `{Organizers}`
            // resolves at publish, so it still picks up the §858 mention upgrade no matter when the
            // post was written or edited.
            EditText = Post.EffectiveText;
            ImageRef = Post.ImageRef;

            // §865.2(5) — resolve the body's {tokens} NOW so the preview shows real values, while
            // still marking which runs are variables. Same resolver the publisher uses (§864), so
            // what he reads here is what will go out.
            var values = await _composer.ValuesForAsync(Post, ct);
            AvailableTokens = SoMePostComposer.TokensFor(Post.TemplateKind);

            // 🔴 §932 — THE PREVIEW SHOWS THE BODY. NOTHING IS ADDED TO IT.
            //
            // ⚠️ This append is the other half of the defect. It made the preview show a credit that
            // the stored body did not contain, so "exactly what publishes" was a promise the page
            // could not keep — he approved a post with an organizer line and a post without one went
            // out. A preview that adds anything is a preview of a different document.
            //
            // 🔒 Preview and publish now render the SAME text through the SAME resolver, so the only
            // way to see a credit here is for the body to actually contain the token.
            PreviewSegments = SoMePostComposer.Segments(EditText, values);
            // §844.5 — shown and edited in DANISH wall-clock; stored as UTC.
            ScheduledAt = SoMeDisplayTime.ToDanish(Post.ScheduledAtUtc).DateTime;
            VideoSupported = SoMeGraphicLibrary.SupportsVideo(Post.TemplateKind);

            // §850 — why this one cannot be approved, shown on the post rather than only on refusal.
            BlockedReason = await _approvalGate.BlockedReasonAsync(Post, ct);
            SingleSpeaker = await _approvalGate.SingleSpeakerOverrideAsync(Post, ct);

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
