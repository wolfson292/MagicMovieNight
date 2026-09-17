using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Anthropic;
using Anthropic.Models.Messages;
using MagicMovieNight.Core.Abstractions;
using MagicMovieNight.Core.Models;
using MagicMovieNight.Core.Taste;
using MagicMovieNight.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MagicMovieNight.Integrations.Claude;

/// <summary>
/// Turns a taste profile plus a candidate pool into tonight's slate.
///
/// The model never sees raw history — tens of thousands of rows would be mostly
/// noise and would blow the budget. It sees a distilled profile and a pre-trimmed
/// pool, and its job is judgement: which of these, for these people, tonight, and
/// why.
/// </summary>
public class ClaudeRecommendationEngine(
    AnthropicClient client,
    MovieNightDbContext db,
    IEnumerable<ICandidateSource> candidateSources,
    TasteProfileService profiles,
    IOptions<ClaudeOptions> options,
    ILogger<ClaudeRecommendationEngine> logger) : IRecommendationEngine
{
    private readonly ClaudeOptions _options = options.Value;

    public async Task<RecommendationRun> RecommendAsync(
        RecommendationRequest request,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();

        var run = new RecommendationRun
        {
            ViewerIds = request.ViewerIds.ToList(),
            Prompt = request.Prompt,
            ModelId = _options.Model,
        };

        var profile = await profiles.BuildAsync(request.ViewerIds, ct);
        var candidates = await GatherCandidatesAsync(profile, request, ct);

        run.CandidateCount = candidates.Count;

        if (candidates.Count == 0)
        {
            run.Error = "No candidates available. Run a history sync, and check that "
                + "TMDB and Trakt are configured.";
            run.Duration = stopwatch.Elapsed;
            db.RecommendationRuns.Add(run);
            await db.SaveChangesAsync(ct);
            return run;
        }

        try
        {
            var picks = await AskClaudeAsync(profile, candidates, request, run, ct);

            var byId = candidates.ToDictionary(c => c.Item.Id, c => c);
            var rank = 1;

            foreach (var pick in picks)
            {
                if (!byId.TryGetValue(pick.Id, out var candidate))
                {
                    // The pool is the contract; anything outside it is unusable even
                    // if it is a real film.
                    logger.LogWarning(
                        "Claude returned id {Id} which was not in the candidate pool; skipping.",
                        pick.Id);
                    continue;
                }

                run.Recommendations.Add(new Recommendation
                {
                    MediaItemId = candidate.Item.Id,
                    Rank = rank++,
                    Pitch = pick.Pitch,
                    BasedOn = pick.BasedOn,
                    WhereToWatch = pick.WhereToWatch ?? DescribeAvailability(candidate.Item),
                    Origin = candidate.Origin,
                    Confidence = pick.Confidence,
                });
            }

            if (run.Recommendations.Count == 0)
            {
                run.Error = "Claude returned no usable picks from the candidate pool.";
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Recommendation run failed.");
            run.Error = ex.Message;
        }

        run.Duration = stopwatch.Elapsed;
        db.RecommendationRuns.Add(run);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Recommendation run {Id}: {Count} picks from {Candidates} candidates in {Elapsed:N1}s ({In} in / {Out} out tokens).",
            run.Id, run.Recommendations.Count, run.CandidateCount,
            run.Duration.TotalSeconds, run.InputTokens, run.OutputTokens);

        return run;
    }

    private async Task<List<Candidate>> GatherCandidatesAsync(
        TasteProfile profile,
        RecommendationRequest request,
        CancellationToken ct)
    {
        var all = new List<Candidate>();

        foreach (var source in candidateSources)
        {
            try
            {
                all.AddRange(await source.GetCandidatesAsync(profile, ct));
            }
            catch (Exception ex)
            {
                // One dead source must not take the whole run down with it.
                logger.LogWarning(ex, "Candidate source {Source} failed; continuing without it.", source.Name);
            }
        }

        var filtered = all
            .GroupBy(c => c.Item.Id)
            .Select(g => g.OrderByDescending(c => c.PreScore).First())
            .Where(c => !request.LibraryOnly || c.Item.InLibrary)
            .Where(c => request.MaxRuntimeMinutes is null
                || c.Item.RuntimeMinutes is null
                || c.Item.RuntimeMinutes <= request.MaxRuntimeMinutes)
            .OrderByDescending(c => c.PreScore)
            .Take(_options.MaxCandidates)
            .ToList();

        logger.LogDebug(
            "Gathered {Total} candidates from {Sources} sources, trimmed to {Kept}.",
            all.Count, candidateSources.Count(), filtered.Count);

        return filtered;
    }

    private async Task<List<ClaudePick>> AskClaudeAsync(
        TasteProfile profile,
        IReadOnlyList<Candidate> candidates,
        RecommendationRequest request,
        RecommendationRun run,
        CancellationToken ct)
    {
        var parameters = BuildRequest(profile, candidates, request, _options);

        var response = await client.Messages.Create(parameters, cancellationToken: ct);

        run.InputTokens = response.Usage?.InputTokens ?? 0;
        run.OutputTokens = response.Usage?.OutputTokens ?? 0;

        if (response.StopReason == "refusal")
        {
            var detail = response.StopDetails?.Explanation ?? "no explanation given";
            throw new InvalidOperationException($"Claude declined this request: {detail}");
        }

        var json = string.Concat(
            response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));

        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException("Claude returned an empty response.");
        }

        var parsed = JsonSerializer.Deserialize<ClaudeResponse>(
            json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        return parsed?.Picks ?? [];
    }

    /// <summary>
    /// Builds the request. Separated from the call so the live smoke test can verify
    /// the exact shape the engine sends, rather than a copy of it that can drift.
    /// </summary>
    internal static MessageCreateParams BuildRequest(
        TasteProfile profile,
        IReadOnlyList<Candidate> candidates,
        RecommendationRequest request,
        ClaudeOptions options) =>
        new()
        {
            Model = options.Model,
            MaxTokens = 16000,
            // The system prompt is byte-identical across runs, so cache it and pay for
            // it once rather than on every movie night.
            System = new List<TextBlockParam>
            {
                new()
                {
                    Text = RecommendationPrompt.System,
                    CacheControl = new CacheControlEphemeral(),
                },
            },
            Thinking = new ThinkingConfigAdaptive(),
            OutputConfig = new OutputConfig
            {
                Effort = ParseEffort(options.Effort),
                Format = new JsonOutputFormat { Schema = ResponseSchema },
            },
            Messages =
            [
                new()
                {
                    Role = Role.User,
                    Content = RecommendationPrompt.BuildUserTurn(profile, candidates, request),
                },
            ],
        };

    private static string DescribeAvailability(MediaItem item) =>
        item.InLibrary
            ? "Plex"
            : item.AvailableOn.Count > 0
                ? string.Join(", ", item.AvailableOn)
                : "Not on a subscribed service";

    private static Effort ParseEffort(string value) => value.ToLowerInvariant() switch
    {
        "low" => Effort.Low,
        "medium" => Effort.Medium,
        "high" => Effort.High,
        "max" => Effort.Max,
        _ => Effort.High,
    };

    /// <summary>
    /// Structured output is what makes the reply safe to consume. Without it the model
    /// might return prose around the JSON and every run becomes a parsing gamble.
    /// </summary>
    private static Dictionary<string, JsonElement> ResponseSchema { get; } = BuildSchema();

    private static Dictionary<string, JsonElement> BuildSchema() => new()
    {
        ["type"] = JsonSerializer.SerializeToElement("object"),
        ["additionalProperties"] = JsonSerializer.SerializeToElement(false),
        ["required"] = JsonSerializer.SerializeToElement(new[] { "picks" }),
        ["properties"] = JsonSerializer.SerializeToElement(new
        {
            picks = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    additionalProperties = false,
                    required = new[] { "id", "pitch", "basedOn" },
                    properties = new
                    {
                        id = new
                        {
                            type = "integer",
                            description = "The bracketed id of the chosen candidate.",
                        },
                        pitch = new
                        {
                            type = "string",
                            description = "Two or three conversational sentences making the case for tonight.",
                        },
                        basedOn = new
                        {
                            type = "string",
                            description = "The specific viewing history that justifies this pick.",
                        },
                        whereToWatch = new
                        {
                            type = "string",
                            description = "Where they can actually play it.",
                        },
                        confidence = new
                        {
                            type = "integer",
                            description = "0-100 confidence this lands with them tonight.",
                        },
                    },
                },
            },
        }),
    };

    private record ClaudeResponse
    {
        [JsonPropertyName("picks")]
        public List<ClaudePick>? Picks { get; init; }
    }

    private record ClaudePick
    {
        public int Id { get; init; }

        public required string Pitch { get; init; }

        public string? BasedOn { get; init; }

        public string? WhereToWatch { get; init; }

        public int? Confidence { get; init; }
    }
}
