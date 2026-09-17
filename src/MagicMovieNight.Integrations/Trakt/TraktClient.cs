using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using MagicMovieNight.Core.Abstractions;
using MagicMovieNight.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MagicMovieNight.Integrations.Trakt;

/// <summary>
/// Trakt is the spine of the whole system: Plex scrobbles into it via plextraktsync,
/// and the browser scrobbler extension pushes Netflix/Hulu/Prime watches into it too.
/// Anything the household watches on a streaming service reaches us through here.
/// </summary>
public class TraktClient(
    HttpClient http,
    TraktAuthService auth,
    IOptions<TraktOptions> options,
    ILogger<TraktClient> logger) : IHistorySource
{
    private const int PageSize = 100;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TraktOptions _options = options.Value;

    public WatchSource Source => WatchSource.Trakt;

    public bool IsConfigured => _options.Enabled && auth.IsAuthorized;

    public async IAsyncEnumerable<RawWatchEvent> PullAsync(
        DateTimeOffset since,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var token = await auth.GetAccessTokenAsync(ct);
        if (token is null)
        {
            logger.LogWarning("Trakt is not authorized; skipping history pull.");
            yield break;
        }

        var page = 1;
        while (!ct.IsCancellationRequested)
        {
            var url = $"/users/{_options.User}/history"
                + $"?start_at={since.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffZ}"
                + $"&page={page}&limit={PageSize}&extended=full";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("Trakt history page {Page} failed: {Status}", page, response.StatusCode);
                yield break;
            }

            var items = await response.Content.ReadFromJsonAsync<List<TraktHistoryItem>>(JsonOptions, ct);
            if (items is null || items.Count == 0)
            {
                yield break;
            }

            foreach (var item in items)
            {
                var mapped = Map(item);
                if (mapped is not null)
                {
                    yield return mapped;
                }
            }

            // Trakt reports total pages in a header; a short page means we are done.
            if (items.Count < PageSize)
            {
                yield break;
            }

            page++;
        }
    }

    /// <summary>
    /// Trakt's own personalized recommendations. A VIP account gets these tuned to the
    /// full watch history, which makes them a strong candidate source before Claude
    /// ever sees the pool.
    /// </summary>
    public async Task<IReadOnlyList<RawWatchEvent>> GetRecommendationsAsync(
        MediaKind kind,
        int limit = 50,
        CancellationToken ct = default)
    {
        var token = await auth.GetAccessTokenAsync(ct);
        if (token is null)
        {
            return [];
        }

        var path = kind == MediaKind.Movie ? "movies" : "shows";
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/recommendations/{path}?limit={limit}&ignore_collected=false&extended=full");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Trakt recommendations ({Kind}) failed: {Status}", kind, response.StatusCode);
            return [];
        }

        var items = await response.Content.ReadFromJsonAsync<List<TraktMedia>>(JsonOptions, ct) ?? [];

        return items
            .Where(i => i.Ids is not null)
            .Select(i => new RawWatchEvent
            {
                Title = i.Title ?? "Unknown",
                Year = i.Year,
                Kind = kind,
                SourceKey = $"rec:{kind}:{i.Ids!.Trakt}",
                WatchedAt = DateTimeOffset.UtcNow,
                TraktId = i.Ids.Trakt,
                TmdbId = i.Ids.Tmdb,
                ImdbId = i.Ids.Imdb,
                Fidelity = SourceFidelity.Attributed,
            })
            .ToList();
    }

    private RawWatchEvent? Map(TraktHistoryItem item)
    {
        // Trakt reports episodes with both the episode and its parent show; the show
        // carries the ids worth keeping, the episode carries the numbering.
        var media = item.Movie ?? item.Show;
        if (media?.Ids is null)
        {
            return null;
        }

        var kind = item.Type switch
        {
            "movie" => MediaKind.Movie,
            "episode" => MediaKind.Episode,
            _ => MediaKind.Show,
        };

        return new RawWatchEvent
        {
            Title = media.Title ?? "Unknown",
            Year = media.Year,
            Kind = kind,
            SourceKey = item.Id.ToString(),
            WatchedAt = item.WatchedAt,
            ExternalViewerId = _options.User,
            TraktId = media.Ids.Trakt,
            TmdbId = media.Ids.Tmdb,
            ImdbId = media.Ids.Imdb,
            SeasonNumber = item.Episode?.Season,
            EpisodeNumber = item.Episode?.Number,
            // Trakt records that a play happened, not how much of it — treat a
            // scrobbled play as a completed watch, which is what Trakt means by it.
            PercentComplete = null,
            Fidelity = SourceFidelity.Attributed,
        };
    }
}

public record TraktHistoryItem
{
    public long Id { get; init; }

    [JsonPropertyName("watched_at")]
    public DateTimeOffset WatchedAt { get; init; }

    public string? Type { get; init; }

    public TraktMedia? Movie { get; init; }

    public TraktMedia? Show { get; init; }

    public TraktEpisode? Episode { get; init; }
}

public record TraktMedia
{
    public string? Title { get; init; }

    public int? Year { get; init; }

    public string? Overview { get; init; }

    public int? Runtime { get; init; }

    public List<string>? Genres { get; init; }

    public double? Rating { get; init; }

    public string? Certification { get; init; }

    public TraktIds? Ids { get; init; }
}

public record TraktEpisode
{
    public int? Season { get; init; }

    public int? Number { get; init; }

    public string? Title { get; init; }

    public TraktIds? Ids { get; init; }
}

public record TraktIds
{
    public int? Trakt { get; init; }

    public string? Slug { get; init; }

    public string? Imdb { get; init; }

    public int? Tmdb { get; init; }

    public int? Tvdb { get; init; }
}
