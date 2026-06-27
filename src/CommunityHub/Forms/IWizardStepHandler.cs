using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Forms;

/// <summary>
/// The result of trying to SAVE a wizard step (REQUIREMENTS §148).
/// </summary>
public enum WizardStepOutcome
{
    /// <summary>Validation passed, data persisted (+ side-effects ran) — move to the next step.</summary>
    Advance,

    /// <summary>Validation failed — field errors are in <see cref="WizardStepContext.ModelState"/>;
    /// the SAME step must re-render with the posted values + messages.</summary>
    Invalid,

    /// <summary>The step does not apply to this participant (entitlement/relevance gate) —
    /// skip it (forward on save, hidden in the plan).</summary>
    NotRelevant,
}

/// <summary>
/// Per-request context handed to a <see cref="IWizardStepHandler"/> for both load
/// and save (REQUIREMENTS §148). Carries the current participant identity, the EventId,
/// and a binding bridge back to the host page so a handler — which lives in a different
/// type than the host <see cref="PageModel"/> — can still run real Razor-Pages model
/// binding into its concrete form model and report field errors.
/// </summary>
/// <param name="EventId">The current edition.</param>
/// <param name="ParticipantId">The signed-in (or acted-as) participant.</param>
/// <param name="Role">The participant's role (drives policy text / relevance).</param>
/// <param name="Email">The participant's email (for side-effects such as calendar invites).</param>
/// <param name="FullName">The participant's display name.</param>
/// <param name="Page">The host page — exposes <see cref="PageModel.ModelState"/> and
/// <see cref="PageModel.Request"/>. (Binding goes through <see cref="TryUpdateModelAsync"/>
/// below, because <c>PageModel.TryUpdateModelAsync</c> is not reachable cross-type.)</param>
/// <param name="TryUpdateModelAsync">Host-provided binder: runs Razor-Pages model binding
/// for the CURRENT request (top-level form keys, no prefix) into the given form model and
/// returns whether binding+validation succeeded. Supplied by the host so handlers never
/// need to derive from <see cref="PageModel"/>.</param>
/// <param name="Ct">Request cancellation.</param>
public sealed record WizardStepContext(
    int EventId,
    int ParticipantId,
    ParticipantRole Role,
    string Email,
    string FullName,
    PageModel Page,
    Func<object, Task<bool>> TryUpdateModelAsync,
    CancellationToken Ct)
{
    /// <summary>The host page's ModelState — handlers add field errors here on <see cref="WizardStepOutcome.Invalid"/>.</summary>
    public ModelStateDictionary ModelState => Page.ModelState;

    /// <summary>The current request (posted form / query) for any manual reads.</summary>
    public HttpRequest Request => Page.Request;
}

/// <summary>
/// A single, self-contained step in the in-wizard stepper (REQUIREMENTS §148). One
/// implementation per onboarding form. The handler owns NO chrome: the generic host
/// (<c>/Forms/Wizard</c>) renders the progress bar, the <c>&lt;form method="post"&gt;</c>,
/// the Previous / Save&amp;next / Finish buttons; the handler only (a) loads the form's
/// render model and (b) on POST binds + delegates to the form's shared
/// <see cref="IWizardFormService"/> which runs the form's REAL validate + persist +
/// side-effects. The SAME service is called by the standalone form page, so the two
/// stay byte-for-byte identical.
///
/// <para>Discovery is automatic: every non-abstract <see cref="IWizardStepHandler"/> in
/// the web assembly is registered by the single reflection block in <c>Program.cs</c>
/// and keyed by <see cref="Key"/>. A new step = a new handler class + its
/// <see cref="IWizardFormService"/>; zero shared-file edits.</para>
/// </summary>
public interface IWizardStepHandler
{
    /// <summary>
    /// Stable step key — MUST equal the key the wizard SERVICE emits for this step
    /// (e.g. "hotel" from <c>SpeakerWizardService</c> / <c>RoleWizardService</c>), and the
    /// resx label key suffix. The host resolves the handler for a plan step by this key,
    /// so it must be unique across all handlers (the host throws on a duplicate in DEBUG).
    /// </summary>
    string Key { get; }

    /// <summary>
    /// The fields-only partial (no <c>&lt;form&gt;</c>, no buttons) the host renders inside
    /// its one form, e.g. <c>"_HotelFields"</c>. Bound to <see cref="Model"/>.
    /// </summary>
    string PartialName { get; }

    /// <summary>
    /// The render model passed to <see cref="PartialName"/>. Populated by <see cref="LoadAsync"/>
    /// and, after a failed <see cref="SaveAsync"/>, holds the POSTED values so the same partial
    /// re-renders with the user's input + the <see cref="WizardStepContext.ModelState"/> errors.
    /// </summary>
    object? Model { get; }

    /// <summary>
    /// Load the step's render model from the form's shared service — the SAME load the
    /// standalone page uses (so the inline step shows exactly what the standalone form shows).
    /// </summary>
    Task LoadAsync(WizardStepContext ctx);

    /// <summary>
    /// Bind the posted fields and delegate to the form's shared service to run the REAL
    /// validate + persist + side-effects. Field errors are written into
    /// <see cref="WizardStepContext.ModelState"/>. Returns <see cref="WizardStepOutcome.Advance"/>
    /// on success, <see cref="WizardStepOutcome.Invalid"/> to re-render the same step, or
    /// <see cref="WizardStepOutcome.NotRelevant"/> to skip it.
    /// </summary>
    Task<WizardStepOutcome> SaveAsync(WizardStepContext ctx);
}

/// <summary>
/// Empty marker interface (REQUIREMENTS §148). A form's shared submit-service
/// (the encapsulated validate + persist + side-effects + load, reused by BOTH the
/// standalone page AND the step handler) implements this so the single reflection
/// block in <c>Program.cs</c> self-registers it by its concrete type. The standalone
/// page model and the step handler both inject the concrete <c>XxxFormService</c>.
/// </summary>
public interface IWizardFormService
{
}
