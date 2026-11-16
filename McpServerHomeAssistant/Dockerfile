# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS base
USER $APP_UID
WORKDIR /app

FROM base-sdk:latest AS dependencies
COPY ["McpServerHomeAssistant/McpServerHomeAssistant.csproj", "McpServerHomeAssistant/"]
RUN dotnet restore "McpServerHomeAssistant/McpServerHomeAssistant.csproj"

FROM dependencies AS publish
ARG BUILD_CONFIGURATION=Release
COPY ["McpServerHomeAssistant/", "McpServerHomeAssistant/"]
WORKDIR "/src/McpServerHomeAssistant"
RUN dotnet publish "./McpServerHomeAssistant.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false /p:BuildProjectReferences=false --no-restore

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "McpServerHomeAssistant.dll"]
