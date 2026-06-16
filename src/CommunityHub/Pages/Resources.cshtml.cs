using CommunityHub.Auth;
using CommunityHub.Core.Config;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages;

/// <summary>
/// The shared, read-only "Resources" page (REQUIREMENTS §1). One place for the
/// practical info, links and downloads the organizers maintain for everyone —
/// Wi-Fi, venue/floor-plan, exhibitor guide, slide templates, etc. Visible to
/// every signed-in role (the same content for all roles, by design).
///
/// Content is pure config: it lives in
/// <c>event.&lt;edition&gt;.json -&gt; resources</c> and is loaded via the
/// already-DI-registered <see cref="EventEditionConfigLoader"/>. There is NO
/// database row and NO schema change behind this page — organizers edit JSON.
/// When the section is missing or empty the page renders a friendly empty state.
/// </summary>
[Authorize]
public class ResourcesModel : PageModel
{
    private readonly EventEditionConfigLoader _eventConfigLoader;
    private readonly EventConfigOptions _eventConfigOptions;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly ILogger<ResourcesModel> _logger;

    public ResourcesModel(
        EventEditionConfigLoader eventConfigLoader,
        EventConfigOptions eventConfigOptions,
        ICurrentParticipantAccessor participant,
        ILogger<ResourcesModel> logger)
    {
        _eventConfigLoader = eventConfigLoader;
        _eventConfigOptions = eventConfigOptions;
        _participant = participant;
        _logger = logger;
    }

    public ResourcesConfig Resources { get; private set; } = new();

    public IActionResult OnGet()
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        try
        {
            Resources = _eventConfigLoader
                .Load(_eventConfigOptions.EventConfigPath)
                .Resources;
        }
        catch (Exception ex)
        {
            // A broken/missing config must never 500 the page — show the empty
            // state and log it so an organizer can spot the deploy issue.
            _logger.LogWarning(ex,
                "Resources: failed to load resource config from {Path}",
                _eventConfigOptions.EventConfigPath);
            Resources = new ResourcesConfig();
        }

        return Page();
    }
}
