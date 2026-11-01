# The smoke stack's voice check, on the host beside it (scripts/smoke-stack.sh voice), speaking and
# hearing through the stack's own Lemonade (LEMONADE_URL, default its published 127.0.0.1:13305):
#
#   voice.py say <text> <out.wav>   Kokoro speaks the text; written as 16 kHz s16 mono, the mic format
#   voice.py check <dir>            Whisper reads every reply the test satellite recorded in <dir>, and
#                                   the check passes when the first answer says 56 and an alert rang
#
# Standard library only.
import glob, json, os, struct, sys, urllib.request, uuid, wave

LEMONADE = os.environ.get("LEMONADE_URL", "http://127.0.0.1:13305").rstrip("/") + "/api/v1"


def resample(samples, rate, target):
    m = int(len(samples) * target / rate)
    return [samples[min(int(i * rate / target), len(samples) - 1)] for i in range(m)]


def write_wav(path, samples, rate):
    w = wave.open(path, "wb")
    w.setnchannels(1); w.setsampwidth(2); w.setframerate(rate)
    w.writeframes(struct.pack("<%dh" % len(samples), *samples)); w.close()


def say(text, out):
    body = json.dumps({"model": "kokoro-v1", "voice": "em_santa", "input": text, "response_format": "pcm"}).encode()
    request = urllib.request.Request(f"{LEMONADE}/audio/speech", data=body, headers={"Content-Type": "application/json"})
    pcm = urllib.request.urlopen(request, timeout=120).read()  # 24 kHz s16 mono
    samples = struct.unpack("<%dh" % (len(pcm) // 2), pcm[: len(pcm) // 2 * 2])
    write_wav(out, resample(samples, 24000, 16000), 16000)


def hear(path):
    w = wave.open(path)
    n = w.getnframes()
    samples = resample(struct.unpack("<%dh" % n, w.readframes(n)), w.getframerate(), 16000)
    raw = path + ".16k.wav"
    write_wav(raw, samples, 16000)
    boundary = uuid.uuid4().hex
    fields = [("model", "Whisper-Large-v3-Turbo"), ("language", "es")]
    body = b"".join(f'--{boundary}\r\nContent-Disposition: form-data; name="{k}"\r\n\r\n{v}\r\n'.encode() for k, v in fields)
    body += f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="a.wav"\r\nContent-Type: audio/wav\r\n\r\n'.encode()
    body += open(raw, "rb").read() + f"\r\n--{boundary}--\r\n".encode()
    os.remove(raw)
    request = urllib.request.Request(f"{LEMONADE}/audio/transcriptions", data=body,
                                     headers={"Content-Type": f"multipart/form-data; boundary={boundary}"})
    return json.loads(urllib.request.urlopen(request, timeout=300).read()).get("text", "").strip()


def check(directory):
    replies = sorted(glob.glob(f"{directory}/reply-*.wav"), key=lambda p: int(p.split("reply-")[1].split("-")[0].split(".")[0]))
    heard = []
    for path in replies:
        w = wave.open(path)
        seconds = w.getnframes() / w.getframerate()
        text = hear(path) if seconds >= 0.5 else ""  # the shorter ones are the follow-up listening cue
        heard.append((os.path.basename(path), text))
        print(f"{os.path.basename(path)} ({seconds:.1f} s): {text}")
    answers = [text for name, text in heard if text and "alert" not in name]
    answered = bool(answers) and any(k in answers[0].lower() for k in ("56", "cincuenta y seis"))
    rang = any("alert" in name for name, _ in heard)
    print(f"answered 7x8: {'yes' if answered else 'no'}; the timer rang on the satellite: {'yes' if rang else 'no'}")
    return 0 if answered and rang else 1


if __name__ == "__main__":
    if sys.argv[1:2] == ["say"]:
        say(sys.argv[2], sys.argv[3])
    elif sys.argv[1:2] == ["check"]:
        sys.exit(check(sys.argv[2]))
    else:
        sys.exit("usage: voice.py say <text> <out.wav> | check <dir>")
