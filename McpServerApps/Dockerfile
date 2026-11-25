# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS base
USER $APP_UID
WORKDIR /app

FROM base-sdk:latest AS dependencies
COPY ["McpServerApps/McpServerApps.csproj", "McpServerApps/"]
RUN dotnet restore "McpServerApps/McpServerApps.csproj"

FROM dependencies AS publish
ARG BUILD_CONFIGURATION=Release
COPY ["McpServerApps/", "McpServerApps/"]
WORKDIR "/src/McpServerApps"
RUN dotnet publish "./McpServerApps.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false /p:BuildProjectReferences=false --no-restore

FROM base AS final
WORKDIR /app
# Each built app is a git repository, versioned through the git CLI (McpServerApps/Store).
USER root
RUN apt-get update \
 && apt-get install -y --no-install-recommends git \
 && rm -rf /var/lib/apt/lists/*
COPY --from=publish /app/publish .
# A check opens an app's preview in headless Chromium (McpServerApps/Checks), driven by the Playwright
# that Infrastructure carries, so the browser is the one that package expects. Full Chromium, not the
# headless shell: only it honours the flag that makes the preview's plain-http origin a secure context.
# Installed by the package's own driver with the system libraries it needs, somewhere the server's
# user can read.
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
# Compose runs the server as PUID:PGID, which may have no home in the image; Chromium will not start
# without one it can write (its crash handler keeps a database there). Git sets a HOME of its own.
ENV HOME=/tmp
RUN chmod -R +x .playwright/ \
 && .playwright/node/linux-x64/node .playwright/package/cli.js install --with-deps --no-shell chromium \
 && rm -rf /var/lib/apt/lists/* \
 && chmod -R a+rX /ms-playwright
USER $APP_UID
# The server names both its listeners itself (Listeners in appsettings.json); the image's default
# port would only be overridden, with a warning on every start.
ENV ASPNETCORE_HTTP_PORTS=
ENTRYPOINT ["dotnet", "McpServerApps.dll"]
