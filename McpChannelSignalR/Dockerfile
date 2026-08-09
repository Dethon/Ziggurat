# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app

FROM base-sdk:latest AS dependencies
COPY ["McpChannelSignalR/McpChannelSignalR.csproj", "McpChannelSignalR/"]
RUN dotnet restore "McpChannelSignalR/McpChannelSignalR.csproj"

FROM dependencies AS publish
ARG BUILD_CONFIGURATION=Release
COPY ["McpChannelSignalR/", "McpChannelSignalR/"]
WORKDIR "/src/McpChannelSignalR"
RUN dotnet publish "./McpChannelSignalR.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false /p:BuildProjectReferences=false --no-restore

FROM base AS final
# The upload store mounts at /data/uploads. The directory must exist in the image owned by the
# app user: a named volume copies the mount point's ownership on first use, and a root-owned
# mount point leaves the non-root server unable to write a single upload.
USER root
RUN mkdir -p /data/uploads && chown $APP_UID:$APP_UID /data/uploads
USER $APP_UID
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "McpChannelSignalR.dll"]
