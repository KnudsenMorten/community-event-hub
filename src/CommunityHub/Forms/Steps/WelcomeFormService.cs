using CommunityHub.Core.Content;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Markdig;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Forms.Steps;

/// <summary>§680 — the render model for the wizard's WELCOME step: one block of rendered HTML.</summary>
public sealed class WelcomeFormModel
{
    /// <summary>The role's welcome copy, tokens resolved and markdown rendered. Empty when the
    /// role has no copy (the step is not offered in that case — see <see cref="WelcomeCopyStore"/>).</summary>
    public string Html { get; internal set; } = string.Empty;
}

/// <summary>
/// §680 — load for the Get-Started wizard's WELCOME step (operator 2026-07-29: <i>"i want to
/// introduce a welcome step 1 for all roles in their get started wizard. it should be similar to
/// the welcome email template for the relevant role, except there is no link to get started
/// (button) or support / questions … a) to thank you in the welcome b) intro to the event (days,
/// what happens), c) give the user intro to the Event Hub app including what to find (bullets)"</i>).
///
/// <para><b>Read-only by design.</b> The step shows and stores nothing. Its whole job is that the
/// first screen of the hub is a welcome rather than a form field.</para>
///
/// <para><b>The copy is CONFIG</b> — <c>config/welcome/&lt;edition&gt;/&lt;role&gt;.md</c>, derived
/// line by line from each role's welcome MAIL minus the two things §680 removes (the Get-Started CTA
/// button and the support block, both meaningless to someone already inside the wizard). See
/// <see cref="WelcomeCopyStore"/> for why it is not a Razor page.</para>
/// </summary>
public sealed class WelcomeFormService : IWizardFormService
{
    private readonly CommunityHubDbContext _db;
    private readonly WelcomeCopyStore _copy;
    private readonly MarkdownPipeline _pipeline;

    public WelcomeFormService(CommunityHubDbContext db, WelcomeCopyStore copy)
    {
        _db = db;
        _copy = copy;
        // Same pipeline as the content-hub pages (ContentMarkdownRenderer): stateless + thread-safe.
        _pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
    }

    public async Task<WelcomeFormModel> LoadAsync(
        int eventId, int participantId, ParticipantRole role, CancellationToken ct)
    {
        var model = new WelcomeFormModel();

        var source = _copy.LoadRaw(role);
        if (string.IsNullOrWhiteSpace(source)) return model;

        // 🔒 The SAME token values the welcome MAIL uses (WelcomeEmailService): first name from the
        // participant's own FullName, display name and code from the LIVE Event row. If the two ever
        // disagree, the person is greeted by two different names for the same event on consecutive
        // screens — which is exactly the drift §680 says to avoid.
        var who = await _db.Participants.AsNoTracking()
            .Where(p => p.Id == participantId && p.EventId == eventId)
            .Select(p => new { p.FullName, p.Event.DisplayName, p.Event.Code })
            .FirstOrDefaultAsync(ct);

        var fullName = who?.FullName ?? string.Empty;
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // "there" is WelcomeEmailService's fallback verbatim — "Hi , welcome aboard" is worse
            // than a generic greeting.
            ["firstName"] = string.IsNullOrWhiteSpace(fullName) ? "there" : fullName.Split(' ')[0],
            ["eventDisplayName"] = who?.DisplayName ?? string.Empty,
            // Empty-safe exactly like EmailTemplateProvider.NewTokenSet: a blank code must render
            // as nothing, never a stray " ()".
            ["eventCodeParens"] = string.IsNullOrWhiteSpace(who?.Code) ? string.Empty : $" ({who!.Code})",
            ["eventCode"] = who?.Code ?? string.Empty,
        };

        // Substitute BEFORE rendering (the §688 order): a token value must be able to carry an
        // apostrophe or an ampersand and still come out as text, and the encoding seam lives in
        // Substitute.
        model.Html = Markdown.ToHtml(WelcomeCopyStore.Substitute(source, tokens), _pipeline);
        return model;
    }
}
