using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using MagicMovieNight.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MagicMovieNight.Integrations.Tmdb;

/// <summary>
/// TMDB supplies the metadata every other source omits: genres, cast, runtime, and —
/// critically — which services currently carry a title in the household's region.
/// Without availability, recommendations degrade into "here is a great film you
/// cannot watch tonight".
/// </summary>
public class TmdbClient(
    HttpClient http,
    IOptions<TmdbOptions> options,
    ILogger<TmdbClient> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TmdbOptions _options = options.Value;

    public bool IsConfigured => _options.Enabled;

    public async Task<TmdbSearchResult?> SearchAsync(
        string title,
        int? year,
        MediaKind kind,
        CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            return null;
        }

        var isShow = kind is MediaKind.Show or MediaKind.Episode;
        var path = isShow ? "search/tv" : "search/movie";
        var yearParam = year is null
            ? string.Empty
            : isShow ? $"&first_air_date_year={year}" : $"&year={year}";

        // No leading slash: with a leading slash the base path ("/3") is discarded.
        var url = $"{path}?query={HttpUtility.UrlEncode(title)}{yearParam}&include_adult=false";

        try
        {
            var response = await http.GetFromJsonAsync<TmdbSearchResponse>(url, JsonOptions, ct);
            var best = response?.Results?.FirstOrDefault();

            if (best is null && year is not null)
            {
                // Release years disagree across sources often enough that a year-filtered
                // miss is worth one retry without the filter.
                return await SearchAsync(title, null, kind, ct);
            }

            return best;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            logger.LogWarning(ex, "TMDB search failed for '{Title}'.", title);
            return null;
        }
    }

    /// <summary>Full detail including credits and regional streaming availability.</summary>
    public async Task<TmdbDetails?> GetDetailsAsync(
        int tmdbId,
        MediaKind kind,
        CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            return null;
        }

        var isShow = kind is MediaKind.Show or MediaKind.Episode;
        var path = isShow ? "tv" : "movie";
        var url = $"{path}/{tmdbId}?append_to_response=credits,watch/providers,external_ids";

        try
        {
            return await http.GetFromJsonAsync<TmdbDetails>(url, JsonOptions, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            logger.LogWarning(ex, "TMDB details failed for {Kind} {Id}.", kind, tmdbId);
            return null;
        }
    }

    /// <summary>
    /// Flattens TMDB's provider block into plain service names. Flatrate means
    /// "included in a subscription you may already have"; rent and buy are kept
    /// separate because they cost money and change the recommendation's framing.
    /// </summary>
    public IReadOnlyList<string> ExtractProviders(TmdbDetails details)
    {
        if (details.WatchProviders?.Results is null
            || !details.WatchProviders.Results.TryGetValue(_options.WatchRegion, out var region))
        {
            return [];
        }

        var names = new List<string>();

        foreach (var p in region.Flatrate ?? [])
        {
            names.Add(p.ProviderName!);
        }

        foreach (var p in region.Free ?? [])
        {
            names.Add(p.ProviderName!);
        }

        foreach (var p in region.Rent ?? [])
        {
            names.Add($"{p.ProviderName} (rent)");
        }

        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public string? PosterUrl(string? posterPath) =>
        string.IsNullOrWhiteSpace(posterPath) ? null : _options.ImageBaseUrl + posterPath;
}

public record TmdbSearchResponse
{
    public List<TmdbSearchResult>? Results { get; init; }
}

public record TmdbSearchResult
{
    public int Id { get; init; }

    public string? Title { get; init; }

    public string? Name { get; init; }

    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; init; }

    [JsonPropertyName("first_air_date")]
    public string? FirstAirDate { get; init; }

    public string? Overview { get; init; }

    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; init; }

    [JsonPropertyName("vote_average")]
    public double? VoteAverage { get; init; }

    public string? DisplayName => Title ?? Name;

    public int? Year
    {
        get
        {
            var date = ReleaseDate ?? FirstAirDate;
            return date?.Length >= 4 && int.TryParse(date[..4], out var y) ? y : null;
        }
    }
}

public record TmdbDetails
{
    public int Id { get; init; }

    public string? Title { get; init; }

    public string? Name { get; init; }

    public string? Overview { get; init; }

    public int? Runtime { get; init; }

    [JsonPropertyName("episode_run_time")]
    public List<int>? EpisodeRunTime { get; init; }

    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; init; }

    [JsonPropertyName("first_air_date")]
    public string? FirstAirDate { get; init; }

    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; init; }

    [JsonPropertyName("vote_average")]
    public double? VoteAverage { get; init; }

    public List<TmdbGenre>? Genres { get; init; }

    public TmdbCredits? Credits { get; init; }

    [JsonPropertyName("watch/providers")]
    public TmdbWatchProviders? WatchProviders { get; init; }

    [JsonPropertyName("external_ids")]
    public TmdbExternalIds? ExternalIds { get; init; }

    public int? RuntimeMinutes => Runtime ?? EpisodeRunTime?.FirstOrDefault();

    public int? Year
    {
        get
        {
            var date = ReleaseDate ?? FirstAirDate;
            return date?.Length >= 4 && int.TryParse(date[..4], out var y) ? y : null;
        }
    }
}

public record TmdbGenre
{
    public int Id { get; init; }

    public string? Name { get; init; }
}

public record TmdbCredits
{
    public List<TmdbPerson>? Cast { get; init; }

    public List<TmdbPerson>? Crew { get; init; }
}

public record TmdbPerson
{
    public string? Name { get; init; }

    public string? Job { get; init; }

    public string? Department { get; init; }

    public int Order { get; init; }
}

public record TmdbWatchProviders
{
    public Dictionary<string, TmdbRegionProviders>? Results { get; init; }
}

public record TmdbRegionProviders
{
    public List<TmdbProvider>? Flatrate { get; init; }

    public List<TmdbProvider>? Rent { get; init; }

    public List<TmdbProvider>? Buy { get; init; }

    public List<TmdbProvider>? Free { get; init; }
}

public record TmdbProvider
{
    [JsonPropertyName("provider_name")]
    public string? ProviderName { get; init; }

    [JsonPropertyName("provider_id")]
    public int ProviderId { get; init; }
}

public record TmdbExternalIds
{
    [JsonPropertyName("imdb_id")]
    public string? ImdbId { get; init; }

    [JsonPropertyName("tvdb_id")]
    public int? TvdbId { get; init; }
}
