using MagicMovieNight.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MagicMovieNight.Integrations.Arr;

/// <summary>
/// Adds a film to Radarr. Radarr keys on TMDB ids, which this app already has for
/// anything the catalog enriched, so a film is almost always requestable.
/// </summary>
public class RadarrClient(
    HttpClient http,
    IOptions<RadarrOptions> options,
    ILogger<RadarrClient> logger) : ArrClient(http, logger)
{
    private readonly RadarrOptions _options = options.Value;

    public override string Name => "Radarr";

    public override bool IsConfigured => _options.Enabled;

    protected override ArrOptions Options => _options;

    protected override string Resource => "movie";

    public override async Task<ArrResult> RequestAsync(MediaItem item, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            return ArrResult.Fail("Radarr is not configured.");
        }

        if (item.TmdbId is null)
        {
            return ArrResult.Fail(
                $"No TMDB id known for '{item.Title}', and Radarr needs one.");
        }

        var (profileId, rootFolder, error) = await ResolveTargetsAsync(ct);

        if (error is not null)
        {
            return ArrResult.Fail(error);
        }

        var payload = new
        {
            title = item.Title,
            tmdbId = item.TmdbId.Value,
            qualityProfileId = profileId,
            rootFolderPath = rootFolder,
            monitored = true,
            minimumAvailability = _options.MinimumAvailability,
            addOptions = new { searchForMovie = _options.SearchOnAdd },
        };

        return await PostAsync(payload, item.DisplayTitle, ct);
    }
}
