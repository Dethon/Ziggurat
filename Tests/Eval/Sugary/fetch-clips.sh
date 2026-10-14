#!/usr/bin/env bash
# Makes the clips and stills the Sugary tier's video cases name (video-cases.json), from Nutrition5k's
# side-angle videos of the 14 `n5k` dishes, the way the standalone Sugary benchmark made the clips its
# video figures were measured on (2026-10-12: Gemini 3.8 Flash, 8.3 g from these clips against 11.2 g
# from the overhead photo). Free public data (CC BY 4.0); nothing here calls a model.
#
#   Tests/Eval/Sugary/fetch-clips.sh
#
# Needs curl, ffmpeg and ffprobe. Writes Tests/Eval/Sugary/clips/ (gitignored, about 30 MB) and keeps
# the raw camera files (about 300 MB) in $N5K_CACHE, default ~/.cache/ziggurat/nutrition5k, so a second
# run downloads nothing. Running it again remakes the clips and stills from the cache.
#
# Each dish was filmed by up to four fixed cameras (A to D) circling the plate on a rig, a few seconds
# each. For every dish, as the standalone did:
#   - the clip is every camera's frames played at 4 a second, one camera after another, at 1280x720,
#     then made 960x540 (H.264, no sound, about 35 s);
#   - two stills per camera, the frames at a quarter and three quarters of it, 1568 px wide — the
#     standalone's "stills8" (Gemini 9.2 g, Haiku 16.6 g from them).
# One dish (1558641320) has only cameras B and D, so its clip is shorter and it has four stills.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
out="$here/clips"
cache="${N5K_CACHE:-${XDG_CACHE_HOME:-$HOME/.cache}/ziggurat/nutrition5k}"
base="https://storage.googleapis.com/nutrition5k_dataset/nutrition5k_dataset/imagery/side_angles"

for tool in curl ffmpeg ffprobe; do
  command -v "$tool" >/dev/null || { echo "fetch-clips: $tool is not installed" >&2; exit 1; }
done

mkdir -p "$out" "$cache"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

dishes=$(grep -o '"clips/n5k-[0-9]*\.mp4"' "$here/video-cases.json" | grep -o '[0-9]\{10\}')

for id in $dishes; do
  inputs=()
  filters=""
  joined=""
  n=0
  for camera in A B C D; do
    raw="$cache/${id}_$camera.h264"
    if [ ! -s "$raw" ]; then
      if ! curl -sSf -m 600 -o "$raw.part" "$base/dish_$id/camera_$camera.h264" 2>/dev/null; then
        rm -f "$raw.part"
        continue
      fi
      mv "$raw.part" "$raw"
    fi

    frames=$(ffprobe -v error -count_frames -select_streams v:0 -show_entries stream=nb_read_frames -of csv=p=0 "$raw")
    for half in 1 2; do
      at=$((half == 1 ? frames / 4 : frames * 3 / 4))
      ffmpeg -v error -y -i "$raw" -vf "select='eq(n,$at)',scale=1568:-2" -frames:v 1 -q:v 3 \
        "$out/n5k-$id-$camera$half.jpg"
    done

    inputs+=(-framerate 4 -i "$raw")
    filters="$filters[$n:v]scale=1280:720,setsar=1[v$n];"
    joined="$joined[v$n]"
    n=$((n + 1))
  done

  if [ "$n" -eq 0 ]; then
    echo "fetch-clips: dish $id has no side-angle video" >&2
    exit 1
  fi

  ffmpeg -v error -y "${inputs[@]}" -filter_complex "${filters}${joined}concat=n=$n:v=1[o]" -map "[o]" \
    -r 4 -c:v libx264 -pix_fmt yuv420p -crf 26 -movflags +faststart "$work/$id.mp4"
  ffmpeg -v error -y -i "$work/$id.mp4" -vf scale=960:540 -c:v libx264 -pix_fmt yuv420p -crf 29 \
    -preset slow -movflags +faststart "$out/n5k-$id.mp4"

  seconds=$(ffprobe -v error -show_entries format=duration -of csv=p=0 "$out/n5k-$id.mp4")
  echo "n5k $id: $n cameras, ${seconds%.*} s"
done
