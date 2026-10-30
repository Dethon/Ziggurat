# syntax=docker/dockerfile:1
# ASP.NET's runtime, not the bare one: Sugary's store, which the run opens through the server's own
# context, comes with the MCP server's ASP.NET dependency.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app

FROM base-sdk:latest AS dependencies
COPY ["McpServerSugary/McpServerSugary.csproj", "McpServerSugary/"]
COPY ["HouseholdMigration/HouseholdMigration.csproj", "HouseholdMigration/"]
RUN dotnet restore "HouseholdMigration/HouseholdMigration.csproj"

FROM dependencies AS publish
ARG BUILD_CONFIGURATION=Release
COPY ["McpServerSugary/", "McpServerSugary/"]
COPY ["HouseholdMigration/", "HouseholdMigration/"]
WORKDIR "/src/HouseholdMigration"
# Sugary is not among the projects base-sdk builds, so it is built here; DuckDB's native library comes
# with it per platform, and a Linux container loads only a Linux one.
RUN dotnet publish "./HouseholdMigration.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false --no-restore \
 && rm -rf /app/publish/runtimes/osx* /app/publish/runtimes/win*

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "HouseholdMigration.dll"]
