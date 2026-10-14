# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app

FROM base-sdk:latest AS dependencies
COPY ["McpServerSugary/McpServerSugary.csproj", "McpServerSugary/"]
RUN dotnet restore "McpServerSugary/McpServerSugary.csproj"

FROM dependencies AS publish
ARG BUILD_CONFIGURATION=Release
COPY ["McpServerSugary/", "McpServerSugary/"]
WORKDIR "/src/McpServerSugary"
RUN dotnet publish "./McpServerSugary.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false /p:BuildProjectReferences=false --no-restore

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "McpServerSugary.dll"]