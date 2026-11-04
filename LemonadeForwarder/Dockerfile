# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app

FROM base-sdk:latest AS dependencies
COPY ["LemonadeForwarder/LemonadeForwarder.csproj", "LemonadeForwarder/"]
RUN dotnet restore "LemonadeForwarder/LemonadeForwarder.csproj"

FROM dependencies AS publish
ARG BUILD_CONFIGURATION=Release
COPY ["LemonadeForwarder/", "LemonadeForwarder/"]
WORKDIR "/src/LemonadeForwarder"
RUN dotnet publish "./LemonadeForwarder.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false /p:BuildProjectReferences=false --no-restore

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "LemonadeForwarder.dll"]
