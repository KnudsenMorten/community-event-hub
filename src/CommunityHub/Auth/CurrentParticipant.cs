using System.Security.Claims;
using CommunityHub.Core.Domain;

namespace CommunityHub.Auth;

/// <summary>
/// The signed-in participant, read from the session cookie's claims (set by
/// the login flow in Stage 3). A thin, allocation-cheap view over the
/// ClaimsPrincipal so page models do not each re-parse claims.
/// </summary>
public sealed class CurrentParticipant
{
    private CurrentParticipant(
        int participantId, string email, string fullName,
        ParticipantRole role, int eventId)
    {
        ParticipantId = participantId;
        Email = email;
        FullName = fullName;
        Role = role;
        EventId = eventId;
    }

    public int ParticipantId { get; }
    public string Email { get; }
    public string FullName { get; }
    public ParticipantRole Role { get; }
    public int EventId { get; }

    /// <summary>First word of the full name, for greetings.</summary>
    public string FirstName =>
        string.IsNullOrWhiteSpace(FullName) ? "there" : FullName.Split(' ')[0];

    /// <summary>
    /// Build from a ClaimsPrincipal, or null if not authenticated / claims
    /// are malformed.
    /// </summary>
    public static CurrentParticipant? FromPrincipal(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var idStr = user.FindFirstValue(ClaimTypes.NameIdentifier);
        var email = user.FindFirstValue(ClaimTypes.Email);
        var name = user.FindFirstValue(ClaimTypes.Name);
        var roleStr = user.FindFirstValue(ClaimTypes.Role);
        var eventStr = user.FindFirstValue("EventId");

        if (!int.TryParse(idStr, out var id)
            || !int.TryParse(eventStr, out var eventId)
            || !Enum.TryParse<ParticipantRole>(roleStr, out var role))
        {
            return null;
        }

        return new CurrentParticipant(
            id, email ?? string.Empty, name ?? string.Empty, role, eventId);
    }
}

/// <summary>
/// Accessor for the current participant within a request scope.
/// </summary>
public interface ICurrentParticipantAccessor
{
    CurrentParticipant? Current { get; }
}

/// <summary>
/// Default accessor - reads the participant from the request's HttpContext.
/// </summary>
public sealed class HttpCurrentParticipantAccessor : ICurrentParticipantAccessor
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpCurrentParticipantAccessor(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public CurrentParticipant? Current =>
        CurrentParticipant.FromPrincipal(
            _httpContextAccessor.HttpContext?.User);
}
