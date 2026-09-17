using MagicMovieNight.Core.Abstractions;
using MagicMovieNight.Core.Models;
using MagicMovieNight.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MagicMovieNight.Integrations.Ingest;

/// <summary>
/// Records what the household thinks of things — both from the UI and from an
/// imported Netflix ratings export.
///
/// Ratings upsert rather than accumulate: a person holds one opinion of a title at a
/// time, and changing your mind should replace the old rating, not sit alongside it.
/// </summary>
public class RatingService(
    MovieNightDbContext db,
    ICatalogService catalog,
    ILogger<RatingService> logger)
{
    /// <summary>
    /// Sets a rating from the UI. Rating something the same way twice clears it, which
    /// is how the thumb buttons toggle off.
    /// </summary>
    public async Task<RatingValue?> SetAsync(
        int viewerId,
        int mediaItemId,
        RatingValue value,
        int? seasonNumber = null,
        int? episodeNumber = null,
        CancellationToken ct = default)
    {
        var existing = await db.Ratings.FirstOrDefaultAsync(
            r => r.ViewerId == viewerId
                && r.MediaItemId == mediaItemId
                && r.SeasonNumber == seasonNumber
                && r.EpisodeNumber == episodeNumber,
            ct);

        if (existing is not null)
        {
            if (existing.Value == value)
            {
                db.Ratings.Remove(existing);
                await db.SaveChangesAsync(ct);
                return null;
            }

            existing.Value = value;
            existing.Stars = null;
            existing.Source = WatchSource.Manual;
            existing.RatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return value;
        }

        db.Ratings.Add(new Rating
        {
            ViewerId = viewerId,
            MediaItemId = mediaItemId,
            SeasonNumber = seasonNumber,
            EpisodeNumber = episodeNumber,
            Value = value,
            Source = WatchSource.Manual,
            SourceKey = $"ui:{viewerId}:{mediaItemId}:{seasonNumber}:{episodeNumber}",
            RatedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync(ct);
        return value;
    }

    /// <summary>
    /// Imports a ratings export. An imported rating never overwrites one made in the
    /// app — if someone has taken the trouble to rate something here, that is the more
    /// current opinion.
    /// </summary>
    public async Task<IngestResult> ImportAsync(
        IRatingImporter importer,
        Stream file,
        int viewerId,
        CancellationToken ct = default)
    {
        var result = new IngestResult();

        await foreach (var raw in importer.ParseAsync(file, ct))
        {
            var item = await catalog.ResolveAsync(raw.ToResolvable(), ct);
            if (item is null)
            {
                result = result.Add(IngestOutcome.Unresolved);
                continue;
            }

            var existing = await db.Ratings.FirstOrDefaultAsync(
                r => r.ViewerId == viewerId
                    && r.MediaItemId == item.Id
                    && r.SeasonNumber == raw.SeasonNumber
                    && r.EpisodeNumber == raw.EpisodeNumber,
                ct);

            if (existing is not null)
            {
                result = result.Add(IngestOutcome.Skipped);
                continue;
            }

            db.Ratings.Add(new Rating
            {
                ViewerId = viewerId,
                MediaItemId = item.Id,
                SeasonNumber = raw.SeasonNumber,
                EpisodeNumber = raw.EpisodeNumber,
                Value = raw.Value,
                Stars = raw.Stars,
                Source = importer.Source,
                SourceKey = raw.SourceKey,
                RatedAt = raw.RatedAt,
            });

            await db.SaveChangesAsync(ct);
            result = result.Add(IngestOutcome.Imported);
        }

        logger.LogInformation(
            "Imported {File}: {Imported} ratings, {Skipped} already rated, {Unresolved} unresolved.",
            importer.DisplayName, result.Imported, result.Skipped, result.Unresolved);

        return result;
    }
}
