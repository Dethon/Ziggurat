# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS base
USER $APP_UID
WORKDIR /app

FROM base-sdk:latest AS dependencies
COPY ["Agent/Agent.csproj", "Agent/"]
RUN dotnet restore "Agent/Agent.csproj"

FROM dependencies AS publish
ARG BUILD_CONFIGURATION=Release
COPY ["Agent/", "Agent/"]
WORKDIR "/src/Agent"
RUN dotnet publish "./Agent.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false /p:BuildProjectReferences=false --no-restore

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Agent.dll"]
