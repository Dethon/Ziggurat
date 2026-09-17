#!/usr/bin/env python3
"""Probe Jev on ziggurat's real skill descriptions and approval phrases, Spanish and English.

Key: $TYPESAFE_API_KEY, else ~/.config/typesafe/key. Reads skill descriptions from Domain/Prompts.
"""
import json, os, re, statistics, sys, time, urllib.request, pathlib

REPO = pathlib.Path("/home/dethon/repos/ziggurat")
URL = "https://api.typesafe.ai/v1/systemone"
MODEL = os.environ.get("JEV_MODEL", "jev-latest")


def key():
    k = os.environ.get("TYPESAFE_API_KEY")
    if k:
        return k.strip()
    p = pathlib.Path.home() / ".config/typesafe/key"
    if p.exists():
        return p.read_text().strip()
    sys.exit("no key: export TYPESAFE_API_KEY or write ~/.config/typesafe/key")


def descriptions():
    out = {}
    for f in sorted((REPO / "Domain/Prompts").glob("*Skill.cs")):
        text = f.read_text()
        name = re.search(r'public const string Name\s*=\s*"([^"]+)"', text)
        desc = re.search(r'public const string Description\s*=\s*\n?\s*"((?:[^"\\]|\\.)*)"', text)
        if name and desc:
            out[name.group(1)] = desc.group(1).replace('\\"', '"')
    return out


def ask(state, questions, k):
    body = json.dumps({"state": state, "model": MODEL, "questions": questions}).encode()
    req = urllib.request.Request(URL, body, {
        "Authorization": f"Bearer {k}", "Content-Type": "application/json"})
    t = time.perf_counter()
    with urllib.request.urlopen(req, timeout=30) as r:
        data = json.load(r)
    return data, (time.perf_counter() - t) * 1000


# (request, expected skill names; empty set = none)
SKILL_CASES = [
    ("enciende la luz del salón", {"home-assistant"}),
    ("turn on the AC in the bedroom", {"home-assistant"}),
    ("¿a cuánto está el termostato?", {"home-assistant"}),
    ("¿cómo ha tenido la glucosa esta noche?", {"home-assistant"}),
    ("despiértame mañana a las siete", {"home-assistant"}),
    ("pon la radio en la cocina", {"home-assistant"}),
    ("sube el volumen de la música", {"home-assistant"}),
    ("cancela la alarma del dentista", {"home-assistant"}),
    ("pon un temporizador de ocho minutos", {"countdown-timers"}),
    ("recuérdame en veinte minutos que saque la pizza", {"countdown-timers"}),
    ("para la alarma", {"countdown-timers"}),
    ("how long is left on the timer?", {"countdown-timers"}),
    ("avísame cuando el azúcar pase de 180", {"home-watches"}),
    ("dime cuando termine la lavadora", {"home-watches"}),
    ("close the blinds when it gets hot", {"home-watches"}),
    ("apaga el aire dentro de una hora", {"scheduling"}),
    ("todas las mañanas a las nueve mándame las noticias", {"scheduling"}),
    ("cancel the nightly check", {"scheduling"}),
    ("calcula el sha256 de ese archivo", {"sandbox"}),
    ("clone the repo and count the lines", {"sandbox"}),
    ("guarda esta receta en mis notas", {"obsidian-vault"}),
    ("añade una etiqueta a esa nota", {"obsidian-vault"}),
    ("borra esas notas", {"obsidian-vault"}),
    ("busca cuánto tiene que reposar el gazpacho", {"web-browsing"}),
    ("what does the site say about opening hours?", {"web-browsing"}),
    ("busca una receta de gazpacho y guárdala en mis notas", {"web-browsing", "obsidian-vault"}),
    ("apaga las luces y avísame cuando termine la lavadora", {"home-assistant", "home-watches"}),
    ("hola, ¿qué tal?", set()),
    ("gracias", set()),
    ("¿cuánto es 17 por 23?", set()),
    ("explícame qué es un agujero negro", set()),
    ("¿qué dice mi nota sobre el gazpacho?", set()),
    ("what did I tell you about my sister?", set()),
    ("tell me a joke", set()),
]

# (prompt spoken, answer, expected: True approve / False decline / None ambiguous)
APPROVAL_CASES = [
    ("sí", True), ("vale", True), ("adelante", True), ("hazlo", True), ("venga, dale", True),
    ("por supuesto", True), ("sí, claro que sí", True), ("go ahead", True), ("do it", True),
    ("no", False), ("mejor no", False), ("ni se te ocurra", False), ("déjalo", False),
    ("espera, no lo hagas", False), ("no, gracias", False), ("don't", False), ("never mind", False),
    ("no sé", None), ("¿qué has dicho?", None), ("eh...", None), ("¿cuáles notas?", None),
    ("sí... bueno, no, espera", None), ("thank you.", None), ("subtítulos realizados por la comunidad de amara.org", None),
]
APPROVAL_PROMPT = "¿Apruebas borrar siete notas del vault? Di sí o no."


def main():
    k = key()
    skills = descriptions()
    print(f"model={MODEL} skills={list(skills)}\n")
    if len(skills) != 7:
        print(f"WARNING: expected 7 skills, parsed {len(skills)}")

    criteria = dict(skills)
    criteria["none"] = "The request needs none of the listed skills: conversation, a question answered from knowledge or memory, reading or searching notes, arithmetic, or anything else."
    choice_q = {"type": "choice",
                "instructions": "`request` is what a person just said to a home assistant, in Spanish or English. Which one skill does carrying out the request need first?",
                "criteria": criteria}
    noul_qs = {f"needs_{n}": {"type": "noul",
                              "instructions": f"`request` is what a person just said to a home assistant, in Spanish or English. Does carrying out the request need this skill? Skill: {d}"}
               for n, d in skills.items()}

    lat, choice_ok, noul_ok, rows = [], 0, 0, []
    for text, want in SKILL_CASES:
        qs = {"skill": choice_q, **noul_qs}
        data, ms = ask({"request": text}, qs, k)
        lat.append(ms)
        a = data["answers"]
        pick, conf = a["skill"]["choice"], a["skill"]["confidence"]
        c_ok = (pick in want) if want else pick == "none"
        nouls = {n: a[f"needs_{n}"]["noul"] for n in skills}
        got = {n for n, p in nouls.items() if p >= 0.5}
        n_ok = got == want
        choice_ok += c_ok
        noul_ok += n_ok
        hot = ", ".join(f"{n}={p:.2f}" for n, p in sorted(nouls.items(), key=lambda x: -x[1])[:3])
        rows.append(f"{'ok ' if c_ok else 'BAD'} {'ok ' if n_ok else 'BAD'} {ms:5.0f}ms  {text!r}\n"
                    f"        want={sorted(want) or 'none'} choice={pick} ({conf:.2f}) nouls: {hot}  in_tok={data['usage']['input_tokens']}")
    print("SKILLS  [choice] [nouls]")
    print("\n".join(rows))
    n = len(SKILL_CASES)
    print(f"\nchoice {choice_ok}/{n}   nouls(exact set @0.5) {noul_ok}/{n}")
    print(f"latency ms: median {statistics.median(lat):.0f}  p90 {sorted(lat)[int(len(lat) * .9)]:.0f}  max {max(lat):.0f}\n")

    print("APPROVAL  (approve if p>=0.9, decline if p<=0.1 on 'approved' AND answered; else re-ask)")
    ok, lat2 = 0, []
    for ans, want in APPROVAL_CASES:
        qs = {
            "approved": {"type": "noul", "instructions": "`prompt` was spoken aloud asking for a yes or no. `answer` is the transcribed spoken reply. Did the person give permission to go ahead?"},
            "declined": {"type": "noul", "instructions": "`prompt` was spoken aloud asking for a yes or no. `answer` is the transcribed spoken reply. Did the person refuse or tell the assistant not to go ahead?"},
        }
        data, ms = ask({"prompt": APPROVAL_PROMPT, "answer": ans}, qs, k)
        lat2.append(ms)
        pa, pd = data["answers"]["approved"]["noul"], data["answers"]["declined"]["noul"]
        got = True if pa >= 0.9 and pd <= 0.1 else False if pd >= 0.9 and pa <= 0.1 else None
        good = got == want
        ok += good
        print(f"{'ok ' if good else 'BAD'} {ms:5.0f}ms  {ans!r:55} want={want} got={got} approved={pa:.2f} declined={pd:.2f}")
    print(f"\napproval {ok}/{len(APPROVAL_CASES)}   latency median {statistics.median(lat2):.0f}ms")


if __name__ == "__main__":
    main()
