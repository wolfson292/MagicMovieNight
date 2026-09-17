namespace MagicMovieNight.Core.Models;

/// <summary>
/// A movie or show, deduplicated across every source. External ids are what make
/// dedup possible — a Netflix CSV row and a Trakt history entry only collapse into
/// one item once both resolve to the same TMDB/IMDb id.
/// </summary>
public class MediaItem
{
    public int Id { get; set; }

    public required string Title { get; set; }

    public int? Year { get; set; }

    public MediaKind Kind { get; set; }

    public int? TmdbId { get; set; }

    public int? TraktId { get; set; }

    public string? ImdbId { get; set; }

    public int? TvdbId { get; set; }

    /// <summary>Plex rating key, when the item exists in the local library.</summary>
    public string? PlexRatingKey { get; set; }

    public string? Overview { get; set; }

    public int? RuntimeMinutes { get; set; }

    public string? ContentRating { get; set; }

    public double? CommunityRating { get; set; }

    public string? PosterUrl { get; set; }

    public List<string> Genres { get; set; } = [];

    /// <summary>Directors, showrunners, and top-billed cast — the affinity signal Claude reasons over.</summary>
    public List<string> People { get; set; } = [];

    /// <summary>True when the item is in the Plex library, i.e. watchable with zero friction.</summary>
    public bool InLibrary { get; set; }

    /// <summary>Streaming services currently carrying this item, from TMDB watch providers.</summary>
    public List<string> AvailableOn { get; set; } = [];

    public DateTimeOffset LastEnrichedAt { get; set; }

    public List<WatchEvent> WatchEvents { get; set; } = [];

    public string DisplayTitle => Year is null ? Title : $"{Title} ({Year})";
}
