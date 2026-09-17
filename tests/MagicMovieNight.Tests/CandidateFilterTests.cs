using MagicMovieNight.Core.Abstractions;
using MagicMovieNight.Core.Models;
using MagicMovieNight.Core.Taste;

namespace MagicMovieNight.Tests;

/// <summary>
/// The prompt asks the model not to suggest things already seen. This is the part that
/// guarantees it — an instruction is a request, an exclusion is enforcement.
/// </summary>
public class CandidateFilterTests
{
    private static readonly RecommendationRequest Anything = new();

    [Fact]
    public void ExcludedTitlesNeverReachTheModel()
    {
        var pool = new[] { Candidate(1, "Seen It"), Candidate(2, "Fresh") };

        var kept = CandidateFilter.Apply(pool, new HashSet<int> { 1 }, Anything, 10);

        Assert.Single(kept);
        Assert.Equal("Fresh", kept[0].Item.Title);
    }

    [Fact]
    public void DuplicateProposalsCollapseToTheHighestRanked()
    {
        // The library and Trakt can both propose the same title; the pool must not
        // contain it twice, and the better provenance should win.
        var pool = new[]
        {
            Candidate(1, "Sicario", preScore: 0.4, source: "library"),
            Candidate(1, "Sicario", preScore: 2.1, source: "trakt"),
        };

        var kept = CandidateFilter.Apply(pool, new HashSet<int>(), Anything, 10);

        Assert.Single(kept);
        Assert.Equal("trakt", kept[0].Source);
    }

    [Fact]
    public void RuntimeCapIsRespected()
    {
        var pool = new[] { Candidate(1, "Short", runtime: 95), Candidate(2, "Epic", runtime: 201) };

        var kept = CandidateFilter.Apply(
            pool, new HashSet<int>(), new RecommendationRequest { MaxRuntimeMinutes = 120 }, 10);

        Assert.Single(kept);
        Assert.Equal("Short", kept[0].Item.Title);
    }

    [Fact]
    public void AnUnknownRuntimeSurvivesTheCap()
    {
        // Dropping these would silently discard everything TMDB could not identify —
        // exactly the long tail worth surfacing.
        var pool = new[] { Candidate(1, "Mystery", runtime: null) };

        var kept = CandidateFilter.Apply(
            pool, new HashSet<int>(), new RecommendationRequest { MaxRuntimeMinutes = 90 }, 10);

        Assert.Single(kept);
    }

    [Fact]
    public void LibraryOnlyKeepsOnlyLocalTitles()
    {
        var pool = new[]
        {
            Candidate(1, "On Plex", inLibrary: true),
            Candidate(2, "On Netflix", inLibrary: false),
        };

        var kept = CandidateFilter.Apply(
            pool, new HashSet<int>(), new RecommendationRequest { LibraryOnly = true }, 10);

        Assert.Single(kept);
        Assert.Equal("On Plex", kept[0].Item.Title);
    }

    [Fact]
    public void TheBestCandidatesSurviveTheCap()
    {
        var pool = Enumerable.Range(1, 50)
            .Select(i => Candidate(i, $"Title {i}", preScore: i))
            .ToList();

        var kept = CandidateFilter.Apply(pool, new HashSet<int>(), Anything, 5);

        Assert.Equal(5, kept.Count);
        Assert.Equal(50, kept[0].Item.Id);
        Assert.All(kept, c => Assert.True(c.PreScore >= 46));
    }

    [Fact]
    public void AskingForRewatchesLiftsTheExclusions()
    {
        var pool = new[] { Candidate(1, "Seen It"), Candidate(2, "Fresh") };

        var kept = CandidateFilter.Apply(
            pool,
            new HashSet<int> { 1 },
            new RecommendationRequest { IncludeAlreadyWatched = true },
            10);

        Assert.Equal(2, kept.Count);
        Assert.Contains(kept, c => c.Item.Title == "Seen It");
    }

    [Fact]
    public void RewatchesStillRespectTheOtherFilters()
    {
        // Lifting the already-seen exclusion must not quietly lift the runtime cap too.
        var pool = new[]
        {
            Candidate(1, "Seen And Short", runtime: 95),
            Candidate(2, "Seen And Endless", runtime: 240),
        };

        var kept = CandidateFilter.Apply(
            pool,
            new HashSet<int> { 1, 2 },
            new RecommendationRequest { IncludeAlreadyWatched = true, MaxRuntimeMinutes = 120 },
            10);

        Assert.Single(kept);
        Assert.Equal("Seen And Short", kept[0].Item.Title);
    }

    [Fact]
    public void ExclusionsApplyByDefault()
    {
        // The default must stay "something new" — a rewatch is opt-in, never implicit.
        var kept = CandidateFilter.Apply(
            new[] { Candidate(1, "Seen It") }, new HashSet<int> { 1 }, new RecommendationRequest(), 10);

        Assert.Empty(kept);
    }

    [Fact]
    public void EverythingExcludedYieldsAnEmptyPoolRatherThanThrowing()
    {
        var pool = new[] { Candidate(1, "Seen"), Candidate(2, "Also Seen") };

        var kept = CandidateFilter.Apply(pool, new HashSet<int> { 1, 2 }, Anything, 10);

        Assert.Empty(kept);
    }

    private static Candidate Candidate(
        int id, string title, double preScore = 1.0, int? runtime = 100,
        bool inLibrary = false, string source = "test") =>
        new()
        {
            Item = new MediaItem
            {
                Id = id,
                Title = title,
                Kind = MediaKind.Movie,
                RuntimeMinutes = runtime,
                InLibrary = inLibrary,
            },
            Source = source,
            PreScore = preScore,
        };
}
