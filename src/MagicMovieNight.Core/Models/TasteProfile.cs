namespace MagicMovieNight.Core.Models;

/// <summary>
/// A compact, human-readable summary of what someone (or the household) actually
/// watches. This is what gets sent to Claude — never the raw history, which would
/// be tens of thousands of rows and mostly noise.
/// </summary>
public record TasteProfile
{
    public required string Subject { get; init; }

    /// <summary>People this profile covers. More than one means a household blend.</summary>
    public required IReadOnlyList<int> PersonIds { get; init; }

    /// <summary>
    /// Every profile those people watch on, resolved once here so consumers never have
    /// to redo the person-to-profile lookup. This is what watch history is queried by.
    /// </summary>
    public required IReadOnlyList<int> ProfileIds { get; init; }

    public int TotalWatches { get; init; }

    public DateTimeOffset? FirstWatch { get; init; }

    public DateTimeOffset? LastWatch { get; init; }

    /// <summary>Genres ranked by weighted watch count, most-watched first.</summary>
    public required IReadOnlyList<WeightedTag> TopGenres { get; init; }

    /// <summary>Directors, showrunners, and actors that recur across finished titles.</summary>
    public required IReadOnlyList<WeightedTag> TopPeople { get; init; }

    /// <summary>Recent finishes — the strongest short-term signal.</summary>
    public required IReadOnlyList<string> RecentlyLoved { get; init; }

    /// <summary>Started and abandoned below the meaningful threshold. Negative signal.</summary>
    public required IReadOnlyList<string> Abandoned { get; init; }

    /// <summary>Shows with unwatched episodes remaining.</summary>
    public required IReadOnlyList<string> InProgress { get; init; }

    /// <summary>Median runtime of finished movies — the household's tolerance for a long night.</summary>
    public int? TypicalMovieRuntime { get; init; }

    /// <summary>Share of watches that are shows rather than movies, 0-1.</summary>
    public double ShowBias { get; init; }

    /// <summary>Titles the household explicitly thumbed down. Hard exclusions for the model.</summary>
    public required IReadOnlyList<string> Disliked { get; init; }

    /// <summary>Titles they explicitly rated up or loved — the strongest positive signal there is.</summary>
    public required IReadOnlyList<string> Loved { get; init; }

    /// <summary>
    /// Ratings on individual episodes, which say something a title-level rating cannot:
    /// that a show is worth watching but a particular stretch of it is not.
    /// </summary>
    public required IReadOnlyList<string> EpisodeRatings { get; init; }

    public int TotalRatings { get; init; }
}

public record WeightedTag(string Name, double Weight, int Count);
