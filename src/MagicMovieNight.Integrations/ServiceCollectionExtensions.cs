using Anthropic;
using MagicMovieNight.Core.Abstractions;
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
    public static IServiceCollection AddMovieNightIntegrations(
        this IServiceCollection services,
        IConfiguration config)
    {
        services.Configure<TraktOptions>(config.GetSection(TraktOptions.Section));
        services.Configure<TautulliOptions>(config.GetSection(TautulliOptions.Section));
        services.Configure<TmdbOptions>(config.GetSection(TmdbOptions.Section));
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

            if (!string.IsNullOrWhiteSpace(options.ApiToken))
            {
                http.DefaultRequestHeaders.Add("Authorization", $"Bearer {options.ApiToken}");
            }
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

        static void ConfigureTrakt(IServiceProvider sp, HttpClient http)
        {
            var options = sp.GetRequiredService<IOptions<TraktOptions>>().Value;

            http.BaseAddress = new Uri(options.BaseUrl);
            http.DefaultRequestHeaders.Add("trakt-api-version", "2");
            http.DefaultRequestHeaders.Add("Accept", "application/json");

            if (!string.IsNullOrWhiteSpace(options.ClientId))
            {
                http.DefaultRequestHeaders.Add("trakt-api-key", options.ClientId);
            }
        }
    }
}
