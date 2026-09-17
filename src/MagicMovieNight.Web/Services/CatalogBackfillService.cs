using MagicMovieNight.Core.Abstractions;

namespace MagicMovieNight.Web.Services;

/// <summary>
/// Repairs catalog entries that were created while TMDB was unavailable.
///
/// Enrichment normally happens once, when a title is first seen. If TMDB is down,
/// misconfigured, or rate-limiting at that moment, the entry is stored bare and nothing
/// revisits it — a film watched once and never again would have no genres, cast or
/// runtime forever, and the recommender would be reasoning about a title it knows
/// nothing but the name of.
///
/// This sweeps in small batches so it never monopolises TMDB or the database, and it
/// stops as soon as there is nothing left to fix.
/// </summary>
public class CatalogBackfillService(
    IServiceScopeFactory scopeFactory,
    ILogger<CatalogBackfillService> logger) : BackgroundService
{
    /// <summary>Start after the history sync, so newly ingested titles are included.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);

    /// <summary>Small enough that a restart loses almost nothing.</summary>
    private const int BatchSize = 25;

    /// <summary>Keeps TMDB well inside its rate limits.</summary>
    private static readonly TimeSpan BetweenBatches = TimeSpan.FromSeconds(2);

    /// <summary>How long to wait once the catalog is fully enriched.</summary>
    private static readonly TimeSpan WhenIdle = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var sweepTotal = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var catalog = scope.ServiceProvider.GetRequiredService<ICatalogService>();

                var processed = await catalog.BackfillAsync(BatchSize, stoppingToken);

                if (processed == 0)
                {
                    if (sweepTotal > 0)
                    {
                        logger.LogInformation(
                            "Catalog backfill finished: {Total} entries enriched.", sweepTotal);
                        sweepTotal = 0;
                    }

                    await Task.Delay(WhenIdle, stoppingToken);
                    continue;
                }

                sweepTotal += processed;

                if (sweepTotal % 250 < BatchSize)
                {
                    logger.LogInformation("Catalog backfill in progress: {Total} so far.", sweepTotal);
                }

                await Task.Delay(BetweenBatches, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A backfill failure must never take the app down; the next pass retries.
                logger.LogError(ex, "Catalog backfill batch failed; retrying shortly.");
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
    }
}
