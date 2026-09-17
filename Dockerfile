# Build on the SDK image, ship on the runtime image — the final layer carries no
# compiler, no source, and no NuGet cache.
# Pinned deliberately. The floating `sdk:10.0` tag moved from 10.0.202 to 10.0.401 and
# 10.0.401 does not emit wwwroot/_framework/blazor.web.js for this project, which shipped
# an image whose UI had no interactivity at all. Bump this on purpose, never by accident,
# and let the assertion below prove the result before it ever reaches a registry.
FROM mcr.microsoft.com/dotnet/sdk:10.0.202 AS build
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

# Fail the build rather than ship a UI that silently cannot do anything. Without
# blazor.web.js every button in the app is inert, and the pages still render perfectly,
# so nothing short of clicking something reveals the problem.
RUN test -f /app/wwwroot/_framework/blazor.web.js \
 || (echo "BUILD FAILED: wwwroot/_framework/blazor.web.js is missing from the publish output." \
  && echo "The app would start and render, but no button would work." \
  && echo "SDK in use: $(dotnet --version)" \
  && exit 1) \
 && grep -q blazor.web.js /app/MagicMovieNight.Web.staticwebassets.endpoints.json \
 || (echo "BUILD FAILED: blazor.web.js is not routed in the static asset manifest." \
  && echo "MapStaticAssets would return 404 for it at runtime." \
  && exit 1)

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
