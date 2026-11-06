using MagicMovieNight.Core.Models;
using MagicMovieNight.Core.Taste;

namespace MagicMovieNight.Tests;

/// <summary>
/// "Didn't watch": the correction for a shared profile that lists everything anyone
/// played on it. Scott is person 1, Angela person 2; profile 10 is the family login they
/// share and profile 20 is Scott's own.
/// </summary>
public class WatchAttributionTests
{
    private const int Scott = 1;
    private const int Angela = 2;
    private const int Family = 10;
    private const int ScottsOwn = 20;

    private static readonly DateTimeOffset Now = new(2026, 11, 6, 20, 0, 0, TimeSpan.Zero);

    private static readonly IReadOnlyDictionary<int, IReadOnlyCollection<int>> Members =
        new Dictionary<int, IReadOnlyCollection<int>>
        {
            [Family] = [Scott, Angela],
            [ScottsOwn] = [Scott],
        };

    [Fact]
    public void ATitleMarkedDidntWatchLeavesThatPersonsHistory()
    {
        var show = Item(1, "Haunted Hotel", ["Animation"]);
        var events = new[] { Watch(show, Family), Watch(show, Family, season: 2, episode: 1) };

        var seen = WatchAttribution.SeenBy(
            events, [Scott], Members, [Rate(Scott, show, RatingValue.NotWatched)]);

        Assert.Empty(seen);
    }

    [Fact]
    public void ItStillCountsForTheHouseholdBecauseSomeoneElseWatchedIt()
    {
        var show = Item(1, "Haunted Hotel", ["Animation"]);
        var events = new[] { Watch(show, Family) };

        var seen = WatchAttribution.SeenBy(
            events, [Scott, Angela], Members, [Rate(Scott, show, RatingValue.NotWatched)]);

        Assert.Single(seen);
    }

    [Fact]
    public void ItDropsForTheHouseholdOnlyWhenEveryoneOnTheProfileDisownsIt()
    {
        var show = Item(1, "Somebody's Guest", ["Drama"]);
        var events = new[] { Watch(show, Family) };

        var seen = WatchAttribution.SeenBy(
            events, [Scott, Angela], Members,
            [Rate(Scott, show, RatingValue.NotWatched), Rate(Angela, show, RatingValue.NotWatched)]);

        Assert.Empty(seen);
    }

    [Fact]
    public void AnEpisodeMarkedDidntWatchDropsOnlyThatEpisode()
    {
        var show = Item(1, "From", ["Horror"], MediaKind.Show);
        var e8 = Watch(show, Family, season: 3, episode: 8);
        var e9 = Watch(show, Family, season: 3, episode: 9);

        var seen = WatchAttribution.SeenBy(
            [e8, e9], [Scott], Members,
            [Rate(Scott, show, RatingValue.NotWatched, season: 3, episode: 9)]);

        Assert.Equal([e8], seen);
    }

    [Fact]
    public void OtherRatingsDoNotRemoveAnything()
    {
        var film = Item(1, "Liked Film", ["Comedy"]);
        var events = new[] { Watch(film, Family) };

        var seen = WatchAttribution.SeenBy(
            events, [Scott], Members, [Rate(Scott, film, RatingValue.Down)]);

        Assert.Single(seen);
    }

    [Fact]
    public void OnePersonsDidntWatchDoesNotTouchAnothersHistory()
    {
        var show = Item(1, "Haunted Hotel", ["Animation"]);
        var events = new[] { Watch(show, Family) };

        var seen = WatchAttribution.SeenBy(
            events, [Angela], Members, [Rate(Scott, show, RatingValue.NotWatched)]);

        Assert.Single(seen);
    }

    [Fact]
    public void DidntWatchIsNotAnOpinionInTheTasteProfile()
    {
        var horror = Item(1, "Scary Thing", ["Horror"]);
        var comedy = Item(2, "Funny Thing", ["Comedy"]);

        var profile = TasteProfileBuilder.Build(
            "test", [Scott], [ScottsOwn],
            [Watch(comedy, ScottsOwn)],
            [Rate(Scott, horror, RatingValue.NotWatched)],
            [], Now);

        Assert.DoesNotContain(profile.TopGenres, g => g.Name == "Horror");
        Assert.Empty(profile.Disliked);
        Assert.Empty(profile.Loved);
        Assert.Equal(0, profile.TotalRatings);
    }

    private static MediaItem Item(int id, string title, string[] genres, MediaKind kind = MediaKind.Movie) =>
        new() { Id = id, Title = title, Kind = kind, Genres = [.. genres] };

    private static WatchEvent Watch(MediaItem item, int profileId, int? season = null, int? episode = null) => new()
    {
        ProfileId = profileId,
        MediaItem = item,
        MediaItemId = item.Id,
        WatchedAt = Now.AddDays(-3),
        PercentComplete = 100,
        SeasonNumber = season,
        EpisodeNumber = episode,
        Fidelity = SourceFidelity.Full,
        SourceKey = Guid.NewGuid().ToString(),
    };

    private static Rating Rate(
        int personId,
        MediaItem item,
        RatingValue value,
        int? season = null,
        int? episode = null) => new()
    {
        PersonId = personId,
        MediaItem = item,
        MediaItemId = item.Id,
        Value = value,
        RatedAt = Now.AddDays(-1),
        SeasonNumber = season,
        EpisodeNumber = episode,
        Source = WatchSource.Manual,
        SourceKey = Guid.NewGuid().ToString(),
    };
}
