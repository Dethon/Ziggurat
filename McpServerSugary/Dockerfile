# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app

# Her app (Sugary.Client), built into the web root the server serves it from.
FROM node:22-alpine AS web
WORKDIR /src/Sugary.Client
COPY ["Sugary.Client/package.json", "Sugary.Client/package-lock.json", "./"]
RUN npm ci
COPY ["Sugary.Client/", "./"]
RUN npm run build

FROM base-sdk:latest AS dependencies
COPY ["McpServerSugary/McpServerSugary.csproj", "McpServerSugary/"]
RUN dotnet restore "McpServerSugary/McpServerSugary.csproj"

FROM dependencies AS publish
ARG BUILD_CONFIGURATION=Release
COPY ["McpServerSugary/", "McpServerSugary/"]
WORKDIR "/src/McpServerSugary"
# DuckDB (her log's queries) ships a native library per platform, a few hundred MB together; a
# Linux container loads only a Linux one.
RUN dotnet publish "./McpServerSugary.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false /p:BuildProjectReferences=false --no-restore \
 && rm -rf /app/publish/runtimes/osx* /app/publish/runtimes/win*

FROM base AS final
WORKDIR /app
# A meal's clip is read and made small, and its stills taken, with ffmpeg (McpServerSugary/Clips);
# the sound is dropped here and never leaves the container.
USER root
RUN apt-get update \
 && apt-get install -y --no-install-recommends ffmpeg \
 && rm -rf /var/lib/apt/lists/*
USER $APP_UID
COPY --from=publish /app/publish .
COPY --from=web /src/McpServerSugary/wwwroot ./wwwroot
ENTRYPOINT ["dotnet", "McpServerSugary.dll"]