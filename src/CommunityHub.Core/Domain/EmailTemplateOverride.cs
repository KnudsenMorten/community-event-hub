namespace CommunityHub.Core.Domain;

/// <summary>
/// A per-edition OVERRIDE of one on-disk email template (REQUIREMENTS §25h). The shipped
/// <c>templates/emails/&lt;key&gt;.html</c> file is the DEFAULT; when an organizer edits a
/// template in <c>/Organizer/EmailTemplates</c> the full edited text is stored here and
/// WINS at send time. No row ⇒ the shipped default applies. Reset-to-default = delete the
/// row. Unlike <see cref="ConfigOverride"/> (a deep-merged JSON fragment) this is the
/// WHOLE template text (subject line + body), replacing the file verbatim.
/// </summary>
public class EmailTemplateOverride
{
    public int Id { get; set; }

    /// <summary>The edition this override belongs to.</summary>
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>The template key = the on-disk file name without <c>.html</c>
    /// (e.g. <c>welcome</c>, <c>broadcast</c>, <c>group-photo-invite</c>).</summary>
    public string TemplateKey { get; set; } = string.Empty;

    /// <summary>The full edited template text — first line <c>Subject: …</c> then the body,
    /// same shape as the on-disk file. Rendered through the normal token engine + layout.</summary>
    public string OverrideText { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Audit: the organizer who last saved this override.</summary>
    public string? UpdatedByEmail { get; set; }
}
