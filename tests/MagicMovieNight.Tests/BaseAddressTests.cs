using MagicMovieNight.Integrations;

namespace MagicMovieNight.Tests;

/// <summary>
/// Pins the URI composition rules that silently broke TMDB enrichment in production.
///
/// HttpClient resolves relative URIs per RFC 3986. Two rules bite:
/// a relative URI beginning with "/" resets to the host root, and a base address whose
/// path lacks a trailing slash has its last segment treated as a file and replaced.
/// Either one turns "https://api.themoviedb.org/3/search/movie" into
/// "https://api.themoviedb.org/search/movie", which 404s. The client caught the 404,
/// logged a warning, and returned null — so every title was ingested with no genres,
/// cast or runtime and nothing looked broken.
/// </summary>
public class BaseAddressTests
{
    [Fact]
    public void TheTmdbDefaultBaseUrlKeepsItsTrailingSlash()
    {
        var options = new TmdbOptions();

        Assert.EndsWith("/", options.BaseUrl);
    }

    [Fact]
    public void TheConfiguredBaseAndARelativePathResolveToARealTmdbEndpoint()
    {
        var resolved = Resolve(new TmdbOptions().BaseUrl, "search/movie?query=Sicario");

        Assert.Equal("https://api.themoviedb.org/3/search/movie?query=Sicario", resolved.ToString());
    }

    [Fact]
    public void ALeadingSlashWouldDropTheApiVersionSegment()
    {
        // This is the exact mistake — documented so nobody reintroduces it.
        var broken = Resolve("https://api.themoviedb.org/3/", "/search/movie");

        Assert.Equal("https://api.themoviedb.org/search/movie", broken.ToString());
        Assert.DoesNotContain("/3/", broken.ToString());
    }

    [Fact]
    public void AMissingTrailingSlashWouldAlsoDropIt()
    {
        var broken = Resolve("https://api.themoviedb.org/3", "search/movie");

        Assert.DoesNotContain("/3/", broken.ToString());
    }

    [Theory]
    [InlineData("https://api.themoviedb.org/3")]
    [InlineData("https://api.themoviedb.org/3/")]
    public void NormalisingTheBaseUrlMakesEitherSpellingWork(string configured)
    {
        // Mirrors the normalisation applied when the client is registered.
        var normalised = configured.EndsWith('/') ? configured : configured + "/";

        Assert.Equal(
            "https://api.themoviedb.org/3/movie/123",
            Resolve(normalised, "movie/123").ToString());
    }

    /// <summary>
    /// Tautulli broke the same way a month after TMDB. It moved behind a URL base
    /// ("/tautulli") when it got a SWAG subfolder, and "/api/v2" resolved to the host
    /// root. An earlier version of this file asserted Tautulli was safe "because its base
    /// has no path" — true until the day it wasn't.
    /// </summary>
    [Theory]
    [InlineData("http://tautulli:8181", "http://tautulli:8181/api/v2?cmd=get_history")]
    [InlineData("http://tautulli:8181/", "http://tautulli:8181/api/v2?cmd=get_history")]
    [InlineData("http://tautulli:8181/tautulli", "http://tautulli:8181/tautulli/api/v2?cmd=get_history")]
    [InlineData("http://tautulli:8181/tautulli/", "http://tautulli:8181/tautulli/api/v2?cmd=get_history")]
    public void TautulliKeepsItsUrlBase(string configured, string expected)
    {
        // Mirrors the normalisation applied when the client is registered, and the
        // relative path TautulliClient requests.
        var normalised = configured.EndsWith('/') ? configured : configured + "/";

        Assert.Equal(expected, Resolve(normalised, "api/v2?cmd=get_history").ToString());
    }

    [Fact]
    public void ALeadingSlashWouldDropTautullisUrlBase()
    {
        var broken = Resolve("http://tautulli:8181/tautulli/", "/api/v2?cmd=get_history");

        Assert.Equal("http://tautulli:8181/api/v2?cmd=get_history", broken.ToString());
    }

    [Fact]
    public void TraktIsUnaffectedBecauseItsBaseHasNoPath()
    {
        Assert.Equal(
            "https://api.trakt.tv/users/me/history",
            Resolve(new TraktOptions().BaseUrl, "/users/me/history").ToString());
    }

    private static Uri Resolve(string baseUrl, string relative) =>
        new(new Uri(baseUrl), relative);
}
