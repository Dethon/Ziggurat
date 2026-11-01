# A test Wyoming satellite for the smoke kit's voice server (scripts/smoke-stack.sh voice). The hub is
# the Wyoming client: it dials this, sends run-satellite, and this then behaves as nabu-satellite does
# on a wake — run-pipeline carrying {source, wake_rms, wake_score}, then the mic as bare 16 kHz s16 mono
# audio-chunks, in real time — for each question in /voice (q*.wav, in name order), with three seconds
# of silence after each so the hub's silence gate ends the utterance. Whatever the hub plays between
# audio-start and audio-stop is written to /voice/reply-<n>.wav, or reply-<n>-alert.wav for a stream
# the hub marked an alert (a timer or alarm ringing), and every event the hub sends is logged. It
# waits LINGER seconds after the last reply, for anything the questions set going (a timer) to ring.
# Standard library only, so it runs on a bare python image.
import glob, json, os, socket, threading, time, wave

PORT = 10800
CHUNK = 1024  # samples per audio-chunk, as the real satellite sends


def send(conn, typ, data=None, payload=b""):
    header = {"type": typ, "version": "1.5.0"}
    body = json.dumps(data).encode() if data else b""
    if body:
        header["data_length"] = len(body)
    if payload:
        header["payload_length"] = len(payload)
    conn.sendall(json.dumps(header).encode() + b"\n" + body + payload)


def read_exact(f, n):
    buf = b""
    while len(buf) < n:
        part = f.read(n - len(buf))
        if not part:
            raise EOFError
        buf += part
    return buf


def reader(conn, state):
    f = conn.makefile("rb")
    while True:
        try:
            line = f.readline()
            if not line:
                raise EOFError
            header = json.loads(line)
            data = header.get("data") or {}
            if header.get("data_length"):
                data = json.loads(read_exact(f, header["data_length"]))
            payload = read_exact(f, header["payload_length"]) if header.get("payload_length") else b""
        except (EOFError, ConnectionError, ValueError):
            print("hub disconnected", flush=True)
            return
        typ = header["type"]
        if typ != "audio-chunk":
            print("<-", typ, json.dumps(data)[:200], flush=True)
        if typ == "run-satellite":
            state["running"].set()
        elif typ == "audio-start":
            state["audio"], state["rate"], state["alert"] = bytearray(), data.get("rate", 22050), data.get("alert", False)
        elif typ == "audio-chunk" and state.get("audio") is not None:
            state["audio"] += payload
        elif typ == "audio-stop" and state.get("audio") is not None:
            n = state["replies"] = state.get("replies", 0) + 1
            path = f"/voice/reply-{n}{'-alert' if state['alert'] else ''}.wav"
            w = wave.open(path, "wb")
            w.setnchannels(1); w.setsampwidth(2); w.setframerate(state["rate"])
            w.writeframes(bytes(state["audio"])); w.close()
            print(f"saved {path}: {len(state['audio']) / 2 / state['rate']:.1f} s at {state['rate']} Hz", flush=True)
            state["audio"] = None
            state["spoken"].set()


def speak(conn, path):
    w = wave.open(path)
    assert (w.getframerate(), w.getnchannels(), w.getsampwidth()) == (16000, 1, 2), path
    pcm = w.readframes(w.getnframes()) + b"\x00\x00" * 16000 * 3
    send(conn, "run-pipeline", {"source": "wake", "wake_rms": 900, "wake_score": 0.9})
    fmt = {"rate": 16000, "width": 2, "channels": 1}
    for i in range(0, len(pcm), CHUNK * 2):
        send(conn, "audio-chunk", fmt, pcm[i:i + CHUNK * 2])
        time.sleep(CHUNK / 16000)  # a microphone delivers in real time
    print("spoke", path, flush=True)


server = socket.socket()
server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
server.bind(("0.0.0.0", PORT))
server.listen(1)
print("listening on", PORT, flush=True)
conn, addr = server.accept()
print("hub connected from", addr, flush=True)
state = {"running": threading.Event(), "spoken": threading.Event()}
threading.Thread(target=reader, args=(conn, state), daemon=True).start()
state["running"].wait(30)
time.sleep(2)
for question in sorted(glob.glob("/voice/q*.wav")):
    state["spoken"].clear()
    speak(conn, question)
    if not state["spoken"].wait(240):
        print("no reply within 240 s for", question, flush=True)
    time.sleep(3)
print("done", flush=True)
time.sleep(int(os.environ.get("LINGER", "5")))
