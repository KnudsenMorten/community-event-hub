using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// §324: mint the LinkedIn posting token. The Key Vault holds the LinkedIn APP's
/// client id/secret, but LinkedIn only issues an ACCESS token through a member's
/// consent — so a page-admin organizer clicks Connect once: LinkedIn's consent
/// screen → authorization code → token exchanged + stored (LinkedInTokenStore),
/// auto-refreshed thereafter. Organizer-only.
/// </summary>
[Authorize]
public class LinkedInConnectModel : PageModel
{
    private const string StateKey = "LinkedInOAuthState";

    private readonly LinkedInOptions _options;
    private readonly LinkedInTokenStore _tokens;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<LinkedInConnectModel> _log;

    public LinkedInConnectModel(
        LinkedInOptions options, LinkedInTokenStore tokens,
        ICurrentParticipantAccessor participant, IHttpClientFactory httpFactory,
        ILogger<LinkedInConnectModel> log)
    {
        _options = options;
        _tokens = tokens;
        _participant = participant;
        _httpFactory = httpFactory;
        _log = log;
    }

    public bool AccessDenied { get; private set; }
    public bool AppConfigured { get; private set; }
    public LinkedInOAuthToken? Current { get; private set; }
    public string? Message { get; private set; }
    public bool IsError { get; private set; }

    /// <summary>
    /// §824.10 — the scopes this connect will ask LinkedIn to approve, shown on the page.
    /// </summary>
    /// <remarks>
    /// Printed because a consent failure is otherwise unattributable: LinkedIn rejects the whole
    /// screen when the app lacks product access for ONE scope, and its error does not say which.
    /// Seeing the list before pressing Connect is what makes that diagnosable in one look.
    /// </remarks>
    public IReadOnlyList<string> RequestedScopes => _options.ScopeList;

    private string RedirectUri => $"{Request.Scheme}://{Request.Host}/Organizer/LinkedInConnect?handler=Callback";

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        AppConfigured = !string.IsNullOrWhiteSpace(_options.ClientId)
                        && !string.IsNullOrWhiteSpace(_options.ClientSecret);
        Current = await _tokens.GetOrgTokenRowAsync(ct);
        Message = TempData["LinkedInConnectMessage"] as string;
        IsError = TempData["LinkedInConnectIsError"] as bool? ?? false;
        return Page();
    }

    /// <summary>Start the consent flow — redirect to LinkedIn's authorization screen.</summary>
    public IActionResult OnPostStart()
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        // §337: minting the ORG-WIDE posting token is a real organizer write — an
        // acting-as session carries the target's Organizer role, so the role check
        // alone would let it start the consent flow on someone else's identity.
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }
        if (string.IsNullOrWhiteSpace(_options.ClientId)) return RedirectToPage();

        var state = Guid.NewGuid().ToString("N");
        TempData[StateKey] = state;
        var url = "https://www.linkedin.com/oauth/v2/authorization"
            + "?response_type=code"
            + $"&client_id={Uri.EscapeDataString(_options.ClientId!)}"
            + $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}"
            + $"&state={state}"
            // §824.10 — the scope list is CONFIGURATION now, not a literal. The two write scopes
            // could POST but could not LOOK UP a sponsor's organization id, which is what
            // {SponsorLinkedInUrl} needs to become a real company mention (§824.3).
            // ⚠️ LinkedIn refuses the WHOLE consent screen when the app lacks product access for any
            // single scope requested — so if this errors, set LinkedIn__Scopes back to
            // "w_member_social w_organization_social". Posting never depended on the read scopes.
            + "&scope=" + Uri.EscapeDataString(_options.Scopes ?? string.Empty);
        return Redirect(url);
    }

    /// <summary>LinkedIn redirects back here with ?code= — exchange + store the token.</summary>
    public async Task<IActionResult> OnGetCallbackAsync(
        string? code, string? state, string? error, string? error_description, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        // §337: this handler STORES the token — it is the write, not just the redirect.
        // Guarding only OnPostStart would leave the storing leg reachable on a replay.
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        if (!string.IsNullOrEmpty(error))
        {
            return Flash($"LinkedIn said no: {error} — {error_description}", isError: true);
        }
        if (string.IsNullOrEmpty(code) || TempData[StateKey] as string != state)
        {
            return Flash("The connect attempt didn't complete (missing code or state mismatch) — try again.", isError: true);
        }

        try
        {
            var http = _httpFactory.CreateClient();
            using var resp = await http.PostAsync(_options.TokenEndpoint,
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = code,
                    ["redirect_uri"] = RedirectUri,
                    ["client_id"] = _options.ClientId!,
                    ["client_secret"] = _options.ClientSecret!,
                }), ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("LinkedIn token exchange failed ({Status}): {Body}", (int)resp.StatusCode, body);
                return Flash($"Token exchange failed (HTTP {(int)resp.StatusCode}). Check that the redirect URL "
                    + $"'{RedirectUri}' is authorized on the LinkedIn app.", isError: true);
            }
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var access = doc.RootElement.GetProperty("access_token").GetString()!;
            var refresh = doc.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
            var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 60 * 60 * 24 * 60;
            await _tokens.SaveOrgTokenAsync(access, refresh, expiresIn, me.Email, ct);
            return Flash("✅ LinkedIn connected — the posting token is stored"
                + (refresh is null ? " (no refresh token issued — reconnect when it expires)." : " and will auto-refresh."),
                isError: false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "LinkedIn connect callback failed.");
            return Flash("The connect failed unexpectedly — try again.", isError: true);
        }
    }

    private IActionResult Flash(string message, bool isError)
    {
        TempData["LinkedInConnectMessage"] = message;
        TempData["LinkedInConnectIsError"] = isError;
        return RedirectToPage();
    }
}
