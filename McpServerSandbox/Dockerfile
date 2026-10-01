# syntax=docker/dockerfile:1

# The root launcher and the per-exec FUSE daemon (sandbox-runtime/): the container's entrypoint,
# which starts the server as a uid of its own and runs each command as PUID with the session's other
# mounts at /vfs. Static, so nothing in the runtime image can break them.
FROM rust:1.97.1-slim AS runtime-build
WORKDIR /src/sandbox-runtime
COPY ["sandbox-runtime/", "./"]
RUN target="$(uname -m)-unknown-linux-musl" \
 && rustup target add "$target" \
 && cargo build --release --locked --target "$target" \
 && mkdir -p /out \
 && cp "target/$target/release/sandbox-launcher" "target/$target/release/vfs-daemon" /out/

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
        fuse3 \
        tini \
 && rm -rf /var/lib/apt/lists/*

# Commands run with the bounding set dropped and no-new-privs, so a setuid binary gains nothing;
# fusermount3 is the one that would matter once the container holds SYS_ADMIN for FUSE, so it loses
# the bit as well rather than relying on one guard alone.
RUN chmod u-s /usr/bin/fusermount3

# Where each exec's daemon mounts the session's other mounts, linked at /<name>. A mount named like
# an empty system directory would lose its link to it, so those go: the media library's mount is
# `media`. rmdir, not rm -r: a directory the image ever fills is a collision to decide, not delete.
RUN mkdir /vfs && rmdir /media

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
COPY --from=runtime-build /out/sandbox-launcher /out/vfs-daemon /usr/local/bin/

# Root on purpose: the launcher owns the switch to PUID/PGID that compose's `user:` used to, starts
# the server as SANDBOX_SERVER_UID and drops every command to PUID. tini reaps what a command
# leaves behind once its exec is over.
ENV SANDBOX_SERVER_UID=1700
ENTRYPOINT ["tini", "--", "sandbox-launcher", "serve", "--", "dotnet", "McpServerSandbox.dll"]
