# syntax=docker/dockerfile:1

# Stage 1: Restore dependencies
FROM base-sdk:latest AS dependencies
COPY ["McpServerWebSearch/McpServerWebSearch.csproj", "McpServerWebSearch/"]
RUN dotnet restore "McpServerWebSearch/McpServerWebSearch.csproj"

# Stage 2: Publish
FROM dependencies AS publish
ARG BUILD_CONFIGURATION=Release
COPY ["McpServerWebSearch/", "McpServerWebSearch/"]
WORKDIR "/src/McpServerWebSearch"
RUN dotnet publish "./McpServerWebSearch.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false /p:BuildProjectReferences=false --no-restore

# Stage 3: Final runtime image (browser runs in Camoufox sidecar)
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=publish /app/publish .
RUN chmod -R +x .playwright/
# The server names both its listeners itself (Listeners in appsettings.json); the image's default
# port would only be overridden, with a warning on every start.
ENV ASPNETCORE_HTTP_PORTS=
USER $APP_UID
ENTRYPOINT ["dotnet", "McpServerWebSearch.dll"]
