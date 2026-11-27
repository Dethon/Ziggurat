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
  && cp "target/$target/release/sandbox-launcher" "target/$target/release/vfs-daemon" "target/$target/release/vfs-action" /out/

FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS base

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

# Her log at /sugary is Parquet, and a command analyses it with Polars or DuckDB without installing
# anything. DuckDB — the Python package and the CLI — is pinned to the version the Sugary server
# queries with (DuckDB.NET.Data.Full in McpServerSugary.csproj), so a statement means the same to the
# query tool and to a command. Into the system's own Python, so they import under the minimal PATH a
# command gets.
# The CLI's checksums are GitHub's own digests for those release assets; a version bump changes all three.
ARG DUCKDB_VERSION=1.5.6
ARG DUCKDB_CLI_SHA256_AMD64=6e89deac1ebbc36eed0291caf8b567b030c7b86ac35998f71854e22b3c5d5e2f
ARG DUCKDB_CLI_SHA256_ARM64=c544e92c9b7c31fc53c2139802cabd8e2d1b2b3e3f933117f31611239c1402db
ARG POLARS_VERSION=1.44.2
ARG TARGETARCH
RUN pip install --no-cache-dir --break-system-packages "duckdb==${DUCKDB_VERSION}" "polars==${POLARS_VERSION}" \
 && arch="${TARGETARCH:-amd64}" \
 && if [ "$arch" = arm64 ]; then sum="$DUCKDB_CLI_SHA256_ARM64"; else sum="$DUCKDB_CLI_SHA256_AMD64"; fi \
 && curl -fsSL -o /tmp/duckdb.zip \
        "https://github.com/duckdb/duckdb/releases/download/v${DUCKDB_VERSION}/duckdb_cli-linux-${arch}.zip" \
 && echo "$sum  /tmp/duckdb.zip" | sha256sum -c - \
 && unzip -q /tmp/duckdb.zip -d /usr/local/bin \
 && rm /tmp/duckdb.zip \
 && chmod 0755 /usr/local/bin/duckdb

# A built app (McpServerApps) is an npm project with a bundler where the household has a sandbox, so
# a command runs npm here and copies the build's dist/ into /apps. Node's official build, pinned to an
# LTS line that still bundles corepack (Node 25 dropped it), into /usr/local so node, npm, npx and
# corepack are on a command's minimal PATH, with corepack's pnpm and yarn shims enabled here because
# enabling writes beside node, where a command cannot (each fetches its pinned manager into the home on
# first use). --no-same-owner because the tarball's files belong to uid 1000, which is commonly PUID:
# kept, the command user would own what every later command runs.
# The checksums are nodejs.org's SHASUMS256.txt for that release (its signature checked against the
# release keys when pinned); a version bump changes all three.
ARG NODE_VERSION=24.22.3
ARG NODE_SHA256_AMD64=061ec3e3062115705b56b51fa9cc330ab50d34b6969387d4e4b1a50c6809acc2
ARG NODE_SHA256_ARM64=261af2af250dfd7e9f25cf151d1b2713a0adf8f10a9ad116c4947f68627affe8
RUN arch="${TARGETARCH:-amd64}" \
 && if [ "$arch" = arm64 ]; then node_arch=arm64; sum="$NODE_SHA256_ARM64"; else node_arch=x64; sum="$NODE_SHA256_AMD64"; fi \
 && curl -fsSL -o /tmp/node.tar.gz \
        "https://nodejs.org/dist/v${NODE_VERSION}/node-v${NODE_VERSION}-linux-${node_arch}.tar.gz" \
 && echo "$sum  /tmp/node.tar.gz" | sha256sum -c - \
 && tar -xzf /tmp/node.tar.gz -C /usr/local --strip-components=1 --no-same-owner \
        --exclude="node-v${NODE_VERSION}-linux-${node_arch}/CHANGELOG.md" \
        --exclude="node-v${NODE_VERSION}-linux-${node_arch}/LICENSE" \
        --exclude="node-v${NODE_VERSION}-linux-${node_arch}/README.md" \
 && rm /tmp/node.tar.gz \
 && corepack enable

# npm's cache is ~/.npm already, but its global prefix is the root-owned /usr/local, where a command's
# `npm install -g` is refused. npm's own builtin config moves it to the home (npm expands ${HOME}, the
# workspace in a command's minimal environment), and a login shell — every exec is `bash -lc` — finds
# what it installed. Appended, so nothing installed there shadows the image's own tools.
RUN printf '%s\n' 'prefix=${HOME}/.npm-global' > /usr/local/lib/node_modules/npm/npmrc \
 && printf '%s\n' \
      '# npm installs globals into the home (/usr/local/lib/node_modules/npm/npmrc).' \
      'PATH="$PATH:$HOME/.npm-global/bin"' \
      > /etc/profile.d/npm-global.sh

# Commands run with the bounding set dropped and no-new-privs, so a setuid binary gains nothing;
# fusermount3 is the one that would matter once the container holds SYS_ADMIN for FUSE, so it loses
# the bit as well rather than relying on one guard alone.
RUN chmod u-s /usr/bin/fusermount3

# Where each exec's daemon mounts the session's other mounts, linked at /<name>. A mount named like
# an empty system directory would lose its link to it, so those go: the media library's mount is
# `media`. rmdir, not rm -r: a directory the image ever fills is a collision to decide, not delete.
RUN mkdir /vfs && rmdir /media

# Those links are what `find /vault` starts from, and find (like du) does not follow a link it is
# given unless asked: without -H it would answer nothing, silently. A login shell — every exec is
# `bash -lc` — gets the reading a person means.
RUN printf '%s\n' \
      '# The session'"'"'s mounts are links into /vfs; start from where they point.' \
      'find() { command find -H "$@"; }' \
      'du() { command du -H "$@"; }' \
      > /etc/profile.d/vfs-links.sh

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
COPY --from=runtime-build /out/sandbox-launcher /out/vfs-daemon /out/vfs-action /usr/local/bin/

# Root on purpose: the launcher owns the switch to PUID/PGID that compose's `user:` used to, starts
# the server as SANDBOX_SERVER_UID and drops every command to PUID. tini reaps what a command
# leaves behind once its exec is over.
ENV SANDBOX_SERVER_UID=1700
ENTRYPOINT ["tini", "--", "sandbox-launcher", "serve", "--", "dotnet", "McpServerSandbox.dll"]
