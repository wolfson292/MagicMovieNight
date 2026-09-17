using MagicMovieNight.Integrations;
using MagicMovieNight.Integrations.Arr;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MagicMovieNight.Tests;

public class ArrClientTests
{
    [Theory]
    [InlineData("SonarrClient", "http://sonarr:8989")]
    [InlineData("RadarrClient", "http://radarr:7878")]
    public void ArrClientsAuthenticateWithTheApiKeyHeader(string clientName, string expectedBase)
    {
        var client = CreateClient(clientName);

        // Sonarr and Radarr both take the key as a header, not a query parameter.
        Assert.True(client.DefaultRequestHeaders.Contains("X-Api-Key"));
        Assert.Equal(expectedBase + "/", client.BaseAddress?.ToString());
    }

    [Theory]
    [InlineData("SonarrClient")]
    [InlineData("RadarrClient")]
    public void ArrBaseAddressesKeepTheirTrailingSlash(string clientName)
    {
        // Same trap as TMDB: without it, "api/v3/series" would resolve against the
        // parent path and the request would miss.
        var client = CreateClient(clientName);

        Assert.EndsWith("/", client.BaseAddress?.ToString());
        Assert.Equal(
            $"{client.BaseAddress}api/v3/series",
            new Uri(client.BaseAddress!, "api/v3/series").ToString());
    }

    [Fact]
    public void AValidationErrorIsReducedToItsFirstMessage()
    {
        const string body = """
            [{"propertyName":"TvdbId","errorMessage":"This series has already been added","severity":"error"}]
            """;

        Assert.Equal("This series has already been added", ArrClient.Summarise(body));
    }

    [Fact]
    public void ANonJsonErrorBodyIsPassedThroughTruncated()
    {
        var body = new string('x', 500);

        var summary = ArrClient.Summarise(body);

        Assert.Equal(200, summary.Length);
    }

    [Fact]
    public void AnEmptyBodySummarisesToNothing() => Assert.Equal(string.Empty, ArrClient.Summarise(""));

    private static HttpClient CreateClient(string name)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Deliberately without trailing slashes, to prove normalisation works.
                ["Sonarr:BaseUrl"] = "http://sonarr:8989",
                ["Sonarr:ApiKey"] = "sonarr-key",
                ["Radarr:BaseUrl"] = "http://radarr:7878",
                ["Radarr:ApiKey"] = "radarr-key",
            })
            .Build();

        var provider = new ServiceCollection()
            .AddLogging()
            .AddMovieNightIntegrations(config)
            .BuildServiceProvider();

        return provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);
    }
}
