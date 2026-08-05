using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using CommunityHub.Core.Config;
using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Content;

/// <summary>
/// §680 — loads the per-role WELCOME copy for the Get-Started wizard's step 1 from
/// <c>config/welcome/&lt;edition&gt;/&lt;role&gt;.md</c>.
/// </summary>
/// <remarks>
/// <para>🔒 <b>The copy is CONFIG, never a Razor page.</b> §680's second constraint: the welcome
/// text is derived from the role's welcome E-MAIL, which is template-driven and operator-editable.
/// A second hand-maintained copy inside a <c>.cshtml</c> is exactly how the two drift apart (the
/// §660 lesson), so the step renders a file the operator can edit, per edition.</para>
///
/// <para>Same shape as <see cref="Tasks.Definitions.TaskBodyStore"/> and
/// <c>ContentMarkdownRenderer</c>: resolved through <see cref="ConfigPaths"/> so the file is found
/// from the App Service content root AND the Functions host's mounted package, read once per
/// process, and <b>fail-soft — a missing file is an ABSENT step, never an exception</b>. The wizard
/// is on the critical path for every role; one missing content file must not be able to take Get
/// Started down.</para>
///
/// <para>⚠️ <b>A role with no file simply gets no welcome step.</b> That is the ORGANIZER's case by
/// design (§680 lists six roles; organizers are staff and get no welcome mail either —
/// <see cref="Email.WelcomeVariants.TemplateKeyFor"/> returns null for them). It is also the
/// fail-soft path if a deploy ever ships without the <c>config/welcome</c> content: the rest of the
/// wizard is unaffected. <c>WelcomeCopyCatalogTests</c> turns a genuinely missing file into a build
/// failure — loud where it is cheap.</para>
/// </remarks>
public sealed class WelcomeCopyStore
{
    /// <summary>
    /// The wizard step key — ONE source for the four wizard services that emit the step, the
    /// handler that claims it, and the host's first-run landing rule. A step key that is written
    /// out four times is a step key that eventually disagrees with itself.
    /// </summary>
    public const string StepKey = "welcome";

    /// <summary>
    /// The step's "route". Unlike every other wizard step the welcome has NO standalone page to
    /// open — it exists only inside the wizard — so it points at the wizard itself. That keeps the
    /// host's handler-less fallback link honest if the handler is ever unavailable.
    /// </summary>
    public const string StepRoute = "/Forms/Wizard";

    private readonly string _editionCode;

    /// <summary>Raw file text per slug — <c>""</c> for "missing or empty", cached either way so a
    /// wizard build costs at most one disk read per role per process.</summary>
    private readonly ConcurrentDictionary<string, string> _rawCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <param name="editionCode">
    /// The edition folder, e.g. <c>eldk27</c>. A CONSTRUCTOR argument, not a constant: the evergreen
    /// rule (CLAUDE.md) is that a new edition is a new config folder, not a code change.
    /// </param>
    public WelcomeCopyStore(string editionCode = "eldk27")
    {
        _editionCode = editionCode;
    }

    /// <summary>
    /// The copy file slug for <paramref name="role"/>, or null for a role that gets no welcome.
    /// </summary>
    /// <remarks>
    /// Deliberately the SAME six roles §680 names — Speaker · Sponsor · Volunteer · Media · Event
    /// Partner · Attendee. Attendee is included here although it has no welcome MAIL (operator
    /// 2026-06-22: attendees are covered by the Master Class confirmed-seat mail); §680 asks for the
    /// step for every role, and <c>attendee.md</c> was written for it.
    /// </remarks>
    public static string? SlugFor(ParticipantRole role) => role switch
    {
        ParticipantRole.Speaker => "speaker",
        ParticipantRole.Sponsor => "sponsor",
        ParticipantRole.Volunteer => "volunteer",
        ParticipantRole.Media => "media",
        ParticipantRole.EventPartner => "eventpartner",
        ParticipantRole.Attendee => "attendee",
        // Organizer (and any future role) gets no welcome step.
        _ => null,
    };

    /// <summary>The on-disk path a role's copy resolves to (empty for a role with no slug).</summary>
    public string ResolvePath(ParticipantRole role) =>
        SlugFor(role) is { } slug ? ConfigPaths.Resolve($"config/welcome/{_editionCode}/{slug}.md") : string.Empty;

    /// <summary>
    /// The RAW authored markdown for this role, cached; <c>""</c> when the role has no copy.
    /// Placeholders are NOT substituted here — see <see cref="Substitute"/>.
    /// </summary>
    public string LoadRaw(ParticipantRole role)
    {
        var slug = SlugFor(role);
        if (slug is null) return string.Empty;

        return _rawCache.GetOrAdd(slug, _ =>
        {
            var path = ResolvePath(role);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return string.Empty;
            try
            {
                return File.ReadAllText(path);
            }
            catch (IOException)
            {
                // Fail-soft (see the class remarks): an unreadable file must not throw on the
                // wizard's critical path — the step is simply not offered.
                return string.Empty;
            }
        });
    }

    /// <summary>
    /// Is there real welcome copy for this role? The wizard services call this to decide whether to
    /// offer the step at all, so a blank or missing file can never produce an EMPTY welcome step.
    /// </summary>
    public bool Exists(ParticipantRole role) => !string.IsNullOrWhiteSpace(LoadRaw(role));

    /// <summary>Drop the cache (tests, and a future content-reload hook).</summary>
    public void Clear() => _rawCache.Clear();

    private static readonly Regex TokenRx = new(@"\{\{(\w+)\}\}", RegexOptions.Compiled);

    /// <summary>
    /// Replace every <c>{{token}}</c> with its value; an unmapped token renders as NOTHING.
    /// </summary>
    /// <remarks>
    /// <para>🔒 <b>Values are HTML-ENCODED at this seam</b> — the same contract
    /// <c>EmailTemplateRenderer</c> states (§10c-4, "encode at the seam"). The substituted text is
    /// handed to Markdig, which passes raw HTML through by design (the content files are trusted,
    /// in-repo copy), and <c>{{firstName}}</c> is NOT in-repo copy — it is imported participant
    /// data. Encoding here is what keeps a name from being markup. Encoding is invisible for an
    /// ordinary name and renders correctly for <c>&amp;</c> either way.</para>
    ///
    /// <para><b>Unmapped ⇒ empty, never literal braces.</b> A stray <c>{{token}}</c> on a
    /// participant-facing page reads as a broken system; the same rule TaskBodyService applies.
    /// This is a deliberate 20-line twin of that private method rather than a refactor of it: the
    /// task-body path is load-bearing (§688 fixed a live bug in its substitute-then-parse ORDER),
    /// and this is a different, simpler seam — one pass, no nesting, no missing-key reporting.</para>
    /// </remarks>
    public static string Substitute(string source, IReadOnlyDictionary<string, string> tokens)
    {
        if (string.IsNullOrEmpty(source) || !source.Contains("{{", StringComparison.Ordinal))
        {
            return source;
        }

        return TokenRx.Replace(source, m =>
            tokens.TryGetValue(m.Groups[1].Value, out var value)
                ? System.Net.WebUtility.HtmlEncode(value)
                : string.Empty);
    }
}
