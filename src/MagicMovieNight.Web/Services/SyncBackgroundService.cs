using MagicMovieNight.Integrations;
using MagicMovieNight.Integrations.Ingest;
using Microsoft.Extensions.Options;

namespace MagicMovieNight.Web.Services;

/// <summary>
/// Keeps history current in the background. Plex and Trakt both accumulate watches
/// continuously, and nobody wants to press a sync button before deciding what to
/// watch.
/// </summary>
public class SyncBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptions<HouseholdOptions> options,
    ILogger<SyncBackgroundService> logger) : BackgroundService
{
    /// <summary>Let the web host finish starting before hammering the network.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    private readonly HouseholdOptions _options = options.Value;

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

        using var timer = new PeriodicTimer(_options.SyncInterval);

        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var ingestion = scope.ServiceProvider.GetRequiredService<IngestionService>();

                var result = await ingestion.SyncAllAsync(stoppingToken);

                logger.LogInformation(
                    "Scheduled sync complete: {Imported} new events, {Skipped} already known.",
                    result.Imported, result.Skipped);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A failed sync must never take the app down — the UI still works off
                // whatever history is already in the database.
                logger.LogError(ex, "Scheduled sync failed; will retry next cycle.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
