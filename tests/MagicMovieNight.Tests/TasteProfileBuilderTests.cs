using MagicMovieNight.Core.Models;
using MagicMovieNight.Core.Taste;

namespace MagicMovieNight.Tests;

public class TasteProfileBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RecentWatchesOutweighOldOnes()
    {
        var events = new[]
        {
            Watch("Old Thriller", Now.AddYears(-3), ["Thriller"]),
            Watch("Old Thriller 2", Now.AddYears(-3), ["Thriller"]),
            Watch("Old Thriller 3", Now.AddYears(-3), ["Thriller"]),
            Watch("New Comedy", Now.AddDays(-5), ["Comedy"]),
        };

        var profile = TasteProfileBuilder.Build("test", [1], events, [], [], Now);

        // Three old thrillers must not outrank one comedy from last week.
        Assert.Equal("Comedy", profile.TopGenres[0].Name);
    }

    [Fact]
    public void FullFidelitySourcesOutweighCsvRows()
    {
        var tautulli = Watch("A", Now.AddDays(-10), ["Drama"]);
        var csv = Watch("B", Now.AddDays(-10), ["Drama"], fidelity: SourceFidelity.TitleAndDate);

        Assert.True(
            TasteProfileBuilder.Weight(tautulli, Now) > TasteProfileBuilder.Weight(csv, Now));
    }

    [Fact]
    public void AbandonedTitlesAreExcludedFromAffinityAndListedSeparately()
    {
        var events = new[]
        {
            Watch("Finished It", Now.AddDays(-2), ["Comedy"], percent: 95),
            Watch("Bailed On It", Now.AddDays(-1), ["Horror"], percent: 8),
        };

        var profile = TasteProfileBuilder.Build("test", [1], events, [], [], Now);

        Assert.Contains("Bailed On It", profile.Abandoned[0]);
        Assert.DoesNotContain(profile.TopGenres, g => g.Name == "Horror");
        Assert.Contains(profile.TopGenres, g => g.Name == "Comedy");
    }

    [Fact]
    public void RewatchingSomethingClearsItsAbandonedStatus()
    {
        var item = Item("Slow Burn", ["Drama"]);

        var events = new[]
        {
            Watch(item, Now.AddDays(-30), percent: 10),
            Watch(item, Now.AddDays(-2), percent: 98),
        };

        var profile = TasteProfileBuilder.Build("test", [1], events, [], [], Now);

        // They came back and finished it — that is the opposite of a negative signal.
        Assert.Empty(profile.Abandoned);
        Assert.Contains("Slow Burn", profile.RecentlyLoved);
    }

    [Fact]
    public void SourcesWithoutCompletionDataCountAsWatched()
    {
        var events = new[] { Watch("Netflix Thing", Now.AddDays(-3), ["Comedy"], percent: null) };

        var profile = TasteProfileBuilder.Build("test", [1], events, [], [], Now);

        Assert.Contains("Netflix Thing", profile.RecentlyLoved);
    }

    [Fact]
    public void TypicalRuntimeUsesFinishedFilmsOnly()
    {
        var events = new[]
        {
            Watch(Item("Short", ["Comedy"], runtime: 90), Now.AddDays(-5), percent: 100),
            Watch(Item("Medium", ["Comedy"], runtime: 110), Now.AddDays(-4), percent: 100),
            Watch(Item("Epic", ["Drama"], runtime: 240), Now.AddDays(-3), percent: 5),
        };

        var profile = TasteProfileBuilder.Build("test", [1], events, [], [], Now);

        // The three-hour film they bailed on should not raise the household's tolerance.
        Assert.Equal(100, profile.TypicalMovieRuntime);
    }

    [Fact]
    public void ShowBiasReflectsTheSplit()
    {
        var events = new[]
        {
            Watch(Item("Series A", ["Drama"], kind: MediaKind.Show), Now.AddDays(-1)),
            Watch(Item("Series B", ["Drama"], kind: MediaKind.Show), Now.AddDays(-2)),
            Watch(Item("Film A", ["Drama"]), Now.AddDays(-3)),
            Watch(Item("Film B", ["Drama"]), Now.AddDays(-4)),
        };

        var profile = TasteProfileBuilder.Build("test", [1], events, [], [], Now);

        Assert.Equal(0.5, profile.ShowBias, 3);
    }

    [Fact]
    public void EmptyHistoryProducesAUsableProfile()
    {
        var profile = TasteProfileBuilder.Build("test", [1], [], [], [], Now);

        Assert.Equal(0, profile.TotalWatches);
        Assert.Empty(profile.TopGenres);
        Assert.Null(profile.TypicalMovieRuntime);
        Assert.Equal(0, profile.ShowBias);
    }

    private static MediaItem Item(
        string title,
        string[] genres,
        int? runtime = null,
        MediaKind kind = MediaKind.Movie) =>
        new()
        {
            Id = title.GetHashCode(),
            Title = title,
            Kind = kind,
            Genres = [.. genres],
            RuntimeMinutes = runtime,
        };

    private static WatchEvent Watch(
        string title,
        DateTimeOffset at,
        string[] genres,
        int? percent = 100,
        SourceFidelity fidelity = SourceFidelity.Full) =>
        Watch(Item(title, genres), at, percent, fidelity);

    private static WatchEvent Watch(
        MediaItem item,
        DateTimeOffset at,
        int? percent = 100,
        SourceFidelity fidelity = SourceFidelity.Full) =>
        new()
        {
            ViewerId = 1,
            MediaItem = item,
            MediaItemId = item.Id,
            WatchedAt = at,
            PercentComplete = percent,
            Fidelity = fidelity,
            SourceKey = Guid.NewGuid().ToString(),
        };
}
