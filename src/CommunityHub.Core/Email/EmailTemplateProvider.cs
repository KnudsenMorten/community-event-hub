using CommunityHub.Core.Auth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CommunityHub.Core.Email;

/// <summary>Where the email templates live.</summary>
public sealed class EmailTemplateOptions
{
    public const string SectionName = "EmailTemplates";

    /// <summary>
    /// Directory holding _layout.html and the content templates. Deployed
    /// alongside the app (e.g. a "templates/emails" content folder).
    /// </summary>
    public string TemplateDirectory { get; set; } = "templates/emails";

    /// <summary>
    /// PRIVATE per-edition template directory (publish-safe layer). A file
    /// <c>{PrivateTemplateDirectory}/{key}.html</c> overrides the shipped
    /// default in <see cref="TemplateDirectory"/> when present. This lets an
    /// edition ship its real, named copy (which is denylisted from the public
    /// mirror) without putting it in the open-source <see cref="TemplateDirectory"/>.
    /// Resolution order is: DB editor override → this private file → shipped default.
    /// </summary>
    public string PrivateTemplateDirectory { get; set; } = "config/email-templates";

    /// <summary>
    /// Branding tokens for the shared layout - from event.&lt;edition&gt;.json.
    /// These are the same for every email of an edition.
    /// </summary>
    public string BrandColor { get; set; } = "#1a1a2e";
    public string LogoUrl { get; set; } = string.Empty;
    public string SupportEmail { get; set; } = "info@expertslive.dk";
    public string HubUrl { get; set; } = string.Empty;

    /// <summary>
    /// Edition display name and short code for the shared layout footer
    /// ("{{eventDisplayName}} ({{eventCode}})"). From event.&lt;edition&gt;.json.
    /// Callers that build an email may override <c>eventDisplayName</c> with the
    /// live Event row's value; these provide the always-present base/footer
    /// fallback so no email ever renders a bare placeholder.
    /// </summary>
    public string EventDisplayName { get; set; } = string.Empty;
    public string EventCode { get; set; } = string.Empty;
}

/// <summary>
/// Loads and caches the email templates from disk, and renders them via
/// <see cref="EmailTemplateRenderer"/>. Content templates are cached by name
/// after first read; the layout is read once.
///
/// A missing content template throws - reminder jobs should fail loudly on a
/// misconfigured deployment rather than send a blank email.
/// </summary>
public sealed class EmailTemplateProvider
{
    private readonly string _templateDirectory;
    private readonly string _privateTemplateDirectory;
    private readonly EmailTemplateOptions _options;
    private readonly Lazy<EmailTemplateRenderer> _renderer;
    private readonly Dictionary<string, string> _contentCache = new();
    private readonly object _cacheLock = new();
    // Per-edition template overrides (REQUIREMENTS §25h). The provider is a SINGLETON, the
    // override store is SCOPED, so we open a scope per render (like LoggingEmailSender) and
    // read the edition from the ambient EmailContext. Null in legacy/test wiring ⇒ overrides
    // are not consulted and the shipped on-disk default is used (unchanged behaviour).
    private readonly IServiceScopeFactory? _scopes;
    private readonly IEmailContextAccessor? _emailContext;

    public EmailTemplateProvider(
        IOptions<EmailTemplateOptions> options,
        IServiceScopeFactory? scopes = null,
        IEmailContextAccessor? emailContext = null)
    {
        _options = options.Value;
        _scopes = scopes;
        _emailContext = emailContext;
        _templateDirectory = ResolveContentDir(_options.TemplateDirectory);
        _privateTemplateDirectory = ResolveContentDir(_options.PrivateTemplateDirectory);
        _renderer = new Lazy<EmailTemplateRenderer>(() =>
        {
            var layoutPath = Path.Combine(_templateDirectory, "_layout.html");
            if (!File.Exists(layoutPath))
            {
                throw new FileNotFoundException(
                    $"Email layout template not found: {layoutPath}");
            }
            return new EmailTemplateRenderer(File.ReadAllText(layoutPath));
        });
    }

    /// <summary>
    /// Resolve a (possibly relative) content directory to an absolute path. The web
    /// host's working directory IS its content root, so the relative default resolves
    /// fine there; the Azure Functions host runs with a DIFFERENT working directory,
    /// so the same relative "templates/emails" missed the bundle and every templated
    /// render threw FileNotFoundException (found 2026-06-23). When the path is relative
    /// and absent from the cwd, fall back to <see cref="AppContext.BaseDirectory"/>
    /// (the published app folder, where the csproj copies the templates). Empty/rooted
    /// paths and paths that already exist relative to cwd are returned unchanged.
    /// </summary>
    private static string ResolveContentDir(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || Path.IsPathRooted(dir) || Directory.Exists(dir))
        {
            return dir;
        }
        var baseDir = Path.Combine(AppContext.BaseDirectory, dir);
        return Directory.Exists(baseDir) ? baseDir : dir;
    }

    /// <summary>
    /// A fresh token map pre-filled with the edition's branding tokens
    /// (brandColor, logoUrl, supportEmail, hubUrl). Callers add their
    /// content-specific tokens to this and pass it to <see cref="Render"/>.
    ///
    /// <para>§169 (personal email magic-link): when the email is addressed to a
    /// KNOWN participant — supplied explicitly via <paramref name="participantId"/>
    /// or read from the ambient <see cref="EmailContext"/> — the <c>hubUrl</c> CTA
    /// is rewritten to that participant's <b>auto-login magic-link</b>
    /// (<c>{HubUrl}/go/{token}</c>) so the recipient lands signed-in. Because the
    /// <c>/go/{token}/{**target}</c> route also accepts a trailing path, templates
    /// that append a deep-link (e.g. <c>{{hubUrl}}/Speaker/Graphics</c>) keep
    /// working — they become <c>{HubUrl}/go/{token}/Speaker/Graphics</c>.</para>
    ///
    /// <para>Best-effort + fail-safe: with no magic-link service wired, no origin
    /// configured, no participant, or any error, the plain hub URL is left in
    /// place — a send never breaks on this layer. Multi-recipient / unaddressed
    /// sends (broadcast) pass no participant and keep the plain URL.</para>
    /// </summary>
    public Dictionary<string, string> NewTokenSet(int? participantId = null)
    {
        var tokens = new Dictionary<string, string>
        {
            ["brandColor"] = _options.BrandColor,
            ["logoUrl"] = _options.LogoUrl,
            ["supportEmail"] = _options.SupportEmail,
            ["hubUrl"] = _options.HubUrl,
            // Base/footer fallbacks; a caller may overwrite eventDisplayName with the
            // live Event row value. eventCodeParens has no per-send override and is
            // supplied here so the layout footer always resolves. It is empty-safe:
            // blank code ⇒ "" (no stray "()"), otherwise " (ELDK27)".
            ["eventDisplayName"] = _options.EventDisplayName,
            ["eventCode"] = _options.EventCode,
            ["eventCodeParens"] = string.IsNullOrWhiteSpace(_options.EventCode)
                ? string.Empty
                : $" ({_options.EventCode})",
        };
        ApplyMagicHubUrl(tokens, participantId);
        return tokens;
    }

    /// <summary>
    /// §169 central seam: rewrite the <c>hubUrl</c> (and add a <c>magicHubUrl</c>)
    /// token to the participant's personal auto-login magic-link. Best-effort +
    /// fail-safe — see <see cref="NewTokenSet"/>.
    /// </summary>
    private void ApplyMagicHubUrl(Dictionary<string, string> tokens, int? participantIdOverride)
    {
        var origin = (_options.HubUrl ?? string.Empty).TrimEnd('/');
        if (string.IsNullOrEmpty(origin) || _scopes is null) return;

        var pid = participantIdOverride ?? _emailContext?.Current?.ParticipantId;
        if (pid is not int id || id <= 0) return;

        try
        {
            using var scope = _scopes.CreateScope();
            var magic = scope.ServiceProvider
                .GetService<IEmailMagicLinkService>();
            if (magic is null) return;

            var url = magic.BuildUrlForParticipantAsync(id, origin)
                .GetAwaiter().GetResult();
            tokens["hubUrl"] = url;
            tokens["magicHubUrl"] = url;
        }
        catch
        {
            // Fail-safe: a magic-link hiccup must never break a send — keep the
            // plain hub URL so the recipient can still sign in with email + PIN.
        }
    }

    /// <summary>
    /// Render the named content template (e.g. "task-deadline-reminder")
    /// with the supplied tokens. The ".html" extension is optional.
    /// </summary>
    public RenderedEmail Render(
        string templateName,
        IReadOnlyDictionary<string, string> tokens)
    {
        return _renderer.Value.Render(ResolveContent(templateName), tokens);
    }

    /// <summary>
    /// The effective template TEXT for the current edition: the per-edition override
    /// (§25h) if one is saved, else the shipped on-disk default. The edition is read from
    /// the ambient <see cref="EmailContext"/>; with no edition / no override wiring the
    /// shipped default is returned. Best-effort: any override-lookup error falls back to
    /// the default so a send never breaks on the override layer.
    /// </summary>
    private string ResolveContent(string templateName)
    {
        var eventId = _emailContext?.Current?.EventId ?? 0;
        if (eventId > 0 && _scopes is not null)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var store = scope.ServiceProvider.GetService<EmailTemplateOverrideStore>();
                var ovr = store?.GetOverrideTextAsync(eventId, TemplateKey(templateName))
                    .GetAwaiter().GetResult();
                if (!string.IsNullOrWhiteSpace(ovr)) return ovr;
            }
            catch { /* override is best-effort; fall through to the shipped default */ }
        }
        return GetContentTemplate(templateName);
    }

    /// <summary>The template key (file name without ".html") for override lookups.</summary>
    public static string TemplateKey(string templateName) =>
        templateName.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
            ? templateName.Substring(0, templateName.Length - 5)
            : templateName;

    /// <summary>The SHIPPED on-disk default text for a template (ignores overrides) — for the editor.</summary>
    public string GetDefaultText(string templateName) => GetContentTemplate(templateName);

    /// <summary>Render arbitrary template TEXT (e.g. an unsaved edit) with tokens — for the editor preview.</summary>
    public RenderedEmail RenderText(string rawText, IReadOnlyDictionary<string, string> tokens) =>
        _renderer.Value.Render(rawText, tokens);

    /// <summary>
    /// Render the named content template's BODY FRAGMENT ONLY — the
    /// <c>Subject:</c> line stripped and the email <c>_layout.html</c> shell NOT
    /// applied. Returns the substituted (HTML-encoded at the seam) body HTML, for
    /// surfaces that want the content but not the email shell — e.g. the first-login
    /// PORTAL welcome card. Honours the per-edition override / private-config layer
    /// exactly like <see cref="Render"/>.
    /// </summary>
    public string RenderBodyFragment(
        string templateName, IReadOnlyDictionary<string, string> tokens) =>
        _renderer.Value.RenderBodyFragment(ResolveContent(templateName), tokens);

    /// <summary>The shipped content-template keys (file names without ".html", excluding the _layout shell).</summary>
    public IReadOnlyList<string> ListTemplateKeys()
    {
        if (!Directory.Exists(_templateDirectory)) return Array.Empty<string>();
        return Directory.GetFiles(_templateDirectory, "*.html")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .Where(n => !string.IsNullOrEmpty(n) && !n.StartsWith("_", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private string GetContentTemplate(string templateName)
    {
        var fileName = templateName.EndsWith(
            ".html", StringComparison.OrdinalIgnoreCase)
            ? templateName
            : templateName + ".html";

        lock (_cacheLock)
        {
            if (_contentCache.TryGetValue(fileName, out var cached))
            {
                return cached;
            }

            // Resolution: PRIVATE per-edition file (publish-safe layer) wins over the
            // shipped default. The private dir holds an edition's real, named copy;
            // the shipped default is the generic open-source fallback.
            var privatePath = string.IsNullOrWhiteSpace(_privateTemplateDirectory)
                ? null
                : Path.Combine(_privateTemplateDirectory, fileName);
            var path = privatePath is not null && File.Exists(privatePath)
                ? privatePath
                : Path.Combine(_templateDirectory, fileName);

            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"Email content template not found: {path}");
            }

            var text = File.ReadAllText(path);
            _contentCache[fileName] = text;
            return text;
        }
    }
}
