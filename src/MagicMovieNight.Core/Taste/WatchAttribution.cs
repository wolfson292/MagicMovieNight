using MagicMovieNight.Core.Models;

namespace MagicMovieNight.Core.Taste;

/// <summary>
/// Decides which watch events a group of people actually saw.
///
/// Sources report the account, not the viewer, so every event on a shared profile is
/// attributed to everyone who uses it. A <see cref="RatingValue.NotWatched"/> rating
/// corrects that for one person and one title (or one episode).
///
/// An event is dropped only when <em>every</em> member of the group who uses that
/// profile has said they did not watch it. That keeps the household honest: if Scott
/// marks a show Angela watched on the family login, it leaves Scott's own taste, but
/// "what should we watch" still knows Angela saw it.
/// </summary>
public static class WatchAttribution
{
    /// <param name="events">Watch events on the group's profiles.</param>
    /// <param name="personIds">The people the question is about.</param>
    /// <param name="membersByProfile">Profile id → the people who use that profile.</param>
    /// <param name="ratings">Ratings to consult; only <see cref="RatingValue.NotWatched"/> ones matter.</param>
    public static List<WatchEvent> SeenBy(
        IEnumerable<WatchEvent> events,
        IReadOnlyCollection<int> personIds,
        IReadOnlyDictionary<int, IReadOnlyCollection<int>> membersByProfile,
        IEnumerable<Rating> ratings)
    {
        var notWatched = ratings
            .Where(r => r.Value == RatingValue.NotWatched && personIds.Contains(r.PersonId))
            .Select(r => (r.PersonId, r.MediaItemId, r.SeasonNumber, r.EpisodeNumber))
            .ToHashSet();

        if (notWatched.Count == 0)
        {
            return events.ToList();
        }

        return events
            .Where(e =>
            {
                var viewers = membersByProfile.TryGetValue(e.ProfileId, out var members)
                    ? members.Where(personIds.Contains).ToList()
                    : [];

                // An event nobody in the group is attached to cannot be disowned by them.
                if (viewers.Count == 0)
                {
                    return true;
                }

                return !viewers.All(person => DidNotWatch(notWatched, person, e));
            })
            .ToList();
    }

    private static bool DidNotWatch(
        HashSet<(int, int, int?, int?)> notWatched,
        int personId,
        WatchEvent e) =>
        notWatched.Contains((personId, e.MediaItemId, null, null))
        || (e.SeasonNumber is not null
            && notWatched.Contains((personId, e.MediaItemId, e.SeasonNumber, e.EpisodeNumber)));
}
