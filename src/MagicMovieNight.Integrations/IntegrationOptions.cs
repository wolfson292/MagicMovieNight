namespace MagicMovieNight.Integrations;

public class TraktOptions
{
    public const string Section = "Trakt";

    /// <summary>Client id from https://trakt.tv/oauth/applications.</summary>
    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    /// <summary>Where the device-flow tokens are persisted between container restarts.</summary>
    public string TokenPath { get; set; } = "/config/trakt-token.json";

    public string BaseUrl { get; set; } = "https://api.trakt.tv";

    /// <summary>Trakt username or slug whose history to pull. Defaults to the authenticated user.</summary>
    public string User { get; set; } = "me";

    public bool Enabled => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}

public class TautulliOptions
{
    public const string Section = "Tautulli";

    /// <summary>e.g. http://tautulli:8181 — a container name resolves on a shared Docker network.</summary>
    public string? BaseUrl { get; set; }

    public string? ApiKey { get; set; }

    public bool Enabled => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ApiKey);
}

public class TmdbOptions
{
    public const string Section = "Tmdb";

    /// <summary>v4 read access token (Bearer) from https://www.themoviedb.org/settings/api.</summary>
    public string? ApiToken { get; set; }

    /// <summary>
    /// Must keep the trailing slash. HttpClient resolves relative URIs per RFC 3986,
    /// where a path segment without a trailing slash is treated as a file and replaced —
    /// so "https://api.themoviedb.org/3" + "search/movie" silently becomes
    /// ".../search/movie" with the "/3" dropped, and every call 404s.
    /// </summary>
    public string BaseUrl { get; set; } = "https://api.themoviedb.org/3/";

    public string ImageBaseUrl { get; set; } = "https://image.tmdb.org/t/p/w500";

    /// <summary>ISO 3166-1 region used to look up which services actually carry a title.</summary>
    public string WatchRegion { get; set; } = "US";

    public bool Enabled => !string.IsNullOrWhiteSpace(ApiToken);
}

/// <summary>
/// Shared shape for Sonarr and Radarr, which are the same application with different
/// nouns — their add endpoints differ only in the id they key on and the field names
/// around monitoring.
/// </summary>
public abstract class ArrOptions
{
    /// <summary>e.g. http://sonarr:8989 — a container name resolves on a shared Docker network.</summary>
    public string? BaseUrl { get; set; }

    public string? ApiKey { get; set; }

    /// <summary>
    /// Quality profile to add under. Left unset, the first profile the server reports is
    /// used, which is right often enough to not be worth configuring up front.
    /// </summary>
    public int? QualityProfileId { get; set; }

    /// <summary>Root folder to add into. Unset means the server's first root folder.</summary>
    public string? RootFolderPath { get; set; }

    /// <summary>Kick off a search as soon as something is added, rather than only monitoring it.</summary>
    public bool SearchOnAdd { get; set; } = true;

    public bool Enabled => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ApiKey);
}

public class SonarrOptions : ArrOptions
{
    public const string Section = "Sonarr";

    /// <summary>Sonarr defaults to monitoring every season of a newly added series.</summary>
    public string MonitorMode { get; set; } = "all";
}

public class RadarrOptions : ArrOptions
{
    public const string Section = "Radarr";

    /// <summary>Radarr's equivalent of Sonarr's monitor mode.</summary>
    public string MinimumAvailability { get; set; } = "released";
}

public class ClaudeOptions
{
    public const string Section = "Claude";

    /// <summary>Read from ANTHROPIC_API_KEY when left unset.</summary>
    public string? ApiKey { get; set; }

    public string Model { get; set; } = "claude-opus-5";

    /// <summary>Candidates sent to the model. Beyond a few hundred the pitch quality stops improving.</summary>
    public int MaxCandidates { get; set; } = 150;

    /// <summary>Effort level: low, medium, high, xhigh, or max.</summary>
    public string Effort { get; set; } = "high";
}

public class HouseholdOptions
{
    public const string Section = "Household";

    /// <summary>How often the pullers run.</summary>
    public TimeSpan SyncInterval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>How far back the first sync reaches when there is no watermark yet.</summary>
    public int InitialBackfillDays { get; set; } = 3650;

    /// <summary>Recommendations produced per run.</summary>
    public int DefaultRecommendationCount { get; set; } = 5;
}
