#!/bin/sh
# Maps the single STT_BACKEND env var (cpu|gpu, default gpu) onto Lemonade's whisper.cpp
# device selection (config.json — the same mechanism Lemonade's docker docs use for
# llamacpp), sets the decode-quality flags via whispercpp.args (appended verbatim to the
# spawned whisper-server command line) — the Wyoming-era VAD/prompt/beam trio plus the
# short-phrase knobs (non-speech-token suppression, best-of, VAD padding and minimum speech
# duration) — and pre-pulls the model, registering it first when it is a `user.` one. Both
# tiers run the same model, so the hub needs no corresponding setting; everything here is
# container-side only. The NPU/flm tier ignores whispercpp.* entirely.
set -eu

BACKEND="${STT_BACKEND:-gpu}"
# Pre-pull target only. Keep in sync with the hub's Stt__OpenAi__Model if you override it;
# a mismatch just means the wrong model is warmed and the right one downloads lazily.
MODEL="${STT_MODEL:-Whisper-Large-v3-Turbo}"
# Only for a `user.` model, which is in no registry until we put it there: an org/repo:file.bin
# reference to its ggml weights. Lemonade resolves checkpoints against HuggingFace/ModelScope
# only — a local path is not a thing it accepts — so a self-converted model must be published
# before it can be named here.
MODEL_CHECKPOINT="${STT_MODEL_CHECKPOINT:-}"
# Pre-pull target only, same as MODEL. Keep in sync with the agent's Memory:Embedding:Model;
# a mismatch just means the wrong model is warmed and the right one loads on first recall,
# which is the several-second cold start this pre-pull exists to remove.
EMBEDDING_MODEL="${EMBEDDING_MODEL:-Qwen3-Embedding-0.6B-GGUF}"

# ${VAR-default}: unset inherits the tuned default, set-but-empty disables that flag.
# whisper-server's own beam default is -1 (greedy); 5 matches the old wyoming-whisper, and
# best-of 5 matches OpenAI's against whisper.cpp's 2 (it applies on temperature fallback).
# The initial prompt biases spelling/vocabulary toward Castilian assistant turns; it must
# not contain double quotes (it is embedded in config.json as a quoted argument) and must
# stay free of meta-language — the earlier "…p. ej. Valladolid." tail was observed being
# emitted verbatim as a transcript on short, quiet audio. This is only the fallback for
# callers that are not the hub: the hub posts its own per-request prompt
# (Stt.OpenAi.Prompt), which replaces this one for that request.
BEAM_SIZE="${STT_BEAM_SIZE-5}"
BEST_OF="${STT_BEST_OF-5}"
SUPPRESS_NST="${STT_SUPPRESS_NST-1}"
VAD_THRESHOLD="${STT_VAD_THRESHOLD-0.6}"
VAD_SPEECH_PAD_MS="${STT_VAD_SPEECH_PAD_MS-150}"
VAD_MIN_SPEECH_MS="${STT_VAD_MIN_SPEECH_MS-150}"
INITIAL_PROMPT="${STT_INITIAL_PROMPT-Asistente de voz en español de España. Órdenes breves de domótica, temporizadores, listas de la compra, música y preguntas generales.}"

case "$BACKEND" in
  cpu)  WHISPER_BACKEND="cpu" ;;
  gpu)  WHISPER_BACKEND="vulkan" ;;
  npu)  echo "STT_BACKEND selects the whisper.cpp device, whose NPU option is Windows-only. The Linux NPU tier goes through Lemonade's separate 'flm' recipe instead: leave STT_BACKEND on cpu or gpu and apply docker-compose.override.npu.yml with STT_MODEL set to an flm-recipe ASR model." >&2
        exit 1 ;;
  *)    echo "Unknown STT_BACKEND '$BACKEND' (expected cpu|gpu)" >&2; exit 1 ;;
esac

CONFIG_DIR="${LEMONADE_CONFIG_DIR:-$HOME/.cache/lemonade}"
mkdir -p "$CONFIG_DIR"

WHISPER_ARGS=""
if [ -n "$BEAM_SIZE" ]; then
  WHISPER_ARGS="--beam-size $BEAM_SIZE"
fi
if [ -n "$BEST_OF" ]; then
  WHISPER_ARGS="$WHISPER_ARGS --best-of $BEST_OF"
fi
# Non-speech tokens are what the round-1 eval saw come out as "[Música]" and YouTube
# boilerplate on near-unintelligible audio.
if [ -n "$SUPPRESS_NST" ]; then
  WHISPER_ARGS="$WHISPER_ARGS --suppress-nst"
fi
# The \" survive into config.json as JSON escapes, so the prompt reaches whisper-server as
# one quoted argument (lemond's parse_custom_args honors the quotes).
if [ -n "$INITIAL_PROMPT" ]; then
  WHISPER_ARGS="$WHISPER_ARGS --prompt \\\"$INITIAL_PROMPT\\\""
fi

# Silero VAD trims non-speech before the decoder (fewer silence/noise hallucinations);
# threshold 0.6 rejects borderline noise wakes — the 2026-07 gibberish protection carried
# over from wyoming-whisper, same rollback signal (quiet/far speech getting "ignored").
# whisper.cpp doesn't bundle the model, so fetch it once into the cache volume. Fail-open:
# an unreachable HF means this boot runs without VAD, never a crash loop.
VAD_MODEL="$CONFIG_DIR/vad/ggml-silero-v5.1.2.bin"
if [ -n "$VAD_THRESHOLD" ]; then
  if [ ! -s "$VAD_MODEL" ]; then
    mkdir -p "$CONFIG_DIR/vad"
    curl -fsSL --max-time 120 -o "$VAD_MODEL.tmp" \
      "https://huggingface.co/ggml-org/whisper-vad/resolve/main/ggml-silero-v5.1.2.bin" \
      && mv "$VAD_MODEL.tmp" "$VAD_MODEL" \
      || { rm -f "$VAD_MODEL.tmp"; echo "lemonade: VAD model download failed; starting without VAD" >&2; }
  fi
  if [ -s "$VAD_MODEL" ]; then
    WHISPER_ARGS="$WHISPER_ARGS --vad --vad-model $VAD_MODEL --vad-threshold $VAD_THRESHOLD"
    # Inside this branch only: these are VAD arguments, and a boot that fell back to no-VAD
    # must not pass them. whisper.cpp pads a VAD segment by 30 ms, tight enough to clip a
    # leading plosive off a one-word command, and discards speech shorter than 250 ms
    # outright — both are exactly the short-command case.
    if [ -n "$VAD_SPEECH_PAD_MS" ]; then
      WHISPER_ARGS="$WHISPER_ARGS --vad-speech-pad-ms $VAD_SPEECH_PAD_MS"
    fi
    if [ -n "$VAD_MIN_SPEECH_MS" ]; then
      WHISPER_ARGS="$WHISPER_ARGS --vad-min-speech-duration-ms $VAD_MIN_SPEECH_MS"
    fi
  fi
fi
WHISPER_ARGS="${WHISPER_ARGS# }"

# Dedicated STT/TTS container: whispercpp is the only recipe we configure, so a plain
# overwrite is fine (no llamacpp settings to preserve).
cat > "$CONFIG_DIR/config.json" <<EOF
{
  "whispercpp": { "backend": "$WHISPER_BACKEND", "args": "$WHISPER_ARGS" }
}
EOF

# /api/v1/pull doubles as the registration endpoint: a `user.` model must arrive with its recipe
# and checkpoints or Lemonade answers a 400 demanding the very `user.` namespace it was given
# (the same misleading error the FLM tier hits — see docker-compose.override.npu.yml). A built-in
# model is named alone; sending registration fields for one would claim a name we do not own.
case "$MODEL" in
  user.*)
    if [ -z "$MODEL_CHECKPOINT" ]; then
      echo "STT_MODEL '$MODEL' is a user model, so STT_MODEL_CHECKPOINT must name its ggml checkpoint (org/repo:file.bin)" >&2
      exit 1
    fi
    PULL_PAYLOAD="{\"model_name\": \"$MODEL\", \"recipe\": \"whispercpp\", \"checkpoints\": {\"main\": \"$MODEL_CHECKPOINT\"}, \"labels\": [\"transcription\"]}"
    ;;
  *)
    PULL_PAYLOAD="{\"model_name\": \"$MODEL\"}"
    ;;
esac

echo "lemonade: whispercpp.backend=$WHISPER_BACKEND model=$MODEL embedding=$EMBEDDING_MODEL args=$WHISPER_ARGS"
# Echoed before the STT_CONFIG_ONLY seam so the payload is assertable without a server or registry.
echo "lemonade: stt pull payload=$PULL_PAYLOAD"

# Test seam: config-mapping can be verified without starting the server (no GPU, no model pull).
if [ "${STT_CONFIG_ONLY:-0}" = "1" ]; then
  exit 0
fi

# Pre-pull the tier's whisper model and the recall embedding model once the server is up, so
# neither the first utterance pays a download nor the first turn pays a model load; Kokoro
# (TTS) downloads on first use. The embedding model is loaded pinned: Lemonade's eviction
# score favours dropping fast-loading models, which is exactly what a small embedding model
# is. Its loaded-model limit is applied per model type, so this displaces neither whisper nor
# Kokoro. Best-effort by design.
(
  i=0
  while [ "$i" -lt 60 ]; do
    sleep 2
    if curl -fsS "http://127.0.0.1:13305/api/v1/health" >/dev/null 2>&1; then
      curl -fsS -X POST "http://127.0.0.1:13305/api/v1/pull" \
        -H "Content-Type: application/json" \
        -d "$PULL_PAYLOAD" >/dev/null 2>&1 \
        || echo "lemonade: pulling $MODEL failed; it will download on the first utterance" >&2
      curl -fsS -X POST "http://127.0.0.1:13305/api/v1/pull" \
        -H "Content-Type: application/json" \
        -d "{\"model_name\": \"$EMBEDDING_MODEL\"}" >/dev/null 2>&1 \
        || echo "lemonade: pulling $EMBEDDING_MODEL failed; recall will load it on first use" >&2
      # Says so when the pin does not take, rather than leaving a model that looks pinned
      # and is not: an unpinned embedding model can be evicted, and the next turn then pays
      # the several-second load this whole block exists to remove.
      curl -fsS -X POST "http://127.0.0.1:13305/api/v1/load" \
        -H "Content-Type: application/json" \
        -d "{\"model_name\": \"$EMBEDDING_MODEL\", \"pinned\": true}" >/dev/null 2>&1 \
        || echo "lemonade: pinning $EMBEDDING_MODEL failed; it may be evicted" >&2
      exit 0
    fi
    i=$((i + 1))
  done
) &

exec ./lemond --host 0.0.0.0 --port 13305