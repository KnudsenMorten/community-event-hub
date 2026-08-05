using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Organizer;

/// <summary>
/// §603 / §602.5 — which tasks are backed by a REAL ARTEFACT, and whether that artefact exists.
///
/// <para><b>Why this exists.</b> Operator 2026-07-28: *"'Mark complete' retired where an artefact
/// exists - agree"*. Today every task row renders a green <b>Mark complete</b> button, so a sponsor
/// can declare the sponsor-wall task done with no file uploaded — and the reminder engine believes
/// them. §598 is the same defect from the other side: he deleted the files in SharePoint and CEH
/// still reported them as uploaded.</para>
///
/// <para><b>The rule:</b> where a task's completion is VERIFIABLE from data, it is derived and the
/// self-declaration is removed. Where a task genuinely has no artefact ("read this", "join the
/// group" — external actions), an explicit acknowledgement stays, which is honest rather than a
/// fake upload record. That distinction is the whole point; a blanket removal would strand the
/// tasks that legitimately need a human to say "done".</para>
/// </summary>
public static class TaskArtefactRules
{
    /// <summary>
    /// The sponsor upload KIND a task is satisfied by, or null when the task has no artefact.
    /// </summary>
    /// <remarks>
    /// 🔒 MATCHED ON THE SOURCE-KEY SLUG, AND DELIBERATELY NARROW.
    ///
    /// <para>Sponsor task keys are <c>sponsor:{companyId}:{slug}</c>, where the slug is derived from
    /// the task TITLE (<c>SponsorOrderPullService</c>). There is no field on the task carrying its
    /// upload definition — that lives in <c>config/sponsor.&lt;edition&gt;.json</c> and is not
    /// persisted — so the slug is the only join available until §601 replaces the JSON task model
    /// altogether.</para>
    ///
    /// <para>It is kept to explicit, tested tokens rather than a loose "contains upload" heuristic:
    /// a false positive here would HIDE the completion control on a task that has no other way to be
    /// completed, stranding a sponsor. A false negative merely leaves today's behaviour in place,
    /// which is the safe direction to fail.</para>
    /// </remarks>
    public static string? UploadKindFor(ParticipantTask task)
    {
        var key = task.SourceKey;
        if (string.IsNullOrWhiteSpace(key)) return null;

        // 🔥 §708 / §707.57d — THE SPEAKER DECKS. The reason the speaker migration exists.
        //
        // Photographed on PROD: /Speaker/Tasks showing "Upload final presentation" with a ✓ done
        // badge while /Speaker showed "Final: not uploaded yet" for the same speaker at the same
        // moment. The task said done and the file did not exist — possible only because the task
        // was Manual and nothing checked. Naming a kind here is what removes the tick
        // (AllowsManualCompletion excludes artefact-backed tasks) so the badge follows the FILE.
        //
        // 🔒 Both slugs are short (25 and 27 chars) and matched on their DISTINCTIVE middle word,
        // not a tail. §707.54a is the warning: SponsorTaskKeys.Slug TRUNCATES at 60, so a marker
        // near the end of a long title is simply not in the stored key, and the rule then returns
        // false forever, silently, in the permissive direction.
        if (key.StartsWith("speakerdl:", StringComparison.OrdinalIgnoreCase))
        {
            var speakerSlug = key[(key.LastIndexOf(':') + 1)..];
            if (speakerSlug.Contains("preview-presentation", StringComparison.OrdinalIgnoreCase))
                return "preview";
            if (speakerSlug.Contains("final-presentation", StringComparison.OrdinalIgnoreCase))
                return "final";
            return null;
        }

        if (!key.StartsWith("sponsor:", StringComparison.OrdinalIgnoreCase)) return null;

        var slug = key[(key.LastIndexOf(':') + 1)..];

        // The exhibitor/sponsor WALL artwork. Its versioned uploader already exists
        // (SponsorUploadKinds "wall" → DocLibraryPaths.SponsorExhibitorWall, named
        // <sponsor>-exhibitor-wall-<N>.<ext>), so the artefact is recorded as a SponsorUploadAudit
        // row and completion becomes checkable.
        if (slug.Contains("sponsor-wall", StringComparison.OrdinalIgnoreCase)
            || slug.Contains("wall-design", StringComparison.OrdinalIgnoreCase))
            return "wall";

        return null;
    }

    /// <summary>True when this task's completion can be derived from an uploaded artefact.</summary>
    public static bool IsArtefactBacked(ParticipantTask task) => UploadKindFor(task) is not null;

    /// <summary>
    /// The set of artefact KINDS this company has actually uploaded, from the §68 upload audit.
    /// One query for a whole page — call once and pass the result to <see cref="HasArtefact"/>.
    /// </summary>
    public static async Task<IReadOnlySet<string>> UploadedKindsAsync(
        CommunityHubDbContext db, int eventId, string? companyId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(companyId))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // §598 — an artefact a COMPLETE SharePoint read confirmed is gone does NOT count. Without
        // this filter the verifier's stamp would change nothing: the operator deleted the files by
        // hand and the task still read DONE, which is the defect being fixed.
        var kinds = await db.SponsorUploadAudits.AsNoTracking()
            .Where(a => a.EventId == eventId
                        && a.SponsorCompanyId == companyId
                        && a.ArtefactMissingAt == null)
            .Select(a => a.Kind)
            .Distinct()
            .ToListAsync(ct);

        return kinds.Where(k => !string.IsNullOrWhiteSpace(k))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether the artefact backing this task exists. False for a task that needs one and has none —
    /// which is exactly the state the old self-declared "Mark complete" could hide.
    /// </summary>
    public static bool HasArtefact(ParticipantTask task, IReadOnlySet<string> uploadedKinds)
    {
        var kind = UploadKindFor(task);
        return kind is not null && uploadedKinds.Contains(kind);
    }

    /// <summary>
    /// §602.5 — should the row offer a manual complete/reopen control at all?
    /// <c>false</c> for artefact-backed tasks: their state follows the file.
    /// </summary>
    /// <remarks>
    /// §648 — a FORM-backed task is excluded for the same reason: its state follows what was
    /// actually submitted. Leaving the button would let a sponsor declare "Submit session
    /// description" done with the form untouched — exactly the lie §603 removed from the upload
    /// tasks, and §598 is the same lie from the other side (a deleted file still reading as sent).
    /// </remarks>
    public static bool AllowsManualCompletion(ParticipantTask task) =>
        !IsArtefactBacked(task)
        && string.IsNullOrWhiteSpace(task.FormStepKey)
        && !IsDecisionBacked(task)
        && !IsPurchaseBacked(task);

    /// <summary>
    /// §687.8 — true when this task completes because the sponsor actually ORDERED something.
    /// </summary>
    /// <remarks>
    /// Operator 2026-07-29: <i>"in case there is a real dependency to this, then dont show the mark
    /// completed state and use this validation as the real tasks is completed or not"</i>. The
    /// booth-layout task is the case — its own copy says the order is what reserves the furniture,
    /// so a self-declared tick means an empty booth on the day.
    ///
    /// <para>🔒 Matched on the SOURCE-KEY SLUG, like the sibling rules above and for the same
    /// reason: the row carries no pointer to its definition. Narrow and explicit on purpose.</para>
    /// </remarks>
    public static bool IsPurchaseBacked(ParticipantTask task)
    {
        var key = task.SourceKey;
        if (string.IsNullOrWhiteSpace(key)) return false;
        if (!key.StartsWith("sponsor:", StringComparison.OrdinalIgnoreCase)) return false;

        var slug = key[(key.LastIndexOf(':') + 1)..];

        // "Choose your booth layout (table, chairs)".
        return slug.Contains("booth-layout", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// §670 / §676 — true when this task completes by ANSWERING rather than by ticking.
    /// </summary>
    /// <remarks>
    /// <para>Operator §670, on the two buttons: <i>"mark complete will automatically be set once they
    /// choose one of the 2 buttons, so no need to show that"</i>. A "Mark complete" beside them is
    /// a third way to close the task that records NO answer — leaving the organizer with a done task
    /// and no idea whether the sponsor is contributing, which is precisely the gap §670 exists to
    /// close.</para>
    ///
    /// <para>🔒 Matched on the SOURCE-KEY SLUG, exactly like <see cref="UploadKindFor"/> and for the
    /// same reason: the task row carries no pointer to its definition. Deliberately narrow and
    /// explicit — a false positive would hide the completion control from a task that has no other
    /// way to finish, so the tokens are the two migrated titles' slugs and nothing else.</para>
    /// </remarks>
    public static bool IsDecisionBacked(ParticipantTask task)
    {
        var key = task.SourceKey;
        if (string.IsNullOrWhiteSpace(key)) return false;
        if (!key.StartsWith("sponsor:", StringComparison.OrdinalIgnoreCase)) return false;

        var slug = key[(key.LastIndexOf(':') + 1)..];

        // "Event App Game - Extra Exposure Opportunity" (§670) and
        // "Brochures … for {{expectedAttendees}} attendee bags" (§676).
        //
        // 🔴 §707.54 — MATCH THE START OF THE TITLE, NOT ITS TAIL. `SponsorTaskKeys.Slug` TRUNCATES
        // at 60 characters, and the attendee-bag title is longer than that: the stored key ends
        // "…-for-expectedattendees-a", so `attendee-bags` was NEVER present and this rule silently
        // returned false for every one of those rows. The consequence was the §603 lie — a DECISION
        // task offering "Mark complete", letting a sponsor tick it without answering either button.
        // Operator 2026-07-30 saw exactly that.
        //
        // 🔑 Same root cause as §707.43 (a {{token}} in that same title), and the same lesson: a
        // long title plus a truncating slug means any marker near the END of the title is not there.
        // `brochures` is the FIRST word, so it survives truncation. `attendee-bags` is kept for rows
        // seeded from a shorter historic title.
        return slug.Contains("event-app-game", StringComparison.OrdinalIgnoreCase)
               || slug.Contains("attendee-bags", StringComparison.OrdinalIgnoreCase)
               || slug.StartsWith("brochures", StringComparison.OrdinalIgnoreCase);
    }
}
