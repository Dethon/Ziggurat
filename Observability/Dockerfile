# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS base
USER $APP_UID
WORKDIR /app

FROM base-sdk:latest AS dependencies
COPY ["Dashboard.Client/Dashboard.Client.csproj", "Dashboard.Client/"]
COPY ["Observability/Observability.csproj", "Observability/"]
RUN dotnet restore "Observability/Observability.csproj"

FROM dependencies AS publish
ARG BUILD_CONFIGURATION=Release
COPY ["Dashboard.Client/", "Dashboard.Client/"]
COPY ["Observability/", "Observability/"]
WORKDIR "/src/Observability"
RUN dotnet build "../Dashboard.Client/Dashboard.Client.csproj" -c $BUILD_CONFIGURATION --no-restore
RUN dotnet publish "./Observability.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false /p:BuildProjectReferences=false --no-restore

FROM base AS final
WORKDIR /app
ENV ASPNETCORE_ENVIRONMENT=Production
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Observability.dll"]
