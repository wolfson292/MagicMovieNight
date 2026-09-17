using System.Text;
using MagicMovieNight.Core.Models;
using MagicMovieNight.Core.Taste;
using MagicMovieNight.Integrations.Importers;
using Microsoft.Extensions.Logging.Abstractions;

namespace MagicMovieNight.Tests;

public class RatingScaleTests
{
    [Theory]
    [InlineData(1, RatingValue.Down)]
    [InlineData(2, RatingValue.Up)]
    [InlineData(3, RatingValue.Loved)]
    public void NetflixThumbsMapOntoTheHouseScale(int thumbs, RatingValue expected) =>
        Assert.Equal(expected, RatingScale.FromNetflixThumbs(thumbs));

    [Fact]
    public void UnknownThumbsValuesAreRejectedRatherThanGuessed() =>
        Assert.Null(RatingScale.FromNetflixThumbs(9));

    [Theory]
    [InlineData(1, 5, RatingValue.Down)]
    [InlineData(2, 5, RatingValue.Down)]
    [InlineData(4, 5, RatingValue.Up)]
    [InlineData(5, 5, RatingValue.Loved)]
    [InlineData(3, 10, RatingValue.Down)]
    [InlineData(7, 10, RatingValue.Up)]
    [InlineData(9, 10, RatingValue.Loved)]
    public void StarScalesNormalizeOntoThumbs(int stars, int outOf, RatingValue expected) =>
        Assert.Equal(expected, RatingScale.FromStars(stars, outOf));

    [Fact]
    public void ADislikeCountsHarderThanALike() =>
        Assert.True(
            Math.Abs(RatingScale.Weight(RatingValue.Down)) > RatingScale.Weight(RatingValue.Up));
}

public class RatingsInTasteProfileTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AThumbsDownPushesItsGenreBelowAnUnratedOne()
    {
        var horror = Item(1, "Scary Thing", ["Horror"]);
        var comedy = Item(2, "Funny Thing", ["Comedy"]);

        var events = new[] { Watch(horror, Now.AddDays(-3)), Watch(comedy, Now.AddDays(-3)) };
        var ratings = new[] { Rate(horror, RatingValue.Down, Now.AddDays(-2)) };

        var profile = TasteProfileBuilder.Build("test", [1], [1], events, ratings, [], Now);

        // They watched both, but said outright they disliked the horror one.
        var horrorWeight = profile.TopGenres.Single(g => g.Name == "Horror").Weight;
        var comedyWeight = profile.TopGenres.Single(g => g.Name == "Comedy").Weight;

        Assert.True(horrorWeight < 0);
        Assert.True(comedyWeight > horrorWeight);
    }

    [Fact]
    public void RatedDownTitlesAppearInTheExclusionList()
    {
        var item = Item(1, "Regrettable Film", ["Drama"]);
        var ratings = new[] { Rate(item, RatingValue.Down, Now.AddDays(-1)) };

        var profile = TasteProfileBuilder.Build("test", [1], [1], [], ratings, [], Now);

        Assert.Contains("Regrettable Film", profile.Disliked);
    }

    [Fact]
    public void LovedTitlesAreCalledOutSeparatelyFromMerelyLikedOnes()
    {
        var loved = Item(1, "Masterpiece", ["Drama"]);
        var liked = Item(2, "Decent", ["Drama"]);

        var ratings = new[]
        {
            Rate(loved, RatingValue.Loved, Now.AddDays(-1)),
            Rate(liked, RatingValue.Up, Now.AddDays(-1)),
        };

        var profile = TasteProfileBuilder.Build("test", [1], [1], [], ratings, [], Now);

        Assert.Contains(profile.Loved, s => s.Contains("Masterpiece") && s.Contains("loved"));
        Assert.Contains(profile.Loved, s => s.Contains("Decent"));
        Assert.Equal(2, profile.TotalRatings);
    }

    [Fact]
    public void DislikingOneEpisodeDoesNotSinkTheShow()
    {
        var show = Item(1, "Great Show", ["Drama"], MediaKind.Show);

        var titleLevel = TasteProfileBuilder.Build(
            "test", [1], [1], [], [Rate(show, RatingValue.Down, Now.AddDays(-1))], [], Now);

        var episodeLevel = TasteProfileBuilder.Build(
            "test", [1], [1], [], [Rate(show, RatingValue.Down, Now.AddDays(-1), season: 3, episode: 7)], [], Now);

        var titleWeight = titleLevel.TopGenres.Single(g => g.Name == "Drama").Weight;
        var episodeWeight = episodeLevel.TopGenres.Single(g => g.Name == "Drama").Weight;

        // Both are negative, but one bad episode must hurt far less than writing off
        // the whole series.
        Assert.True(episodeWeight > titleWeight);
        Assert.Contains(episodeLevel.EpisodeRatings, s => s.Contains("S3E7"));

        // An episode rating is not a reason to stop recommending the series.
        Assert.Empty(episodeLevel.Disliked);
    }

    [Fact]
    public void OldRatingsMatterLessThanRecentOnes()
    {
        var item = Item(1, "Thing", ["Drama"]);

        var recent = TasteProfileBuilder.Build(
            "test", [1], [1], [], [Rate(item, RatingValue.Loved, Now.AddDays(-2))], [], Now);

        var old = TasteProfileBuilder.Build(
            "test", [1], [1], [], [Rate(item, RatingValue.Loved, Now.AddYears(-4))], [], Now);

        Assert.True(
            recent.TopGenres.Single(g => g.Name == "Drama").Weight
            > old.TopGenres.Single(g => g.Name == "Drama").Weight);
    }

    private static MediaItem Item(int id, string title, string[] genres, MediaKind kind = MediaKind.Movie) =>
        new() { Id = id, Title = title, Kind = kind, Genres = [.. genres] };

    private static WatchEvent Watch(MediaItem item, DateTimeOffset at) => new()
    {
        ProfileId = 1,
        MediaItem = item,
        MediaItemId = item.Id,
        WatchedAt = at,
        PercentComplete = 100,
        Fidelity = SourceFidelity.Full,
        SourceKey = Guid.NewGuid().ToString(),
    };

    private static Rating Rate(
        MediaItem item,
        RatingValue value,
        DateTimeOffset at,
        int? season = null,
        int? episode = null) => new()
    {
        PersonId = 1,
        MediaItem = item,
        MediaItemId = item.Id,
        Value = value,
        RatedAt = at,
        SeasonNumber = season,
        EpisodeNumber = episode,
        Source = WatchSource.Manual,
        SourceKey = Guid.NewGuid().ToString(),
    };
}

public class NetflixRatingsCsvImporterTests
{
    [Fact]
    public async Task ParsesAThumbsExport()
    {
        const string csv = """
            Profile Name,Title Name,Thumbs Value,Device Model,Event Utc Ts,Title Type
            Scott,Slow Horses,3,Apple TV,2026-08-14 20:14:02,Show
            Scott,Emily in Paris,1,Apple TV,2026-07-02 21:03:55,Show
            Scott,Hit Man,2,Web,2026-06-11 19:45:00,Movie
            """;

        var ratings = await ParseAsync(csv);

        Assert.Equal(3, ratings.Count);
        Assert.Equal(RatingValue.Loved, ratings[0].Value);
        Assert.Equal("Slow Horses", ratings[0].Title);
        Assert.Equal(RatingValue.Down, ratings[1].Value);
        Assert.Equal(RatingValue.Up, ratings[2].Value);
        Assert.Equal("Scott", ratings[0].ExternalProfileId);
    }

    [Fact]
    public async Task ParsesTheOlderFiveStarExport()
    {
        const string csv = """
            Profile Name,Title Name,Star Value,Date
            Scott,The Wire,5,03/14/2016
            Scott,Some Dud,1,04/02/2016
            """;

        var ratings = await ParseAsync(csv);

        Assert.Equal(RatingValue.Loved, ratings[0].Value);
        Assert.Equal(5, ratings[0].Stars);
        Assert.Equal(RatingValue.Down, ratings[1].Value);
    }

    [Fact]
    public async Task EpisodeRatingsAreRecognisedFromTheEpisodeColumn()
    {
        const string csv = """
            Profile Name,Title Name,Episode Title Name,Thumbs Value,Event Utc Ts
            Scott,Severance,Cold Harbor,3,2026-04-01 22:00:00
            """;

        var ratings = await ParseAsync(csv);

        Assert.Single(ratings);
        Assert.Equal(MediaKind.Episode, ratings[0].Kind);
    }

    [Fact]
    public async Task RejectsTheViewingActivityFileWithAPointerToTheRightOne()
    {
        // Easy mistake: the viewing-activity CSV and the ratings CSV both start with a
        // title column, so the error has to say where the real file comes from.
        const string csv = "Title,Date\nHit Man,8/30/26";

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => ParseAsync(csv));

        Assert.Contains("getmyinfo", ex.Message);
    }

    [Fact]
    public async Task ReimportingTheSameFileProducesTheSameKeys()
    {
        const string csv = """
            Profile Name,Title Name,Thumbs Value,Event Utc Ts
            Scott,Slow Horses,3,2026-08-14 20:14:02
            """;

        var first = await ParseAsync(csv);
        var second = await ParseAsync(csv);

        Assert.Equal(first[0].SourceKey, second[0].SourceKey);
    }

    private static async Task<List<Core.Abstractions.RawRating>> ParseAsync(string csv)
    {
        var importer = new NetflixRatingsCsvImporter(NullLogger<NetflixRatingsCsvImporter>.Instance);
        var results = new List<Core.Abstractions.RawRating>();

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        await foreach (var r in importer.ParseAsync(stream))
        {
            results.Add(r);
        }

        return results;
    }
}
