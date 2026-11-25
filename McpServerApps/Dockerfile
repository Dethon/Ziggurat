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
USER $APP_UID
COPY --from=publish /app/publish .
# The server names both its listeners itself (Listeners in appsettings.json); the image's default
# port would only be overridden, with a warning on every start.
ENV ASPNETCORE_HTTP_PORTS=
ENTRYPOINT ["dotnet", "McpServerApps.dll"]
