# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS base
USER $APP_UID
WORKDIR /app

FROM base-sdk:latest AS dependencies
COPY ["McpChannelTelegram/McpChannelTelegram.csproj", "McpChannelTelegram/"]
RUN dotnet restore "McpChannelTelegram/McpChannelTelegram.csproj"

FROM dependencies AS publish
ARG BUILD_CONFIGURATION=Release
COPY ["McpChannelTelegram/", "McpChannelTelegram/"]
WORKDIR "/src/McpChannelTelegram"
RUN dotnet publish "./McpChannelTelegram.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false /p:BuildProjectReferences=false --no-restore

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "McpChannelTelegram.dll"]
