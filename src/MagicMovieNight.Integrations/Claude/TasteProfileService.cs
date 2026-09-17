using MagicMovieNight.Core.Models;
using MagicMovieNight.Core.Taste;
using MagicMovieNight.Data;
using Microsoft.EntityFrameworkCore;

namespace MagicMovieNight.Integrations.Claude;

/// <summary>
/// Turns "who is watching" into a taste profile.
///
/// The translation from people to history is the interesting part. A source only ever
/// reports which account was used, so watch events hang off profiles; but a profile can
/// represent several people and a person can watch on several profiles. Asking for
/// Scott's taste therefore means gathering every profile Scott is a member of — his own
/// account and every shared one — which is exactly how watching together on one login
/// still informs both people's profiles.
///
/// Passing no people means the household: everyone flagged as part of it, pooled.
/// Pooling is deliberately not the same as averaging two separate profiles — it lets a
/// title they both watched outweigh two titles only one of them watched, which is what
/// "what should *we* watch" actually means.
/// </summary>
public class TasteProfileService(MovieNightDbContext db)
{
    /// <summary>A series watched this recently is assumed to still be in progress.</summary>
    private static readonly TimeSpan InProgressWindow = TimeSpan.FromDays(60);

    public async Task<TasteProfile> BuildAsync(
        IReadOnlyList<int> personIds,
        CancellationToken ct = default)
    {
        var resolvedPeople = personIds.Count > 0
            ? personIds.ToList()
            : await db.People
                .Where(p => p.InHousehold)
                .Select(p => p.Id)
                .ToListAsync(ct);

        var profileIds = await db.ProfileMemberships
            .Where(m => resolvedPeople.Contains(m.PersonId))
            .Select(m => m.ProfileId)
            .Distinct()
            .ToListAsync(ct);

        var subject = await DescribeSubjectAsync(personIds, resolvedPeople, profileIds, ct);

        var events = await db.WatchEvents
            .Include(e => e.MediaItem)
            .Where(e => profileIds.Contains(e.ProfileId))
            .OrderByDescending(e => e.WatchedAt)
            .ToListAsync(ct);

        // Ratings are per person and need no profile translation — that is the point of
        // keeping them on the human rather than the account.
        var ratings = await db.Ratings
            .Include(r => r.MediaItem)
            .Where(r => resolvedPeople.Contains(r.PersonId))
            .OrderByDescending(r => r.RatedAt)
            .ToListAsync(ct);

        var inProgress = FindInProgress(events);

        return TasteProfileBuilder.Build(
            subject, resolvedPeople, profileIds, events, ratings, inProgress);
    }

    private async Task<string> DescribeSubjectAsync(
        IReadOnlyList<int> requested,
        IReadOnlyList<int> resolved,
        IReadOnlyList<int> profileIds,
        CancellationToken ct)
    {
        var names = await db.People
            .Where(p => resolved.Contains(p.Id))
            .OrderBy(p => p.Name)
            .Select(p => p.Name)
            .ToListAsync(ct);

        if (names.Count == 0)
        {
            return "nobody in particular — no people are set up yet";
        }

        // Saying so matters: a profile with no history looks identical to a person with
        // no taste, and the model should know which it is looking at.
        if (profileIds.Count == 0)
        {
            return $"{string.Join(" and ", names)} — no profiles mapped to them yet";
        }

        if (requested.Count == 0)
        {
            return $"the household — {string.Join(" and ", names)}";
        }

        return names.Count == 1
            ? names[0]
            : $"{string.Join(" and ", names)}, watching together";
    }

    /// <summary>
    /// We do not know a show's episode count, so "in progress" means episodes watched
    /// recently. Imperfect, but it surfaces the thing the household is most likely to
    /// want next — the show they are already mid-way through.
    /// </summary>
    private static List<string> FindInProgress(List<WatchEvent> events)
    {
        var cutoff = DateTimeOffset.UtcNow - InProgressWindow;

        return events
            .Where(e => e.MediaItem is not null)
            .Where(e => e.MediaItem!.Kind is MediaKind.Show or MediaKind.Episode)
            .Where(e => e.WatchedAt >= cutoff)
            .GroupBy(e => e.MediaItem!.DisplayTitle)
            .OrderByDescending(g => g.Max(e => e.WatchedAt))
            .Select(g => g.Key)
            .Take(10)
            .ToList();
    }
}
