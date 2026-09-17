namespace MagicMovieNight.Core.Models;

/// <summary>Where a watch event came from. Determines fidelity and trust.</summary>
public enum WatchSource
{
    Unknown = 0,

    /// <summary>Tautulli's Plex history — highest fidelity: viewer, device, completion %.</summary>
    Tautulli = 1,

    /// <summary>Trakt history — the unified spine, includes scrobbler-sourced streamer watches.</summary>
    Trakt = 2,

    /// <summary>Netflix "Viewing activity" CSV export. Date-only, no completion %.</summary>
    NetflixCsv = 3,

    /// <summary>Amazon "Request your data" Prime Video watch history export.</summary>
    PrimeCsv = 4,

    /// <summary>Hulu privacy-portal data export.</summary>
    HuluCsv = 5,

    /// <summary>Manually entered by a household member in the UI.</summary>
    Manual = 6,
}

public enum MediaKind
{
    Movie = 0,
    Show = 1,
    Episode = 2,
}

/// <summary>How a recommendation was produced, so the UI can explain itself.</summary>
public enum RecommendationOrigin
{
    /// <summary>Claude picked and justified it from the candidate pool.</summary>
    Claude = 0,

    /// <summary>Trakt's own personalized recommendations (VIP endpoint).</summary>
    TraktPersonal = 1,

    /// <summary>Already in the Plex library and matches the taste profile.</summary>
    LibraryMatch = 2,

    /// <summary>Next unwatched episode of a show in progress.</summary>
    ContinueWatching = 3,
}

/// <summary>Household feedback on a recommendation — the training signal.</summary>
public enum Verdict
{
    None = 0,
    ThumbsUp = 1,
    ThumbsDown = 2,
    Watched = 3,
    NotTonight = 4,
    AlreadySeen = 5,
}
