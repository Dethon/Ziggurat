# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS base
USER $APP_UID
WORKDIR /app

FROM base-sdk:latest AS dependencies
COPY ["WebChat/WebChat.csproj", "WebChat/"]
COPY ["WebChat.Client/WebChat.Client.csproj", "WebChat.Client/"]
RUN dotnet restore "WebChat/WebChat.csproj"

FROM dependencies AS publish
ARG BUILD_CONFIGURATION=Release
COPY ["WebChat/", "WebChat/"]
COPY ["WebChat.Client/", "WebChat.Client/"]
WORKDIR "/src/WebChat"
RUN dotnet build "../WebChat.Client/WebChat.Client.csproj" -c $BUILD_CONFIGURATION --no-restore
RUN dotnet publish "./WebChat.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false /p:BuildProjectReferences=false --no-restore

FROM base AS final
WORKDIR /app
ENV ASPNETCORE_ENVIRONMENT=Production
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "WebChat.dll"]
