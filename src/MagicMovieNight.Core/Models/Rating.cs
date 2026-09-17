namespace MagicMovieNight.Core.Models;

/// <summary>
/// An explicit opinion about a title, as opposed to the implicit signal of having
/// watched it. Explicit ratings are the strongest evidence available — someone
/// finishing a film only says they finished it, but a thumbs-down says what they
/// actually thought.
///
/// Ratings can attach to a whole title or to one episode, because "the show is great
/// but that season finale was a betrayal" is a real and useful distinction.
/// </summary>
public class Rating
{
    public int Id { get; set; }

    public int ViewerId { get; set; }

    public Viewer? Viewer { get; set; }

    public int MediaItemId { get; set; }

    public MediaItem? MediaItem { get; set; }

    /// <summary>Set when the rating is about a specific episode rather than the show.</summary>
    public int? SeasonNumber { get; set; }

    public int? EpisodeNumber { get; set; }

    public RatingValue Value { get; set; }

    /// <summary>
    /// Original 1-10 score where the source had one (Trakt) or 1-5 stars (older
    /// Netflix exports). Kept alongside the normalized thumbs so nothing is lost.
    /// </summary>
    public int? Stars { get; set; }

    public WatchSource Source { get; set; }

    /// <summary>Stable per-source key so re-importing a ratings export is idempotent.</summary>
    public required string SourceKey { get; set; }

    public DateTimeOffset RatedAt { get; set; } = DateTimeOffset.UtcNow;

    public bool IsEpisodeRating => SeasonNumber is not null;
}

/// <summary>
/// Netflix's thumbs scale, which is the one the household already thinks in.
/// Star scales from other sources normalize onto it.
/// </summary>
public enum RatingValue
{
    Down = -1,
    Up = 1,

    /// <summary>Netflix's "Love this" — two thumbs up.</summary>
    Loved = 2,
}

public static class RatingScale
{
    /// <summary>Netflix "Thumbs Value": 1 down, 2 up, 3 two-thumbs-up.</summary>
    public static RatingValue? FromNetflixThumbs(int thumbs) => thumbs switch
    {
        1 => RatingValue.Down,
        2 => RatingValue.Up,
        3 => RatingValue.Loved,
        _ => null,
    };

    /// <summary>
    /// Older Netflix exports and Trakt use star scales. The cut points are deliberately
    /// generous at the top: people rarely award a 10, so an 8 already means "loved it".
    /// </summary>
    public static RatingValue? FromStars(int stars, int outOf) => (stars, outOf) switch
    {
        (< 1, _) => null,
        (_, 5) => stars switch { <= 2 => RatingValue.Down, 3 or 4 => RatingValue.Up, _ => RatingValue.Loved },
        (_, 10) => stars switch { <= 5 => RatingValue.Down, <= 7 => RatingValue.Up, _ => RatingValue.Loved },
        _ => null,
    };

    /// <summary>
    /// How much a rating counts in the taste profile. A thumbs-down is weighted harder
    /// than a thumbs-up because people rate things down far less often — when they
    /// bother, they mean it.
    /// </summary>
    public static double Weight(RatingValue value) => value switch
    {
        RatingValue.Loved => 2.5,
        RatingValue.Up => 1.5,
        RatingValue.Down => -3.0,
        _ => 0,
    };
}
