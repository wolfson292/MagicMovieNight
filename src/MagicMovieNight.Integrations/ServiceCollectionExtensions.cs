using Anthropic;
using MagicMovieNight.Core.Abstractions;
using MagicMovieNight.Integrations.Arr;
using MagicMovieNight.Integrations.Candidates;
using MagicMovieNight.Integrations.Catalog;
using MagicMovieNight.Integrations.Claude;
using MagicMovieNight.Integrations.Importers;
using MagicMovieNight.Integrations.Ingest;
using MagicMovieNight.Integrations.Tautulli;
using MagicMovieNight.Integrations.Tmdb;
using MagicMovieNight.Integrations.Trakt;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MagicMovieNight.Integrations;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Trakt rejects requests with no User-Agent outright — 403, with no hint as to why.
    /// HttpClient sends none by default, which is why this worked from curl and failed
    /// from the app. Sent to every outbound API as ordinary good manners.
    /// </summary>
    internal const string UserAgent =
        "MagicMovieNight/1.0 (+https://github.com/wolfson292/MagicMovieNight)";

    public static IServiceCollection AddMovieNightIntegrations(
        this IServiceCollection services,
        IConfiguration config)
    {
        services.Configure<TraktOptions>(config.GetSection(TraktOptions.Section));
        services.Configure<TautulliOptions>(config.GetSection(TautulliOptions.Section));
        services.Configure<TmdbOptions>(config.GetSection(TmdbOptions.Section));
        services.Configure<SonarrOptions>(config.GetSection(SonarrOptions.Section));
        services.Configure<RadarrOptions>(config.GetSection(RadarrOptions.Section));
        services.Configure<ClaudeOptions>(config.GetSection(ClaudeOptions.Section));
        services.Configure<HouseholdOptions>(config.GetSection(HouseholdOptions.Section));

        // Every outbound call gets retries and a circuit breaker: Trakt rate-limits,
        // TMDB occasionally 502s, and a sync that dies on one bad response is useless
        // when it runs unattended at 4am.
        services.AddHttpClient<TraktAuthService>(ConfigureTrakt).AddStandardResilienceHandler();
        services.AddHttpClient<TraktClient>(ConfigureTrakt).AddStandardResilienceHandler();

        services.AddHttpClient<TautulliClient>((sp, http) =>
        {
            var options = sp.GetRequiredService<IOptions<TautulliOptions>>().Value;
            if (!string.IsNullOrWhiteSpace(options.BaseUrl))
            {
                http.BaseAddress = new Uri(options.BaseUrl);
            }

            http.Timeout = TimeSpan.FromSeconds(60);
        }).AddStandardResilienceHandler();

        services.AddHttpClient<TmdbClient>((sp, http) =>
        {
            var options = sp.GetRequiredService<IOptions<TmdbOptions>>().Value;

            // Belt and braces: a base URL configured without the trailing slash would
            // silently drop the "/3" from every request.
            var baseUrl = options.BaseUrl.EndsWith('/') ? options.BaseUrl : options.BaseUrl + "/";
            http.BaseAddress = new Uri(baseUrl);
            http.DefaultRequestHeaders.Add("Accept", "application/json");
            http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

            if (!string.IsNullOrWhiteSpace(options.ApiToken))
            {
                http.DefaultRequestHeaders.Add("Authorization", $"Bearer {options.ApiToken}");
            }
        }).AddStandardResilienceHandler();

        // Sonarr and Radarr take the API key as a header on every call. The base address
        // keeps its trailing slash for the same reason TMDB's does.
        services.AddHttpClient<SonarrClient>((sp, http) =>
        {
            var options = sp.GetRequiredService<IOptions<SonarrOptions>>().Value;
            ConfigureArr(http, options);
        }).AddStandardResilienceHandler();

        services.AddHttpClient<RadarrClient>((sp, http) =>
        {
            var options = sp.GetRequiredService<IOptions<RadarrOptions>>().Value;
            ConfigureArr(http, options);
        }).AddStandardResilienceHandler();

        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ClaudeOptions>>().Value;

            // An unset key is not an error here — the SDK also resolves credentials
            // from ANTHROPIC_API_KEY and from an `ant auth login` profile.
            return string.IsNullOrWhiteSpace(options.ApiKey)
                ? new AnthropicClient()
                : new AnthropicClient { ApiKey = options.ApiKey };
        });

        services.AddScoped<ICatalogService, CatalogService>();
        services.AddScoped<TasteProfileService>();
        services.AddScoped<IRecommendationEngine, ClaudeRecommendationEngine>();
        services.AddScoped<IngestionService>();
        services.AddScoped<RatingService>();
        services.AddScoped<FeedbackService>();
        services.AddScoped<MediaRequestService>();

        services.AddScoped<IHistorySource>(sp => sp.GetRequiredService<TautulliClient>());
        services.AddScoped<IHistorySource>(sp => sp.GetRequiredService<TraktClient>());

        services.AddScoped<ICandidateSource, LibraryCandidateSource>();
        services.AddScoped<ICandidateSource, TraktCandidateSource>();
        services.AddScoped<ICandidateSource, ContinueWatchingCandidateSource>();

        services.AddScoped<IHistoryImporter, NetflixCsvImporter>();
        services.AddScoped<IHistoryImporter, PrimeVideoCsvImporter>();
        services.AddScoped<IHistoryImporter, HuluCsvImporter>();

        services.AddScoped<IRatingImporter, NetflixRatingsCsvImporter>();

        return services;

        static void ConfigureArr(HttpClient http, ArrOptions options)
        {
            if (string.IsNullOrWhiteSpace(options.BaseUrl))
            {
                return;
            }

            var baseUrl = options.BaseUrl.EndsWith('/') ? options.BaseUrl : options.BaseUrl + "/";
            http.BaseAddress = new Uri(baseUrl);
            http.DefaultRequestHeaders.Add("Accept", "application/json");
            http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

            if (!string.IsNullOrWhiteSpace(options.ApiKey))
            {
                http.DefaultRequestHeaders.Add("X-Api-Key", options.ApiKey);
            }
        }

        static void ConfigureTrakt(IServiceProvider sp, HttpClient http)
        {
            var options = sp.GetRequiredService<IOptions<TraktOptions>>().Value;

            http.BaseAddress = new Uri(options.BaseUrl);
            http.DefaultRequestHeaders.Add("trakt-api-version", "2");
            http.DefaultRequestHeaders.Add("Accept", "application/json");
            http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

            if (!string.IsNullOrWhiteSpace(options.ClientId))
            {
                http.DefaultRequestHeaders.Add("trakt-api-key", options.ClientId);
            }
        }
    }
}
