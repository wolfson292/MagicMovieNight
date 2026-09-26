using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Xunit.Abstractions;

namespace MagicMovieNight.Tests;

/// <summary>
/// Loads every page against a real database and asserts it comes back 200.
///
/// This exists because of a specific failure that kept recurring: code that compiles,
/// deploys, and renders, but throws the moment it runs. The History page shipped with a
/// grouping EF could not translate — invisible to the compiler, invisible to a
/// screenshot, and a 500 for anyone who clicked the tab. Checking that a page renders is
/// not the same as checking that it works, and only executing the query finds these.
///
/// Needs Postgres. CI provides one as a service container; locally the tests no-op
/// unless TEST_DATABASE_URL is set, so the rest of the suite stays runnable anywhere.
/// </summary>
public class PageSmokeTests(ITestOutputHelper output)
{
    private static string? ConnectionString => Environment.GetEnvironmentVariable("TEST_DATABASE_URL");

    [Theory]
    [InlineData("/")]
    [InlineData("/rate")]
    [InlineData("/history")]
    [InlineData("/import")]
    [InlineData("/household")]
    [InlineData("/setup")]
    [InlineData("/health")]
    public async Task EveryPageLoads(string path)
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            output.WriteLine("TEST_DATABASE_URL not set — skipping page smoke tests.");
            return;
        }

        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();

        output.WriteLine($"{path} -> {(int)response.StatusCode}");

        Assert.True(
            response.IsSuccessStatusCode,
            $"{path} returned {(int)response.StatusCode}. Body starts: "
                + body[..Math.Min(400, body.Length)]);
    }

    [Fact]
    public async Task ThePagesRenderTheirOwnContentRatherThanAnErrorShell()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            output.WriteLine("TEST_DATABASE_URL not set — skipping page smoke tests.");
            return;
        }

        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        // A 200 alone is not proof: Blazor's error boundary can swallow an exception and
        // still return a page. Look for content only the real page produces.
        var history = await client.GetStringAsync("/history");

        Assert.Contains("Watch events", history);
        Assert.DoesNotContain("An error occurred while processing your request", history);
    }

    /// <summary>
    /// The JSON other things in the house read.
    /// </summary>
    /// <remarks>
    /// Here rather than with the unit tests for the same reason the pages are:
    /// it projects across a navigation property, and an EF projection that
    /// cannot be translated compiles, deploys, and throws the first time
    /// anything asks. Only running the query finds that.
    ///
    /// An empty database is the interesting case as well as the easy one --
    /// there are no recommendations until somebody runs one, so "no picks yet"
    /// is a normal answer this must give rather than an error.
    /// </remarks>
    [Fact]
    public async Task TonightAnswersWithJsonEvenWithNothingToSuggest()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            output.WriteLine("TEST_DATABASE_URL not set — skipping page smoke tests.");
            return;
        }

        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/tonight");
        var body = await response.Content.ReadAsStringAsync();

        output.WriteLine($"/api/tonight -> {(int)response.StatusCode}: {body[..Math.Min(200, body.Length)]}");

        Assert.True(
            response.IsSuccessStatusCode,
            $"/api/tonight returned {(int)response.StatusCode}. Body starts: "
                + body[..Math.Min(400, body.Length)]);

        using var parsed = System.Text.Json.JsonDocument.Parse(body);
        Assert.True(parsed.RootElement.TryGetProperty("picks", out var picks));
        Assert.Equal(System.Text.Json.JsonValueKind.Array, picks.ValueKind);
    }

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.UseSetting("ConnectionStrings:MovieNight", ConnectionString!);
        });
}
