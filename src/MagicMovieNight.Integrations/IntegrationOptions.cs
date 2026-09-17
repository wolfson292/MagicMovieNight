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

    /// <summary>e.g. http://tautulli:8181 — the container name works on the shared Docker network.</summary>
    public string? BaseUrl { get; set; }

    public string? ApiKey { get; set; }

    public bool Enabled => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ApiKey);
}

public class TmdbOptions
{
    public const string Section = "Tmdb";

    /// <summary>v4 read access token (Bearer) from https://www.themoviedb.org/settings/api.</summary>
    public string? ApiToken { get; set; }

    public string BaseUrl { get; set; } = "https://api.themoviedb.org/3";

    public string ImageBaseUrl { get; set; } = "https://image.tmdb.org/t/p/w500";

    /// <summary>ISO 3166-1 region used to look up which services actually carry a title.</summary>
    public string WatchRegion { get; set; } = "US";

    public bool Enabled => !string.IsNullOrWhiteSpace(ApiToken);
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
