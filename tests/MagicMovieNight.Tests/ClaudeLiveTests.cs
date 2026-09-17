using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using MagicMovieNight.Core.Abstractions;
using MagicMovieNight.Core.Models;
using MagicMovieNight.Integrations;
using MagicMovieNight.Integrations.Claude;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace MagicMovieNight.Tests;

/// <summary>
/// Exercises the real Claude API with the exact request the engine builds.
///
/// Everything else in this suite is offline and deterministic. This one costs money
/// and needs a network, so it no-ops unless ANTHROPIC_API_KEY is set — the rest of the
/// suite must stay runnable on a laptop with no credentials. It is here because the
/// request shape (adaptive thinking, effort, structured output, a cache breakpoint on
/// the system prompt) is the part most likely to break silently against a live API,
/// and a compile is no evidence at all that the API accepts it.
/// </summary>
public class ClaudeLiveTests(ITestOutputHelper output)
{
    private static string? ApiKey => Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");

    [Fact]
    public async Task TheEngineRequestIsAcceptedAndReturnsUsablePicks()
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            output.WriteLine("ANTHROPIC_API_KEY not set — skipping the live API check.");
            return;
        }

        var profile = SampleProfile();
        var candidates = SampleCandidates();

        var options = new ClaudeOptions { Model = "claude-opus-5", Effort = "high" };

        var parameters = ClaudeRecommendationEngine.BuildRequest(
            profile,
            candidates,
            new RecommendationRequest { Count = 3, Prompt = "something tense, we have about two hours" },
            options);

        var client = new AnthropicClient { ApiKey = ApiKey };

        var response = await client.Messages.Create(parameters);

        // StopReason is an ApiEnum, so compare through its string conversion.
        Assert.False(response.StopReason == "refusal", "Claude refused the request.");

        var json = string.Concat(
            response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));

        output.WriteLine($"model:  {response.Model}");
        output.WriteLine($"stop:   {response.StopReason}");
        output.WriteLine($"tokens: {response.Usage?.InputTokens} in / {response.Usage?.OutputTokens} out");
        output.WriteLine($"cache:  {response.Usage?.CacheCreationInputTokens} written, "
            + $"{response.Usage?.CacheReadInputTokens} read");
        output.WriteLine(json);

        var parsed = JsonSerializer.Deserialize<LivePicks>(
            json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(parsed?.Picks);
        Assert.NotEmpty(parsed!.Picks!);

        var poolIds = candidates.Select(c => c.Item.Id).ToHashSet();

        foreach (var pick in parsed.Picks!)
        {
            // The candidate pool is the contract. A pick outside it is unusable even if
            // it names a real film, so this is the assertion that matters most.
            Assert.Contains(pick.Id, poolIds);
            Assert.False(string.IsNullOrWhiteSpace(pick.Pitch));
            Assert.False(string.IsNullOrWhiteSpace(pick.BasedOn));
        }
    }

    [Fact]
    public async Task TheSystemPromptIsActuallyCacheable()
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            output.WriteLine("ANTHROPIC_API_KEY not set — skipping the live API check.");
            return;
        }

        var options = new ClaudeOptions { Model = "claude-opus-5", Effort = "low" };
        var client = new AnthropicClient { ApiKey = ApiKey };

        // Two identical requests back to back: the second should read the system prompt
        // from cache rather than paying for it again. If this stops holding, the cost
        // claim in the README is wrong.
        var parameters = ClaudeRecommendationEngine.BuildRequest(
            SampleProfile(), SampleCandidates(), new RecommendationRequest { Count = 1 }, options);

        var first = await client.Messages.Create(parameters);
        var second = await client.Messages.Create(parameters);

        output.WriteLine($"first:  {first.Usage?.CacheCreationInputTokens} written, "
            + $"{first.Usage?.CacheReadInputTokens} read");
        output.WriteLine($"second: {second.Usage?.CacheCreationInputTokens} written, "
            + $"{second.Usage?.CacheReadInputTokens} read");

        Assert.True(
            second.Usage?.CacheReadInputTokens > 0,
            "The system prompt did not come back from cache on an identical second request.");
    }

    private static TasteProfile SampleProfile() => new()
    {
        Subject = "the whole household — Scott and Sam",
        ViewerIds = [1, 2],
        TotalWatches = 4210,
        FirstWatch = new DateTimeOffset(2016, 4, 2, 0, 0, 0, TimeSpan.Zero),
        LastWatch = DateTimeOffset.UtcNow.AddDays(-1),
        TopGenres =
        [
            new("Thriller", 42.1, 88),
            new("Drama", 38.7, 120),
            new("Science Fiction", 22.4, 51),
            new("Comedy", 9.2, 40),
        ],
        TopPeople =
        [
            new("Denis Villeneuve", 8.4, 5),
            new("Gary Oldman", 7.1, 9),
            new("Tony Gilroy", 6.6, 4),
        ],
        RecentlyLoved = ["Slow Horses (2022)", "Andor (2022)", "Dune: Part Two (2024)"],
        Abandoned = ["Emily in Paris (2020)"],
        InProgress = ["Severance (2022)"],
        TypicalMovieRuntime = 118,
        ShowBias = 0.62,
        Disliked = ["Emily in Paris (2020)"],
        Loved = ["Slow Horses (2022) (loved it)", "Andor (2022) (loved it)"],
        EpisodeRatings = ["Severance S2E4 — thumbs down"],
        TotalRatings = 3,
    };

    private static List<Candidate> SampleCandidates() =>
    [
        Candidate(101, "Sicario", 2015, 121, ["Thriller", "Crime"], ["Denis Villeneuve", "Emily Blunt"], inLibrary: true),
        Candidate(102, "The Lives of Others", 2006, 137, ["Drama", "Thriller"], ["Florian Henckel von Donnersmarck"]),
        Candidate(103, "Paddington 2", 2017, 103, ["Comedy", "Family"], ["Paul King"], inLibrary: true),
        Candidate(104, "Tinker Tailor Soldier Spy", 2011, 127, ["Thriller", "Drama"], ["Tomas Alfredson", "Gary Oldman"]),
        Candidate(105, "Michael Clayton", 2007, 119, ["Thriller", "Drama"], ["Tony Gilroy", "George Clooney"]),
    ];

    private static Candidate Candidate(
        int id, string title, int year, int runtime,
        string[] genres, string[] people, bool inLibrary = false) =>
        new()
        {
            Item = new MediaItem
            {
                Id = id,
                Title = title,
                Year = year,
                Kind = MediaKind.Movie,
                RuntimeMinutes = runtime,
                Genres = [.. genres],
                People = [.. people],
                CommunityRating = 7.8,
                InLibrary = inLibrary,
                AvailableOn = inLibrary ? [] : ["Netflix"],
            },
            Source = "test",
            Origin = RecommendationOrigin.LibraryMatch,
            PreScore = 1.0,
        };

    private record LivePicks(List<LivePick>? Picks);

    private record LivePick(int Id, string? Pitch, string? BasedOn, string? WhereToWatch, int? Confidence);
}
