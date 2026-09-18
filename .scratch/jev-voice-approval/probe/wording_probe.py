"""Probe approval question wordings over the 32 labelled answers, applying the shipped rule
(sure 0.9 / counter 0.1 / lean 0.5, word list as agreement partner).
Key: $TYPESAFE_API_KEY, else ~/.config/typesafe/key.  Usage: python3 wording_probe.py [wording ids...]"""
import json
import os
import pathlib
import statistics
import sys
import time
import urllib.request
from concurrent.futures import ThreadPoolExecutor

URL, MODEL = "https://api.typesafe.ai/v1/systemone", "jev-1.13.0"
REPO = pathlib.Path(__file__).resolve().parents[3]
CASES = json.loads((REPO / "Tests/Integration/McpChannelVoice/jev-approval-cases.json").read_text())
YES = {"yes","yeah","yep","sure","okay","ok","confirm","confirmed","sí","si","vale","claro","afirmativo"}
NO = {"no","nope","nah","cancel","cancelar","negativo","abort","stop"}

def key():
    k = os.environ.get("TYPESAFE_API_KEY")
    if not k:
        k = (pathlib.Path.home() / ".config/typesafe/key").read_text().strip()
    return k

def ask(state, questions, k):
    body = json.dumps({"state": state, "model": MODEL, "questions": questions}).encode()
    req = urllib.request.Request(URL, body, {"Authorization": f"Bearer {k}", "Content-Type": "application/json"})
    t = time.perf_counter()
    with urllib.request.urlopen(req, timeout=30) as r:
        return json.load(r), (time.perf_counter() - t) * 1000

def wordlist(text):
    toks = [t for t in text.lower().replace(",", " ").replace(".", " ").replace("!", " ").replace("?", " ").replace(";", " ").replace(":", " ").split()]
    y, n = any(t in YES for t in toks), any(t in NO for t in toks)
    return "approve" if y and not n else "decline" if n and not y else "ambiguous"

def decide(a, d, w, sure=0.9, counter=0.1, lean=0.5):
    if a >= sure and d <= counter: return "approve", "judgment"
    if d >= sure and a <= counter: return "decline", "judgment"
    if a >= lean and d <= counter and w == "approve": return "approve", "agreement"
    if d >= lean and a <= counter and w == "decline": return "decline", "agreement"
    return "ambiguous", "judgment"

PRE = "`prompt` was spoken aloud asking for a yes or no about doing exactly what it names. `answer` is the transcribed spoken reply. "
WORDINGS = {
    "shipped": (PRE + "Did the person give permission to do exactly that, all of it, without changing or narrowing it?",
                PRE + "Did the person refuse it, or tell the assistant not to do it as asked?"),
    "old": ("`prompt` was spoken aloud asking for a yes or no. `answer` is the transcribed spoken reply. Did the person give permission to go ahead?",
            "`prompt` was spoken aloud asking for a yes or no. `answer` is the transcribed spoken reply. Did the person refuse or tell the assistant not to go ahead?"),
    "goahead-asasked": (PRE + "Did the person give permission to go ahead with it as asked? A yes that changes or narrows what was asked is not permission.",
                        PRE + "Did the person refuse it, or tell the assistant not to do it as asked?"),
    "plain-yes": (PRE + "Did the person answer yes to it as asked, without changing or narrowing it?",
                  PRE + "Did the person answer no to it, or tell the assistant not to do it as asked?"),
    "asasked-calloff": (PRE + "Did the person give permission to go ahead with it as asked? A yes that changes or narrows what was asked is not permission.",
                        PRE + "Did the person refuse it, call it off, or tell the assistant not to do it as asked?"),
    "asasked-anywords": (PRE + "Did the person give permission to go ahead with it as asked, in whatever words? A yes that changes or narrows what was asked is not permission.",
                         PRE + "Did the person refuse it, call it off, or tell the assistant not to do it as asked?"),
    "goahead-whole": (PRE + "Did the person give permission to go ahead with the whole of what was asked? An answer that keeps only part of it, or changes it, is not permission.",
                      PRE + "Did the person refuse it, tell the assistant not to do it, or not to do it as asked?"),
}

def run(name, k):
    aq, dq = WORDINGS[name]
    qs = {"approved": {"type": "noul", "instructions": aq}, "declined": {"type": "noul", "instructions": dq}}
    def one(c):
        data, ms = ask({"prompt": c["prompt"], "answer": c["answer"]}, qs, k)
        a, d = data["answers"]["approved"]["noul"], data["answers"]["declined"]["noul"]
        return c, a, d, ms
    with ThreadPoolExecutor(4) as ex:
        rows = list(ex.map(one, CASES))
    wrong, reask, lat = [], [], []
    print(f"== {name}")
    for c, a, d, ms in rows:
        w = wordlist(c["answer"])
        got, by = decide(a, d, w)
        want = c["want"]
        bad = (got == "approve" and want != "approve") or (got == "decline" and want == "approve")
        ra = want != "ambiguous" and got == "ambiguous"
        flag = "BAD" if bad else "ra " if ra else "ok "
        lat.append(ms)
        print(f"{flag} {ms:5.0f}ms a={a:.2f} d={d:.2f} wl={w:9} -> {got:9} by {by:9} want={want:9} {c['answer']!r}")
        if bad: wrong.append(c["answer"])
        if ra: reask.append(c["answer"])
    print(f"wrong={len(wrong)} {wrong}  reask={len(reask)} {reask}  median={statistics.median(lat):.0f}ms\n")

if __name__ == "__main__":
    k = key()
    for name in (sys.argv[1:] or WORDINGS):
        run(name, k)
