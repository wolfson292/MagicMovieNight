using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using MagicMovieNight.Core.Abstractions;
using MagicMovieNight.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MagicMovieNight.Integrations.Tautulli;

/// <summary>
/// Tautulli is the highest-fidelity source we have: it knows which household member
/// pressed play, on which device, and how far they actually got. Trakt only records
/// that a play happened. Where the two overlap on Plex content, Tautulli wins.
/// </summary>
public class TautulliClient(
    HttpClient http,
    IOptions<TautulliOptions> options,
    ILogger<TautulliClient> logger) : IHistorySource
{
    private const int PageSize = 500;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TautulliOptions _options = options.Value;

    public WatchSource Source => WatchSource.Tautulli;

    public bool IsConfigured => _options.Enabled;

    public async IAsyncEnumerable<RawWatchEvent> PullAsync(
        DateTimeOffset since,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            yield break;
        }

        var start = 0;
        while (!ct.IsCancellationRequested)
        {
            var url = $"/api/v2?apikey={_options.ApiKey}&cmd=get_history"
                + $"&order_column=date&order_dir=asc"
                + $"&start={start}&length={PageSize}"
                + $"&after={since.UtcDateTime:yyyy-MM-dd}";

            TautulliEnvelope? envelope;
            try
            {
                envelope = await http.GetFromJsonAsync<TautulliEnvelope>(url, JsonOptions, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException)
            {
                logger.LogError(ex, "Tautulli history request failed at offset {Start}.", start);
                yield break;
            }

            var rows = envelope?.Response?.Data?.Data;
            if (rows is null || rows.Count == 0)
            {
                yield break;
            }

            foreach (var row in rows)
            {
                // Tautulli logs every start, including 4-second mis-clicks. Those are
                // noise for taste, but they are genuine abandonment signal, so keep
                // them and let the profile builder decide.
                yield return Map(row);
            }

            if (rows.Count < PageSize)
            {
                yield break;
            }

            start += PageSize;
        }
    }

    private static RawWatchEvent Map(TautulliHistoryRow row)
    {
        var kind = row.MediaType switch
        {
            "movie" => MediaKind.Movie,
            "episode" => MediaKind.Episode,
            "show" or "season" => MediaKind.Show,
            _ => MediaKind.Movie,
        };

        // For episodes the grandparent title is the show name, which is what we want
        // to attribute taste to — nobody has an affinity for "Chapter 4".
        var title = kind == MediaKind.Episode
            ? row.GrandparentTitle ?? row.Title ?? "Unknown"
            : row.Title ?? row.FullTitle ?? "Unknown";

        return new RawWatchEvent
        {
            Title = title,
            Year = row.Year,
            Kind = kind,
            SourceKey = row.RowId?.ToString() ?? $"{row.Date}:{row.RatingKey}:{row.UserId}",
            WatchedAt = DateTimeOffset.FromUnixTimeSeconds(row.Date),
            ExternalViewerId = row.User ?? row.FriendlyName,
            PercentComplete = row.PercentComplete,
            Device = row.Player,
            SeasonNumber = row.ParentMediaIndex,
            EpisodeNumber = row.MediaIndex,
            Fidelity = SourceFidelity.Full,
        };
    }
}

public record TautulliEnvelope
{
    public TautulliResponse? Response { get; init; }
}

public record TautulliResponse
{
    public string? Result { get; init; }

    public string? Message { get; init; }

    public TautulliData? Data { get; init; }
}

public record TautulliData
{
    [JsonPropertyName("recordsFiltered")]
    public int RecordsFiltered { get; init; }

    [JsonPropertyName("recordsTotal")]
    public int RecordsTotal { get; init; }

    public List<TautulliHistoryRow>? Data { get; init; }
}

public record TautulliHistoryRow
{
    [JsonPropertyName("row_id")]
    public long? RowId { get; init; }

    public long Date { get; init; }

    public string? User { get; init; }

    [JsonPropertyName("user_id")]
    public long UserId { get; init; }

    [JsonPropertyName("friendly_name")]
    public string? FriendlyName { get; init; }

    [JsonPropertyName("media_type")]
    public string? MediaType { get; init; }

    public string? Title { get; init; }

    [JsonPropertyName("full_title")]
    public string? FullTitle { get; init; }

    [JsonPropertyName("grandparent_title")]
    public string? GrandparentTitle { get; init; }

    public int? Year { get; init; }

    [JsonPropertyName("percent_complete")]
    public int? PercentComplete { get; init; }

    public string? Player { get; init; }

    [JsonPropertyName("rating_key")]
    public string? RatingKey { get; init; }

    [JsonPropertyName("parent_media_index")]
    public int? ParentMediaIndex { get; init; }

    [JsonPropertyName("media_index")]
    public int? MediaIndex { get; init; }
}
