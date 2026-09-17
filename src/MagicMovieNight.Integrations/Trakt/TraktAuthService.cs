using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MagicMovieNight.Integrations.Trakt;

/// <summary>
/// Trakt OAuth via the device flow. A container has no browser and no redirect URI,
/// so the device flow is the only sane option: the app shows a code, someone types it
/// at trakt.tv/activate once, and the refresh token carries it from there.
/// </summary>
public class TraktAuthService(
    HttpClient http,
    IOptions<TraktOptions> options,
    ILogger<TraktAuthService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TraktOptions _options = options.Value;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private TraktToken? _cached;

    /// <summary>True when a usable token is on disk. The UI shows the pairing prompt when it is not.</summary>
    public bool IsAuthorized => LoadToken() is not null;

    /// <summary>
    /// Starts pairing. The returned code is shown to the household; polling continues
    /// in the background until they activate it or the code expires.
    /// </summary>
    public async Task<TraktDeviceCode> StartDeviceAuthAsync(CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync(
            "/oauth/device/code",
            new { client_id = _options.ClientId },
            ct);

        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<TraktDeviceCode>(JsonOptions, ct))
            ?? throw new InvalidOperationException("Trakt returned an empty device code response.");
    }

    /// <summary>
    /// Polls until the user activates the code. Trakt answers 400 while pending, which
    /// is expected rather than an error; 409/410/418 are terminal.
    /// </summary>
    public async Task<bool> PollForTokenAsync(TraktDeviceCode code, CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(code.ExpiresIn);
        var interval = TimeSpan.FromSeconds(Math.Max(code.Interval, 1));

        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(interval, ct);

            var response = await http.PostAsJsonAsync(
                "/oauth/device/token",
                new
                {
                    code = code.DeviceCode,
                    client_id = _options.ClientId,
                    client_secret = _options.ClientSecret,
                },
                ct);

            if (response.IsSuccessStatusCode)
            {
                var token = await response.Content.ReadFromJsonAsync<TraktToken>(JsonOptions, ct);
                if (token is not null)
                {
                    SaveToken(token);
                    logger.LogInformation("Trakt device authorization completed.");
                    return true;
                }
            }

            switch ((int)response.StatusCode)
            {
                case 400:
                    continue; // Still waiting for the user.
                case 409:
                    logger.LogWarning("Trakt device code already used.");
                    return false;
                case 410:
                    logger.LogWarning("Trakt device code expired before activation.");
                    return false;
                case 418:
                    logger.LogWarning("Trakt device authorization denied by the user.");
                    return false;
                case 429:
                    // Slow down and keep going.
                    interval += TimeSpan.FromSeconds(1);
                    continue;
            }
        }

        return false;
    }

    /// <summary>Returns a valid access token, refreshing it when it is close to expiry.</summary>
    public async Task<string?> GetAccessTokenAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var token = LoadToken();
            if (token is null)
            {
                return null;
            }

            // Refresh a day early rather than discovering expiry mid-sync.
            if (token.ExpiresAt > DateTimeOffset.UtcNow.AddDays(1))
            {
                return token.AccessToken;
            }

            logger.LogInformation("Refreshing Trakt access token.");

            var response = await http.PostAsJsonAsync(
                "/oauth/token",
                new
                {
                    refresh_token = token.RefreshToken,
                    client_id = _options.ClientId,
                    client_secret = _options.ClientSecret,
                    redirect_uri = "urn:ietf:wg:oauth:2.0:oob",
                    grant_type = "refresh_token",
                },
                ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError(
                    "Trakt token refresh failed with {Status}. Re-pairing is required.",
                    response.StatusCode);
                return null;
            }

            var refreshed = await response.Content.ReadFromJsonAsync<TraktToken>(JsonOptions, ct);
            if (refreshed is null)
            {
                return null;
            }

            SaveToken(refreshed);
            return refreshed.AccessToken;
        }
        finally
        {
            _lock.Release();
        }
    }

    private TraktToken? LoadToken()
    {
        if (_cached is not null)
        {
            return _cached;
        }

        if (!File.Exists(_options.TokenPath))
        {
            return null;
        }

        try
        {
            _cached = JsonSerializer.Deserialize<TraktToken>(
                File.ReadAllText(_options.TokenPath),
                JsonOptions);
            return _cached;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not read the Trakt token at {Path}.", _options.TokenPath);
            return null;
        }
    }

    private void SaveToken(TraktToken token)
    {
        var dir = Path.GetDirectoryName(_options.TokenPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(_options.TokenPath, JsonSerializer.Serialize(token, JsonOptions));
        _cached = token;
    }
}

public record TraktDeviceCode
{
    [JsonPropertyName("device_code")]
    public required string DeviceCode { get; init; }

    [JsonPropertyName("user_code")]
    public required string UserCode { get; init; }

    [JsonPropertyName("verification_url")]
    public required string VerificationUrl { get; init; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; init; }

    [JsonPropertyName("interval")]
    public int Interval { get; init; }
}

public record TraktToken
{
    [JsonPropertyName("access_token")]
    public required string AccessToken { get; init; }

    [JsonPropertyName("refresh_token")]
    public required string RefreshToken { get; init; }

    [JsonPropertyName("created_at")]
    public long CreatedAt { get; init; }

    [JsonPropertyName("expires_in")]
    public long ExpiresIn { get; init; }

    public DateTimeOffset ExpiresAt =>
        DateTimeOffset.FromUnixTimeSeconds(CreatedAt).AddSeconds(ExpiresIn);
}
