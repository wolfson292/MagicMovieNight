using System.Text.Json;
using MagicMovieNight.Integrations.Tautulli;

namespace MagicMovieNight.Tests;

/// <summary>
/// Guards against Tautulli's loose typing. Every payload here is shaped like a real
/// response from a live server — the mixed types are not hypothetical, they are what
/// broke the first production sync: `rating_key` arrived as a number against a string
/// property and the exception took down the entire page of history.
/// </summary>
public class TautulliParsingTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// One episode row and one movie row, exactly as a live server sends them: numeric
    /// rating_key, numeric season/episode on the episode, empty strings on the movie.
    /// </summary>
    private const string RealisticPage = """
        {
          "response": {
            "result": "success",
            "data": {
              "recordsTotal": 18929,
              "data": [
                {
                  "row_id": 18929,
                  "date": 1789677860,
                  "user": "Deb",
                  "user_id": 183413862,
                  "friendly_name": "Deb",
                  "media_type": "episode",
                  "title": "Yes & Baby",
                  "full_title": "Ted Lasso - Yes & Baby",
                  "grandparent_title": "Ted Lasso",
                  "year": 2026,
                  "percent_complete": 100,
                  "player": "Deborah's FireTVStick",
                  "rating_key": 279015,
                  "parent_media_index": 4,
                  "media_index": 7
                },
                {
                  "row_id": 18928,
                  "date": 1789600000,
                  "user": "scott",
                  "user_id": 1,
                  "friendly_name": "Scott",
                  "media_type": "movie",
                  "title": "Sicario",
                  "full_title": "Sicario",
                  "grandparent_title": "",
                  "year": 2015,
                  "percent_complete": 64,
                  "player": "Living Room Apple TV",
                  "rating_key": 12345,
                  "parent_media_index": "",
                  "media_index": ""
                }
              ]
            }
          }
        }
        """;

    [Fact]
    public void ARealPageParsesInsteadOfThrowing()
    {
        var envelope = JsonSerializer.Deserialize<TautulliEnvelope>(RealisticPage, Options);

        var rows = envelope?.Response?.Data?.Data;

        Assert.NotNull(rows);
        Assert.Equal(2, rows!.Count);
        Assert.Equal(18929, envelope!.Response!.Data!.RecordsTotal);
    }

    [Fact]
    public void ANumericRatingKeyBecomesAString()
    {
        var rows = Parse();

        // This exact mismatch aborted the first live sync.
        Assert.Equal("279015", rows[0].RatingKey);
    }

    [Fact]
    public void EpisodeNumberingSurvivesAndMoviesGetNulls()
    {
        var rows = Parse();

        Assert.Equal(4, rows[0].ParentMediaIndex);
        Assert.Equal(7, rows[0].MediaIndex);

        // The movie row sends "" for both — that means "not applicable", not a parse error.
        Assert.Null(rows[1].ParentMediaIndex);
        Assert.Null(rows[1].MediaIndex);
    }

    [Fact]
    public void TheFieldsTheProfileDependsOnSurvive()
    {
        var rows = Parse();

        Assert.Equal("Deb", rows[0].User);
        Assert.Equal("Ted Lasso", rows[0].GrandparentTitle);
        Assert.Equal(100, rows[0].PercentComplete);
        Assert.Equal("Deborah's FireTVStick", rows[0].Player);
        Assert.Equal(1789677860, rows[0].Date);
        Assert.Equal(18929, rows[0].RowId);

        // Partial completion is the abandonment signal, so it has to come through intact.
        Assert.Equal(64, rows[1].PercentComplete);
    }

    [Theory]
    [InlineData("\"\"", null)]
    [InlineData("null", null)]
    [InlineData("\"12\"", 12)]
    [InlineData("12", 12)]
    [InlineData("\"not a number\"", null)]
    public void OptionalIntegersTolerateEveryShapeTautulliSends(string json, int? expected)
    {
        var row = JsonSerializer.Deserialize<TautulliHistoryRow>(
            $$"""{"media_index": {{json}}}""", Options);

        Assert.Equal(expected, row!.MediaIndex);
    }

    [Theory]
    [InlineData("279015", "279015")]
    [InlineData("\"279015\"", "279015")]
    [InlineData("null", null)]
    public void RatingKeyTakesNumbersOrStrings(string json, string? expected)
    {
        var row = JsonSerializer.Deserialize<TautulliHistoryRow>(
            $$"""{"rating_key": {{json}}}""", Options);

        Assert.Equal(expected, row!.RatingKey);
    }

    private static List<TautulliHistoryRow> Parse() =>
        JsonSerializer.Deserialize<TautulliEnvelope>(RealisticPage, Options)!
            .Response!.Data!.Data!;
}
