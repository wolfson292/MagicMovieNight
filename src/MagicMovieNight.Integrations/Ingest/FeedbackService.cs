using MagicMovieNight.Core.Models;
using MagicMovieNight.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MagicMovieNight.Integrations.Ingest;

/// <summary>
/// Applies what happened to a recommendation.
///
/// "We watched it" is recorded as a real watch straight away. Waiting for Tautulli to
/// notice only works for Plex; a film watched on Netflix tonight might never show up
/// otherwise, and the profile would keep recommending it.
///
/// "Already seen it" is treated as a gap in the history rather than an opinion. The
/// household saw it somewhere this system cannot observe, so the title is excluded from
/// future picks — but no watch date is invented for it. Fabricating one would tell the
/// recency weighting that a film from years ago was watched tonight, which is worse than
/// admitting we do not know. If they have a view on it, the thumbs on the same card
/// record that properly.
/// </summary>
public class FeedbackService(
    MovieNightDbContext db,
    ILogger<FeedbackService> logger)
{
    /// <summary>
    /// Records a verdict. Passing the verdict already set clears it, matching the
    /// toggle behaviour of the buttons.
    /// </summary>
    public async Task<Verdict> ApplyAsync(
        int recommendationId,
        Verdict verdict,
        int viewerId,
        CancellationToken ct = default)
    {
        var recommendation = await db.Recommendations
            .FirstOrDefaultAsync(r => r.Id == recommendationId, ct)
            ?? throw new InvalidOperationException($"Recommendation {recommendationId} not found.");

        var resolved = recommendation.Verdict == verdict ? Verdict.None : verdict;

        recommendation.Verdict = resolved;
        recommendation.VerdictAt = resolved == Verdict.None ? null : DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);

        if (resolved == Verdict.Watched)
        {
            await RecordWatchAsync(recommendation.MediaItemId, viewerId, ct);
        }

        return resolved;
    }

    /// <summary>
    /// Media the household should not be offered again: anything already watched, and
    /// anything they told us they had already seen.
    /// </summary>
    public async Task<HashSet<int>> GetExcludedMediaAsync(
        IReadOnlyList<int> viewerIds,
        CancellationToken ct = default)
    {
        var watched = await db.WatchEvents
            .Where(e => viewerIds.Contains(e.ViewerId))
            .Select(e => e.MediaItemId)
            .Distinct()
            .ToListAsync(ct);

        var alreadySeen = await db.Recommendations
            .Where(r => r.Verdict == Verdict.AlreadySeen)
            .Select(r => r.MediaItemId)
            .Distinct()
            .ToListAsync(ct);

        return [.. watched, .. alreadySeen];
    }

    /// <summary>
    /// Writes a watch the household reported directly. The source key is derived from
    /// the viewer, title and day, so pressing the button twice cannot double-count and
    /// neither can a later Tautulli sync of the same evening create a conflict — that
    /// arrives under a different source.
    /// </summary>
    private async Task RecordWatchAsync(int mediaItemId, int viewerId, CancellationToken ct)
    {
        var today = DateTimeOffset.UtcNow;
        var sourceKey = $"manual:{viewerId}:{mediaItemId}:{today:yyyy-MM-dd}";

        var exists = await db.WatchEvents.AnyAsync(
            e => e.Source == WatchSource.Manual && e.SourceKey == sourceKey, ct);

        if (exists)
        {
            return;
        }

        db.WatchEvents.Add(new WatchEvent
        {
            ViewerId = viewerId,
            MediaItemId = mediaItemId,
            Source = WatchSource.Manual,
            SourceKey = sourceKey,
            WatchedAt = today,
            // They said they watched it, so treat it as finished. Attributed rather than
            // Full because there is no completion percentage behind the claim.
            PercentComplete = 100,
            Fidelity = SourceFidelity.Attributed,
        });

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Recorded a manual watch of media {MediaId} for viewer {ViewerId}.",
            mediaItemId, viewerId);
    }
}
