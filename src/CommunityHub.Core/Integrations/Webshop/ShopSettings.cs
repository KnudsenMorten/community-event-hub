using CommunityHub.Core.Config;

namespace CommunityHub.Core.Integrations.Webshop;

/// <summary>
/// §1305 — the shop's configuration in one place: <c>config/shop.&lt;edition&gt;.json</c> and the event
/// start from <c>config/event.&lt;edition&gt;.json</c> (the anchor of the late-order surcharge).
/// </summary>
public sealed class ShopSettings
{
    private readonly ShopConfigLoader _loader;
    private readonly ShopConfigOptions _options;
    private readonly EventEditionConfigLoader? _eventLoader;
    private readonly EventConfigOptions? _eventOptions;

    public ShopSettings(
        ShopConfigLoader loader, ShopConfigOptions options,
        EventEditionConfigLoader? eventLoader = null, EventConfigOptions? eventOptions = null)
    {
        _loader = loader;
        _options = options;
        _eventLoader = eventLoader;
        _eventOptions = eventOptions;
    }

    /// <summary>
    /// The shop config (an empty one when the file is missing), with the environment's reservation-window override
    /// applied (<see cref="ShopConfigOptions.ReservationOpensOn"/>).
    /// </summary>
    public ShopConfig Shop()
    {
        var cfg = _loader.Load(_options.ConfigPath);
        if (!string.IsNullOrWhiteSpace(_options.ReservationOpensOn)
            && cfg.Reservation.OpensOn != _options.ReservationOpensOn.Trim())
        {
            // A new window object, so the cached config is never left half-overridden.
            cfg.Reservation = new ShopReservationWindow
            {
                OpensOn = _options.ReservationOpensOn.Trim(),
                WindowDays = cfg.Reservation.WindowDays,
                ReminderEveryDays = cfg.Reservation.ReminderEveryDays,
            };
        }
        return cfg;
    }

    /// <summary>True when the shop config carries what a contract needs.</summary>
    public bool IsConfigured => ShopConfigLoader.IsUsable(Shop());

    /// <summary>When the event starts (day 1), or null when the edition config has no usable date.</summary>
    public DateTimeOffset? EventStartsAt()
    {
        if (_eventLoader is null) return null;
        try
        {
            var path = (_eventOptions ?? new EventConfigOptions()).EventConfigPath;
            return _eventLoader.Load(path).Dates?.EventStartsAt();
        }
        catch
        {
            return null;
        }
    }
}
