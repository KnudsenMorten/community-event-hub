using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Forms;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Forms;

/// <summary>
/// The generic in-wizard STEPPER host (REQUIREMENTS §148). A true one-step-at-a-time
/// wizard: it asks the role's existing wizard SERVICE for the ordered, entitlement-gated,
/// done-marked step plan (<see cref="SpeakerWizardService"/> for speakers, otherwise
/// <see cref="RoleWizardService"/>), picks the current step (from <c>?step=</c> or the first
/// incomplete one), and renders THAT step's fields INLINE — the step's own
/// <see cref="IWizardStepHandler"/> loads its model and the host drops the handler's
/// fields-partial inside ONE <c>&lt;form method="post"&gt;</c> with a progress bar +
/// Previous / Save&amp;next / Finish.
///
/// <para>On POST the plan is REBUILT (stateless — re-reads data, so refresh / re-entry is
/// always correct), then the step's handler runs the form's REAL validate + persist +
/// side-effects via its shared service: <see cref="WizardStepOutcome.Advance"/> → PRG to the
/// next incomplete step (or the hub when done); <see cref="WizardStepOutcome.Invalid"/> →
/// re-render the SAME step with field errors; <see cref="WizardStepOutcome.NotRelevant"/> →
/// skip forward. The host owns NO form logic.</para>
///
/// <para>Sponsors keep their bespoke <c>/Sponsor/GetStarted</c> (their steps embed sections of
/// the single Company Details page and share files), so they are redirected out of here.</para>
/// </summary>
[Authorize]
public class WizardModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly SpeakerWizardService _speaker;
    private readonly RoleWizardService _role;
    private readonly Dictionary<string, IWizardStepHandler> _handlers;

    public WizardModel(
        ICurrentParticipantAccessor participant,
        SpeakerWizardService speaker,
        RoleWizardService role,
        IEnumerable<IWizardStepHandler> handlers)
    {
        _participant = participant;
        _speaker = speaker;
        _role = role;

        // Key every discovered handler by its stable Key. In DEBUG a duplicate key is a
        // wiring bug (two handlers claim the same step) — fail loudly; in release last wins.
        _handlers = new(StringComparer.Ordinal);
        foreach (var h in handlers)
        {
#if DEBUG
            if (_handlers.ContainsKey(h.Key))
                throw new InvalidOperationException(
                    $"Duplicate IWizardStepHandler.Key '{h.Key}' ({h.GetType().FullName}). Each wizard step key must be unique.");
#endif
            _handlers[h.Key] = h;
        }
    }

    /// <summary>One normalized step (across the speaker / role wizard view shapes).</summary>
    public sealed record PlanStep(string Key, string Route, bool Done);

    /// <summary>The normalized, ordered, gated, done-marked plan the host renders against.</summary>
    public sealed class WizardPlan
    {
        public required string ResxPrefix { get; init; }   // "SpeakerWiz" | "RoleWiz" → Step.<key> labels
        public required IReadOnlyList<PlanStep> Steps { get; init; }
        public int EntitledCount => Steps.Count;
        public int DoneCount => Steps.Count(s => s.Done);
        public int Percent => EntitledCount == 0 ? 0 : (int)Math.Round(100.0 * DoneCount / EntitledCount);
        public bool AllDone => EntitledCount > 0 && DoneCount >= EntitledCount;
        public PlanStep? NextStep => Steps.FirstOrDefault(s => !s.Done);
        public int IndexOf(string key)
        {
            for (var i = 0; i < Steps.Count; i++)
                if (string.Equals(Steps[i].Key, key, StringComparison.Ordinal)) return i;
            return -1;
        }
    }

    // ----- render state (populated by OnGet / OnPost-invalid) -------------
    public bool AccessDenied { get; private set; }
    public bool NothingToDo { get; private set; }
    public bool ShowAllDone { get; private set; }
    public WizardPlan? Plan { get; private set; }
    public int CurrentIndex { get; private set; }
    public string FullName { get; private set; } = string.Empty;

    public PlanStep? CurrentStep => Plan is not null && CurrentIndex >= 0 && CurrentIndex < Plan.Steps.Count
        ? Plan.Steps[CurrentIndex] : null;

    /// <summary>The handler for the current step, or null when no inline handler is registered yet
    /// (during rollout): the view then falls back to a link to the step's standalone page.</summary>
    public IWizardStepHandler? CurrentHandler { get; private set; }

    /// <summary>1-based position of the current step (for "Step X of N").</summary>
    public int CurrentStepNumber => CurrentIndex + 1;
    public bool IsFirstStep => CurrentIndex <= 0;
    public bool IsLastStep => Plan is not null && CurrentIndex == Plan.Steps.Count - 1;

    public async Task<IActionResult> OnGetAsync(string? step, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        // Sponsors keep their bespoke wizard (shared Company Details page).
        if (me.Role == ParticipantRole.Sponsor) return RedirectToPage("/Sponsor/GetStarted");

        FullName = me.FullName;
        Plan = await BuildPlanAsync(me, ct);
        // A null plan means the role simply has no generic wizard (e.g. Attendee) — that is NOT a
        // denial, so show the neutral "nothing to set up" copy, not the speaker-only access-denied
        // string (which misleadingly told attendees they "do not have a speaker profile").
        if (Plan is null) { NothingToDo = true; return Page(); }
        if (Plan.EntitledCount == 0) { NothingToDo = true; return Page(); }

        // Current step: an in-plan ?step= wins (so completed steps are revisitable);
        // otherwise the first incomplete step; otherwise everything is done.
        var idx = !string.IsNullOrEmpty(step) ? Plan.IndexOf(step) : -1;
        if (idx < 0) idx = Plan.NextStep is { } n ? Plan.IndexOf(n.Key) : -1;
        if (idx < 0) { ShowAllDone = true; return Page(); }

        CurrentIndex = idx;
        CurrentHandler = ResolveHandler(Plan.Steps[idx].Key);
        if (CurrentHandler is not null)
            await CurrentHandler.LoadAsync(BuildContext(me, ct));

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role == ParticipantRole.Sponsor) return RedirectToPage("/Sponsor/GetStarted");

        FullName = me.FullName;

        // Stateless: rebuild the plan from current data so re-entry / refresh is always correct.
        Plan = await BuildPlanAsync(me, ct);
        // Null plan = role has no generic wizard (not a denial) → neutral "nothing to do" copy.
        if (Plan is null) { NothingToDo = true; return Page(); }
        if (Plan.EntitledCount == 0) return RedirectToPage("/Index");   // nothing left to do

        var stepKey = Request.Form["__step"].ToString();
        var dir = Request.Form["__dir"].ToString();
        var idx = string.IsNullOrEmpty(stepKey) ? -1 : Plan.IndexOf(stepKey);

        // Unknown / stale step → restart at the first incomplete step (or hub).
        if (idx < 0) return RedirectToStepOrHub(Plan.NextStep);

        // Previous: no save, just move back one step (completed steps stay revisitable).
        if (dir == "prev")
            return RedirectToStep(Plan.Steps[Math.Max(0, idx - 1)].Key);

        // Next / Save.
        var handler = ResolveHandler(stepKey);
        if (handler is null)
            // No inline handler yet (rollout) → behave as "skip for now": advance.
            return AdvanceFrom(idx);

        var outcome = await handler.SaveAsync(BuildContext(me, ct));
        switch (outcome)
        {
            case WizardStepOutcome.Advance:
                // Move FORWARD to the next step IN SEQUENCE — NOT the first-incomplete step.
                // The first-incomplete can be THIS step again when "done" needs more than this
                // save provides (e.g. Profile is "done" only once a phone is present, yet you
                // may Save & next without one) — which made Save & next silently loop back to
                // the same step ("nothing happens"). Completed/skipped steps stay revisitable
                // via the breadcrumb; the last step's Finish lands on the hub.
                return AdvanceFrom(idx);

            case WizardStepOutcome.NotRelevant:
                return AdvanceFrom(idx);

            case WizardStepOutcome.Invalid:
            default:
                // Re-render the SAME step inline with the posted values + ModelState errors.
                CurrentIndex = idx;
                CurrentHandler = handler;   // Model already holds the posted values from SaveAsync
                return Page();
        }
    }

    // ----- helpers --------------------------------------------------------

    private WizardStepContext BuildContext(CurrentParticipant me, CancellationToken ct) =>
        new(me.EventId, me.ParticipantId, me.Role, me.Email, me.FullName, this,
            // The host owns binding: run model binding for THIS request (flat, no prefix) into
            // the handler's concrete form model. Uses the public non-generic overload so handlers
            // never need to derive from PageModel.
            model => TryUpdateModelAsync(model, model.GetType(), name: string.Empty),
            ct);

    private IWizardStepHandler? ResolveHandler(string key) =>
        _handlers.TryGetValue(key, out var h) ? h : null;

    private IActionResult AdvanceFrom(int idx)
    {
        var next = Plan!.Steps.Skip(idx + 1).FirstOrDefault();
        return next is null ? RedirectToPage("/Index") : RedirectToStep(next.Key);
    }

    private IActionResult RedirectToStep(string key) => RedirectToPage(new { step = key });

    private IActionResult RedirectToStepOrHub(PlanStep? step) =>
        step is null ? RedirectToPage("/Index") : RedirectToStep(step.Key);

    private async Task<WizardPlan?> BuildPlanAsync(CurrentParticipant me, CancellationToken ct)
    {
        if (me.Role == ParticipantRole.Speaker)
        {
            var v = await _speaker.BuildAsync(me.EventId, me.ParticipantId, ct);
            return new WizardPlan
            {
                ResxPrefix = "SpeakerWiz",
                Steps = v.Steps.Select(s => new PlanStep(s.Key, s.Route, s.Done)).ToList(),
            };
        }

        if (RoleWizardService.Handles(me.Role))
        {
            var v = await _role.BuildAsync(me.EventId, me.ParticipantId, ct);
            return new WizardPlan
            {
                ResxPrefix = "RoleWiz",
                Steps = v.Steps.Select(s => new PlanStep(s.Key, s.Route, s.Done)).ToList(),
            };
        }

        return null;   // role has no generic/inline wizard (e.g. Attendee)
    }
}
