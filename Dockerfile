# Build on the SDK image, ship on the runtime image — the final layer carries no
# compiler, no source, and no NuGet cache.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore before copying the rest so a source-only change does not invalidate the
# (slow) restore layer.
COPY MagicMovieNight.slnx ./
COPY src/MagicMovieNight.Core/*.csproj src/MagicMovieNight.Core/
COPY src/MagicMovieNight.Data/*.csproj src/MagicMovieNight.Data/
COPY src/MagicMovieNight.Integrations/*.csproj src/MagicMovieNight.Integrations/
COPY src/MagicMovieNight.Web/*.csproj src/MagicMovieNight.Web/
COPY tests/MagicMovieNight.Tests/*.csproj tests/MagicMovieNight.Tests/
RUN dotnet restore src/MagicMovieNight.Web/MagicMovieNight.Web.csproj

COPY . .
RUN dotnet publish src/MagicMovieNight.Web/MagicMovieNight.Web.csproj \
    -c Release -o /app --no-restore

# TEMPORARY DIAGNOSTIC: blazor.web.js is missing from the published image although a
# local publish on SDK 10.0.202 includes it. Print what this SDK actually produced.
RUN echo "=== SDK VERSION: $(dotnet --version) ===" \
 && echo "=== /app/wwwroot ===" && ls -la /app/wwwroot \
 && echo "=== /app/wwwroot/_framework ===" && (ls -la /app/wwwroot/_framework || echo "MISSING") \
 && echo "=== blazor.web.js in endpoints manifest? ===" \
 && (grep -c blazor.web.js /app/MagicMovieNight.Web.staticwebassets.endpoints.json || echo "0 matches")

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# curl is only here for the container healthcheck — the runtime image ships without
# any HTTP client, and a healthcheck that cannot make a request is worse than none.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

# Trakt tokens live here; mount it as a volume so pairing survives a redeploy.
RUN mkdir -p /config

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_gcServer=0

EXPOSE 8080

COPY --from=build /app ./

# /health is mapped by the app and checks database connectivity, so this reports
# "unhealthy" for the failure that actually matters.
HEALTHCHECK --interval=30s --timeout=5s --start-period=40s --retries=3 \
    CMD curl -fsS http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "MagicMovieNight.Web.dll"]
