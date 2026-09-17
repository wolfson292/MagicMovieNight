using MagicMovieNight.Core.Models;

namespace MagicMovieNight.Core.Taste;

/// <summary>
/// Collapses raw watch history into the compact profile the recommender reasons over.
///
/// Two ideas do most of the work here. First, recency weighting: what the household
/// watched last month says far more about tonight than what they watched in 2019, so
/// every event decays on a half-life. Second, fidelity weighting: a Tautulli event
/// that says "watched 96% on the living room Apple TV" is worth more than a Netflix
/// CSV row that says only "you saw something called Bodyguard that day".
///
/// Explicit ratings sit on top of both. Having watched something says only that it was
/// watched; a thumbs-down says what they actually thought, so ratings push genre and
/// people affinity in either direction rather than merely adding to it.
/// </summary>
public static class TasteProfileBuilder
{
    /// <summary>Weight halves every this many days. Six months keeps a year or two of history relevant without letting it dominate.</summary>
    public const double RecencyHalfLifeDays = 180.0;

    /// <summary>Below this completion percentage a start counts as an abandonment, not a watch.</summary>
    public const int AbandonedBelowPercent = 25;

    private const int TopGenreCount = 12;
    private const int TopPeopleCount = 20;
    private const int RecentlyLovedCount = 25;
    private const int AbandonedCount = 10;
    private const int RatedTitleCount = 40;
    private const int RatedEpisodeCount = 20;

    public static TasteProfile Build(
        string subject,
        IReadOnlyList<int> viewerIds,
        IReadOnlyList<WatchEvent> events,
        IReadOnlyList<Rating> ratings,
        IReadOnlyList<string> disliked,
        IReadOnlyList<string> inProgress,
        DateTimeOffset? now = null)
    {
        var asOf = now ?? DateTimeOffset.UtcNow;

        // Events without a resolved catalog entry carry no genre or people data, so
        // they can only contribute to counts — drop them from the affinity maths.
        var resolved = events.Where(e => e.MediaItem is not null).ToList();

        var meaningful = resolved.Where(e => e.IsMeaningful).ToList();

        var genres = new Dictionary<string, (double Weight, int Count)>(StringComparer.OrdinalIgnoreCase);
        var people = new Dictionary<string, (double Weight, int Count)>(StringComparer.OrdinalIgnoreCase);

        foreach (var e in meaningful)
        {
            var w = Weight(e, asOf);

            foreach (var g in e.MediaItem!.Genres)
            {
                var cur = genres.GetValueOrDefault(g);
                genres[g] = (cur.Weight + w, cur.Count + 1);
            }

            foreach (var p in e.MediaItem.People)
            {
                var cur = people.GetValueOrDefault(p);
                people[p] = (cur.Weight + w, cur.Count + 1);
            }
        }

        // Explicit ratings are applied after the watch signal, and can be negative —
        // a thumbs-down on a horror film should pull Horror down, not just fail to
        // push it up. Episode ratings are deliberately weighted lower than title
        // ratings: disliking one episode is not disliking the show.
        foreach (var rating in ratings.Where(r => r.MediaItem is not null))
        {
            var w = RatingScale.Weight(rating.Value)
                * Recency(rating.RatedAt, asOf)
                * (rating.IsEpisodeRating ? 0.3 : 1.0);

            foreach (var g in rating.MediaItem!.Genres)
            {
                var cur = genres.GetValueOrDefault(g);
                genres[g] = (cur.Weight + w, cur.Count);
            }

            foreach (var p in rating.MediaItem.People)
            {
                var cur = people.GetValueOrDefault(p);
                people[p] = (cur.Weight + w, cur.Count);
            }
        }

        var recentlyLoved = meaningful
            .OrderByDescending(e => e.WatchedAt)
            .Select(e => e.MediaItem!.DisplayTitle)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(RecentlyLovedCount)
            .ToList();

        var abandoned = resolved
            .Where(e => e.PercentComplete is > 0 and < AbandonedBelowPercent)
            // Something re-watched later was not really abandoned.
            .Where(e => !meaningful.Any(m => m.MediaItemId == e.MediaItemId))
            .OrderByDescending(e => e.WatchedAt)
            .Select(e => e.MediaItem!.DisplayTitle)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(AbandonedCount)
            .ToList();

        var movieRuntimes = meaningful
            .Where(e => e.MediaItem!.Kind == MediaKind.Movie && e.MediaItem.RuntimeMinutes > 0)
            .Select(e => e.MediaItem!.RuntimeMinutes!.Value)
            .OrderBy(r => r)
            .ToList();

        var showWatches = meaningful.Count(e => e.MediaItem!.Kind is MediaKind.Show or MediaKind.Episode);

        var titleRatings = ratings.Where(r => r.MediaItem is not null && !r.IsEpisodeRating).ToList();

        var loved = titleRatings
            .Where(r => r.Value is RatingValue.Up or RatingValue.Loved)
            .OrderByDescending(r => r.Value)
            .ThenByDescending(r => r.RatedAt)
            .Select(r => r.Value == RatingValue.Loved
                ? $"{r.MediaItem!.DisplayTitle} (loved it)"
                : r.MediaItem!.DisplayTitle)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(RatedTitleCount)
            .ToList();

        // An explicit thumbs-down outranks anything inferred, so these are merged with
        // the caller's list rather than replacing it.
        var allDisliked = titleRatings
            .Where(r => r.Value == RatingValue.Down)
            .OrderByDescending(r => r.RatedAt)
            .Select(r => r.MediaItem!.DisplayTitle)
            .Concat(disliked)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(RatedTitleCount)
            .ToList();

        var episodeRatings = ratings
            .Where(r => r.MediaItem is not null && r.IsEpisodeRating)
            .OrderByDescending(r => r.RatedAt)
            .Select(r => $"{r.MediaItem!.Title} S{r.SeasonNumber}"
                + (r.EpisodeNumber is null ? "" : $"E{r.EpisodeNumber}")
                + $" — {Describe(r.Value)}")
            .Take(RatedEpisodeCount)
            .ToList();

        return new TasteProfile
        {
            Subject = subject,
            ViewerIds = viewerIds,
            TotalWatches = events.Count,
            FirstWatch = events.Count == 0 ? null : events.Min(e => e.WatchedAt),
            LastWatch = events.Count == 0 ? null : events.Max(e => e.WatchedAt),
            TopGenres = Rank(genres, TopGenreCount),
            TopPeople = Rank(people, TopPeopleCount),
            RecentlyLoved = recentlyLoved,
            Abandoned = abandoned,
            InProgress = inProgress,
            TypicalMovieRuntime = Median(movieRuntimes),
            ShowBias = meaningful.Count == 0 ? 0 : (double)showWatches / meaningful.Count,
            Disliked = allDisliked,
            Loved = loved,
            EpisodeRatings = episodeRatings,
            TotalRatings = ratings.Count,
        };
    }

    /// <summary>
    /// Combined recency and fidelity weight for one event. Ranges from ~0.05 for an
    /// old CSV row to 1.0 for a Tautulli watch from today.
    /// </summary>
    public static double Weight(WatchEvent e, DateTimeOffset asOf)
    {
        var recency = Recency(e.WatchedAt, asOf);

        var fidelity = e.Fidelity switch
        {
            SourceFidelity.Full => 1.0,
            SourceFidelity.Attributed => 0.8,
            _ => 0.5,
        };

        return recency * fidelity;
    }

    /// <summary>Exponential decay on the shared half-life. 1.0 today, 0.5 six months ago.</summary>
    public static double Recency(DateTimeOffset at, DateTimeOffset asOf) =>
        Math.Pow(0.5, Math.Max(0, (asOf - at).TotalDays) / RecencyHalfLifeDays);

    private static string Describe(RatingValue value) => value switch
    {
        RatingValue.Loved => "loved it",
        RatingValue.Up => "thumbs up",
        _ => "thumbs down",
    };

    private static List<WeightedTag> Rank(
        Dictionary<string, (double Weight, int Count)> source,
        int take) =>
        source
            .OrderByDescending(kv => kv.Value.Weight)
            .Take(take)
            .Select(kv => new WeightedTag(kv.Key, Math.Round(kv.Value.Weight, 3), kv.Value.Count))
            .ToList();

    private static int? Median(List<int> sorted)
    {
        if (sorted.Count == 0)
        {
            return null;
        }

        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}
