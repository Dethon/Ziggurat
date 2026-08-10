#!/bin/sh
# On-box smoke test for lemonade (run from DockerCompose/): sh lemonade/smoke.sh [host:port]
# Scriptable slice of the on-box validation checklist: health, incremental PCM streaming, and
# verbose_json quality signals.
# Requires curl and python3 ON THE HOST (python3 only to wrap the synthesized PCM into a WAV —
# swap in any mono 16-bit speech wav at /tmp/lemonade-smoke.wav if python3 is unavailable).
set -eu
HOST="${1:-localhost:13305}"
# Override for the NPU tier: sh lemonade/smoke.sh localhost:13305 whisper-v3-turbo-FLM
MODEL="${2:-${STT_MODEL:-Whisper-Large-v3-Turbo}}"

echo "== health =="
curl -fsS "http://$HOST/api/v1/health" && echo

echo "== speech: streamed pcm; ttfb well under total proves incremental streaming =="
# Synthesize real speech first, then transcribe it: the shipped default config runs Silero VAD,
# which strips non-speech (a synthetic tone) before the decoder, so a tone would yield an empty
# transcript and no quality signals. Kokoro output (24 kHz mono s16le) is the on-box speech source.
curl -fsS -N -o /tmp/lemonade-smoke.pcm \
  -w 'ttfb=%{time_starttransfer}s total=%{time_total}s bytes=%{size_download}\n' \
  -X POST "http://$HOST/v1/audio/speech" \
  -H "Content-Type: application/json" \
  -d '{"model":"kokoro-v1","input":"Hola, esto es una prueba de síntesis de voz en streaming.","voice":"em_santa","speed":1.2,"response_format":"pcm","stream_format":"audio"}'
echo "pcm is 24 kHz mono s16le: 48000 bytes per second of audio"

echo "== transcription: transcribe the synthesized speech; expect JSON with text + segments carrying avg_logprob/no_speech_prob =="
python3 - <<'EOF'
import wave
with open('/tmp/lemonade-smoke.pcm', 'rb') as f:
    pcm = f.read()
w = wave.open('/tmp/lemonade-smoke.wav', 'wb')
w.setnchannels(1); w.setsampwidth(2); w.setframerate(24000)
w.writeframes(pcm)
w.close()
EOF
curl -fsS -X POST "http://$HOST/v1/audio/transcriptions" \
  -F "file=@/tmp/lemonade-smoke.wav" -F "model=$MODEL" \
  -F "response_format=verbose_json" -F "language=es"
echo
