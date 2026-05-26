using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// The WooCommerce pull job (CONTEXT.md section 9 / 11). Runs daily, fetches
/// completed shop orders, classifies each ordered product by category, and
/// expands it into the sponsor task list defined in sponsor.&lt;edition&gt;.json
/// via <see cref="SponsorTaskExpander"/> - so what tasks a sponsor gets is
/// controlled entirely by JSON, no code change.
///
/// Task creation is idempotent: each task carries a SourceKey of
/// "woo:{orderId}:{productId}:{title-slug}" and the job skips a task whose
/// SourceKey already exists, so re-running the pull never duplicates work.
/// </summary>
public sealed class WooCommercePullJob
{
    private readonly CommunityHubDbContext _db;
    private readonly WooCommerceClient _woo;
    private readonly SponsorProductClassifier _classifier;
    private readonly SponsorConfigLoader _configLoader;
    private readonly WooCommerceOptions _options;
    private readonly SponsorConfigOptions _configOptions;
    private readonly ILogger<WooCommercePullJob> _log;

    public WooCommercePullJob(
        CommunityHubDbContext db,
        WooCommerceClient woo,
        SponsorProductClassifier classifier,
        SponsorConfigLoader configLoader,
        WooCommerceOptions options,
        SponsorConfigOptions configOptions,
        ILogger<WooCommercePullJob> log)
    {
        _db = db;
        _woo = woo;
        _classifier = classifier;
        _configLoader = configLoader;
        _options = options;
        _configOptions = configOptions;
        _log = log;
    }

    /// <summary>Daily at 06:00 UTC.</summary>
    [Function("WooCommercePullJob")]
    public async Task Run(
        [TimerTrigger("0 0 6 * * *")] TimerInfo timer,
        CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            _log.LogInformation("WooCommercePullJob: disabled by config.");
            return;
        }

        var activeEvent = await _db.Events
            .Where(e => e.IsActive)
            .Select(e => new { e.Id, e.StartDate })
            .FirstOrDefaultAsync(ct);
        if (activeEvent is null)
        {
            _log.LogWarning("WooCommercePullJob: no active event.");
            return;
        }

        // Load the sponsor task-set config for this edition.
        SponsorTaskExpander expander;
        try
        {
            var config = _configLoader.Load(_configOptions.SponsorConfigPath);
            expander = new SponsorTaskExpander(config);
        }
        catch (FileNotFoundException ex)
        {
            _log.LogError(ex, "WooCommercePullJob: sponsor config missing.");
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var orders = await _woo.GetOrdersAsync("completed", ct);
        var created = 0;

        foreach (var order in orders)
        {
            var firstOrderDate = order.CreatedAt is not null
                ? DateOnly.FromDateTime(order.CreatedAt.Value.UtcDateTime)
                : (DateOnly?)null;

            foreach (var item in order.LineItems)
            {
                var cls = _classifier.Classify(
                    item.CategoriesText, item.ProductName);

                // Expand into the JSON-defined task list for this product.
                var sponsorTasks = expander.Expand(
                    cls, activeEvent.StartDate, firstOrderDate, today);

                foreach (var st in sponsorTasks)
                {
                    var sourceKey =
                        $"woo:{order.OrderId}:{item.ProductId}:{Slug(st.Title)}";

                    var exists = await _db.Tasks.AnyAsync(
                        t => t.EventId == activeEvent.Id
                             && t.SourceKey == sourceKey, ct);
                    if (exists)
                    {
                        continue; // idempotent
                    }

                    _db.Tasks.Add(new ParticipantTask
                    {
                        EventId = activeEvent.Id,
                        Title = st.Title,
                        Description =
                            $"Sponsor: {order.BillingCompany} " +
                            $"(order {order.OrderId}, {item.ProductName}).",
                        DueDate = st.DueDate,
                        State = TaskState.Open,
                        SourceKey = sourceKey,
                        SponsorCompanyId = order.CompanyId,
                    });
                    created++;
                }
            }
        }

        if (created > 0)
        {
            await _db.SaveChangesAsync(ct);
        }
        _log.LogInformation(
            "WooCommercePullJob: {Orders} orders, {Created} new tasks.",
            orders.Count, created);
    }

    /// <summary>A short, stable slug of a task title for the SourceKey.</summary>
    private static string Slug(string title)
    {
        var chars = title.ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || c == ' ')
            .ToArray();
        var slug = new string(chars).Replace(' ', '-');
        return slug.Length > 60 ? slug[..60] : slug;
    }
}
