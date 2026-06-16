using System.Security.Claims;
using CommunityHub.Core.Auth;
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
        ParticipantRole role, int eventId,
        ActingAsContext? actingAs)
    {
        ParticipantId = participantId;
        Email = email;
        FullName = fullName;
        Role = role;
        EventId = eventId;
        ActingAs = actingAs;
    }

    public int ParticipantId { get; }
    public string Email { get; }
    public string FullName { get; }
    public ParticipantRole Role { get; }
    public int EventId { get; }

    /// <summary>
    /// Non-null when this is an <b>acting-as</b> session — i.e. the identity
    /// above is the TARGET participant, but the session was established by an
    /// organizer (or a secretary token) acting on their behalf. The whole app
    /// reads <see cref="ParticipantId"/> / <see cref="Role"/> as usual, so every
    /// page naturally renders the target's view and on-behalf writes land on the
    /// target's own rows; this property is what the UI uses to show the
    /// "you are acting as …" banner and the "return to organizer" control, and
    /// what the server uses to BLOCK starting a nested impersonation.
    /// </summary>
    public ActingAsContext? ActingAs { get; }

    /// <summary>True when this session is impersonating another participant.</summary>
    public bool IsActingAs => ActingAs is not null;

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

        // Acting-as marker claims (set by the impersonation / secretary-token
        // sign-in). Their presence flips this session into an on-behalf session.
        // Parsing lives in Core's ActingAsClaims so the contract is unit-testable.
        var actingAs = ActingAsClaims.Parse(
            user.FindFirstValue(ActingAsClaims.ActorKind),
            user.FindFirstValue(ActingAsClaims.ActorParticipantId),
            user.FindFirstValue(ActingAsClaims.ActorLabel));

        return new CurrentParticipant(
            id, email ?? string.Empty, name ?? string.Empty, role, eventId, actingAs);
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
