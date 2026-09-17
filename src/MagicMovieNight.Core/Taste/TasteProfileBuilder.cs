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

    public static TasteProfile Build(
        string subject,
        IReadOnlyList<int> viewerIds,
        IReadOnlyList<WatchEvent> events,
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
            Disliked = disliked,
        };
    }

    /// <summary>
    /// Combined recency and fidelity weight for one event. Ranges from ~0.05 for an
    /// old CSV row to 1.0 for a Tautulli watch from today.
    /// </summary>
    public static double Weight(WatchEvent e, DateTimeOffset asOf)
    {
        var ageDays = Math.Max(0, (asOf - e.WatchedAt).TotalDays);
        var recency = Math.Pow(0.5, ageDays / RecencyHalfLifeDays);

        var fidelity = e.Fidelity switch
        {
            SourceFidelity.Full => 1.0,
            SourceFidelity.Attributed => 0.8,
            _ => 0.5,
        };

        return recency * fidelity;
    }

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
