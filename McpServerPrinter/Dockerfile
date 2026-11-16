# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS base
USER $APP_UID
WORKDIR /app

FROM base-sdk:latest AS dependencies
COPY ["McpServerPrinter/McpServerPrinter.csproj", "McpServerPrinter/"]
RUN dotnet restore "McpServerPrinter/McpServerPrinter.csproj"

FROM dependencies AS publish
ARG BUILD_CONFIGURATION=Release
COPY ["McpServerPrinter/", "McpServerPrinter/"]
WORKDIR "/src/McpServerPrinter"
RUN dotnet publish "./McpServerPrinter.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false /p:BuildProjectReferences=false --no-restore

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
# Create the spool mount point owned by the non-root app user so that, on first run,
# Docker initializes the (empty) printer-spool named volume with app ownership. Otherwise
# the named volume mounts root-owned and the app user (APP_UID=1654) cannot write to it,
# causing "Access denied" on every spooled document.
USER root
RUN mkdir -p /spool && chown $APP_UID:$APP_UID /spool
USER $APP_UID
ENTRYPOINT ["dotnet", "McpServerPrinter.dll"]