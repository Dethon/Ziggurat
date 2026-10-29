# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app

# FROM base-sdk for the SDK band every image pins; the service references none of the projects
# base-sdk builds.
FROM base-sdk:latest AS dependencies
COPY ["Accounts/Accounts.csproj", "Accounts/"]
RUN dotnet restore "Accounts/Accounts.csproj"

FROM dependencies AS publish
ARG BUILD_CONFIGURATION=Release
COPY ["Accounts/", "Accounts/"]
WORKDIR "/src/Accounts"
RUN dotnet publish "./Accounts.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false --no-restore \
 && rm -rf /app/publish/runtimes/osx* /app/publish/runtimes/win*

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Accounts.dll"]
