using MagicMovieNight.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MagicMovieNight.Integrations.Arr;

/// <summary>
/// Adds a series to Sonarr.
///
/// Sonarr keys on TVDB ids, which is the awkward part: TMDB is where the rest of this
/// app's metadata comes from, and not every show TMDB knows has a TVDB id attached. When
/// it is missing the request is refused with an explanation rather than guessed at —
/// adding the wrong series is worse than not adding one.
/// </summary>
public class SonarrClient(
    HttpClient http,
    IOptions<SonarrOptions> options,
    ILogger<SonarrClient> logger) : ArrClient(http, logger)
{
    private readonly SonarrOptions _options = options.Value;

    public override string Name => "Sonarr";

    public override bool IsConfigured => _options.Enabled;

    protected override ArrOptions Options => _options;

    protected override string Resource => "series";

    public override async Task<ArrResult> RequestAsync(MediaItem item, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            return ArrResult.Fail("Sonarr is not configured.");
        }

        if (item.TvdbId is null)
        {
            return ArrResult.Fail(
                $"No TVDB id known for '{item.Title}', and Sonarr needs one. "
                + "Adding it by hand is safer than guessing at the wrong series.");
        }

        var (profileId, rootFolder, error) = await ResolveTargetsAsync(ct);

        if (error is not null)
        {
            return ArrResult.Fail(error);
        }

        var payload = new
        {
            title = item.Title,
            tvdbId = item.TvdbId.Value,
            qualityProfileId = profileId,
            rootFolderPath = rootFolder,
            monitored = true,
            seasonFolder = true,
            addOptions = new
            {
                monitor = _options.MonitorMode,
                searchForMissingEpisodes = _options.SearchOnAdd,
            },
        };

        return await PostAsync(payload, item.DisplayTitle, ct);
    }
}
