using MagicMovieNight.Integrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MagicMovieNight.Tests;

/// <summary>
/// Pins the outbound HTTP client configuration.
///
/// Trakt answers 403 to any request without a User-Agent, and HttpClient sends none by
/// default. The failure is invisible from the outside: the credentials are valid, the
/// host is reachable, and the same request from curl succeeds — because curl always
/// sends a User-Agent. Pairing simply never started.
/// </summary>
public class HttpClientConfigurationTests
{
    [Theory]
    [InlineData("TraktAuthService")]
    [InlineData("TraktClient")]
    public void TraktClientsSendAUserAgent(string clientName)
    {
        var client = CreateClient(clientName);

        Assert.NotEmpty(client.DefaultRequestHeaders.UserAgent);
        Assert.Contains("MagicMovieNight", client.DefaultRequestHeaders.UserAgent.ToString());
    }

    [Theory]
    [InlineData("TraktAuthService")]
    [InlineData("TraktClient")]
    public void TraktClientsSendTheApiVersionAndKey(string clientName)
    {
        var client = CreateClient(clientName);

        Assert.True(client.DefaultRequestHeaders.Contains("trakt-api-version"));
        Assert.True(client.DefaultRequestHeaders.Contains("trakt-api-key"));
        Assert.Equal("https://api.trakt.tv", client.BaseAddress?.ToString().TrimEnd('/'));
    }

    [Fact]
    public void TheTmdbClientBaseAddressKeepsTheApiVersionSegment()
    {
        var client = CreateClient("TmdbClient");

        // Guards the other half of the TMDB bug: without the trailing slash every
        // relative request silently loses "/3".
        Assert.Equal("https://api.themoviedb.org/3/", client.BaseAddress?.ToString());
        Assert.NotEmpty(client.DefaultRequestHeaders.UserAgent);
    }

    [Fact]
    public void TheTautulliClientUsesTheConfiguredBaseAddress()
    {
        var client = CreateClient("TautulliClient");

        Assert.Equal("http://tautulli:8181/", client.BaseAddress?.ToString());
    }

    private static HttpClient CreateClient(string name)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Trakt:ClientId"] = "test-client-id",
                ["Trakt:ClientSecret"] = "test-secret",
                ["Tautulli:BaseUrl"] = "http://tautulli:8181",
                ["Tautulli:ApiKey"] = "test-key",
                // Deliberately without the trailing slash, to prove normalisation works.
                ["Tmdb:BaseUrl"] = "https://api.themoviedb.org/3",
                ["Tmdb:ApiToken"] = "test-token",
            })
            .Build();

        var provider = new ServiceCollection()
            .AddLogging()
            .AddMovieNightIntegrations(config)
            .BuildServiceProvider();

        return provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);
    }
}
