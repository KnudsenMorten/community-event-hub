namespace CommunityHub.Core.Email;

/// <summary>
/// Ambient metadata about the email currently being sent, set by a caller just
/// before it calls <see cref="IEmailSender"/> so the logging decorator
/// (<c>LoggingEmailSender</c>) can record a rich <c>EmailLog</c> row WITHOUT
/// every call site having to pass extra arguments. Set it via
/// <see cref="IEmailContextAccessor.Set"/> (ideally in a <c>using</c> scope);
/// when nothing is set the decorator falls back to category "other".
/// </summary>
/// <param name="TemplateName">
/// The branded template this send rendered, when the caller sent via a template
/// (e.g. the per-participant <c>ParticipantEmailService</c> path). Recorded on
/// the <c>EmailLog</c> row so an organizer can <b>re-send the exact same email</b>
/// after a failure without re-typing it. Null for raw/ad-hoc sends (broadcast,
/// PIN) that did not come from a named template.
/// </param>
public sealed record EmailContext(
    string Category,
    int EventId = 0,
    int? ParticipantId = null,
    string? RecipientName = null,
    string? TemplateName = null);

/// <summary>
/// Holds the current <see cref="EmailContext"/> for the logical async flow.
/// Scoped per request/job; backed by <c>AsyncLocal</c> so a background thread
/// inside the same flow sees it too.
/// </summary>
public interface IEmailContextAccessor
{
    EmailContext? Current { get; }

    /// <summary>
    /// Set the ambient context and return an <see cref="IDisposable"/> that
    /// restores the previous value when disposed — use in a <c>using</c> so the
    /// context never leaks past the send it describes.
    /// </summary>
    IDisposable Set(EmailContext context);
}

/// <inheritdoc />
public sealed class EmailContextAccessor : IEmailContextAccessor
{
    private static readonly AsyncLocal<EmailContext?> _current = new();

    public EmailContext? Current => _current.Value;

    public IDisposable Set(EmailContext context)
    {
        var previous = _current.Value;
        _current.Value = context;
        return new Restore(previous);
    }

    private sealed class Restore : IDisposable
    {
        private readonly EmailContext? _previous;
        private bool _done;
        public Restore(EmailContext? previous) => _previous = previous;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            _current.Value = _previous;
        }
    }
}
