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
ADD --checksum=sha256:bf1a75b9930474cf3389ef415e6e5d38ca96fea4a3a00f7e301d080a58ee2239 --chmod=555 \
    https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/3dspeaker_speech_eres2netv2_sv_zh-cn_16k-common.onnx \
    /app/models/speaker-embedding.onnx
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "McpChannelVoice.dll"]
