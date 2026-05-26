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
    /// Branding tokens for the shared layout - from event.&lt;edition&gt;.json.
    /// These are the same for every email of an edition.
    /// </summary>
    public string BrandColor { get; set; } = "#1a1a2e";
    public string LogoUrl { get; set; } = string.Empty;
    public string SupportEmail { get; set; } = "info@expertslive.dk";
    public string HubUrl { get; set; } = string.Empty;
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
    private readonly EmailTemplateOptions _options;
    private readonly Lazy<EmailTemplateRenderer> _renderer;
    private readonly Dictionary<string, string> _contentCache = new();
    private readonly object _cacheLock = new();

    public EmailTemplateProvider(IOptions<EmailTemplateOptions> options)
    {
        _options = options.Value;
        _templateDirectory = _options.TemplateDirectory;
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
    /// A fresh token map pre-filled with the edition's branding tokens
    /// (brandColor, logoUrl, supportEmail, hubUrl). Callers add their
    /// content-specific tokens to this and pass it to <see cref="Render"/>.
    /// </summary>
    public Dictionary<string, string> NewTokenSet() => new()
    {
        ["brandColor"] = _options.BrandColor,
        ["logoUrl"] = _options.LogoUrl,
        ["supportEmail"] = _options.SupportEmail,
        ["hubUrl"] = _options.HubUrl,
    };

    /// <summary>
    /// Render the named content template (e.g. "task-deadline-reminder")
    /// with the supplied tokens. The ".html" extension is optional.
    /// </summary>
    public RenderedEmail Render(
        string templateName,
        IReadOnlyDictionary<string, string> tokens)
    {
        var content = GetContentTemplate(templateName);
        return _renderer.Value.Render(content, tokens);
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

            var path = Path.Combine(_templateDirectory, fileName);
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
