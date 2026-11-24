# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS base
USER $APP_UID
WORKDIR /app

FROM base-sdk:latest AS dependencies
COPY ["McpChannelVoice/McpChannelVoice.csproj", "McpChannelVoice/"]
RUN dotnet restore "McpChannelVoice/McpChannelVoice.csproj"

FROM dependencies AS publish
ARG BUILD_CONFIGURATION=Release
COPY ["McpChannelVoice/", "McpChannelVoice/"]
WORKDIR "/src/McpChannelVoice"
RUN dotnet publish "./McpChannelVoice.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false /p:BuildProjectReferences=false --no-restore

FROM base AS final
WORKDIR /app
# Remote-URL ADD defaults to mode 600 — chmod so any PUID/PGID runtime user can read it.
# 555 not 444: --chmod also applies to the auto-created /app/models dir, which needs x.
# Upstream re-exported this asset on 2026-11-19 (sherpa-onnx #4239): different bytes, the same
# embeddings bit for bit on identical input, so enrolled voices still match.
ADD --checksum=sha256:eda0bf86422a9725a3523d5a1d3d75dedf340664e0ba65cfc1c7b2a78b6cf0b5 --chmod=555 \
    https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/3dspeaker_speech_eres2netv2_sv_zh-cn_16k-common.onnx \
    /app/models/speaker-embedding.onnx
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "McpChannelVoice.dll"]
