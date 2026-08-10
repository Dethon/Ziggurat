"""Stdlib-only Lemonade STT worker, run inside the compose network via docker run.

Posts each wav to Lemonade's OpenAI-compatible /v1/audio/transcriptions endpoint with
response_format=verbose_json and records the transcript plus a score derived from the
segment avg_logprob (score = exp(mean avg_logprob)), matching the _medium backend so the
score/WER columns stay comparable across backends. This is the prod-parity path after the
Wyoming STT containers were retired: the hub transcribes through this same endpoint.
"""
import argparse
import json
import math
import urllib.request
import uuid
from typing import Any


def _post_transcription(host: str, port: int, model: str, wav_path: str,
                        prompt: str | None = None) -> dict[str, Any]:
    with open(wav_path, "rb") as fh:
        audio = fh.read()
    boundary = uuid.uuid4().hex
    fields = [("model", model), ("response_format", "verbose_json"), ("language", "es")]
    # Lemonade forwards this to whisper-server, where it replaces the server's own --prompt for
    # this request. Omitted entirely when unset, so an unprompted run keeps the container default
    # and the two configurations stay comparable on identical audio.
    if prompt:
        fields.append(("prompt", prompt))
    parts: list[bytes] = []
    for name, value in fields:
        parts.append(
            f'--{boundary}\r\nContent-Disposition: form-data; name="{name}"\r\n\r\n{value}\r\n'.encode()
        )
    parts.append(
        f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="audio.wav"\r\n'
        "Content-Type: audio/wav\r\n\r\n".encode() + audio + b"\r\n"
    )
    parts.append(f"--{boundary}--\r\n".encode())
    req = urllib.request.Request(
        f"http://{host}:{port}/v1/audio/transcriptions",
        data=b"".join(parts),
        headers={"Content-Type": f"multipart/form-data; boundary={boundary}"},
        method="POST",
    )
    with urllib.request.urlopen(req, timeout=300) as resp:
        return json.loads(resp.read())


def _score(payload: dict[str, Any]) -> float | None:
    logprobs: list[float] = [
        s["avg_logprob"]
        for s in (payload.get("segments") or [])
        if s.get("avg_logprob") is not None
    ]
    return math.exp(sum(logprobs) / len(logprobs)) if logprobs else None


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default="lemonade")
    ap.add_argument("--port", type=int, default=13305)
    ap.add_argument("--model", default="Whisper-Large-v3-Turbo")
    ap.add_argument("--prompt", default=None, help="whisper initial prompt posted with each clip")
    ap.add_argument("--manifest", required=True, help="jsonl of {'wav': path}")
    ap.add_argument("--out", required=True)
    args = ap.parse_args()
    with open(args.manifest, encoding="utf-8") as fin, open(args.out, "w", encoding="utf-8") as fout:
        for line in fin:
            wav: str = json.loads(line)["wav"]
            payload = _post_transcription(args.host, args.port, args.model, wav, args.prompt)
            text: str = payload.get("text", "")
            row = {"wav": wav, "text": text, "score": _score(payload)}
            fout.write(json.dumps(row, ensure_ascii=False) + "\n")
            # Flush per row: mirrors _medium's per-row flush so a mid-batch docker kill (or a
            # non-zero exit the caller merges around) still leaves completed rows on disk.
            fout.flush()
            print(wav, "->", text[:60], flush=True)


if __name__ == "__main__":
    main()
