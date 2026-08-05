using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Forms.Steps;

/// <summary>
/// The render + edit model for the Signal step (REQUIREMENTS §148, §109). Shared by the
/// standalone <c>/Forms/Signal</c> page AND the inline wizard step, and is the model the
/// <c>_SignalFields</c> partial binds to. The step has NO posted form fields — joining is
/// external — so every property is display-only (<see cref="BindNeverAttribute"/>),
/// populated by <see cref="SignalFormService"/>; completion is a MANUAL mark-done.
/// </summary>
public sealed class SignalFormModel
{
    /// <summary>The participant's role (drives which Signal links resolve).</summary>
    [BindNever] public ParticipantRole Role { get; set; }

    /// <summary>True when the role has no Signal groups (GetForRole null) — the step is not relevant.</summary>
    [BindNever] public bool OutOfScope { get; set; }

    /// <summary>The role-appropriate chat + broadcast links, or null when out of scope.</summary>
    [BindNever] public SignalGroupLinks? Links { get; set; }

    /// <summary>True once the per-participant <c>signal:</c> task is marked Done.</summary>
    [BindNever] public bool Done { get; set; }
}

/// <summary>
/// Shared submit-service for the Signal step (REQUIREMENTS §148, §109). It encapsulates the
/// form's ENTIRE behavior — the OnGet load (resolve role-appropriate Signal chat + broadcast
/// links from <see cref="SignalGroupsProvider.GetForRole"/> and ensure the per-participant
/// <c>signal:</c> task exists, idempotent), the standalone page's manual on/off toggle, and the
/// wizard's Save&amp;next mark-done — so that BOTH the standalone <c>/Forms/Signal</c> page and
/// the inline <see cref="SignalStepHandler"/> call the exact same logic and stay identical.
/// Out-of-scope roles (<see cref="SignalGroupsProvider.GetForRole"/> null) are NotRelevant.
/// Completion detection: a <see cref="ParticipantTask"/> with SourceKey
/// <see cref="WizardStepTasks.Signal(int)"/> in state Done (mirrors the wizard services).
/// Implements the <see cref="IWizardFormService"/> marker so it self-registers by concrete type.
/// </summary>
public sealed class SignalFormService : IWizardFormService
{
    private readonly CommunityHubDbContext _db;
    private readonly SignalGroupsProvider _signal;
    private readonly TimeProvider _clock;
    private readonly CommunityHub.Core.Email.EmailTemplateProvider? _templates;
    private readonly CommunityHub.Core.Email.IEmailSender? _email;
    private readonly CommunityHub.Core.Email.IEmailContextAccessor? _emailContext;
    private readonly Microsoft.Extensions.Logging.ILogger<SignalFormService>? _log;

    /// <param name="templates">§779 — OPTIONAL, so every existing construction and test keeps
    /// working. Absent ⇒ the "mail me the links" button reports itself unavailable rather than
    /// pretending to have sent.</param>
    public SignalFormService(
        CommunityHubDbContext db,
        SignalGroupsProvider signal,
        TimeProvider clock,
        CommunityHub.Core.Email.EmailTemplateProvider? templates = null,
        CommunityHub.Core.Email.IEmailSender? email = null,
        CommunityHub.Core.Email.IEmailContextAccessor? emailContext = null,
        Microsoft.Extensions.Logging.ILogger<SignalFormService>? log = null)
    {
        _db = db;
        _signal = signal;
        _clock = clock;
        _templates = templates;
        _email = email;
        _emailContext = emailContext;
        _log = log;
    }

    /// <summary>Relevance gate (REQUIREMENTS §148) — the role has Signal groups in scope.</summary>
    public bool IsRelevant(ParticipantRole role) => _signal.GetForRole(role) is not null;

    /// <summary>Completion detection (REQUIREMENTS §148) — the <c>signal:</c> task is Done.
    /// Mirrors SpeakerWizardService / RoleWizardService.</summary>
    public Task<bool> IsDoneAsync(int eventId, int participantId, CancellationToken ct) =>
        _db.Tasks.AnyAsync(
            t => t.EventId == eventId && t.AssignedParticipantId == participantId
                 && t.SourceKey == WizardStepTasks.Signal(participantId) && t.State == TaskState.Done, ct);

    /// <summary>
    /// Load the step's current state — the SAME load the standalone page's OnGet used: resolve
    /// the role-appropriate links, and when in scope ensure the <c>signal:</c> task exists
    /// (idempotent) so it also surfaces in the task list + reminders, then read its done state.
    /// </summary>
    public async Task<SignalFormModel> LoadAsync(int eventId, int participantId, ParticipantRole role, CancellationToken ct)
    {
        var model = new SignalFormModel { Role = role };

        var links = _signal.GetForRole(role);
        if (links is null) { model.OutOfScope = true; return model; }
        model.Links = links;

        var task = await EnsureTaskAsync(eventId, participantId, links, ct);
        model.Done = task.State == TaskState.Done;
        return model;
    }

    /// <summary>
    /// The standalone page's manual on/off toggle (mark done / not done) — unchanged behavior.
    /// Out-of-scope roles are a no-op. Returns the refreshed model.
    /// </summary>
    public async Task<SignalFormModel> ToggleAsync(int eventId, int participantId, ParticipantRole role, CancellationToken ct)
    {
        var model = new SignalFormModel { Role = role };

        var links = _signal.GetForRole(role);
        if (links is null) { model.OutOfScope = true; return model; }
        model.Links = links;

        var task = await EnsureTaskAsync(eventId, participantId, links, ct);
        if (task.State == TaskState.Done)
        {
            task.State = TaskState.Open;
            task.CompletedAt = null;
        }
        else
        {
            task.State = TaskState.Done;
            task.CompletedAt = _clock.GetUtcNow();
        }
        await _db.SaveChangesAsync(ct);
        model.Done = task.State == TaskState.Done;
        return model;
    }

    /// <summary>
    /// The wizard's Save&amp;next (REQUIREMENTS §148): the Save acts as MARK-DONE — ensure the
    /// task exists (idempotent) and mark it Done. There are no posted fields to validate, so this
    /// always <see cref="WizardStepOutcome.Advance"/>s when in scope; an out-of-scope role
    /// (<see cref="SignalGroupsProvider.GetForRole"/> null) returns <see cref="WizardStepOutcome.NotRelevant"/>.
    /// The relevance gate is re-derived server-side here, so a crafted POST can never bypass it.
    /// </summary>
    public async Task<WizardStepOutcome> SaveAsync(
        SignalFormModel model, int eventId, int participantId, ParticipantRole role,
        ModelStateDictionary modelState, CancellationToken ct)
    {
        model.Role = role;

        var links = _signal.GetForRole(role);
        if (links is null) { model.OutOfScope = true; return WizardStepOutcome.NotRelevant; }
        model.Links = links;

        var task = await EnsureTaskAsync(eventId, participantId, links, ct);
        if (task.State != TaskState.Done)
        {
            task.State = TaskState.Done;
            task.CompletedAt = _clock.GetUtcNow();
            await _db.SaveChangesAsync(ct);
        }
        model.Done = true;
        return WizardStepOutcome.Advance;
    }

    /// <summary>
    /// §779 — mail this participant THEIR OWN Signal join links, so they can open them on a phone.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-03: <i>"add button and instruction so they can send both links to
    /// their mail from their mobile so they can join there. this is relevant when they signup from
    /// desktop with no signal app installed"</i>.</para>
    ///
    /// <para>🔒 <b>A <c>signal.group</c> link only does something on a device with Signal on it.</b>
    /// Get Started is filled in on a desktop, so the join buttons are dead ends for exactly the
    /// people the step exists for. Mail is the bridge to the phone — there is no other way to carry
    /// a link like that across, short of retyping a URL nobody can retype.</para>
    ///
    /// <para>🔒 <b>Only the links THIS ROLE gets.</b> Media is broadcast-only by config; mailing it a
    /// chat link would put somebody in a group the configuration deliberately keeps them out of.
    /// The role is re-resolved here rather than trusted from the caller, so a crafted POST cannot
    /// ask for another role's groups.</para>
    ///
    /// <para>🔒 <b>RING-EXEMPT.</b> The participant pressed a button asking for this mail; it is the
    /// same shape as the PIN sign-in, and a ring gate that silently dropped it would leave somebody
    /// staring at a button that appears to do nothing. It is not ring-gated for the same reason
    /// user-initiated mail never is.</para>
    /// </remarks>
    public async Task<(bool Ok, string Message)> SendLinksEmailAsync(
        int eventId, int participantId, ParticipantRole role, CancellationToken ct = default)
    {
        var links = _signal.GetForRole(role);
        if (links is null) return (false, "There are no Signal groups for your role.");

        if (_templates is null || _email is null)
            return (false, "Sending the links isn't available right now.");

        var me = await _db.Participants
            .AsNoTracking()
            .Where(p => p.Id == participantId && p.EventId == eventId)
            .Select(p => new { p.Email, p.FullName })
            .FirstOrDefaultAsync(ct);

        if (me is null || string.IsNullOrWhiteSpace(me.Email) || !me.Email.Contains('@'))
            return (false, "We don't have a valid e-mail address for you.");

        var eventName = await _db.Events.AsNoTracking()
            .Where(e => e.Id == eventId)
            .Select(e => e.DisplayName)
            .FirstOrDefaultAsync(ct);

        var tokens = _templates.NewTokenSet(participantId);
        tokens["firstName"] = FirstName(me.FullName);
        if (!string.IsNullOrWhiteSpace(eventName)) tokens["eventDisplayName"] = eventName!;
        // 🔒 The two *Block tokens are RAW HTML by the renderer's own naming convention, which is
        // what lets a button be a button. An absent group renders as an EMPTY string — never a
        // button to a link this role was not given.
        tokens["chatBlock"] = links.HasChat
            ? ButtonBlock(links.ChatUrl!, links.ChatLabel ?? "Join the chat group")
            : string.Empty;
        tokens["broadcastBlock"] = links.HasBroadcast
            ? ButtonBlock(links.BroadcastUrl!, links.BroadcastLabel)
            : string.Empty;

        var rendered = _templates.Render("signal-join-links", tokens);

        using (_emailContext?.Set(new CommunityHub.Core.Email.EmailContext(
            "signal-join-links", eventId, participantId, me.FullName,
            TemplateName: "signal-join-links",
            RingExempt: true)))
        {
            try
            {
                await _email.SendAsync(me.Email, rendered.Subject, rendered.HtmlBody, ct);
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "§779: could not mail the Signal links to participant {Pid}.", participantId);
                return (false, "We couldn't send the mail just now — please try again.");
            }
        }

        // 🔑 The address is echoed back. "Sent" alone leaves somebody watching the wrong inbox, and
        // this mail exists precisely because they are about to move to another device.
        return (true, $"Sent to {me.Email} — open it on the phone that has Signal installed.");
    }

    private static string FirstName(string? fullName)
    {
        var trimmed = (fullName ?? string.Empty).Trim();
        if (trimmed.Length == 0) return "there";
        var space = trimmed.IndexOf(' ');
        return space <= 0 ? trimmed : trimmed[..space];
    }

    /// <summary>
    /// One join button, in the operator-verified bulletproof shape.
    /// </summary>
    /// <remarks>
    /// 🔒 <b>VML roundrect for Outlook, plain anchor for everything else.</b> Desktop Outlook renders
    /// with the WORD engine, where only VML gives rounded corners and white text — the established
    /// CEH standard. ⚠️ The URL is substituted HERE, in code, so no <c>{{token}}</c> ever sits inside
    /// the <c>&lt;!--[if mso]&gt;</c> conditional comment: a token whose value breaks a comment once
    /// swallowed an entire mail into it, live.
    /// </remarks>
    private static string ButtonBlock(string url, string label)
    {
        var href = System.Net.WebUtility.HtmlEncode(url);
        var text = System.Net.WebUtility.HtmlEncode(label);

        return $"""
        <table role="presentation" cellpadding="0" cellspacing="0" width="100%" style="margin:8px 0 6px;"><tr>
          <td align="center">
            <!--[if mso]>
            <v:roundrect xmlns:v="urn:schemas-microsoft-com:vml" xmlns:w="urn:schemas-microsoft-com:office:word" href="{href}" style="height:52px;v-text-anchor:middle;width:296px;" arcsize="50%" stroke="f" fillcolor="#1565c0">
              <w:anchorlock/>
              <center style="color:#ffffff;font-family:Aptos,'Segoe UI',Arial,sans-serif;font-size:16px;font-weight:bold;">{text}</center>
            </v:roundrect>
            <![endif]-->
            <!--[if !mso]><!-- -->
            <a href="{href}" style="background-color:#1565c0;border-radius:999px;color:#ffffff;display:inline-block;font-family:Aptos,'Segoe UI',Arial,sans-serif;font-size:16px;font-weight:700;line-height:52px;text-align:center;text-decoration:none;width:296px;-webkit-text-size-adjust:none;">{text}</a>
            <!--<![endif]-->
          </td>
        </tr></table>
        """;
    }

    /// <summary>Ensure the "Join Signal groups" task exists (idempotent), per-participant scoped.</summary>
    private async Task<ParticipantTask> EnsureTaskAsync(
        int eventId, int participantId, SignalGroupLinks links, CancellationToken ct)
    {
        var sourceKey = WizardStepTasks.Signal(participantId);
        // §162: embed the actual signal.group join URLs in the description — the task row's
        // linkifier turns them into clickable join buttons, so the person can join straight from
        // the task (they were missing the links before).
        var description = BuildSignalTaskDescription(links);
        var task = await _db.Tasks.FirstOrDefaultAsync(
            t => t.EventId == eventId && t.AssignedParticipantId == participantId
                 && t.SourceKey == sourceKey, ct);
        if (task is null)
        {
            task = new ParticipantTask
            {
                EventId = eventId,
                AssignedParticipantId = participantId,
                Title = "Join Signal groups",
                Description = description,
                State = TaskState.Open,
                IsMandatory = false,
                SourceKey = sourceKey,
                CreatedAt = _clock.GetUtcNow(),
            };
            _db.Tasks.Add(task);
            await _db.SaveChangesAsync(ct);
        }
        else if (task.Description != description)
        {
            // Refresh existing tasks so they pick up the join links (idempotent).
            task.Description = description;
            await _db.SaveChangesAsync(ct);
        }
        return task;
    }

    private static string BuildSignalTaskDescription(SignalGroupLinks links)
    {
        var sb = new System.Text.StringBuilder(
            "Join the ELDK27 Signal group(s) below, then mark this done.");
        if (links.HasChat) sb.Append("\n\nChat group: ").Append(links.ChatUrl);
        if (links.HasBroadcast) sb.Append("\n\nBroadcast group: ").Append(links.BroadcastUrl);
        return sb.ToString();
    }
}
