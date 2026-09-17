using MagicMovieNight.Core.Abstractions;
using MagicMovieNight.Core.Models;
using MagicMovieNight.Data;
using MagicMovieNight.Integrations.Catalog;
using MagicMovieNight.Integrations.Trakt;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MagicMovieNight.Integrations.Candidates;

/// <summary>
/// Everything in the Plex library that nobody in the household has watched.
/// These are the cheapest possible recommendations — zero friction, already paid for.
/// </summary>
public class LibraryCandidateSource(MovieNightDbContext db) : ICandidateSource
{
    public string Name => "Plex library";

    public async Task<IReadOnlyList<Candidate>> GetCandidatesAsync(
        TasteProfile profile,
        CancellationToken ct = default)
    {
        var watchedIds = await db.WatchEvents
            .Where(e => profile.ViewerIds.Contains(e.ViewerId))
            .Select(e => e.MediaItemId)
            .Distinct()
            .ToListAsync(ct);

        var unwatched = await db.MediaItems
            .Where(m => m.InLibrary && !watchedIds.Contains(m.Id))
            .ToListAsync(ct);

        var genreWeights = profile.TopGenres.ToDictionary(
            g => g.Name,
            g => g.Weight,
            StringComparer.OrdinalIgnoreCase);

        var peopleWeights = profile.TopPeople.ToDictionary(
            p => p.Name,
            p => p.Weight,
            StringComparer.OrdinalIgnoreCase);

        return unwatched
            .Select(item => new Candidate
            {
                Item = item,
                Source = Name,
                Origin = RecommendationOrigin.LibraryMatch,
                PreScore = Affinity(item, genreWeights, peopleWeights)
                    // Being on Plex is worth a real bump: it is watchable in one click.
                    + 0.5,
                Reason = "in your Plex library, unwatched",
            })
            .OrderByDescending(c => c.PreScore)
            .Take(120)
            .ToList();
    }

    /// <summary>
    /// Cheap pre-score so the pool sent to the model is the plausible slice rather
    /// than the whole library. Deliberately crude — the real judgement happens later.
    /// </summary>
    internal static double Affinity(
        MediaItem item,
        Dictionary<string, double> genreWeights,
        Dictionary<string, double> peopleWeights)
    {
        var genreScore = item.Genres.Sum(g => genreWeights.GetValueOrDefault(g));
        var peopleScore = item.People.Sum(p => peopleWeights.GetValueOrDefault(p)) * 1.5;
        var ratingScore = (item.CommunityRating ?? 5.0) / 10.0;

        return genreScore + peopleScore + ratingScore;
    }
}

/// <summary>
/// Trakt's own personalized recommendations. A VIP account tunes these against the
/// full history, so they carry real signal before Claude ever weighs in.
/// </summary>
public class TraktCandidateSource(
    TraktClient trakt,
    ICatalogService catalog,
    ILogger<TraktCandidateSource> logger) : ICandidateSource
{
    public string Name => "Trakt recommendations";

    public async Task<IReadOnlyList<Candidate>> GetCandidatesAsync(
        TasteProfile profile,
        CancellationToken ct = default)
    {
        if (!trakt.IsConfigured)
        {
            return [];
        }

        var candidates = new List<Candidate>();

        foreach (var kind in new[] { MediaKind.Movie, MediaKind.Show })
        {
            var recommendations = await trakt.GetRecommendationsAsync(kind, 40, ct);

            foreach (var raw in recommendations)
            {
                ct.ThrowIfCancellationRequested();

                var item = await catalog.ResolveAsync(raw, ct);
                if (item is null)
                {
                    continue;
                }

                candidates.Add(new Candidate
                {
                    Item = item,
                    Source = Name,
                    Origin = RecommendationOrigin.TraktPersonal,
                    // Trakt already ranked these against the full history, so they
                    // start ahead of a raw library match.
                    PreScore = 1.5 + (item.CommunityRating ?? 5.0) / 10.0,
                    Reason = "Trakt personal recommendation",
                });
            }
        }

        logger.LogDebug("Trakt contributed {Count} candidates.", candidates.Count);

        return candidates;
    }
}

/// <summary>
/// Shows the household is already mid-way through. Often the right answer on a
/// weeknight, and a recommender that ignores them feels broken.
/// </summary>
public class ContinueWatchingCandidateSource(MovieNightDbContext db) : ICandidateSource
{
    public string Name => "Continue watching";

    public async Task<IReadOnlyList<Candidate>> GetCandidatesAsync(
        TasteProfile profile,
        CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-60);

        var recentShows = await db.WatchEvents
            .Include(e => e.MediaItem)
            .Where(e => profile.ViewerIds.Contains(e.ViewerId))
            .Where(e => e.WatchedAt >= cutoff)
            .Where(e => e.MediaItem!.Kind == MediaKind.Show)
            .GroupBy(e => e.MediaItem!)
            .Select(g => new { Item = g.Key, LastWatched = g.Max(e => e.WatchedAt) })
            .ToListAsync(ct);

        return recentShows
            .Select(x => new Candidate
            {
                Item = x.Item,
                Source = Name,
                Origin = RecommendationOrigin.ContinueWatching,
                // Highest pre-score of any source: the household already committed to it.
                PreScore = 2.5,
                Reason = $"in progress, last watched {x.LastWatched:MMM d}",
            })
            .OrderByDescending(c => c.PreScore)
            .Take(15)
            .ToList();
    }
}
