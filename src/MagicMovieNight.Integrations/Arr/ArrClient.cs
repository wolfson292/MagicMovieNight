using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MagicMovieNight.Core.Models;
using Microsoft.Extensions.Logging;

namespace MagicMovieNight.Integrations.Arr;

/// <summary>
/// Shared client for Sonarr and Radarr. They are close enough to be one class: both are
/// v3 APIs that take an external id, a quality profile and a root folder, and differ
/// mainly in which id they key on — Sonarr wants a TVDB id, Radarr a TMDB id.
///
/// Adding something that is already there is treated as success rather than an error.
/// From the household's point of view "already requested" and "just requested" mean the
/// same thing: it is on its way.
/// </summary>
public abstract class ArrClient(HttpClient http, ILogger logger)
{
    protected static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public abstract string Name { get; }

    public abstract bool IsConfigured { get; }

    protected abstract ArrOptions Options { get; }

    /// <summary>Which resource path this server uses: "series" or "movie".</summary>
    protected abstract string Resource { get; }

    public abstract Task<ArrResult> RequestAsync(MediaItem item, CancellationToken ct = default);

    /// <summary>
    /// Resolves the quality profile and root folder, preferring configuration and
    /// falling back to whatever the server lists first.
    ///
    /// The fallback is a convenience, not a good default: profile order is arbitrary and
    /// the first one is often "Any", while a Sonarr install can easily have several root
    /// folders where only one holds series. Configure both when it matters.
    /// </summary>
    protected async Task<(int? ProfileId, string? RootFolder, string? Error)> ResolveTargetsAsync(
        CancellationToken ct)
    {
        try
        {
            int? profileId = Options.QualityProfileId > 0 ? Options.QualityProfileId : null;

            if (profileId is null)
            {
                var profiles = await http.GetFromJsonAsync<List<ArrNamedId>>(
                    "api/v3/qualityprofile", JsonOptions, ct);

                profileId = profiles?.FirstOrDefault()?.Id;
            }

            var rootFolder = Options.RootFolderPath;

            if (string.IsNullOrWhiteSpace(rootFolder))
            {
                var folders = await http.GetFromJsonAsync<List<ArrRootFolder>>(
                    "api/v3/rootfolder", JsonOptions, ct);

                rootFolder = folders?.FirstOrDefault()?.Path;
            }

            if (profileId is null || string.IsNullOrWhiteSpace(rootFolder))
            {
                return (null, null,
                    $"{Name} has no quality profile or root folder configured to add into.");
            }

            return (profileId, rootFolder, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            logger.LogWarning(ex, "Could not read {Name} settings.", Name);
            return (null, null, $"Could not reach {Name}: {ex.Message}");
        }
    }

    protected async Task<ArrResult> PostAsync(object payload, string title, CancellationToken ct)
    {
        try
        {
            using var response = await http.PostAsJsonAsync($"api/v3/{Resource}", payload, JsonOptions, ct);

            if (response.IsSuccessStatusCode)
            {
                logger.LogInformation("Requested '{Title}' on {Name}.", title, Name);
                return ArrResult.Ok($"Requested on {Name}");
            }

            var body = await response.Content.ReadAsStringAsync(ct);

            // Both servers answer 400 with a validation list when the item already
            // exists. That is not a failure worth showing as one.
            if (response.StatusCode == HttpStatusCode.BadRequest
                && body.Contains("already", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation("'{Title}' is already in {Name}.", title, Name);
                return ArrResult.Ok($"Already in {Name}");
            }

            logger.LogWarning(
                "{Name} rejected '{Title}': {Status} {Body}", Name, title, response.StatusCode, body);

            return ArrResult.Fail($"{Name} said no ({(int)response.StatusCode}). {Summarise(body)}");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogError(ex, "Request to {Name} failed for '{Title}'.", Name, title);
            return ArrResult.Fail($"Could not reach {Name}: {ex.Message}");
        }
    }

    /// <summary>Arr validation errors come back as a wall of JSON; show the first message.</summary>
    internal static string Summarise(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);

            if (doc.RootElement.ValueKind == JsonValueKind.Array
                && doc.RootElement.GetArrayLength() > 0
                && doc.RootElement[0].TryGetProperty("errorMessage", out var message))
            {
                return message.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
            // Not JSON; fall through to the truncated raw body.
        }

        return body.Length > 200 ? body[..200] : body;
    }
}

public record ArrResult(bool Success, string Message)
{
    public static ArrResult Ok(string message) => new(true, message);

    public static ArrResult Fail(string message) => new(false, message);
}

public record ArrNamedId
{
    public int Id { get; init; }

    public string? Name { get; init; }
}

public record ArrRootFolder
{
    public int Id { get; init; }

    public string? Path { get; init; }

    [JsonPropertyName("accessible")]
    public bool Accessible { get; init; } = true;
}
