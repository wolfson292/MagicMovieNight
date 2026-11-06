using System.Net;
using System.Text;
using MagicMovieNight.Integrations;
using MagicMovieNight.Integrations.Tautulli;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MagicMovieNight.Tests;

/// <summary>
/// Drives the real client through the real registration, against a stub server.
///
/// Tautulli history stopped arriving for a month: Tautulli had moved behind a URL base,
/// the client requested "/api/v2" and lost it, every page 404'd, and the client caught
/// that and reported an empty history. These pin both halves — the URL it actually
/// requests, and that a failure surfaces instead of passing for a quiet month.
/// </summary>
public class TautulliClientTests
{
    private const string OneRow = """
        {"response":{"result":"success","message":null,"data":{"recordsFiltered":1,"recordsTotal":1,"data":[
          {"row_id":42,"date":1793900000,"user":"wolfson292","user_id":1,"friendly_name":"Scott",
           "media_type":"movie","title":"Sicario","full_title":"Sicario","grandparent_title":"",
           "year":2015,"percent_complete":98,"player":"Living Room","rating_key":123,
           "parent_media_index":"","media_index":""}]}}}
        """;

    [Theory]
    [InlineData("http://tautulli:8181/tautulli", "http://tautulli:8181/tautulli/api/v2")]
    [InlineData("http://tautulli:8181/tautulli/", "http://tautulli:8181/tautulli/api/v2")]
    [InlineData("http://tautulli:8181", "http://tautulli:8181/api/v2")]
    public async Task RequestsHistoryUnderTheConfiguredUrlBase(string baseUrl, string expectedPath)
    {
        var stub = new StubHandler(HttpStatusCode.OK, OneRow);
        var client = CreateClient(baseUrl, stub);

        var events = await client.PullAsync(DateTimeOffset.UnixEpoch).ToListAsync();

        Assert.Single(events);
        Assert.Equal("Sicario", events[0].Title);
        Assert.StartsWith(expectedPath + "?", stub.Requested.Single().ToString());
    }

    [Fact]
    public async Task ANotFoundIsAFailureNotAnEmptyHistory()
    {
        var client = CreateClient("http://tautulli:8181/tautulli", new StubHandler(HttpStatusCode.NotFound, ""));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.PullAsync(DateTimeOffset.UnixEpoch).ToListAsync().AsTask());
    }

    [Fact]
    public async Task AnApiErrorReportedAsSuccessIsAFailureToo()
    {
        const string wrongKey = """{"response":{"result":"error","message":"Invalid apikey","data":{}}}""";
        var client = CreateClient("http://tautulli:8181/tautulli", new StubHandler(HttpStatusCode.OK, wrongKey));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.PullAsync(DateTimeOffset.UnixEpoch).ToListAsync().AsTask());

        Assert.Contains("Invalid apikey", ex.Message);
    }

    private static TautulliClient CreateClient(string baseUrl, StubHandler stub)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Tautulli:BaseUrl"] = baseUrl,
                ["Tautulli:ApiKey"] = "test-key",
            })
            .Build();

        var services = new ServiceCollection()
            .AddLogging()
            .AddMovieNightIntegrations(config);

        services.AddHttpClient<TautulliClient>().ConfigurePrimaryHttpMessageHandler(() => stub);

        return services.BuildServiceProvider().GetRequiredService<TautulliClient>();
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<Uri> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requested.Add(request.RequestUri!);

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
