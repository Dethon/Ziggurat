# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base

# Install bash, python3, pip, common CLI tooling
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
        bash \
        python3 \
        python3-pip \
        python3-venv \
        curl \
        ca-certificates \
        git \
        jq \
        unzip \
        gzip \
        ffmpeg \
 && rm -rf /var/lib/apt/lists/*

# The mount point, spelled inside the container. The sandbox mount *is* the container root, so this
# states a truth that already held — and it is what makes the one spelling the filesystem prompt
# teaches work in a command as well as in an argument. Without it the agent has to know the prefix
# is required in the path argument and forbidden three characters later in the command string.
# Keep the target in step with the sandbox filesystem's mount point; a test pins the two together.
RUN ln -s / /sandbox

WORKDIR /app

FROM base-sdk:latest AS dependencies
COPY ["McpServerSandbox/McpServerSandbox.csproj", "McpServerSandbox/"]
RUN dotnet restore "McpServerSandbox/McpServerSandbox.csproj"

FROM dependencies AS publish
ARG BUILD_CONFIGURATION=Release
COPY ["McpServerSandbox/", "McpServerSandbox/"]
WORKDIR "/src/McpServerSandbox"
RUN dotnet publish "./McpServerSandbox.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false /p:BuildProjectReferences=false --no-restore

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

ENTRYPOINT ["dotnet", "McpServerSandbox.dll"]
