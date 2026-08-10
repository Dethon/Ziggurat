# Lemonade Server: OpenAI-compatible STT (whisper.cpp on cpu or vulkan) + TTS (Kokoro).
# Replaces wyoming-whisper + wyoming-piper. The entrypoint maps STT_BACKEND (cpu|gpu) to
# the whisper.cpp device; both tiers run the same model (Whisper-Large-v3-Turbo by default).
# Pin: v11.0.0 (2026-07-15, current latest); bump deliberately, not via :latest.
FROM ghcr.io/lemonade-sdk/lemonade-server:v11.0.0

# The stock image has the Vulkan loader but no ICD, so ggml-vulkan would enumerate zero
# devices and fall back to CPU *without erroring*. RADV is what actually drives the 890M.
#
# libreadline8t64 is what FLM's `flm-real` engine links against (the NPU tier, see
# docker-compose.override.npu.yml). Without it the engine dies on start with
# "libreadline.so.8: cannot open shared object file", so `flm list --json` fails,
# flm_discover_models() registers ZERO FLM models, and the NPU whisper model then resolves
# as an unknown model — surfacing as a misleading HTTP 400 demanding a `user.` namespace
# prefix rather than anything mentioning FLM. Confirmed on hardware 2026-07-24.
USER root
RUN apt-get update \
    && apt-get install -y --no-install-recommends mesa-vulkan-drivers libreadline8t64 \
    && rm -rf /var/lib/apt/lists/*
USER lemonade

COPY --chmod=755 entrypoint.sh /entrypoint.sh

ENTRYPOINT ["/entrypoint.sh"]